using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace PCPhoneConnect;

public partial class MainWindow : Window
{
    private readonly PhoneClient _client = new();
    private int _srcW, _srcH;

    // Pointer-gesture tracking
    private (double x, double y)? _downNorm;
    private readonly Stopwatch _pressTimer = new();

    // Live drag ("grab and pull"): once the cursor moves past the tap threshold
    // while held, we stream a continuous touch (down -> move* -> up).
    private bool _dragging;
    private readonly Stopwatch _segTimer = new();   // real time since the last sent segment
    private const double MoveIntervalMs = 10;        // cap move messages to ~100/s

    // Release-momentum ("fling"): the launcher pages on drag distance, not on
    // injected velocity, so on a fast flick we project the endpoint forward to
    // carry the gesture past the page-turn threshold. Slow releases stay 1:1.
    private (double x, double y) _lastSentNorm;
    private double _velX, _velY;                     // normalized units per ms (smoothed)
    private const double MomentumMs = 110;           // how far a flick coasts

    // While the phone is showing Recent apps (opened via the Recents button/key),
    // the wheel scrolls the carousel horizontally instead of vertically.
    private bool _recentsMode;

    // Wheel scrolling. A short distance played slowly reads as a drag, not a fling:
    // a fast swipe makes the app coast after the gesture ends, which overshot content
    // badly enough that you couldn't get back to it.
    private const double WheelDistance = 0.16;  // fraction of screen height per notch
    private const int WheelMs = 280;            // slow enough to avoid fling momentum
    private const double RecentsWheelDistance = 0.30;
    private const int RecentsWheelMs = 220;

    private const double TapMovePixels = 14; // device-space movement below this = tap

    // Recent connections, most-recent first, persisted between runs.
    private readonly ObservableCollection<string> _history = new();
    private static readonly string HistoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PCPhoneConnect", "history.txt");

    public MainWindow()
    {
        InitializeComponent();
        _client.HeaderReceived += OnHeader;
        _client.FrameReceived += OnFrame;
        _client.Disconnected += OnDisconnected;
        _client.FolderListed += OnFolderListed;
        _client.TransferProgress += OnTransferProgress;
        _client.TransferDone += OnTransferDone;
        _client.TransferError += OnTransferError;
        _client.MessageReceived += OnMessageReceived;
        _client.ReplyResult += OnReplyResult;
        _client.ThreadsListed += OnThreadsListed;
        _client.ThreadLoaded += OnThreadLoaded;
        _client.SmsSent += OnSmsSent;
        Closed += (_, _) => _client.Dispose();
        PreviewKeyDown += OnKeyDown;
        Loaded += OnLoaded;
        // The layered-window style needs a real HWND, which exists from here on.
        SourceInitialized += (_, _) => ApplyWindowAlpha();
        LoadHistory();
        LoadFolders();
        LoadHiddenNumbers();
        LoadSent();
        HistoryList.ItemsSource = _history;

        // Read the version from the assembly so the badge can't drift from the
        // csproj. 1.50 ships as 1.50.0.0, so trim the trailing zero parts.
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (v != null) VersionText.Text = $"v{v.Major}.{v.Minor:00}";

        LoadWidgetSettings();

        // Remember the last connection: pre-fill the fields on launch.
        if (_history.Count > 0) ApplyEntry(_history[0]);
    }

    private void ApplyEntry(string entry)
    {
        var parts = entry.Split(':');
        IpBox.Text = parts[0];
        if (parts.Length > 1) PortBox.Text = parts[1];
    }

    // ---------------- Connection history ----------------

    /// <summary>
    /// Opens the recent-connections list. Driven by an explicit button click
    /// rather than field focus: focus arrives on mouse-down, and the mouse-up
    /// that followed immediately dismissed the StaysOpen="False" popup, so the
    /// list flashed open and shut and looked like it never appeared at all.
    /// </summary>
    private void OnShowHistory(object sender, RoutedEventArgs e)
    {
        HistoryHeader.Text = _history.Count > 0
            ? "Recent connections"
            : "No recent connections yet";
        HistoryPopup.IsOpen = !HistoryPopup.IsOpen;
    }

    private void OnHistoryPick(object sender, MouseButtonEventArgs e)
    {
        // Resolve the clicked row directly (don't depend on selection state).
        var item = ItemsControl.ContainerFromElement(HistoryList, (DependencyObject)e.OriginalSource) as ListBoxItem;
        if (item?.DataContext is not string entry) return;
        ApplyEntry(entry);
        HistoryPopup.IsOpen = false;
        ConnectButton.Focus();
    }

    private void RememberConnection(string ip, int port)
    {
        var entry = $"{ip}:{port}";
        int existing = _history.IndexOf(entry);
        if (existing >= 0) _history.Move(existing, 0);
        else _history.Insert(0, entry);
        while (_history.Count > 10) _history.RemoveAt(_history.Count - 1);
        SaveHistory();
    }

    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(HistoryPath)) return;
            foreach (var line in File.ReadAllLines(HistoryPath))
            {
                var t = line.Trim();
                if (t.Length > 0 && !_history.Contains(t)) _history.Add(t);
            }
        }
        catch { /* history is best-effort */ }
    }

    private void SaveHistory()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
            File.WriteAllLines(HistoryPath, _history);
        }
        catch { /* history is best-effort */ }
    }

    /// <summary>
    /// Optional auto-connect: <c>PCPhoneConnect.exe &lt;ip&gt; [port]</c> prefills the
    /// fields and connects on launch. With no arguments the app starts idle.
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Length < 2) return;
        IpBox.Text = args[1].Trim();
        if (args.Length >= 3) PortBox.Text = args[2].Trim();
        OnConnectClick(this, new RoutedEventArgs());
    }

    // ---------------- Connection ----------------

    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        if (_client.IsConnected)
        {
            _client.Disconnect();
            ResetUi("Disconnected.");
            return;
        }

        var host = IpBox.Text.Trim();
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            Status("Enter a valid port (1–65535).");
            return;
        }
        if (host.Length == 0)
        {
            Status("Enter the phone's IP address.");
            return;
        }

        Status($"Connecting to {host}:{port}…");
        HistoryPopup.IsOpen = false;
        ConnectButton.IsEnabled = false;
        try
        {
            await Task.Run(() => _client.Connect(host, port)); // off the UI thread
            ConnectButton.Content = "Disconnect";
            Status($"Connected to {host}:{port}. Waiting for screen…");
            RememberConnection(host, port);
        }
        catch (Exception ex)
        {
            Status($"Could not connect: {ex.Message}");
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private bool _initialHomeDone;

    /// <summary>
    /// Land on the phone's MAIN home page. A single Home press only returns to the
    /// launcher on whatever page it was last showing; pressing Home again while
    /// already on the launcher is what jumps to the primary page.
    /// </summary>
    private async Task GoToMainHomeAsync()
    {
        _client.Key("home");
        await Task.Delay(450);
        if (_client.IsConnected) _client.Key("home");
    }

    private void OnHeader(DeviceHeader h) => Dispatcher.Invoke(() =>
    {
        // Shape the bezel to the real device aspect ratio.
        if (h.Width > 0 && h.Height > 0)
        {
            const double innerH = 720;
            double innerW = innerH * h.Width / h.Height;
            PhoneBezel.Height = innerH + 20;
            PhoneBezel.Width = innerW + 20;
        }
        Placeholder.Visibility = Visibility.Collapsed;
        SetNavEnabled(true);

        // Show the detected device: friendly name, with the bare model number and
        // Android version alongside it when the phone reported them.
        var detail = h.Model.Length > 0 && !h.Name.Contains(h.Model, StringComparison.OrdinalIgnoreCase)
            ? $"{h.Name} · {h.Model}"
            : h.Name;
        if (h.AndroidVersion.Length > 0) detail += $" · Android {h.AndroidVersion}";
        Title = $"PC Phone Connect — {h.Name}";
        TitleText.Text = Title;
        Status($"Mirroring {detail} ({h.Width}×{h.Height}). Click to tap, drag to swipe, right-click = Back.");

        // Start the view on the phone's main home page. Only mark it done once
        // control actually works, so a connection made while the accessibility
        // service is off retries instead of silently skipping.
        if (!_initialHomeDone && h.ControlEnabled)
        {
            _initialHomeDone = true;
            _ = GoToMainHomeAsync();
        }

        if (!h.ControlEnabled)
        {
            Status("Mirroring only — remote control is OFF. On the phone, tap " +
                   "\"Enable control (Accessibility settings)\". An app update can " +
                   "switch it off.");
        }

        MessagesStatus.Text = h.MessagesEnabled
            ? "Messages arrive here as they do on the phone."
            : "Message access is off — tap \"Allow messages\" in the phone app.";
        if (h.MessagesEnabled) _client.RequestMessages();
        SmsHistoryButton.IsEnabled = h.SmsHistory;
        SmsHistoryButton.ToolTip = h.SmsHistory
            ? "Browse SMS conversations on the phone"
            : "SMS history is off — tap \"Allow SMS history\" in the phone app.";

        if (h.StorageRoot.Length > 0) _storageRoot = h.StorageRoot;
        if (RemoteDirBox.Text.Trim().Length == 0) RemoteDirBox.Text = _storageRoot;
        if (!h.FileAccess)
        {
            TransferStatus.Text = "File transfer needs \"All files access\" — " +
                                  "tap \"Allow file transfer\" in the phone app.";
        }
        else if (FilePanel.Visibility == Visibility.Visible)
        {
            BrowsePhone(RemoteDirBox.Text.Trim());
        }
    });

    private void OnFrame(byte[] jpeg)
    {
        BitmapImage bmp;
        try
        {
            bmp = new BitmapImage();
            using var ms = new MemoryStream(jpeg);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
        }
        catch
        {
            return; // skip corrupt frame
        }

        Dispatcher.BeginInvoke(() =>
        {
            _srcW = bmp.PixelWidth;
            _srcH = bmp.PixelHeight;
            ScreenImage.Source = bmp;
            if (Placeholder.Visibility == Visibility.Visible)
                Placeholder.Visibility = Visibility.Collapsed;
        });
    }

    private void OnDisconnected(string reason) => Dispatcher.Invoke(() => ResetUi($"Disconnected: {reason}"));

    private void ResetUi(string status)
    {
        _recentsMode = false;
        _dragging = false;
        _downNorm = null;
        Title = "PC Phone Connect";
        TitleText.Text = Title;
        ConnectButton.Content = "Connect";
        SetNavEnabled(false);
        ScreenImage.Source = null;
        Placeholder.Visibility = Visibility.Visible;

        // Drop per-device state so a reconnect doesn't operate on the old phone's
        // paths, and re-enable buttons a failed transfer may have left disabled.
        SetTransferButtons(true);
        PhoneFiles.Items.Clear();
        _phonePath = "";
        _phoneParent = null;
        // Message keys belong to the old connection; replying with them would fail.
        MessageList.Items.Clear();
        // A send that was never confirmed would otherwise claim the first result
        // of the next connection and echo the wrong message.
        _pendingSends.Clear();
        ClearTypeBoxLocal();
        Status(status);
    }

    private void SetNavEnabled(bool on)
    {
        BackButton.IsEnabled = on;
        HomeButton.IsEnabled = on;
        RecentsButton.IsEnabled = on;
        NotificationsButton.IsEnabled = on;
    }

    private void Status(string text) => StatusText.Text = text;

    // ---------------- Pointer -> tap / swipe / long press ----------------

    private (double x, double y)? ToNormalized(Point p)
    {
        double iw = ScreenImage.ActualWidth, ih = ScreenImage.ActualHeight;
        if (_srcW <= 0 || _srcH <= 0 || iw <= 0 || ih <= 0) return null;

        double scale = Math.Min(iw / _srcW, ih / _srcH);
        double dispW = _srcW * scale, dispH = _srcH * scale;
        double offX = (iw - dispW) / 2, offY = (ih - dispH) / 2;

        double nx = (p.X - offX) / dispW;
        double ny = (p.Y - offY) / dispH;
        if (nx < 0 || nx > 1 || ny < 0 || ny > 1) return null;
        return (nx, ny);
    }

    /// <summary>Same mapping as <see cref="ToNormalized"/> but clamped to the edges.</summary>
    private (double x, double y)? ClampToImage(Point p)
    {
        double iw = ScreenImage.ActualWidth, ih = ScreenImage.ActualHeight;
        if (_srcW <= 0 || _srcH <= 0 || iw <= 0 || ih <= 0) return null;
        double scale = Math.Min(iw / _srcW, ih / _srcH);
        double dispW = _srcW * scale, dispH = _srcH * scale;
        double offX = (iw - dispW) / 2, offY = (ih - dispH) / 2;
        return (Math.Clamp((p.X - offX) / dispW, 0, 1), Math.Clamp((p.Y - offY) / dispH, 0, 1));
    }

    private void OnScreenMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_client.IsConnected) return;
        _downNorm = ToNormalized(e.GetPosition(ScreenImage));
        _dragging = false;
        _pressTimer.Restart();
        _segTimer.Restart();
        ScreenImage.CaptureMouse();
    }

    private void OnScreenMouseMove(object sender, MouseEventArgs e)
    {
        if (!_client.IsConnected || !ScreenImage.IsMouseCaptured || _downNorm == null) return;
        var cur = ToNormalized(e.GetPosition(ScreenImage));
        if (cur == null) return;

        if (!_dragging)
        {
            double dx = (cur.Value.x - _downNorm.Value.x) * _srcW;
            double dy = (cur.Value.y - _downNorm.Value.y) * _srcH;
            if (Math.Sqrt(dx * dx + dy * dy) < TapMovePixels) return; // still a potential tap
            _dragging = true;
            _client.TouchDown(_downNorm.Value.x, _downNorm.Value.y); // grab where the press began
            _lastSentNorm = _downNorm.Value;
            _velX = _velY = 0;
            _segTimer.Restart();
        }

        double elapsed = _segTimer.Elapsed.TotalMilliseconds;
        if (elapsed < MoveIntervalMs) return;
        _segTimer.Restart();

        // Track a smoothed velocity for release momentum.
        double dt = Math.Max(elapsed, 1);
        double ivx = (cur.Value.x - _lastSentNorm.x) / dt;
        double ivy = (cur.Value.y - _lastSentNorm.y) / dt;
        _velX = 0.6 * ivx + 0.4 * _velX;
        _velY = 0.6 * ivy + 0.4 * _velY;
        _lastSentNorm = cur.Value;

        _client.TouchMove(cur.Value.x, cur.Value.y, (int)Math.Round(elapsed));
    }

    private void OnScreenMouseUp(object sender, MouseButtonEventArgs e)
    {
        // Snapshot the gesture BEFORE touching capture: releasing it raises
        // LostMouseCapture, whose handler clears _downNorm, which would otherwise
        // wipe the press position and swallow every tap.
        var start = _downNorm;
        var upPoint = e.GetPosition(ScreenImage);
        _pressTimer.Stop();

        // Release capture unconditionally. Returning early while the image still
        // holds capture swallows every later mouse event in the window — the whole
        // app looks frozen until it is restarted.
        _ignoreLostCapture = true;
        ScreenImage.ReleaseMouseCapture();
        _ignoreLostCapture = false;

        _downNorm = null;
        if (!_client.IsConnected) { _dragging = false; return; }
        if (start == null) { _dragging = false; return; }
        // Releasing past the edge of the image used to snap the gesture back to
        // the press point, cancelling the drag. Clamp to the edge instead.
        var end = ToNormalized(upPoint) ?? ClampToImage(upPoint) ?? start.Value;

        // A live drag was in progress — release the continuous touch.
        if (_dragging)
        {
            _dragging = false;

            // A pause before letting go means the user is placing, not flinging —
            // decay the tracked velocity by how long the pointer sat still, or a
            // stale reading would fling a deliberately positioned page.
            double idleMs = _segTimer.Elapsed.TotalMilliseconds;
            double decay = idleMs <= 40 ? 1.0 : Math.Max(0, 1.0 - (idleMs - 40) / 120.0);
            _velX *= decay;
            _velY *= decay;

            // Project the endpoint forward along the release velocity. A fast flick
            // then coasts past the page-turn threshold; a slow release barely moves.
            double projX = Math.Clamp(end.x + _velX * MomentumMs, 0, 1);
            double projY = Math.Clamp(end.y + _velY * MomentumMs, 0, 1);
            double coastPix = Math.Sqrt(Math.Pow((projX - end.x) * _srcW, 2) +
                                        Math.Pow((projY - end.y) * _srcH, 2));

            if (coastPix > TapMovePixels * 2)
            {
                const int steps = 4;
                for (int i = 1; i <= steps; i++)
                {
                    double t = i / (double)steps;
                    _client.TouchMove(end.x + (projX - end.x) * t, end.y + (projY - end.y) * t, 8);
                }
                _client.TouchUp(projX, projY, 8);
            }
            else
            {
                _client.TouchUp(end.x, end.y, (int)Math.Round(_segTimer.Elapsed.TotalMilliseconds));
            }
            return;
        }

        double dxPix = (end.x - start.Value.x) * _srcW;
        double dyPix = (end.y - start.Value.y) * _srcH;
        double moved = Math.Sqrt(dxPix * dxPix + dyPix * dyPix);
        long ms = _pressTimer.ElapsedMilliseconds;

        if (moved < TapMovePixels)
        {
            // A tap selects/dismisses — that leaves the Recents carousel.
            _recentsMode = false;
            if (ms >= 500) _client.LongPress(start.Value.x, start.Value.y, (int)Math.Min(ms, 2000));
            else _client.Tap(start.Value.x, start.Value.y);
        }
        else
        {
            // Fast flick that never triggered a move event — send it as one swipe.
            int dur = (int)Math.Clamp(ms, 50, 900);
            _client.Swipe(start.Value.x, start.Value.y, end.x, end.y, dur);
        }
    }

    /// <summary>
    /// Capture can be taken away mid-drag (alt-tab, a system dialog, the window
    /// deactivating). Without this the injected touch is never lifted and the
    /// phone keeps a finger held down, freezing whatever was being dragged.
    /// </summary>
    private bool _ignoreLostCapture;

    private void OnScreenLostCapture(object sender, MouseEventArgs e)
    {
        // The normal mouse-up releases capture itself and handles the gesture;
        // only react when capture is taken away unexpectedly.
        if (_ignoreLostCapture) return;
        if (_dragging)
        {
            _dragging = false;
            if (_client.IsConnected) _client.TouchUp(_lastSentNorm.x, _lastSentNorm.y, 16);
        }
        _downNorm = null;
    }

    private void OnScreenRightUp(object sender, MouseButtonEventArgs e)
    {
        if (!_client.IsConnected) return;
        _recentsMode = false;
        _client.Key("back");
    }

    /// <summary>
    /// Mouse wheel → swipe, so scrolling works in browsers, Reddit, feeds, etc.
    /// Normally vertical: wheel up scrolls the page up (finger swipes down), wheel
    /// down scrolls down (finger swipes up). In Recents mode the carousel is
    /// horizontal, so the wheel swipes left/right instead. The swipe runs at the
    /// cursor position so split layouts scroll the pane under the pointer.
    /// </summary>
    private void OnScreenWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_client.IsConnected) return;

        var n = ToNormalized(e.GetPosition(ScreenImage));

        // Spinning the wheel fast delivers several notches faster than a ~280ms
        // swipe can be injected, and the phone drops gestures that arrive while
        // one is running — so scroll distance scales with the accumulated delta
        // instead of silently losing the extra notches.
        int notches = Math.Clamp(Math.Abs(e.Delta) / 120, 1, 3);

        if (_recentsMode)
        {
            const double half = RecentsWheelDistance / 2;
            double cy = Math.Clamp(n?.y ?? 0.5, 0.05, 0.95);
            // Wheel down → move forward through recents → swipe left; wheel up → right.
            double x1 = e.Delta < 0 ? 0.5 + half : 0.5 - half;
            double x2 = e.Delta < 0 ? 0.5 - half : 0.5 + half;
            _client.Swipe(x1, cy, x2, cy, RecentsWheelMs);
        }
        else
        {
            double half = Math.Min(WheelDistance * notches, 0.8) / 2;
            double cx = Math.Clamp(n?.x ?? 0.5, 0.05, 0.95);
            // Wheel down → scroll page down → swipe up; wheel up → swipe down.
            double y1 = e.Delta < 0 ? 0.5 + half : 0.5 - half;
            double y2 = e.Delta < 0 ? 0.5 - half : 0.5 + half;
            _client.Swipe(cx, y1, cx, y2, WheelMs);
        }

        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!_client.IsConnected) return;
        // Don't hijack keys while a text field has focus — let it edit normally.
        if (Keyboard.FocusedElement is TextBox) return;
        switch (e.Key)
        {
            case Key.Escape: _recentsMode = false; _client.Key("back"); e.Handled = true; break;
            case Key.Home: _recentsMode = false; _client.Key("home"); e.Handled = true; break;
        }
    }

    // ---------------- Text entry ----------------

    // The text box is the source of truth: its full contents are mirrored to the
    // phone's focused field on every change (one atomic SET_TEXT). This avoids the
    // per-keystroke races that dropped spaces, and bypasses on-device autocorrect.
    private bool _suppressMirror;

    private void OnTypeChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressMirror || !_client.IsConnected) return;
        _client.SetText(TypeBox.Text);
    }

    private void OnTextInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (_client.IsConnected)
        {
            _client.SetText(TypeBox.Text);   // make sure the field has the final text
            _client.Key("enter");            // fire the field's Search/Send/Go action
        }
        // Always clear, connected or not, so the box never carries the last message
        // into the next one.
        ClearTypeBoxLocal();
        e.Handled = true;
        // Backspace and all editing are handled locally by the TextBox and mirrored
        // through OnTypeChanged, so no special-casing is needed.
    }

    private void OnClearField(object sender, RoutedEventArgs e)
    {
        if (_client.IsConnected) _client.SetText("");
        ClearTypeBoxLocal();
    }

    // Clear the box without pushing an empty field to the phone (used after submit).
    private void ClearTypeBoxLocal()
    {
        _suppressMirror = true;
        TypeBox.Clear();
        _suppressMirror = false;
    }

    // ---------------- Widget view ----------------

    private bool _widgetMode;
    private Rect _normalBounds;

    private static readonly string WidgetSettingsPath = Path.Combine(
        Path.GetDirectoryName(HistoryPath)!, "widget.txt");
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "PCPhoneConnect";

    /// <summary>
    /// Double-click toggles the compact widget. Clicks that land on the mirror or
    /// on a control are left alone — double-tap is a real gesture on the phone and
    /// the text box needs its own double-click to select a word.
    /// </summary>
    private void OnWindowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractiveSource(e.OriginalSource as DependencyObject)) return;
        SetWidgetMode(!_widgetMode);
        e.Handled = true;
    }

    /// <summary>True if the point is inside the mirror or any input control.</summary>
    private bool IsInteractiveSource(DependencyObject? source)
    {
        while (source != null)
        {
            if (ReferenceEquals(source, ScreenImage)) return true;
            if (source is TextBox or ButtonBase or ListBox or ListBoxItem) return true;

            // Text inside a TextBlock arrives as a Run, which is a content
            // element rather than a Visual. Walking only the visual tree stopped
            // dead there, so clicking a conversation's name looked like a click
            // on nothing and toggled the widget instead of opening the thread.
            source = source switch
            {
                Visual or Visual3D => VisualTreeHelper.GetParent(source),
                FrameworkContentElement content => content.Parent,
                _ => null,
            };
        }
        return false;
    }

    private void SetWidgetMode(bool on)
    {
        if (on == _widgetMode) return;

        if (on)
        {
            _normalBounds = new Rect(Left, Top, Width, Height);

            // Strip everything except the mirror — but keep the typing bar, since
            // typing to the phone is the main thing you still want from a widget.
            TitleBar.Visibility = Visibility.Collapsed;
            ConnectionBar.Visibility = Visibility.Collapsed;
            NavBar.Visibility = Visibility.Collapsed;
            StatusBar.Visibility = Visibility.Collapsed;
            FilesTab.Visibility = Visibility.Collapsed;
            FilePanel.Visibility = Visibility.Collapsed;
            MessagesTab.Visibility = Visibility.Collapsed;
            MessagesPanel.Visibility = Visibility.Collapsed;
            // A little padding so the kept typing bar isn't flush to the corner.
            ContentGrid.Margin = new Thickness(6, 6, 6, 4);

            MinWidth = 160;
            MinHeight = 260;
            Width = Math.Max(240, _normalBounds.Width * 0.55);
            Height = Math.Max(400, _normalBounds.Height * 0.55);
        }
        else
        {
            TitleBar.Visibility = Visibility.Visible;
            ConnectionBar.Visibility = Visibility.Visible;
            NavBar.Visibility = Visibility.Visible;
            StatusBar.Visibility = Visibility.Visible;
            FilesTab.Visibility = Visibility.Visible;
            MessagesTab.Visibility = Visibility.Visible;
            ContentGrid.Margin = new Thickness(16);

            MinWidth = 360;
            MinHeight = 600;
            if (_normalBounds.Width > 0)
            {
                Width = _normalBounds.Width;
                Height = _normalBounds.Height;
                Left = _normalBounds.X;
                Top = _normalBounds.Y;
            }
        }

        _widgetMode = on;
        WidgetViewItem.IsChecked = on;
        SaveWidgetSettings();
    }

    private void OnToggleWidgetFromMenu(object sender, RoutedEventArgs e) =>
        SetWidgetMode(WidgetViewItem.IsChecked);

    private void OnCloseFromMenu(object sender, RoutedEventArgs e) => Close();

    /// <summary>Chromeless widget has no title bar, so dragging the frame moves it.</summary>
    private void OnRootMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_widgetMode || e.ChangedButton != MouseButton.Left) return;
        if (IsInteractiveSource(e.OriginalSource as DependencyObject)) return;
        try { DragMove(); } catch { /* already released */ }
    }

    /// <summary>Right-click on the mirror means Back, so don't open the menu there.</summary>
    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, ScreenImage) ||
            IsInteractiveSource(e.OriginalSource as DependencyObject))
        {
            e.Handled = true;
            return;
        }
        // Tick whichever opacity is currently in effect.
        if (RootGrid.ContextMenu is { } menu)
        {
            foreach (var top in menu.Items.OfType<MenuItem>())
            {
                foreach (var sub in top.Items.OfType<MenuItem>())
                {
                    if (sub.Tag is string t &&
                        double.TryParse(t, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var v))
                    {
                        sub.IsCheckable = true;
                        sub.IsChecked = Math.Abs(v - _windowAlpha) < 0.001;
                    }
                }
            }
        }
    }

    private void OnAlwaysOnTop(object sender, RoutedEventArgs e)
    {
        Topmost = AlwaysOnTopItem.IsChecked;
        SaveWidgetSettings();
    }

    private void OnOpacity(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } &&
            double.TryParse(tag, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            _windowAlpha = Math.Clamp(value, 0.2, 1.0);
            ApplyWindowAlpha();
            SaveWidgetSettings();
        }
    }

    // ---- Window transparency ----
    // Window.Opacity is only genuinely see-through when AllowsTransparency is set,
    // and WPF allows that only on a chromeless window. Without it the content was
    // merely blended against the window's own dark background, which read as
    // "dimmer, not transparent". The window is now WindowStyle=None +
    // AllowsTransparency with the title bar drawn below, so Opacity is real.
    private double _windowAlpha = 1.0;

    private void ApplyWindowAlpha() => Opacity = Math.Clamp(_windowAlpha, 0.2, 1.0);

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2) { SetWidgetMode(!_widgetMode); e.Handled = true; return; }
        try { DragMove(); } catch { /* button already released */ }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void OnStartWithWindows(object sender, RoutedEventArgs e)
    {
        var wanted = StartWithWindowsItem.IsChecked;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) throw new IOException("Run key unavailable.");
            if (wanted)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) throw new IOException("Cannot resolve the exe path.");
                key.SetValue(RunValue, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(RunValue, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            StartWithWindowsItem.IsChecked = !wanted; // reflect what actually happened
            Status($"Could not change startup setting: {ex.Message}");
        }
    }

    private bool StartsWithWindows()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) != null;
        }
        catch { return false; }
    }

    // Widget preferences persist alongside the other settings.
    private void LoadWidgetSettings()
    {
        StartWithWindowsItem.IsChecked = StartsWithWindows();
        try
        {
            if (!File.Exists(WidgetSettingsPath)) return;
            foreach (var line in File.ReadAllLines(WidgetSettingsPath))
            {
                var parts = line.Split('=', 2);
                if (parts.Length != 2) continue;
                var value = parts[1].Trim();
                switch (parts[0].Trim().ToLowerInvariant())
                {
                    case "topmost":
                        Topmost = value == "1";
                        AlwaysOnTopItem.IsChecked = Topmost;
                        break;
                    case "opacity":
                        if (double.TryParse(value, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var o))
                            _windowAlpha = Math.Clamp(o, 0.2, 1.0);
                        break;
                    case "widget":
                        if (value == "1") Dispatcher.BeginInvoke(() => SetWidgetMode(true));
                        break;
                }
            }
        }
        catch { /* best-effort */ }
    }

    private void SaveWidgetSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(WidgetSettingsPath)!);
            File.WriteAllLines(WidgetSettingsPath, new[]
            {
                $"topmost={(Topmost ? 1 : 0)}",
                $"opacity={_windowAlpha.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}",
                $"widget={(_widgetMode ? 1 : 0)}",
            });
        }
        catch { /* best-effort */ }
    }

    // ---------------- Messages panel ----------------

    /// <summary>
    /// Messages arrive as data rather than pixels, because the system blanks
    /// Google Messages during any screen capture and that cannot be disabled
    /// without root. Replies go back out through the notification's own reply
    /// action, so they are sent by Messages itself and RCS still works.
    /// </summary>
    private void OnToggleMessages(object sender, RoutedEventArgs e)
    {
        bool opening = MessagesPanel.Visibility != Visibility.Visible;
        MessagesPanel.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
        MessagesTab.Visibility = opening ? Visibility.Collapsed : Visibility.Visible;
        if (opening)
        {
            FilePanel.Visibility = Visibility.Collapsed;   // one panel at a time
            FilesTab.Visibility = Visibility.Visible;
            if (_client.IsConnected) _client.RequestMessages();
        }
    }

    private void OnRefreshMessages(object sender, RoutedEventArgs e)
    {
        if (!_client.IsConnected) { MessagesStatus.Text = "Connect to the phone first."; return; }
        _client.RequestMessages();
        MessagesStatus.Text = "Fetching conversations on the phone…";
    }

    private void OnMessageReceived(PhoneMessage m) => Dispatcher.Invoke(() =>
    {
        // Don't disturb the history view; note it instead.
        if (_historyMode)
        {
            MessagesStatus.Text = $"New message from {m.Sender} — press Live to see it.";
            return;
        }

        // One row per conversation: a newer message replaces the older entry.
        for (int i = 0; i < MessageList.Items.Count; i++)
        {
            if (MessageList.Items[i] is MessageRow existing && existing.Message.Key == m.Key)
            {
                MessageList.Items.RemoveAt(i);
                break;
            }
        }
        MessageList.Items.Insert(0, new MessageRow(m));
        while (MessageList.Items.Count > 50) MessageList.Items.RemoveAt(MessageList.Items.Count - 1);

        if (MessagesPanel.Visibility != Visibility.Visible)
        {
            MessagesStatus.Text = $"{MessageList.Items.Count} conversations.";
        }
    });

    private void OnReplyResult(string key, bool sent) => Dispatcher.Invoke(() =>
    {
        MessagesStatus.Text = sent
            ? "Reply sent."
            : "Reply failed — the notification may have been dismissed on the phone.";
        if (sent) ReplyBox.Clear();
    });

    private void OnReplyKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OnSendReply(sender, e);
        e.Handled = true;
    }

    /// <summary>
    /// Two ways to send, chosen by where you are. A live notification can be
    /// replied to through the app that posted it, which keeps RCS working. Inside
    /// a history thread there is no notification left, so it goes as plain SMS.
    /// </summary>
    private void OnSendReply(object sender, RoutedEventArgs e)
    {
        if (!_client.IsConnected) { MessagesStatus.Text = "Connect to the phone first."; return; }
        var text = ReplyBox.Text.Trim();
        if (text.Length == 0) return;

        // Inside an open conversation: send to that thread.
        if (_msgView == MessagesView.Thread)
        {
            if (_openThreadAddress.Length == 0)
            {
                MessagesStatus.Text = "No number for this conversation.";
                return;
            }
            if (_openThreadAddress.Contains(','))
            {
                MessagesStatus.Text = "Group replies aren't supported — reply from a " +
                                      "notification, or on the phone.";
                return;
            }
            MessagesStatus.Text = $"Sending to {_openThreadName}…";
            _pendingSends.Enqueue((_openThreadAddress, text));
            _client.SendSms(_openThreadAddress, text);
            // Confirmation now waits on the radio, which takes seconds. Clear on
            // dispatch so the box is ready, and put the text back if it fails.
            ReplyBox.Clear();
            return;
        }

        // Live view: reply through the notification that carried the message.
        if (MessageList.SelectedItem is not MessageRow row)
        {
            MessagesStatus.Text = _msgView == MessagesView.Threads
                ? "Open a conversation first, then type to send."
                : "Select a message to reply to.";
            return;
        }
        if (!row.Message.CanReply)
        {
            MessagesStatus.Text = $"{row.Message.Sender} can't be replied to — " +
                                  "the notification is gone. Open the conversation instead.";
            return;
        }
        MessagesStatus.Text = $"Replying to {row.Message.Sender}…";
        _client.SendReply(row.Message.Key, text);
    }

    private void OnSmsSent(bool ok, string message) => Dispatcher.Invoke(() =>
    {
        if (!_pendingSends.TryDequeue(out var p))
        {
            MessagesStatus.Text = message;
            return;
        }
        // A failed send is not echoed: the local copy exists to mirror what the
        // network actually took, and showing a message that never went out is
        // worse than showing none.
        if (!ok)
        {
            MessagesStatus.Text = message;
            if (ReplyBox.Text.Length == 0) ReplyBox.Text = p.text;   // don't lose what they typed
            return;
        }
        MessagesStatus.Text = message;

        var echo = RecordSent(p.address, p.text);
        // Show it straight away if that conversation is still on screen.
        if (_msgView == MessagesView.Thread && ThreadKey(_openThreadAddress) == ThreadKey(p.address))
        {
            MessageList.Items.Add(new SmsRow(echo));
            MessageList.ScrollIntoView(MessageList.Items[^1]);
        }
    });

    // ---- SMS history ----
    // Live notifications and stored history share the one list. History is
    // read-only: replying still goes through a notification, because sending from
    // here would use SmsManager, which downgrades RCS conversations to plain SMS
    // and never appears in the phone's own thread.

    // Three views behind one list. The old design overloaded a single button to
    // mean History / Live / back-out-of-a-thread, which left no obvious way out
    // of an open conversation. Back is now its own control.
    private enum MessagesView { Live, Threads, Thread }

    private MessagesView _msgView = MessagesView.Live;
    private long _openThreadId;
    private string _openThreadName = "";
    private string _openThreadAddress = "";
    private readonly Dictionary<long, string> _threadAddresses = new();
    private readonly Dictionary<long, string> _threadNames = new();

    private bool _historyMode => _msgView != MessagesView.Live;

    private void OnShowSmsHistory(object sender, RoutedEventArgs e)
    {
        if (!_client.IsConnected) { MessagesStatus.Text = "Connect to the phone first."; return; }
        // Always jumps to the conversation list, from wherever you are.
        ShowThreadList();
    }

    /// <summary>One step back: a thread returns to the list, the list to Live.</summary>
    private void OnMessagesBack(object sender, RoutedEventArgs e)
    {
        if (_msgView == MessagesView.Thread) ShowThreadList();
        else ShowLiveMessages();
    }

    private void ShowThreadList()
    {
        _msgView = MessagesView.Threads;
        _openThreadId = 0;
        _openThreadName = "";
        _openThreadAddress = "";
        MessageList.Items.Clear();
        MessagesStatus.Text = "Loading conversations…";
        UpdateMessagesChrome();
        _client.RequestThreads();
    }

    private void ShowLiveMessages()
    {
        _msgView = MessagesView.Live;
        _openThreadId = 0;
        _openThreadName = "";
        _openThreadAddress = "";
        MessageList.Items.Clear();
        MessagesStatus.Text = "Live messages. Newest arrive at the top.";
        UpdateMessagesChrome();
        if (_client.IsConnected) _client.RequestMessages();
    }

    /// <summary>Header and buttons always say where you are and how to leave.</summary>
    private void UpdateMessagesChrome()
    {
        switch (_msgView)
        {
            case MessagesView.Live:
                MessagesHeading.Text = "Messages";
                MsgBackButton.Visibility = Visibility.Collapsed;
                SmsHistoryButton.Content = "History";
                break;
            case MessagesView.Threads:
                MessagesHeading.Text = "Conversations";
                MsgBackButton.Visibility = Visibility.Visible;
                MsgBackButton.ToolTip = "Back to live messages";
                SmsHistoryButton.Content = "Refresh";
                break;
            case MessagesView.Thread:
                MessagesHeading.Text = _openThreadName.Length > 0 ? _openThreadName : "Conversation";
                MsgBackButton.Visibility = Visibility.Visible;
                MsgBackButton.ToolTip = "Back to conversations";
                SmsHistoryButton.Content = "History";
                break;
        }
    }

    private void OnThreadsListed(List<SmsThread> threads) => Dispatcher.Invoke(() =>
    {
        if (!_historyMode) return;
        MessageList.Items.Clear();
        int hidden = 0;
        foreach (var t in ApplySentToList(threads))
        {
            if (IsHidden(t)) { hidden++; continue; }
            MessageList.Items.Add(new ThreadRow(t));
        }
        var shown = MessageList.Items.Count;
        MessagesStatus.Text = shown == 0
            ? (hidden > 0 ? $"All {hidden} conversations are hidden." : "No conversations found.")
            : $"{shown} conversations — double-click to open" +
              (hidden > 0 ? $", {hidden} hidden." : ". Right-click to hide one.");
    });

    // ---- Hidden conversations ----
    // The phone cannot tell us what is spam: the message store has no spam flag,
    // and reading the system blocked-number list throws
    // "Caller must be system, default dialer or default SMS app". So the block
    // list lives here instead, seeded from the phone's and extended by hand.
    private readonly HashSet<string> _hiddenNumbers = new();
    private static readonly string HiddenPath = Path.Combine(
        Path.GetDirectoryName(HistoryPath)!, "hidden-numbers.txt");

    private bool IsHidden(SmsThread t)
    {
        if (_hiddenNumbers.Count == 0) return false;
        var parts = t.Address.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(NormaliseNumber)
            .Where(p => p.Length > 0)
            .ToList();
        // A group is only hidden when every participant is hidden.
        return parts.Count > 0 && parts.All(_hiddenNumbers.Contains);
    }

    private static string NormaliseNumber(string s)
    {
        var digits = new string(s.Where(char.IsDigit).ToArray());
        return digits.Length > 10 ? digits[^10..] : digits;
    }

    private void OnHideConversation(object sender, RoutedEventArgs e)
    {
        if (MessageList.SelectedItem is not ThreadRow row)
        {
            MessagesStatus.Text = "Select a conversation to hide.";
            return;
        }
        var added = row.Thread.Address
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(NormaliseNumber)
            .Where(p => p.Length > 0)
            .ToList();
        if (added.Count == 0) { MessagesStatus.Text = "That conversation has no number to hide."; return; }
        foreach (var p in added) _hiddenNumbers.Add(p);
        SaveHiddenNumbers();
        MessageList.Items.Remove(row);
        MessagesStatus.Text = $"Hidden. {_hiddenNumbers.Count} numbers on the hide list.";
    }

    private void OnClearHidden(object sender, RoutedEventArgs e)
    {
        _hiddenNumbers.Clear();
        SaveHiddenNumbers();
        MessagesStatus.Text = "Hide list cleared.";
        if (_msgView == MessagesView.Threads) _client.RequestThreads();
    }

    private void LoadHiddenNumbers()
    {
        try
        {
            if (!File.Exists(HiddenPath)) return;
            foreach (var line in File.ReadAllLines(HiddenPath))
            {
                var n = NormaliseNumber(line);
                if (n.Length > 0) _hiddenNumbers.Add(n);
            }
        }
        catch { /* best-effort */ }
    }

    private void SaveHiddenNumbers()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HiddenPath)!);
            File.WriteAllLines(HiddenPath, _hiddenNumbers);
        }
        catch { /* best-effort */ }
    }

    private void OnThreadLoaded(long id, List<SmsMessage> messages) => Dispatcher.Invoke(() =>
    {
        _openThreadId = id;
        if (_threadAddresses.TryGetValue(id, out var address)) _openThreadAddress = address;
        if (_threadNames.TryGetValue(id, out var name)) _openThreadName = name;
        _msgView = MessagesView.Thread;
        UpdateMessagesChrome();
        MessageList.Items.Clear();
        var merged = MergeSent(_openThreadAddress, messages);
        foreach (var m in merged) MessageList.Items.Add(new SmsRow(m));
        MessagesStatus.Text = $"{merged.Count} messages — ◀ goes back.";
        if (MessageList.Items.Count > 0) MessageList.ScrollIntoView(MessageList.Items[^1]);
    });

    // ---- Locally echoed sends ----
    // Only the phone's default SMS app may write to the message store, so a
    // message sent from here never lands in the thread the phone (or this app)
    // reads back. Keep our own copy so an open conversation still reads as a
    // conversation. This is a PC-side echo: the phone's Messages app won't show it.
    private readonly List<SmsMessage> _sent = new();
    private const int SentLimit = 1000;
    private static readonly string SentPath = Path.Combine(
        Path.GetDirectoryName(HistoryPath)!, "sent-messages.tsv");
    // Sends in flight, oldest first. The phone answers in order on one worker,
    // so the first waiting send owns the next result. A single slot lost the
    // first message's echo and mislabelled the second whenever two were sent
    // before the first was confirmed.
    private readonly Queue<(string address, string text)> _pendingSends = new();

    /// <summary>
    /// Folds our echoed sends into what the phone returned, in time order. An
    /// echo is dropped when the store already has the same text at nearly the
    /// same time, so making this the default SMS app later wouldn't double up.
    /// </summary>
    private List<SmsMessage> MergeSent(string address, List<SmsMessage> stored)
    {
        var key = ThreadKey(address);
        if (key.Length == 0) return stored;
        var echoes = _sent
            .Where(e => ThreadKey(e.Sender) == key)
            .Where(e => !stored.Any(s =>
                s.Outgoing && s.Text == e.Text && Math.Abs(s.DateMs - e.DateMs) < 300_000))
            .ToList();
        if (echoes.Count == 0) return stored;
        return stored.Concat(echoes).OrderBy(m => m.DateMs).ToList();
    }

    /// <summary>
    /// The conversation list comes from the same store we can't write to, so a
    /// thread we just sent to would still show their last message as the latest.
    /// Put our own send back on top where it belongs, and re-sort.
    /// </summary>
    private List<SmsThread> ApplySentToList(List<SmsThread> threads)
    {
        if (_sent.Count == 0) return threads;
        var latest = _sent
            .GroupBy(m => ThreadKey(m.Sender))
            .ToDictionary(g => g.Key, g => g.MaxBy(m => m.DateMs)!);
        var updated = threads.Select(t =>
            latest.TryGetValue(ThreadKey(t.Address), out var m) && m.DateMs > t.DateMs
                ? t with { Snippet = m.Text, DateMs = m.DateMs, Outgoing = true }
                : t);
        return updated.OrderByDescending(t => t.DateMs).ToList();
    }

    /// <summary>Comparable key for an address, groups included (order-independent).</summary>
    private static string ThreadKey(string address) => string.Join(
        ",",
        address.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(NormaliseNumber)
            .Where(p => p.Length > 0)
            .OrderBy(p => p));

    private SmsMessage RecordSent(string address, string text)
    {
        // Sender carries the address so a message can be matched to its thread;
        // SmsRow shows "You" for outgoing messages, so it is never displayed.
        var m = new SmsMessage(text, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            true, "SENT", address);
        _sent.Add(m);
        // The file is rewritten whole on every send, so it can't grow forever.
        if (_sent.Count > SentLimit) _sent.RemoveRange(0, _sent.Count - SentLimit);
        SaveSent();
        return m;
    }

    private void LoadSent()
    {
        try
        {
            if (!File.Exists(SentPath)) return;
            foreach (var line in File.ReadAllLines(SentPath))
            {
                var f = line.Split('\t');
                if (f.Length < 3 || !long.TryParse(f[1], out var ms)) continue;
                _sent.Add(new SmsMessage(Unescape(f[2]), ms, true, "SENT", f[0]));
            }
            if (_sent.Count > SentLimit) _sent.RemoveRange(0, _sent.Count - SentLimit);
        }
        catch { /* best-effort */ }
    }

    private void SaveSent()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SentPath)!);
            File.WriteAllLines(SentPath, _sent.Select(
                m => $"{m.Sender}\t{m.DateMs}\t{Escape(m.Text)}"));
        }
        catch { /* best-effort */ }
    }

    // Tabs separate the fields and newlines separate the records, so a message
    // body containing either would split its own row.
    private static string Escape(string s) =>
        s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "").Replace("\n", "\\n");

    private static string Unescape(string s)
    {
        var b = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 >= s.Length) { b.Append(s[i]); continue; }
            b.Append(s[++i] switch { 't' => '\t', 'n' => '\n', var c => c });
        }
        return b.ToString();
    }

    private void OnMessageListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (MessageList.SelectedItem is ThreadRow row)
        {
            _openThreadName = row.Thread.Name.Length > 0 ? row.Thread.Name : row.Thread.Address;
            _openThreadAddress = row.Thread.Address;
            // Keyed by id so a reply that arrives after the user has opened a
            // different conversation is still matched to the right one.
            _threadAddresses[row.Thread.Id] = row.Thread.Address;
            _threadNames[row.Thread.Id] = _openThreadName;
            MessagesStatus.Text = $"Loading {_openThreadName}…";
            _client.RequestThread(row.Thread.Id);
        }
    }

    /// <summary>A conversation in the phone's SMS store.</summary>
    public sealed record ThreadRow(SmsThread Thread)
    {
        public string Tag => $"[{Thread.Kind}]  ";

        /// <summary>The bit drawn white: who the conversation is with.</summary>
        public string Who
        {
            get
            {
                var who = string.IsNullOrWhiteSpace(Thread.Name) ? Thread.Address : Thread.Name;
                return string.IsNullOrWhiteSpace(who) ? "(unknown)" : who;
            }
        }

        public string When => $"  ·  {Stamp(Thread.DateMs)}";

        public string Body => (Thread.Outgoing ? "You: " : "") + Thread.Snippet;

        public override string ToString() => $"{Tag}{Who}{When}\n{Body}";
    }

    /// <summary>One stored message.</summary>
    public sealed record SmsRow(SmsMessage Message)
    {
        public string Tag => $"[{Message.Kind}]  ";

        /// <summary>
        /// Name the actual sender. In a group thread "Them" is useless — several
        /// people are talking — so use the per-message sender when the phone
        /// supplied one, falling back to "Them" for older data.
        /// </summary>
        public string Who =>
            Message.Outgoing ? "You"
            : string.IsNullOrWhiteSpace(Message.Sender) ? "Them"
            : Message.Sender;
        public string When => $"  ·  {Stamp(Message.DateMs)}";
        public string Body => Message.Text;

        public override string ToString() => $"{Tag}{Who}{When}\n{Body}";
    }

    private static string Stamp(long ms)
    {
        if (ms <= 0) return "";
        var t = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
        return t.Date == DateTime.Today ? t.ToString("HH:mm") : t.ToString("d MMM HH:mm");
    }

    /// <summary>One conversation as shown in the list.</summary>
    public sealed record MessageRow(PhoneMessage Message)
    {
        // The app name is the source: Messages, Signal, WhatsApp and so on.
        private string Source => string.IsNullOrWhiteSpace(Message.App) ? "?" : Message.App;

        public string Tag => $"[{Source}]  ";

        public string Who =>
            string.IsNullOrWhiteSpace(Message.Sender) ? Source : Message.Sender;

        public string When
        {
            get
            {
                var when = Message.PostedAtMs > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(Message.PostedAtMs)
                        .LocalDateTime.ToString("HH:mm")
                    : "";
                return $"  ·  {when}" + (Message.CanReply ? "" : "  (no reply)");
            }
        }

        public string Body => Message.Text;

        public override string ToString() => $"{Tag}{Who}{When}\n{Body}";
    }

    // ---------------- File transfer panel ----------------

    private string _phonePath = "";
    private string? _phoneParent;
    private string _storageRoot = "/sdcard";
    private static readonly string SettingsPath = Path.Combine(
        Path.GetDirectoryName(HistoryPath)!, "folders.txt");

    private void OnToggleFiles(object sender, RoutedEventArgs e)
    {
        bool opening = FilePanel.Visibility != Visibility.Visible;
        FilePanel.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
        FilesTab.Visibility = opening ? Visibility.Collapsed : Visibility.Visible;
        if (opening && _client.IsConnected && PhoneFiles.Items.Count == 0)
            BrowsePhone(RemoteDirBox.Text.Trim().Length > 0 ? RemoteDirBox.Text.Trim() : _storageRoot);
    }

    private void BrowsePhone(string path)
    {
        if (!_client.IsConnected) { TransferStatus.Text = "Connect to the phone first."; return; }
        TransferStatus.Text = "Loading…";
        _client.ListFolder(path);
    }

    private void OnFolderListed(FolderListing listing) => Dispatcher.Invoke(() =>
    {
        _phonePath = listing.Path;
        _phoneParent = listing.Parent;
        RemoteDirBox.Text = listing.Path;
        PhoneFiles.Items.Clear();
        foreach (var entry in listing.Entries)
        {
            PhoneFiles.Items.Add(new FileRow(entry));
        }
        TransferStatus.Text = listing.Entries.Count == 0
            ? "Empty folder."
            : $"{listing.Entries.Count} items. Double-click a folder to open it.";
        SaveFolders();
    });

    private void OnPhoneUp(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_phoneParent)) BrowsePhone(_phoneParent!);
    }

    private void OnRemoteGo(object sender, RoutedEventArgs e) => BrowsePhone(RemoteDirBox.Text.Trim());

    private void OnRemoteDirKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        BrowsePhone(RemoteDirBox.Text.Trim());
        e.Handled = true;
    }

    private void OnPhoneFileOpen(object sender, MouseButtonEventArgs e)
    {
        if (PhoneFiles.SelectedItem is not FileRow row) return;
        if (row.Entry.IsDirectory) BrowsePhone(CombinePhone(_phonePath, row.Entry.Name));
        else OnDownload(sender, e);
    }

    private static string CombinePhone(string dir, string name) =>
        dir.EndsWith('/') ? dir + name : dir + "/" + name;

    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        if (!_client.IsConnected) { TransferStatus.Text = "Connect to the phone first."; return; }
        if (PhoneFiles.SelectedItem is not FileRow row)
        {
            TransferStatus.Text = "Select a file or folder on the phone to download.";
            return;
        }
        var local = LocalDirBox.Text.Trim();
        if (local.Length == 0) { TransferStatus.Text = "Choose a PC folder first."; return; }

        var phonePath = CombinePhone(_phonePath, row.Entry.Name);

        if (!row.Entry.IsDirectory)
        {
            _client.DownloadFolder = local;
            TransferStatus.Text = $"Downloading {row.Entry.Name}…";
            _client.DownloadFile(phonePath);
            return;
        }

        // Folder: enumerate on the phone, then pull each file into a mirrored tree.
        SetTransferButtons(false);
        try
        {
            TransferStatus.Text = $"Scanning {row.Entry.Name}…";
            var tree = await _client.GetTreeAsync(phonePath);
            if (tree.Files.Count == 0)
            {
                Directory.CreateDirectory(Path.Combine(local, row.Entry.Name));
                TransferStatus.Text = $"{row.Entry.Name} is empty — created the folder.";
                return;
            }

            var destRoot = Path.Combine(local, SafeName(row.Entry.Name));
            int done = 0;
            foreach (var file in tree.Files)
            {
                // Sanitise every segment: the phone may hold names that are legal
                // on Linux but illegal on Windows (: * ? " < > |), and a crafted
                // path must not be able to escape the destination folder.
                var rel = string.Join(Path.DirectorySeparatorChar,
                    file.RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                        .Where(seg => seg != "." && seg != "..")
                        .Select(SafeName));
                if (rel.Length == 0) continue;
                var destDir = Path.GetDirectoryName(Path.Combine(destRoot, rel)) ?? destRoot;
                Directory.CreateDirectory(destDir);
                TransferStatus.Text =
                    $"Downloading {row.Entry.Name} — {done + 1}/{tree.Files.Count}: {Path.GetFileName(rel)}";
                await _client.DownloadFileAsync(CombinePhone(phonePath, file.RelativePath), destDir);
                done++;
            }
            TransferStatus.Text = tree.Truncated
                ? $"Downloaded {done} files (list was capped at 5000)."
                : $"Downloaded {done} files into {row.Entry.Name}.";
        }
        catch (Exception ex)
        {
            TransferStatus.Text = $"Folder download failed: {ex.Message}";
        }
        finally
        {
            SetTransferButtons(true);
        }
    }

    private void SetTransferButtons(bool on)
    {
        DownloadButton.IsEnabled = on;
        UploadButton.IsEnabled = on;
        UploadFolderButton.IsEnabled = on;
    }

    private static string SafeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    private async void OnUploadFolder(object sender, RoutedEventArgs e)
    {
        if (!_client.IsConnected) { TransferStatus.Text = "Connect to the phone first."; return; }
        if (_phonePath.Length == 0) { TransferStatus.Text = "Open a phone folder first."; return; }

        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Send folder to phone",
            InitialDirectory = Directory.Exists(LocalDirBox.Text.Trim()) ? LocalDirBox.Text.Trim() : "",
        };
        if (dlg.ShowDialog() != true) return;

        var sourceRoot = dlg.FolderName;
        var folderName = new DirectoryInfo(sourceRoot).Name;
        var targetRoot = CombinePhone(_phonePath, folderName);

        SetTransferButtons(false);
        try
        {
            // Enumerating a deep tree can take seconds; keep it off the UI thread.
            TransferStatus.Text = $"Scanning {folderName}…";
            var files = await Task.Run(
                () => Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories));
            if (files.Length == 0)
            {
                TransferStatus.Text = $"{folderName} has no files to send.";
                return;
            }
            int done = 0;
            foreach (var file in files)
            {
                var rel = Path.GetRelativePath(sourceRoot, file);
                var relDir = Path.GetDirectoryName(rel);
                var phoneDir = string.IsNullOrEmpty(relDir)
                    ? targetRoot
                    : CombinePhone(targetRoot, relDir.Replace(Path.DirectorySeparatorChar, '/'));
                var label = Path.GetFileName(file);
                TransferStatus.Text = $"Uploading {folderName} — {done + 1}/{files.Length}: {label}";
                await Task.Run(() => _client.UploadFile(file, phoneDir));
                done++;
            }
            TransferStatus.Text = $"Uploaded {done} files into {folderName}.";
            BrowsePhone(_phonePath);
        }
        catch (Exception ex)
        {
            TransferStatus.Text = $"Folder upload failed: {ex.Message}";
        }
        finally
        {
            SetTransferButtons(true);
        }
    }

    private async void OnUpload(object sender, RoutedEventArgs e)
    {
        if (!_client.IsConnected) { TransferStatus.Text = "Connect to the phone first."; return; }
        if (_phonePath.Length == 0) { TransferStatus.Text = "Open a phone folder first."; return; }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Send to phone",
            Multiselect = true,
            InitialDirectory = Directory.Exists(LocalDirBox.Text.Trim()) ? LocalDirBox.Text.Trim() : "",
        };
        if (dlg.ShowDialog() != true) return;

        var files = dlg.FileNames;
        var target = _phonePath;
        SetTransferButtons(false);
        try
        {
            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                TransferStatus.Text = $"Uploading {name}…";
                await Task.Run(() => _client.UploadFile(file, target,
                    (done, total) => Dispatcher.BeginInvoke(() =>
                        TransferStatus.Text = $"Uploading {name} — {Percent(done, total)}")));
            }
            TransferStatus.Text = files.Length == 1
                ? $"Uploaded {Path.GetFileName(files[0])}."
                : $"Uploaded {files.Length} files.";
            BrowsePhone(target); // refresh so the new files show
        }
        catch (Exception ex)
        {
            TransferStatus.Text = $"Upload failed: {ex.Message}";
        }
        finally
        {
            SetTransferButtons(true);
        }
    }

    private static string Percent(long done, long total) =>
        total <= 0 ? "…" : $"{100.0 * done / total:0}%";

    private void OnTransferProgress(string name, long done, long total) =>
        Dispatcher.BeginInvoke(() => TransferStatus.Text = $"Downloading {name} — {Percent(done, total)}");

    private void OnTransferDone(string path) => Dispatcher.Invoke(() =>
    {
        TransferStatus.Text = path.Length > 0 ? $"Saved {Path.GetFileName(path)}" : "Transfer complete.";
    });

    private void OnTransferError(string message) =>
        Dispatcher.Invoke(() => TransferStatus.Text = message);

    private void OnBrowseLocal(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "PC folder for transfers",
            InitialDirectory = Directory.Exists(LocalDirBox.Text.Trim()) ? LocalDirBox.Text.Trim() : "",
        };
        if (dlg.ShowDialog() == true) LocalDirBox.Text = dlg.FolderName;
    }

    private void OnLocalDirChanged(object sender, TextChangedEventArgs e) => SaveFolders();

    // Local and remote folders persist between runs, next to the connection history.
    // Setting LocalDirBox.Text raises TextChanged -> SaveFolders, which used to
    // overwrite the file with the default before it had been read, losing the
    // saved folders on every launch. Suppress saving while loading.
    private bool _loadingFolders;

    private void LoadFolders()
    {
        _loadingFolders = true;
        try
        {
            LocalDirBox.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!File.Exists(SettingsPath)) return;
            var lines = File.ReadAllLines(SettingsPath);
            if (lines.Length > 0 && lines[0].Trim().Length > 0) LocalDirBox.Text = lines[0].Trim();
            if (lines.Length > 1 && lines[1].Trim().Length > 0) RemoteDirBox.Text = lines[1].Trim();
        }
        catch { /* best-effort */ }
        finally { _loadingFolders = false; }
    }

    private void SaveFolders()
    {
        if (_loadingFolders) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllLines(SettingsPath, new[] { LocalDirBox.Text.Trim(), RemoteDirBox.Text.Trim() });
        }
        catch { /* best-effort */ }
    }

    /// <summary>A phone file/folder as shown in the list.</summary>
    private sealed record FileRow(PhoneEntry Entry)
    {
        public override string ToString() => Entry.IsDirectory
            ? $"📁  {Entry.Name}"
            : $"📄  {Entry.Name}    {Human(Entry.Size)}";

        private static string Human(long bytes) => bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
            < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
        };
    }

    // ---------------- Navigation buttons ----------------

    private void OnBackClick(object sender, RoutedEventArgs e) { _recentsMode = false; _client.Key("back"); }
    private void OnHomeClick(object sender, RoutedEventArgs e) { _recentsMode = false; _client.Key("home"); }
    private void OnRecentsClick(object sender, RoutedEventArgs e) { _recentsMode = true; _client.Key("recents"); }
    private void OnNotificationsClick(object sender, RoutedEventArgs e) => _client.Key("notifications");
}


