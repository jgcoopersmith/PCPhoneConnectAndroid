package com.j0ker.pcphoneconnect

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.PixelFormat
import android.hardware.display.DisplayManager
import android.hardware.display.VirtualDisplay
import android.media.ImageReader
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.util.DisplayMetrics
import android.view.WindowManager
import org.json.JSONObject
import java.io.BufferedInputStream
import java.io.ByteArrayOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.atomic.AtomicReference

/**
 * Captures the screen through MediaProjection and serves JPEG frames over a TCP
 * socket, while accepting control messages back from the PC on the same socket.
 *
 * Wire format (phone -> PC): repeated [1-byte type][4-byte big-endian length][payload]
 *   type 0 = UTF-8 JSON header (sent once per client)
 *   type 1 = JPEG frame
 *
 * Wire format (PC -> phone): repeated [4-byte big-endian length][UTF-8 JSON]
 *   {"t":"tap","x":0.5,"y":0.5}                     normalized 0..1
 *   {"t":"long","x":..,"y":..,"dur":600}
 *   {"t":"swipe","x1":..,"y1":..,"x2":..,"y2":..,"dur":200}
 *   {"t":"key","k":"back|home|recents|notifications|lock|power"}
 */
class StreamService : Service() {

    private var projection: MediaProjection? = null
    private var virtualDisplay: VirtualDisplay? = null
    private var imageReader: ImageReader? = null
    private var serverSocket: ServerSocket? = null

    @Volatile private var running = false
    private var port = DEFAULT_PORT

    // Real (unscaled) display size — used to map normalized control coords to pixels.
    private var realWidth = 0
    private var realHeight = 0
    // Scaled capture/stream size.
    private var streamWidth = 0
    private var streamHeight = 0

    private val latestJpeg = AtomicReference<ByteArray?>(null)
    private val frameSeq = AtomicLong(0)

    private var reusableBitmap: Bitmap? = null
    private val main = Handler(Looper.getMainLooper())

    // File server and socket for the currently connected PC, if any.
    @Volatile private var fileTransfer: FileTransfer? = null
    @Volatile private var clientSocket: Socket? = null
    private var captureThread: android.os.HandlerThread? = null

    // Framed writer for the connected PC, so control handlers can reply.
    @Volatile private var sender: ((Int, ByteArray) -> Unit)? = null

    // Provider queries can take a moment; keep them off the control thread so
    // taps and swipes stay responsive while history loads.
    private val smsHistory by lazy { SmsHistory(this) }
    private val smsWorker by lazy {
        java.util.concurrent.Executors.newSingleThreadExecutor { r ->
            Thread(r, "pc-sms").apply { isDaemon = true }
        }
    }

    private fun sendFramedSafe(type: Int, payload: ByteArray) {
        try { sender?.invoke(type, payload) } catch (_: Throwable) { }
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        // A sticky restart redelivers a null intent. There is no projection token
        // to recover, so stop instead of lingering as a zombie service.
        if (intent == null) {
            stopEverything()
            return START_NOT_STICKY
        }
        when (intent.action) {
            ACTION_STOP -> {
                stopEverything()
                return START_NOT_STICKY
            }
            ACTION_START -> {
                port = intent.getIntExtra(EXTRA_PORT, DEFAULT_PORT)
                val resultCode = intent.getIntExtra(EXTRA_RESULT_CODE, 0)
                val data = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU)
                    intent.getParcelableExtra(EXTRA_RESULT_DATA, Intent::class.java)
                else
                    @Suppress("DEPRECATION") intent.getParcelableExtra(EXTRA_RESULT_DATA)
                if (data == null) {
                    stopSelf()
                    return START_NOT_STICKY
                }
                startForegroundNotification()
                startProjection(resultCode, data)
            }
        }
        // Not sticky: the MediaProjection token cannot survive a process restart,
        // so a relaunched service could never resume capture anyway.
        return START_NOT_STICKY
    }

    private fun startProjection(resultCode: Int, data: Intent) {
        measureDisplay()
        val mgr = getSystemService(Context.MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
        val proj = mgr.getMediaProjection(resultCode, data)
        if (proj == null) {
            log("Failed to obtain MediaProjection")
            stopEverything()
            return
        }
        proj.registerCallback(object : MediaProjection.Callback() {
            override fun onStop() {
                log("Projection stopped by system")
                stopEverything()
            }
        }, main)
        projection = proj

        // Frame capture, the pixel copy and JPEG encoding are all expensive; run
        // them on a dedicated thread so they never stutter the UI or delay the
        // gesture dispatch that shares the main looper.
        val thread = android.os.HandlerThread("pc-capture").apply { start() }
        captureThread = thread
        val captureHandler = Handler(thread.looper)

        val reader = ImageReader.newInstance(
            streamWidth, streamHeight, PixelFormat.RGBA_8888, 2
        )
        reader.setOnImageAvailableListener({ r -> onFrame(r) }, captureHandler)
        imageReader = reader

        virtualDisplay = proj.createVirtualDisplay(
            "PCPhoneConnect",
            streamWidth, streamHeight, displayDensity(),
            DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR,
            reader.surface, null, null
        )

        running = true
        Thread({ acceptLoop() }, "pc-accept").start()
        log("Streaming ${streamWidth}x$streamHeight (device ${realWidth}x$realHeight) on port $port")
        notifyState()
    }

    private fun measureDisplay() {
        val wm = getSystemService(Context.WINDOW_SERVICE) as WindowManager
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            val b = wm.currentWindowMetrics.bounds
            realWidth = b.width()
            realHeight = b.height()
        } else {
            val dm = DisplayMetrics()
            @Suppress("DEPRECATION") wm.defaultDisplay.getRealMetrics(dm)
            realWidth = dm.widthPixels
            realHeight = dm.heightPixels
        }
        // Scale the longest edge down to MAX_EDGE to keep bandwidth reasonable.
        val longest = maxOf(realWidth, realHeight)
        val scale = if (longest > MAX_EDGE) MAX_EDGE.toFloat() / longest else 1f
        // Keep dimensions even — some encoders dislike odd sizes.
        streamWidth = ((realWidth * scale).toInt()) and 0x7FFFFFFE
        streamHeight = ((realHeight * scale).toInt()) and 0x7FFFFFFE
        if (streamWidth <= 0) streamWidth = realWidth
        if (streamHeight <= 0) streamHeight = realHeight
    }

    private fun displayDensity(): Int {
        val dm = resources.displayMetrics
        return dm.densityDpi
    }

    private fun onFrame(reader: ImageReader) {
        val image = try {
            reader.acquireLatestImage()
        } catch (t: Throwable) {
            null
        } ?: return
        try {
            val plane = image.planes[0]
            val buffer = plane.buffer
            val pixelStride = plane.pixelStride
            val rowStride = plane.rowStride
            val rowPadding = rowStride - pixelStride * streamWidth
            val bmpWidth = streamWidth + (if (pixelStride > 0) rowPadding / pixelStride else 0)

            var bmp = reusableBitmap
            if (bmp == null || bmp.width != bmpWidth || bmp.height != streamHeight) {
                bmp?.recycle()
                bmp = Bitmap.createBitmap(bmpWidth, streamHeight, Bitmap.Config.ARGB_8888)
                reusableBitmap = bmp
            }
            bmp.copyPixelsFromBuffer(buffer)

            val out = ByteArrayOutputStream(64 * 1024)
            if (rowPadding == 0) {
                bmp.compress(Bitmap.CompressFormat.JPEG, JPEG_QUALITY, out)
            } else {
                val cropped = Bitmap.createBitmap(bmp, 0, 0, streamWidth, streamHeight)
                cropped.compress(Bitmap.CompressFormat.JPEG, JPEG_QUALITY, out)
                cropped.recycle()
            }
            latestJpeg.set(out.toByteArray())
            frameSeq.incrementAndGet()
        } catch (t: Throwable) {
            // Drop this frame; keep streaming.
        } finally {
            image.close()
        }
    }

    private fun acceptLoop() {
        var ss: ServerSocket? = null
        try {
            // reuseAddress must be set before bind, so create unbound then bind.
            ss = ServerSocket()
            ss.reuseAddress = true
            ss.bind(java.net.InetSocketAddress(port))
            serverSocket = ss
            while (running) {
                val socket = try {
                    ss.accept()
                } catch (t: Throwable) {
                    break
                }
                log("PC connected: ${socket.inetAddress.hostAddress}")
                notifyState()
                handleClient(socket)
                log("PC disconnected")
                notifyState()
            }
        } catch (t: Throwable) {
            log("Server error: ${t.message}")
        } finally {
            // Don't leak the listener if bind succeeded but the loop threw, or if
            // a Start/Stop race replaced the serverSocket field.
            try { ss?.close() } catch (_: Throwable) {}
            if (serverSocket === ss) serverSocket = null
        }
    }

    private fun handleClient(socket: Socket) {
        socket.tcpNoDelay = true
        val output = DataOutputStream(socket.getOutputStream().buffered(64 * 1024))
        val input = DataInputStream(BufferedInputStream(socket.getInputStream()))

        // Frames and file responses share one socket, so every write is framed
        // under this lock to keep messages from interleaving.
        val outLock = Any()
        // Must never throw: this is called from the file worker thread, where an
        // uncaught IOException on a dead socket would take down the process.
        fun sendFramed(type: Int, payload: ByteArray) {
            try {
                synchronized(outLock) {
                    output.writeByte(type)
                    output.writeInt(payload.size)
                    output.write(payload)
                    output.flush()
                }
            } catch (_: Throwable) {
                // Client gone; the frame loop notices and closes the connection.
                try { socket.close() } catch (_: Throwable) {}
            }
        }

        val files = FileTransfer(::sendFramed)
        fileTransfer = files
        clientSocket = socket
        sender = ::sendFramed

        // Push message notifications to the PC for as long as this client is
        // connected. Mirroring cannot show Messages at all (the system blanks it
        // during capture), so messages travel as data instead of pixels.
        MessageNotificationService.listener = object : MessageNotificationService.Listener {
            override fun onMessage(message: JSONObject) {
                sendFramed(TYPE_MESSAGE, message.toString().toByteArray(Charsets.UTF_8))
            }
        }

        // Reader thread for control messages.
        val reader = Thread({ controlLoop(input, files) }, "pc-control")
        reader.isDaemon = true
        reader.start()

        try {
            // Header
            val header = JSONObject()
                .put("name", deviceDisplayName())
                .put("model", Build.MODEL ?: "")
                .put("android", Build.VERSION.RELEASE ?: "")
                .put("w", realWidth)
                .put("h", realHeight)
                .put("sw", streamWidth)
                .put("sh", streamHeight)
                .put("files", files.hasAccess)
                .put("root", files.defaultRoot())
                // Whether input injection is actually available. Without this the
                // PC mirrors happily while every tap is silently discarded.
                .put("control", ControlAccessibilityService.isEnabled)
                .put("messages", MessageNotificationService.isEnabled)
                .put("smsHistory", smsHistory.hasAccess)
                .toString()
                .toByteArray(Charsets.UTF_8)
            sendFramed(TYPE_HEADER, header)

            var lastSeq = -1L
            val frameIntervalMs = 1000L / TARGET_FPS
            while (running && !socket.isClosed) {
                val seq = frameSeq.get()
                if (seq != lastSeq) {
                    val jpeg = latestJpeg.get()
                    if (jpeg != null) {
                        sendFramed(TYPE_FRAME, jpeg)
                        lastSeq = seq
                    }
                }
                Thread.sleep(frameIntervalMs)
            }
        } catch (t: Throwable) {
            // client gone
        } finally {
            files.shutdown()
            if (fileTransfer === files) fileTransfer = null
            if (clientSocket === socket) clientSocket = null
            sender = null
            MessageNotificationService.listener = null
            // The PC may have dropped mid-drag; release any held finger.
            try { ControlAccessibilityService.instance?.cancelDrag() } catch (_: Throwable) {}
            try { socket.close() } catch (_: Throwable) {}
        }
    }

    /**
     * PC -> phone messages are [1-byte type][4-byte length][payload], where type
     * 0 is a UTF-8 JSON control message and type 1 is a raw chunk of a file the
     * PC is uploading.
     */
    private fun controlLoop(input: DataInputStream, files: FileTransfer) {
        try {
            while (running) {
                val type = input.read()
                if (type < 0) break
                val len = input.readInt()
                if (len < 0 || len > MAX_INBOUND) {
                    // The stream is out of sync and cannot be resynchronised;
                    // drop the client so it reconnects rather than silently
                    // leaving control dead while video keeps streaming.
                    log("Bad control frame ($len bytes) — dropping client")
                    try { clientSocket?.close() } catch (_: Throwable) {}
                    break
                }
                val buf = ByteArray(len)
                input.readFully(buf)
                when (type) {
                    IN_JSON -> dispatchControl(String(buf, Charsets.UTF_8), files)
                    IN_FILEDATA -> files.feed(buf)
                }
            }
        } catch (t: Throwable) {
            // socket closed
        }
    }

    private fun dispatchControl(json: String, files: FileTransfer) {
        // File operations don't need the accessibility service, so handle them
        // before the input-injection guard below.
        try {
            val o = JSONObject(json)
            when (o.optString("t")) {
                "msgs" -> {
                    // PC asked for whatever conversations are on screen now.
                    MessageNotificationService.instance?.currentMessages()?.forEach { m ->
                        sendFramedSafe(TYPE_MESSAGE, m.toString().toByteArray(Charsets.UTF_8))
                    }
                    return
                }
                "threads" -> {
                    smsWorker.execute {
                        sendFramedSafe(
                            TYPE_MESSAGE,
                            smsHistory.threads(o.optInt("limit", 40)).toString()
                                .toByteArray(Charsets.UTF_8)
                        )
                    }
                    return
                }
                "thread" -> {
                    smsWorker.execute {
                        sendFramedSafe(
                            TYPE_MESSAGE,
                            smsHistory.thread(o.optLong("id"), o.optInt("limit", 100)).toString()
                                .toByteArray(Charsets.UTF_8)
                        )
                    }
                    return
                }
                "reply" -> {
                    val ok = MessageNotificationService.instance
                        ?.reply(o.optString("key"), o.optString("text")) ?: false
                    sendFramedSafe(
                        TYPE_MESSAGE,
                        JSONObject().put("r", "reply").put("key", o.optString("key"))
                            .put("ok", ok).toString().toByteArray(Charsets.UTF_8)
                    )
                    return
                }
                "ls" -> { files.list(o.optString("path").ifBlank { null }); return }
                "tree" -> { files.tree(o.optString("path")); return }
                "get" -> { files.get(o.optString("path")); return }
                "put" -> {
                    files.beginPut(o.optString("dir"), o.optString("name"), o.optLong("size"))
                    return
                }
            }
        } catch (t: Throwable) {
            return
        }

        val svc = ControlAccessibilityService.instance ?: return
        try {
            val o = JSONObject(json)
            when (o.optString("t")) {
                "tap" -> svc.tap(px(o.optDouble("x")), py(o.optDouble("y")))
                "long" -> svc.longPress(
                    px(o.optDouble("x")), py(o.optDouble("y")),
                    o.optLong("dur", 600)
                )
                "swipe" -> svc.swipe(
                    px(o.optDouble("x1")), py(o.optDouble("y1")),
                    px(o.optDouble("x2")), py(o.optDouble("y2")),
                    o.optLong("dur", 200)
                )
                "down" -> svc.touchDown(px(o.optDouble("x")), py(o.optDouble("y")))
                "move" -> svc.touchMove(px(o.optDouble("x")), py(o.optDouble("y")), o.optLong("d", 16))
                "up" -> svc.touchUp(px(o.optDouble("x")), py(o.optDouble("y")), o.optLong("d", 16))
                "text" -> svc.typeText(o.optString("s"))
                "settext" -> svc.setFieldText(o.optString("s"))
                "key" -> when (o.optString("k")) {
                    "del" -> svc.deleteText()
                    "enter" -> svc.imeEnter()
                    "clearall" -> svc.clearField()
                    else -> svc.globalKey(o.optString("k"))
                }
            }
        } catch (t: Throwable) {
            // ignore malformed control message
        }
    }

    /**
     * Normalised control coordinates are scaled by the current display size, which
     * swaps on rotation. Refresh the cached size here rather than querying the
     * WindowManager per coordinate: that query is slow enough that it broke the
     * timing gesture continuation depends on, and drags stopped working entirely.
     */
    override fun onConfigurationChanged(newConfig: android.content.res.Configuration) {
        super.onConfigurationChanged(newConfig)
        val (w, h) = measureRealSize()
        if (w > 0 && h > 0) {
            realWidth = w
            realHeight = h
        }
    }

    private fun measureRealSize(): Pair<Int, Int> {
        val wm = getSystemService(Context.WINDOW_SERVICE) as? WindowManager
            ?: return realWidth to realHeight
        return try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                val b = wm.currentWindowMetrics.bounds
                b.width() to b.height()
            } else {
                val dm = DisplayMetrics()
                @Suppress("DEPRECATION") wm.defaultDisplay.getRealMetrics(dm)
                dm.widthPixels to dm.heightPixels
            }
        } catch (_: Throwable) {
            realWidth to realHeight
        }
    }

    private fun px(nx: Double): Float = (nx.coerceIn(0.0, 1.0) * realWidth).toFloat()
    private fun py(ny: Double): Float = (ny.coerceIn(0.0, 1.0) * realHeight).toFloat()

    /**
     * The phone's product name. [Build.MODEL] alone is a bare part number
     * ("SM-S926U"), so prefer an OEM marketing-name property, then decode the
     * model code, and finally fall back to manufacturer + model. Google and
     * several other OEMs already put the retail name in MODEL, in which case
     * every step below simply passes it through.
     */
    private fun deviceDisplayName(): String {
        val model = (Build.MODEL ?: "Android").trim()
        val mfr = (Build.MANUFACTURER ?: "").trim()
            .replaceFirstChar { if (it.isLowerCase()) it.titlecase() else it.toString() }

        val marketing = systemProperty("ro.product.marketname")
            ?: systemProperty("ro.product.vendor.marketname")
            ?: systemProperty("ro.vendor.product.display")

        val base = when {
            !marketing.isNullOrBlank() && !marketing.equals(model, true) -> marketing
            else -> samsungProductName(model) ?: model
        }
        // Avoid "Samsung Samsung Galaxy…" when the name already names the maker.
        return if (mfr.isEmpty() || base.contains(mfr, ignoreCase = true)) base else "$mfr $base"
    }

    /**
     * Samsung publishes no marketing-name property, so map its model codes to the
     * retail name: SM-S926U -> "Galaxy S24+". Codes follow a regular scheme, where
     * the third digit is the generation and the fourth the variant. Anything that
     * doesn't match returns null so the caller falls back to the raw model.
     */
    private fun samsungProductName(model: String): String? {
        val m = model.uppercase()
        if (!m.startsWith("SM-")) return null

        // Galaxy S: SM-S9<gen><variant>, gen 0=S22 .. 3=S25; variant 1=base, 6=+, 8=Ultra.
        Regex("^SM-S9(\\d)(\\d)").find(m)?.let { r ->
            val gen = 22 + r.groupValues[1].toInt()
            val variant = when (r.groupValues[2]) {
                "1" -> ""
                "6" -> "+"
                "8" -> " Ultra"
                else -> return@let
            }
            return "Galaxy S$gen$variant"
        }
        // Galaxy Z Fold / Flip.
        Regex("^SM-F9\\d{2}").find(m)?.let { return "Galaxy Z Fold" }
        Regex("^SM-F7\\d{2}").find(m)?.let { return "Galaxy Z Flip" }
        // Galaxy A / Note / Tab keep their numbering.
        Regex("^SM-A(\\d{2,3})").find(m)?.let { return "Galaxy A${it.groupValues[1]}" }
        Regex("^SM-N(\\d{3})").find(m)?.let { return "Galaxy Note" }
        Regex("^SM-[TXP]\\d{3}").find(m)?.let { return "Galaxy Tab" }
        return null
    }

    /** Read a build system property; absent or blocked properties yield null. */
    private fun systemProperty(key: String): String? = runCatching {
        val cls = Class.forName("android.os.SystemProperties")
        val get = cls.getMethod("get", String::class.java)
        (get.invoke(null, key) as? String)?.trim()?.takeIf { it.isNotEmpty() }
    }.getOrNull()

    private fun startForegroundNotification() {
        val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(
                CHANNEL_ID, "Screen sharing", NotificationManager.IMPORTANCE_LOW
            )
            nm.createNotificationChannel(channel)
        }
        val notification: Notification = Notification.Builder(this, CHANNEL_ID)
            .setContentTitle("PC Phone Connect")
            .setContentText("Sharing screen to your PC")
            // The app's own mark, not the stock system transfer arrow — that read
            // as "downloading" when nothing is being downloaded.
            .setSmallIcon(R.drawable.ic_stat_share)
            .setOngoing(true)
            .build()
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            startForeground(
                NOTIF_ID, notification,
                android.content.pm.ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION
            )
        } else {
            startForeground(NOTIF_ID, notification)
        }
    }

    private fun stopEverything() {
        running = false
        // Close the live client too, otherwise the frame loop keeps the socket
        // (and the file worker) alive after the service is told to stop.
        try { clientSocket?.close() } catch (_: Throwable) {}
        clientSocket = null
        try { fileTransfer?.shutdown() } catch (_: Throwable) {}
        fileTransfer = null
        try { serverSocket?.close() } catch (_: Throwable) {}
        serverSocket = null
        // A drag in flight would otherwise leave a finger held down on screen.
        try { ControlAccessibilityService.instance?.cancelDrag() } catch (_: Throwable) {}
        try { virtualDisplay?.release() } catch (_: Throwable) {}
        virtualDisplay = null
        try { imageReader?.close() } catch (_: Throwable) {}
        imageReader = null
        try { captureThread?.quitSafely() } catch (_: Throwable) {}
        captureThread = null
        try { projection?.stop() } catch (_: Throwable) {}
        projection = null
        reusableBitmap?.recycle()
        reusableBitmap = null
        latestJpeg.set(null)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
            stopForeground(STOP_FOREGROUND_REMOVE)
        } else {
            @Suppress("DEPRECATION") stopForeground(true)
        }
        notifyState()
        stopSelf()
    }

    override fun onDestroy() {
        running = false
        isRunning = false
        super.onDestroy()
    }

    private fun log(msg: String) {
        main.post { listener?.onLog(msg) }
    }

    private fun notifyState() {
        isRunning = running
        main.post { listener?.onStateChanged(running) }
    }

    interface Listener {
        fun onLog(message: String)
        fun onStateChanged(running: Boolean)
    }

    companion object {
        const val ACTION_START = "com.j0ker.pcphoneconnect.START"
        const val ACTION_STOP = "com.j0ker.pcphoneconnect.STOP"
        const val EXTRA_PORT = "port"
        const val EXTRA_RESULT_CODE = "resultCode"
        const val EXTRA_RESULT_DATA = "resultData"

        const val DEFAULT_PORT = 6060
        private const val MAX_EDGE = 1280
        private const val JPEG_QUALITY = 55
        private const val TARGET_FPS = 30

        private const val CHANNEL_ID = "screen_share"
        private const val NOTIF_ID = 42

        private const val TYPE_HEADER = 0
        private const val TYPE_FRAME = 1
        // 2 and 3 belong to FileTransfer (response / file chunk).
        private const val TYPE_MESSAGE = 4
        // Inbound (PC -> phone) message types.
        private const val IN_JSON = 0
        private const val IN_FILEDATA = 1
        private const val MAX_INBOUND = 8 * 1024 * 1024

        @Volatile
        var isRunning = false
            private set

        @Volatile
        var listener: Listener? = null
    }
}
