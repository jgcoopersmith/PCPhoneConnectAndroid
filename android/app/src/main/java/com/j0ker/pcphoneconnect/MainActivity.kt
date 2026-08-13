package com.j0ker.pcphoneconnect

import android.Manifest
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.media.projection.MediaProjectionConfig
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.text.format.Formatter
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat
import com.j0ker.pcphoneconnect.databinding.ActivityMainBinding
import java.net.Inet4Address
import java.net.NetworkInterface

class MainActivity : AppCompatActivity(), StreamService.Listener {

    private lateinit var binding: ActivityMainBinding
    private lateinit var projectionManager: MediaProjectionManager
    private val logLines = ArrayDeque<String>()

    private val notifPermission =
        registerForActivityResult(ActivityResultContracts.RequestPermission()) { /* result ignored */ }

    private val smsPermission =
        registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { result ->
            refreshUi(StreamService.isRunning)
            // A permission that was denied twice stops showing a dialog at all,
            // so the button would look dead. Hand the user to app settings.
            if (result.values.any { !it } && !shouldShowRequestPermissionRationale(
                    Manifest.permission.SEND_SMS
                )
            ) openAppSettings()
        }

    private fun openAppSettings() {
        toast("Turn on SMS under Permissions")
        startActivity(
            Intent(
                Settings.ACTION_APPLICATION_DETAILS_SETTINGS,
                android.net.Uri.fromParts("package", packageName, null)
            )
        )
    }

    private val projectionLauncher =
        registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
            if (result.resultCode == RESULT_OK && result.data != null) {
                startStreaming(result.resultCode, result.data!!)
            } else {
                toast("Screen sharing was not granted")
            }
        }

    private fun startStreaming(resultCode: Int, data: Intent) {
        val intent = Intent(this, StreamService::class.java).apply {
            action = StreamService.ACTION_START
            putExtra(StreamService.EXTRA_PORT, currentPort())
            putExtra(StreamService.EXTRA_RESULT_CODE, resultCode)
            putExtra(StreamService.EXTRA_RESULT_DATA, data)
        }
        ContextCompat.startForegroundService(this, intent)
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        binding = ActivityMainBinding.inflate(layoutInflater)
        setContentView(binding.root)

        // targetSdk 35+ forces edge-to-edge, which draws content under the status
        // bar and camera cutout — the app's title was hidden behind them. Inset the
        // root by the system bars so the whole UI stays visible.
        ViewCompat.setOnApplyWindowInsetsListener(binding.root) { view, insets ->
            val bars = insets.getInsets(
                WindowInsetsCompat.Type.systemBars() or WindowInsetsCompat.Type.displayCutout()
            )
            view.setPadding(bars.left, bars.top, bars.right, bars.bottom)
            insets
        }

        projectionManager =
            getSystemService(Context.MEDIA_PROJECTION_SERVICE) as MediaProjectionManager

        binding.enableAccessibilityButton.setOnClickListener {
            startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
            toast("Enable \"PC Phone Connect\" under Installed apps")
        }

        binding.enableFilesButton.setOnClickListener { requestAllFilesAccess() }

        binding.enableMessagesButton.setOnClickListener {
            if (enableNotificationAccess()) {
                toast("Message access enabled")
                binding.root.postDelayed({ if (!isFinishing) refreshUi(StreamService.isRunning) }, 1200)
            } else {
                startActivity(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS))
                toast("Turn on \"PC Phone Connect\"")
            }
        }

        binding.enableSmsButton.setOnClickListener {
            // Android only shows a permission dialog once per denial, so send the
            // user to app settings if they've already turned one of these down.
            smsPermission.launch(
                arrayOf(
                    Manifest.permission.READ_SMS,
                    Manifest.permission.READ_CONTACTS,
                    Manifest.permission.SEND_SMS
                )
            )
        }

        binding.startButton.setOnClickListener { requestStart() }
        binding.stopButton.setOnClickListener {
            startService(Intent(this, StreamService::class.java).apply {
                action = StreamService.ACTION_STOP
            })
        }

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS)
            != PackageManager.PERMISSION_GRANTED
        ) {
            notifPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
        }
    }

    override fun onResume() {
        super.onResume()
        StreamService.listener = this
        restoreAccessibilityIfPossible()
        refreshUi(StreamService.isRunning)
    }

    /**
     * Android switches this app's accessibility service off on every update, and
     * with it off the mirror still streams while every tap is silently dropped —
     * which reads as "the app broke". An app cannot enable its own service, but
     * with WRITE_SECURE_SETTINGS granted once over adb it can put itself back in
     * the enabled list, so an update no longer costs a trip through Settings.
     */
    private fun restoreAccessibilityIfPossible() {
        if (ControlAccessibilityService.isEnabled) return
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.WRITE_SECURE_SETTINGS)
            != PackageManager.PERMISSION_GRANTED
        ) return

        val component = "$packageName/${ControlAccessibilityService::class.java.name}"
        try {
            val current = Settings.Secure.getString(
                contentResolver, Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES
            ).orEmpty()
            // Preserve any other services the user relies on.
            val services = current.split(':').filter { it.isNotBlank() }.toMutableList()
            if (services.none { it.equals(component, ignoreCase = true) }) {
                services += component
                Settings.Secure.putString(
                    contentResolver,
                    Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES,
                    services.joinToString(":")
                )
            }
            Settings.Secure.putInt(contentResolver, Settings.Secure.ACCESSIBILITY_ENABLED, 1)
            toast("Remote control re-enabled")
            // Binding happens a moment later, so the status line drawn right after
            // this would still say OFF. Refresh once the service has connected.
            binding.root.postDelayed({
                if (!isFinishing) refreshUi(StreamService.isRunning)
            }, 1200)
        } catch (_: Throwable) {
            // Grant missing or blocked by the OEM — fall back to the manual button.
        }
    }

    override fun onPause() {
        super.onPause()
        if (StreamService.listener === this) StreamService.listener = null
    }

    private fun requestStart() {
        if (!ControlAccessibilityService.isEnabled) {
            toast("Tip: enable the Accessibility service first for remote control")
        }
        // The dialog itself cannot be skipped by an ordinary app: from Android 12
        // the system validates a real consent token, so getMediaProjection returns
        // null without one however the PROJECT_MEDIA app-op is set (verified on
        // Android 16). What we can do is let the accessibility service press the
        // confirm button, so the dialog flashes past instead of needing a tap.
        if (ControlAccessibilityService.isEnabled) ControlAccessibilityService.armAutoAccept()
        projectionLauncher.launch(screenCaptureIntent())
    }

    /**
     * Always capture the whole display. From Android 14 the consent dialog offers
     * "Share one app" and defaults to it; a single-app capture would mirror only
     * this app, which is useless here. Pinning the config to the default display
     * removes that choice entirely, so the dialog just asks to share the screen.
     */
    private fun screenCaptureIntent(): Intent =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            projectionManager.createScreenCaptureIntent(
                MediaProjectionConfig.createConfigForDefaultDisplay()
            )
        } else {
            projectionManager.createScreenCaptureIntent()
        }

    /**
     * Browsing shared storage needs "All files access" on Android 11+, which is
     * granted from a system settings page rather than a runtime dialog.
     */
    private fun requestAllFilesAccess() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.R) {
            toast("Not needed on this Android version")
            return
        }
        if (android.os.Environment.isExternalStorageManager()) {
            toast("File transfer access is already on")
            return
        }
        val intent = Intent(Settings.ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION).apply {
            data = android.net.Uri.parse("package:$packageName")
        }
        try {
            startActivity(intent)
        } catch (_: Throwable) {
            startActivity(Intent(Settings.ACTION_MANAGE_ALL_FILES_ACCESS_PERMISSION))
        }
        toast("Turn on \"Allow access to manage all files\"")
    }

    /**
     * Notification access is a secure setting, so with WRITE_SECURE_SETTINGS the
     * app can switch itself on exactly as it does for the accessibility service.
     * Returns false when the grant is missing, so the caller can fall back to the
     * settings screen.
     */
    private fun enableNotificationAccess(): Boolean {
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.WRITE_SECURE_SETTINGS)
            != PackageManager.PERMISSION_GRANTED
        ) return false
        val component = "$packageName/${MessageNotificationService::class.java.name}"
        return try {
            val current = Settings.Secure.getString(
                contentResolver, "enabled_notification_listeners"
            ).orEmpty()
            val listeners = current.split(':').filter { it.isNotBlank() }.toMutableList()
            if (listeners.none { it.equals(component, ignoreCase = true) }) {
                listeners += component
                Settings.Secure.putString(
                    contentResolver, "enabled_notification_listeners", listeners.joinToString(":")
                )
            }
            true
        } catch (_: Throwable) {
            false
        }
    }

    private fun granted(permission: String) =
        ContextCompat.checkSelfPermission(this, permission) == PackageManager.PERMISSION_GRANTED

    private fun hasNotificationAccess(): Boolean = try {
        val component = "$packageName/${MessageNotificationService::class.java.name}"
        Settings.Secure.getString(contentResolver, "enabled_notification_listeners")
            .orEmpty()
            .split(':')
            .any { it.equals(component, ignoreCase = true) }
    } catch (_: Throwable) {
        false
    }

    private fun hasAllFilesAccess(): Boolean =
        Build.VERSION.SDK_INT < Build.VERSION_CODES.R ||
            android.os.Environment.isExternalStorageManager()

    private fun currentPort(): Int =
        binding.portInput.text?.toString()?.trim()?.toIntOrNull()?.takeIf { it in 1..65535 }
            ?: StreamService.DEFAULT_PORT

    private fun refreshUi(running: Boolean) {
        binding.startButton.isEnabled = !running
        binding.stopButton.isEnabled = running
        val canSelfRestore = ContextCompat.checkSelfPermission(
            this, Manifest.permission.WRITE_SECURE_SETTINGS
        ) == PackageManager.PERMISSION_GRANTED
        binding.accessibilityStatus.text = buildString {
            append("Accessibility service: ")
            append(if (ControlAccessibilityService.isEnabled) "ON" else "OFF")
            append(if (canSelfRestore) " (auto-restores after updates)" else " (turns off on app update)")
        }
        val filesOn = hasAllFilesAccess()
        binding.fileAccessStatus.text = "File transfer access: " + if (filesOn) "ON" else "OFF"
        binding.enableFilesButton.isEnabled = !filesOn

        val msgsOn = hasNotificationAccess()
        binding.messagesStatus.text = "Message access: " + if (msgsOn) "ON" else "OFF"
        binding.enableMessagesButton.isEnabled = !msgsOn

        // Reading and sending are separate grants, so check both. Gating the
        // button on reading alone left it greyed out for anyone who already had
        // history working, with no way left to ask for the sending permission.
        val readOn = granted(Manifest.permission.READ_SMS)
        val sendOn = granted(Manifest.permission.SEND_SMS)
        binding.smsStatus.text = "SMS history: " + when {
            readOn && sendOn -> "ON"
            readOn -> "read only"
            else -> "OFF"
        }
        binding.enableSmsButton.isEnabled = !readOn || !sendOn
        if (running) {
            val port = currentPort()
            val ips = localIpv4Addresses()
            binding.addressText.text = if (ips.isEmpty()) {
                "Connected to Wi-Fi? No local IPv4 found."
            } else {
                "On your PC, connect to:\n" + ips.joinToString("\n") { "    $it : $port" }
            }
        } else {
            binding.addressText.text = "Server stopped."
        }
    }

    private fun localIpv4Addresses(): List<String> {
        val result = mutableListOf<String>()
        try {
            for (nif in NetworkInterface.getNetworkInterfaces()) {
                if (!nif.isUp || nif.isLoopback) continue
                for (addr in nif.inetAddresses) {
                    if (addr is Inet4Address && !addr.isLoopbackAddress && addr.isSiteLocalAddress) {
                        result.add(addr.hostAddress ?: continue)
                    }
                }
            }
        } catch (_: Throwable) {
        }
        if (result.isEmpty()) {
            // Fall back to the Wi-Fi service address.
            try {
                val wifi = applicationContext.getSystemService(Context.WIFI_SERVICE)
                        as android.net.wifi.WifiManager
                @Suppress("DEPRECATION")
                val ip = wifi.connectionInfo.ipAddress
                if (ip != 0) {
                    @Suppress("DEPRECATION")
                    result.add(Formatter.formatIpAddress(ip))
                }
            } catch (_: Throwable) {
            }
        }
        return result
    }

    private fun toast(msg: String) = Toast.makeText(this, msg, Toast.LENGTH_SHORT).show()

    // StreamService.Listener
    override fun onLog(message: String) {
        runOnUiThread {
            logLines.addLast(message)
            while (logLines.size > 8) logLines.removeFirst()
            binding.logText.text = logLines.joinToString("\n")
        }
    }

    override fun onStateChanged(running: Boolean) {
        runOnUiThread { refreshUi(running) }
    }
}
