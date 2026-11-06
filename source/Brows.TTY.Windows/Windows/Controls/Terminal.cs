using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Brows.Ghostty;
using Brows.Win32;

namespace Brows.Windows.Controls;

/// <summary>
/// Hosts an interactive Windows process in a Ghostty terminal. Access this control on its WPF dispatcher.
/// </summary>
public class Terminal : Control {
    static Terminal() {
        FocusableProperty.OverrideMetadata(typeof(Terminal), new FrameworkPropertyMetadata(true));
        FontFamilyProperty.OverrideMetadata(typeof(Terminal),
            new FrameworkPropertyMetadata(new FontFamily("Consolas"), FontChanged));
        FontSizeProperty.OverrideMetadata(typeof(Terminal), new FrameworkPropertyMetadata(14.0, FontChanged));
        ClipToBoundsProperty.OverrideMetadata(typeof(Terminal), new FrameworkPropertyMetadata(true));
    }

    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly TerminalDrawing _drawing = new();
    private readonly DispatcherTimer _timer;
    private TerminalSession? _session;
    private TerminalSession? _faultedSession;
    private DrawingGroup?[] _rows = Array.Empty<DrawingGroup>();
    private TerminalRow?[] _rowSnapshots = Array.Empty<TerminalRow>();
    private NativeFrame _frame;
    private bool _forceFrame = true;
    private bool _fontDirty = true;
    private bool _cursorOn = true;
    private DateTime _lastBlink = DateTime.UtcNow;
    private Point? _selectionStart;
    private Point? _selectionEnd;
    private Point? _pendingSelectionEnd;
    private bool _selectionPending;
    private uint _textKey;
    private Key _textPressKey;
    private Action? _pendingControlAltPress;
    private DispatcherOperation? _pendingControlAltOperation;
    private TextComposition? _controlAltComposition;
    private readonly HashSet<Key> _shortcutKeys = new();
    private readonly HashSet<Key> _forwardedKeys = new();
    private ushort _columns = 80;
    private ushort _lines = 24;
    private uint _cellPixelWidth;
    private uint _cellPixelHeight;

    private static readonly DependencyPropertyKey IsRunningPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsRunning), typeof(bool), typeof(Terminal), new PropertyMetadata(false));
    private static readonly DependencyPropertyKey TitlePropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(Title), typeof(string), typeof(Terminal), new PropertyMetadata(""));
    private static readonly DependencyPropertyKey LastErrorPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(LastError), typeof(Exception), typeof(Terminal), new PropertyMetadata(null));

    private static void FontChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) {
        Terminal terminal = (Terminal)sender;
        terminal._fontDirty = true;
        terminal._forceFrame = true;
        terminal.InvalidateMeasure();
        terminal.InvalidateVisual();
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

    /* Keys that never produce text are sent as raw key events so applications
     * receive their identity; text-producing keys flow on to text input. */
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

    private void DeferControlAltPress(Key key, uint action) {
        TerminalSession? session = _session;
        if (session is null) { return; }
        uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        ushort modifiers = Modifiers();
        _pendingControlAltPress = () => {
            if (!ReferenceEquals(_session, session)) { return; }
            Send(engine => {
                session.Key(virtualKey, modifiers, action, "");
                _forwardedKeys.Add(key);
            }, true);
        };
        /* WPF processes character/composition input before background work.
         * A text event cancels this fallback; later keys flush it in order. */
        _pendingControlAltOperation = Dispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(FlushControlAltPress));
    }

    private void OnTextInputStart(object sender, TextCompositionEventArgs args) {
        if (_pendingControlAltPress is null) { return; }
        /* ControlText is a chord translation, not an AltGr text composition. */
        if (!string.IsNullOrEmpty(args.ControlText)) { return; }
        _controlAltComposition = args.TextComposition;
        CancelControlAltPress();
    }

    private void ReportFailure(Exception exception) {
        SetValue(LastErrorPropertyKey, exception);
        Failed?.Invoke(this, new TerminalFailedEventArgs(exception));
    }

    private void HandleFailure(TerminalSession session, Exception exception) {
        if (!ReferenceEquals(_session, session)) { return; }
        if (session.Faulted) {
            FailSession(session, exception);
            return;
        }
        ReportFailure(exception);
    }

    private void FailSession(TerminalSession session, Exception exception) {
        if (ReferenceEquals(_faultedSession, session)) { return; }
        _faultedSession = session;
        _timer.Stop();
        ClearSelectionDrag();
        SetValue(IsRunningPropertyKey, false);
        if (!ReferenceEquals(_session, session)) { return; }
        ReportFailure(exception);
    }

    private async void OnLoaded(object sender, RoutedEventArgs args) {
        if (DesignerProperties.GetIsInDesignMode(this)) { return; }
        if (!AutoStart) { return; }
        try { await StartAsync(); }
        catch (Exception exception) { ReportFailure(exception); }
    }

    private async void OnUnloaded(object sender, RoutedEventArgs args) {
        try { await StopAsync(); }
        catch (Exception exception) { ReportFailure(exception); }
    }

    private async Task ObserveExit(TerminalSession session) {
        uint code;
        try { code = await session.Completion; }
        catch (Exception exception) {
            if (!ReferenceEquals(_session, session)) { return; }
            _timer.Stop();
            SetValue(IsRunningPropertyKey, false);
            if (!ReferenceEquals(_session, session)) { return; }
            ReportFailure(exception);
            return;
        }
        if (!ReferenceEquals(_session, session)) { return; }
        Exception? frameError = null;
        try {
            ApplyPendingSelection();
            RefreshFrame(false);
        }
        catch (Exception exception) { frameError = exception; }
        if (!ReferenceEquals(_session, session)) { return; }
        /* Collect errors before external callbacks can dispose this session. */
        Exception? transportError = null;
        try { session.CheckError(); }
        catch (Exception exception) { transportError = exception; }
        _timer.Stop();
        SetValue(IsRunningPropertyKey, false);
        if (!ReferenceEquals(_session, session)) { return; }
        InvalidateVisual();
        ProcessExited?.Invoke(this, new TerminalExitedEventArgs(code));
        if (frameError is not null) { HandleFailure(session, frameError); }
        if (transportError is not null) { HandleFailure(session, transportError); }
    }

    private void ConfigureSize() {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        if (_fontDirty) {
            _drawing.Configure(FontFamily, FontSize, dpi.PixelsPerDip);
            _fontDirty = false;
        }
        ushort columns = (ushort)Math.Max(1, Math.Min(32767, Math.Floor(ActualWidth / _drawing.CellWidth)));
        ushort lines = (ushort)Math.Max(1, Math.Min(32767, Math.Floor(ActualHeight / _drawing.CellHeight)));
        uint cellPixelWidth = (uint)Math.Max(1, Math.Round(_drawing.CellWidth * dpi.DpiScaleX));
        uint cellPixelHeight = (uint)Math.Max(1, Math.Round(_drawing.CellHeight * dpi.DpiScaleY));
        bool gridChanged = columns != _columns || lines != _lines;
        bool cellPixelSizeChanged = cellPixelWidth != _cellPixelWidth || cellPixelHeight != _cellPixelHeight;
        bool sizeUnchanged = !gridChanged && !cellPixelSizeChanged;
        if (sizeUnchanged) { return; }
        _columns = columns;
        _lines = lines;
        _cellPixelWidth = cellPixelWidth;
        _cellPixelHeight = cellPixelHeight;
        if (_session is not null) {
            _session.Resize(columns, lines, cellPixelWidth, cellPixelHeight);
        }
        _forceFrame = true;
    }

    private void RefreshFrame(bool checkTransport = true) {
        if (_session is null) { return; }
        ConfigureSize();
        if (checkTransport) { _session.CheckError(); }
        TerminalFrame snapshot = _session.Engine.ReadFrame(_forceFrame);
        _forceFrame = false;
        if (_rows.Length != snapshot.Info.Rows) {
            _rows = new DrawingGroup[snapshot.Info.Rows];
            _rowSnapshots = new TerminalRow[snapshot.Info.Rows];
        }
        foreach (TerminalRow row in snapshot.ChangedRows) {
            _rowSnapshots[row.Index] = row;
            _rows[row.Index] = _drawing.DrawRow(row);
        }
        bool cursorChanged = _frame.CursorX != snapshot.Info.CursorX || _frame.CursorY != snapshot.Info.CursorY
            || _frame.CursorVisible != snapshot.Info.CursorVisible || _frame.CursorStyle != snapshot.Info.CursorStyle
            || _frame.CursorColor != snapshot.Info.CursorColor || _frame.CursorBlink != snapshot.Info.CursorBlink;
        _frame = snapshot.Info;
        if (!string.Equals(Title, snapshot.Title, StringComparison.Ordinal)) {
            SetValue(TitlePropertyKey, snapshot.Title);
        }
        bool cursorShouldBlink = IsRunning && _frame.CursorVisible != 0 && _frame.CursorBlink != 0;
        bool blink = cursorShouldBlink && DateTime.UtcNow - _lastBlink >= TimeSpan.FromMilliseconds(500);
        if (blink) {
            _cursorOn = !_cursorOn;
            _lastBlink = DateTime.UtcNow;
        }
        bool redraw = snapshot.ChangedRows.Count != 0 || cursorChanged || blink;
        if (redraw) { InvalidateVisual(); }
    }

    private void OnTick(object? sender, EventArgs args) {
        TerminalSession? session = _session;
        if (session is null) { return; }
        /* Input drops are recoverable. Only a fatal transport error should
         * stop the timer in this path; rendering failures still stop retries. */
        try { session.CheckError(); }
        catch (Exception exception) {
            HandleFailure(session, exception);
            return;
        }
        try {
            ApplyPendingSelection();
            RefreshFrame(false);
        }
        catch (Exception exception) {
            if (!ReferenceEquals(_session, session)) { return; }
            _timer.Stop();
            HandleFailure(session, exception);
        }
    }

    private void Send(Action<TerminalEngine> action, bool scrollToBottom = false) {
        TerminalSession? session = _session;
        bool canSend = session is not null && IsRunning;
        if (!canSend) { return; }
        try {
            if (scrollToBottom) { session!.Engine.Scroll(0, true); }
            action(session!.Engine);
            session.CheckError();
            _cursorOn = true;
            _lastBlink = DateTime.UtcNow;
        }
        catch (Exception exception) { HandleFailure(session!, exception); }
    }

    private void ApplyLocal(Action<TerminalEngine> action) {
        TerminalSession? session = _session;
        if (session is null) { return; }
        try {
            action(session.Engine);
            RefreshFrame();
        }
        catch (Exception exception) { HandleFailure(session, exception); }
    }

    private void ClearSelectionDrag() {
        _selectionStart = null;
        _selectionEnd = null;
        _pendingSelectionEnd = null;
        _selectionPending = false;
    }

    private void QueueSelection(Point endpoint) {
        if (_selectionStart is null) { return; }
        Point? latestEndpoint = _selectionPending ? _pendingSelectionEnd : _selectionEnd;
        if (latestEndpoint == endpoint) { return; }
        if (_selectionEnd == endpoint) {
            _pendingSelectionEnd = null;
            _selectionPending = false;
            return;
        }
        _pendingSelectionEnd = endpoint;
        _selectionPending = true;
    }

    private bool ApplyPendingSelection() {
        if (!_selectionPending) { return false; }
        Point? start = _selectionStart;
        Point? endpoint = _pendingSelectionEnd;
        _pendingSelectionEnd = null;
        _selectionPending = false;
        TerminalSession? session = _session;
        if (session is null) { return false; }
        if (start is null) { return false; }
        if (endpoint is null) { return false; }
        Point selectionStart = start.Value;
        Point selectionEnd = endpoint.Value;
        session.Engine.Select((ushort)selectionStart.X, (ushort)selectionStart.Y,
            (ushort)selectionEnd.X, (ushort)selectionEnd.Y);
        _selectionEnd = endpoint;
        return true;
    }

    private void FlushPendingSelection() {
        TerminalSession? session = _session;
        if (session is null) { return; }
        try {
            bool selectionApplied = ApplyPendingSelection();
            if (!selectionApplied) { return; }
            bool sessionIsCurrent = ReferenceEquals(_session, session);
            if (!sessionIsCurrent) { return; }
            RefreshFrame();
        }
        catch (Exception exception) { HandleFailure(session, exception); }
    }

    private Point CellAt(Point point) {
        return new Point(Math.Max(0, Math.Min(_columns - 1, Math.Floor(point.X / _drawing.CellWidth))),
            Math.Max(0, Math.Min(_lines - 1, Math.Floor(point.Y / _drawing.CellHeight))));
    }

    private void SendMouse(MouseEventArgs args, uint action, uint button) {
        Point point = args.GetPosition(this);
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        bool pressed = args.LeftButton == MouseButtonState.Pressed || args.RightButton == MouseButtonState.Pressed
            || args.MiddleButton == MouseButtonState.Pressed;
        Send(engine => engine.Mouse(action, button, Modifiers(), (float)(point.X * dpi.DpiScaleX),
            (float)(point.Y * dpi.DpiScaleY), pressed));
    }

    private bool ReportsMouse => _frame.MouseTracking != 0 && (Keyboard.Modifiers & ModifierKeys.Shift) == 0;

    /// <inheritdoc />
    protected override void OnRender(DrawingContext context) {
        base.OnRender(context);
        if (_fontDirty) {
            try {
                DpiScale dpi = VisualTreeHelper.GetDpi(this);
                _drawing.Configure(FontFamily, FontSize, dpi.PixelsPerDip);
                /* Snapshots own their cell/text buffers, so stopped sessions can
                 * rebuild glyphs without reading or resizing native state. */
                foreach (TerminalRow? row in _rowSnapshots) {
                    if (row is not null) { _rows[row.Index] = _drawing.DrawRow(row); }
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
            if (row is not null) { context.DrawDrawing(row); }
        }
        bool showCursor = IsRunning && _frame.CursorVisible != 0 && (_frame.CursorBlink == 0 || _cursorOn);
        if (!showCursor) { return; }
        Rect cursor = new(_frame.CursorX * _drawing.CellWidth, _frame.CursorY * _drawing.CellHeight,
            _drawing.CellWidth, _drawing.CellHeight);
        if (_frame.CursorStyle == 0) { cursor.Width = 2; }
        if (_frame.CursorStyle == 2) { cursor.Y += cursor.Height - 2; cursor.Height = 2; }
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
            /* Leave window close and the system menu to the host. */
            return;
        }
        bool copy = (key == Key.C && mods == (ModifierKeys.Control | ModifierKeys.Shift))
            || (key == Key.Insert && mods == ModifierKeys.Control);
        bool paste = (key == Key.V && mods == (ModifierKeys.Control | ModifierKeys.Shift))
            || (key == Key.Insert && mods == ModifierKeys.Shift);
        if (copy) { _shortcutKeys.Add(key); CopySelection(); args.Handled = true; return; }
        if (paste) { _shortcutKeys.Add(key); PasteClipboard(); args.Handled = true; return; }
        _textKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        _textPressKey = key;
        bool special = TextlessKey(key);
        bool control = (mods & ModifierKeys.Control) != 0 && (mods & ModifierKeys.Alt) == 0;
        bool controlAlt = (mods & (ModifierKeys.Control | ModifierKeys.Alt))
            == (ModifierKeys.Control | ModifierKeys.Alt) && !special;
        if (controlAlt) {
            DeferControlAltPress(key, args.IsRepeat ? 2u : 1u);
            return;
        }
        if (special || control) {
            Send(engine => {
                _session!.Key(_textKey, Modifiers(), args.IsRepeat ? 2u : 1u, "");
                _forwardedKeys.Add(key);
            }, true);
            args.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnPreviewKeyUp(KeyEventArgs args) {
        base.OnPreviewKeyUp(args);
        FlushControlAltPress();
        Key key = args.Key == Key.System ? args.SystemKey : args.Key;
        /* A held key can move between local shortcuts and terminal input when
         * modifiers change during autorepeat. Retire both kinds of state. */
        if (_shortcutKeys.Remove(key)) { args.Handled = true; }
        /* Only keys whose press reached the terminal are released, so a release
         * from a key pressed outside the control is never sent alone. */
        if (!_forwardedKeys.Remove(key)) { return; }
        Send(engine => _session!.Key((uint)KeyInterop.VirtualKeyFromKey(key), Modifiers(), 0, ""));
    }

    /// <inheritdoc />
    protected override void OnTextInput(TextCompositionEventArgs args) {
        base.OnTextInput(args);
        bool systemMenu = args.SystemText == " " && Keyboard.Modifiers == ModifierKeys.Alt;
        if (systemMenu) { return; }
        string text = string.IsNullOrEmpty(args.Text) ? args.SystemText : args.Text;
        if (string.IsNullOrEmpty(text)) { return; }
        bool controlAltText = _pendingControlAltPress is not null
            || ReferenceEquals(_controlAltComposition, args.TextComposition);
        CancelControlAltPress();
        _controlAltComposition = null;
        Send(engine => {
            if (controlAltText) {
                /* AltGr produced text, rather than a Ctrl+Alt binding. Encode it
                 * as composition text so Kitty does not substitute the physical key. */
                engine.Key(TerminalKey.Unidentified, (ushort)(Modifiers() & ~6), 1, text);
                return;
            }
            _session!.Key(_textKey, Modifiers(), 1, text);
            _forwardedKeys.Add(_textPressKey);
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
        CancelControlAltPress();
        _controlAltComposition = null;
        _shortcutKeys.Clear();
        /* The release events of held keys go to the window that takes the focus,
         * so release them here to keep applications tracking keys consistent. */
        ReleaseForwardedKeys();
        Send(engine => engine.Focus(false));
        InvalidateVisual();
    }

    private void ReleaseForwardedKeys() {
        Key[] held = _forwardedKeys.ToArray();
        _forwardedKeys.Clear();
        foreach (Key key in held) {
            Send(engine => _session!.Key((uint)KeyInterop.VirtualKeyFromKey(key), Modifiers(), 0, ""));
        }
    }

    /// <inheritdoc />
    protected override void OnMouseDown(MouseButtonEventArgs args) {
        base.OnMouseDown(args);
        Focus();
        if (_session is null) { return; }
        FlushPendingSelection();
        ClearSelectionDrag();
        if (ReportsMouse) {
            SendMouse(args, 0,
                args.ChangedButton == MouseButton.Left ? 1u : args.ChangedButton == MouseButton.Right ? 2u : 3u);
            CaptureMouse();
        }
        else if (args.ChangedButton == MouseButton.Left) {
            _selectionStart = CellAt(args.GetPosition(this));
            Point point = _selectionStart.Value;
            _selectionEnd = point;
            CaptureMouse();
            ApplyLocal(engine =>
                engine.Select((ushort)point.X, (ushort)point.Y, (ushort)point.X, (ushort)point.Y));
        }
        args.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs args) {
        base.OnMouseMove(args);
        if (_session is null) { return; }
        if (ReportsMouse) { SendMouse(args, 2, 0); return; }
        bool selecting = _selectionStart.HasValue && args.LeftButton == MouseButtonState.Pressed;
        if (!selecting) { return; }
        Point point = CellAt(args.GetPosition(this));
        QueueSelection(point);
    }

    /// <inheritdoc />
    protected override void OnMouseUp(MouseButtonEventArgs args) {
        base.OnMouseUp(args);
        FlushPendingSelection();
        if (ReportsMouse) {
            SendMouse(args, 1,
                args.ChangedButton == MouseButton.Left ? 1u : args.ChangedButton == MouseButton.Right ? 2u : 3u);
        }
        ClearSelectionDrag();
        ReleaseMouseCapture();
        args.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnMouseWheel(MouseWheelEventArgs args) {
        base.OnMouseWheel(args);
        if (_session is null) { return; }
        if (ReportsMouse) { SendMouse(args, 0, args.Delta > 0 ? 4u : 5u); }
        else {
            FlushPendingSelection();
            ClearSelectionDrag();
            ApplyLocal(engine => engine.Scroll(-Math.Sign(args.Delta) * 3));
        }
        args.Handled = true;
    }

    /// <summary>
    /// Identifies the command line used for the next process start.
    /// </summary>
    public static readonly DependencyProperty CommandLineProperty = DependencyProperty.Register(nameof(CommandLine),
        typeof(string), typeof(Terminal),
        new PropertyMetadata("\"" + Environment.GetEnvironmentVariable("ComSpec") + "\""));
    /// <summary>
    /// Identifies the working directory used for the next process start.
    /// </summary>
    public static readonly DependencyProperty WorkingDirectoryProperty = DependencyProperty.Register(
        nameof(WorkingDirectory), typeof(string), typeof(Terminal), new PropertyMetadata(null));
    /// <summary>
    /// Identifies whether loading the control starts its process automatically.
    /// </summary>
    public static readonly DependencyProperty AutoStartProperty = DependencyProperty.Register(nameof(AutoStart),
        typeof(bool), typeof(Terminal), new PropertyMetadata(true));
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
    /// Occurs after the root process exits and its final output is displayed.
    /// </summary>
    public event EventHandler<TerminalExitedEventArgs>? ProcessExited;
    /// <summary>
    /// Occurs when an automatic operation or input action fails. Explicit StartAsync and StopAsync calls also throw.
    /// </summary>
    public event EventHandler<TerminalFailedEventArgs>? Failed;

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
    public Terminal() {
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
            if (IsRunning) { return; }
            ClearSelectionDrag();
            _cursorOn = true;
            _lastBlink = DateTime.UtcNow;
            CancelControlAltPress();
            _controlAltComposition = null;
            TerminalSession? previous = _session;
            _session = null;
            if (previous is not null) { await Task.Run(previous.Dispose); }
            _forwardedKeys.Clear();
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
            if (failed is not null) { await Task.Run(failed.Dispose); }
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>
    /// Stops the process and releases native resources asynchronously. Safe to call repeatedly.
    /// </summary>
    public async Task StopAsync() {
        VerifyAccess();
        await _lifecycle.WaitAsync();
        try {
            _timer.Stop();
            CancelControlAltPress();
            _controlAltComposition = null;
            ClearSelectionDrag();
            _cursorOn = true;
            _lastBlink = DateTime.UtcNow;
            _shortcutKeys.Clear();
            _forwardedKeys.Clear();
            ReleaseMouseCapture();
            TerminalSession? session = _session;
            _session = null;
            SetValue(IsRunningPropertyKey, false);
            if (session is not null) { await Task.Run(session.Dispose); }
            InvalidateVisual();
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>
    /// Pastes text using the terminal's bracketed-paste mode. Does not append Enter.
    /// </summary>
    public void Paste(string text) {
        VerifyAccess();
        if (text is null) { throw new ArgumentNullException(nameof(text)); }
        Send(engine => engine.Paste(text), true);
    }

    /// <summary>
    /// Copies the selected terminal text to the Windows clipboard.
    /// </summary>
    public void CopySelection() {
        VerifyAccess();
        if (_session is null) { return; }
        FlushPendingSelection();
        if (_session is null) { return; }
        try {
            string text = _session.Engine.SelectedText();
            if (text.Length != 0) { Clipboard.SetText(text); }
        }
        catch (Exception exception) { ReportFailure(exception); }
    }

    /// <summary>
    /// Pastes text from the Windows clipboard.
    /// </summary>
    public void PasteClipboard() {
        VerifyAccess();
        try {
            if (Clipboard.ContainsText()) { Paste(Clipboard.GetText()); }
        }
        catch (Exception exception) { ReportFailure(exception); }
    }
}
