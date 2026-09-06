namespace YKeys;

internal static class Program
{
    private const string Version = "0.1.5-dev";

    private static int Main(string[] args)
    {
        if (args.Contains("--version") || args.Contains("-V"))
        {
            Console.WriteLine($"ykeys {Version}");
            return 0;
        }

        // One-shot verbs run and exit; they never start the hotkey daemon.
        if (args.Length > 0 && args[0] == "shell-hotkeys")
        {
            return ShellHotkeys.Run(args[1..]);
        }

        if (args.Length > 0 && args[0] == "signal")
        {
            return SendOneSignal(args[1..]);
        }

        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(
                $"""
                ykeys {Version} — hotkey daemon for Windows (companion to YTile)

                usage: ykeys [--log]
                       ykeys signal <WindowClass>[#code]
                       ykeys shell-hotkeys <status|disable|restore> [LETTERS] [--restart-shell]

                Reads ~/.config/ykeys/ykeys.json and registers its "hotkeys" map as
                global hotkeys; each binding runs its command line when pressed.
                The config is re-applied automatically whenever the file changes.

                  --log    write output to %LOCALAPPDATA%\ykeys\ykeys.log instead
                           of the console (used when launched by `ytile start`)

                A binding that starts with "@signal:" pokes an app that is already
                running instead of starting a process — no image load, so it costs
                microseconds rather than the ~20 ms a spawn does:

                  "alt+space": "@signal:YSpot.Signal"      window class
                  "win+v":     "@signal:YSpot.Signal#3"    ...and which action

                The app must be running; if it is not, the chord logs and does
                nothing until it is. It must also not be holding the chord
                itself: RegisterHotKey is first-come, so if the app you are
                signalling already registered alt+space, ykeys is refused it
                and skips the binding. Tell the app to hand the chord over
                first (YSpot: Settings, "Let YKeys hold the hotkey").

                To check a target without binding a key:

                  ykeys signal YSpot.Signal      send it once, and say what happened

                Windows keeps several Win+<letter> chords for itself, so binding
                them here is refused until they are handed back:

                  ykeys shell-hotkeys status     what Windows suppresses, and what is free
                  ykeys shell-hotkeys disable    hand back the win+ chords in your config
                  ykeys shell-hotkeys restore    give them to Windows again

                That writes a per-user registry setting and takes effect when the
                shell restarts; the daemon itself never touches it.
                """);
            return 0;
        }

        // A mistyped flag must not start the daemon as if nothing happened —
        // the user would be waiting for behaviour they never actually asked for.
        string[] known = ["--log", "--version", "-V", "--help", "-h"];
        string[] unknown = [.. args.Where(a => !known.Contains(a))];
        if (unknown.Length > 0)
        {
            Console.Error.WriteLine($"ykeys: unknown argument(s): {string.Join(' ', unknown)}");
            Console.Error.WriteLine("try: ykeys --help");
            return 2;
        }

        // The instance check must precede the log redirect: the first instance
        // holds the log write handle, so a second --log instance would die on
        // the FileStream open before ever reaching this message.
        // Semaphore, not Mutex: no thread affinity, released from any thread.
        using var instanceLock = new Semaphore(1, 1, @"Local\ykeys-instance", out _);
        if (!instanceLock.WaitOne(0))
        {
            Console.Error.WriteLine("ykeys: another instance is already running.");
            return 1;
        }

        // --log: headless mode — everything goes to a file instead of the
        // hidden console. Truncates per session; FileShare.Read allows tailing.
        if (args.Contains("--log"))
        {
            string logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ykeys");
            Directory.CreateDirectory(logDir);
            var log = new StreamWriter(new FileStream(
                Path.Combine(logDir, "ykeys.log"),
                FileMode.Create, FileAccess.Write, FileShare.Read))
            { AutoFlush = true };
            Console.SetOut(log);
            Console.SetError(log);
        }

        YKeysConfig config = YKeysConfig.Load(null, out string? configError);
        Log(File.Exists(YKeysConfig.DefaultPath)
            ? $"config: {YKeysConfig.DefaultPath}"
            : $"config: no file at {YKeysConfig.DefaultPath} — no hotkeys until one exists");
        if (configError is not null)
        {
            Log($"config problems: {configError}");
        }

        Log($"ykeys {Version} — Ctrl+C to exit.");
        HotkeyListener.Start(config.Hotkeys);

        using FileSystemWatcher watcher = WatchConfig();

        using var done = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            done.Set();
        };
        done.Wait();

        HotkeyListener.Stop();
        Thread.Sleep(100);
        return 0;
    }

    /// <summary>Re-applies the config whenever the file changes. Editors fire
    /// bursts of Changed/Renamed events, so reloads are debounced.</summary>
    private static FileSystemWatcher WatchConfig()
    {
        string dir = Path.GetDirectoryName(YKeysConfig.DefaultPath)!;
        Directory.CreateDirectory(dir);

        var debounce = new System.Timers.Timer(300) { AutoReset = false };
        debounce.Elapsed += (_, _) =>
        {
            YKeysConfig config = YKeysConfig.Load(null, out string? error, out bool fileLevelFailure);

            // A half-saved file or a stray comma must not unregister everything
            // the user is mid-keystroke on. Keep the working set and wait for
            // the next save. Deleting the file is different — that is a real
            // instruction, and File.Exists tells the two apart.
            if (fileLevelFailure && File.Exists(YKeysConfig.DefaultPath))
            {
                Log($"config NOT reloaded, keeping the {HotkeyListener.RegisteredCount} live binding(s): {error}");
                return;
            }

            Log(error is null
                ? $"config reloaded ({config.Hotkeys.Count} hotkeys)"
                : $"config reloaded with problems: {error}");
            HotkeyListener.Apply(config.Hotkeys);
        };

        var watcher = new FileSystemWatcher(dir, Path.GetFileName(YKeysConfig.DefaultPath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        FileSystemEventHandler onChange = (_, _) => { debounce.Stop(); debounce.Start(); };
        watcher.Changed += onChange;
        watcher.Created += onChange;
        // Deletion must unregister everything, matching what a fresh start
        // with no config file would do.
        watcher.Deleted += onChange;
        watcher.Renamed += (_, _) => { debounce.Stop(); debounce.Start(); };
        // Without this the watcher can die (buffer overflow, the directory going
        // away) and hot-reload silently stops working forever. Say so, and try
        // to bring it back.
        watcher.Error += (_, e) =>
        {
            Log($"config watcher failed: {e.GetException().Message} — restarting it");
            try
            {
                watcher!.EnableRaisingEvents = false;
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                Log($"config watcher could not be restarted: {ex.Message} — edits need a ykeys restart");
            }
        };
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    /// <summary>One timestamped writer for the whole daemon: a log that mixes
    /// stamped and unstamped lines is hard to correlate with anything else.</summary>
    internal static void Log(string message) => Console.WriteLine($"{DateTime.Now:MM-dd HH:mm:ss.fff} {message}");

    /// `ykeys signal <class>[#code]` — send one signal and report.
    ///
    /// The diagnostic for "my @signal binding does nothing", which otherwise
    /// means pressing a key and reading a log to find out whether the window
    /// was even there. It is also the only way to exercise the whole delivery
    /// path without synthesising a keypress, which is why it exists at all.
    ///
    /// One thing it CANNOT tell you: the foreground hand-off. Pressing the
    /// bound chord is what makes ykeys the process Windows lets take the
    /// foreground, and running this from a shell does not, so a target that
    /// shows a window may show it without focus here and be perfectly fine
    /// from the real hotkey.
    private static int SendOneSignal(string[] args)
    {
        if (args.Length != 1 || args[0].StartsWith('-'))
        {
            Console.Error.WriteLine("usage: ykeys signal <WindowClass>[#code]");
            return 2;
        }

        // Accept what the user most likely has on the clipboard: the whole
        // right-hand side out of ykeys.json, prefix and all. Prepending the
        // prefix unconditionally turned `ykeys signal @signal:YSpot.Signal`
        // into a hunt for a window class literally called "@signal:YSpot.Signal"
        // and answered "is the app running?" — the one answer the verb exists
        // to rule out, delivered with full confidence, for the likeliest typo.
        string arg = args[0];
        string spec = SignalSender.IsSignal(arg) ? arg : SignalSender.Prefix + arg;
        if (!SignalSender.TryParse(spec, out SignalTarget? target, out string? error))
        {
            Console.Error.WriteLine($"ykeys: {error}");
            return 2;
        }

        if (!SignalSender.TrySend("signal", target!, out string? failure))
        {
            Console.Error.WriteLine($"ykeys: {failure}");
            return 1;
        }
        Console.WriteLine($"signalled {target!.WindowClass} with code {target.Code}");
        return 0;
    }
}
