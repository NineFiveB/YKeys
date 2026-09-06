using System.Globalization;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace YKeys;

/// <summary>
/// A parsed <c>@signal:</c> binding: the window class to look for, and the code
/// to hand the app that owns it.
/// </summary>
internal sealed record SignalTarget(string WindowClass, uint Code);

/// <summary>
/// The no-spawn dispatch path: poke an app that is already running instead of
/// starting a process.
///
/// <para>Spawning costs 8 ms at the median and 20 ms at p95 on a warm machine
/// even for a program that does nothing at all, because that is what image
/// loading costs. For a binding whose whole job is "show the window you already
/// have", that is the entire latency budget of the thing being summoned. This
/// path is a window lookup and a PostMessage — microseconds — so a launcher
/// bound through YKeys is as quick as one that registered the chord itself.</para>
///
/// <para>The message id comes from RegisterWindowMessage, so the two sides
/// agree by using the same string rather than by sharing a header, and the id
/// cannot collide with an app's own WM_APP range.</para>
/// </summary>
internal static unsafe class SignalSender
{
    /// <summary>What marks a binding as a signal rather than a command line.</summary>
    public const string Prefix = "@signal:";

    /// <summary>
    /// The string both sides pass to RegisterWindowMessage. Part of the public
    /// contract with every app that accepts a signal — changing it breaks them
    /// all, silently, since a message nobody registered simply goes nowhere.
    /// </summary>
    public const string MessageName = "YKeysSignal";

    /// <summary>Message-only windows live under this parent and are invisible
    /// to FindWindow, which walks only top-level windows.</summary>
    private static readonly HWND HwndMessage = (HWND)(-3);

    /// <summary>Class names are capped by RegisterClass itself.</summary>
    private const int MaxClassName = 256;

    private static uint s_message;

    public static bool IsSignal(string commandLine)
        => commandLine.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <c>@signal:SomeWindowClass</c> or <c>@signal:SomeWindowClass#7</c>.
    ///
    /// <para>The code is a plain number and its meaning belongs entirely to the
    /// receiving app — one window can serve several chords. Anything richer
    /// would need memory shared across the process boundary, which would make
    /// this a blocking send and cost exactly what the path exists to avoid.</para>
    /// </summary>
    public static bool TryParse(string commandLine, out SignalTarget? target, out string? error)
    {
        target = null;
        error = null;

        string rest = commandLine[Prefix.Length..].Trim();
        if (rest.Length == 0)
        {
            error = "no window class after '@signal:'";
            return false;
        }

        uint code = 0;
        int hash = rest.IndexOf('#');
        if (hash >= 0)
        {
            string codeText = rest[(hash + 1)..].Trim();
            if (!uint.TryParse(codeText, NumberStyles.None, CultureInfo.InvariantCulture, out code))
            {
                error = $"'{codeText}' is not a whole number (use '@signal:ClassName#3')";
                return false;
            }
            rest = rest[..hash].TrimEnd();
        }

        if (rest.Length == 0)
        {
            error = "no window class before '#'";
            return false;
        }
        if (rest.Length > MaxClassName)
        {
            error = $"window class is longer than {MaxClassName} characters";
            return false;
        }

        target = new SignalTarget(rest, code);
        return true;
    }

    /// <summary>
    /// Called on the pump thread, deliberately: both calls below are
    /// non-blocking, and the foreground grant has to happen while the WM_HOTKEY
    /// that earned it is still the last input event. Handing this to a worker
    /// would race exactly the thing it is here to guarantee.
    /// </summary>
    public static void Send(string chord, SignalTarget target)
    {
        if (!TrySend(chord, target, out string? failure))
        {
            Log($"hotkey '{chord}': {failure}");
        }
    }

    /// <summary>
    /// The delivery itself, shared with <c>ykeys signal</c> so the diagnostic and
    /// the hotkey exercise exactly the same path — a diagnostic that took a
    /// different route would be worth very little.
    /// </summary>
    public static bool TrySend(string chord, SignalTarget target, out string? failure)
    {
        failure = null;
        HWND hwnd = Find(target.WindowClass);
        if (hwnd.IsNull)
        {
            failure = $"nothing is listening on window class '{target.WindowClass}' "
                + "— is the app running?";
            return false;
        }

        // The whole reason a launcher can be summoned by someone else's hotkey.
        // Pressing the chord makes YKEYS the process allowed to take the
        // foreground; without handing that right on, the app we poke would show
        // its window and then fail SetForegroundWindow, leaving a visible
        // launcher that does not have the keyboard. The failure is silent and
        // looks like the app's bug, so it is worth a log line when it happens.
        uint pid = 0;
        _ = PInvoke.GetWindowThreadProcessId(hwnd, &pid);
        bool granted = pid != 0 && PInvoke.AllowSetForegroundWindow(pid);

        if (s_message == 0)
        {
            s_message = PInvoke.RegisterWindowMessage(MessageName);
            if (s_message == 0)
            {
                failure = $"could not register the '{MessageName}' message "
                    + $"(error {Marshal.GetLastPInvokeError()})";
                return false;
            }
        }

        if (!PInvoke.PostMessage(hwnd, s_message, (WPARAM)target.Code, default))
        {
            // The code is the whole diagnosis here, and dropping it left one
            // sentence for three different problems with three different
            // remedies. We got PAST the window lookup, so the app is plainly
            // there and "is it running?" is already answered.
            int err = Marshal.GetLastPInvokeError();
            string why = err switch
            {
                // UIPI. Not reachable with YSpot, whose shell is specified
                // unelevated — but the @signal protocol is published for any
                // app, and a target the user runs elevated needs
                // ChangeWindowMessageFilterEx on its own side.
                5 => " — it runs at a higher integrity level than ykeys, and has not "
                    + "allowed this message through (ChangeWindowMessageFilterEx)",
                // The window went away between the lookup and the post.
                1400 => " — the window went away as we posted; the app is probably restarting",
                // 10,000 queued messages: the target's pump is wedged.
                1816 => " — its message queue is full, so the app has stopped pumping",
                _ => string.Empty,
            };
            failure = $"could not post to '{target.WindowClass}' (error {err}){why}";
            return false;
        }

        if (!granted)
        {
            // Not a failure: the post landed. Expected when this runs from a
            // shell rather than from WM_HOTKEY, and worth saying either way —
            // it is the one symptom that otherwise reads as the target's bug.
            Log($"'{chord}': signalled '{target.WindowClass}', but Windows refused the "
                + "foreground hand-off — the window may appear without focus");
        }
        return true;
    }

    /// <summary>
    /// Message-only windows first: an app that exists to receive signals has no
    /// reason to put a real top-level window in the way, and FindWindow cannot
    /// see those at all. Falling back covers apps that listen on an ordinary
    /// window instead.
    /// </summary>
    private static HWND Find(string windowClass)
    {
        HWND messageOnly = PInvoke.FindWindowEx(HwndMessage, HWND.Null, windowClass, null);
        return messageOnly.IsNull ? PInvoke.FindWindow(windowClass, null) : messageOnly;
    }

    private static void Log(string message) => Program.Log(message);
}
