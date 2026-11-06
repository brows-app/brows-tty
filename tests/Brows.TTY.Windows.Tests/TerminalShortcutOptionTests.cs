using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Threading;
using Brows.Ghostty;
using Brows.Win32;
using Brows.Windows.Controls;

namespace Brows.TTY.Tests;

[TestFixture, Apartment(ApartmentState.STA)]
public sealed class TerminalShortcutOptionTests {
    private const ModifierKeys ControlShiftModifiers = ModifierKeys.Control | ModifierKeys.Shift;
    private const string PasteText = "paste-action-only";
    private const string PastePrefixBytes = "1b5b3230307e";
    private const string PasteSuffixBytes = "1b5b3230317e";
    private const string ControlVPressBytes = "1b5b3131383b3575";
    private const string ControlVReleaseBytes = "1b5b3131383b353a3375";
    private const string HistoryPrefix = "shortcut-row-";
    private const int HistoryRows = 180;

    private static readonly ShortcutOption[] ShortcutOptions = {
        new("PressEnterToCopy", Key.Enter, ModifierKeys.None, ShortcutGroup.Copy, null),
        new("PressControlPlusCToCopy", Key.C, ModifierKeys.Control, ShortcutGroup.Copy, null),
        new("PressControlPlusShiftPlusCToCopy", Key.C, ControlShiftModifiers, ShortcutGroup.Copy, null),
        new("PressControlPlusInsertToCopy", Key.Insert, ModifierKeys.Control, ShortcutGroup.Copy, null),
        new("PressControlPlusVToPaste", Key.V, ModifierKeys.Control, ShortcutGroup.Paste, "v"),
        new("PressControlPlusShiftPlusVToPaste", Key.V, ControlShiftModifiers, ShortcutGroup.Paste, "V"),
        new("PressShiftPlusInsertToPaste", Key.Insert, ModifierKeys.Shift, ShortcutGroup.Paste, null),
        new("PressControlPlusShiftPlusUpToScroll", Key.Up, ControlShiftModifiers, ShortcutGroup.Scroll, null),
        new("PressControlPlusShiftPlusDownToScroll", Key.Down, ControlShiftModifiers, ShortcutGroup.Scroll, null),
        new("PressControlPlusShiftPlusPageUpToScrollOnePage", Key.PageUp, ControlShiftModifiers,
            ShortcutGroup.Scroll, null),
        new("PressControlPlusShiftPlusPageDownToScrollOnePage", Key.PageDown, ControlShiftModifiers,
            ShortcutGroup.Scroll, null),
        new("PressControlPlusShiftPlusHomeToScrollToTop", Key.Home, ControlShiftModifiers,
            ShortcutGroup.Scroll, null),
        new("PressControlPlusShiftPlusEndToScrollToBottom", Key.End, ControlShiftModifiers,
            ShortcutGroup.Scroll, null),
        new("PressControlPlusEqualsToIncreaseFontSize", Key.OemPlus, ModifierKeys.Control,
            ShortcutGroup.Font, "="),
        new("PressControlPlusShiftPlusEqualsToIncreaseFontSize", Key.OemPlus, ControlShiftModifiers,
            ShortcutGroup.Font, "+"),
        new("PressControlPlusAddToIncreaseFontSize", Key.Add, ModifierKeys.Control, ShortcutGroup.Font, "+"),
        new("PressControlPlusMinusToDecreaseFontSize", Key.OemMinus, ModifierKeys.Control,
            ShortcutGroup.Font, "-"),
        new("PressControlPlusSubtractToDecreaseFontSize", Key.Subtract, ModifierKeys.Control,
            ShortcutGroup.Font, "-"),
        new("PressControlPlus0ToResetFontSize", Key.D0, ModifierKeys.Control, ShortcutGroup.Font, "0"),
        new("PressControlPlusNumPad0ToResetFontSize", Key.NumPad0, ModifierKeys.Control, ShortcutGroup.Font, "0")
    };

    private SynchronizationContext? _previousContext;

    private static IEnumerable<TestCaseData> OptionCases() {
        foreach (ShortcutOption option in ShortcutOptions) {
            yield return new TestCaseData(option.PropertyName)
                .SetName("ShortcutOption_" + option.PropertyName + "_DefaultsAndRoundTrips");
        }
    }

    private static IEnumerable<TestCaseData> CasesFor(ShortcutGroup group, string behaviorName) {
        foreach (ShortcutOption option in ShortcutOptions) {
            if (option.Group == group) {
                yield return new TestCaseData(option.PropertyName, option.Key, option.Modifiers, option.Text)
                    .SetName("Disabled_" + option.PropertyName + "_" + behaviorName);
            }
        }
    }

    private static IEnumerable<TestCaseData> CopyCases() {
        return CasesFor(ShortcutGroup.Copy, "KeepsSelectionAndForwards");
    }

    private static IEnumerable<TestCaseData> PasteCases() {
        return CasesFor(ShortcutGroup.Paste, "SkipsClipboardPaste");
    }

    private static IEnumerable<TestCaseData> ScrollCases() {
        return CasesFor(ShortcutGroup.Scroll, "LeavesBottomViewportAndForwards");
    }

    private static IEnumerable<TestCaseData> FontCases() {
        return CasesFor(ShortcutGroup.Font, "KeepsCurrentSizeAndForwards");
    }

    private static void Pump(Task task) {
        DispatcherFrame frame = new();
        DispatcherTimer timeout = new() { Interval = TimeSpan.FromSeconds(15) };
        timeout.Tick += (sender, args) => frame.Continue = false;
        timeout.Start();
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        task.ContinueWith(completed => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)),
            TaskScheduler.Default);
        if (!task.IsCompleted) {
            Dispatcher.PushFrame(frame);
        }
        timeout.Stop();
        Assert.That(task.IsCompleted, Is.True, "Dispatcher operation exceeded 15 seconds.");
        task.GetAwaiter().GetResult();
    }

    private static HwndSource Host(TerminalEmulator terminal) {
        HwndSourceParameters parameters = new("Terminal shortcut option test") {
            WindowStyle = unchecked((int)0x80000000),
            Width = 1600,
            Height = 400
        };
        terminal.Width = 1600;
        terminal.Height = 400;
        HwndSource source = new(parameters) { RootVisual = terminal };
        terminal.Measure(new Size(1600, 400));
        terminal.Arrange(new Rect(0, 0, 1600, 400));
        terminal.UpdateLayout();
        return source;
    }

    private static void WaitFor(Func<bool> condition) {
        TaskCompletionSource<bool> completion = new();
        DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (sender, args) => {
            if (condition()) {
                completion.TrySetResult(true);
            }
        };
        timer.Start();
        try {
            Pump(completion.Task);
        }
        finally {
            timer.Stop();
        }
    }

    private static void DrainDispatcher() {
        Pump(Dispatcher.CurrentDispatcher.InvokeAsync(() => {
        }, DispatcherPriority.ContextIdle).Task);
    }

    private static T Field<T>(TerminalEmulator terminal, string name) {
        return (T)typeof(TerminalEmulator).GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(terminal)!;
    }

    private static PropertyInfo GetClrProperty(string propertyName) {
        PropertyInfo? property = typeof(TerminalEmulator).GetProperty(propertyName,
            BindingFlags.Instance | BindingFlags.Public);
        Assert.That(property, Is.Not.Null, "Missing CLR property " + propertyName + ".");
        Assert.That(property!.PropertyType, Is.EqualTo(typeof(bool)), propertyName);
        return property;
    }

    private static DependencyProperty GetDependencyProperty(string propertyName) {
        FieldInfo? field = typeof(TerminalEmulator).GetField(propertyName + "Property",
            BindingFlags.Static | BindingFlags.Public);
        Assert.That(field, Is.Not.Null, "Missing dependency property field for " + propertyName + ".");
        DependencyProperty dependencyProperty = (DependencyProperty)field!.GetValue(null)!;
        Assert.That(dependencyProperty.Name, Is.EqualTo(propertyName));
        return dependencyProperty;
    }

    private static void SetOption(TerminalEmulator terminal, string propertyName, bool value) {
        terminal.SetValue(GetDependencyProperty(propertyName), value);
    }

    private static TerminalSession StartKeyEcho(TerminalEmulator terminal) {
        string architecture = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "AMD64";
        string rid = architecture.Equals("ARM64", StringComparison.OrdinalIgnoreCase) ? "win-arm64" : "win-x64";
        terminal.CommandLine = "\"" + Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            "helpers", rid, "key-echo.exe") + "\"";
        Pump(terminal.StartAsync());
        TerminalSession session = Field<TerminalSession>(terminal, "_session");
        WaitFor(() => ReadText(session).IndexOf("key-echo-ready", StringComparison.Ordinal) >= 0);
        return session;
    }

    private static TerminalSession StartIdlePowerShell(TerminalEmulator terminal) {
        terminal.CommandLine = "powershell.exe -NoLogo -NoProfile -Command \"Start-Sleep -Seconds 60\"";
        Pump(terminal.StartAsync());
        return Field<TerminalSession>(terminal, "_session");
    }

    private static string ReadText(TerminalSession session) {
        TerminalFrame frame = session.Engine.ReadFrame(true);
        return string.Join("\n", frame.ChangedRows.OrderBy(row => row.Index).Select(row =>
            string.Concat(Enumerable.Range(0, row.Cells.Length).Select(row.GetText))));
    }

    private static string ReadKeyBytes(TerminalSession session) {
        return string.Concat(ReadText(session).Split('\n').Select(line => line.Trim())
            .Where(line => line.StartsWith("key-echo:", StringComparison.Ordinal))
            .Select(line => line.Substring("key-echo:".Length)));
    }

    private static void SendBarrier(TerminalSession session) {
        session.Engine.Key(TerminalKey.Unidentified, 0, 1, "!");
        WaitFor(() => ReadKeyBytes(session).EndsWith("21", StringComparison.Ordinal));
    }

    private static void Write(TerminalSession session, string text) {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        session.Engine.Write(bytes, bytes.Length);
    }

    private static string Hex(string text) {
        StringBuilder result = new(text.Length * 2);
        foreach (byte value in Encoding.UTF8.GetBytes(text)) {
            result.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }
        return result.ToString();
    }

    private static void SetClipboardText(string text) {
        DataObject data = new();
        data.SetData(DataFormats.UnicodeText, text);
        Clipboard.SetDataObject(data, false);
    }

    private static KeyEventArgs RaiseKey(TerminalEmulator terminal, HwndSource source, Key key,
        RoutedEvent routedEvent) {
        KeyEventArgs args = new(Keyboard.PrimaryDevice, source, Environment.TickCount, key) {
            RoutedEvent = routedEvent
        };
        terminal.RaiseEvent(args);
        return args;
    }

    private static TextCompositionEventArgs RaiseText(TerminalEmulator terminal, string text) {
        TextComposition composition = new(InputManager.Current, terminal, text);
        TextCompositionEventArgs args = new(Keyboard.PrimaryDevice, composition) {
            RoutedEvent = TextCompositionManager.TextInputEvent
        };
        terminal.RaiseEvent(args);
        return args;
    }

    private static void RaiseLocalShortcut(TerminalEmulator terminal, HwndSource source, KeyboardState keyboard,
        ShortcutOption option) {
        keyboard.SetModifiers(option.Modifiers);
        Assert.That(Keyboard.Modifiers, Is.EqualTo(option.Modifiers));
        KeyEventArgs press = RaiseKey(terminal, source, option.Key, Keyboard.PreviewKeyDownEvent);
        Assert.That(press.Handled, Is.True);
        if (option.Text is not null) {
            RaiseText(terminal, option.Text);
        }
        KeyEventArgs release = RaiseKey(terminal, source, option.Key, Keyboard.PreviewKeyUpEvent);
        Assert.That(release.Handled, Is.True);
        keyboard.SetModifiers(ModifierKeys.None);
    }

    private static void WriteHistory(TerminalSession session) {
        StringBuilder history = new();
        for (int row = 1; row <= HistoryRows; row++) {
            history.Append(HistoryPrefix).Append(row.ToString("D3", CultureInfo.InvariantCulture)).Append("\r\n");
        }
        Write(session, history.ToString());
    }

    private static int FirstHistoryRow(TerminalSession session) {
        foreach (string row in ReadText(session).Split('\n')) {
            int marker = row.IndexOf(HistoryPrefix, StringComparison.Ordinal);
            if (marker < 0) {
                continue;
            }
            int start = marker + HistoryPrefix.Length;
            bool parsed = int.TryParse(row.Substring(start, 3), NumberStyles.None,
                CultureInfo.InvariantCulture, out int number);
            if (parsed) {
                return number;
            }
        }
        Assert.Fail("The visible viewport contains no generated history row.");
        return 0;
    }

    private static ShortcutOption FindOption(string propertyName) {
        return ShortcutOptions.Single(option => option.PropertyName == propertyName);
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

    [TestCaseSource(nameof(OptionCases))]
    public void ShortcutOption_DefaultsTrueAndRoundTripsThroughClrAndDependencyProperty(string propertyName) {
        DependencyProperty dependencyProperty = GetDependencyProperty(propertyName);
        PropertyInfo clrProperty = GetClrProperty(propertyName);
        TerminalEmulator first = new() { AutoStart = false };
        TerminalEmulator second = new() { AutoStart = false };
        Assert.That(dependencyProperty.GetMetadata(typeof(TerminalEmulator)).DefaultValue, Is.True, propertyName);
        Assert.That(first.ReadLocalValue(dependencyProperty), Is.EqualTo(DependencyProperty.UnsetValue));
        Assert.That(clrProperty.GetValue(first), Is.True, propertyName);
        Assert.That(clrProperty.GetValue(second), Is.True, propertyName);

        clrProperty.SetValue(first, false);
        Assert.That(clrProperty.GetValue(first), Is.False, propertyName);
        Assert.That(first.GetValue(dependencyProperty), Is.False, propertyName);
        Assert.That(clrProperty.GetValue(second), Is.True, propertyName);

        first.SetValue(dependencyProperty, true);
        Assert.That(clrProperty.GetValue(first), Is.True, propertyName);
        Assert.That(first.GetValue(dependencyProperty), Is.True, propertyName);
        first.ClearValue(dependencyProperty);
        Assert.That(clrProperty.GetValue(first), Is.True, propertyName);
    }

    [Test]
    public void ShortcutOptions_AreSettableInXamlAndSupportBindings() {
        foreach (ShortcutOption option in ShortcutOptions) {
            string xaml = "<controls:TerminalEmulator xmlns:controls=\"clr-namespace:Brows.Windows.Controls;"
                + "assembly=Brows.TTY.Windows\" " + option.PropertyName + "=\"False\" />";
            TerminalEmulator xamlTerminal = (TerminalEmulator)XamlReader.Parse(xaml);
            DependencyProperty dependencyProperty = GetDependencyProperty(option.PropertyName);
            PropertyInfo clrProperty = GetClrProperty(option.PropertyName);
            Assert.That(clrProperty.GetValue(xamlTerminal), Is.False, option.PropertyName);
            Assert.That(xamlTerminal.GetValue(dependencyProperty), Is.False, option.PropertyName);

            BooleanSource source = new() { Value = true };
            TerminalEmulator boundTerminal = new() { AutoStart = false };
            BindingOperations.SetBinding(boundTerminal, dependencyProperty,
                new Binding(nameof(BooleanSource.Value)) { Source = source, Mode = BindingMode.OneWay });
            Assert.That(clrProperty.GetValue(boundTerminal), Is.True, option.PropertyName);
            source.Value = false;
            DrainDispatcher();
            Assert.That(clrProperty.GetValue(boundTerminal), Is.False, option.PropertyName);
        }
    }

    [TestCaseSource(nameof(CopyCases))]
    public void DisabledCopy_PreservesSelectionClipboardAndForwardsKey(string propertyName, Key key,
        ModifierKeys modifiers, string? text) {
        TerminalEmulator terminal = new() { AutoStart = false };
        SetOption(terminal, propertyName, false);
        using HwndSource source = Host(terminal);
        using KeyboardState keyboard = new();
        try {
            TerminalSession session = StartKeyEcho(terminal);
            Field<DispatcherTimer>(terminal, "_timer").Stop();
            Write(session, "\u001b[2J\u001b[Hcopied-text");
            session.Engine.Select(0, 0, 5, 0);
            SetClipboardText("unchanged");
            Assert.That(session.Engine.SelectedText(), Is.EqualTo("copied"));
            keyboard.SetModifiers(modifiers);
            Assert.That(Keyboard.Modifiers, Is.EqualTo(modifiers));

            KeyEventArgs press = RaiseKey(terminal, source, key, Keyboard.PreviewKeyDownEvent);
            HashSet<Key> forwardedKeys = Field<HashSet<Key>>(terminal, "_forwardedKeys");
            Assert.That(forwardedKeys, Does.Contain(key), propertyName);
            Assert.That(press.Handled, Is.True, propertyName);
            if (text is not null) {
                RaiseText(terminal, text);
            }
            KeyEventArgs release = RaiseKey(terminal, source, key, Keyboard.PreviewKeyUpEvent);
            keyboard.SetModifiers(ModifierKeys.None);
            Assert.That(release.Handled, Is.False, propertyName);
            Assert.That(forwardedKeys, Does.Not.Contain(key), propertyName);
            Assert.That(session.Engine.SelectedText(), Is.EqualTo("copied"), propertyName);
            Assert.That(Clipboard.GetText(), Is.EqualTo("unchanged"), propertyName);

            Write(session, "\r\n");
            SendBarrier(session);
            Assert.That(terminal.LastError, Is.Null);
        }
        finally {
            keyboard.SetModifiers(ModifierKeys.None);
            Pump(terminal.StopAsync());
        }
    }

    [TestCaseSource(nameof(PasteCases))]
    public void DisabledPaste_ForwardsKeyWithoutClipboardPayloadOrBracketedPaste(string propertyName, Key key,
        ModifierKeys modifiers, string? text) {
        TerminalEmulator terminal = new() { AutoStart = false };
        SetOption(terminal, propertyName, false);
        using HwndSource source = Host(terminal);
        using KeyboardState keyboard = new();
        try {
            TerminalSession session = StartKeyEcho(terminal);
            Write(session, "\u001b[?2004h");
            SetClipboardText(PasteText);
            keyboard.SetModifiers(modifiers);
            KeyEventArgs press = RaiseKey(terminal, source, key, Keyboard.PreviewKeyDownEvent);
            HashSet<Key> forwardedKeys = Field<HashSet<Key>>(terminal, "_forwardedKeys");
            Assert.That(forwardedKeys, Does.Contain(key), propertyName);
            Assert.That(press.Handled, Is.True, propertyName);
            if (text is not null) {
                RaiseText(terminal, text);
            }
            KeyEventArgs release = RaiseKey(terminal, source, key, Keyboard.PreviewKeyUpEvent);
            keyboard.SetModifiers(ModifierKeys.None);
            Assert.That(release.Handled, Is.False, propertyName);
            Assert.That(forwardedKeys, Does.Not.Contain(key), propertyName);
            Assert.That(Clipboard.GetText(), Is.EqualTo(PasteText), propertyName);

            SendBarrier(session);
            string input = ReadKeyBytes(session);
            Assert.That(input, Does.Not.Contain(Hex(PasteText)), propertyName);
            Assert.That(input, Does.Not.Contain(PastePrefixBytes), propertyName);
            Assert.That(input, Does.Not.Contain(PasteSuffixBytes), propertyName);
            Assert.That(terminal.LastError, Is.Null);
        }
        finally {
            keyboard.SetModifiers(ModifierKeys.None);
            Pump(terminal.StopAsync());
        }
    }

    [TestCaseSource(nameof(ScrollCases))]
    public void DisabledScroll_LeavesBottomViewportUnchangedAndForwardsKey(string propertyName, Key key,
        ModifierKeys modifiers, string? text) {
        TerminalEmulator terminal = new() { AutoStart = false };
        SetOption(terminal, propertyName, false);
        using HwndSource source = Host(terminal);
        using KeyboardState keyboard = new();
        try {
            TerminalSession session = StartIdlePowerShell(terminal);
            WriteHistory(session);
            WaitFor(() => ReadText(session).IndexOf(HistoryPrefix + "180", StringComparison.Ordinal) >= 0);
            session.Engine.Scroll(0, true);
            int bottomFirstRow = FirstHistoryRow(session);

            keyboard.SetModifiers(modifiers);
            Assert.That(Keyboard.Modifiers, Is.EqualTo(modifiers));
            KeyEventArgs press = RaiseKey(terminal, source, key, Keyboard.PreviewKeyDownEvent);
            HashSet<Key> forwardedKeys = Field<HashSet<Key>>(terminal, "_forwardedKeys");
            Assert.That(forwardedKeys, Does.Contain(key), propertyName);
            Assert.That(press.Handled, Is.True, propertyName);
            KeyEventArgs release = RaiseKey(terminal, source, key, Keyboard.PreviewKeyUpEvent);
            keyboard.SetModifiers(ModifierKeys.None);
            Assert.That(release.Handled, Is.False, propertyName);
            Assert.That(forwardedKeys, Does.Not.Contain(key), propertyName);
            Assert.That(FirstHistoryRow(session), Is.EqualTo(bottomFirstRow), propertyName);
            Assert.That(terminal.LastError, Is.Null);
        }
        finally {
            keyboard.SetModifiers(ModifierKeys.None);
            Pump(terminal.StopAsync());
        }
    }

    [TestCaseSource(nameof(FontCases))]
    public void DisabledFontShortcut_KeepsCurrentSizeAndForwardsKey(string propertyName, Key key,
        ModifierKeys modifiers, string? text) {
        TerminalEmulator terminal = new() { AutoStart = false, FontSize = 18 };
        using HwndSource source = Host(terminal);
        using KeyboardState keyboard = new();
        try {
            TerminalSession session = StartKeyEcho(terminal);
            string setupProperty = propertyName == "PressControlPlusAddToIncreaseFontSize"
                ? "PressControlPlusEqualsToIncreaseFontSize"
                : "PressControlPlusAddToIncreaseFontSize";
            RaiseLocalShortcut(terminal, source, keyboard, FindOption(setupProperty));
            Assert.That(terminal.FontSize, Is.EqualTo(19));
            SetOption(terminal, propertyName, false);

            keyboard.SetModifiers(modifiers);
            KeyEventArgs press = RaiseKey(terminal, source, key, Keyboard.PreviewKeyDownEvent);
            HashSet<Key> forwardedKeys = Field<HashSet<Key>>(terminal, "_forwardedKeys");
            Assert.That(forwardedKeys, Does.Contain(key), propertyName);
            Assert.That(terminal.FontSize, Is.EqualTo(19), propertyName);
            if (text is not null) {
                RaiseText(terminal, text);
            }
            Assert.That(terminal.FontSize, Is.EqualTo(19), propertyName);
            KeyEventArgs release = RaiseKey(terminal, source, key, Keyboard.PreviewKeyUpEvent);
            keyboard.SetModifiers(ModifierKeys.None);
            Assert.That(release.Handled, Is.False, propertyName);
            Assert.That(forwardedKeys, Does.Not.Contain(key), propertyName);
            Assert.That(terminal.FontSize, Is.EqualTo(19), propertyName);
            Assert.That(terminal.LastError, Is.Null);
        }
        finally {
            keyboard.SetModifiers(ModifierKeys.None);
            Pump(terminal.StopAsync());
        }
    }

    [Test]
    public void LocalPress_StaysLocalAfterItsOptionTurnsOffBeforeTextAndRelease() {
        const string optionName = "PressControlPlusVToPaste";
        TerminalEmulator terminal = new() { AutoStart = false };
        using HwndSource source = Host(terminal);
        using KeyboardState keyboard = new();
        try {
            TerminalSession session = StartKeyEcho(terminal);
            Write(session, "\u001b[?2004h");
            SetClipboardText(PasteText);
            keyboard.SetModifiers(ModifierKeys.Control);
            KeyEventArgs press = RaiseKey(terminal, source, Key.V, Keyboard.PreviewKeyDownEvent);
            Assert.That(press.Handled, Is.True);
            Assert.That(Field<HashSet<Key>>(terminal, "_shortcutKeys"), Does.Contain(Key.V));
            Assert.That(Field<HashSet<Key>>(terminal, "_forwardedKeys"), Does.Not.Contain(Key.V));

            SetOption(terminal, optionName, false);
            TextCompositionEventArgs text = RaiseText(terminal, "v");
            KeyEventArgs release = RaiseKey(terminal, source, Key.V, Keyboard.PreviewKeyUpEvent);
            keyboard.SetModifiers(ModifierKeys.None);
            Assert.That(text.Handled, Is.True);
            Assert.That(release.Handled, Is.True);
            Assert.That(Field<HashSet<Key>>(terminal, "_forwardedKeys"), Does.Not.Contain(Key.V));

            SendBarrier(session);
            string expectedInput = PastePrefixBytes + Hex(PasteText) + PasteSuffixBytes + "21";
            Assert.That(ReadKeyBytes(session), Is.EqualTo(expectedInput));
            Assert.That(terminal.LastError, Is.Null);
        }
        finally {
            keyboard.SetModifiers(ModifierKeys.None);
            Pump(terminal.StopAsync());
        }
    }

    [Test]
    public void NewForwardedRepeat_RetiresOldLocalTrackingAndAllowsText() {
        const string optionName = "PressControlPlusVToPaste";
        TerminalEmulator terminal = new() { AutoStart = false };
        using HwndSource source = Host(terminal);
        using KeyboardState keyboard = new();
        try {
            TerminalSession session = StartKeyEcho(terminal);
            Write(session, "\u001b[?2004h");
            SetClipboardText(PasteText);
            keyboard.SetModifiers(ModifierKeys.Control);
            RaiseKey(terminal, source, Key.V, Keyboard.PreviewKeyDownEvent);
            SetOption(terminal, optionName, false);

            KeyEventArgs forwardedRepeat = RaiseKey(terminal, source, Key.V, Keyboard.PreviewKeyDownEvent);
            Assert.That(forwardedRepeat.Handled, Is.True);
            Assert.That(Field<HashSet<Key>>(terminal, "_shortcutKeys"), Does.Not.Contain(Key.V));
            Assert.That(Field<HashSet<Key>>(terminal, "_forwardedKeys"), Does.Contain(Key.V));
            TextCompositionEventArgs text = RaiseText(terminal, "\u00e9");
            KeyEventArgs release = RaiseKey(terminal, source, Key.V, Keyboard.PreviewKeyUpEvent);
            keyboard.SetModifiers(ModifierKeys.None);
            Assert.That(text.Handled, Is.True);
            Assert.That(release.Handled, Is.False);
            Assert.That(Field<HashSet<Key>>(terminal, "_forwardedKeys"), Does.Not.Contain(Key.V));

            SendBarrier(session);
            string expectedInput = PastePrefixBytes + Hex(PasteText) + PasteSuffixBytes
                + ControlVPressBytes + ControlVPressBytes + ControlVReleaseBytes + "21";
            Assert.That(ReadKeyBytes(session), Is.EqualTo(expectedInput),
                "A forwarded Ctrl+V repeat must reach the child after local tracking is retired.");
            Assert.That(terminal.LastError, Is.Null);
        }
        finally {
            keyboard.SetModifiers(ModifierKeys.None);
            Pump(terminal.StopAsync());
        }
    }

    private sealed record ShortcutOption {
        internal string PropertyName { get; }
        internal Key Key { get; }
        internal ModifierKeys Modifiers { get; }
        internal ShortcutGroup Group { get; }
        internal string? Text { get; }

        internal ShortcutOption(string propertyName, Key key, ModifierKeys modifiers,
            ShortcutGroup group, string? text) {
            PropertyName = propertyName;
            Key = key;
            Modifiers = modifiers;
            Group = group;
            Text = text;
        }
    }

    private enum ShortcutGroup {
        Copy,
        Paste,
        Scroll,
        Font
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

        internal void SetModifiers(ModifierKeys modifiers) {
            byte[] state = new byte[256];
            if ((modifiers & ModifierKeys.Control) != 0) {
                state[0x11] = state[0xa2] = 0x80;
            }
            if ((modifiers & ModifierKeys.Shift) != 0) {
                state[0x10] = state[0xa0] = 0x80;
            }
            if ((modifiers & ModifierKeys.Alt) != 0) {
                state[0x12] = state[0xa5] = 0x80;
            }
            Assert.That(SetKeyboardState(state), Is.Not.Zero);
        }

        public void Dispose() {
            Assert.That(SetKeyboardState(_saved), Is.Not.Zero);
        }
    }

    private sealed class BooleanSource : INotifyPropertyChanged {
        public event PropertyChangedEventHandler? PropertyChanged;

        public bool Value {
            get;
            set {
                if (field == value) {
                    return;
                }
                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            }
        }
    }
}
