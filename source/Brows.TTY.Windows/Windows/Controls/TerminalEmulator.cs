using Brows.Ghostty;
using Brows.Win32;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Brows.Windows.Controls;

/// <summary>
/// Hosts an interactive Windows process in a Ghostty terminal. Access this control on its WPF dispatcher.
/// </summary>
public class TerminalEmulator : Control {
    static TerminalEmulator() {
        FocusableProperty.OverrideMetadata(typeof(TerminalEmulator), new FrameworkPropertyMetadata(true));
        FontFamilyProperty.OverrideMetadata(typeof(TerminalEmulator),
            new FrameworkPropertyMetadata(new FontFamily("Consolas"), FontChanged));
        FontSizeProperty.OverrideMetadata(typeof(TerminalEmulator), new FrameworkPropertyMetadata(14.0, FontChanged));
        ClipToBoundsProperty.OverrideMetadata(typeof(TerminalEmulator), new FrameworkPropertyMetadata(true));
    }

    private const double MinimumZoomFontSize = 1.0;
    private const double FontZoomStep = 1.0;

    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly TerminalDrawing _drawing = new();
    private readonly DispatcherTimer _timer;
    private TerminalSession? _session;
    private TerminalSession? _faultedSession;
    private DrawingGroup?[] _rows = Array.Empty<DrawingGroup>();
    private TerminalRow?[] _rowSnapshots = Array.Empty<TerminalRow>();
    private bool[] _blinkingRows = Array.Empty<bool>();
    private NativeFrame _frame;
    private bool _forceFrame = true;
    private bool _fontDirty = true;
    private double? _fontZoomResetSize;
    private bool _settingFontZoomSize;
    private bool _cursorOn = true;
    private DateTime _lastBlink = DateTime.UtcNow;
    private bool _textBlinkOn = true;
    private DateTime _lastTextBlink = DateTime.UtcNow;
    private int _blinkingRowCount;
    private Point? _selectionStart;
    private Point? _selectionEnd;
    private Point? _pendingSelectionEnd;
    private bool _selectionPending;
    private bool _copyMouseUpPending;
    private readonly Dictionary<MouseButton, uint> _applicationMouseButtons = new();
    private readonly List<MouseButton> _applicationMouseButtonOrder = new();
    private readonly HashSet<MouseButton> _localMouseButtons = new();
    private Point _lastMousePosition;
    private bool _hasLastMousePosition;
    private long _pendingWheelDelta;
    private bool? _wheelReportsToApplication;
    private uint _textKey;
    private uint _textAction = 1;
    private uint _textUnshiftedCodepoint;
    private string? _textExpectedPhysicalText;
    private Key _textPressKey;
    private bool _textPressPending;
    private ushort _textModifiers;
    private ushort _textConsumedModifiers;
    private Action? _pendingControlAltPress;
    private DispatcherOperation? _pendingControlAltOperation;
    private TextComposition? _controlAltComposition;
    private TextComposition? _committedComposition;
    private readonly HashSet<Key> _shortcutKeys = new();
    private readonly HashSet<Key> _forwardedKeys = new();
    private readonly Dictionary<Key, uint> _forwardedKeyCodepoints = new();
    private ushort _columns = 80;
    private ushort _lines = 24;
    private uint _cellPixelWidth;
    private uint _cellPixelHeight;

    private static readonly DependencyPropertyKey IsRunningPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsRunning), typeof(bool), typeof(TerminalEmulator), new PropertyMetadata(false));

    private static readonly DependencyPropertyKey TitlePropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(Title), typeof(string), typeof(TerminalEmulator), new PropertyMetadata(""));

    private static readonly DependencyPropertyKey LastErrorPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(LastError), typeof(Exception), typeof(TerminalEmulator), new PropertyMetadata(null));

    private static void FontChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) {
        TerminalEmulator terminal = (TerminalEmulator)sender;
        bool fontSizeChanged = args.Property == FontSizeProperty;
        bool fontSizeChangeIsExternal = fontSizeChanged && !terminal._settingFontZoomSize;
        if (fontSizeChangeIsExternal) {
            terminal._fontZoomResetSize = (double)args.NewValue;
        }
        terminal._fontDirty = true;
        terminal._forceFrame = true;
        terminal.InvalidateMeasure();
        terminal.InvalidateVisual();
    }

    private static bool IsUsableFontSize(double fontSize) {
        bool fontSizeIsFinite = !double.IsNaN(fontSize) && !double.IsInfinity(fontSize);
        bool fontSizeIsPositive = fontSize > 0;
        bool fontSizePassesDependencyPropertyValidation = FontSizeProperty.IsValidValue(fontSize);
        return fontSizeIsFinite && fontSizeIsPositive && fontSizePassesDependencyPropertyValidation;
    }

    private static DependencyProperty RegisterShortcutProperty(string propertyName) {
        return DependencyProperty.Register(propertyName, typeof(bool), typeof(TerminalEmulator),
            new PropertyMetadata(true));
    }

    private static DependencyProperty? GetShortcutProperty(Key key, ModifierKeys modifiers) {
        switch (modifiers) {
            case ModifierKeys.None:
                if (key == Key.Enter) {
                    return PressEnterToCopyProperty;
                }
                break;
            case ModifierKeys.Control:
                switch (key) {
                    case Key.C:
                        return PressControlPlusCToCopyProperty;
                    case Key.Insert:
                        return PressControlPlusInsertToCopyProperty;
                    case Key.V:
                        return PressControlPlusVToPasteProperty;
                    case Key.OemPlus:
                        return PressControlPlusEqualsToIncreaseFontSizeProperty;
                    case Key.Add:
                        return PressControlPlusAddToIncreaseFontSizeProperty;
                    case Key.OemMinus:
                        return PressControlPlusMinusToDecreaseFontSizeProperty;
                    case Key.Subtract:
                        return PressControlPlusSubtractToDecreaseFontSizeProperty;
                    case Key.D0:
                        return PressControlPlus0ToResetFontSizeProperty;
                    case Key.NumPad0:
                        return PressControlPlusNumPad0ToResetFontSizeProperty;
                }
                break;
            case ModifierKeys.Control | ModifierKeys.Shift:
                switch (key) {
                    case Key.C:
                        return PressControlPlusShiftPlusCToCopyProperty;
                    case Key.V:
                        return PressControlPlusShiftPlusVToPasteProperty;
                    case Key.Up:
                        return PressControlPlusShiftPlusUpToScrollProperty;
                    case Key.Down:
                        return PressControlPlusShiftPlusDownToScrollProperty;
                    case Key.PageUp:
                        return PressControlPlusShiftPlusPageUpToScrollOnePageProperty;
                    case Key.PageDown:
                        return PressControlPlusShiftPlusPageDownToScrollOnePageProperty;
                    case Key.Home:
                        return PressControlPlusShiftPlusHomeToScrollToTopProperty;
                    case Key.End:
                        return PressControlPlusShiftPlusEndToScrollToBottomProperty;
                    case Key.OemPlus:
                        return PressControlPlusShiftPlusEqualsToIncreaseFontSizeProperty;
                }
                break;
            case ModifierKeys.Shift:
                if (key == Key.Insert) {
                    return PressShiftPlusInsertToPasteProperty;
                }
                break;
        }
        return null;
    }

    private bool TryHandleFontShortcut(Key key, ModifierKeys mods) {
        bool controlOnly = mods == ModifierKeys.Control;
        bool controlWithShift = mods == (ModifierKeys.Control | ModifierKeys.Shift);
        bool oemPlus = key == Key.OemPlus && (controlOnly || controlWithShift);
        bool numpadPlus = key == Key.Add && controlOnly;
        bool zoomIn = oemPlus || numpadPlus;
        bool zoomOut = (key is Key.OemMinus or Key.Subtract) && controlOnly;
        bool reset = (key is Key.D0 or Key.NumPad0) && controlOnly;
        bool fontShortcut = zoomIn || zoomOut || reset;
        if (!fontShortcut) {
            return false;
        }
        RegisterShortcutKey(key);
        double currentFontSize = FontSize;
        _fontZoomResetSize ??= currentFontSize;
        double targetFontSize = currentFontSize;
        if (reset) {
            targetFontSize = _fontZoomResetSize.Value;
        }
        else if (zoomIn) {
            targetFontSize = currentFontSize + FontZoomStep;
        }
        else if (currentFontSize > MinimumZoomFontSize) {
            targetFontSize = Math.Max(MinimumZoomFontSize, currentFontSize - FontZoomStep);
        }
        bool targetFontSizeIsUsable = IsUsableFontSize(targetFontSize);
        bool targetFontSizeChanged = targetFontSize != currentFontSize;
        bool targetFontSizeCanBeApplied = targetFontSizeIsUsable && targetFontSizeChanged;
        if (!targetFontSizeCanBeApplied) {
            return true;
        }
        _settingFontZoomSize = true;
        try {
            SetCurrentValue(FontSizeProperty, targetFontSize);
        }
        finally {
            _settingFontZoomSize = false;
        }
        return true;
    }

    private static ushort Modifiers() {
        ModifierKeys mods = Keyboard.Modifiers;
        return (ushort)(((mods & ModifierKeys.Shift) != 0 ? 1 : 0)
            | ((mods & ModifierKeys.Control) != 0 ? 2 : 0)
            | ((mods & ModifierKeys.Alt) != 0 ? 4 : 0)
            | ((mods & ModifierKeys.Windows) != 0 ? 8 : 0)
            | (Keyboard.IsKeyToggled(Key.CapsLock) ? 16 : 0)
            | (Keyboard.IsKeyToggled(Key.NumLock) ? 32 : 0));
    }

    /*
     * Keys that never produce text are sent as raw key events so applications
     * receive their identity; text-producing keys flow on to text input.
     */
    private static bool TextlessKey(Key key) {
        return key is Key.Enter or Key.Back or Key.Tab or Key.Escape
            or Key.Left or Key.Right or Key.Up or Key.Down
            or Key.Home or Key.End or Key.Insert or Key.Delete
            or Key.PageUp or Key.PageDown
            or >= Key.F1 and <= Key.F24
            or Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin
            or Key.CapsLock or Key.NumLock or Key.Scroll
            or Key.PrintScreen or Key.Pause or Key.Apps or Key.Clear or Key.Help
            or Key.Sleep
            or Key.VolumeMute or Key.VolumeDown or Key.VolumeUp
            or Key.MediaNextTrack or Key.MediaPreviousTrack or Key.MediaPlayPause or Key.MediaStop
            or Key.BrowserBack or Key.BrowserForward or Key.BrowserRefresh or Key.BrowserStop
            or Key.BrowserSearch or Key.BrowserFavorites or Key.BrowserHome
            or Key.LaunchMail or Key.SelectMedia or Key.LaunchApplication1 or Key.LaunchApplication2;
    }

    private void CancelControlAltPress() {
        _pendingControlAltOperation?.Abort();
        _pendingControlAltOperation = null;
        _pendingControlAltPress = null;
    }

    private void FlushControlAltPress() {
        Action? press = _pendingControlAltPress;
        CancelControlAltPress();
        press?.Invoke();
    }

    private void DeferControlAltPress(Key key, uint action, uint unshiftedCodepoint) {
        TerminalSession? session = _session;
        if (session is null) {
            return;
        }
        uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        ushort modifiers = Modifiers();
        _pendingControlAltPress = () => {
            if (!ReferenceEquals(_session, session)) {
                return;
            }
            Send(engine => {
                session.Key(virtualKey, modifiers, action, unshiftedCodepoint, 0, "");
                _forwardedKeys.Add(key);
                _forwardedKeyCodepoints[key] = unshiftedCodepoint;
            }, true);
        };
        /*
         * WPF processes character/composition input before background work.
         * A text event cancels this fallback; later keys flush it in order.
         */
        _pendingControlAltOperation = Dispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(FlushControlAltPress));
    }

    private void OnTextInputStart(object sender, TextCompositionEventArgs args) {
        bool hasPreeditText = !string.IsNullOrEmpty(args.TextComposition.CompositionText);
        bool isImeComposition = args.TextComposition is FrameworkTextComposition;
        bool compositionIsActive = hasPreeditText || isImeComposition;
        if (compositionIsActive) {
            _committedComposition = args.TextComposition;
        }
        if (_pendingControlAltPress is null) {
            return;
        }
        /*
         * ControlText is a chord translation, not an AltGr text composition.
         */
        if (!string.IsNullOrEmpty(args.ControlText)) {
            return;
        }
        _controlAltComposition = args.TextComposition;
        CancelControlAltPress();
    }

    private void ReportFailure(Exception exception) {
        SetValue(LastErrorPropertyKey, exception);
        RaiseEvent(new TerminalFailedEventArgs(exception));
    }

    private void HandleFailure(TerminalSession session, Exception exception) {
        if (!ReferenceEquals(_session, session)) {
            return;
        }
        if (session.Faulted) {
            FailSession(session, exception);
            return;
        }
        ReportFailure(exception);
    }

    private void FailSession(TerminalSession session, Exception exception) {
        if (ReferenceEquals(_faultedSession, session)) {
            return;
        }
        _faultedSession = session;
        _timer.Stop();
        CancelMouseInteraction(false);
        SetValue(IsRunningPropertyKey, false);
        if (!ReferenceEquals(_session, session)) {
            return;
        }
        ReportFailure(exception);
    }

    private async void OnLoaded(object sender, RoutedEventArgs args) {
        if (DesignerProperties.GetIsInDesignMode(this)) {
            return;
        }
        if (!AutoStart) {
            return;
        }
        try {
            await StartAsync();
        }
        catch (Exception exception) {
            ReportFailure(exception);
        }
    }

    private async void OnUnloaded(object sender, RoutedEventArgs args) {
        try {
            await StopAsync();
        }
        catch (Exception exception) {
            ReportFailure(exception);
        }
    }

    private async Task ObserveExit(TerminalSession session) {
        uint code;
        try {
            code = await session.Completion;
        }
        catch (Exception exception) {
            if (!ReferenceEquals(_session, session)) {
                return;
            }
            _timer.Stop();
            SetValue(IsRunningPropertyKey, false);
            if (!ReferenceEquals(_session, session)) {
                return;
            }
            ReportFailure(exception);
            return;
        }
        if (!ReferenceEquals(_session, session)) {
            return;
        }
        Exception? frameError = null;
        try {
            session.Engine.CompleteOutput();
            RestoreBlinkingText();
            ApplyPendingSelection();
            RefreshFrame(false);
        }
        catch (Exception exception) {
            frameError = exception;
        }
        if (!ReferenceEquals(_session, session)) {
            return;
        }
        /*
         * Collect errors before external callbacks can dispose this session.
         */
        Exception? transportError = null;
        try {
            session.CheckError();
        }
        catch (Exception exception) {
            transportError = exception;
        }
        CancelMouseInteraction(false);
        bool sessionIsCurrentAfterMouseCancellation = ReferenceEquals(_session, session);
        if (!sessionIsCurrentAfterMouseCancellation) {
            return;
        }
        _copyMouseUpPending = false;
        _timer.Stop();
        SetValue(IsRunningPropertyKey, false);
        if (!ReferenceEquals(_session, session)) {
            return;
        }
        InvalidateVisual();
        RaiseEvent(new TerminalExitedEventArgs(code));
        if (frameError is not null) {
            HandleFailure(session, frameError);
        }
        if (transportError is not null) {
            HandleFailure(session, transportError);
        }
    }

    private void ConfigureSize() {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        if (_fontDirty) {
            _drawing.Configure(FontFamily, FontSize, dpi.PixelsPerDip);
            _fontDirty = false;
        }
        bool actualSizeIsBelowOneCell = ActualWidth < _drawing.CellWidth
            || ActualHeight < _drawing.CellHeight;
        if (actualSizeIsBelowOneCell) {
            return;
        }
        ushort columns = (ushort)Math.Max(1, Math.Min(32767, Math.Floor(ActualWidth / _drawing.CellWidth)));
        ushort lines = (ushort)Math.Max(1, Math.Min(32767, Math.Floor(ActualHeight / _drawing.CellHeight)));
        uint cellPixelWidth = (uint)Math.Max(1, Math.Round(_drawing.CellWidth * dpi.DpiScaleX));
        uint cellPixelHeight = (uint)Math.Max(1, Math.Round(_drawing.CellHeight * dpi.DpiScaleY));
        bool gridChanged = columns != _columns || lines != _lines;
        bool cellPixelSizeChanged = cellPixelWidth != _cellPixelWidth || cellPixelHeight != _cellPixelHeight;
        bool sizeUnchanged = !gridChanged && !cellPixelSizeChanged;
        if (sizeUnchanged) {
            return;
        }
        if (gridChanged) {
            CancelLocalSelectionGesture();
            _localMouseButtons.Clear();
            ReleaseMouseCaptureIfIdle();
        }
        _columns = columns;
        _lines = lines;
        _cellPixelWidth = cellPixelWidth;
        _cellPixelHeight = cellPixelHeight;
        if (_session is not null) {
            _session.Resize(columns, lines, cellPixelWidth, cellPixelHeight);
        }
        _forceFrame = true;
    }

    private static bool RowContainsBlinkingText(TerminalRow row) {
        foreach (NativeCell cell in row.Cells) {
            if ((cell.Flags & 8) != 0) {
                return true;
            }
        }
        return false;
    }

    private void RedrawBlinkingRows() {
        for (int index = 0; index < _blinkingRows.Length; index++) {
            if (!_blinkingRows[index]) {
                continue;
            }
            TerminalRow? row = _rowSnapshots[index];
            if (row is not null) {
                _rows[index] = _drawing.DrawRow(row, _textBlinkOn);
            }
        }
    }

    private void RestoreBlinkingText() {
        _lastTextBlink = DateTime.UtcNow;
        if (_textBlinkOn) {
            return;
        }
        _textBlinkOn = true;
        RedrawBlinkingRows();
        InvalidateVisual();
    }

    private void RefreshFrame(bool checkTransport = true) {
        if (_session is null) {
            return;
        }
        ConfigureSize();
        if (checkTransport) {
            _session.CheckError();
        }
        TerminalFrame snapshot = _session.Engine.ReadFrame(_forceFrame);
        _forceFrame = false;
        if (_rows.Length != snapshot.Info.Rows) {
            _rows = new DrawingGroup[snapshot.Info.Rows];
            _rowSnapshots = new TerminalRow[snapshot.Info.Rows];
            _blinkingRows = new bool[snapshot.Info.Rows];
            _blinkingRowCount = 0;
        }
        foreach (TerminalRow row in snapshot.ChangedRows) {
            _rowSnapshots[row.Index] = row;
            bool rowWasBlinking = _blinkingRows[row.Index];
            bool rowIsBlinking = RowContainsBlinkingText(row);
            if (rowWasBlinking != rowIsBlinking) {
                _blinkingRowCount += rowIsBlinking ? 1 : -1;
                _blinkingRows[row.Index] = rowIsBlinking;
            }
            _rows[row.Index] = _drawing.DrawRow(row, _textBlinkOn);
        }
        bool cursorChanged = _frame.CursorX != snapshot.Info.CursorX || _frame.CursorY != snapshot.Info.CursorY
            || _frame.CursorVisible != snapshot.Info.CursorVisible || _frame.CursorStyle != snapshot.Info.CursorStyle
            || _frame.CursorColor != snapshot.Info.CursorColor || _frame.CursorBlink != snapshot.Info.CursorBlink;
        _frame = snapshot.Info;
        if (!string.Equals(Title, snapshot.Title, StringComparison.Ordinal)) {
            SetValue(TitlePropertyKey, snapshot.Title);
        }
        DateTime now = DateTime.UtcNow;
        bool cursorShouldBlink = IsRunning && _frame.CursorVisible != 0 && _frame.CursorBlink != 0;
        bool cursorBlink = cursorShouldBlink && now - _lastBlink >= TimeSpan.FromMilliseconds(500);
        if (cursorBlink) {
            _cursorOn = !_cursorOn;
            _lastBlink = now;
        }
        bool textBlink = IsRunning && _blinkingRowCount != 0
            && now - _lastTextBlink >= TimeSpan.FromMilliseconds(500);
        if (textBlink) {
            _textBlinkOn = !_textBlinkOn;
            _lastTextBlink = now;
            RedrawBlinkingRows();
        }
        bool redraw = snapshot.ChangedRows.Count != 0 || cursorChanged || cursorBlink || textBlink;
        if (redraw) {
            InvalidateVisual();
        }
    }

    private void OnTick(object? sender, EventArgs args) {
        TerminalSession? session = _session;
        if (session is null) {
            return;
        }
        /*
         * Input drops are recoverable. Only a fatal transport error should
         * stop the timer in this path; rendering failures still stop retries.
         */
        try {
            session.CheckError();
        }
        catch (Exception exception) {
            HandleFailure(session, exception);
            return;
        }
        try {
            ConfigureSize();
        }
        catch (Exception exception) {
            if (!ReferenceEquals(_session, session)) {
                return;
            }
            _timer.Stop();
            HandleFailure(session, exception);
            return;
        }
        try {
            ApplyPendingSelection();
        }
        catch (Exception exception) {
            HandleSelectionFailure(session, exception);
        }
        try {
            RefreshFrame(false);
        }
        catch (Exception exception) {
            if (!ReferenceEquals(_session, session)) {
                return;
            }
            _timer.Stop();
            HandleFailure(session, exception);
        }
    }

    private void Send(Action<TerminalEngine> action, bool scrollToBottom = false) {
        TerminalSession? session = _session;
        bool canSend = session is not null && IsRunning;
        if (!canSend) {
            return;
        }
        try {
            if (scrollToBottom) {
                session!.Engine.Scroll(0, true);
            }
            action(session!.Engine);
            session.CheckError();
            _cursorOn = true;
            _lastBlink = DateTime.UtcNow;
        }
        catch (Exception exception) {
            HandleFailure(session!, exception);
        }
    }

    private void ApplyLocal(Action<TerminalEngine> action) {
        TerminalSession? session = _session;
        if (session is null) {
            return;
        }
        try {
            action(session.Engine);
            RefreshFrame();
        }
        catch (Exception exception) {
            HandleFailure(session, exception);
        }
    }

    private void RegisterShortcutKey(Key key) {
        _shortcutKeys.Add(key);
        _textPressKey = key;
        _textPressPending = false;
        _textExpectedPhysicalText = null;
    }

    private bool TryHandleClipboardShortcut(Key key, ModifierKeys modifiers) {
        bool enterCopy = key == Key.Enter && modifiers == ModifierKeys.None;
        if (enterCopy) {
            bool copyHandled = _shortcutKeys.Contains(key) || TryCopySelection();
            if (!copyHandled) {
                return false;
            }
            RegisterShortcutKey(key);
            return true;
        }
        bool controlCopy = key == Key.C && modifiers == ModifierKeys.Control;
        if (controlCopy) {
            bool copyHandled = _shortcutKeys.Contains(key) || TryCopySelection();
            if (!copyHandled) {
                return false;
            }
            RegisterShortcutKey(key);
            return true;
        }
        bool copy = (key == Key.C && modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            || (key == Key.Insert && modifiers == ModifierKeys.Control);
        if (copy) {
            RegisterShortcutKey(key);
            CopySelection();
            return true;
        }
        bool paste = (key == Key.V && (modifiers == ModifierKeys.Control
                || modifiers == (ModifierKeys.Control | ModifierKeys.Shift)))
            || (key == Key.Insert && modifiers == ModifierKeys.Shift);
        if (paste) {
            RegisterShortcutKey(key);
            PasteClipboard();
            return true;
        }
        return false;
    }

    private bool TryHandleScrollShortcut(Key key, ModifierKeys modifiers) {
        if (modifiers != (ModifierKeys.Control | ModifierKeys.Shift)) {
            return false;
        }

        bool scrollUp = key == Key.Up;
        bool scrollDown = key == Key.Down;
        bool pageUp = key == Key.PageUp;
        bool pageDown = key == Key.PageDown;
        bool top = key == Key.Home;
        bool bottom = key == Key.End;
        bool usesWheelRows = scrollUp || scrollDown;
        bool usesViewportPages = pageUp || pageDown;
        bool isScrollShortcut = usesWheelRows || usesViewportPages || top || bottom;
        if (!isScrollShortcut) {
            return false;
        }

        RegisterShortcutKey(key);
        FlushPendingSelection();
        ClearSelectionDrag();
        if (_session is null) {
            return true;
        }

        if (usesWheelRows) {
            int lines = SystemParameters.WheelScrollLines;
            if (lines == -1) {
                lines = _lines;
            }
            if (lines < 0) {
                lines = 0;
            }
            int delta = scrollUp ? -lines : lines;
            ApplyLocal(engine => engine.Scroll(delta));
            return true;
        }
        if (usesViewportPages) {
            int delta = pageUp ? -_lines : _lines;
            ApplyLocal(engine => engine.Scroll(delta));
            return true;
        }
        if (top) {
            ApplyLocal(engine => engine.ScrollToTop());
            return true;
        }
        ApplyLocal(engine => engine.Scroll(0, true));
        return true;
    }

    private bool TryCopySelection() {
        TerminalSession? session = _session;
        if (session is null) {
            return false;
        }
        try {
            ApplyPendingSelection();
            string text = session.Engine.SelectedText();
            if (text.Length == 0) {
                return false;
            }
            Clipboard.SetText(text);
            session.Engine.Select(0, 0, 0, 0, true);
            CancelLocalSelectionGesture();
            RefreshFrame();
        }
        catch (Exception exception) {
            HandleSelectionFailure(session, exception);
            return false;
        }
        return true;
    }

    private void ClearSelectionDrag() {
        TerminalSession? session = _session;
        _selectionStart = null;
        _selectionEnd = null;
        _pendingSelectionEnd = null;
        _selectionPending = false;
        if (session is null) {
            return;
        }
        try {
            session.Engine.CancelSelection();
        }
        catch (ObjectDisposedException) {
        }
    }

    private void CancelLocalSelectionGesture() {
        ClearSelectionDrag();
        _localMouseButtons.Remove(MouseButton.Left);
        ReleaseMouseCaptureIfIdle();
    }

    private void HandleSelectionFailure(TerminalSession session, Exception exception) {
        if (!ReferenceEquals(_session, session)) {
            return;
        }
        CancelLocalSelectionGesture();
        if (session.Faulted) {
            HandleFailure(session, exception);
            return;
        }
        ReportFailure(exception);
    }

    private void QueueSelection(Point endpoint) {
        if (_selectionStart is null) {
            return;
        }
        Point? latestEndpoint = _selectionPending ? _pendingSelectionEnd : _selectionEnd;
        if (latestEndpoint == endpoint) {
            return;
        }
        if (_selectionEnd == endpoint) {
            _pendingSelectionEnd = null;
            _selectionPending = false;
            return;
        }
        _pendingSelectionEnd = endpoint;
        _selectionPending = true;
    }

    private bool ApplyPendingSelection() {
        if (!_selectionPending) {
            return false;
        }
        Point? start = _selectionStart;
        Point? endpoint = _pendingSelectionEnd;
        _pendingSelectionEnd = null;
        _selectionPending = false;
        TerminalSession? session = _session;
        if (session is null) {
            return false;
        }
        if (start is null) {
            return false;
        }
        if (endpoint is null) {
            return false;
        }
        Point selectionEnd = endpoint.Value;
        bool selectionWasApplied = session.Engine.UpdateSelection((ushort)selectionEnd.X, (ushort)selectionEnd.Y);
        if (!selectionWasApplied) {
            CancelLocalSelectionGesture();
            return true;
        }
        _selectionEnd = endpoint;
        return true;
    }

    private void FlushPendingSelection() {
        TerminalSession? session = _session;
        if (session is null) {
            return;
        }
        try {
            bool selectionApplied = ApplyPendingSelection();
            if (!selectionApplied) {
                return;
            }
            bool sessionIsCurrent = ReferenceEquals(_session, session);
            if (!sessionIsCurrent) {
                return;
            }
            RefreshFrame();
        }
        catch (Exception exception) {
            HandleSelectionFailure(session, exception);
        }
    }

    private Point CellAt(Point point) {
        return new Point(Math.Max(0, Math.Min(_columns - 1, Math.Floor(point.X / _drawing.CellWidth))),
            Math.Max(0, Math.Min(_lines - 1, Math.Floor(point.Y / _drawing.CellHeight))));
    }

    private static uint MouseButtonCode(MouseButton button) {
        switch (button) {
            case MouseButton.Left:
                return 1;
            case MouseButton.Right:
                return 2;
            case MouseButton.Middle:
                return 3;
            case MouseButton.XButton1:
                return 8;
            case MouseButton.XButton2:
                return 9;
            default:
                return 0;
        }
    }

    private uint CurrentApplicationMouseButton() {
        for (int index = _applicationMouseButtonOrder.Count - 1; index >= 0; index--) {
            MouseButton button = _applicationMouseButtonOrder[index];
            bool buttonIsStillPressed = _applicationMouseButtons.TryGetValue(button, out uint code);
            if (buttonIsStillPressed) {
                return code;
            }
        }
        return 0;
    }

    private void RememberMousePosition(Point point) {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        _lastMousePosition = new Point(point.X * dpi.DpiScaleX, point.Y * dpi.DpiScaleY);
        _hasLastMousePosition = true;
    }

    private void SendMouse(TerminalSession session, Point point, uint action, uint button, bool anyButtonPressed) {
        RememberMousePosition(point);
        SendMouse(session, action, button, anyButtonPressed);
    }

    private void SendMouse(TerminalSession session, uint action, uint button, bool anyButtonPressed) {
        bool sessionIsCurrentAndRunning = ReferenceEquals(_session, session) && IsRunning;
        if (!sessionIsCurrentAndRunning) {
            return;
        }
        if (!_hasLastMousePosition) {
            RememberMousePosition(new Point(0, 0));
        }
        try {
            session.Engine.Mouse(action, button, Modifiers(), (float)_lastMousePosition.X,
                (float)_lastMousePosition.Y, anyButtonPressed);
            session.CheckError();
        }
        catch (Exception exception) {
            HandleFailure(session, exception);
        }
    }

    private long AccumulateWheelDelta(int delta, bool reportsToApplication) {
        bool wheelRouteChanged = _wheelReportsToApplication != reportsToApplication;
        if (wheelRouteChanged) {
            _pendingWheelDelta = 0;
            _wheelReportsToApplication = reportsToApplication;
        }
        _pendingWheelDelta += delta;
        long detents = _pendingWheelDelta / 120;
        _pendingWheelDelta -= detents * 120;
        return detents;
    }

    private static int WheelScrollRows(long detents, int configuredRows, int viewportRows) {
        long rowsPerDetent = configuredRows == -1 ? viewportRows : Math.Max(0, configuredRows);
        long rowDelta = -detents * rowsPerDetent;
        if (rowDelta > int.MaxValue) {
            return int.MaxValue;
        }
        if (rowDelta < int.MinValue) {
            return int.MinValue;
        }
        return (int)rowDelta;
    }

    private void ReleaseMouseCaptureIfIdle() {
        bool noPressedButtons = _applicationMouseButtons.Count == 0 && _localMouseButtons.Count == 0;
        bool idleControlOwnsMouseCapture = noPressedButtons && IsMouseCaptured;
        if (idleControlOwnsMouseCapture) {
            ReleaseMouseCapture();
        }
    }

    private void CancelMouseInteraction(bool sendApplicationReleases = true) {
        TerminalSession? session = _session;
        bool canSendReleases = sendApplicationReleases && session is not null && IsRunning;
        MouseButton[] applicationButtons = _applicationMouseButtonOrder.ToArray();
        _applicationMouseButtons.Clear();
        _applicationMouseButtonOrder.Clear();
        _localMouseButtons.Clear();
        _pendingWheelDelta = 0;
        _wheelReportsToApplication = null;
        ClearSelectionDrag();
        if (canSendReleases) {
            for (int index = applicationButtons.Length - 1; index >= 0; index--) {
                bool sessionIsCurrentAndRunning = ReferenceEquals(_session, session) && IsRunning;
                if (!sessionIsCurrentAndRunning) {
                    return;
                }
                uint button = MouseButtonCode(applicationButtons[index]);
                bool anotherButtonRemainsPressed = index > 0;
                SendMouse(session!, 1, button, anotherButtonRemainsPressed);
                bool sessionChangedWhileReleasing = !ReferenceEquals(_session, session);
                if (sessionChangedWhileReleasing) {
                    return;
                }
            }
        }
        bool sessionIsUnchanged = session is null || ReferenceEquals(_session, session);
        bool unchangedSessionOwnsMouseCapture = sessionIsUnchanged && IsMouseCaptured;
        if (unchangedSessionOwnsMouseCapture) {
            ReleaseMouseCapture();
        }
    }

    private void HandleMouseCaptureLost(Point point) {
        RememberMousePosition(point);
        CancelMouseInteraction();
    }

    private void HandleMouseDown(MouseButtonEventArgs args, Point position, bool captureMouse) {
        TerminalSession? session = _session;
        if (session is null) {
            return;
        }
        RememberMousePosition(position);
        bool exitedApplicationGestureIsStale = !IsRunning && _applicationMouseButtons.Count != 0;
        if (exitedApplicationGestureIsStale) {
            _applicationMouseButtons.Clear();
            _applicationMouseButtonOrder.Clear();
            ReleaseMouseCaptureIfIdle();
        }
        if (args.ChangedButton == MouseButton.Right) {
            _copyMouseUpPending = false;
            bool copyWasHandled = TryCopySelection();
            if (copyWasHandled) {
                bool sessionIsCurrent = ReferenceEquals(_session, session);
                if (sessionIsCurrent) {
                    _copyMouseUpPending = true;
                }
                args.Handled = true;
                return;
            }
        }
        bool sessionIsCurrentAfterCopy = ReferenceEquals(_session, session);
        if (!sessionIsCurrentAfterCopy) {
            args.Handled = true;
            return;
        }
        bool noMouseGestureIsActive = _applicationMouseButtons.Count == 0 && _localMouseButtons.Count == 0;
        if (noMouseGestureIsActive) {
            FlushPendingSelection();
            bool sessionIsCurrent = ReferenceEquals(_session, session);
            if (!sessionIsCurrent) {
                args.Handled = true;
                return;
            }
            ClearSelectionDrag();
        }
        uint button = MouseButtonCode(args.ChangedButton);
        bool unsupportedOrAlreadyPressedButton = button == 0
            || _applicationMouseButtons.ContainsKey(args.ChangedButton)
            || _localMouseButtons.Contains(args.ChangedButton);
        if (unsupportedOrAlreadyPressedButton) {
            args.Handled = true;
            return;
        }
        bool reportsToApplication = ReportsMouse;
        if (reportsToApplication) {
            _applicationMouseButtons.Add(args.ChangedButton, button);
            _applicationMouseButtonOrder.Remove(args.ChangedButton);
            _applicationMouseButtonOrder.Add(args.ChangedButton);
            SendMouse(session, position, 0, button, true);
            bool sessionChangedOrStopped = !ReferenceEquals(_session, session) || !IsRunning;
            if (sessionChangedOrStopped) {
                args.Handled = true;
                return;
            }
        }
        else {
            _localMouseButtons.Add(args.ChangedButton);
            if (args.ChangedButton == MouseButton.Left) {
                Point cell = CellAt(position);
                _selectionStart = cell;
                _selectionEnd = cell;
                try {
                    session.Engine.BeginSelection((ushort)cell.X, (ushort)cell.Y);
                    RefreshFrame();
                }
                catch (Exception exception) {
                    CancelLocalSelectionGesture();
                    HandleSelectionFailure(session, exception);
                }
            }
        }
        bool sessionChanged = !ReferenceEquals(_session, session);
        if (sessionChanged) {
            args.Handled = true;
            return;
        }
        bool mouseGestureIsActive = _applicationMouseButtons.Count != 0 || _localMouseButtons.Count != 0;
        bool captureMouseForGesture = captureMouse && mouseGestureIsActive;
        if (captureMouseForGesture) {
            bool captureSucceeded = CaptureMouse();
            if (!captureSucceeded) {
                CancelMouseInteraction();
            }
        }
        args.Handled = true;
    }

    private void HandleMouseMove(Point position) {
        TerminalSession? session = _session;
        if (session is null) {
            return;
        }
        RememberMousePosition(position);
        bool exitedApplicationGestureIsStale = !IsRunning && _applicationMouseButtons.Count != 0;
        if (exitedApplicationGestureIsStale) {
            _applicationMouseButtons.Clear();
            _applicationMouseButtonOrder.Clear();
            ReleaseMouseCaptureIfIdle();
        }
        if (_applicationMouseButtons.Count != 0) {
            uint button = CurrentApplicationMouseButton();
            bool anyButtonPressed = _applicationMouseButtons.Count != 0 || _localMouseButtons.Count != 0;
            SendMouse(session, position, 2, button, anyButtonPressed);
            bool sessionIsCurrent = ReferenceEquals(_session, session);
            if (!sessionIsCurrent) {
                return;
            }
        }
        bool localSelectionIsActive = _selectionStart.HasValue && _localMouseButtons.Contains(MouseButton.Left);
        if (localSelectionIsActive) {
            Point point = CellAt(position);
            QueueSelection(point);
        }
        bool noMouseGestureIsActive = _applicationMouseButtons.Count == 0 && _localMouseButtons.Count == 0;
        bool reportsUnownedHoverMotion = noMouseGestureIsActive && ReportsMouse;
        if (reportsUnownedHoverMotion) {
            SendMouse(session, position, 2, 0, false);
        }
    }

    private void HandleMouseUp(MouseButtonEventArgs args, Point position) {
        TerminalSession? session = _session;
        bool copiedWithRightButton = args.ChangedButton == MouseButton.Right && _copyMouseUpPending;
        if (copiedWithRightButton) {
            _copyMouseUpPending = false;
            args.Handled = true;
            return;
        }
        RememberMousePosition(position);
        uint applicationButton = 0;
        bool applicationButtonWasPressed = _applicationMouseButtons.TryGetValue(args.ChangedButton,
            out applicationButton);
        if (applicationButtonWasPressed) {
            _applicationMouseButtons.Remove(args.ChangedButton);
            _applicationMouseButtonOrder.Remove(args.ChangedButton);
        }
        bool localButtonWasPressed = _localMouseButtons.Remove(args.ChangedButton);
        bool localLeftButtonWasReleased = localButtonWasPressed && args.ChangedButton == MouseButton.Left;
        if (localLeftButtonWasReleased) {
            FlushPendingSelection();
            bool sessionIsCurrent = ReferenceEquals(_session, session);
            if (!sessionIsCurrent) {
                args.Handled = true;
                return;
            }
            CancelLocalSelectionGesture();
        }
        if (applicationButtonWasPressed) {
            bool anotherButtonRemainsPressed = _applicationMouseButtons.Count != 0 || _localMouseButtons.Count != 0;
            if (session is not null) {
                SendMouse(session, position, 1, applicationButton, anotherButtonRemainsPressed);
            }
        }
        bool sessionChanged = !ReferenceEquals(_session, session);
        if (sessionChanged) {
            args.Handled = true;
            return;
        }
        ReleaseMouseCaptureIfIdle();
        args.Handled = true;
    }

    private void HandleMouseWheel(MouseWheelEventArgs args, Point position) {
        TerminalSession? session = _session;
        if (session is null) {
            return;
        }
        RememberMousePosition(position);
        bool reportsToApplication = ReportsMouse;
        long detents = AccumulateWheelDelta(args.Delta, reportsToApplication);
        if (reportsToApplication) {
            uint button = detents > 0 ? 4u : 5u;
            long wheelEvents = detents < 0 ? -detents : detents;
            bool anyButtonPressed = _applicationMouseButtons.Count != 0 || _localMouseButtons.Count != 0;
            for (long index = 0; index < wheelEvents; index++) {
                bool sessionIsCurrentAndRunning = ReferenceEquals(_session, session) && IsRunning;
                if (!sessionIsCurrentAndRunning) {
                    break;
                }
                SendMouse(session, position, 0, button, anyButtonPressed);
            }
        }
        else if (detents != 0) {
            int rows = WheelScrollRows(detents, SystemParameters.WheelScrollLines, _lines);
            if (rows != 0) {
                FlushPendingSelection();
                bool sessionIsCurrent = ReferenceEquals(_session, session);
                if (!sessionIsCurrent) {
                    args.Handled = true;
                    return;
                }
                CancelLocalSelectionGesture();
                bool sessionIsCurrentAfterSelectionCancellation = ReferenceEquals(_session, session);
                if (!sessionIsCurrentAfterSelectionCancellation) {
                    args.Handled = true;
                    return;
                }
                ApplyLocal(engine => engine.Scroll(rows));
            }
        }
        args.Handled = true;
    }

    private bool ReportsMouse {
        get {
            bool mouseTrackingIsEnabled = _frame.MouseTracking != 0;
            bool shiftTemporarilyDisablesMouseTracking = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            return IsRunning && mouseTrackingIsEnabled && !shiftTemporarilyDisablesMouseTracking;
        }
    }

    private void HandleKeyboardFocusLost() {
        CancelMouseInteraction();
        CancelControlAltPress();
        _controlAltComposition = null;
        _committedComposition = null;
        _textPressPending = false;
        _textExpectedPhysicalText = null;
        _shortcutKeys.Clear();
        /*
         * The release events of held keys go to the window that takes the focus,
         * so release them here to keep applications tracking keys consistent.
         */
        ReleaseForwardedKeys();
        Send(engine => engine.Focus(false));
        InvalidateVisual();
    }

    private void ReleaseForwardedKeys() {
        Key[] held = _forwardedKeys.ToArray();
        _forwardedKeys.Clear();
        Dictionary<Key, uint> codepoints = new(_forwardedKeyCodepoints);
        _forwardedKeyCodepoints.Clear();
        foreach (Key key in held) {
            uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
            codepoints.TryGetValue(key, out uint unshiftedCodepoint);
            Send(engine => _session!.Key(virtualKey, Modifiers(), 0, unshiftedCodepoint, 0, ""));
        }
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext context) {
        base.OnRender(context);
        if (_fontDirty) {
            try {
                DpiScale dpi = VisualTreeHelper.GetDpi(this);
                _drawing.Configure(FontFamily, FontSize, dpi.PixelsPerDip);
                /*
                 * Snapshots own their cell/text buffers, so stopped sessions can
                 * rebuild glyphs without reading or resizing native state.
                 */
                foreach (TerminalRow? row in _rowSnapshots) {
                    if (row is not null) {
                        _rows[row.Index] = _drawing.DrawRow(row, _textBlinkOn);
                    }
                }
                _fontDirty = false;
            }
            catch (Exception exception) {
                ReportFailure(exception);
                return;
            }
        }
        context.DrawRectangle(_drawing.Brush(_frame.Background == 0 ? 0xff1e1e1e : _frame.Background), null,
            new Rect(RenderSize));
        foreach (DrawingGroup? row in _rows) {
            if (row is not null) {
                context.DrawDrawing(row);
            }
        }
        bool showCursor = IsRunning && _frame.CursorVisible != 0 && (_frame.CursorBlink == 0 || _cursorOn);
        if (!showCursor) {
            return;
        }
        uint cursorY = _frame.CursorY;
        bool cursorRowIsAvailable = cursorY < (uint)_rowSnapshots.Length;
        TerminalRow? cursorRow = cursorRowIsAvailable ? _rowSnapshots[(int)cursorY] : null;
        Rect cursor = _drawing.CursorBounds(_frame, cursorRow);
        if (_frame.CursorStyle == 0) {
            cursor.Width = 2;
        }
        if (_frame.CursorStyle == 2) {
            cursor.Y += cursor.Height - 2;
            cursor.Height = 2;
        }
        Brush brush = _drawing.Brush(_frame.CursorColor);
        bool hollow = !IsKeyboardFocusWithin || _frame.CursorStyle == 3;
        context.PushOpacity(hollow ? 0.8 : 0.55);
        context.DrawRectangle(hollow ? null : brush, hollow ? new Pen(brush, 1) : null, cursor);
        context.Pop();
    }

    /// <inheritdoc />
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) {
        base.OnDpiChanged(oldDpi, newDpi);
        _fontDirty = true;
        _forceFrame = true;
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnPreviewKeyDown(KeyEventArgs args) {
        base.OnPreviewKeyDown(args);
        FlushControlAltPress();
        Key key = args.Key == Key.System ? args.SystemKey : args.Key;
        ModifierKeys mods = Keyboard.Modifiers;
        bool windowShortcut = mods == ModifierKeys.Alt && key is Key.F4 or Key.Space;
        if (windowShortcut) {
            /*
             * Leave window close and the system menu to the host.
             */
            return;
        }
        DependencyProperty? shortcutProperty = GetShortcutProperty(key, mods);
        bool shortcutEnabled = shortcutProperty is not null && (bool)GetValue(shortcutProperty);
        if (shortcutEnabled) {
            bool shortcutHandled = TryHandleScrollShortcut(key, mods)
                || TryHandleFontShortcut(key, mods)
                || TryHandleClipboardShortcut(key, mods);
            if (shortcutHandled) {
                args.Handled = true;
                return;
            }
        }
        _shortcutKeys.Remove(key);
        _textKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        _textPressKey = key;
        _textModifiers = Modifiers();
        _textAction = args.IsRepeat ? 2u : 1u;
        _textPressPending = true;
        TerminalSession? session = _session;
        _textUnshiftedCodepoint = session is null ? 0 : session.UnshiftedCodepoint(_textKey);
        _textExpectedPhysicalText = session is null
            ? null
            : session.ExpectedPhysicalText(_textKey, _textModifiers);
        _textConsumedModifiers = session is null
            ? (ushort)0
            : session.ConsumedModifiers(_textKey, _textModifiers);
        bool special = TextlessKey(key);
        bool control = (mods & ModifierKeys.Control) != 0 && (mods & ModifierKeys.Alt) == 0;
        bool controlAlt = (mods & (ModifierKeys.Control | ModifierKeys.Alt))
            == (ModifierKeys.Control | ModifierKeys.Alt) && !special;
        if (controlAlt) {
            DeferControlAltPress(key, _textAction, _textUnshiftedCodepoint);
            return;
        }
        bool keyRequiresImmediateInput = special || control;
        if (keyRequiresImmediateInput) {
            Send(engine => {
                session!.Key(_textKey, _textModifiers, _textAction, _textUnshiftedCodepoint, 0, "");
                _forwardedKeys.Add(key);
                _forwardedKeyCodepoints[key] = _textUnshiftedCodepoint;
            }, true);
            args.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnPreviewKeyUp(KeyEventArgs args) {
        base.OnPreviewKeyUp(args);
        FlushControlAltPress();
        Key key = args.Key == Key.System ? args.SystemKey : args.Key;
        bool pendingTextMatchesReleasedKey = _textPressPending && key == _textPressKey;
        if (pendingTextMatchesReleasedKey) {
            _textPressPending = false;
            _textExpectedPhysicalText = null;
        }
        /*
         * A held key can move between local shortcuts and terminal input when
         * modifiers change during autorepeat. Retire both kinds of state.
         */
        if (_shortcutKeys.Remove(key)) {
            args.Handled = true;
        }
        /*
         * Only keys whose press reached the terminal are released, so a release
         * from a key pressed outside the control is never sent alone.
         */
        if (!_forwardedKeys.Remove(key)) {
            return;
        }
        _forwardedKeyCodepoints.TryGetValue(key, out uint unshiftedCodepoint);
        _forwardedKeyCodepoints.Remove(key);
        uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        Send(engine => _session!.Key(virtualKey, Modifiers(), 0, unshiftedCodepoint, 0, ""));
    }

    /// <inheritdoc />
    protected override void OnTextInput(TextCompositionEventArgs args) {
        base.OnTextInput(args);
        bool systemMenu = args.SystemText == " " && Keyboard.Modifiers == ModifierKeys.Alt;
        if (systemMenu) {
            return;
        }
        string text = string.IsNullOrEmpty(args.Text) ? args.SystemText : args.Text;
        if (string.IsNullOrEmpty(text)) {
            return;
        }
        bool shortcutTextIsLocal = _shortcutKeys.Contains(_textPressKey);
        bool isEnterShortcut = _textPressKey == Key.Enter;
        bool isNewlineText = text is "\r" or "\n" or "\r\n";
        bool suppressEnterText = shortcutTextIsLocal && isEnterShortcut && isNewlineText;
        bool suppressOtherShortcutText = shortcutTextIsLocal && !isEnterShortcut;
        bool suppressShortcutText = suppressEnterText || suppressOtherShortcutText;
        if (suppressShortcutText) {
            if (ReferenceEquals(_committedComposition, args.TextComposition)) {
                _committedComposition = null;
            }
            args.Handled = true;
            return;
        }
        bool textHasPhysicalKey = _textPressPending && _textPressKey != Key.None;
        bool textMatchesPhysicalKey = false;
        bool textCanBeMatchedToPhysicalKey = textHasPhysicalKey && !TextlessKey(_textPressKey)
            && _textExpectedPhysicalText is not null;
        if (textCanBeMatchedToPhysicalKey) {
            textMatchesPhysicalKey = string.Equals(_textExpectedPhysicalText, text, StringComparison.Ordinal);
        }
        bool committedComposition = args.TextComposition is FrameworkTextComposition
            || ReferenceEquals(_committedComposition, args.TextComposition)
            || (textHasPhysicalKey && !TextlessKey(_textPressKey) && !textMatchesPhysicalKey);
        bool controlAltText = _pendingControlAltPress is not null
            || ReferenceEquals(_controlAltComposition, args.TextComposition);
        CancelControlAltPress();
        _controlAltComposition = null;
        if (ReferenceEquals(_committedComposition, args.TextComposition)) {
            _committedComposition = null;
        }
        bool pureTextInput = committedComposition || controlAltText || !textHasPhysicalKey;
        _textPressPending = false;
        _textExpectedPhysicalText = null;
        Send(engine => {
            if (pureTextInput) {
                /*
                 * Composition text has no single physical key identity. Encode
                 * the committed text directly so Kitty does not substitute one.
                 */
                engine.Key(TerminalKey.Unidentified, (ushort)(Modifiers() & ~6), 1, text);
                return;
            }
            TerminalSession session = _session!;
            session.Key(_textKey, _textModifiers, _textAction, _textUnshiftedCodepoint,
                _textConsumedModifiers, text);
            _forwardedKeys.Add(_textPressKey);
            _forwardedKeyCodepoints[_textPressKey] = _textUnshiftedCodepoint;
        }, true);
        args.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs args) {
        base.OnGotKeyboardFocus(args);
        Send(engine => engine.Focus(true));
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs args) {
        base.OnLostKeyboardFocus(args);
        HandleKeyboardFocusLost();
    }

    /// <inheritdoc />
    protected override void OnMouseDown(MouseButtonEventArgs args) {
        base.OnMouseDown(args);
        Focus();
        HandleMouseDown(args, args.GetPosition(this), true);
    }

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs args) {
        base.OnMouseMove(args);
        HandleMouseMove(args.GetPosition(this));
    }

    /// <inheritdoc />
    protected override void OnMouseUp(MouseButtonEventArgs args) {
        base.OnMouseUp(args);
        HandleMouseUp(args, args.GetPosition(this));
    }

    /// <inheritdoc />
    protected override void OnMouseWheel(MouseWheelEventArgs args) {
        base.OnMouseWheel(args);
        HandleMouseWheel(args, args.GetPosition(this));
    }

    /// <inheritdoc />
    protected override void OnLostMouseCapture(MouseEventArgs args) {
        base.OnLostMouseCapture(args);
        HandleMouseCaptureLost(args.GetPosition(this));
    }

    /// <summary>
    /// Identifies the command line used for the next process start.
    /// </summary>
    public static readonly DependencyProperty CommandLineProperty = DependencyProperty.Register(nameof(CommandLine),
        typeof(string), typeof(TerminalEmulator),
        new PropertyMetadata("\"" + Environment.GetEnvironmentVariable("ComSpec") + "\""));

    /// <summary>
    /// Identifies the working directory used for the next process start.
    /// </summary>
    public static readonly DependencyProperty WorkingDirectoryProperty = DependencyProperty.Register(
        nameof(WorkingDirectory), typeof(string), typeof(TerminalEmulator), new PropertyMetadata(null));

    /// <summary>
    /// Identifies whether loading the control starts its process automatically.
    /// </summary>
    public static readonly DependencyProperty AutoStartProperty = DependencyProperty.Register(nameof(AutoStart),
        typeof(bool), typeof(TerminalEmulator), new PropertyMetadata(true));

    /// <summary>
    /// Identifies whether Enter copies selected text. The default is true.
    /// </summary>
    public static readonly DependencyProperty PressEnterToCopyProperty =
        RegisterShortcutProperty(nameof(PressEnterToCopy));

    /// <summary>
    /// Identifies whether Ctrl+C copies selected text. The default is true.
    /// </summary>
    public static readonly DependencyProperty PressControlPlusCToCopyProperty =
        RegisterShortcutProperty(nameof(PressControlPlusCToCopy));

    /// <summary>
    /// Identifies whether Ctrl+Shift+C copies selected text. The default is true.
    /// </summary>
    public static readonly DependencyProperty PressControlPlusShiftPlusCToCopyProperty =
        RegisterShortcutProperty(nameof(PressControlPlusShiftPlusCToCopy));

    /// <summary>
    /// Identifies whether Ctrl+Insert copies selected text. The default is true.
    /// </summary>
    public static readonly DependencyProperty PressControlPlusInsertToCopyProperty =
        RegisterShortcutProperty(nameof(PressControlPlusInsertToCopy));

    /// <summary>
    /// Identifies whether Ctrl+V pastes clipboard text. The default is true.
    /// </summary>
    public static readonly DependencyProperty PressControlPlusVToPasteProperty =
        RegisterShortcutProperty(nameof(PressControlPlusVToPaste));

    /// <summary>
    /// Identifies whether Ctrl+Shift+V pastes clipboard text. The default is true.
    /// </summary>
    public static readonly DependencyProperty PressControlPlusShiftPlusVToPasteProperty =
        RegisterShortcutProperty(nameof(PressControlPlusShiftPlusVToPaste));

    /// <summary>
    /// Identifies whether Shift+Insert pastes clipboard text. The default is true.
    /// </summary>
    public static readonly DependencyProperty PressShiftPlusInsertToPasteProperty =
        RegisterShortcutProperty(nameof(PressShiftPlusInsertToPaste));

    /// <summary>
    /// Identifies whether Ctrl+Shift+Up scrolls history. The default is true.
    /// </summary>
    public static readonly DependencyProperty PressControlPlusShiftPlusUpToScrollProperty =
        RegisterShortcutProperty(nameof(PressControlPlusShiftPlusUpToScroll));

    /// <summary>
    /// Identifies whether Ctrl+Shift+Down scrolls history. The default is true.
    /// </summary>
    public static readonly DependencyProperty PressControlPlusShiftPlusDownToScrollProperty =
        RegisterShortcutProperty(nameof(PressControlPlusShiftPlusDownToScroll));

    /// <summary>
    /// Identifies whether Ctrl+Shift+PageUp scrolls up one page. The default is true.
    /// </summary>
    public static readonly DependencyProperty PressControlPlusShiftPlusPageUpToScrollOnePageProperty =
        RegisterShortcutProperty(nameof(PressControlPlusShiftPlusPageUpToScrollOnePage));

    /// <summary>
    /// Identifies whether Ctrl+Shift+PageDown scrolls down one page. The default is true.
    /// </summary>
    public static readonly DependencyProperty PressControlPlusShiftPlusPageDownToScrollOnePageProperty =
        RegisterShortcutProperty(nameof(PressControlPlusShiftPlusPageDownToScrollOnePage));

    /// <summary>
    /// Identifies whether Ctrl+Shift+Home scrolls to the top. The default is true.
    /// </summary>
    public static readonly DependencyProperty PressControlPlusShiftPlusHomeToScrollToTopProperty =
        RegisterShortcutProperty(nameof(PressControlPlusShiftPlusHomeToScrollToTop));

    /// <summary>
    /// Identifies whether Ctrl+Shift+End scrolls to the bottom. The default is true.
    /// </summary>
    public static readonly DependencyProperty PressControlPlusShiftPlusEndToScrollToBottomProperty =
        RegisterShortcutProperty(nameof(PressControlPlusShiftPlusEndToScrollToBottom));

    /// <summary>
    /// Identifies whether Ctrl+Equals increases the font size. The default is true.
    /// </summary>
    public static readonly DependencyProperty PressControlPlusEqualsToIncreaseFontSizeProperty =
        RegisterShortcutProperty(nameof(PressControlPlusEqualsToIncreaseFontSize));

    /// <summary>
    /// Identifies whether Ctrl+Shift+Equals increases the font size. The default is true.
    /// </summary>
    public static readonly DependencyProperty PressControlPlusShiftPlusEqualsToIncreaseFontSizeProperty =
        RegisterShortcutProperty(nameof(PressControlPlusShiftPlusEqualsToIncreaseFontSize));

    /// <summary>
    /// Identifies whether Ctrl+Numpad Plus increases the font size. The default is true.
    /// </summary>
    public static readonly DependencyProperty PressControlPlusAddToIncreaseFontSizeProperty =
        RegisterShortcutProperty(nameof(PressControlPlusAddToIncreaseFontSize));

    /// <summary>
    /// Identifies whether Ctrl+Minus decreases the font size. The default is true.
    /// </summary>
    public static readonly DependencyProperty PressControlPlusMinusToDecreaseFontSizeProperty =
        RegisterShortcutProperty(nameof(PressControlPlusMinusToDecreaseFontSize));

    /// <summary>
    /// Identifies whether Ctrl+Numpad Minus decreases the font size. The default is true.
    /// </summary>
    public static readonly DependencyProperty PressControlPlusSubtractToDecreaseFontSizeProperty =
        RegisterShortcutProperty(nameof(PressControlPlusSubtractToDecreaseFontSize));

    /// <summary>
    /// Identifies whether Ctrl+0 resets the font size. The default is true.
    /// </summary>
    public static readonly DependencyProperty PressControlPlus0ToResetFontSizeProperty =
        RegisterShortcutProperty(nameof(PressControlPlus0ToResetFontSize));

    /// <summary>
    /// Identifies whether Ctrl+Numpad 0 resets the font size. The default is true.
    /// </summary>
    public static readonly DependencyProperty PressControlPlusNumPad0ToResetFontSizeProperty =
        RegisterShortcutProperty(nameof(PressControlPlusNumPad0ToResetFontSize));

    /// <summary>
    /// Identifies the read-only running state.
    /// </summary>
    public static readonly DependencyProperty IsRunningProperty = IsRunningPropertyKey.DependencyProperty;

    /// <summary>
    /// Identifies the read-only title reported by the terminal process.
    /// </summary>
    public static readonly DependencyProperty TitleProperty = TitlePropertyKey.DependencyProperty;

    /// <summary>
    /// Identifies the most recent terminal failure.
    /// </summary>
    public static readonly DependencyProperty LastErrorProperty = LastErrorPropertyKey.DependencyProperty;

    /// <summary>
    /// Identifies the event raised after the root process exits and its final output is displayed.
    /// </summary>
    public static readonly RoutedEvent TerminalExitedEvent = EventManager.RegisterRoutedEvent(
        nameof(TerminalExited),
        RoutingStrategy.Bubble,
        typeof(EventHandler<TerminalExitedEventArgs>),
        typeof(TerminalEmulator));

    /// <summary>
    /// Identifies the event raised when an automatic operation or input action fails.
    /// </summary>
    public static readonly RoutedEvent TerminalFailedEvent = EventManager.RegisterRoutedEvent(
        nameof(TerminalFailed),
        RoutingStrategy.Bubble,
        typeof(EventHandler<TerminalFailedEventArgs>),
        typeof(TerminalEmulator));

    /// <summary>
    /// Occurs after the root process exits and its final output is displayed.
    /// </summary>
    public event EventHandler<TerminalExitedEventArgs>? TerminalExited {
        add {
            AddHandler(TerminalExitedEvent, value);
        }
        remove {
            RemoveHandler(TerminalExitedEvent, value);
        }
    }

    /// <summary>
    /// Occurs when an automatic operation or input action fails. Explicit StartAsync and StopAsync calls also throw.
    /// </summary>
    public event EventHandler<TerminalFailedEventArgs>? TerminalFailed {
        add {
            AddHandler(TerminalFailedEvent, value);
        }
        remove {
            RemoveHandler(TerminalFailedEvent, value);
        }
    }

    /// <summary>
    /// Gets or sets the Windows command line. Quote executable paths containing spaces.
    /// Changes apply on the next start.
    /// </summary>
    public string CommandLine {
        get => (string)GetValue(CommandLineProperty);
        set => SetValue(CommandLineProperty, value);
    }

    /// <summary>
    /// Gets or sets the starting directory, or null to inherit the host directory.
    /// </summary>
    public string? WorkingDirectory {
        get => (string?)GetValue(WorkingDirectoryProperty);
        set => SetValue(WorkingDirectoryProperty, value);
    }

    /// <summary>
    /// Gets or sets whether loading starts the process. Unloading always stops it asynchronously.
    /// </summary>
    public bool AutoStart { get => (bool)GetValue(AutoStartProperty); set => SetValue(AutoStartProperty, value); }

    /// <summary>
    /// Gets or sets whether Enter copies selected text.
    /// </summary>
    public bool PressEnterToCopy {
        get => (bool)GetValue(PressEnterToCopyProperty);
        set => SetValue(PressEnterToCopyProperty, value);
    }

    /// <summary>
    /// Gets or sets whether Ctrl+C copies selected text.
    /// </summary>
    public bool PressControlPlusCToCopy {
        get => (bool)GetValue(PressControlPlusCToCopyProperty);
        set => SetValue(PressControlPlusCToCopyProperty, value);
    }

    /// <summary>
    /// Gets or sets whether Ctrl+Shift+C copies selected text.
    /// </summary>
    public bool PressControlPlusShiftPlusCToCopy {
        get => (bool)GetValue(PressControlPlusShiftPlusCToCopyProperty);
        set => SetValue(PressControlPlusShiftPlusCToCopyProperty, value);
    }

    /// <summary>
    /// Gets or sets whether Ctrl+Insert copies selected text.
    /// </summary>
    public bool PressControlPlusInsertToCopy {
        get => (bool)GetValue(PressControlPlusInsertToCopyProperty);
        set => SetValue(PressControlPlusInsertToCopyProperty, value);
    }

    /// <summary>
    /// Gets or sets whether Ctrl+V pastes clipboard text.
    /// </summary>
    public bool PressControlPlusVToPaste {
        get => (bool)GetValue(PressControlPlusVToPasteProperty);
        set => SetValue(PressControlPlusVToPasteProperty, value);
    }

    /// <summary>
    /// Gets or sets whether Ctrl+Shift+V pastes clipboard text.
    /// </summary>
    public bool PressControlPlusShiftPlusVToPaste {
        get => (bool)GetValue(PressControlPlusShiftPlusVToPasteProperty);
        set => SetValue(PressControlPlusShiftPlusVToPasteProperty, value);
    }

    /// <summary>
    /// Gets or sets whether Shift+Insert pastes clipboard text.
    /// </summary>
    public bool PressShiftPlusInsertToPaste {
        get => (bool)GetValue(PressShiftPlusInsertToPasteProperty);
        set => SetValue(PressShiftPlusInsertToPasteProperty, value);
    }

    /// <summary>
    /// Gets or sets whether Ctrl+Shift+Up scrolls history.
    /// </summary>
    public bool PressControlPlusShiftPlusUpToScroll {
        get => (bool)GetValue(PressControlPlusShiftPlusUpToScrollProperty);
        set => SetValue(PressControlPlusShiftPlusUpToScrollProperty, value);
    }

    /// <summary>
    /// Gets or sets whether Ctrl+Shift+Down scrolls history.
    /// </summary>
    public bool PressControlPlusShiftPlusDownToScroll {
        get => (bool)GetValue(PressControlPlusShiftPlusDownToScrollProperty);
        set => SetValue(PressControlPlusShiftPlusDownToScrollProperty, value);
    }

    /// <summary>
    /// Gets or sets whether Ctrl+Shift+PageUp scrolls up one page.
    /// </summary>
    public bool PressControlPlusShiftPlusPageUpToScrollOnePage {
        get => (bool)GetValue(PressControlPlusShiftPlusPageUpToScrollOnePageProperty);
        set => SetValue(PressControlPlusShiftPlusPageUpToScrollOnePageProperty, value);
    }

    /// <summary>
    /// Gets or sets whether Ctrl+Shift+PageDown scrolls down one page.
    /// </summary>
    public bool PressControlPlusShiftPlusPageDownToScrollOnePage {
        get => (bool)GetValue(PressControlPlusShiftPlusPageDownToScrollOnePageProperty);
        set => SetValue(PressControlPlusShiftPlusPageDownToScrollOnePageProperty, value);
    }

    /// <summary>
    /// Gets or sets whether Ctrl+Shift+Home scrolls to the top.
    /// </summary>
    public bool PressControlPlusShiftPlusHomeToScrollToTop {
        get => (bool)GetValue(PressControlPlusShiftPlusHomeToScrollToTopProperty);
        set => SetValue(PressControlPlusShiftPlusHomeToScrollToTopProperty, value);
    }

    /// <summary>
    /// Gets or sets whether Ctrl+Shift+End scrolls to the bottom.
    /// </summary>
    public bool PressControlPlusShiftPlusEndToScrollToBottom {
        get => (bool)GetValue(PressControlPlusShiftPlusEndToScrollToBottomProperty);
        set => SetValue(PressControlPlusShiftPlusEndToScrollToBottomProperty, value);
    }

    /// <summary>
    /// Gets or sets whether Ctrl+Equals increases the font size.
    /// </summary>
    public bool PressControlPlusEqualsToIncreaseFontSize {
        get => (bool)GetValue(PressControlPlusEqualsToIncreaseFontSizeProperty);
        set => SetValue(PressControlPlusEqualsToIncreaseFontSizeProperty, value);
    }

    /// <summary>
    /// Gets or sets whether Ctrl+Shift+Equals increases the font size.
    /// </summary>
    public bool PressControlPlusShiftPlusEqualsToIncreaseFontSize {
        get => (bool)GetValue(PressControlPlusShiftPlusEqualsToIncreaseFontSizeProperty);
        set => SetValue(PressControlPlusShiftPlusEqualsToIncreaseFontSizeProperty, value);
    }

    /// <summary>
    /// Gets or sets whether Ctrl+Numpad Plus increases the font size.
    /// </summary>
    public bool PressControlPlusAddToIncreaseFontSize {
        get => (bool)GetValue(PressControlPlusAddToIncreaseFontSizeProperty);
        set => SetValue(PressControlPlusAddToIncreaseFontSizeProperty, value);
    }

    /// <summary>
    /// Gets or sets whether Ctrl+Minus decreases the font size.
    /// </summary>
    public bool PressControlPlusMinusToDecreaseFontSize {
        get => (bool)GetValue(PressControlPlusMinusToDecreaseFontSizeProperty);
        set => SetValue(PressControlPlusMinusToDecreaseFontSizeProperty, value);
    }

    /// <summary>
    /// Gets or sets whether Ctrl+Numpad Minus decreases the font size.
    /// </summary>
    public bool PressControlPlusSubtractToDecreaseFontSize {
        get => (bool)GetValue(PressControlPlusSubtractToDecreaseFontSizeProperty);
        set => SetValue(PressControlPlusSubtractToDecreaseFontSizeProperty, value);
    }

    /// <summary>
    /// Gets or sets whether Ctrl+0 resets the font size.
    /// </summary>
    public bool PressControlPlus0ToResetFontSize {
        get => (bool)GetValue(PressControlPlus0ToResetFontSizeProperty);
        set => SetValue(PressControlPlus0ToResetFontSizeProperty, value);
    }

    /// <summary>
    /// Gets or sets whether Ctrl+Numpad 0 resets the font size.
    /// </summary>
    public bool PressControlPlusNumPad0ToResetFontSize {
        get => (bool)GetValue(PressControlPlusNumPad0ToResetFontSizeProperty);
        set => SetValue(PressControlPlusNumPad0ToResetFontSizeProperty, value);
    }

    /// <summary>
    /// Gets whether the root process is running.
    /// </summary>
    public bool IsRunning => (bool)GetValue(IsRunningProperty);

    /// <summary>
    /// Gets the terminal title.
    /// </summary>
    public string Title => (string)GetValue(TitleProperty);

    /// <summary>
    /// Gets the latest failure, cleared by a successful new start.
    /// </summary>
    public Exception? LastError => (Exception?)GetValue(LastErrorProperty);

    /// <summary>
    /// Creates a terminal control. Native code is loaded when a session starts.
    /// </summary>
    public TerminalEmulator() {
        _timer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _timer.Tick += OnTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        TextCompositionManager.AddPreviewTextInputStartHandler(this, OnTextInputStart);
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.None);
    }

    /// <summary>
    /// Starts the configured process without blocking the dispatcher. Calling this while running has no effect.
    /// </summary>
    public async Task StartAsync() {
        VerifyAccess();
        await _lifecycle.WaitAsync();
        try {
            if (IsRunning) {
                return;
            }
            CancelMouseInteraction();
            RestoreBlinkingText();
            _copyMouseUpPending = false;
            _cursorOn = true;
            _lastBlink = DateTime.UtcNow;
            CancelControlAltPress();
            _controlAltComposition = null;
            _committedComposition = null;
            _textPressPending = false;
            _textExpectedPhysicalText = null;
            TerminalSession? previous = _session;
            _session = null;
            if (previous is not null) {
                await Task.Run(previous.Dispose);
            }
            _forwardedKeys.Clear();
            _forwardedKeyCodepoints.Clear();
            ConfigureSize();
            string command = CommandLine;
            string? directory = WorkingDirectory;
            ushort columns = _columns, lines = _lines;
            TerminalSession session = await Task.Run(() => new TerminalSession(command, directory, columns, lines));
            _session = session;
            _cellPixelWidth = 0;
            _cellPixelHeight = 0;
            _forceFrame = true;
            SetValue(LastErrorPropertyKey, null);
            SetValue(IsRunningPropertyKey, true);
            RefreshFrame();
            _timer.Start();
            _ = ObserveExit(session);
        }
        catch {
            _timer.Stop();
            TerminalSession? failed = _session;
            _session = null;
            SetValue(IsRunningPropertyKey, false);
            if (failed is not null) {
                await Task.Run(failed.Dispose);
            }
            throw;
        }
        finally {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Stops the process and releases native resources asynchronously. Safe to call repeatedly.
    /// </summary>
    public async Task StopAsync() {
        VerifyAccess();
        await _lifecycle.WaitAsync();
        try {
            _timer.Stop();
            RestoreBlinkingText();
            CancelControlAltPress();
            _controlAltComposition = null;
            _committedComposition = null;
            _textPressPending = false;
            _textExpectedPhysicalText = null;
            CancelMouseInteraction();
            _copyMouseUpPending = false;
            _cursorOn = true;
            _lastBlink = DateTime.UtcNow;
            _shortcutKeys.Clear();
            _forwardedKeys.Clear();
            _forwardedKeyCodepoints.Clear();
            TerminalSession? session = _session;
            _session = null;
            SetValue(IsRunningPropertyKey, false);
            if (session is not null) {
                await Task.Run(session.Dispose);
            }
            InvalidateVisual();
        }
        finally {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Pastes text using the terminal's bracketed-paste mode. Does not append Enter.
    /// </summary>
    public void Paste(string text) {
        VerifyAccess();
        if (text is null) {
            throw new ArgumentNullException(nameof(text));
        }
        Send(engine => engine.Paste(text), true);
    }

    /// <summary>
    /// Copies the selected terminal text to the Windows clipboard and clears the selection after a successful copy.
    /// </summary>
    public void CopySelection() {
        VerifyAccess();
        TryCopySelection();
    }

    /// <summary>
    /// Pastes text from the Windows clipboard.
    /// </summary>
    public void PasteClipboard() {
        VerifyAccess();
        try {
            if (Clipboard.ContainsText()) {
                Paste(Clipboard.GetText());
            }
        }
        catch (Exception exception) {
            ReportFailure(exception);
        }
    }
}
