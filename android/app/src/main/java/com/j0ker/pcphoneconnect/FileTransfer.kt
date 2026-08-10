package com.j0ker.pcphoneconnect

import android.os.Build
import android.os.Environment
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.FileOutputStream
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.ThreadPoolExecutor
import java.util.concurrent.TimeUnit

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

    /**
     * Bounded queue with a caller-runs policy: the socket reader blocks once the
     * worker falls behind instead of buffering the whole upload in RAM. An
     * unbounded queue let a fast PC push a multi-GB file straight onto the heap.
     */
    private val worker = ThreadPoolExecutor(
        1, 1, 0L, TimeUnit.MILLISECONDS,
        ArrayBlockingQueue(QUEUE_DEPTH),
        { r -> Thread(r, "pc-files").apply { isDaemon = true } },
        ThreadPoolExecutor.CallerRunsPolicy()
    )

    // In-progress upload from the PC.
    private var uploadStream: FileOutputStream? = null
    private var uploadRemaining = 0L
    private var uploadPath: String? = null
    // Bytes still to swallow from an upload whose beginPut failed.
    private var discardRemaining = 0L

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
        // Wait briefly so an in-flight beginPut can't open a stream after this
        // point and leak it; then discard whatever partial upload remains.
        runCatching { worker.awaitTermination(500, TimeUnit.MILLISECONDS) }
        val path = uploadPath
        closeUpload()
        if (path != null) runCatching { File(path).delete() }
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
            if (kids == null) {
                // listFiles() returns null for "cannot read", which is not the same
                // as an empty folder — report it instead of showing nothing.
                error(
                    if (hasAccess) "Cannot read ${dir.absolutePath} (permission denied)."
                    else "Grant \"All files access\" in the phone app to browse files."
                )
                return@execute
            }
            val entries = JSONArray()
            kids
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

    /**
     * Recursively enumerate a folder so the PC can pull it in one action. Paths
     * come back relative to [rawPath] so the PC can rebuild the same structure.
     */
    fun tree(rawPath: String) = worker.execute {
        try {
            val requested = File(rawPath)
            if (!requested.isDirectory) {
                error("Not a folder: $rawPath")
                return@execute
            }
            // Resolve first: shared storage is reached through symlinks (/sdcard ->
            // /storage/emulated/0), so compare canonical paths or the walk escapes
            // — or, if the root itself looks like a link, never starts.
            val root = runCatching { requested.canonicalFile }.getOrDefault(requested)
            val rootPath = root.absolutePath

            val files = JSONArray()
            var total = 0L
            var count = 0
            // Iterate explicitly: inside forEach, `return@forEach` is a continue, so
            // the old cap kept walking the entire tree (minutes on a big folder)
            // while only recording the first N entries.
            val walker = root.walkTopDown()
                .onEnter { dir ->
                    // Don't follow links that point outside the folder being copied.
                    val real = runCatching { dir.canonicalPath }.getOrDefault(dir.absolutePath)
                    real == rootPath || real.startsWith("$rootPath/")
                }
                .iterator()
            while (walker.hasNext() && count < MAX_TREE_FILES) {
                val f = walker.next()
                if (!f.isFile) continue
                val rel = f.absolutePath.removePrefix(rootPath).trimStart('/')
                if (rel.isEmpty()) continue
                files.put(JSONObject().put("p", rel).put("s", f.length()))
                total += f.length()
                count++
            }
            respond(
                JSONObject()
                    .put("r", "tree")
                    .put("root", rootPath)
                    .put("name", root.name)
                    .put("bytes", total)
                    .put("truncated", count >= MAX_TREE_FILES)
                    .put("files", files)
            )
        } catch (t: Throwable) {
            error("Folder scan failed: ${t.message}")
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
            // Open before announcing: a failed open after getstart left the PC
            // holding an empty file it believed was a real download.
            file.inputStream().use { input ->
                respond(
                    JSONObject()
                        .put("r", "getstart")
                        .put("name", file.name)
                        .put("size", file.length())
                )
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
            // The PC still streams the chunks it already committed to sending even
            // if this fails, so remember how many bytes to swallow — otherwise the
            // next upload would be corrupted by the abandoned file's tail.
            discardRemaining = size
            val folder = File(dir)
            if (!folder.isDirectory && !folder.mkdirs()) {
                error("No such folder: $dir")
                return@execute
            }
            val target = uniqueFile(folder, name)
            uploadStream = FileOutputStream(target)
            uploadRemaining = size
            uploadPath = target.absolutePath
            discardRemaining = 0L
            if (size == 0L) finishUpload()
        } catch (t: Throwable) {
            closeUpload()
            error("Upload failed: ${t.message}")
        }
    }

    /** Feed one binary chunk of the in-progress upload. */
    fun feed(data: ByteArray) = worker.execute {
        val out = uploadStream
        if (out == null) {
            // No open upload: drop the tail of a failed transfer silently.
            if (discardRemaining > 0L) discardRemaining -= data.size
            return@execute
        }
        try {
            // Never write past the declared size — a PC that sends more than it
            // announced would otherwise append junk to the file.
            val n = minOf(data.size.toLong(), uploadRemaining).toInt()
            if (n > 0) {
                out.write(data, 0, n)
                uploadRemaining -= n
            }
            if (uploadRemaining <= 0L) finishUpload()
        } catch (t: Throwable) {
            val path = uploadPath
            closeUpload()
            // Don't leave a half-written file behind.
            if (path != null) runCatching { File(path).delete() }
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
        // Guard against pulling an unbounded tree (e.g. the storage root).
        private const val MAX_TREE_FILES = 5000
        // At 128 KB per chunk this caps queued upload data at a few MB.
        private const val QUEUE_DEPTH = 16
    }
}
