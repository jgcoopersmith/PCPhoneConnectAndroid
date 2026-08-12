package com.j0ker.pcphoneconnect

import android.Manifest
import android.content.ContentResolver
import android.content.Context
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.provider.ContactsContract
import androidx.core.content.ContextCompat
import org.json.JSONArray
import org.json.JSONObject

/**
 * Reads SMS *and* MMS history so the PC can show real scrollback.
 *
 * Everything here follows what the device actually reports, not what the docs
 * imply, because the two disagree in ways that matter:
 *
 *  - Conversations must come from content://mms-sms/conversations. Querying
 *    content://sms alone silently drops every MMS thread, which on this phone
 *    included the most recent conversation by well over an hour.
 *  - That view's `snippet` column is NULL here, so the preview has to be read
 *    from the newest message in the thread.
 *  - SMS rows carry `date` in milliseconds; MMS rows carry it in SECONDS.
 *  - MMS rows have a NULL `body`; their text lives in content://mms/part with
 *    ct='text/plain'.
 *  - Direction is `type` on SMS rows and `msg_box` on MMS rows (2 = sent).
 *
 * Read-only by design. Sending from here would go through SmsManager, which
 * downgrades an RCS conversation to plain SMS and never appears in the phone's
 * own thread, so replies stay on the notification path.
 */
class SmsHistory(private val context: Context) {

    val hasAccess: Boolean
        get() = ContextCompat.checkSelfPermission(context, Manifest.permission.READ_SMS) ==
            PackageManager.PERMISSION_GRANTED

    private val resolver: ContentResolver get() = context.contentResolver
    private val nameCache = HashMap<String, String>()
    private val addressCache = HashMap<String, String>()

    /** One entry per conversation, newest first, covering SMS and MMS alike. */
    fun threads(limit: Int = 40): JSONObject {
        if (!hasAccess) return error("SMS access not granted on the phone.")
        val out = JSONArray()
        try {
            resolver.query(
                THREADS_URI,
                arrayOf("_id", "date", "recipient_ids", "message_count"),
                null, null, "date DESC"
            )?.use { c ->
                val iId = c.getColumnIndex("_id")
                val iDate = c.getColumnIndex("date")
                val iRecip = c.getColumnIndex("recipient_ids")
                val iCount = c.getColumnIndex("message_count")
                val blocked = blockedNumbers()
                var added = 0
                while (c.moveToNext() && added < limit) {
                    val threadId = c.getLong(iId)
                    val latest = newestInThread(threadId)
                    val address = resolveParticipants(threadId, c.getString(iRecip).orEmpty())

                    // Spam shells: Google Messages moves messages it classifies as
                    // spam into its own private store, leaving a thread here with
                    // nothing readable in it. They showed as blank rows from
                    // unknown numbers, so drop threads with no messages at all.
                    if (latest == null) continue

                    // And drop conversations from numbers the user has blocked.
                    if (isBlocked(address, blocked)) continue

                    out.put(
                        JSONObject()
                            .put("id", threadId)
                            .put("address", address)
                            .put("name", displayName(address))
                            .put("snippet", latest?.text.orEmpty())
                            .put("date", if (iDate >= 0) c.getLong(iDate) else 0L)
                            .put("outgoing", latest?.outgoing ?: false)
                            .put("count", if (iCount >= 0) c.getInt(iCount) else 0)
                            .put("kind", latest?.kind ?: "SMS")
                    )
                    added++
                }
            }
        } catch (t: Throwable) {
            return error("Could not read conversations: ${t.message}")
        }
        return JSONObject().put("r", "threads").put("threads", out)
    }

    /**
     * Send a plain SMS to one recipient.
     *
     * This is the fallback for history threads, where there is no notification
     * left to reply through. It really is SMS: an RCS conversation will receive
     * it as a green-bubble text, and because this app is not the default SMS app
     * it cannot write to the message store, so the sent message will not appear
     * in the phone's own thread or in this history. Replying to a live
     * notification avoids both problems and is preferred where possible.
     */
    fun send(to: String, text: String): JSONObject {
        if (to.isBlank() || text.isBlank()) return sendResult(false, "Nothing to send.")
        if (ContextCompat.checkSelfPermission(context, Manifest.permission.SEND_SMS)
            != PackageManager.PERMISSION_GRANTED
        ) {
            return sendResult(false, "SMS sending not granted on the phone.")
        }
        return try {
            val manager = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
                context.getSystemService(android.telephony.SmsManager::class.java)
            } else {
                @Suppress("DEPRECATION") android.telephony.SmsManager.getDefault()
            } ?: return sendResult(false, "No SMS service available.")

            // Long messages have to be split or they are silently truncated.
            val parts = manager.divideMessage(text)
            if (parts.size > 1) {
                manager.sendMultipartTextMessage(to, null, parts, null, null)
            } else {
                manager.sendTextMessage(to, null, text, null, null)
            }
            sendResult(true, "Sent as SMS to $to")
        } catch (t: Throwable) {
            sendResult(false, "Send failed: ${t.message}")
        }
    }

    private fun sendResult(ok: Boolean, message: String) =
        JSONObject().put("r", "sent").put("ok", ok).put("m", message)

    /** Messages in one conversation, oldest first so it reads top to bottom. */
    fun thread(threadId: Long, limit: Int = 100): JSONObject {
        if (!hasAccess) return error("SMS access not granted on the phone.")
        val out = JSONArray()
        try {
            val rows = readThread(threadId, limit)
            rows.reversed().forEach {
                out.put(
                    JSONObject()
                        .put("text", it.text)
                        .put("date", it.dateMs)
                        .put("outgoing", it.outgoing)
                        .put("kind", it.kind)
                        .put("sender", it.sender)
                )
            }
        } catch (t: Throwable) {
            return error("Could not read the conversation: ${t.message}")
        }
        return JSONObject().put("r", "thread").put("id", threadId).put("messages", out)
    }

    // ---------------- Reading ----------------

    private data class Row(
        val text: String,
        val dateMs: Long,
        val outgoing: Boolean,
        val kind: String,  // "SMS" or "MMS", so the PC can say where it came from
        val sender: String // who wrote it, which matters in a group thread
    )

    private fun newestInThread(threadId: Long): Row? = readThread(threadId, 1).firstOrNull()

    /** Newest first. Normalises the SMS/MMS differences described above. */
    private fun readThread(threadId: Long, limit: Int): List<Row> {
        val rows = ArrayList<Row>(limit)
        val uri = Uri.withAppendedPath(CONVERSATIONS_URI, threadId.toString())
        resolver.query(
            uri,
            arrayOf("_id", "date", "body", "ct_t", "type", "msg_box", "address"),
            null, null, "date DESC"
        )?.use { c ->
            val iId = c.getColumnIndex("_id")
            val iDate = c.getColumnIndex("date")
            val iBody = c.getColumnIndex("body")
            val iCt = c.getColumnIndex("ct_t")
            val iType = c.getColumnIndex("type")
            val iBox = c.getColumnIndex("msg_box")
            val iAddr = c.getColumnIndex("address")
            while (c.moveToNext() && rows.size < limit) {
                val isMms = iCt >= 0 && !c.getString(iCt).isNullOrBlank()
                val raw = if (iDate >= 0) c.getLong(iDate) else 0L
                // MMS keeps seconds, SMS keeps milliseconds.
                val dateMs = if (isMms) raw * 1000L else raw
                val body = if (iBody >= 0) c.getString(iBody).orEmpty() else ""
                val text = when {
                    body.isNotBlank() -> body
                    isMms -> mmsText(c.getLong(iId)).ifBlank { "[attachment]" }
                    else -> ""
                }
                val outgoing = if (isMms) {
                    iBox >= 0 && c.getInt(iBox) == MMS_SENT
                } else {
                    iType >= 0 && c.getInt(iType) == SMS_SENT
                }
                // Who wrote it. Without this every incoming message in a group
                // thread read as "Them", which tells you nothing about who spoke.
                val from = when {
                    outgoing -> ""
                    isMms -> mmsSender(c.getLong(iId))
                    iAddr >= 0 -> c.getString(iAddr).orEmpty()
                    else -> ""
                }
                rows.add(
                    Row(
                        text, dateMs, outgoing,
                        if (isMms) "MMS" else "SMS",
                        if (from.isBlank()) "" else displayName(from)
                    )
                )
            }
        }
        return rows
    }

    /**
     * The sender of an MMS. Its address table holds every party, distinguished by
     * type: 137 is FROM, 151 is TO, 130 is CC — confirmed against this device.
     */
    private fun mmsSender(messageId: Long): String = try {
        resolver.query(
            Uri.parse("content://mms/$messageId/addr"), arrayOf("address", "type"),
            null, null, null
        )?.use { c ->
            val iAddr = c.getColumnIndex("address")
            val iType = c.getColumnIndex("type")
            var found = ""
            while (c.moveToNext() && found.isEmpty()) {
                if (iType >= 0 && c.getInt(iType) == MMS_ADDR_FROM && iAddr >= 0) {
                    val a = c.getString(iAddr).orEmpty().trim()
                    if (a.isNotBlank() && !a.equals("insert-address-token", true)) found = a
                }
            }
            found
        }.orEmpty()
    } catch (_: Throwable) {
        ""
    }

    /** MMS bodies live in the part table, not on the message row. */
    private fun mmsText(messageId: Long): String {
        val sb = StringBuilder()
        try {
            resolver.query(
                PART_URI, arrayOf("_id", "ct", "text"),
                "mid = ?", arrayOf(messageId.toString()), null
            )?.use { c ->
                val iCt = c.getColumnIndex("ct")
                val iText = c.getColumnIndex("text")
                while (c.moveToNext()) {
                    if (c.getString(iCt) != "text/plain") continue
                    val t = c.getString(iText).orEmpty()
                    if (t.isNotBlank()) {
                        if (sb.isNotEmpty()) sb.append(' ')
                        sb.append(t)
                    }
                }
            }
        } catch (_: Throwable) {
        }
        return sb.toString().trim()
    }

    /**
     * Who a conversation is with.
     *
     * The canonical address is usually the number, but an RCS group chat stores
     * an internal blob there instead — a base32 string ending @rcs.google.com,
     * which is what was being shown as the conversation's name. Those threads
     * carry MMS messages, and MMS keeps its real participants in a per-message
     * address table, so fall back to that.
     */
    private fun resolveParticipants(threadId: Long, recipientIds: String): String {
        val canonical = addressesFor(recipientIds)
        if (canonical.isNotBlank() && !canonical.contains(RCS_DOMAIN)) return canonical
        return mmsParticipants(threadId).ifBlank { canonical }
    }

    /** Participants of the newest MMS in a thread, excluding this device. */
    private fun mmsParticipants(threadId: Long): String {
        val messageId = try {
            resolver.query(
                Uri.parse("content://mms"), arrayOf("_id"),
                "thread_id = ?", arrayOf(threadId.toString()), "date DESC"
            )?.use { c ->
                if (!c.moveToFirst()) null else {
                    val i = c.getColumnIndex("_id"); if (i >= 0) c.getLong(i) else null
                }
            }
        } catch (_: Throwable) {
            null
        } ?: return ""

        val people = LinkedHashSet<String>()
        try {
            resolver.query(
                Uri.parse("content://mms/$messageId/addr"), arrayOf("address", "type"),
                null, null, null
            )?.use { c ->
                val iAddr = c.getColumnIndex("address")
                while (c.moveToNext()) {
                    if (iAddr < 0) continue
                    val a = c.getString(iAddr).orEmpty().trim()
                    // insert-address-token is the placeholder for this device.
                    if (a.isBlank() || a.equals("insert-address-token", true)) continue
                    if (a.contains(RCS_DOMAIN)) continue
                    if (normalise(a) == myNumberTail) continue
                    people.add(a)
                }
            }
        } catch (_: Throwable) {
        }
        return people.joinToString(", ")
    }

    /** This device's own number, so it isn't listed as a participant. */
    private val myNumberTail: String? by lazy {
        try {
            val tm = context.getSystemService(Context.TELEPHONY_SERVICE)
                as? android.telephony.TelephonyManager
            @Suppress("DEPRECATION", "MissingPermission")
            normalise(tm?.line1Number.orEmpty())
        } catch (_: Throwable) {
            null
        }
    }

    /**
     * Numbers the user has blocked. Reading this is normally reserved for the
     * default SMS or dialer app, so treat a refusal as "nothing blocked" rather
     * than failing the whole listing.
     */
    private fun blockedNumbers(): Set<String> = try {
        val set = HashSet<String>()
        resolver.query(
            Uri.parse("content://com.android.blockednumber/blocked"),
            arrayOf("original_number"), null, null, null
        )?.use { c ->
            while (c.moveToNext()) {
                normalise(c.getString(0).orEmpty())?.let { set.add(it) }
            }
        }
        set
    } catch (_: Throwable) {
        emptySet()
    }

    private fun isBlocked(addresses: String, blocked: Set<String>): Boolean {
        if (blocked.isEmpty() || addresses.isBlank()) return false
        val parts = addresses.split(", ").mapNotNull { normalise(it) }
        // A group chat is only hidden if every participant is blocked.
        return parts.isNotEmpty() && parts.all { blocked.contains(it) }
    }

    /** Compare on the last 10 digits so +1 prefixes and formatting don't matter. */
    private fun normalise(number: String): String? {
        val digits = number.filter { it.isDigit() }
        if (digits.isEmpty()) return null
        return if (digits.length > 10) digits.takeLast(10) else digits
    }

    /** recipient_ids is a space-separated list into the canonical address table. */
    private fun addressesFor(recipientIds: String): String {
        if (recipientIds.isBlank()) return ""
        return recipientIds.trim().split(' ')
            .filter { it.isNotBlank() }
            .mapNotNull { canonicalAddress(it) }
            .joinToString(", ")
    }

    private fun canonicalAddress(id: String): String? {
        addressCache[id]?.let { return it }
        // Never read column 0: this provider ignores the projection and hands back
        // every column, so index 0 is _id. Doing that made every conversation
        // display its recipient id — 633, 659 — instead of a name or number.
        val value = try {
            resolver.query(
                CANONICAL_URI, arrayOf("address"), "_id = ?", arrayOf(id), null
            )?.use { c ->
                if (!c.moveToFirst()) "" else {
                    val i = c.getColumnIndex("address")
                    if (i >= 0) c.getString(i).orEmpty() else ""
                }
            }.orEmpty()
        } catch (_: Throwable) {
            ""
        }
        if (value.isNotBlank()) addressCache[id] = value
        return value.ifBlank { null }
    }

    /** Resolve numbers to contact names where possible; cached per address. */
    private fun displayName(addresses: String): String {
        if (addresses.isBlank()) return ""
        return addresses.split(", ").joinToString(", ") { one ->
            nameCache[one] ?: lookupName(one).also { nameCache[one] = it }
        }
    }

    private fun lookupName(address: String): String = try {
        if (ContextCompat.checkSelfPermission(context, Manifest.permission.READ_CONTACTS)
            != PackageManager.PERMISSION_GRANTED
        ) {
            address
        } else {
            val uri = Uri.withAppendedPath(
                ContactsContract.PhoneLookup.CONTENT_FILTER_URI, Uri.encode(address)
            )
            resolver.query(
                uri, arrayOf(ContactsContract.PhoneLookup.DISPLAY_NAME), null, null, null
            )?.use { c ->
                if (!c.moveToFirst()) address else {
                    // Same rule as above: resolve by name, not by position.
                    val i = c.getColumnIndex(ContactsContract.PhoneLookup.DISPLAY_NAME)
                    if (i >= 0) c.getString(i).orEmpty().ifBlank { address } else address
                }
            } ?: address
        }
    } catch (_: Throwable) {
        address
    }

    private fun error(message: String) = JSONObject().put("r", "smserr").put("m", message)

    companion object {
        // Two different shapes behind one path, and mixing them up is an error:
        //   ?simple=true  -> the threads table  (_id, date in ms, recipient_ids)
        //   /<threadId>   -> merged SMS+MMS messages in that thread
        // The bare URI is a merged message view with no recipient_ids at all.
        private val THREADS_URI: Uri = Uri.parse("content://mms-sms/conversations?simple=true")
        private val CONVERSATIONS_URI: Uri = Uri.parse("content://mms-sms/conversations")
        private val CANONICAL_URI: Uri = Uri.parse("content://mms-sms/canonical-addresses")
        private val PART_URI: Uri = Uri.parse("content://mms/part")
        private const val SMS_SENT = 2
        private const val MMS_SENT = 2
        // RCS group threads keep an internal blob here rather than a number.
        private const val RCS_DOMAIN = "@rcs.google.com"
        // PDU address types: 137 = FROM, 151 = TO, 130 = CC.
        private const val MMS_ADDR_FROM = 137
    }
}
