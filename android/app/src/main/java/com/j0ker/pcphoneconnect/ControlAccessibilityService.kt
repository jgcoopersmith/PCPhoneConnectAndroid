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
        @Volatile
        var instance: ControlAccessibilityService? = null
            private set

        val isEnabled: Boolean get() = instance != null
    }
}
