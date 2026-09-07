using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace YKeys.Tests;

/// <summary>
/// A chord another program holds must be taken the moment it is freed.
///
/// <para>This is the hand-off case, and it is the one that fails silently.
/// `RegisterHotKey` is first-come, so an app that registers its own chord —
/// YSpot registers Alt+Space out of the box — leaves ykeys refused and the
/// binding skipped. Tell that app to hand the chord over and, without a retry,
/// NOBODY ends up holding it: the app released it, ykeys gave up before it
/// did, and neither side reports anything wrong. The chord simply stops
/// working.</para>
///
/// <para>The test occupies a chord itself, starts the listener, watches the
/// binding get skipped, then releases the chord and waits for ykeys to pick it
/// up. F19 with three modifiers because this registers a REAL global hotkey
/// for the few seconds the test runs, and that combination is one no keyboard
/// sends by accident.</para>
/// </summary>
[TestClass]
public sealed class HotkeyRetryTests
{
    private const int OccupiedId = 0xBEE;
    private const HOT_KEY_MODIFIERS Mods =
        HOT_KEY_MODIFIERS.MOD_CONTROL | HOT_KEY_MODIFIERS.MOD_ALT | HOT_KEY_MODIFIERS.MOD_SHIFT;
    private static readonly uint Vk = (uint)VIRTUAL_KEY.VK_F19;

    /// Long enough for several 5 s retry ticks, short enough to fail fast.
    private const int WaitMs = 20_000;

    [TestMethod]
    [Timeout(60_000)]
    public void AChordSomeoneElseHeldIsTakenOnceItIsFreed()
    {
        // Occupy it first, from this process, so the listener is refused.
        // hwnd 0 registers against the calling THREAD, which is this one.
        Assert.IsTrue(
            PInvoke.RegisterHotKey(HWND.Null, OccupiedId, Mods, Vk),
            "could not occupy the test chord — is something else holding ctrl+alt+shift+f19?");

        bool released = false;
        try
        {
            Assert.IsTrue(
                HotkeyParser.TryParse("ctrl+alt+shift+f19", out HOT_KEY_MODIFIERS m, out uint vk, out string? err),
                $"the parser rejected the test chord: {err}");

            HotkeyListener.Start([new HotkeyBinding("ctrl+alt+shift+f19", m, vk, "@signal:Nothing.Listening")]);

            // Wait for the listener to have TRIED and been refused. Polling
            // RegisteredCount == 0 would pass instantly, before the pump
            // thread had run at all, and the test would then be watching the
            // FIRST registration succeed rather than the retry — which is how
            // the first version of this test passed in 128 ms while proving
            // nothing.
            Assert.IsTrue(
                Waited(() => HotkeyListener.SkippedCount == 1, 5_000),
                "the listener never tried the chord this test is holding");
            Assert.AreEqual(0, HotkeyListener.RegisteredCount,
                "the listener registered a chord this test is holding");

            // Hand it over, the way an app told to release its chord would.
            PInvoke.UnregisterHotKey(HWND.Null, OccupiedId);
            released = true;

            Assert.IsTrue(
                Waited(() => HotkeyListener.RegisteredCount == 1, WaitMs),
                $"the freed chord was never picked up within {WaitMs / 1000} s — the retry is not running");
            Assert.AreEqual(0, HotkeyListener.SkippedCount, "the retry left it on the skipped list");
        }
        finally
        {
            HotkeyListener.Stop();
            if (!released)
            {
                PInvoke.UnregisterHotKey(HWND.Null, OccupiedId);
            }
        }
    }

    /// Poll rather than sleep-then-assert: the retry is on a 5 s timer, so a
    /// fixed sleep would either flake or pad every run by its worst case.
    private static bool Waited(Func<bool> until, int timeoutMs)
    {
        for (int waited = 0; waited < timeoutMs; waited += 100)
        {
            if (until())
            {
                return true;
            }
            Thread.Sleep(100);
        }
        return until();
    }
}
