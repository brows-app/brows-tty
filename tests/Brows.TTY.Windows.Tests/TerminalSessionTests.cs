using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using Brows.Ghostty;
using Brows.Win32;
using Brows.Win32.PlatformInvoke;

namespace Brows.TTY.Tests;

[TestFixture]
public sealed class TerminalSessionTests {
    private static string Shell => "\"" + Environment.GetEnvironmentVariable("ComSpec") + "\"";

    private static string ReadText(TerminalSession session) {
        TerminalFrame frame = session.Engine.ReadFrame(true);
        return string.Join("\n", frame.ChangedRows.Select(row =>
            string.Concat(Enumerable.Range(0, row.Cells.Length).Select(row.GetText))));
    }

    private static async Task WaitForText(TerminalSession session, string text) {
        Stopwatch elapsed = Stopwatch.StartNew();
        string actual = "";
        while (elapsed.Elapsed < TimeSpan.FromSeconds(10)) {
            actual = ReadText(session);
            if (actual.Contains(text)) { return; }
            session.CheckError();
            await Task.Delay(20);
        }
        Assert.Fail($"Expected terminal output '{text}', received '{actual}'.");
    }

    private static async Task<uint> Complete(TerminalSession session) {
        Task completed = await Task.WhenAny(session.Completion, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.That(completed, Is.SameAs(session.Completion), "The shell did not exit within 15 seconds.");
        return await session.Completion;
    }

    [Test]
    public async Task ColorQueries_RoundTripThroughConsoleProcessAndApplyBackground() {
        string architecture = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "AMD64";
        string rid = architecture.Equals("ARM64", StringComparison.OrdinalIgnoreCase) ? "win-arm64" : "win-x64";
        string probe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "helpers", rid, "color-query.exe");
        Assert.That(File.Exists(probe), Is.True, "Run scripts/Build-Native.ps1 to build the console probe.");
        using TerminalSession session = new("\"" + probe + "\"", null, 80, 24);
        Assert.That(await Complete(session), Is.Zero,
            "The console process must receive both OSC 10/11 replies before applying its theme.");
        Assert.That(ReadText(session), Does.Contain("color-query-roundtrip-ok"));
        Assert.That(session.Engine.ReadFrame().Info.Background, Is.EqualTo(0xff0d1117u));
    }

    [Test]
    public async Task ShellOutput_ReachesGhosttyAndPreservesExitCode() {
        using TerminalSession session = new(Shell + " /d /q /c \"echo native-session-ok & exit /b 7\"", null, 80, 24);
        uint code = await Complete(session);
        Assert.That(code, Is.EqualTo(7));
        Assert.That(ReadText(session), Does.Contain("native-session-ok"));
    }

    [Test]
    public async Task InteractiveShell_AcceptsInputAndResize() {
        using TerminalSession session = new(Shell + " /d /q", null, 80, 24);
        session.Engine.Paste("echo interactive-native-ok");
        session.Key(0x0d, 0, 1, "");
        await WaitForText(session, "interactive-native-ok");
        session.Resize(100, 30, 8, 16);
        TerminalFrame frame = session.Engine.ReadFrame();
        Assert.That(frame.Info.Columns, Is.EqualTo(100));
        Assert.That(frame.Info.Rows, Is.EqualTo(30));
        session.Engine.Paste("exit /b 3");
        session.Key(0x0d, 0, 1, "");
        Assert.That(await Complete(session), Is.EqualTo(3));
    }

    [TestCase(80, 50)]
    [TestCase(140, 24)]
    [TestCase(140, 50)]
    public async Task ResizeAfterScrollback_KeepsPromptAndTypingAligned(int columns, int rows) {
        using TerminalSession session = new(Shell + " /d /q /k prompt resize-ready$G", null, 80, 24);
        await WaitForText(session, "resize-ready>");
        session.Engine.Paste("for /L %i in (1,1,60) do @echo resize-row-%i");
        session.Key(0x0d, 0, 1, "");
        await WaitForText(session, "resize-row-60");
        await WaitForText(session, "resize-ready>");
        session.Resize((ushort)columns, (ushort)rows, 8, 16);
        session.Engine.Paste("rem resize-check");
        await WaitForText(session, "resize-check");
        TerminalFrame frame = session.Engine.ReadFrame(true);
        string[] text = frame.ChangedRows.Select(row =>
            string.Concat(Enumerable.Range(0, row.Cells.Length).Select(row.GetText))).ToArray();
        Assert.That(text[frame.Info.CursorY], Is.EqualTo("resize-ready>rem resize-check"),
            "Typing after a resize must remain beside the prompt, not overwrite earlier output.");
        Assert.That(text.Take((int)frame.Info.CursorY), Does.Contain("resize-row-60"),
            "The final output line must remain above the prompt.");
        session.Engine.Scroll(-32767);
        Assert.That(ReadText(session).Split('\n'), Does.Contain("resize-row-1"),
            "Growing the active screen must preserve older output in scrollback.");
        session.Engine.Scroll(0, true);
        session.Key(0x0d, 0, 1, "");
        session.Resize(80, 24, 8, 16);
        session.Engine.Paste("rem restored-check");
        await WaitForText(session, "restored-check");
        TerminalFrame restored = session.Engine.ReadFrame(true);
        TerminalRow cursorRow = restored.ChangedRows.Single(row => row.Index == restored.Info.CursorY);
        Assert.That(string.Concat(Enumerable.Range(0, cursorRow.Cells.Length).Select(cursorRow.GetText)),
            Is.EqualTo("resize-ready>rem restored-check"),
            "Restoring the original size must keep typing aligned with the prompt too.");
        session.Key(0x0d, 0, 1, "");
        session.Engine.Paste("exit /b 0");
        session.Key(0x0d, 0, 1, "");
        Assert.That(await Complete(session), Is.Zero);
    }

    [Test]
    public async Task Stop_ClosesRunningShellAndCanBeRepeated() {
        using TerminalSession session = new(Shell + " /d /q", null, 80, 24);
        session.Engine.Paste("echo ready-for-stop");
        session.Key(0x0d, 0, 1, "");
        await WaitForText(session, "ready-for-stop");
        await Task.Run(session.Stop);
        Assert.That(await Complete(session), Is.EqualTo(1223), "Stopping a live shell reports ERROR_CANCELLED.");
        session.Stop();
    }

    [Test]
    public async Task QueueOverflow_DropsInputWithoutPoisoningTheSession() {
        /* ping ignores console input, so the queue stays full for the whole session. */
        using TerminalSession session = new(Shell + " /d /q /c \"ping -n 60 127.0.0.1 >nul\"", null, 80, 24);
        int quota = unchecked((int)0x80070718);
        session.Engine.Paste(new string('x', 17 * 1024 * 1024));
        Assert.That(() => session.CheckError(),
            Throws.Exception.With.Property("HResult").EqualTo(quota),
            "A full input queue must report the dropped chunk.");
        Assert.That(() => session.CheckError(), Throws.Nothing,
            "A dropped input chunk is observed once instead of latching as a transport failure.");
        await Task.Run(session.Stop);
        Assert.That(await Complete(session), Is.EqualTo(1223),
            "A dropped input chunk must not fault the exit report.");
    }

    [Test]
    public void MissingExecutable_ReportsErrorAndAllowsNextSession() {
        Assert.That(() => new TerminalSession("Z:\\missing-terminal-app-87654321.exe", null, 80, 24),
            Throws.Exception.With.Property("HResult").EqualTo(unchecked((int)0x80070002)));
        using TerminalSession valid = new(Shell + " /d /q /c exit", null, 80, 24);
        Assert.That(valid.Completion.Wait(TimeSpan.FromSeconds(15)), Is.True);
    }

    [Test]
    public void Win32Key_MapsModifiersLocksMediaAndExtraKeys() {
        LibraryLoader.EnsureLoaded();
        (Key key, TerminalKey expected)[] mapped = {
            (Key.LeftShift, TerminalKey.ShiftLeft),
            (Key.RightShift, TerminalKey.ShiftRight),
            (Key.LeftCtrl, TerminalKey.ControlLeft),
            (Key.RightCtrl, TerminalKey.ControlRight),
            (Key.LeftAlt, TerminalKey.AltLeft),
            (Key.RightAlt, TerminalKey.AltRight),
            (Key.LWin, TerminalKey.MetaLeft),
            (Key.RWin, TerminalKey.MetaRight),
            (Key.CapsLock, TerminalKey.CapsLock),
            (Key.NumLock, TerminalKey.NumLock),
            (Key.Scroll, TerminalKey.ScrollLock),
            (Key.PrintScreen, TerminalKey.PrintScreen),
            (Key.Pause, TerminalKey.Pause),
            (Key.Apps, TerminalKey.ContextMenu),
            (Key.Help, TerminalKey.Help),
            (Key.Clear, TerminalKey.NumpadClear),
            (Key.Oem102, TerminalKey.IntlBackslash),
            (Key.Multiply, TerminalKey.NumpadMultiply),
            (Key.Add, TerminalKey.NumpadAdd),
            (Key.Subtract, TerminalKey.NumpadSubtract),
            (Key.Decimal, TerminalKey.NumpadDecimal),
            (Key.Divide, TerminalKey.NumpadDivide),
            (Key.VolumeMute, TerminalKey.AudioVolumeMute),
            (Key.VolumeDown, TerminalKey.AudioVolumeDown),
            (Key.VolumeUp, TerminalKey.AudioVolumeUp),
            (Key.MediaNextTrack, TerminalKey.MediaTrackNext),
            (Key.MediaPreviousTrack, TerminalKey.MediaTrackPrevious),
            (Key.MediaPlayPause, TerminalKey.MediaPlayPause),
            (Key.MediaStop, TerminalKey.MediaStop),
            (Key.BrowserBack, TerminalKey.BrowserBack),
            (Key.BrowserForward, TerminalKey.BrowserForward),
            (Key.BrowserRefresh, TerminalKey.BrowserRefresh),
            (Key.BrowserStop, TerminalKey.BrowserStop),
            (Key.BrowserSearch, TerminalKey.BrowserSearch),
            (Key.BrowserFavorites, TerminalKey.BrowserFavorites),
            (Key.BrowserHome, TerminalKey.BrowserHome),
            (Key.LaunchMail, TerminalKey.LaunchMail),
            (Key.SelectMedia, TerminalKey.MediaSelect),
            (Key.LaunchApplication1, TerminalKey.LaunchApp1),
            (Key.LaunchApplication2, TerminalKey.LaunchApp2),
            (Key.Sleep, TerminalKey.Sleep),
        };
        foreach ((Key key, TerminalKey expected) in mapped) {
            uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
            Assert.That(SessionMethods.bt_win32_key(virtualKey),
                Is.EqualTo((uint)expected), key + " must map to " + expected + ".");
        }
        /* Generic modifier codes arrive from non-WPF callers; they map to the left side. */
        (uint virtualKey, TerminalKey expected)[] generic = {
            (0x10, TerminalKey.ShiftLeft),
            (0x11, TerminalKey.ControlLeft),
            (0x12, TerminalKey.AltLeft),
            (0x6c, TerminalKey.NumpadSeparator),
        };
        foreach ((uint virtualKey, TerminalKey expected) in generic) {
            Assert.That(SessionMethods.bt_win32_key(virtualKey),
                Is.EqualTo((uint)expected), "Virtual key 0x" + virtualKey.ToString("x") + " must map to " + expected + ".");
        }
    }

    [Test]
    public async Task LargeOutput_IsDrainedBeforeProcessCompletion() {
        using TerminalSession session = new(
            Shell + " /d /q /c \"for /L %i in (1,1,10000) do @echo load-line-%i\"", null, 100, 30);
        Assert.That(await Complete(session), Is.Zero);
        Assert.That(ReadText(session), Does.Contain("load-line-10000"));
        session.Engine.Scroll(-100);
        Assert.That(ReadText(session), Does.Not.Contain("load-line-10000"));
        Assert.That(ReadText(session), Does.Contain("load-line-"));
    }
}
