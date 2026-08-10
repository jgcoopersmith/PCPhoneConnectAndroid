package com.j0ker.pcphoneconnect

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.GestureDescription
import android.graphics.Path
import android.os.Build
import android.view.accessibility.AccessibilityEvent

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
        val stroke = GestureDescription.StrokeDescription(path, 0, DRAG_SEGMENT_MS, true)
        dragStroke = stroke
        lastX = x; lastY = y
        try {
            dispatchGesture(GestureDescription.Builder().addStroke(stroke).build(), null, null)
        } catch (_: Throwable) {
            dragStroke = null
        }
    }

    fun touchMove(x: Float, y: Float) {
        val prev = dragStroke ?: run { touchDown(x, y); return }
        val path = Path().apply { moveTo(lastX, lastY); lineTo(x, y) }
        val next = try {
            prev.continueStroke(path, 0, DRAG_SEGMENT_MS, true)
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

    fun touchUp(x: Float, y: Float) {
        val prev = dragStroke
        dragStroke = null
        if (prev == null) { tap(x, y); return }
        val path = Path().apply {
            moveTo(lastX, lastY)
            // A zero-length path is rejected; nudge by a pixel if the finger didn't move.
            if (x == lastX && y == lastY) lineTo(x + 1f, y) else lineTo(x, y)
        }
        val end = try {
            prev.continueStroke(path, 0, DRAG_SEGMENT_MS, false)
        } catch (_: Throwable) {
            null
        }
        if (end != null) {
            try {
                dispatchGesture(GestureDescription.Builder().addStroke(end).build(), null, null)
            } catch (_: Throwable) { }
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
        // Per-segment gesture duration for continuous drags. A little longer than the
        // PC's move cadence so consecutive segments overlap and stay continuous.
        private const val DRAG_SEGMENT_MS = 60L

        @Volatile
        var instance: ControlAccessibilityService? = null
            private set

        val isEnabled: Boolean get() = instance != null
    }
}
