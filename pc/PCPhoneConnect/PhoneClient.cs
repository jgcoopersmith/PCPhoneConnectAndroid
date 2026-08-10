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

    // Outbound message types (PC -> phone).
    private const byte OutJson = 0;
    private const byte OutFileData = 1;
    private const int ChunkSize = 128 * 1024;

    public event Action<DeviceHeader>? HeaderReceived;
    public event Action<byte[]>? FrameReceived;
    public event Action<string>? Disconnected;

    public bool IsConnected => _running;

    public void Connect(string host, int port, int timeoutMs = 5000)
    {
        Disconnect();
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            if (!tcp.ConnectAsync(host, port).Wait(timeoutMs))
            {
                tcp.Close();
                throw new TimeoutException($"No response from {host}:{port} within {timeoutMs / 1000}s.");
            }
        }
        catch (AggregateException ae)
        {
            tcp.Close();
            throw ae.InnerException ?? ae; // surface the real SocketException message
        }
        _tcp = tcp;
        _stream = tcp.GetStream();
        _running = true;
        var stream = _stream;
        _readThread = new Thread(() => ReadLoop(stream)) { IsBackground = true, Name = "phone-read" };
        _readThread.Start();
    }

    private void ReadLoop(NetworkStream stream)
    {
        var lenBuf = new byte[4];
        try
        {
            while (_running && ReferenceEquals(_stream, stream))
            {
                int type = stream.ReadByte();
                if (type < 0) break;
                ReadFully(stream, lenBuf, 4);
                int len = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];
                if (len < 0 || len > 32 * 1024 * 1024) break;
                var payload = new byte[len];
                ReadFully(stream, payload, len);

                switch (type)
                {
                    case 0:
                        var header = ParseHeader(payload);
                        if (header != null) HeaderReceived?.Invoke(header);
                        break;
                    case 1:
                        FrameReceived?.Invoke(payload);
                        break;
                    case 2:
                        HandleFileResponse(Encoding.UTF8.GetString(payload));
                        break;
                    case 3:
                        HandleFileChunk(payload);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            if (_running && ReferenceEquals(_stream, stream)) Disconnected?.Invoke(ex.Message);
        }
        finally
        {
            // Only report the disconnect if this is still the active connection —
            // a stale read thread from a replaced connection must stay silent.
            if (_running && ReferenceEquals(_stream, stream))
            {
                _running = false;
                Disconnected?.Invoke("Connection closed");
            }
        }
    }

    // ---- Inbound file responses ----

    private FileStream? _downloadStream;
    private long _downloadRemaining;
    private long _downloadTotal;
    private string? _downloadPath;

    /// <summary>Folder the next download is written into. Set before DownloadFile.</summary>
    public string DownloadFolder { get; set; } =
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public event Action<FolderListing>? FolderListed;
    public event Action<string, long, long>? TransferProgress; // name, done, total
    public event Action<string>? TransferDone;                 // local path or phone path
    public event Action<string>? TransferError;

    private void HandleFileResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            switch (root.GetProperty("r").GetString())
            {
                case "ls":
                    var entries = new List<PhoneEntry>();
                    if (root.TryGetProperty("entries", out var arr))
                    {
                        foreach (var e in arr.EnumerateArray())
                        {
                            entries.Add(new PhoneEntry(
                                e.GetProperty("n").GetString() ?? "",
                                e.TryGetProperty("d", out var d) && d.GetBoolean(),
                                e.TryGetProperty("s", out var s) ? s.GetInt64() : 0));
                        }
                    }
                    FolderListed?.Invoke(new FolderListing(
                        root.GetProperty("path").GetString() ?? "",
                        root.TryGetProperty("parent", out var p) && p.ValueKind == JsonValueKind.String
                            ? p.GetString()
                            : null,
                        entries));
                    break;

                case "getstart":
                    BeginDownload(
                        root.GetProperty("name").GetString() ?? "file",
                        root.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0);
                    break;

                case "getdone":
                    FinishDownload();
                    break;

                case "putdone":
                    TransferDone?.Invoke(root.TryGetProperty("path", out var pp)
                        ? pp.GetString() ?? "" : "");
                    break;

                case "err":
                    AbortDownload();
                    TransferError?.Invoke(root.TryGetProperty("m", out var m)
                        ? m.GetString() ?? "Unknown error" : "Unknown error");
                    break;
            }
        }
        catch
        {
            // malformed response — ignore
        }
    }

    private void BeginDownload(string name, long size)
    {
        AbortDownload();
        try
        {
            Directory.CreateDirectory(DownloadFolder);
            var target = UniqueLocalPath(DownloadFolder, name);
            _downloadStream = File.Create(target);
            _downloadPath = target;
            _downloadRemaining = size;
            _downloadTotal = size;
            if (size == 0) FinishDownload();
        }
        catch (Exception ex)
        {
            AbortDownload();
            TransferError?.Invoke($"Cannot save file: {ex.Message}");
        }
    }

    private void HandleFileChunk(byte[] data)
    {
        var s = _downloadStream;
        if (s == null) return;
        try
        {
            s.Write(data, 0, data.Length);
            _downloadRemaining -= data.Length;
            TransferProgress?.Invoke(
                Path.GetFileName(_downloadPath ?? ""),
                _downloadTotal - Math.Max(_downloadRemaining, 0),
                _downloadTotal);
        }
        catch (Exception ex)
        {
            AbortDownload();
            TransferError?.Invoke($"Write failed: {ex.Message}");
        }
    }

    private void FinishDownload()
    {
        var path = _downloadPath;
        try { _downloadStream?.Dispose(); } catch { }
        _downloadStream = null;
        _downloadPath = null;
        if (path != null) TransferDone?.Invoke(path);
    }

    private void AbortDownload()
    {
        try { _downloadStream?.Dispose(); } catch { }
        _downloadStream = null;
        _downloadPath = null;
        _downloadRemaining = 0;
    }

    private static string UniqueLocalPath(string folder, string name)
    {
        var safe = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(safe)) safe = "file";
        var candidate = Path.Combine(folder, safe);
        if (!File.Exists(candidate)) return candidate;
        var stem = Path.GetFileNameWithoutExtension(safe);
        var ext = Path.GetExtension(safe);
        for (int i = 2; ; i++)
        {
            candidate = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
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
                root.TryGetProperty("sh", out var sh) ? sh.GetInt32() : 0,
                root.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "",
                root.TryGetProperty("android", out var a) ? a.GetString() ?? "" : "",
                root.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.True,
                root.TryGetProperty("root", out var rt) ? rt.GetString() ?? "" : "");
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

    /// <summary>Replace the phone's focused field with exactly this text.</summary>
    public void SetText(string s) =>
        Send("{\"t\":\"settext\",\"s\":" + JsonSerializer.Serialize(s) + "}");

    // Continuous drag: a touch that stays down across down -> move* -> up.
    // durationMs is the real time the cursor took for this segment, so the phone
    // replays the stroke at the user's actual speed (needed for fling velocity).
    public void TouchDown(double x, double y) =>
        Send($"{{\"t\":\"down\",\"x\":{F(x)},\"y\":{F(y)}}}");

    public void TouchMove(double x, double y, int durationMs) =>
        Send($"{{\"t\":\"move\",\"x\":{F(x)},\"y\":{F(y)},\"d\":{durationMs}}}");

    public void TouchUp(double x, double y, int durationMs) =>
        Send($"{{\"t\":\"up\",\"x\":{F(x)},\"y\":{F(y)},\"d\":{durationMs}}}");

    // ---- File transfer ----

    /// <summary>List a folder on the phone; null/empty lists the storage root.</summary>
    public void ListFolder(string? path) =>
        Send("{\"t\":\"ls\",\"path\":" + JsonSerializer.Serialize(path ?? "") + "}");

    /// <summary>Ask the phone to send a file (arrives via FileStarted/FileDone).</summary>
    public void DownloadFile(string phonePath) =>
        Send("{\"t\":\"get\",\"path\":" + JsonSerializer.Serialize(phonePath) + "}");

    /// <summary>Upload a local file into <paramref name="phoneDir"/> on the phone.</summary>
    public void UploadFile(string localPath, string phoneDir, Action<long, long>? progress = null)
    {
        var info = new FileInfo(localPath);
        Send("{\"t\":\"put\",\"dir\":" + JsonSerializer.Serialize(phoneDir) +
             ",\"name\":" + JsonSerializer.Serialize(info.Name) +
             ",\"size\":" + info.Length + "}");

        using var fs = info.OpenRead();
        var buf = new byte[ChunkSize];
        long sent = 0;
        while (true)
        {
            int n = fs.Read(buf, 0, buf.Length);
            if (n <= 0) break;
            SendFramed(OutFileData, n == buf.Length ? buf : buf[..n]);
            sent += n;
            progress?.Invoke(sent, info.Length);
        }
    }

    private static string F(double v) =>
        v.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);

    private void Send(string json) => SendFramed(OutJson, Encoding.UTF8.GetBytes(json));

    /// <summary>PC -> phone frame: [1-byte type][4-byte big-endian length][payload].</summary>
    private void SendFramed(byte type, byte[] body)
    {
        var stream = _stream;
        if (stream == null || !_running) return;
        var frame = new byte[5 + body.Length];
        frame[0] = type;
        frame[1] = (byte)((body.Length >> 24) & 0xFF);
        frame[2] = (byte)((body.Length >> 16) & 0xFF);
        frame[3] = (byte)((body.Length >> 8) & 0xFF);
        frame[4] = (byte)(body.Length & 0xFF);
        Array.Copy(body, 0, frame, 5, body.Length);
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

public record DeviceHeader(
    string Name, int Width, int Height, int StreamWidth, int StreamHeight,
    string Model = "", string AndroidVersion = "",
    bool FileAccess = false, string StorageRoot = "");

/// <summary>One entry in a phone folder listing.</summary>
public record PhoneEntry(string Name, bool IsDirectory, long Size);

/// <summary>A phone folder: its path, its parent (null at the root) and contents.</summary>
public record FolderListing(string Path, string? Parent, List<PhoneEntry> Entries);
