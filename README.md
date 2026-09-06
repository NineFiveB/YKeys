# YKeys

A hotkey daemon for Windows. C#, .NET 10, NativeAOT. One job: turn global
hotkeys into commands.

Built as the companion to [YTile](https://github.com/AegiosOT/YTile) — YTile
releases bundle `ykeys.exe` and `ytile start` brings it up automatically — but
it is a standalone tool: any chord can run any program.

## How it works

`~/.config/ykeys/ykeys.json` maps chords to command lines:

```json
{
  "hotkeys": {
    "alt+1": "ytile workspace 1",
    "alt+shift+h": "ytile move left",
    "alt+enter": "wt"
  }
}
```

Chords are `modifier+...+key`: modifiers `alt`, `ctrl`, `shift`, `win`; keys
are letters, digits, `f1`-`f24` (`f12` is debugger-reserved), arrows, numpad
keys (`numpad1`, `numpad_add`), and named punctuation like `space`, `enter`,
`minus`, `plus` — the `oem_plus`/`oem_1` spellings used by whkd and komorebi
configs work too, so those translate over unchanged.

Comments and trailing commas are allowed, which makes parking a binding the
obvious two-second gesture:

```jsonc
{
  "hotkeys": {
    // "win+q": "ytile float",     <- handed back to Windows for now
    "alt+1": "ytile workspace 1",
  }
}
```

Commands run as detached processes. There is no shell in the way, but you can
ask for one — `"powershell -NoProfile -Command \"a; b\""` works fine. Quote the
program path if it contains spaces. Config changes are picked up automatically;
no restart needed, and a half-saved or malformed file leaves your existing
bindings alone rather than unregistering everything.

## Signalling an app that is already running

Starting a process costs about 8 ms at the median and 20 ms at the 95th
percentile on a warm machine — even for a program that does nothing at all,
because that is what loading an image costs. For "run this thing", nobody
notices. For "show the launcher I already have open", that *is* the delay.

So a binding can poke a running app instead of starting one:

```json
{
  "hotkeys": {
    "alt+space": "@signal:YSpot.Signal",
    "win+v": "@signal:YSpot.Signal#3"
  }
}
```

The name after `@signal:` is a **window class**; the optional `#n` is a number
passed along to say which of the app's actions you mean (what the numbers mean
is up to the app — YSpot's are 0 toggle, 1 show, 2 settings, 3 clipboard).
YKeys finds the window, hands it the right to take the foreground, and posts a
message. Microseconds, no process.

**The app has to hand the chord over first.** `RegisterHotKey` is first-come,
so if the app you are signalling registers `alt+space` itself at startup — as
YSpot does out of the box — YKeys is refused that chord and skips the binding
with a line in the log. Nothing looks broken, because the chord still opens
the app: by *its* registration, not through YKeys. In YSpot that switch is
Settings → "Let YKeys hold the hotkey"; other apps will have their own.

If the app is not running, the chord logs a line saying so and does nothing;
start the app and the same chord starts working, no reload needed.

To check a target without binding a key to it:

```
ykeys signal YSpot.Signal        # send it once, and say what happened
```

That takes exactly the same path the hotkey does, so "nothing is listening on
window class ..." from here means the same thing it would mean from a chord.
One thing it cannot tell you: pressing the bound chord is what makes YKeys the
process Windows will let take the foreground, and running the command from a
shell does not — so a target may appear without focus here and be perfectly
fine from the real hotkey.

**Writing an app that accepts signals.** Create a message-only window
(`CreateWindowEx` with `HWND_MESSAGE` as the parent) whose class is the name you
tell people to put in their config, and handle the message id you get from
`RegisterWindowMessage("YKeysSignal")` — `wParam` is the number after `#`. That
is the whole protocol. Take the foreground from the window procedure if you have
a window to show: YKeys has already called `AllowSetForegroundWindow` for you,
which is what makes `SetForegroundWindow` succeed even though the keypress went
to a different process.

Hotkeys are registered with `RegisterHotKey`, so they keep working even while
an elevated window has focus, and a chord some other program already owns is
skipped with a log line naming it rather than silently swallowed.

## Windows keeps some chords for itself

`Win+Q`, `Win+E`, `Win+R` and friends belong to the shell, and YKeys will not
steal a chord another program claimed first — those bindings are simply
refused. Windows can be told to give the `Win+`*letter* ones back:

```
ykeys shell-hotkeys status     # what Windows suppresses, what is ours, what is actually free
ykeys shell-hotkeys disable    # hand back the win+ chords in your config
ykeys shell-hotkeys restore    # give them to Windows again
```

`disable` with no argument derives the letters from your own `win+` bindings;
name them explicitly (`disable QRE`) to override. It writes a per-user registry
value, records exactly which letters it added, and `restore` subtracts only
those — letters you set by hand survive. **Nothing here happens automatically:**
the daemon never touches this setting, because the change only takes effect
when the shell restarts and so could not be honestly undone when YKeys stops.
Add `--restart-shell` to restart Explorer now, or sign out later.

Two limits worth knowing. Only letters can be released this way — `Win+1` and
`Win+;` cannot. And several chords belong to components other than Explorer
(Copilot, Game Bar, Settings, Widgets, Quick Settings) which ignore the setting
entirely; `status` probes each chord live and tells you which are genuinely free.

## Install

Bundled with YTile — installing YTile gives you ykeys. Standalone: grab
`ykeys.exe` from the [latest release](https://github.com/AegiosOT/YKeys/releases)
and put it on your PATH.

## Running

```
ykeys            # run in the foreground, Ctrl+C to exit
ykeys --log      # log to %LOCALAPPDATA%\ykeys\ykeys.log instead (what `ytile start` uses)
ykeys --version
```

One instance per session; a second start exits with a message. To stop a
background instance: `ytile stop` (when managed by YTile) or kill `ykeys.exe`.

## Building

Requires the .NET 10 SDK, plus VS Build Tools with the C++ workload for the
NativeAOT linker.

```
dotnet build                                        # dev build (JIT)
dotnet publish src/YKeys -r win-x64 -c Release -o publish   # NativeAOT ykeys.exe
```

## Code signing

Release binaries are Authenticode-signed by CI via
[Azure Artifact Signing](https://learn.microsoft.com/en-us/azure/artifact-signing/),
sharing the [YTile](https://github.com/AegiosOT/YTile) suite's signing setup —
details in [packaging/signing](packaging/signing/README.md). Releases up to
v0.1.1 predate the signing setup and are unsigned.

This program will not transfer any information to other networked systems
unless specifically requested by the user or the person installing or
operating it. (It only runs the commands you bind, locally.)

## License

MIT — see [LICENSE](LICENSE). (Releases up to v0.1.2 were published under GPL-3.0; later releases are MIT.)
