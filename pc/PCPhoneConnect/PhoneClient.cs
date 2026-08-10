using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace PCPhoneConnect;

/// <summary>
/// TCP client for a PC Phone Connect Android agent. Receives a JSON header then
/// a stream of JPEG frames, and sends back length-prefixed JSON control messages.
///
/// Wire format (phone -> PC): [1-byte type][4-byte big-endian length][payload]
///   type 0 = UTF-8 JSON header, type 1 = JPEG frame.
/// Wire format (PC -> phone): [4-byte big-endian length][UTF-8 JSON].
/// </summary>
public sealed class PhoneClient : IDisposable
{
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private Thread? _readThread;
    private readonly object _writeLock = new();
    private volatile bool _running;

    public event Action<DeviceHeader>? HeaderReceived;
    public event Action<byte[]>? FrameReceived;
    public event Action<string>? Disconnected;

    public bool IsConnected => _running;

    public void Connect(string host, int port)
    {
        Disconnect();
        _tcp = new TcpClient();
        _tcp.NoDelay = true;
        _tcp.Connect(host, port);
        _stream = _tcp.GetStream();
        _running = true;
        _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "phone-read" };
        _readThread.Start();
    }

    private void ReadLoop()
    {
        var stream = _stream!;
        var lenBuf = new byte[4];
        try
        {
            while (_running)
            {
                int type = stream.ReadByte();
                if (type < 0) break;
                ReadFully(stream, lenBuf, 4);
                int len = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];
                if (len < 0 || len > 32 * 1024 * 1024) break;
                var payload = new byte[len];
                ReadFully(stream, payload, len);

                if (type == 0)
                {
                    var header = ParseHeader(payload);
                    if (header != null) HeaderReceived?.Invoke(header);
                }
                else if (type == 1)
                {
                    FrameReceived?.Invoke(payload);
                }
            }
        }
        catch (Exception ex)
        {
            if (_running) Disconnected?.Invoke(ex.Message);
        }
        finally
        {
            if (_running)
            {
                _running = false;
                Disconnected?.Invoke("Connection closed");
            }
        }
    }

    private static DeviceHeader? ParseHeader(byte[] payload)
    {
        try
        {
            var json = Encoding.UTF8.GetString(payload);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new DeviceHeader(
                root.TryGetProperty("name", out var n) ? n.GetString() ?? "Android" : "Android",
                root.TryGetProperty("w", out var w) ? w.GetInt32() : 0,
                root.TryGetProperty("h", out var h) ? h.GetInt32() : 0,
                root.TryGetProperty("sw", out var sw) ? sw.GetInt32() : 0,
                root.TryGetProperty("sh", out var sh) ? sh.GetInt32() : 0);
        }
        catch
        {
            return null;
        }
    }

    private static void ReadFully(Stream stream, byte[] buffer, int count)
    {
        int read = 0;
        while (read < count)
        {
            int r = stream.Read(buffer, read, count - read);
            if (r <= 0) throw new EndOfStreamException();
            read += r;
        }
    }

    // ---- Control commands (coordinates are normalized 0..1) ----

    public void Tap(double x, double y) =>
        Send($"{{\"t\":\"tap\",\"x\":{F(x)},\"y\":{F(y)}}}");

    public void LongPress(double x, double y, int durationMs = 600) =>
        Send($"{{\"t\":\"long\",\"x\":{F(x)},\"y\":{F(y)},\"dur\":{durationMs}}}");

    public void Swipe(double x1, double y1, double x2, double y2, int durationMs = 200) =>
        Send($"{{\"t\":\"swipe\",\"x1\":{F(x1)},\"y1\":{F(y1)},\"x2\":{F(x2)},\"y2\":{F(y2)},\"dur\":{durationMs}}}");

    public void Key(string key) =>
        Send($"{{\"t\":\"key\",\"k\":\"{key}\"}}");

    /// <summary>Insert text at the cursor of the phone's focused field.</summary>
    public void Text(string s) =>
        Send("{\"t\":\"text\",\"s\":" + JsonSerializer.Serialize(s) + "}");

    // Continuous drag: a touch that stays down across down -> move* -> up.
    // durationMs is the real time the cursor took for this segment, so the phone
    // replays the stroke at the user's actual speed (needed for fling velocity).
    public void TouchDown(double x, double y) =>
        Send($"{{\"t\":\"down\",\"x\":{F(x)},\"y\":{F(y)}}}");

    public void TouchMove(double x, double y, int durationMs) =>
        Send($"{{\"t\":\"move\",\"x\":{F(x)},\"y\":{F(y)},\"d\":{durationMs}}}");

    public void TouchUp(double x, double y, int durationMs) =>
        Send($"{{\"t\":\"up\",\"x\":{F(x)},\"y\":{F(y)},\"d\":{durationMs}}}");

    private static string F(double v) =>
        v.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);

    private void Send(string json)
    {
        var stream = _stream;
        if (stream == null || !_running) return;
        var body = Encoding.UTF8.GetBytes(json);
        var frame = new byte[4 + body.Length];
        frame[0] = (byte)((body.Length >> 24) & 0xFF);
        frame[1] = (byte)((body.Length >> 16) & 0xFF);
        frame[2] = (byte)((body.Length >> 8) & 0xFF);
        frame[3] = (byte)(body.Length & 0xFF);
        Array.Copy(body, 0, frame, 4, body.Length);
        try
        {
            lock (_writeLock)
            {
                stream.Write(frame, 0, frame.Length);
                stream.Flush();
            }
        }
        catch
        {
            // ignore transient write failures; the read loop reports disconnects
        }
    }

    public void Disconnect()
    {
        _running = false;
        try { _stream?.Close(); } catch { }
        try { _tcp?.Close(); } catch { }
        _stream = null;
        _tcp = null;
    }

    public void Dispose() => Disconnect();
}

public record DeviceHeader(string Name, int Width, int Height, int StreamWidth, int StreamHeight);
