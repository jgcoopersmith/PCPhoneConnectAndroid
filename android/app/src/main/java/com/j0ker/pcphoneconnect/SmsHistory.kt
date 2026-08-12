package com.j0ker.pcphoneconnect

import android.Manifest
import android.content.ContentResolver
import android.content.Context
import android.content.pm.PackageManager
import android.net.Uri
import android.provider.ContactsContract
import android.provider.Telephony
import androidx.core.content.ContextCompat
import org.json.JSONArray
import org.json.JSONObject

/**
 * Reads SMS/MMS history out of the system provider so the PC can show scrollback,
 * not just whatever happens to be sitting in a notification.
 *
 * Read-only by design. Sending from here would go out through SmsManager, which
 * would downgrade an RCS conversation to plain SMS and would not be recorded in
 * the thread, so replies stay on the notification path where the messaging app
 * sends them itself.
 */
class SmsHistory(private val context: Context) {

    val hasAccess: Boolean
        get() = ContextCompat.checkSelfPermission(context, Manifest.permission.READ_SMS) ==
            PackageManager.PERMISSION_GRANTED

    private val resolver: ContentResolver get() = context.contentResolver
    private val nameCache = HashMap<String, String>()

    /**
     * One entry per conversation, newest first. Built by scanning recent messages
     * rather than the conversations view, whose recipient ids need a second lookup
     * per row and differ across OEMs.
     */
    fun threads(limit: Int = 40): JSONObject {
        if (!hasAccess) return error("SMS access not granted on the phone.")
        val out = JSONArray()
        try {
            val seen = LinkedHashMap<Long, JSONObject>()
            resolver.query(
                Telephony.Sms.CONTENT_URI,
                arrayOf(
                    Telephony.Sms.THREAD_ID, Telephony.Sms.ADDRESS,
                    Telephony.Sms.BODY, Telephony.Sms.DATE, Telephony.Sms.TYPE
                ),
                null, null,
                "${Telephony.Sms.DATE} DESC LIMIT $SCAN_ROWS"
            )?.use { c ->
                val iThread = c.getColumnIndex(Telephony.Sms.THREAD_ID)
                val iAddr = c.getColumnIndex(Telephony.Sms.ADDRESS)
                val iBody = c.getColumnIndex(Telephony.Sms.BODY)
                val iDate = c.getColumnIndex(Telephony.Sms.DATE)
                val iType = c.getColumnIndex(Telephony.Sms.TYPE)
                while (c.moveToNext() && seen.size < limit) {
                    val thread = c.getLong(iThread)
                    if (seen.containsKey(thread)) continue   // first row is the newest
                    val address = c.getString(iAddr).orEmpty()
                    seen[thread] = JSONObject()
                        .put("id", thread)
                        .put("address", address)
                        .put("name", displayName(address))
                        .put("snippet", c.getString(iBody).orEmpty())
                        .put("date", c.getLong(iDate))
                        .put("outgoing", c.getInt(iType) == Telephony.Sms.MESSAGE_TYPE_SENT)
                }
            }
            seen.values.forEach { out.put(it) }
        } catch (t: Throwable) {
            return error("Could not read conversations: ${t.message}")
        }
        return JSONObject().put("r", "threads").put("threads", out)
    }

    /** Messages within one conversation, oldest first so it reads top to bottom. */
    fun thread(threadId: Long, limit: Int = 100): JSONObject {
        if (!hasAccess) return error("SMS access not granted on the phone.")
        val out = JSONArray()
        try {
            resolver.query(
                Telephony.Sms.CONTENT_URI,
                arrayOf(
                    Telephony.Sms.ADDRESS, Telephony.Sms.BODY,
                    Telephony.Sms.DATE, Telephony.Sms.TYPE
                ),
                "${Telephony.Sms.THREAD_ID} = ?",
                arrayOf(threadId.toString()),
                "${Telephony.Sms.DATE} DESC LIMIT $limit"
            )?.use { c ->
                val iAddr = c.getColumnIndex(Telephony.Sms.ADDRESS)
                val iBody = c.getColumnIndex(Telephony.Sms.BODY)
                val iDate = c.getColumnIndex(Telephony.Sms.DATE)
                val iType = c.getColumnIndex(Telephony.Sms.TYPE)
                val rows = ArrayList<JSONObject>()
                while (c.moveToNext()) {
                    rows.add(
                        JSONObject()
                            .put("text", c.getString(iBody).orEmpty())
                            .put("date", c.getLong(iDate))
                            .put("outgoing", c.getInt(iType) == Telephony.Sms.MESSAGE_TYPE_SENT)
                            .put("address", c.getString(iAddr).orEmpty())
                    )
                }
                rows.reversed().forEach { out.put(it) }   // oldest first
            }
        } catch (t: Throwable) {
            return error("Could not read the conversation: ${t.message}")
        }
        return JSONObject().put("r", "thread").put("id", threadId).put("messages", out)
    }

    /** Resolve a number to a contact name where possible; cached per address. */
    private fun displayName(address: String): String {
        if (address.isBlank()) return ""
        nameCache[address]?.let { return it }
        val resolved = try {
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
                    if (c.moveToFirst()) c.getString(0).orEmpty().ifBlank { address } else address
                } ?: address
            }
        } catch (_: Throwable) {
            address
        }
        nameCache[address] = resolved
        return resolved
    }

    private fun error(message: String) = JSONObject().put("r", "smserr").put("m", message)

    companion object {
        // Enough recent messages to cover the most active conversations without
        // dragging the whole database across the wire.
        private const val SCAN_ROWS = 2000
    }
}
