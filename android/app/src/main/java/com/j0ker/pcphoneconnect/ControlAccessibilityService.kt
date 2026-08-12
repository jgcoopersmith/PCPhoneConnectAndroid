package com.j0ker.pcphoneconnect

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.GestureDescription
import android.graphics.Path
import android.os.Build
import android.os.Bundle
import android.view.accessibility.AccessibilityEvent
import android.view.accessibility.AccessibilityNodeInfo

/**
 * Injects touch gestures and navigation actions on behalf of the paired PC.
 *
 * Control coordinates arrive as absolute device pixels (already mapped from the
 * normalized values the PC sends — see [StreamService]). Gesture dispatch is the
 * only way to synthesize touch on a non-rooted device, so remote control depends
 * on the user enabling this service in Accessibility settings.
 */
class ControlAccessibilityService : AccessibilityService() {

    override fun onServiceConnected() {
        super.onServiceConnected()
        instance = this
    }

    override fun onUnbind(intent: android.content.Intent?): Boolean {
        if (instance === this) instance = null
        return super.onUnbind(intent)
    }

    override fun onDestroy() {
        if (instance === this) instance = null
        super.onDestroy()
    }

    /**
     * Auto-confirms the screen-capture consent dialog. Android gives apps no way
     * to skip that dialog, but an accessibility service may press the button —
     * which is what removes the extra tap after Start server.
     *
     * Deliberately narrow: it only acts inside a short window armed by our own
     * Start server tap, and only on a SystemUI dialog that names this app. Any
     * other app's capture request is left alone, so this can never silently grant
     * screen recording to something else.
     */
    override fun onAccessibilityEvent(event: AccessibilityEvent?) {
        if (event == null) return
        if (System.currentTimeMillis() > autoAcceptUntil) return
        if (event.packageName?.toString() != SYSTEM_UI) return
        confirmCaptureDialog()
    }

    private fun confirmCaptureDialog() {
        val root = rootInActiveWindow ?: return
        // Only ever touch a dialog that is asking on behalf of THIS app.
        val mine = root.findAccessibilityNodeInfosByText(getString(R.string.app_name))
            .any { it != null }
        if (!mine) return

        for (label in CONFIRM_LABELS) {
            val hits = root.findAccessibilityNodeInfosByText(label) ?: continue
            for (node in hits) {
                if (!label.equals(node.text?.toString(), ignoreCase = true)) continue
                var target: AccessibilityNodeInfo? = node
                var hops = 0
                while (target != null && !target.isClickable && hops++ < 4) target = target.parent
                if (target != null && target.isClickable && target.isEnabled) {
                    if (target.performAction(AccessibilityNodeInfo.ACTION_CLICK)) {
                        autoAcceptUntil = 0L // one shot
                        return
                    }
                }
            }
        }
    }

    override fun onInterrupt() { /* no-op */ }

    // Dispatching a one-shot gesture while a continuous drag is live cancels the
    // drag inside the framework, and the drag state here would never learn about
    // it — leaving a finger logically stuck down. Drop these while dragging.
    fun tap(x: Float, y: Float) {
        if (dragActive) return
        val path = Path().apply { moveTo(x, y) }
        val stroke = GestureDescription.StrokeDescription(path, 0, 60)
        dispatchGesture(GestureDescription.Builder().addStroke(stroke).build(), null, null)
    }

    fun longPress(x: Float, y: Float, durationMs: Long = 600) {
        if (dragActive) return
        val path = Path().apply { moveTo(x, y) }
        val stroke = GestureDescription.StrokeDescription(path, 0, durationMs.coerceIn(1, 4000))
        dispatchGesture(GestureDescription.Builder().addStroke(stroke).build(), null, null)
    }

    fun swipe(x1: Float, y1: Float, x2: Float, y2: Float, durationMs: Long = 200) {
        if (dragActive) return
        val path = Path().apply {
            moveTo(x1, y1)
            lineTo(x2, y2)
        }
        val stroke = GestureDescription.StrokeDescription(path, 0, durationMs.coerceIn(1, 4000))
        dispatchGesture(GestureDescription.Builder().addStroke(stroke).build(), null, null)
    }

    // ---- Continuous drag ("grab and pull") via gesture continuation ----
    // A touch stays down across down -> move* -> up, so the content follows the
    // cursor live (home-screen paging, dragging, sliders) instead of a one-shot flick.

    // Touched from the socket's control thread and from service teardown, so all
    // drag state is guarded by this lock.
    private val dragLock = Any()
    private var dragStroke: GestureDescription.StrokeDescription? = null
    private var lastX = 0f
    private var lastY = 0f

    /**
     * Lift any in-flight drag. Called when the PC disconnects or the server stops:
     * without it the synthesised finger stays pressed and the phone behaves as if
     * something is being held down until the gesture times out.
     */
    fun cancelDrag() {
        val prev = synchronized(dragLock) {
            val p = dragStroke
            dragStroke = null
            p
        } ?: return
        val path = Path().apply { moveTo(lastX, lastY); lineTo(lastX + 1f, lastY) }
        try {
            val end = prev.continueStroke(path, 0, DOWN_SEGMENT_MS, false)
            dispatchGesture(GestureDescription.Builder().addStroke(end).build(), null, null)
        } catch (_: Throwable) {
        }
    }

    /** True while a continuous drag is in progress. */
    private val dragActive: Boolean get() = synchronized(dragLock) { dragStroke != null }

    fun touchDown(x: Float, y: Float) {
        val path = Path().apply { moveTo(x, y) }
        val stroke = GestureDescription.StrokeDescription(path, 0, DOWN_SEGMENT_MS, true)
        synchronized(dragLock) {
            dragStroke = stroke
            lastX = x; lastY = y
        }
        try {
            dispatchGesture(GestureDescription.Builder().addStroke(stroke).build(), null, null)
        } catch (_: Throwable) {
            synchronized(dragLock) { dragStroke = null }
        }
    }

    /**
     * [durationMs] is the real time the cursor took to reach this point, so the
     * injected stroke plays at the user's actual speed — that is what lets a quick
     * flick build fling velocity and turn a home page instead of rubber-banding.
     */
    fun touchMove(x: Float, y: Float, durationMs: Long) {
        val prev: GestureDescription.StrokeDescription
        val fromX: Float
        val fromY: Float
        synchronized(dragLock) {
            val p = dragStroke ?: run { touchDown(x, y); return }
            prev = p; fromX = lastX; fromY = lastY
        }
        val path = Path().apply { moveTo(fromX, fromY); lineTo(x, y) }
        val next = try {
            prev.continueStroke(path, 0, durationMs.coerceIn(4, 1000), true)
        } catch (_: Throwable) {
            // Previous gesture already ended (dropped/timed out) — start a fresh touch.
            touchDown(x, y); return
        }
        synchronized(dragLock) {
            dragStroke = next
            lastX = x; lastY = y
        }
        try {
            dispatchGesture(GestureDescription.Builder().addStroke(next).build(), null, null)
        } catch (_: Throwable) {
            synchronized(dragLock) { dragStroke = null }
        }
    }

    fun touchUp(x: Float, y: Float, durationMs: Long) {
        val prev: GestureDescription.StrokeDescription?
        val fromX: Float
        val fromY: Float
        synchronized(dragLock) {
            prev = dragStroke
            dragStroke = null
            fromX = lastX; fromY = lastY
        }
        if (prev == null) { tap(x, y); return }
        val path = Path().apply {
            moveTo(fromX, fromY)
            // A zero-length path is rejected; nudge by a pixel if the finger didn't move.
            if (x == fromX && y == fromY) lineTo(x + 1f, y) else lineTo(x, y)
        }
        val end = try {
            prev.continueStroke(path, 0, durationMs.coerceIn(4, 1000), false)
        } catch (_: Throwable) {
            null
        }
        if (end != null) {
            try {
                dispatchGesture(GestureDescription.Builder().addStroke(end).build(), null, null)
            } catch (_: Throwable) { }
        }
    }

    // ---- Text entry into the currently focused editable field ----

    private fun focusedEditable(): AccessibilityNodeInfo? {
        val root = rootInActiveWindow ?: return null
        val node = root.findFocus(AccessibilityNodeInfo.FOCUS_INPUT) ?: return null
        return if (node.isEditable) node else null
    }

    private fun applyText(node: AccessibilityNodeInfo, text: CharSequence, caret: Int) {
        val setArgs = Bundle().apply {
            putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE, text)
        }
        node.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT, setArgs)
        val selArgs = Bundle().apply {
            putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_START_INT, caret)
            putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_END_INT, caret)
        }
        node.performAction(AccessibilityNodeInfo.ACTION_SET_SELECTION, selArgs)
    }

    /**
     * Replace the focused field's entire contents with [s] in one atomic action.
     * The PC mirrors its text box here on every change, which avoids the
     * per-keystroke read-modify-write races that dropped spaces and reordered
     * characters, and bypasses on-device autocorrect.
     */
    fun setFieldText(s: String) {
        val node = focusedEditable() ?: return
        applyText(node, s, s.length)
    }

    /** Insert [s] at the cursor of the focused field (no clipboard involved). */
    fun typeText(s: String) {
        if (s.isEmpty()) return
        val node = focusedEditable() ?: return
        val cur = node.text?.toString() ?: ""
        var start = node.textSelectionStart
        var end = node.textSelectionEnd
        if (start < 0 || end < 0 || start > cur.length || end > cur.length) {
            start = cur.length; end = cur.length
        }
        val lo = minOf(start, end); val hi = maxOf(start, end)
        val updated = StringBuilder(cur.length + s.length)
            .append(cur, 0, lo).append(s).append(cur, hi, cur.length)
        applyText(node, updated, lo + s.length)
    }

    /** Backspace: delete the selection, or the character before the cursor. */
    fun deleteText() {
        val node = focusedEditable() ?: return
        val cur = node.text?.toString() ?: ""
        if (cur.isEmpty()) return
        var start = node.textSelectionStart
        var end = node.textSelectionEnd
        if (start < 0 || end < 0 || start > cur.length || end > cur.length) {
            start = cur.length; end = cur.length
        }
        val lo = minOf(start, end); val hi = maxOf(start, end)
        if (lo == hi) {
            if (lo == 0) return
            applyText(node, cur.removeRange(lo - 1, lo), lo - 1)
        } else {
            applyText(node, cur.removeRange(lo, hi), lo)
        }
    }

    /** Empty the focused field entirely. */
    fun clearField() {
        val node = focusedEditable() ?: return
        applyText(node, "", 0)
    }

    /**
     * Trigger the field's IME action (Search / Send / Go). ACTION_IME_ENTER is the
     * clean route but plenty of fields refuse it, and the result was that Enter on
     * the PC did nothing at all — so fall back to a newline, which submits
     * single-line fields and inserts a break in multiline ones.
     */
    fun imeEnter() {
        val node = focusedEditable() ?: return
        val submitted = Build.VERSION.SDK_INT >= Build.VERSION_CODES.R &&
            runCatching {
                node.performAction(AccessibilityNodeInfo.AccessibilityAction.ACTION_IME_ENTER.id)
            }.getOrDefault(false)
        if (!submitted) typeText("\n")
    }

    /** key is one of: back, home, recents, notifications, power. */
    fun globalKey(key: String) {
        val action = when (key.lowercase()) {
            "back" -> GLOBAL_ACTION_BACK
            "home" -> GLOBAL_ACTION_HOME
            "recents" -> GLOBAL_ACTION_RECENTS
            "notifications" -> GLOBAL_ACTION_NOTIFICATIONS
            "power" -> if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP_MR1) 6 /* GLOBAL_ACTION_POWER_DIALOG */ else return
            "lock" -> if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) 8 /* GLOBAL_ACTION_LOCK_SCREEN */ else return
            else -> return
        }
        performGlobalAction(action)
    }

    companion object {
        // Initial touch-down dwell before the first move arrives.
        private const val DOWN_SEGMENT_MS = 12L

        private const val SYSTEM_UI = "com.android.systemui"
        // Confirm button wording differs by Android version / OEM skin.
        private val CONFIRM_LABELS = listOf("Share screen", "Start now", "Start recording")

        /** Only auto-confirm while this is in the future. */
        @Volatile
        private var autoAcceptUntil = 0L

        /** Called just before the app requests capture; expires quickly. */
        fun armAutoAccept(windowMs: Long = 8000) {
            autoAcceptUntil = System.currentTimeMillis() + windowMs
        }

        @Volatile
        var instance: ControlAccessibilityService? = null
            private set

        val isEnabled: Boolean get() = instance != null
    }
}
