package com.leanmic.app

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Typeface
import android.os.Build
import android.os.Bundle
import android.text.InputType
import android.view.View
import android.widget.Button
import android.widget.CheckBox
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.RadioButton
import android.widget.RadioGroup
import android.widget.ScrollView
import android.widget.SeekBar
import android.widget.TextView

class MainActivity : Activity() {

    private lateinit var statusView: TextView
    private lateinit var wifiRadio: RadioButton
    private lateinit var usbRadio: RadioButton
    private lateinit var hostEdit: EditText
    private lateinit var nsCheck: CheckBox
    private lateinit var gainLabel: TextView
    private lateinit var button: Button
    private val prefs by lazy { getSharedPreferences("leanmic", MODE_PRIVATE) }

    private fun dp(v: Int) = (v * resources.displayMetrics.density).toInt()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(20), dp(20), dp(20), dp(20))
        }
        fun gap(h: Int) = root.addView(View(this), LinearLayout.LayoutParams(1, dp(h)))

        // (The system action bar already shows the app name.)
        statusView = TextView(this).apply { textSize = 18f; typeface = Typeface.DEFAULT_BOLD }
        root.addView(statusView)
        gap(20)

        val group = RadioGroup(this).apply { orientation = RadioGroup.HORIZONTAL }
        wifiRadio = RadioButton(this).apply { id = View.generateViewId(); text = "Wi-Fi"; textSize = 17f }
        usbRadio = RadioButton(this).apply { id = View.generateViewId(); text = "USB"; textSize = 17f }
        group.addView(wifiRadio)
        group.addView(usbRadio)
        root.addView(group)

        hostEdit = EditText(this).apply {
            hint = "PC IP address (leave empty to find it automatically)"
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_URI
            isSingleLine = true
            setText(prefs.getString("host", ""))
        }
        root.addView(hostEdit)
        group.setOnCheckedChangeListener { _, _ ->
            hostEdit.visibility = if (wifiRadio.isChecked) View.VISIBLE else View.GONE
        }
        if (prefs.getBoolean("usb", false)) usbRadio.isChecked = true else wifiRadio.isChecked = true
        gap(8)

        nsCheck = CheckBox(this).apply {
            text = "Noise suppression / echo cancel"
            isChecked = prefs.getBoolean("ns", false)
        }
        root.addView(nsCheck)
        gap(12)

        val gainPct = prefs.getInt("gain", 100)
        MicService.gain = gainPct / 100f
        gainLabel = TextView(this).apply { text = "Mic gain: $gainPct%" }
        root.addView(gainLabel)
        root.addView(SeekBar(this).apply {
            max = 300
            progress = gainPct
            setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
                override fun onProgressChanged(sb: SeekBar, p: Int, fromUser: Boolean) {
                    MicService.gain = p / 100f
                    gainLabel.text = "Mic gain: $p%"
                }
                override fun onStartTrackingTouch(sb: SeekBar) {}
                override fun onStopTrackingTouch(sb: SeekBar) { prefs.edit().putInt("gain", sb.progress).apply() }
            })
        })
        gap(20)

        button = Button(this).apply { textSize = 20f; setOnClickListener { toggle() } }
        root.addView(button, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, dp(64)))
        gap(24)

        root.addView(TextView(this).apply {
            alpha = 0.7f
            text = "USB: turn on USB debugging (Developer options), plug the phone into the PC and keep " +
                "LeanMic open on the PC.\n\nWi-Fi: phone and PC on the same network.\n\n" +
                "In Discord, Zoom, OBS, etc. on the PC, choose “CABLE Output” as the microphone."
        })

        setContentView(ScrollView(this).apply { fitsSystemWindows = true; addView(root) })
    }

    override fun onStart() {
        super.onStart()
        MicService.listener = { refresh() }
        refresh()
    }

    override fun onStop() {
        MicService.listener = null
        super.onStop()
    }

    private fun refresh() {
        val running = MicService.running
        statusView.text = MicService.status
        button.text = if (running) "Stop" else "Start"
        button.isEnabled = true
        for (v in listOf(wifiRadio, usbRadio, hostEdit, nsCheck)) v.isEnabled = !running
    }

    private fun toggle() {
        // Ignore extra taps until the service reports its new state (re-enabled in refresh()).
        button.isEnabled = false
        button.postDelayed({ button.isEnabled = true }, 1500)
        if (MicService.running) {
            stopService(Intent(this, MicService::class.java))
            return
        }
        val needed = mutableListOf(Manifest.permission.RECORD_AUDIO)
        if (Build.VERSION.SDK_INT >= 33) needed += Manifest.permission.POST_NOTIFICATIONS
        val missing = needed.filter { checkSelfPermission(it) != PackageManager.PERMISSION_GRANTED }
        if (missing.isEmpty()) start() else requestPermissions(missing.toTypedArray(), 1)
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED) {
            start()
        } else {
            statusView.text = "LeanMic needs microphone permission to work."
            button.isEnabled = true
        }
    }

    private fun start() {
        val usb = usbRadio.isChecked
        val host = hostEdit.text.toString().trim()
        prefs.edit()
            .putBoolean("usb", usb)
            .putString("host", host)
            .putBoolean("ns", nsCheck.isChecked)
            .apply()
        startForegroundService(
            Intent(this, MicService::class.java)
                .putExtra(MicService.EXTRA_USB, usb)
                .putExtra(MicService.EXTRA_HOST, host)
                .putExtra(MicService.EXTRA_NS, nsCheck.isChecked)
        )
    }
}
