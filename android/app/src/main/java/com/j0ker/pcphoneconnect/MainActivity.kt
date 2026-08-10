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

    private val projectionLauncher =
        registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
            if (result.resultCode == RESULT_OK && result.data != null) {
                val intent = Intent(this, StreamService::class.java).apply {
                    action = StreamService.ACTION_START
                    putExtra(StreamService.EXTRA_PORT, currentPort())
                    putExtra(StreamService.EXTRA_RESULT_CODE, result.resultCode)
                    putExtra(StreamService.EXTRA_RESULT_DATA, result.data)
                }
                ContextCompat.startForegroundService(this, intent)
            } else {
                toast("Screen sharing was not granted")
            }
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
        refreshUi(StreamService.isRunning)
    }

    override fun onPause() {
        super.onPause()
        if (StreamService.listener === this) StreamService.listener = null
    }

    private fun requestStart() {
        if (!ControlAccessibilityService.isEnabled) {
            toast("Tip: enable the Accessibility service first for remote control")
        }
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

    private fun hasAllFilesAccess(): Boolean =
        Build.VERSION.SDK_INT < Build.VERSION_CODES.R ||
            android.os.Environment.isExternalStorageManager()

    private fun currentPort(): Int =
        binding.portInput.text?.toString()?.trim()?.toIntOrNull()?.takeIf { it in 1..65535 }
            ?: StreamService.DEFAULT_PORT

    private fun refreshUi(running: Boolean) {
        binding.startButton.isEnabled = !running
        binding.stopButton.isEnabled = running
        binding.accessibilityStatus.text =
            "Accessibility service: " + if (ControlAccessibilityService.isEnabled) "ON" else "OFF"
        val filesOn = hasAllFilesAccess()
        binding.fileAccessStatus.text = "File transfer access: " + if (filesOn) "ON" else "OFF"
        binding.enableFilesButton.isEnabled = !filesOn
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
