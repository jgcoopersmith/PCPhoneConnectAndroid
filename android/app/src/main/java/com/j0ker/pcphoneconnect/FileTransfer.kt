package com.j0ker.pcphoneconnect

import android.os.Build
import android.os.Environment
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.FileOutputStream
import java.util.concurrent.Executors

/**
 * Serves the phone's filesystem to the paired PC: directory listings, downloads
 * (phone -> PC) and uploads (PC -> phone).
 *
 * Work runs on a dedicated single-thread executor so a large transfer never
 * blocks the control channel — taps and swipes stay responsive while a file
 * copies. [send] writes one framed message and is expected to serialise writes
 * against the video stream.
 */
class FileTransfer(private val send: (type: Int, payload: ByteArray) -> Unit) {

    private val worker = Executors.newSingleThreadExecutor { r ->
        Thread(r, "pc-files").apply { isDaemon = true }
    }

    // In-progress upload from the PC.
    private var uploadStream: FileOutputStream? = null
    private var uploadRemaining = 0L
    private var uploadPath: String? = null

    /** True once the user has granted broad filesystem access. */
    val hasAccess: Boolean
        get() = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            Environment.isExternalStorageManager()
        } else {
            true // pre-scoped-storage: the storage permission covers it
        }

    fun defaultRoot(): String = Environment.getExternalStorageDirectory()?.absolutePath ?: "/sdcard"

    fun shutdown() {
        worker.shutdownNow()
        closeUpload()
    }

    // ---------------- Listing ----------------

    fun list(rawPath: String?) = worker.execute {
        try {
            val dir = File(rawPath?.takeIf { it.isNotBlank() } ?: defaultRoot())
            if (!dir.isDirectory) {
                error("Not a folder: ${dir.absolutePath}")
                return@execute
            }
            val kids = dir.listFiles()
            if (kids == null && !hasAccess) {
                error("Grant \"All files access\" in the phone app to browse files.")
                return@execute
            }
            val entries = JSONArray()
            kids.orEmpty()
                .sortedWith(compareBy({ !it.isDirectory }, { it.name.lowercase() }))
                .forEach { f ->
                    entries.put(
                        JSONObject()
                            .put("n", f.name)
                            .put("d", f.isDirectory)
                            .put("s", if (f.isDirectory) 0L else f.length())
                    )
                }
            respond(
                JSONObject()
                    .put("r", "ls")
                    .put("path", dir.absolutePath)
                    .put("parent", dir.parentFile?.absolutePath ?: JSONObject.NULL)
                    .put("entries", entries)
            )
        } catch (t: Throwable) {
            error("List failed: ${t.message}")
        }
    }

    // ---------------- Download: phone -> PC ----------------

    fun get(path: String) = worker.execute {
        try {
            val file = File(path)
            if (!file.isFile) {
                error("Not a file: $path")
                return@execute
            }
            respond(
                JSONObject()
                    .put("r", "getstart")
                    .put("name", file.name)
                    .put("size", file.length())
            )
            file.inputStream().use { input ->
                val buf = ByteArray(CHUNK)
                while (true) {
                    val n = input.read(buf)
                    if (n <= 0) break
                    send(TYPE_FILEDATA, if (n == buf.size) buf.copyOf() else buf.copyOf(n))
                }
            }
            respond(JSONObject().put("r", "getdone").put("name", file.name))
        } catch (t: Throwable) {
            error("Download failed: ${t.message}")
        }
    }

    // ---------------- Upload: PC -> phone ----------------

    fun beginPut(dir: String, name: String, size: Long) = worker.execute {
        try {
            closeUpload()
            val folder = File(dir)
            if (!folder.isDirectory && !folder.mkdirs()) {
                error("No such folder: $dir")
                return@execute
            }
            val target = uniqueFile(folder, name)
            uploadStream = FileOutputStream(target)
            uploadRemaining = size
            uploadPath = target.absolutePath
            if (size == 0L) finishUpload()
        } catch (t: Throwable) {
            closeUpload()
            error("Upload failed: ${t.message}")
        }
    }

    /** Feed one binary chunk of the in-progress upload. */
    fun feed(data: ByteArray) = worker.execute {
        val out = uploadStream ?: return@execute
        try {
            out.write(data)
            uploadRemaining -= data.size
            if (uploadRemaining <= 0L) finishUpload()
        } catch (t: Throwable) {
            closeUpload()
            error("Upload failed: ${t.message}")
        }
    }

    private fun finishUpload() {
        val path = uploadPath
        try {
            uploadStream?.flush()
        } catch (_: Throwable) {
        }
        closeUpload()
        respond(JSONObject().put("r", "putdone").put("path", path ?: ""))
    }

    private fun closeUpload() {
        try { uploadStream?.close() } catch (_: Throwable) {}
        uploadStream = null
        uploadRemaining = 0L
        uploadPath = null
    }

    /** Never clobber an existing file: name.ext -> name (2).ext */
    private fun uniqueFile(dir: File, name: String): File {
        val safe = name.substringAfterLast('/').substringAfterLast('\\').ifBlank { "file" }
        var candidate = File(dir, safe)
        if (!candidate.exists()) return candidate
        val stem = safe.substringBeforeLast('.', safe)
        val ext = safe.substringAfterLast('.', "")
        var i = 2
        while (candidate.exists()) {
            val suffix = if (ext.isEmpty()) "$stem ($i)" else "$stem ($i).$ext"
            candidate = File(dir, suffix)
            i++
        }
        return candidate
    }

    // ---------------- Plumbing ----------------

    private fun respond(o: JSONObject) =
        send(TYPE_RESPONSE, o.toString().toByteArray(Charsets.UTF_8))

    private fun error(message: String) =
        respond(JSONObject().put("r", "err").put("m", message))

    companion object {
        const val TYPE_RESPONSE = 2
        const val TYPE_FILEDATA = 3
        private const val CHUNK = 128 * 1024
    }
}
