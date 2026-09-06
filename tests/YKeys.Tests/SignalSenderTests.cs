namespace YKeys.Tests;

[TestClass]
public sealed class SignalSenderTests
{
    [TestMethod]
    public void IsSignal_OnlyForThePrefix()
    {
        Assert.IsTrue(SignalSender.IsSignal("@signal:YSpot.Signal"));
        // Case-insensitive: the config is hand-written.
        Assert.IsTrue(SignalSender.IsSignal("@SIGNAL:YSpot.Signal"));
        Assert.IsFalse(SignalSender.IsSignal("ytile workspace 1"));
        // A program that merely starts with an at-sign is still a program.
        Assert.IsFalse(SignalSender.IsSignal("@echo.exe"));
        Assert.IsFalse(SignalSender.IsSignal(" @signal:X"));
    }

    [TestMethod]
    public void TryParse_ClassOnly()
    {
        Assert.IsTrue(SignalSender.TryParse("@signal:YSpot.Signal", out SignalTarget? t, out string? err));
        Assert.IsNull(err);
        Assert.AreEqual("YSpot.Signal", t!.WindowClass);
        Assert.AreEqual(0u, t.Code);
    }

    [TestMethod]
    public void TryParse_ClassAndCode()
    {
        Assert.IsTrue(SignalSender.TryParse("@signal:YSpot.Signal#3", out SignalTarget? t, out _));
        Assert.AreEqual("YSpot.Signal", t!.WindowClass);
        Assert.AreEqual(3u, t.Code);

        // Whitespace around the parts is the user being tidy, not an error.
        Assert.IsTrue(SignalSender.TryParse("@signal:  YSpot.Signal # 12 ", out SignalTarget? spaced, out _));
        Assert.AreEqual("YSpot.Signal", spaced!.WindowClass);
        Assert.AreEqual(12u, spaced.Code);
    }

    [TestMethod]
    public void TryParse_RejectsWhatWouldSilentlyDoNothing()
    {
        // Each of these registers a chord perfectly well and then goes nowhere,
        // which is why they are refused at load instead of at the first press.
        Assert.IsFalse(SignalSender.TryParse("@signal:", out _, out string? empty));
        Assert.IsNotNull(empty);

        Assert.IsFalse(SignalSender.TryParse("@signal:#3", out _, out string? noClass));
        Assert.IsNotNull(noClass);

        Assert.IsFalse(SignalSender.TryParse("@signal:YSpot.Signal#toggle", out _, out string? notANumber));
        StringAssert.Contains(notANumber!, "whole number");

        // Negative codes and overflow are both "not a whole number" — WPARAM is
        // unsigned and a wrapped value would signal the wrong thing.
        Assert.IsFalse(SignalSender.TryParse("@signal:X#-1", out _, out _));
        Assert.IsFalse(SignalSender.TryParse("@signal:X#4294967296", out _, out _));
    }

    [TestMethod]
    public void TryParse_RejectsAClassNameRegisterClassCouldNotHold()
    {
        Assert.IsFalse(SignalSender.TryParse("@signal:" + new string('x', 257), out _, out string? err));
        StringAssert.Contains(err!, "256");
        Assert.IsTrue(SignalSender.TryParse("@signal:" + new string('x', 256), out _, out _));
    }

    [TestMethod]
    public void Config_ReportsABadSignalTargetAndSkipsTheBinding()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ykeys-signal-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            {
              "hotkeys": {
                "alt+space": "@signal:YSpot.Signal",
                "alt+1": "@signal:",
                "alt+2": "ytile workspace 2"
              }
            }
            """);
        try
        {
            YKeysConfig config = YKeysConfig.Load(path, out string? error);
            Assert.IsNotNull(error);
            StringAssert.Contains(error, "alt+1");

            // One bad binding must not cost the good ones.
            Assert.AreEqual(2, config.Hotkeys.Count);
            HotkeyBinding signal = config.Hotkeys.Single(h => h.Chord == "alt+space");
            Assert.IsNotNull(signal.Signal);
            Assert.AreEqual("YSpot.Signal", signal.Signal!.WindowClass);

            // A plain command line stays one: nothing to parse, nothing to post.
            Assert.IsNull(config.Hotkeys.Single(h => h.Chord == "alt+2").Signal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void TryParse_AcceptsAWholePastedBindingValue()
    {
        // `ykeys signal @signal:YSpot.Signal` is the likeliest thing a user
        // types: the right-hand side straight out of ykeys.json. The verb
        // hands it here already prefixed only when it was not, so both forms
        // have to land on the same target.
        Assert.IsTrue(SignalSender.TryParse("@signal:YSpot.Signal#3", out SignalTarget? a, out _));
        Assert.IsTrue(SignalSender.IsSignal("@signal:YSpot.Signal#3"));
        Assert.AreEqual("YSpot.Signal", a!.WindowClass);
        Assert.AreEqual(3u, a.Code);
    }

    [TestMethod]
    public void Send_SaysSoWhenNothingIsListening()
    {
        // The common case in practice: the app is not running yet. It must be a
        // log line and a return, never an exception across the WndProc boundary.
        SignalSender.Send("alt+space", new SignalTarget($"NoSuchClass-{Guid.NewGuid():N}", 0));
    }
}
