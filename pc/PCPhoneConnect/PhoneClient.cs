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
///   type 0 = JSON header, 1 = JPEG frame, 2 = JSON file response, 3 = file chunk.
/// Wire format (PC -> phone): [1-byte type][4-byte big-endian length][payload]
///   type 0 = UTF-8 JSON control, 1 = raw upload chunk.
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
        // Bound writes so a wedged phone can't block a sender indefinitely, and
        // enable keepalive so a device that vanishes without a FIN is detected
        // instead of leaving the client stuck in "connected".
        tcp.SendTimeout = 15000;
        try { tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); }
        catch { /* not fatal */ }
        _tcp = tcp;
        _disconnectReported = 0;
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
            if (ReferenceEquals(_stream, stream)) ReportDisconnect(ex.Message);
        }
        finally
        {
            // Only report the disconnect if this is still the active connection —
            // a stale read thread from a replaced connection must stay silent.
            if (ReferenceEquals(_stream, stream)) ReportDisconnect("Connection closed");
        }
    }

    /// <summary>
    /// Tear down once per connection. Fires Disconnected a single time so the real
    /// socket error isn't overwritten by a follow-up "Connection closed", and
    /// faults anything awaiting a transfer so folder copies don't hang forever.
    /// </summary>
    private void ReportDisconnect(string reason)
    {
        if (Interlocked.Exchange(ref _disconnectReported, 1) != 0) return;
        _running = false;
        FailPendingTransfers(reason);
        Disconnected?.Invoke(reason);
    }

    private void FailPendingTransfers(string reason)
    {
        AbortDownload();
        var ex = new IOException(reason);
        Interlocked.Exchange(ref _treeTcs, null)?.TrySetException(ex);
        Interlocked.Exchange(ref _getTcs, null)?.TrySetException(ex);
    }

    // ---- Inbound file responses ----

    private FileStream? _downloadStream;
    private long _downloadRemaining;
    private long _downloadTotal;
    private string? _downloadPath;

    // Pending awaits for the async (folder-walking) transfer paths.
    private TaskCompletionSource<string>? _getTcs;
    private TaskCompletionSource<FolderTree>? _treeTcs;
    private int _disconnectReported;

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

                case "tree":
                    var treeFiles = new List<TreeFile>();
                    if (root.TryGetProperty("files", out var tf))
                    {
                        foreach (var e in tf.EnumerateArray())
                        {
                            treeFiles.Add(new TreeFile(
                                e.GetProperty("p").GetString() ?? "",
                                e.TryGetProperty("s", out var ts) ? ts.GetInt64() : 0));
                        }
                    }
                    var tree = new FolderTree(
                        root.GetProperty("root").GetString() ?? "",
                        root.TryGetProperty("name", out var tn) ? tn.GetString() ?? "" : "",
                        root.TryGetProperty("bytes", out var tb) ? tb.GetInt64() : 0,
                        root.TryGetProperty("truncated", out var tr) && tr.GetBoolean(),
                        treeFiles);
                    Interlocked.Exchange(ref _treeTcs, null)?.TrySetResult(tree);
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
                    var msg = root.TryGetProperty("m", out var m)
                        ? m.GetString() ?? "Unknown error" : "Unknown error";
                    // Fail whichever async operation is waiting, then report.
                    Interlocked.Exchange(ref _treeTcs, null)?.TrySetException(new IOException(msg));
                    Interlocked.Exchange(ref _getTcs, null)?.TrySetException(new IOException(msg));
                    TransferError?.Invoke(msg);
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
        var short_ = _downloadRemaining > 0;
        try { _downloadStream?.Dispose(); } catch { }
        _downloadStream = null;
        _downloadPath = null;

        if (path == null)
        {
            // getdone with nothing open: the file never started or was aborted.
            Interlocked.Exchange(ref _getTcs, null)
                ?.TrySetException(new IOException("Download did not complete."));
            return;
        }
        if (short_)
        {
            // Truncated transfer — don't pass a partial file off as complete.
            var msg = $"{Path.GetFileName(path)} is incomplete ({_downloadRemaining} bytes missing).";
            _downloadRemaining = 0;
            Interlocked.Exchange(ref _getTcs, null)?.TrySetException(new IOException(msg));
            TransferError?.Invoke(msg);
            return;
        }
        Interlocked.Exchange(ref _getTcs, null)?.TrySetResult(path);
        TransferDone?.Invoke(path);
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
                root.TryGetProperty("root", out var rt) ? rt.GetString() ?? "" : "",
                // Older builds don't report this; assume control works so we don't
                // warn about a phone that is actually fine.
                !root.TryGetProperty("control", out var ct) || ct.ValueKind != JsonValueKind.False);
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

    /// <summary>Ask the phone to send a file (arrives via TransferProgress/Done).</summary>
    public void DownloadFile(string phonePath) =>
        Send("{\"t\":\"get\",\"path\":" + JsonSerializer.Serialize(phonePath) + "}");

    /// <summary>Recursively enumerate a phone folder.</summary>
    public Task<FolderTree> GetTreeAsync(string phonePath)
    {
        var tcs = new TaskCompletionSource<FolderTree>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _treeTcs = tcs;
        Send("{\"t\":\"tree\",\"path\":" + JsonSerializer.Serialize(phonePath) + "}");
        return tcs.Task;
    }

    /// <summary>
    /// Download one file into <paramref name="localFolder"/> and complete when the
    /// phone signals the end of the file, so a folder can be pulled sequentially.
    /// </summary>
    public Task<string> DownloadFileAsync(string phonePath, string localFolder)
    {
        var tcs = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        // DownloadFolder and the pending await are single global slots, so a second
        // concurrent download would misfile the in-flight file and complete the
        // wrong await. Only one at a time.
        if (Interlocked.CompareExchange(ref _getTcs, tcs, null) != null)
            return Task.FromException<string>(
                new InvalidOperationException("Another download is already in progress."));
        DownloadFolder = localFolder;
        DownloadFile(phonePath);
        return tcs.Task;
    }

    /// <summary>Upload a local file into <paramref name="phoneDir"/> on the phone.</summary>
    public void UploadFile(string localPath, string phoneDir, Action<long, long>? progress = null)
    {
        var info = new FileInfo(localPath);
        if (!SendFramed(OutJson, Encoding.UTF8.GetBytes(
                "{\"t\":\"put\",\"dir\":" + JsonSerializer.Serialize(phoneDir) +
                ",\"name\":" + JsonSerializer.Serialize(info.Name) +
                ",\"size\":" + info.Length + "}")))
        {
            throw new IOException("Connection lost before the upload started.");
        }

        using var fs = info.OpenRead();
        var buf = new byte[ChunkSize];
        long sent = 0;
        while (true)
        {
            int n = fs.Read(buf, 0, buf.Length);
            if (n <= 0) break;
            if (!SendFramed(OutFileData, n == buf.Length ? buf : buf[..n]))
                throw new IOException($"Connection lost after {sent} of {info.Length} bytes.");
            sent += n;
            progress?.Invoke(sent, info.Length);
        }
        // The declared size is what the phone waits for; a file that shrank
        // mid-read would otherwise leave the phone's writer hanging.
        if (sent != info.Length)
            throw new IOException($"{info.Name} changed while sending ({sent} of {info.Length} bytes).");
    }

    private static string F(double v) =>
        v.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);

    private void Send(string json) => SendFramed(OutJson, Encoding.UTF8.GetBytes(json));

    /// <summary>
    /// PC -> phone frame: [1-byte type][4-byte big-endian length][payload].
    /// Returns false if the frame could not be written, so file transfers can
    /// fail loudly instead of reporting a phantom success.
    /// </summary>
    private bool SendFramed(byte type, byte[] body)
    {
        var stream = _stream;
        if (stream == null || !_running) return false;
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
            return true;
        }
        catch
        {
            // The read loop reports the disconnect; callers that care (file
            // transfer) surface the failure via the false return.
            return false;
        }
    }

    public void Disconnect()
    {
        _running = false;
        try { _stream?.Close(); } catch { }
        try { _tcp?.Close(); } catch { }
        _stream = null;
        _tcp = null;
        // Close any half-written download and release awaiters, otherwise a later
        // download appends to the stale handle and folder pulls hang forever.
        FailPendingTransfers("Disconnected.");
    }

    public void Dispose() => Disconnect();
}

public record DeviceHeader(
    string Name, int Width, int Height, int StreamWidth, int StreamHeight,
    string Model = "", string AndroidVersion = "",
    bool FileAccess = false, string StorageRoot = "", bool ControlEnabled = true);

/// <summary>One entry in a phone folder listing.</summary>
public record PhoneEntry(string Name, bool IsDirectory, long Size);

/// <summary>One file inside a recursively listed folder, path relative to its root.</summary>
public record TreeFile(string RelativePath, long Size);

/// <summary>A recursively listed phone folder.</summary>
public record FolderTree(string Root, string Name, long Bytes, bool Truncated, List<TreeFile> Files);

/// <summary>A phone folder: its path, its parent (null at the root) and contents.</summary>
public record FolderListing(string Path, string? Parent, List<PhoneEntry> Entries);
