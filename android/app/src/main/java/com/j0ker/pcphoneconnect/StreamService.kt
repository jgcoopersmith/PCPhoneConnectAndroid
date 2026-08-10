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

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
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
        return START_STICKY
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

        val reader = ImageReader.newInstance(
            streamWidth, streamHeight, PixelFormat.RGBA_8888, 2
        )
        reader.setOnImageAvailableListener({ r -> onFrame(r) }, main)
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
        try {
            val ss = ServerSocket(port)
            ss.reuseAddress = true
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
        }
    }

    private fun handleClient(socket: Socket) {
        socket.tcpNoDelay = true
        val output = DataOutputStream(socket.getOutputStream().buffered(64 * 1024))
        val input = DataInputStream(BufferedInputStream(socket.getInputStream()))

        // Reader thread for control messages.
        val reader = Thread({ controlLoop(input) }, "pc-control")
        reader.isDaemon = true
        reader.start()

        try {
            // Header
            val header = JSONObject()
                .put("name", Build.MODEL ?: "Android")
                .put("w", realWidth)
                .put("h", realHeight)
                .put("sw", streamWidth)
                .put("sh", streamHeight)
                .toString()
                .toByteArray(Charsets.UTF_8)
            output.writeByte(TYPE_HEADER)
            output.writeInt(header.size)
            output.write(header)
            output.flush()

            var lastSeq = -1L
            val frameIntervalMs = 1000L / TARGET_FPS
            while (running && !socket.isClosed) {
                val seq = frameSeq.get()
                if (seq != lastSeq) {
                    val jpeg = latestJpeg.get()
                    if (jpeg != null) {
                        output.writeByte(TYPE_FRAME)
                        output.writeInt(jpeg.size)
                        output.write(jpeg)
                        output.flush()
                        lastSeq = seq
                    }
                }
                Thread.sleep(frameIntervalMs)
            }
        } catch (t: Throwable) {
            // client gone
        } finally {
            try { socket.close() } catch (_: Throwable) {}
        }
    }

    private fun controlLoop(input: DataInputStream) {
        try {
            while (running) {
                val len = input.readInt()
                if (len <= 0 || len > 1_000_000) break
                val buf = ByteArray(len)
                input.readFully(buf)
                dispatchControl(String(buf, Charsets.UTF_8))
            }
        } catch (t: Throwable) {
            // socket closed
        }
    }

    private fun dispatchControl(json: String) {
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
                "key" -> svc.globalKey(o.optString("k"))
            }
        } catch (t: Throwable) {
            // ignore malformed control message
        }
    }

    private fun px(nx: Double): Float = (nx.coerceIn(0.0, 1.0) * realWidth).toFloat()
    private fun py(ny: Double): Float = (ny.coerceIn(0.0, 1.0) * realHeight).toFloat()

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
            .setSmallIcon(android.R.drawable.stat_sys_upload)
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
        try { serverSocket?.close() } catch (_: Throwable) {}
        serverSocket = null
        try { virtualDisplay?.release() } catch (_: Throwable) {}
        virtualDisplay = null
        try { imageReader?.close() } catch (_: Throwable) {}
        imageReader = null
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

        @Volatile
        var isRunning = false
            private set

        @Volatile
        var listener: Listener? = null
    }
}
