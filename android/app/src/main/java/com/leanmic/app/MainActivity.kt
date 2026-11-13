package com.leanmic.app

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.content.res.ColorStateList
import android.os.Build
import android.os.Bundle
import android.text.InputType
import android.view.Gravity
import android.view.View
import android.view.WindowInsetsController
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.SeekBar
import android.widget.Switch
import android.widget.TextView
import com.leanmic.app.MicService.State

class MainActivity : Activity() {

    private lateinit var p: Palette
    private lateinit var mic: MicButton
    private lateinit var title: TextView
    private lateinit var subtitle: TextView
    private lateinit var mode: Segmented
    private lateinit var hostBox: LinearLayout
    private lateinit var hostEdit: EditText
    private lateinit var nsSwitch: Switch
    private lateinit var gainValue: TextView
    private lateinit var helpBody: TextView
    private lateinit var helpHeader: TextView
    private val prefs by lazy { getSharedPreferences("leanmic", MODE_PRIVATE) }
    private var busyUntil = 0L

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        p = Palette.of(this)
        actionBar?.hide()
        styleSystemBars()

        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dpi(20), dpi(12), dpi(20), dpi(28))
        }
        fun gap(h: Int) = root.addView(View(this), LinearLayout.LayoutParams(1, dpi(h)))
        val full = { LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT) }

        root.addView(label("LeanMic", p, 20f, bold = true))

        // Hero: big mic button + status.
        mic = MicButton(this, p).apply { setOnClickListener { toggle() } }
        root.addView(mic, LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT).apply {
            gravity = Gravity.CENTER_HORIZONTAL
        })
        title = label("", p, 24f, bold = true).apply { gravity = Gravity.CENTER }
        root.addView(title, full())
        gap(4)
        subtitle = label("", p, 15f, color = p.sub).apply { gravity = Gravity.CENTER; minLines = 2 }
        root.addView(subtitle, full())
        gap(22)

        // Connection card.
        val conn = card(p)
        conn.addView(label("Connect with", p, 13f, color = p.sub))
        conn.addView(View(this), LinearLayout.LayoutParams(1, dpi(8)))
        mode = Segmented(this, p, listOf("Wi-Fi", "USB cable")) { refreshMode(); savePrefs() }
        conn.addView(mode, full())
        hostBox = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        hostBox.addView(View(this), LinearLayout.LayoutParams(1, dpi(12)))
        hostBox.addView(label("PC address", p, 13f, color = p.sub))
        hostBox.addView(View(this), LinearLayout.LayoutParams(1, dpi(6)))
        hostEdit = EditText(this).apply {
            hint = "Automatic"
            setHintTextColor(p.sub)
            setTextColor(p.text)
            textSize = 15f
            isSingleLine = true
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_URI
            background = rounded(p.hover, dp(12f))
            setPadding(dpi(14), dpi(12), dpi(14), dpi(12))
            setText(prefs.getString("host", ""))
        }
        hostBox.addView(hostEdit, full())
        hostBox.addView(label("Leave empty and LeanMic finds your PC by itself.", p, 12.5f, color = p.sub).apply {
            setPadding(0, dpi(6), 0, 0)
        })
        conn.addView(hostBox, full())
        root.addView(conn, full())
        gap(14)

        // Sound card.
        val sound = card(p)
        val gainRow = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
        gainRow.addView(label("Mic gain", p, 16f, bold = true), LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
        gainValue = label("", p, 16f, bold = true)
        gainRow.addView(gainValue)
        sound.addView(gainRow, full())
        // While streaming, the live gain is the truth (e.g. after a rotation); otherwise use the saved one.
        val gainPct = if (MicService.running) Math.round(MicService.gain * 100) else prefs.getInt("gain", 100)
        MicService.gain = gainPct / 100f
        updateGainLabel(gainPct)
        sound.addView(SeekBar(this).apply {
            max = 300
            progress = gainPct
            progressTintList = ColorStateList.valueOf(p.accent)
            thumbTintList = ColorStateList.valueOf(p.accent)
            progressBackgroundTintList = ColorStateList.valueOf(p.track)
            contentDescription = "Mic gain"
            setPadding(dpi(4), dpi(14), dpi(4), dpi(10))
            setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
                override fun onProgressChanged(sb: SeekBar, value: Int, fromUser: Boolean) {
                    // Gentle snap to 100%.
                    val v = if (fromUser && value in 96..104) 100.also { sb.progress = 100 } else value
                    MicService.gain = v / 100f
                    updateGainLabel(v)
                    // Save on every change (touch, keyboard, TalkBack); apply() is asynchronous and cheap.
                    prefs.edit().putInt("gain", v).apply()
                }
                override fun onStartTrackingTouch(sb: SeekBar) {}
                override fun onStopTrackingTouch(sb: SeekBar) {}
            })
        }, full())
        sound.addView(View(this).apply { setBackgroundColor(p.border) }, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, 1).apply {
            topMargin = dpi(6); bottomMargin = dpi(12)
        })
        val nsRow = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
        val nsText = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        nsText.addView(label("Echo & noise reduction", p, 16f, bold = true))
        nsText.addView(label("Uses the phone's call processing. Turn on if people hear an echo.", p, 13f, color = p.sub).apply {
            setPadding(0, dpi(2), dpi(12), 0)
        })
        nsRow.addView(nsText, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
        nsSwitch = Switch(this).apply {
            isChecked = prefs.getBoolean("ns", false)
            val (thumb, track) = switchTints(p)
            thumbTintList = thumb
            trackTintList = track
            contentDescription = "Echo and noise reduction"
            setOnCheckedChangeListener { _, _ -> savePrefs() }
        }
        nsRow.addView(nsSwitch)
        nsRow.setOnClickListener { if (nsSwitch.isEnabled) nsSwitch.toggle() }
        sound.addView(nsRow, full())
        root.addView(sound, full())
        gap(14)

        // Help.
        helpHeader = label("", p, 15f, bold = true, color = p.sub).apply {
            setPadding(dpi(4), dpi(10), dpi(4), dpi(10))
            setOnClickListener { setHelp(helpBody.visibility != View.VISIBLE) }
        }
        root.addView(helpHeader, full())
        helpBody = label(
            "1.  Open LeanMic on your PC (free at github.com/thuanquang/leanmic).\n\n" +
                "2.  For Wi-Fi, join the same network as the PC. For USB, turn on USB debugging in Developer options and plug in.\n\n" +
                "3.  Tap the mic. Green means you're live.\n\n" +
                "4.  On the PC, pick “CABLE Output” as the microphone in Discord, Zoom or OBS.\n\n" +
                "LeanMic keeps working with the screen off. Stop it any time from the notification.",
            p, 14f, color = p.sub
        ).apply {
            background = rounded(p.card, dp(18f), p.border, dpi(1))
            setPadding(dpi(18), dpi(16), dpi(18), dpi(16))
            setLineSpacing(0f, 1.15f)
        }
        root.addView(helpBody, full())
        setHelp(!prefs.getBoolean("seenHelp", false))

        if (prefs.getBoolean("usb", false)) mode.select(1)
        refreshMode()

        window.decorView.setBackgroundColor(p.bg)
        setContentView(ScrollView(this).apply {
            fitsSystemWindows = true
            isFillViewport = true
            setBackgroundColor(p.bg)
            addView(root)
        })
    }

    private fun styleSystemBars() {
        window.decorView.setBackgroundColor(p.bg)
        if (Build.VERSION.SDK_INT >= 30) {
            val light = WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS or WindowInsetsController.APPEARANCE_LIGHT_NAVIGATION_BARS
            window.insetsController?.setSystemBarsAppearance(if (p.dark) 0 else light, light)
        } else {
            @Suppress("DEPRECATION")
            window.decorView.systemUiVisibility = if (p.dark) 0 else
                View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR or View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR
        }
        @Suppress("DEPRECATION")
        window.statusBarColor = p.bg
        @Suppress("DEPRECATION")
        window.navigationBarColor = p.bg
    }

    private fun setHelp(open: Boolean) {
        helpBody.visibility = if (open) View.VISIBLE else View.GONE
        helpHeader.text = (if (open) "▾  " else "▸  ") + "How to connect"
        helpHeader.contentDescription = "How to connect, ${if (open) "expanded" else "collapsed"}"
    }

    private fun updateGainLabel(v: Int) {
        gainValue.text = "$v%"
        gainValue.setTextColor(if (v > 200) p.warn else p.text)
    }

    private fun refreshMode() {
        hostBox.visibility = if (mode.selected == 0) View.VISIBLE else View.GONE
    }

    private fun savePrefs() {
        prefs.edit()
            .putBoolean("usb", mode.selected == 1)
            .putString("host", hostEdit.text.toString().trim())
            .putBoolean("ns", nsSwitch.isChecked)
            .apply()
    }

    override fun onStart() {
        super.onStart()
        MicService.listener = { refresh() }
        refresh()
    }

    override fun onStop() {
        MicService.listener = null
        savePrefs()
        super.onStop()
    }

    private fun refresh() {
        val st = MicService.state
        val running = MicService.running
        mic.state = if (!running && st != State.ERROR) State.IDLE else st
        val usb = mode.selected == 1
        val (t, s) = when (mic.state) {
            State.IDLE -> "Tap to start" to
                if (usb) "Your phone becomes your PC's microphone over USB." else "Your phone becomes your PC's microphone over Wi-Fi."
            State.SEARCHING -> "Looking for your PC…" to MicService.status
            State.CONNECTING -> "Connecting…" to MicService.status
            State.STREAMING -> "You're live" to MicService.status
            State.RETRYING -> "Reconnecting…" to MicService.status
            State.BUSY -> "Your PC is busy" to MicService.status
            State.ERROR -> "Couldn't start" to MicService.status
        }
        title.text = t
        title.setTextColor(if (mic.state == State.ERROR) p.warn else p.text)
        subtitle.text = s
        val editable = !running
        mode.isEnabled = editable
        hostEdit.isEnabled = editable
        hostEdit.alpha = if (editable) 1f else 0.5f
        nsSwitch.isEnabled = editable
        if (st == State.STREAMING && !prefs.getBoolean("seenHelp", false)) {
            // First success: the instructions did their job; fold them away from now on.
            prefs.edit().putBoolean("seenHelp", true).apply()
            setHelp(false)
        }
    }

    private fun toggle() {
        // Ignore rapid repeat taps while the service changes state.
        val now = System.currentTimeMillis()
        if (now < busyUntil) return
        busyUntil = now + 800
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
            title.text = "Microphone access needed"
            subtitle.text = "LeanMic can't work without the microphone. Allow it in the app's settings."
        }
    }

    private fun start() {
        savePrefs()
        startForegroundService(
            Intent(this, MicService::class.java)
                .putExtra(MicService.EXTRA_USB, mode.selected == 1)
                .putExtra(MicService.EXTRA_HOST, hostEdit.text.toString().trim())
                .putExtra(MicService.EXTRA_NS, nsSwitch.isChecked)
        )
    }
}
