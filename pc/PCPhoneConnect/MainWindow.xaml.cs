using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace PCPhoneConnect;

public partial class MainWindow : Window
{
    private readonly PhoneClient _client = new();
    private int _srcW, _srcH;

    // Pointer-gesture tracking
    private Point? _downPoint;          // relative to ScreenImage
    private (double x, double y)? _downNorm;
    private readonly Stopwatch _pressTimer = new();

    // Live drag ("grab and pull"): once the cursor moves past the tap threshold
    // while held, we stream a continuous touch (down -> move* -> up).
    private bool _dragging;
    private readonly Stopwatch _moveThrottle = new();
    private const double MoveIntervalMs = 15; // cap move messages to ~66/s

    // While the phone is showing Recent apps (opened via the Recents button/key),
    // the wheel scrolls the carousel horizontally instead of vertically.
    private bool _recentsMode;

    private const double TapMovePixels = 14; // device-space movement below this = tap

    public MainWindow()
    {
        InitializeComponent();
        _client.HeaderReceived += OnHeader;
        _client.FrameReceived += OnFrame;
        _client.Disconnected += OnDisconnected;
        Closed += (_, _) => _client.Dispose();
        PreviewKeyDown += OnKeyDown;
        Loaded += OnLoaded;
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

    private void OnConnectClick(object sender, RoutedEventArgs e)
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
        try
        {
            _client.Connect(host, port);
            ConnectButton.Content = "Disconnect";
            Status($"Connected to {host}:{port}. Waiting for screen…");
        }
        catch (Exception ex)
        {
            Status($"Could not connect: {ex.Message}");
        }
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
        Status($"Mirroring {h.Name} ({h.Width}×{h.Height}). Click to tap, drag to swipe, right-click = Back.");
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
        ConnectButton.Content = "Connect";
        SetNavEnabled(false);
        ScreenImage.Source = null;
        Placeholder.Visibility = Visibility.Visible;
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

    private void OnScreenMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_client.IsConnected) return;
        var p = e.GetPosition(ScreenImage);
        _downPoint = p;
        _downNorm = ToNormalized(p);
        _dragging = false;
        _pressTimer.Restart();
        _moveThrottle.Restart();
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
            _moveThrottle.Restart();
        }

        if (_moveThrottle.Elapsed.TotalMilliseconds < MoveIntervalMs) return;
        _moveThrottle.Restart();
        _client.TouchMove(cur.Value.x, cur.Value.y);
    }

    private void OnScreenMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_client.IsConnected) { _downNorm = null; _dragging = false; return; }
        ScreenImage.ReleaseMouseCapture();
        _pressTimer.Stop();

        var upPoint = e.GetPosition(ScreenImage);
        var upNorm = ToNormalized(upPoint);
        var start = _downNorm;
        _downNorm = null;
        if (start == null) { _dragging = false; return; }
        var end = upNorm ?? start.Value;

        // A live drag was in progress — release the continuous touch.
        if (_dragging)
        {
            _dragging = false;
            _client.TouchUp(end.x, end.y);
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
        const double distance = 0.45; // fraction of the screen per notch
        double half = distance / 2;

        if (_recentsMode)
        {
            double cy = Math.Clamp(n?.y ?? 0.5, 0.05, 0.95);
            // Wheel down → move forward through recents → swipe left; wheel up → right.
            double x1 = e.Delta < 0 ? 0.5 + half : 0.5 - half;
            double x2 = e.Delta < 0 ? 0.5 - half : 0.5 + half;
            _client.Swipe(x1, cy, x2, cy, 90);
        }
        else
        {
            double cx = Math.Clamp(n?.x ?? 0.5, 0.05, 0.95);
            // Wheel down → scroll page down → swipe up; wheel up → swipe down.
            double y1 = e.Delta < 0 ? 0.5 + half : 0.5 - half;
            double y2 = e.Delta < 0 ? 0.5 - half : 0.5 + half;
            _client.Swipe(cx, y1, cx, y2, 90);
        }

        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!_client.IsConnected) return;
        switch (e.Key)
        {
            case Key.Escape: _recentsMode = false; _client.Key("back"); e.Handled = true; break;
            case Key.Home: _recentsMode = false; _client.Key("home"); e.Handled = true; break;
        }
    }

    // ---------------- Navigation buttons ----------------

    private void OnBackClick(object sender, RoutedEventArgs e) { _recentsMode = false; _client.Key("back"); }
    private void OnHomeClick(object sender, RoutedEventArgs e) { _recentsMode = false; _client.Key("home"); }
    private void OnRecentsClick(object sender, RoutedEventArgs e) { _recentsMode = true; _client.Key("recents"); }
    private void OnNotificationsClick(object sender, RoutedEventArgs e) => _client.Key("notifications");
}
