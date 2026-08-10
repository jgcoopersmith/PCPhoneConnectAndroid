using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

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
        Closed += (_, _) => _client.Dispose();
        PreviewKeyDown += OnKeyDown;
        Loaded += OnLoaded;
        LoadHistory();
        LoadFolders();
        HistoryList.ItemsSource = _history;

        // Read the version from the assembly so the badge can't drift from the
        // csproj. 1.50 ships as 1.50.0.0, so trim the trailing zero parts.
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (v != null) VersionText.Text = $"v{v.Major}.{v.Minor:00}";

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
        // Release capture FIRST and unconditionally. Returning early while the
        // image still holds capture swallows every later mouse event in the
        // window — the whole app looks frozen until it is restarted.
        ScreenImage.ReleaseMouseCapture();
        if (!_client.IsConnected) { _downNorm = null; _dragging = false; return; }
        _pressTimer.Stop();

        var upPoint = e.GetPosition(ScreenImage);
        var start = _downNorm;
        _downNorm = null;
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
    private void OnScreenLostCapture(object sender, MouseEventArgs e)
    {
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
        if (!_client.IsConnected) return;
        if (e.Key == Key.Enter)
        {
            _client.SetText(TypeBox.Text);   // make sure the field has the final text
            _client.Key("enter");            // fire the field's Search/Send/Go action
            ClearTypeBoxLocal();
            e.Handled = true;
        }
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
