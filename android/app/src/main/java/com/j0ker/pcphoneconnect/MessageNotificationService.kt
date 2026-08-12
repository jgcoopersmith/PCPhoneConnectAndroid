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
        listener?.onMessage(describe(n))
    }

    override fun onNotificationRemoved(sbn: StatusBarNotification?) {
        sbn?.let { active.remove(it.key) }
    }

    /** Everything currently on screen that looks like a message. */
    fun currentMessages(): List<JSONObject> = try {
        activeNotifications.orEmpty()
            .filter { isMessage(it) }
            .onEach { active[it.key] = it }
            .map { describe(it) }
    } catch (_: Throwable) {
        emptyList()
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

        // Keyed by notification key so a reply can find its action later.
        private val active = ConcurrentHashMap<String, StatusBarNotification>()
    }
}
