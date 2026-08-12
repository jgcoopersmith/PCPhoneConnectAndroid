package com.j0ker.pcphoneconnect

import android.app.Notification
import android.app.RemoteInput
import android.content.Intent
import android.os.Build
import android.os.Bundle
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification
import org.json.JSONObject
import java.util.concurrent.ConcurrentHashMap

/**
 * Surfaces incoming messages to the paired PC and sends replies back through the
 * originating app.
 *
 * This exists because screen mirroring cannot show Google Messages at all: the
 * system blanks it during any screen capture (sensitive content protection), and
 * that cannot be turned off without root. Notifications are not blanked, and a
 * messaging notification carries a RemoteInput reply action — so reading and
 * replying works through the notification itself, which is how smartwatches and
 * Phone Link do it. Replies go out through the app that posted the notification,
 * so RCS still works and Messages stays the default SMS app.
 */
class MessageNotificationService : NotificationListenerService() {

    override fun onListenerConnected() {
        super.onListenerConnected()
        instance = this
    }

    override fun onListenerDisconnected() {
        if (instance === this) instance = null
        super.onListenerDisconnected()
    }

    override fun onDestroy() {
        if (instance === this) instance = null
        super.onDestroy()
    }

    override fun onNotificationPosted(sbn: StatusBarNotification?) {
        val n = sbn ?: return
        if (!isMessage(n)) return
        active[n.key] = n
        val described = describe(n)
        remember(n.key, described)
        listener?.onMessage(described)
    }

    override fun onNotificationRemoved(sbn: StatusBarNotification?) {
        val key = sbn?.key ?: return
        active.remove(key)
        // Keep the message visible on the PC after it is read or swiped away —
        // otherwise it vanishes from the list the moment you glance at the phone.
        // It can no longer be replied to, though, so say so.
        synchronized(recent) {
            recent[key]?.put("canReply", false)
        }
    }

    /**
     * Messages seen this session, newest first, whether or not their notification
     * is still showing. Anything still active is refreshed first so its reply
     * state is accurate.
     */
    fun currentMessages(): List<JSONObject> {
        try {
            activeNotifications.orEmpty()
                .filter { isMessage(it) }
                .forEach {
                    active[it.key] = it
                    remember(it.key, describe(it))
                }
        } catch (_: Throwable) {
            // listener not connected yet; fall through to whatever we remember
        }
        return synchronized(recent) {
            recent.values.sortedByDescending { it.optLong("time") }
        }
    }

    private fun remember(key: String, described: JSONObject) {
        synchronized(recent) {
            recent.remove(key)          // re-insert so ordering stays by recency
            recent[key] = described
            while (recent.size > MAX_REMEMBERED) {
                recent.remove(recent.keys.first())
            }
        }
    }

    /**
     * Type [text] into the notification's reply field and fire it. Returns false
     * when the notification is gone or never offered a reply action.
     */
    fun reply(key: String, text: String): Boolean {
        val sbn = active[key] ?: return false
        val action = replyAction(sbn.notification) ?: return false
        val inputs = action.remoteInputs ?: return false
        return try {
            val results = Bundle().apply {
                inputs.forEach { putCharSequence(it.resultKey, text) }
            }
            val intent = Intent()
            RemoteInput.addResultsToIntent(inputs, intent, results)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
                RemoteInput.setResultsSource(intent, RemoteInput.SOURCE_FREE_FORM_INPUT)
            }
            action.actionIntent.send(this, 0, intent)
            true
        } catch (_: Throwable) {
            false
        }
    }

    // ---------------- Shaping ----------------

    private fun isMessage(sbn: StatusBarNotification): Boolean {
        val n = sbn.notification ?: return false
        if (n.flags and Notification.FLAG_GROUP_SUMMARY != 0) return false // dedupe
        if (n.category == Notification.CATEGORY_MESSAGE) return true
        // Anything offering a free-form reply is a conversation for our purposes.
        return replyAction(n) != null
    }

    private fun replyAction(n: Notification): Notification.Action? =
        n.actions?.firstOrNull { a -> a.remoteInputs?.any { it.allowFreeFormInput } == true }

    private fun describe(sbn: StatusBarNotification): JSONObject {
        val n = sbn.notification
        val extras = n.extras
        fun str(k: String) = extras?.getCharSequence(k)?.toString()?.trim().orEmpty()

        // Prefer the newest line of a conversation over the collapsed summary.
        val latest = messagesFrom(extras).lastOrNull()
        val sender = latest?.first?.takeIf { it.isNotBlank() }
            ?: str(Notification.EXTRA_CONVERSATION_TITLE).ifBlank { str(Notification.EXTRA_TITLE) }
        val body = latest?.second?.takeIf { it.isNotBlank() }
            ?: str(Notification.EXTRA_TEXT).ifBlank { str(Notification.EXTRA_BIG_TEXT) }

        return JSONObject()
            .put("key", sbn.key)
            .put("app", appLabel(sbn.packageName))
            .put("pkg", sbn.packageName)
            .put("sender", sender)
            .put("text", body)
            .put("time", sbn.postTime)
            .put("canReply", replyAction(n) != null)
    }

    /** MessagingStyle carries the individual lines; use them when present. */
    private fun messagesFrom(extras: Bundle?): List<Pair<String, String>> {
        val raw = extras?.getParcelableArray(Notification.EXTRA_MESSAGES) ?: return emptyList()
        return raw.mapNotNull { item ->
            val b = item as? Bundle ?: return@mapNotNull null
            val who = b.getCharSequence("sender")?.toString().orEmpty()
            val what = b.getCharSequence("text")?.toString().orEmpty()
            if (what.isBlank()) null else who to what
        }
    }

    private fun appLabel(pkg: String): String = try {
        val pm = packageManager
        pm.getApplicationLabel(pm.getApplicationInfo(pkg, 0)).toString()
    } catch (_: Throwable) {
        pkg
    }

    interface Listener {
        fun onMessage(message: JSONObject)
    }

    companion object {
        @Volatile
        var instance: MessageNotificationService? = null
            private set

        val isEnabled: Boolean get() = instance != null

        /** Set by StreamService while a PC is connected. */
        @Volatile
        var listener: Listener? = null

        // Keyed by notification key so a reply can find its action later. Only
        // holds notifications still on screen, since a reply needs a live action.
        private val active = ConcurrentHashMap<String, StatusBarNotification>()

        // Messages seen this session, in arrival order, kept after dismissal so
        // reading a message on the phone doesn't erase it from the PC.
        private val recent = LinkedHashMap<String, JSONObject>()
        private const val MAX_REMEMBERED = 50
    }
}
