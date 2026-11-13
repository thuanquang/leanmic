package com.leanmic.app

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Intent
import android.content.pm.ServiceInfo
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.os.PowerManager
import android.provider.Settings
import java.io.IOException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import kotlin.concurrent.thread

/**
 * Records the microphone and streams raw 16-bit mono PCM to the LeanMic PC app over TCP.
 *
 * Phone -> PC (little-endian): "LMIC" | u32 sampleRate | u16 channels | u8 nameLen | name | PCM s16le...
 * PC -> phone: 'K' once the header is accepted, then one byte per second as a heartbeat.
 */
class MicService : Service() {

    companion object {
        const val TCP_PORT = 51235
        const val DISCOVERY_PORT = 51234
        const val EXTRA_HOST = "host"
        const val EXTRA_USB = "usb"
        const val EXTRA_NS = "ns"
        const val ACTION_STOP = "com.leanmic.app.STOP"
        private const val CHANNEL_ID = "streaming"
        private const val NOTIF_ID = 1
        private const val HEARTBEAT_TIMEOUT_MS = 4000

        @Volatile var running = false
            private set
        @Volatile var state = State.IDLE
            private set
        /** Short, human-friendly detail for the current [state]. */
        @Volatile var status = ""
            private set
        /** Peak level (0..1) of the most recent 10 ms of audio sent; for the UI's level ring. */
        @Volatile var level = 0f
            private set
        /** Software gain applied before sending (1.0 = 100%). Can change while streaming. */
        @Volatile var gain = 1.0f
        /** Called on the main thread whenever [state] or [status] changes. */
        var listener: (() -> Unit)? = null
    }

    enum class State { IDLE, SEARCHING, CONNECTING, STREAMING, RETRYING, BUSY, ERROR }

    private val main = Handler(Looper.getMainLooper())
    private var worker: Thread? = null
    @Volatile private var socket: Socket? = null
    @Volatile private var fatalError: String? = null
    /** Per-instance stop flag, so a slow-to-exit worker from a previous instance can never keep running. */
    @Volatile private var active = false
    private var wakeLock: PowerManager.WakeLock? = null
    private var wifiLock: WifiManager.WifiLock? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            stopSelf()
            return START_NOT_STICKY
        }
        // Must be called for every startForegroundService(), even a duplicate one (double-tapped Start).
        createChannel()
        startForeground(
            NOTIF_ID, buildNotification(if (running) status else "Starting…"),
            ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE
        )
        if (running) return START_NOT_STICKY

        val host = intent?.getStringExtra(EXTRA_HOST).orEmpty().trim()
        val usb = intent?.getBooleanExtra(EXTRA_USB, false) ?: false
        val ns = intent?.getBooleanExtra(EXTRA_NS, false) ?: false

        running = true
        active = true
        fatalError = null
        wakeLock = getSystemService(PowerManager::class.java)
            .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "LeanMic:stream").apply { acquire() }
        if (!usb) {
            // Stops Wi-Fi power saving from adding 100+ ms bursts of delay while the screen is off.
            wifiLock = getSystemService(WifiManager::class.java)
                .createWifiLock(WifiManager.WIFI_MODE_FULL_LOW_LATENCY, "LeanMic:wifi").apply { acquire() }
        }
        setStatus(State.CONNECTING, "Starting…")
        worker = thread(name = "leanmic-stream") { streamLoop(host, usb, ns) }
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        active = false
        running = false
        try { socket?.close() } catch (_: IOException) {}
        worker?.interrupt()
        // The loop notices within ~10 ms (one mic read); wait so the mic is free before a quick restart.
        try { worker?.join(500) } catch (_: InterruptedException) {}
        wakeLock?.let { if (it.isHeld) it.release() }
        wifiLock?.let { if (it.isHeld) it.release() }
        level = 0f
        val err = fatalError
        if (err != null) setStatus(State.ERROR, err) else setStatus(State.IDLE, "")
        super.onDestroy()
    }

    private fun setStatus(st: State, s: String) {
        state = st
        status = s
        if (st != State.STREAMING) level = 0f
        main.post {
            listener?.invoke()
            if (running) getSystemService(NotificationManager::class.java).notify(NOTIF_ID, buildNotification(s))
        }
    }

    private fun openRecorder(ns: Boolean): AudioRecord? {
        val source = if (ns) MediaRecorder.AudioSource.VOICE_COMMUNICATION else MediaRecorder.AudioSource.MIC
        for (rate in intArrayOf(48000, 44100)) {
            val min = AudioRecord.getMinBufferSize(rate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT)
            if (min <= 0) continue
            val rec = try {
                AudioRecord(source, rate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, maxOf(min, rate / 5 * 2))
            } catch (e: SecurityException) {
                return null
            } catch (e: IllegalArgumentException) {
                continue
            }
            if (rec.state == AudioRecord.STATE_INITIALIZED) return rec
            rec.release()
        }
        return null
    }

    /** The Wi-Fi network, so sockets don't get routed over mobile data when Wi-Fi has no internet. */
    private fun wifiNetwork(): Network? {
        val cm = getSystemService(ConnectivityManager::class.java)
        @Suppress("DEPRECATION")
        return cm.allNetworks.firstOrNull {
            cm.getNetworkCapabilities(it)?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) == true
        }
    }

    private fun failFatal(msg: String) {
        fatalError = msg
        setStatus(State.ERROR, msg)
        main.post { stopSelf() }
    }

    private fun streamLoop(fixedHost: String, usb: Boolean, ns: Boolean) {
        var rec = openRecorder(ns)
        if (rec == null) {
            failFatal("Couldn't open the microphone. Is another app using it?")
            return
        }
        val rate = rec.sampleRate
        val frame = ShortArray(rate / 100)        // 10 ms chunks keep latency low
        val bytes = ByteArray(frame.size * 2)

        try {
            while (active) {
                if (rec == null) {
                    // The previous recorder broke (e.g. Android's audio service restarted): open a fresh one.
                    rec = openRecorder(ns)?.takeIf { it.sampleRate == rate }
                    if (rec == null) { failFatal("The microphone stopped working. Tap to try again."); return }
                }
                val mic: AudioRecord = rec
                val target: Pair<String, String>? = when {
                    usb -> "127.0.0.1" to "PC"
                    fixedHost.isNotEmpty() -> fixedHost to fixedHost
                    else -> discover()
                }
                if (target == null) { sleep(500); continue }
                val (host, label) = target

                setStatus(State.CONNECTING, if (usb) "Over USB" else label)
                try {
                    Socket().use { s ->
                        socket = s
                        if (!usb) wifiNetwork()?.bindSocket(s)
                        s.tcpNoDelay = true
                        s.connect(InetSocketAddress(host, TCP_PORT), 3000)
                        s.getOutputStream().write(header(rate))

                        // Wait for the PC to accept; over USB, adb accepts locally even if the PC app is closed.
                        s.soTimeout = 3000
                        val input = s.getInputStream()
                        when (input.read()) {
                            'K'.code -> {}
                            'B'.code -> throw PcBusy()
                            else -> throw IOException("PC didn't accept the connection")
                        }

                        // Heartbeat watchdog: if the PC goes silent (Wi-Fi drop, PC asleep), close and reconnect.
                        s.soTimeout = HEARTBEAT_TIMEOUT_MS
                        thread(name = "leanmic-heartbeat", isDaemon = true) {
                            try { while (input.read() >= 0) { /* heartbeat */ } } catch (_: IOException) {}
                            try { s.close() } catch (_: IOException) {}
                        }

                        val out = s.getOutputStream()
                        try { mic.startRecording() } catch (e: IllegalStateException) { throw MicBroken() }
                        setStatus(State.STREAMING, if (usb) "Sending to your PC over USB" else "Sending to $label")
                        while (active) {
                            val n = mic.read(frame, 0, frame.size)
                            if (n < 0) throw MicBroken()
                            if (n == 0) continue
                            val g = gain
                            var peak = 0
                            for (i in 0 until n) {
                                val v = if (g == 1.0f) frame[i].toInt() else (frame[i] * g).toInt().coerceIn(-32768, 32767)
                                bytes[2 * i] = v.toByte()
                                bytes[2 * i + 1] = (v shr 8).toByte()
                                if (v > peak) peak = v else if (-v > peak) peak = -v
                            }
                            level = peak / 32768f
                            out.write(bytes, 0, n * 2)
                        }
                    }
                } catch (e: MicBroken) {
                    try { mic.release() } catch (_: RuntimeException) {}
                    rec = null      // reopened at the top of the loop
                    if (!active) break
                    setStatus(State.RETRYING, "The microphone was interrupted. Restarting it…")
                } catch (e: PcBusy) {
                    if (!active) break
                    setStatus(State.BUSY, "Another phone is using LeanMic on this PC. You'll connect as soon as it stops.")
                    sleep(2000)   // retry every ~3 s while busy (saves battery)
                } catch (e: IOException) {
                    if (!active) break
                    setStatus(
                        State.RETRYING,
                        if (usb) "Waiting for the PC. Check that LeanMic is open on the PC and USB debugging is on."
                        else "Lost the connection to $label. Trying again…"
                    )
                } finally {
                    socket = null
                    if (rec != null) try { mic.stop() } catch (_: IllegalStateException) {}
                }
                sleep(1000)
            }
        } finally {
            try { rec?.release() } catch (_: RuntimeException) {}
        }
    }

    /** The AudioRecord died (read error / can't start); the loop replaces it. */
    private class MicBroken : Exception()

    /** The PC already has a different phone connected. */
    private class PcBusy : IOException("PC is busy with another phone")

    /** The name the user gave the phone (e.g. "Galaxy A54 5G"), falling back to the model code. */
    private fun phoneName(): String =
        Settings.Global.getString(contentResolver, Settings.Global.DEVICE_NAME)?.takeIf { it.isNotBlank() } ?: Build.MODEL

    private fun header(rate: Int): ByteArray {
        val name = phoneName().toByteArray(Charsets.UTF_8).take(255).toByteArray()
        val h = ByteArray(4 + 4 + 2 + 1 + name.size)
        "LMIC".toByteArray(Charsets.US_ASCII).copyInto(h, 0)
        for (i in 0 until 4) h[4 + i] = (rate shr (8 * i)).toByte()
        h[8] = 1; h[9] = 0
        h[10] = name.size.toByte()
        name.copyInto(h, 11)
        return h
    }

    /** Broadcasts "LEANMIC?" on the Wi-Fi LAN; the PC app answers "LEANMIC! <pc name>". Returns (ip, label). */
    private fun discover(): Pair<String, String>? {
        setStatus(State.SEARCHING, "Make sure LeanMic is open on the PC and both are on the same Wi-Fi.")
        val net = wifiNetwork()
        if (net == null) {
            setStatus(State.RETRYING, "Turn on Wi-Fi and join the same network as your PC.")
            sleep(1500)
            return null
        }
        val lock = getSystemService(WifiManager::class.java).createMulticastLock("LeanMic:discover").apply {
            setReferenceCounted(false); acquire()
        }
        try {
            DatagramSocket().use { ds ->
                net.bindSocket(ds)
                ds.broadcast = true
                ds.soTimeout = 1000
                val q = "LEANMIC?".toByteArray(Charsets.US_ASCII)
                ds.send(DatagramPacket(q, q.size, InetAddress.getByName("255.255.255.255"), DISCOVERY_PORT))
                val buf = ByteArray(256)
                val p = DatagramPacket(buf, buf.size)
                ds.receive(p)
                val msg = String(p.data, 0, p.length, Charsets.UTF_8)
                if (msg.startsWith("LEANMIC!")) {
                    val ip = p.address.hostAddress ?: return null
                    val name = msg.removePrefix("LEANMIC!").trim()
                    return ip to (if (name.isEmpty()) ip else "$name ($ip)")
                }
            }
        } catch (_: IOException) {
        } finally {
            lock.release()
        }
        return null
    }

    private fun sleep(ms: Long) {
        try { Thread.sleep(ms) } catch (_: InterruptedException) {}
    }

    private fun createChannel() {
        val nm = getSystemService(NotificationManager::class.java)
        if (nm.getNotificationChannel(CHANNEL_ID) == null) {
            nm.createNotificationChannel(
                NotificationChannel(CHANNEL_ID, "Streaming", NotificationManager.IMPORTANCE_LOW)
            )
        }
    }

    private fun buildNotification(text: String): Notification {
        val open = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE
        )
        val stop = PendingIntent.getService(
            this, 1, Intent(this, MicService::class.java).setAction(ACTION_STOP), PendingIntent.FLAG_IMMUTABLE
        )
        return Notification.Builder(this, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_mic)
            .setContentTitle("LeanMic")
            .setContentText(text)
            .setOngoing(true)
            .setContentIntent(open)
            .addAction(Notification.Action.Builder(null, "Stop", stop).build())
            .build()
    }
}
