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

    override fun onAccessibilityEvent(event: AccessibilityEvent?) { /* passive */ }

    override fun onInterrupt() { /* no-op */ }

    fun tap(x: Float, y: Float) {
        val path = Path().apply { moveTo(x, y) }
        val stroke = GestureDescription.StrokeDescription(path, 0, 60)
        dispatchGesture(GestureDescription.Builder().addStroke(stroke).build(), null, null)
    }

    fun longPress(x: Float, y: Float, durationMs: Long = 600) {
        val path = Path().apply { moveTo(x, y) }
        val stroke = GestureDescription.StrokeDescription(path, 0, durationMs.coerceIn(1, 4000))
        dispatchGesture(GestureDescription.Builder().addStroke(stroke).build(), null, null)
    }

    fun swipe(x1: Float, y1: Float, x2: Float, y2: Float, durationMs: Long = 200) {
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

    private var dragStroke: GestureDescription.StrokeDescription? = null
    private var lastX = 0f
    private var lastY = 0f

    fun touchDown(x: Float, y: Float) {
        val path = Path().apply { moveTo(x, y) }
        val stroke = GestureDescription.StrokeDescription(path, 0, DOWN_SEGMENT_MS, true)
        dragStroke = stroke
        lastX = x; lastY = y
        try {
            dispatchGesture(GestureDescription.Builder().addStroke(stroke).build(), null, null)
        } catch (_: Throwable) {
            dragStroke = null
        }
    }

    /**
     * [durationMs] is the real time the cursor took to reach this point, so the
     * injected stroke plays at the user's actual speed — that is what lets a quick
     * flick build fling velocity and turn a home page instead of rubber-banding.
     */
    fun touchMove(x: Float, y: Float, durationMs: Long) {
        val prev = dragStroke ?: run { touchDown(x, y); return }
        val path = Path().apply { moveTo(lastX, lastY); lineTo(x, y) }
        val next = try {
            prev.continueStroke(path, 0, durationMs.coerceIn(4, 1000), true)
        } catch (_: Throwable) {
            // Previous gesture already ended (dropped/timed out) — start a fresh touch.
            touchDown(x, y); return
        }
        dragStroke = next
        lastX = x; lastY = y
        try {
            dispatchGesture(GestureDescription.Builder().addStroke(next).build(), null, null)
        } catch (_: Throwable) {
            dragStroke = null
        }
    }

    fun touchUp(x: Float, y: Float, durationMs: Long) {
        val prev = dragStroke
        dragStroke = null
        if (prev == null) { tap(x, y); return }
        val path = Path().apply {
            moveTo(lastX, lastY)
            // A zero-length path is rejected; nudge by a pixel if the finger didn't move.
            if (x == lastX && y == lastY) lineTo(x + 1f, y) else lineTo(x, y)
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

    /** Trigger the field's IME action (Search / Send / Go / newline). */
    fun imeEnter() {
        val node = focusedEditable() ?: return
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            node.performAction(AccessibilityNodeInfo.AccessibilityAction.ACTION_IME_ENTER.id)
        } else {
            typeText("\n")
        }
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

        @Volatile
        var instance: ControlAccessibilityService? = null
            private set

        val isEnabled: Boolean get() = instance != null
    }
}
