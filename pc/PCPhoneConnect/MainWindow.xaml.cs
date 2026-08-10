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
        _pressTimer.Restart();
        ScreenImage.CaptureMouse();
    }

    private void OnScreenMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_client.IsConnected) { _downNorm = null; return; }
        ScreenImage.ReleaseMouseCapture();
        _pressTimer.Stop();

        var upPoint = e.GetPosition(ScreenImage);
        var upNorm = ToNormalized(upPoint);
        var start = _downNorm;
        _downNorm = null;
        if (start == null) return;
        // If the release fell outside the image, fall back to the press point.
        var end = upNorm ?? start.Value;

        double dxPix = (end.x - start.Value.x) * _srcW;
        double dyPix = (end.y - start.Value.y) * _srcH;
        double moved = Math.Sqrt(dxPix * dxPix + dyPix * dyPix);
        long ms = _pressTimer.ElapsedMilliseconds;

        if (moved < TapMovePixels)
        {
            if (ms >= 500) _client.LongPress(start.Value.x, start.Value.y, (int)Math.Min(ms, 2000));
            else _client.Tap(start.Value.x, start.Value.y);
        }
        else
        {
            int dur = (int)Math.Clamp(ms, 50, 900);
            _client.Swipe(start.Value.x, start.Value.y, end.x, end.y, dur);
        }
    }

    private void OnScreenRightUp(object sender, MouseButtonEventArgs e)
    {
        if (_client.IsConnected) _client.Key("back");
    }

    /// <summary>
    /// Mouse wheel → vertical swipe, so scrolling works in browsers, Reddit, feeds, etc.
    /// Wheel up scrolls the page up (finger swipes down); wheel down scrolls down
    /// (finger swipes up). The swipe runs at the cursor's X so split layouts scroll
    /// the pane under the pointer.
    /// </summary>
    private void OnScreenWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_client.IsConnected) return;

        var n = ToNormalized(e.GetPosition(ScreenImage));
        double cx = Math.Clamp(n?.x ?? 0.5, 0.05, 0.95);

        const double distance = 0.45; // fraction of screen height per notch
        double half = distance / 2;

        double y1, y2;
        if (e.Delta < 0) // wheel down → scroll page down → swipe up
        {
            y1 = 0.5 + half;
            y2 = 0.5 - half;
        }
        else // wheel up → scroll page up → swipe down
        {
            y1 = 0.5 - half;
            y2 = 0.5 + half;
        }

        // A short duration gives a flick with a little momentum, like a real scroll.
        _client.Swipe(cx, y1, cx, y2, 90);
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!_client.IsConnected) return;
        switch (e.Key)
        {
            case Key.Escape: _client.Key("back"); e.Handled = true; break;
            case Key.Home: _client.Key("home"); e.Handled = true; break;
        }
    }

    // ---------------- Navigation buttons ----------------

    private void OnBackClick(object sender, RoutedEventArgs e) => _client.Key("back");
    private void OnHomeClick(object sender, RoutedEventArgs e) => _client.Key("home");
    private void OnRecentsClick(object sender, RoutedEventArgs e) => _client.Key("recents");
    private void OnNotificationsClick(object sender, RoutedEventArgs e) => _client.Key("notifications");
}
