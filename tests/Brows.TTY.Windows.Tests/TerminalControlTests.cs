using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Brows.Ghostty;
using Brows.Win32;
using Brows.Windows.Controls;

namespace Brows.TTY.Tests;

[TestFixture, Apartment(ApartmentState.STA)]
public sealed class TerminalControlTests {
    private SynchronizationContext? _previousContext;

    private static void Pump(Task task) {
        DispatcherFrame frame = new();
        DispatcherTimer timeout = new() { Interval = TimeSpan.FromSeconds(15) };
        timeout.Tick += (sender, args) => frame.Continue = false;
        timeout.Start();
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        task.ContinueWith(completed => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)),
            TaskScheduler.Default);
        if (!task.IsCompleted) { Dispatcher.PushFrame(frame); }
        timeout.Stop();
        Assert.That(task.IsCompleted, Is.True, "Dispatcher operation exceeded 15 seconds.");
        task.GetAwaiter().GetResult();
    }

    private static HwndSource Host(Terminal terminal) {
        HwndSourceParameters parameters = new("Terminal integration test") {
            WindowStyle = unchecked((int)0x80000000),
            Width = 800,
            Height = 400
        };
        terminal.Width = 800;
        terminal.Height = 400;
        HwndSource source = new(parameters) { RootVisual = terminal };
        terminal.Measure(new Size(800, 400));
        terminal.Arrange(new Rect(0, 0, 800, 400));
        terminal.UpdateLayout();
        return source;
    }

    private static void WaitFor(Func<bool> condition) {
        TaskCompletionSource<bool> completion = new();
        DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (sender, args) => {
            if (condition()) { completion.TrySetResult(true); }
        };
        timer.Start();
        try { Pump(completion.Task); }
        finally { timer.Stop(); }
    }

    private static string ReadText(TerminalSession session) {
        TerminalFrame frame = session.Engine.ReadFrame(true);
        return string.Join("\n", frame.ChangedRows.Select(row =>
            string.Concat(Enumerable.Range(0, row.Cells.Length).Select(row.GetText))));
    }

    private static int CountText(string text, string value) {
        int count = 0;
        int index = text.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0) {
            count++;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }
        return count;
    }

    private static void RaiseKey(Terminal terminal, HwndSource source, Key key, RoutedEvent routedEvent) {
        terminal.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) {
            RoutedEvent = routedEvent
        });
    }

    private static T Field<T>(Terminal terminal, string name) {
        return (T)typeof(Terminal).GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(terminal)!;
    }

    private static string KeyEchoCommand() {
        string architecture = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "AMD64";
        string rid = architecture.Equals("ARM64", StringComparison.OrdinalIgnoreCase) ? "win-arm64" : "win-x64";
        return "\"" + Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "helpers", rid, "key-echo.exe") + "\"";
    }

    private static string ReadKeyBytes(TerminalSession session) {
        return string.Concat(ReadText(session).Split('\n').Select(line => line.Trim())
            .Where(line => line.StartsWith("key-echo:", StringComparison.Ordinal))
            .Select(line => line.Substring("key-echo:".Length)));
    }

    private static void DrainInput() {
        Pump(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle).Task);
    }

    private static IEnumerable<GlyphRun> GlyphRuns(Drawing drawing) {
        if (drawing is GlyphRunDrawing glyph) { yield return glyph.GlyphRun; }
        if (drawing is DrawingGroup group) {
            foreach (Drawing child in group.Children) {
                foreach (GlyphRun run in GlyphRuns(child)) { yield return run; }
            }
        }
    }

    private static byte[] RenderPixels(Terminal terminal) {
        terminal.UpdateLayout();
        RenderTargetBitmap bitmap = new(800, 400, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(terminal);
        byte[] pixels = new byte[800 * 400 * 4];
        bitmap.CopyPixels(pixels, 800 * 4, 0);
        return pixels;
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void StoppedFontChange_RebuildsRetainedText(bool stop, bool changeFamily) {
        Terminal terminal = new() {
            AutoStart = false,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 14,
            CommandLine = "\"" + Environment.GetEnvironmentVariable("ComSpec") + "\" /d /q /c echo FONT-CACHE"
        };
        using HwndSource source = Host(terminal);
        TaskCompletionSource<uint> exit = new();
        terminal.ProcessExited += (sender, args) => exit.TrySetResult(args.ExitCode);
        try {
            Pump(terminal.StartAsync());
            Pump(exit.Task);
            Assert.That(exit.Task.Result, Is.Zero);
            if (stop) { Pump(terminal.StopAsync()); }
            byte[] before = RenderPixels(terminal);
            if (changeFamily) { terminal.FontFamily = new FontFamily("Courier New"); }
            else { terminal.FontSize = 28; }
            DrainInput();
            byte[] after = RenderPixels(terminal);
            GlyphRun[] runs = Field<DrawingGroup?[]>(terminal, "_rows")
                .Where(row => row is not null).SelectMany(row => GlyphRuns(row!)).ToArray();
            Assert.That(runs, Is.Not.Empty, "A font update must preserve the retained text.");
            Assert.That(runs.Select(run => run.FontRenderingEmSize),
                Is.All.EqualTo(changeFamily ? 14 : 28));
            Assert.That(new Typeface(terminal.FontFamily, FontStyles.Normal, FontWeights.Normal,
                FontStretches.Normal).TryGetGlyphTypeface(out GlyphTypeface expectedFace), Is.True);
            Assert.That(runs.Select(run => run.GlyphTypeface.FontUri), Is.All.EqualTo(expectedFace.FontUri));
            ushort[] textGlyphs = "FONT-CACHE"
                .Select(character => expectedFace.CharacterToGlyphMap[character]).ToArray();
            Assert.That(runs.Any(run => run.GlyphIndices.SequenceEqual(textGlyphs)), Is.True,
                "The cached terminal text must be redrawn with the new typeface.");
            Assert.That(after, Is.Not.EqualTo(before), "The visible retained frame must repaint.");
            terminal.FontSize = 14;
            terminal.FontFamily = new FontFamily("Consolas");
            DrainInput();
            Assert.That(RenderPixels(terminal), Is.EqualTo(before),
                "Restoring the font must redraw the same retained text again.");
            Assert.That(terminal.IsRunning, Is.False);
            Assert.That(Field<DispatcherTimer>(terminal, "_timer").IsEnabled, Is.False);
            Assert.That(terminal.LastError, Is.Null);
        }
        finally { Pump(terminal.StopAsync()); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void StoppedDpiChange_RebuildsRetainedGlyphs(bool stop) {
        Terminal terminal = new() {
            AutoStart = false,
            CommandLine = "\"" + Environment.GetEnvironmentVariable("ComSpec") + "\" /d /q /c echo DPI-CACHE"
        };
        using HwndSource source = Host(terminal);
        TaskCompletionSource<uint> exit = new();
        terminal.ProcessExited += (sender, args) => exit.TrySetResult(args.ExitCode);
        try {
            Pump(terminal.StartAsync());
            Pump(exit.Task);
            Assert.That(exit.Task.Result, Is.Zero);
            if (stop) { Pump(terminal.StopAsync()); }
            RenderPixels(terminal);
            DpiScale oldDpi = VisualTreeHelper.GetDpi(terminal);
            DpiScale newDpi = new(oldDpi.DpiScaleX * 1.5, oldDpi.DpiScaleY * 1.5);
            VisualTreeHelper.SetRootDpi(terminal, newDpi);
            DrainInput();
            RenderPixels(terminal);
            GlyphRun[] runs = Field<DrawingGroup?[]>(terminal, "_rows")
                .Where(row => row is not null).SelectMany(row => GlyphRuns(row!)).ToArray();
            Assert.That(runs, Is.Not.Empty);
            Assert.That(runs.Select(run => (double)run.PixelsPerDip),
                Is.All.EqualTo(newDpi.PixelsPerDip).Within(0.001));
            Assert.That(terminal.IsRunning, Is.False);
            Assert.That(Field<DispatcherTimer>(terminal, "_timer").IsEnabled, Is.False);
            Assert.That(terminal.LastError, Is.Null);
        }
        finally { Pump(terminal.StopAsync()); }
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void ControlAltWithoutText_ForwardsPressAndRelease(bool immediateRelease, bool controlComposition) {
        Terminal terminal = new() { AutoStart = false, CommandLine = KeyEchoCommand() };
        using HwndSource source = Host(terminal);
        using KeyboardState keyboard = new();
        try {
            Pump(terminal.StartAsync());
            TerminalSession session = Field<TerminalSession>(terminal, "_session");
            WaitFor(() => ReadText(session).Contains("key-echo-ready"));
            keyboard.SetControlAlt(true);
            Assert.That(Keyboard.Modifiers, Is.EqualTo(ModifierKeys.Control | ModifierKeys.Alt));
            RaiseKey(terminal, source, Key.A, Keyboard.PreviewKeyDownEvent);
            if (controlComposition) {
                InputComposition composition = new(terminal, "\u0001", false, true);
                terminal.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) {
                    RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent
                });
                terminal.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) {
                    RoutedEvent = TextCompositionManager.TextInputEvent
                });
            }
            keyboard.SetControlAlt(false);
            if (!immediateRelease) { DrainInput(); }
            RaiseKey(terminal, source, Key.A, Keyboard.PreviewKeyUpEvent);
            DrainInput();
            session.Engine.Key(TerminalKey.Unidentified, 0, 1, "!");
            WaitFor(() => ReadKeyBytes(session).EndsWith("21", StringComparison.Ordinal));
            Assert.That(ReadKeyBytes(session), Is.EqualTo("1b5b39373b37751b5b39373b313a337521"),
                "The child must receive Ctrl+Alt+A with captured modifiers, its release, and the barrier.");
            Assert.That(terminal.LastError, Is.Null);
        }
        finally { Pump(terminal.StopAsync()); }
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void ControlAltComposition_SendsTextOnce(bool delayedComposition, bool systemText) {
        Terminal terminal = new() { AutoStart = false, CommandLine = KeyEchoCommand() };
        using HwndSource source = Host(terminal);
        using KeyboardState keyboard = new();
        try {
            Pump(terminal.StartAsync());
            TerminalSession session = Field<TerminalSession>(terminal, "_session");
            WaitFor(() => ReadText(session).Contains("key-echo-ready"));
            keyboard.SetControlAlt(true);
            RaiseKey(terminal, source, Key.E, Keyboard.PreviewKeyDownEvent);
            InputComposition composition = new(terminal, "\u20ac", systemText, false);
            if (delayedComposition) {
                terminal.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) {
                    RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent
                });
                keyboard.SetControlAlt(false);
                RaiseKey(terminal, source, Key.E, Keyboard.PreviewKeyUpEvent);
                DrainInput();
            }
            terminal.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) {
                RoutedEvent = TextCompositionManager.TextInputEvent
            });
            keyboard.SetControlAlt(false);
            RaiseKey(terminal, source, Key.E, Keyboard.PreviewKeyUpEvent);
            DrainInput();
            session.Engine.Key(TerminalKey.Unidentified, 0, 1, "!");
            WaitFor(() => ReadKeyBytes(session).EndsWith("21", StringComparison.Ordinal));
            Assert.That(ReadKeyBytes(session), Is.EqualTo("e282ac21"),
                "AltGr text must arrive exactly once without an additional raw Ctrl+Alt chord or release.");
            Assert.That(terminal.LastError, Is.Null);
        }
        finally { Pump(terminal.StopAsync()); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ControlAltPendingPress_IsCancelledOnFocusLossOrStop(bool stop) {
        Terminal terminal = new() { AutoStart = false, CommandLine = KeyEchoCommand() };
        using HwndSource source = Host(terminal);
        using KeyboardState keyboard = new();
        try {
            Pump(terminal.StartAsync());
            TerminalSession session = Field<TerminalSession>(terminal, "_session");
            WaitFor(() => ReadText(session).Contains("key-echo-ready"));
            keyboard.SetControlAlt(true);
            RaiseKey(terminal, source, Key.A, Keyboard.PreviewKeyDownEvent);
            keyboard.SetControlAlt(false);
            if (stop) {
                Pump(terminal.StopAsync());
                Pump(terminal.StartAsync());
                session = Field<TerminalSession>(terminal, "_session");
                WaitFor(() => ReadText(session).Contains("key-echo-ready"));
            }
            else {
                terminal.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice,
                    Environment.TickCount, null, null) {
                    RoutedEvent = UIElement.LostKeyboardFocusEvent
                });
            }
            DrainInput();
            session.Engine.Key(TerminalKey.Unidentified, 0, 1, "!");
            WaitFor(() => ReadKeyBytes(session).EndsWith("21", StringComparison.Ordinal));
            Assert.That(ReadKeyBytes(session), Is.EqualTo("21"),
                "An abandoned pending chord must not be sent to an unfocused or replacement session.");
            Assert.That(terminal.LastError, Is.Null);
        }
        finally { Pump(terminal.StopAsync()); }
    }

    [Test]
    public void ControlAltConsecutiveKeys_PreserveInputOrder() {
        Terminal terminal = new() { AutoStart = false, CommandLine = KeyEchoCommand() };
        using HwndSource source = Host(terminal);
        using KeyboardState keyboard = new();
        try {
            Pump(terminal.StartAsync());
            TerminalSession session = Field<TerminalSession>(terminal, "_session");
            WaitFor(() => ReadText(session).Contains("key-echo-ready"));
            keyboard.SetControlAlt(true);
            RaiseKey(terminal, source, Key.A, Keyboard.PreviewKeyDownEvent);
            RaiseKey(terminal, source, Key.B, Keyboard.PreviewKeyDownEvent);
            keyboard.SetControlAlt(false);
            RaiseKey(terminal, source, Key.B, Keyboard.PreviewKeyUpEvent);
            RaiseKey(terminal, source, Key.A, Keyboard.PreviewKeyUpEvent);
            DrainInput();
            session.Engine.Key(TerminalKey.Unidentified, 0, 1, "!");
            WaitFor(() => ReadKeyBytes(session).EndsWith("21", StringComparison.Ordinal));
            Assert.That(ReadKeyBytes(session),
                Is.EqualTo("1b5b39373b37751b5b39383b37751b5b39383b313a33751b5b39373b313a337521"),
                "Deferred presses must preserve A-down, B-down, B-up, A-up order.");
        }
        finally { Pump(terminal.StopAsync()); }
    }

    [TestCase(Key.C, ModifierKeys.Control)]
    [TestCase(Key.C, ModifierKeys.Control | ModifierKeys.Shift)]
    [TestCase(Key.Insert, ModifierKeys.Control)]
    public void CopyShortcut_CopiesPendingSelectionAndClearsItWithoutSendingKeys(Key key, ModifierKeys modifiers) {
        Terminal terminal = new() { AutoStart = false, CommandLine = KeyEchoCommand() };
        using HwndSource source = Host(terminal);
        using KeyboardState keyboard = new();
        try {
            Pump(terminal.StartAsync());
            TerminalSession session = Field<TerminalSession>(terminal, "_session");
            WaitFor(() => ReadText(session).Contains("key-echo-ready"));
            Field<DispatcherTimer>(terminal, "_timer").Stop();
            byte[] text = Encoding.UTF8.GetBytes("\u001b[2J\u001b[Hcopied-text");
            session.Engine.Write(text, text.Length);
            session.Engine.Select(0, 0, 0, 0);
            typeof(Terminal).GetField("_selectionStart", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(terminal, new Point(0, 0));
            typeof(Terminal).GetField("_selectionEnd", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(terminal, new Point(0, 0));
            typeof(Terminal).GetMethod("QueueSelection", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(terminal, new object[] { new Point(5, 0) });
            Clipboard.SetText("unchanged");
            keyboard.SetModifiers(modifiers);

            RaiseKey(terminal, source, key, Keyboard.PreviewKeyDownEvent);
            RaiseKey(terminal, source, key, Keyboard.PreviewKeyDownEvent);
            terminal.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice,
                new InputComposition(terminal, "\u0003", false, false)) {
                RoutedEvent = TextCompositionManager.TextInputEvent
            });
            RaiseKey(terminal, source, key, Keyboard.PreviewKeyUpEvent);

            Assert.That(Clipboard.GetText(), Is.EqualTo("copied"));
            Assert.That(session.Engine.SelectedText(), Is.Empty);
            Assert.That(Field<Point?>(terminal, "_selectionStart"), Is.Null);
            Assert.That(Field<bool>(terminal, "_selectionPending"), Is.False);
            Assert.That(Field<TerminalRow?[]>(terminal, "_rowSnapshots")
                .Where(row => row is not null).SelectMany(row => row!.Cells).All(cell => cell.Selected == 0), Is.True);
            byte[] newline = Encoding.UTF8.GetBytes("\r\n");
            session.Engine.Write(newline, newline.Length);
            session.Engine.Key(TerminalKey.Unidentified, 0, 1, "!");
            WaitFor(() => ReadKeyBytes(session).EndsWith("21", StringComparison.Ordinal));
            Assert.That(ReadKeyBytes(session), Is.EqualTo("21"), "Copy must not send a press or release to the child.");
            Assert.That(terminal.LastError, Is.Null);
        }
        finally { Pump(terminal.StopAsync()); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RightClick_CopiesSelectionAndSuppressesBothMouseEvents(bool mouseTracking) {
        Terminal terminal = new() { AutoStart = false, CommandLine = KeyEchoCommand() };
        using HwndSource source = Host(terminal);
        try {
            Pump(terminal.StartAsync());
            TerminalSession session = Field<TerminalSession>(terminal, "_session");
            WaitFor(() => ReadText(session).Contains("key-echo-ready"));
            Field<DispatcherTimer>(terminal, "_timer").Stop();
            string modes = mouseTracking ? "\u001b[?1000h\u001b[?1006h" : "";
            byte[] text = Encoding.UTF8.GetBytes("\u001b[2J\u001b[Hcopied-text" + modes);
            session.Engine.Write(text, text.Length);
            session.Engine.Select(0, 0, 5, 0);
            typeof(Terminal).GetMethod("RefreshFrame", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(terminal, new object[] { false });
            Clipboard.SetText("unchanged");

            terminal.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) {
                RoutedEvent = Mouse.MouseDownEvent
            });
            terminal.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) {
                RoutedEvent = Mouse.MouseUpEvent
            });

            Assert.That(Clipboard.GetText(), Is.EqualTo("copied"));
            Assert.That(session.Engine.SelectedText(), Is.Empty);
            byte[] newline = Encoding.UTF8.GetBytes("\r\n");
            session.Engine.Write(newline, newline.Length);
            session.Engine.Key(TerminalKey.Unidentified, 0, 1, "!");
            WaitFor(() => ReadKeyBytes(session).EndsWith("21", StringComparison.Ordinal));
            Assert.That(ReadKeyBytes(session), Is.EqualTo("21"), "Local copy must not send an orphan mouse release.");
            Assert.That(terminal.LastError, Is.Null);
        }
        finally { Pump(terminal.StopAsync()); }
    }

    [Test]
    public void ControlC_WithoutSelectionStillReachesChild() {
        Terminal terminal = new() { AutoStart = false, CommandLine = KeyEchoCommand() };
        using HwndSource source = Host(terminal);
        using KeyboardState keyboard = new();
        try {
            Pump(terminal.StartAsync());
            TerminalSession session = Field<TerminalSession>(terminal, "_session");
            WaitFor(() => ReadText(session).Contains("key-echo-ready"));
            Clipboard.SetText("unchanged");
            keyboard.SetModifiers(ModifierKeys.Control);

            RaiseKey(terminal, source, Key.C, Keyboard.PreviewKeyDownEvent);
            RaiseKey(terminal, source, Key.C, Keyboard.PreviewKeyUpEvent);
            session.Engine.Key(TerminalKey.Unidentified, 0, 1, "!");

            WaitFor(() => ReadKeyBytes(session).EndsWith("21", StringComparison.Ordinal));
            Assert.That(ReadKeyBytes(session), Is.EqualTo("1b5b39393b35751b5b39393b353a337521"));
            Assert.That(Clipboard.GetText(), Is.EqualTo("unchanged"));
            Assert.That(terminal.LastError, Is.Null);
        }
        finally { Pump(terminal.StopAsync()); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RightClick_WithoutSelectionPreservesClipboardAndApplicationMouseInput(bool mouseTracking) {
        Terminal terminal = new() { AutoStart = false, CommandLine = KeyEchoCommand() };
        using HwndSource source = Host(terminal);
        try {
            Pump(terminal.StartAsync());
            TerminalSession session = Field<TerminalSession>(terminal, "_session");
            WaitFor(() => ReadText(session).Contains("key-echo-ready"));
            if (mouseTracking) {
                byte[] modes = Encoding.UTF8.GetBytes("\u001b[?1000h\u001b[?1006h");
                session.Engine.Write(modes, modes.Length);
                typeof(Terminal).GetMethod("RefreshFrame", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(terminal, new object[] { false });
            }
            Clipboard.SetText("unchanged");

            terminal.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) {
                RoutedEvent = Mouse.MouseDownEvent
            });
            terminal.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) {
                RoutedEvent = Mouse.MouseUpEvent
            });
            session.Engine.Key(TerminalKey.Unidentified, 0, 1, "!");

            WaitFor(() => ReadKeyBytes(session).EndsWith("21", StringComparison.Ordinal));
            string input = ReadKeyBytes(session);
            if (mouseTracking) {
                Assert.That(input, Does.StartWith("1b5b3c32"), "The application must receive SGR right-button input.");
                Assert.That(input, Does.Contain("4d"), "The button press must reach the application.");
                Assert.That(input, Does.Contain("6d"), "The button release must reach the application.");
            }
            else {
                Assert.That(input, Is.EqualTo("21"));
            }
            Assert.That(Clipboard.GetText(), Is.EqualTo("unchanged"));
            Assert.That(terminal.LastError, Is.Null);
        }
        finally { Pump(terminal.StopAsync()); }
    }
    [Test]
    public void CopyShortcut_AllowsAltGrTextWhenModifiersChange() {
        Terminal terminal = new() { AutoStart = false, CommandLine = KeyEchoCommand() };
        using HwndSource source = Host(terminal);
        using KeyboardState keyboard = new();
        try {
            Pump(terminal.StartAsync());
            TerminalSession session = Field<TerminalSession>(terminal, "_session");
            WaitFor(() => ReadText(session).Contains("key-echo-ready"));
            keyboard.SetModifiers(ModifierKeys.Control | ModifierKeys.Shift);
            RaiseKey(terminal, source, Key.C, Keyboard.PreviewKeyDownEvent);
            keyboard.SetControlAlt(true);
            RaiseKey(terminal, source, Key.C, Keyboard.PreviewKeyDownEvent);
            terminal.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice,
                new InputComposition(terminal, "é", false, false)) {
                RoutedEvent = TextCompositionManager.TextInputEvent
            });
            RaiseKey(terminal, source, Key.C, Keyboard.PreviewKeyUpEvent);
            session.Engine.Key(TerminalKey.Unidentified, 0, 1, "!");

            WaitFor(() => ReadKeyBytes(session).EndsWith("21", StringComparison.Ordinal));
            Assert.That(ReadKeyBytes(session), Is.EqualTo("c3a921"));
            Assert.That(terminal.LastError, Is.Null);
        }
        finally { Pump(terminal.StopAsync()); }
    }
    private sealed class KeyboardState : IDisposable {
        private readonly byte[] _saved = new byte[256];

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern int GetKeyboardState([Out] byte[] state);

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern int SetKeyboardState([In] byte[] state);

        internal KeyboardState() {
            Assert.That(GetKeyboardState(_saved), Is.Not.Zero);
        }

        internal void SetControlAlt(bool down) {
            SetModifiers(down ? ModifierKeys.Control | ModifierKeys.Alt : ModifierKeys.None);
        }

        internal void SetModifiers(ModifierKeys modifiers) {
            byte[] state = new byte[256];
            if ((modifiers & ModifierKeys.Control) != 0) { state[0x11] = state[0xa2] = 0x80; }
            if ((modifiers & ModifierKeys.Alt) != 0) { state[0x12] = state[0xa5] = 0x80; }
            if ((modifiers & ModifierKeys.Shift) != 0) { state[0x10] = state[0xa0] = 0x80; }
            Assert.That(SetKeyboardState(state), Is.Not.Zero);
        }

        public void Dispose() {
            Assert.That(SetKeyboardState(_saved), Is.Not.Zero);
        }
    }

    private sealed class InputComposition : TextComposition {
        internal InputComposition(Terminal terminal, string text, bool system, bool control)
            : base(InputManager.Current, terminal, text) {
            if (system) { SystemText = text; Text = ""; }
            if (control) { ControlText = text; Text = ""; }
        }
    }

    [Test]
    public void TimerObservedInputDrop_KeepsRendering() {
        Terminal terminal = new() { AutoStart = false };
        using HwndSource source = Host(terminal);
        List<Exception> failures = new();
        terminal.Failed += (sender, args) => failures.Add(args.Exception);
        try {
            Pump(terminal.StartAsync());
            TerminalSession session = Field<TerminalSession>(terminal, "_session");
            /* Bypass Send so the timer is the first observer of the native drop. */
            session.Engine.Paste(new string('x', 17 * 1024 * 1024));
            WaitFor(() => failures.Count != 0);
            Assert.That(failures, Has.Count.EqualTo(1));
            Assert.That(failures[0].HResult, Is.EqualTo(unchecked((int)0x80070718)));
            Assert.That(terminal.IsRunning, Is.True);
            Assert.That(Field<DispatcherTimer>(terminal, "_timer").IsEnabled, Is.True,
                "A recoverable drop must leave rendering enabled.");
            byte[] title = Encoding.UTF8.GetBytes("\u001b]0;after-input-drop\a");
            session.Engine.Write(title, title.Length);
            WaitFor(() => terminal.Title == "after-input-drop");
            Assert.That(failures, Has.Count.EqualTo(1));
        }
        finally { Pump(terminal.StopAsync()); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ShortcutTransition_ReleasesForwardedKey(bool shortcutFirst) {
        string architecture = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "AMD64";
        string rid = architecture.Equals("ARM64", StringComparison.OrdinalIgnoreCase) ? "win-arm64" : "win-x64";
        string echo = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "helpers", rid, "key-echo.exe");
        Terminal terminal = new() { AutoStart = false, CommandLine = "\"" + echo + "\"" };
        using HwndSource source = Host(terminal);
        using KeyboardState keyboard = new();
        keyboard.SetControlAlt(false);
        try {
            Pump(terminal.StartAsync());
            TerminalSession session = Field<TerminalSession>(terminal, "_session");
            WaitFor(() => ReadText(session).Contains("key-echo-ready"));
            HashSet<Key> shortcuts = Field<HashSet<Key>>(terminal, "_shortcutKeys");
            /* Keyboard.Modifiers uses physical state. Seed only the shortcut
             * bookkeeping; presses and releases still traverse real WPF input. */
            if (shortcutFirst) { shortcuts.Add(Key.C); }
            RaiseKey(terminal, source, Key.C, Keyboard.PreviewKeyDownEvent);
            TextComposition composition = new(InputManager.Current, terminal, "c");
            terminal.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) {
                RoutedEvent = TextCompositionManager.TextInputEvent
            });
            if (!shortcutFirst) { shortcuts.Add(Key.C); }
            RaiseKey(terminal, source, Key.C, Keyboard.PreviewKeyUpEvent);
            Assert.That(Field<HashSet<Key>>(terminal, "_forwardedKeys"), Does.Not.Contain(Key.C),
                "Shortcut bookkeeping must not retain a physically released key.");
            WaitFor(() => ReadText(session).Contains("1b5b39393b313a3375"));
            Assert.That(shortcuts, Does.Not.Contain(Key.C));
            Assert.That(terminal.LastError, Is.Null);
        }
        finally { Pump(terminal.StopAsync()); }
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void ExitSubscriber_CanReplaceSessionWithoutStaleFailures(bool failFrame, bool restart) {
        Terminal terminal = new() { AutoStart = false };
        using HwndSource source = Host(terminal);
        List<Exception> failures = new();
        terminal.Failed += (sender, args) => failures.Add(args.Exception);
        try {
            Pump(terminal.StartAsync());
            TerminalSession session = Field<TerminalSession>(terminal, "_session");
            DispatcherTimer timer = Field<DispatcherTimer>(terminal, "_timer");
            timer.Stop();
            session.Engine.Paste(new string('x', 17 * 1024 * 1024));
            session.Engine.Paste("exit /b 7");
            session.Key(0x0d, 0, 1, "");
            /* Keep the dispatcher paused until completion so the automatic exit
             * observer cannot consume the pending error before we set up its callback. */
            Assert.That(session.Completion.Wait(TimeSpan.FromSeconds(15)), Is.True);
            Assert.That(session.Completion.Result, Is.EqualTo(7));
            if (failFrame) { session.Engine.Dispose(); }
            TaskCompletionSource<uint> observed = new();
            terminal.ProcessExited += (sender, args) => {
                Pump(terminal.StopAsync());
                if (restart) { Pump(terminal.StartAsync()); }
                observed.TrySetResult(args.ExitCode);
            };
            Pump(observed.Task);
            Assert.That(observed.Task.Result, Is.EqualTo(7));
            Assert.That(failures, Is.Empty, "Old-session errors must not contaminate a replacement session.");
            Assert.That(terminal.LastError, Is.Null);
            Assert.That(terminal.IsRunning, Is.EqualTo(restart));
            if (restart) {
                Assert.That(Field<TerminalSession>(terminal, "_session"), Is.Not.SameAs(session));
            }
        }
        finally { Pump(terminal.StopAsync()); }
    }

    [Test]
    public void ExitFailureSubscriber_CanRestartWithoutReceivingSecondStaleFailure() {
        Terminal terminal = new() { AutoStart = false };
        using HwndSource source = Host(terminal);
        List<Exception> failures = new();
        List<uint> exitCodes = new();
        TaskCompletionSource<bool> restarted = new();
        terminal.ProcessExited += (sender, args) => exitCodes.Add(args.ExitCode);
        terminal.Failed += (sender, args) => {
            failures.Add(args.Exception);
            Pump(terminal.StopAsync());
            Pump(terminal.StartAsync());
            restarted.TrySetResult(true);
        };
        try {
            Pump(terminal.StartAsync());
            TerminalSession session = Field<TerminalSession>(terminal, "_session");
            Field<DispatcherTimer>(terminal, "_timer").Stop();
            session.Engine.Paste(new string('x', 17 * 1024 * 1024));
            session.Engine.Paste("exit /b 8");
            session.Key(0x0d, 0, 1, "");
            Assert.That(session.Completion.Wait(TimeSpan.FromSeconds(15)), Is.True);
            session.Engine.Dispose();
            Pump(restarted.Task);
            Assert.That(exitCodes, Is.EqualTo(new uint[] { 8 }));
            Assert.That(failures, Has.Count.EqualTo(1),
                "A second collected error must be ignored after the first failure handler replaces the session.");
            Assert.That(failures[0], Is.TypeOf<ObjectDisposedException>());
            Assert.That(terminal.LastError, Is.Null);
            Assert.That(terminal.IsRunning, Is.True);
        }
        finally { Pump(terminal.StopAsync()); }
    }

    [SetUp]
    public void SetUp() {
        _previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
    }

    [TearDown]
    public void TearDown() {
        SynchronizationContext.SetSynchronizationContext(_previousContext);
    }

    [Test]
    public void ShellExit_RendersTextAndRaisesExitEvent() {
        Terminal terminal = new() {
            AutoStart = false,
            CommandLine = "\"" + Environment.GetEnvironmentVariable("ComSpec")
                + "\" /d /q /c \"echo WPF-Ghostty-render-ok & exit /b 9\""
        };
        using HwndSource source = Host(terminal);
        TaskCompletionSource<uint> exit = new();
        terminal.ProcessExited += (sender, args) => exit.TrySetResult(args.ExitCode);
        try {
            Pump(terminal.StartAsync());
            Pump(exit.Task);
            Assert.That(exit.Task.Result, Is.EqualTo(9));
            Assert.That(terminal.IsRunning, Is.False);
            Assert.That(terminal.LastError, Is.Null);
            terminal.UpdateLayout();
            RenderTargetBitmap bitmap = new(800, 400, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(terminal);
            byte[] pixels = new byte[800 * 400 * 4];
            bitmap.CopyPixels(pixels, 800 * 4, 0);
            int litPixels = Enumerable.Range(0, 800 * 40).Count(index => pixels[index * 4] > 100);
            PngBitmapEncoder encoder = new();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream file = File.Create(
                Path.Combine(TestContext.CurrentContext.WorkDirectory, "terminal-render.png"));
            encoder.Save(file);
            Assert.That(litPixels, Is.GreaterThan(100), "Terminal glyphs must be visible in the rendered first rows.");
        }
        finally { Pump(terminal.StopAsync()); }
    }

    [Test]
    public void ApplicationBackground_RendersAcrossTerminal() {
        string script = "[Console]::Write([char]27 + ']11;rgb:1212/3434/abab' + [char]7)";
        string command = "powershell.exe -NoProfile -NonInteractive -EncodedCommand "
            + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        Terminal terminal = new() { AutoStart = false, CommandLine = command };
        using HwndSource source = Host(terminal);
        TaskCompletionSource<uint> exit = new();
        terminal.ProcessExited += (sender, args) => exit.TrySetResult(args.ExitCode);
        try {
            Pump(terminal.StartAsync());
            Pump(exit.Task);
            Assert.That(exit.Task.Result, Is.Zero);
            Assert.That(terminal.LastError, Is.Null);
            terminal.UpdateLayout();
            RenderTargetBitmap bitmap = new(800, 400, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(terminal);
            foreach (Point point in new[] { new Point(100, 100), new Point(799, 399) }) {
                byte[] pixel = new byte[4];
                bitmap.CopyPixels(new Int32Rect((int)point.X, (int)point.Y, 1, 1), pixel, 4, 0);
                Assert.That(pixel, Is.EqualTo(new byte[] { 0xab, 0x34, 0x12, 0xff }),
                    "Application background must fill empty cells and the remaining control area at " + point);
            }
        }
        finally { Pump(terminal.StopAsync()); }
    }

    [Test]
    public void TextAndEnter_RoutedThroughControl_ExecuteShellCommand() {
        Terminal terminal = new() { AutoStart = false };
        using HwndSource source = Host(terminal);
        TaskCompletionSource<uint> exit = new();
        terminal.ProcessExited += (sender, args) => exit.TrySetResult(args.ExitCode);
        try {
            Pump(terminal.StartAsync());
            TextComposition composition = new(InputManager.Current, terminal, "exit /b 11");
            terminal.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) {
                RoutedEvent = TextCompositionManager.TextInputEvent
            });
            terminal.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter) {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            });
            Pump(exit.Task);
            Assert.That(exit.Task.Result, Is.EqualTo(11));
            Assert.That(terminal.LastError, Is.Null);
        }
        finally { Pump(terminal.StopAsync()); }
    }

    [TestCase(Key.F4, false)]
    [TestCase(Key.F4, true)]
    [TestCase(Key.Space, false)]
    [TestCase(Key.Space, true)]
    public void WindowShortcuts_RemainUnhandledAndSendNoTerminalInput(Key key, bool systemKey) {
        Terminal terminal = new() { AutoStart = false, CommandLine = KeyEchoCommand() };
        using HwndSource source = Host(terminal);
        using KeyboardState keyboard = new();
        try {
            Pump(terminal.StartAsync());
            TerminalSession session = Field<TerminalSession>(terminal, "_session");
            WaitFor(() => ReadText(session).Contains("key-echo-ready"));
            keyboard.SetModifiers(ModifierKeys.Alt);
            Assert.That(Keyboard.Modifiers, Is.EqualTo(ModifierKeys.Alt));
            KeyEventArgs press = new(Keyboard.PrimaryDevice, source, Environment.TickCount, key) {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            };
            if (systemKey) {
                typeof(KeyEventArgs).GetMethod("MarkSystem", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(press, null);
                Assert.That(press.Key, Is.EqualTo(Key.System));
            }
            terminal.RaiseEvent(press);
            Assert.That(press.Handled, Is.False, "The host must receive the window shortcut.");
            if (key == Key.Space) {
                InputComposition composition = new(terminal, " ", true, false);
                TextCompositionEventArgs text = new(Keyboard.PrimaryDevice, composition) {
                    RoutedEvent = TextCompositionManager.TextInputEvent
                };
                terminal.RaiseEvent(text);
                Assert.That(text.Handled, Is.False, "System-menu text must remain available to WPF.");
            }
            keyboard.SetModifiers(ModifierKeys.None);
            KeyEventArgs release = new(Keyboard.PrimaryDevice, source, Environment.TickCount, key) {
                RoutedEvent = Keyboard.PreviewKeyUpEvent
            };
            terminal.RaiseEvent(release);
            Assert.That(release.Handled, Is.False);
            session.Engine.Key(TerminalKey.Unidentified, 0, 1, "!");
            WaitFor(() => ReadKeyBytes(session).EndsWith("21", StringComparison.Ordinal));
            Assert.That(ReadKeyBytes(session), Is.EqualTo("21"),
                "Neither shortcut presses, system text, nor releases may reach the child.");
            Assert.That(terminal.LastError, Is.Null);
        }
        finally { Pump(terminal.StopAsync()); }
    }

    [TestCase(Key.F4, ModifierKeys.None)]
    [TestCase(Key.F4, ModifierKeys.Control | ModifierKeys.Alt)]
    [TestCase(Key.F4, ModifierKeys.Shift | ModifierKeys.Alt)]
    [TestCase(Key.F3, ModifierKeys.Alt)]
    [TestCase(Key.Enter, ModifierKeys.Alt)]
    public void OtherFunctionAndAltKeys_StillReachTerminal(Key key, ModifierKeys modifiers) {
        Terminal terminal = new() { AutoStart = false, CommandLine = KeyEchoCommand() };
        using HwndSource source = Host(terminal);
        using KeyboardState keyboard = new();
        try {
            Pump(terminal.StartAsync());
            TerminalSession session = Field<TerminalSession>(terminal, "_session");
            WaitFor(() => ReadText(session).Contains("key-echo-ready"));
            keyboard.SetModifiers(modifiers);
            Assert.That(Keyboard.Modifiers, Is.EqualTo(modifiers));
            KeyEventArgs press = new(Keyboard.PrimaryDevice, source, Environment.TickCount, key) {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            };
            terminal.RaiseEvent(press);
            Assert.That(press.Handled, Is.True);
            RaiseKey(terminal, source, key, Keyboard.PreviewKeyUpEvent);
            session.Engine.Key(TerminalKey.Unidentified, 0, 1, "!");
            WaitFor(() => ReadKeyBytes(session).EndsWith("21", StringComparison.Ordinal));
            Assert.That(ReadKeyBytes(session), Is.Not.EqualTo("21"), "The child must receive the key.");
            Assert.That(terminal.LastError, Is.Null);
        }
        finally { Pump(terminal.StopAsync()); }
    }

    [Test]
    public void TextlessKeyPresses_AreForwardedToTheTerminal() {
        Terminal terminal = new() { AutoStart = false };
        using HwndSource source = Host(terminal);
        Pump(terminal.StartAsync());
        foreach (Key key in new[] {
            Key.LeftShift, Key.RightShift, Key.LeftCtrl, Key.RightCtrl, Key.LeftAlt, Key.RightAlt,
            Key.LWin, Key.RWin, Key.CapsLock, Key.NumLock, Key.Scroll, Key.PrintScreen, Key.Pause,
            Key.Apps, Key.Clear, Key.VolumeMute, Key.MediaPlayPause, Key.BrowserBack, Key.Sleep }) {
            KeyEventArgs args = new(Keyboard.PrimaryDevice, source, Environment.TickCount, key) {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            };
            terminal.RaiseEvent(args);
            Assert.That(args.Handled, Is.True, "A " + key + " press must be forwarded to the terminal.");
        }
        KeyEventArgs text = new(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.OemQuestion) {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
        terminal.RaiseEvent(text);
        Assert.That(text.Handled, Is.False, "A text-producing key press must flow on to text input.");
    }

    [Test]
    public void KeyboardEvents_PressReleaseAndFocusLoss_AreConsistent() {
        string architecture = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "AMD64";
        string rid = architecture.Equals("ARM64", StringComparison.OrdinalIgnoreCase) ? "win-arm64" : "win-x64";
        string echo = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "helpers", rid, "key-echo.exe");
        Assert.That(File.Exists(echo), Is.True, "Run scripts/Build-Native.ps1 to build the key echo helper.");
        Terminal terminal = new() { AutoStart = false, CommandLine = "\"" + echo + "\"" };
        using HwndSource source = Host(terminal);
        using KeyboardState keyboard = new();
        keyboard.SetControlAlt(false);
        Pump(terminal.StartAsync());
        TerminalSession session = (TerminalSession)typeof(Terminal).GetField("_session",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(terminal)!;
        /* The helper enables Kitty flags 2 and 8, so every key press and release
         * reaches the child as a CSI u escape sequence. */
        string shiftPress = "1b5b353734343175";
        string shiftRelease = "1b5b35373434313b313a3375";
        string rightShiftRelease = "1b5b35373434373b313a3375";
        string textPress = "1b5b31323075";
        string textRelease = "1b5b3132303b313a3375";
        WaitFor(() => ReadText(session).Contains("key-echo-ready"));
        /* A modifier press and its release reach the child as a matching pair. */
        RaiseKey(terminal, source, Key.LeftShift, Keyboard.PreviewKeyDownEvent);
        RaiseKey(terminal, source, Key.LeftShift, Keyboard.PreviewKeyUpEvent);
        WaitFor(() => {
            string text = ReadText(session);
            return text.Contains(shiftPress) && text.Contains(shiftRelease);
        });
        /* A release whose press was never forwarded, for example a key pressed in
         * another window, is not sent at all. */
        RaiseKey(terminal, source, Key.RightShift, Keyboard.PreviewKeyUpEvent);
        /* Losing focus while a forwarded key is still held releases it. */
        RaiseKey(terminal, source, Key.LeftShift, Keyboard.PreviewKeyDownEvent);
        terminal.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice,
            Environment.TickCount, null, null) {
            RoutedEvent = UIElement.LostKeyboardFocusEvent
        });
        WaitFor(() => CountText(ReadText(session), shiftRelease) >= 2);
        /* Text keys report their press through text input and still pair with releases. */
        RaiseKey(terminal, source, Key.X, Keyboard.PreviewKeyDownEvent);
        TextComposition composition = new(InputManager.Current, terminal, "x");
        terminal.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) {
            RoutedEvent = TextCompositionManager.TextInputEvent
        });
        RaiseKey(terminal, source, Key.X, Keyboard.PreviewKeyUpEvent);
        WaitFor(() => {
            string text = ReadText(session);
            return text.Contains(textPress) && text.Contains(textRelease);
        });
        string screen = ReadText(session);
        Assert.That(screen, Does.Not.Contain(rightShiftRelease),
            "A release without a forwarded press must not be sent.");
        Assert.That(screen, Does.Not.Contain("1b5b353734343775"),
            "A right-shift press was never forwarded.");
    }

    [Test]
    public void StartStopRestart_ReleasesPreviousSessionAndRemainsUsable() {
        Terminal terminal = new() { AutoStart = false };
        using HwndSource source = Host(terminal);
        try {
            Pump(terminal.StartAsync());
            Assert.That(terminal.IsRunning, Is.True);
            Pump(terminal.StopAsync());
            Assert.That(terminal.IsRunning, Is.False);
            Pump(terminal.StartAsync());
            Assert.That(terminal.IsRunning, Is.True);
            Assert.That(terminal.LastError, Is.Null);
        }
        finally { Pump(terminal.StopAsync()); }
    }

    [Test]
    public void LoadUnloadReload_AutomaticallyOwnsSessionLifetime() {
        Terminal terminal = new();
        using HwndSource source = Host(terminal);
        try {
            WaitFor(() => terminal.IsRunning);
            source.RootVisual = null;
            WaitFor(() => !terminal.IsRunning);
            source.RootVisual = terminal;
            WaitFor(() => terminal.IsRunning);
            Assert.That(terminal.LastError, Is.Null);
        }
        finally { Pump(terminal.StopAsync()); }
    }

    [Test]
    public void SustainedOutput_KeepsDispatcherResponsive() {
        Terminal terminal = new() {
            AutoStart = false,
            CommandLine = "\"" + Environment.GetEnvironmentVariable("ComSpec")
                + "\" /d /q /c \"for /l %i in (1,1,20000) do @echo terminal-output-%i\""
        };
        using HwndSource source = Host(terminal);
        TaskCompletionSource<uint> exit = new();
        terminal.ProcessExited += (sender, args) => exit.TrySetResult(args.ExitCode);
        Stopwatch clock = new();
        List<double> intervals = new();
        double previous = 0;
        DispatcherTimer timer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (sender, args) => {
            double current = clock.Elapsed.TotalMilliseconds;
            intervals.Add(current - previous);
            previous = current;
        };
        try {
            clock.Start();
            timer.Start();
            Pump(terminal.StartAsync());
            Pump(exit.Task);
            timer.Stop();
            Assert.That(exit.Task.Result, Is.Zero);
            Assert.That(terminal.LastError, Is.Null);
            Assert.That(intervals.Count, Is.GreaterThan(2),
                "Input-priority dispatcher work must progress during output.");
            double[] ordered = intervals.OrderBy(value => value).ToArray();
            TestContext.WriteLine($"20,000 output lines: {clock.Elapsed.TotalMilliseconds:F0} ms; "
                + $"input tick p95: {ordered[(int)((ordered.Length - 1) * 0.95)]:F1} ms; "
                + $"max: {ordered.Last():F1} ms.");
            Assert.That(ordered.Last(), Is.LessThan(1000),
                "Terminal output must not stall the dispatcher for a second.");
        }
        finally {
            timer.Stop();
            Pump(terminal.StopAsync());
        }
    }

    [Test]
    public void FaultedSession_MouseSelectionAndScrollReportFailureWithoutCrashing() {
        Terminal terminal = new() { AutoStart = false };
        using HwndSource source = Host(terminal);
        TaskCompletionSource<Exception> failed = new();
        terminal.Failed += (sender, args) => failed.TrySetResult(args.Exception);
        try {
            Pump(terminal.StartAsync());
            FieldInfo sessionField = typeof(Terminal).GetField("_session",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            TerminalSession session = (TerminalSession)sessionField.GetValue(terminal)!;
            session.Engine.Dispose();
            MouseButtonEventArgs selection = new(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) {
                RoutedEvent = Mouse.MouseDownEvent
            };
            MouseWheelEventArgs scroll = new(Mouse.PrimaryDevice, Environment.TickCount, -120) {
                RoutedEvent = Mouse.MouseWheelEvent
            };
            Assert.That(() => terminal.RaiseEvent(selection), Throws.Nothing,
                "A selection failure must not escape the mouse handler.");
            Assert.That(() => terminal.RaiseEvent(scroll), Throws.Nothing,
                "A scroll failure must not escape the mouse handler.");
            Assert.That(failed.Task.IsCompleted, Is.True, "Mouse failures must be reported through Failed.");
            Assert.That(terminal.LastError, Is.Not.Null);
        }
        finally { Pump(terminal.StopAsync()); }
    }

    [Test]
    public void QueueOverflow_ReportsFailureOnceAndStillRaisesExitEvent() {
        Terminal terminal = new() {
            AutoStart = false,
            CommandLine = "\"" + Environment.GetEnvironmentVariable("ComSpec")
                + "\" /d /q /c \"ping -n 4 127.0.0.1 >nul\""
        };
        using HwndSource source = Host(terminal);
        TaskCompletionSource<uint> exit = new();
        List<Exception> failures = new();
        terminal.ProcessExited += (sender, args) => exit.TrySetResult(args.ExitCode);
        terminal.Failed += (sender, args) => failures.Add(args.Exception);
        try {
            Pump(terminal.StartAsync());
            terminal.Paste(new string('x', 17 * 1024 * 1024));
            Pump(exit.Task);
            Assert.That(exit.Task.Result, Is.EqualTo(0),
                "ProcessExited must still fire after a recoverable input failure.");
            Assert.That(terminal.IsRunning, Is.False);
            Assert.That(terminal.LastError, Is.Not.Null);
            Assert.That(failures, Has.Count.EqualTo(1),
                "A queue overflow must be reported once without faulting the session.");
        }
        finally { Pump(terminal.StopAsync()); }
    }

    [Test]
    public void FailedStart_DoesNotPreventStartingValidCommand() {
        Terminal terminal = new() { AutoStart = false, CommandLine = "Z:\\missing-terminal-app-87654321.exe" };
        using HwndSource source = Host(terminal);
        Task failed = terminal.StartAsync();
        try { Pump(failed); }
        catch (FileNotFoundException) { }
        Assert.That(failed.IsFaulted, Is.True);
        Assert.That(terminal.IsRunning, Is.False);
        terminal.CommandLine = "\"" + Environment.GetEnvironmentVariable("ComSpec") + "\"";
        try {
            Pump(terminal.StartAsync());
            Assert.That(terminal.IsRunning, Is.True);
        }
        finally { Pump(terminal.StopAsync()); }
    }
}
