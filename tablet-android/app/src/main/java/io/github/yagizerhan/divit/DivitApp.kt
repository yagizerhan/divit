// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// Application: persistent settings and the connection objects that live as long as the process (ring buffer + TransportClient).
// The connection runs only while a Divit screen is visible: pen input can only be captured in the foreground, so there is
// nothing to do in the background. HIDDEN_GRACE_MS after the last screen goes away the link is closed (the PC releases the pen).

package io.github.yagizerhan.divit

import android.app.Activity
import android.app.Application
import android.content.Context
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import io.github.yagizerhan.divit.capture.SampleRing
import io.github.yagizerhan.divit.net.Endpoint
import io.github.yagizerhan.divit.net.Identity
import io.github.yagizerhan.divit.net.LinkStatus
import io.github.yagizerhan.divit.net.TransportClient
import java.security.SecureRandom

class Prefs(ctx: Context) : Identity {
    private val sp = ctx.getSharedPreferences("divit", Context.MODE_PRIVATE)

    var wifi: Boolean
        get() = sp.getBoolean("wifi", false)
        set(v) = sp.edit().putBoolean("wifi", v).apply()

    /** Was a connection type chosen on first launch? (Older installs with a "wifi" key count as chosen.) */
    var configured: Boolean
        get() = sp.getBoolean("configured", sp.contains("wifi"))
        set(v) = sp.edit().putBoolean("configured", v).apply()

    /** Drawing screen brightness: -1 = system, otherwise 0.02…1. */
    var brightness: Float
        get() = sp.getFloat("brightness", -1f)
        set(v) = sp.edit().putFloat("brightness", v).apply()

    /** Name of the PC connected over Wi-Fi: to find it again by discovery if its IP changes (SPEC §7). */
    var hostName: String
        get() = sp.getString("hostName", "") ?: ""
        set(v) = sp.edit().putString("hostName", v).apply()

    var port: Int
        get() = sp.getInt("port", io.github.yagizerhan.divit.protocol.Wire.PORT)
        set(v) = sp.edit().putInt("port", v).apply()

    var host: String
        get() = sp.getString("host", "") ?: ""
        set(v) = sp.edit().putString("host", v.trim()).apply()

    override val deviceId: ByteArray
        get() = sp.getString("deviceId", null)?.let(::unhex) ?: ByteArray(16).also {
            SecureRandom().nextBytes(it)
            sp.edit().putString("deviceId", hex(it)).apply()
        }

    // Keys per PC ID (SPEC §3.2): "pk.<hostId>" Wi-Fi pairing key, "uk.<hostId>" USB key.
    // The old single "pairToken" is no longer used (pairing again is required).
    override fun key(usb: Boolean, hostId: ByteArray): ByteArray? =
        sp.getString((if (usb) "uk." else "pk.") + hex(hostId), null)?.let(::unhex)?.takeIf { it.size == io.github.yagizerhan.divit.protocol.Auth.KEY }

    override fun savePairKey(hostId: ByteArray, key: ByteArray) { sp.edit().putString("pk." + hex(hostId), hex(key)).apply() }
    override fun forgetPairKey(hostId: ByteArray) { sp.edit().remove("pk." + hex(hostId)).apply() }

    /** USB key: sent by the PC over adb (UsbKeyReceiver). commit(): written to disk before the receiver returns. */
    fun saveUsbKey(hostId: ByteArray, key: ByteArray) = sp.edit().putString("uk." + hex(hostId), hex(key)).commit()

    override var expectedHostId: String
        get() = sp.getString("expectedHostId", "") ?: ""
        set(v) = sp.edit().putString("expectedHostId", v).apply()

    /** Is there a pairing key for the selected PC (for the UI)? */
    val pairedWithExpected: Boolean get() = expectedHostId.isNotEmpty() && sp.contains("pk.$expectedHostId")

    /** Delete all Wi-Fi pairings (USB keys stay: the PC can resend them over adb). */
    fun forgetPairings() {
        val e = sp.edit()
        sp.all.keys.filter { it.startsWith("pk.") || it == "pairToken" }.forEach(e::remove)
        e.apply()
    }

    override val model: String get() = "${Build.MANUFACTURER} ${Build.MODEL}"

    fun endpoint() = if (wifi) Endpoint(true, host, port) else Endpoint(false, "127.0.0.1")

    private fun hex(b: ByteArray) = b.joinToString("") { "%02x".format(it) }
    private fun unhex(s: String) = ByteArray(s.length / 2) { s.substring(it * 2, it * 2 + 2).toInt(16).toByte() }
}

class DivitApp : Application() {
    companion object {
        /** Short absences (notification shade, recents peek, a permission dialog) do not drop the link. */
        const val HIDDEN_GRACE_MS = 5000L
    }

    lateinit var prefs: Prefs; private set
    val ring = SampleRing()
    lateinit var client: TransportClient; private set

    private val main = Handler(Looper.getMainLooper())
    private var visibleScreens = 0
    private val stopWhenHidden = Runnable { stopLink() }
    private var wifiLock: WifiManager.WifiLock? = null

    /** Connect (or keep the current connection if it is to the same endpoint). Main thread. */
    fun startLink(ep: Endpoint) {
        main.removeCallbacks(stopWhenHidden)
        holdWifiLock(ep.wifi)
        client.start(ep)
    }

    /** Close the connection (the PC releases the pen). Main thread. */
    fun stopLink() {
        main.removeCallbacks(stopWhenHidden)
        client.stop()
        holdWifiLock(false)
    }

    /** Wi-Fi low-latency lock while connected over Wi-Fi (it only takes effect while the app is in the foreground). */
    private fun holdWifiLock(on: Boolean) {
        if (on && wifiLock == null) {
            wifiLock = getSystemService(WifiManager::class.java)
                .createWifiLock(WifiManager.WIFI_MODE_FULL_LOW_LATENCY, "divit")
                .apply { setReferenceCounted(false); acquire() }
        } else if (!on) {
            wifiLock?.let { if (it.isHeld) it.release() }
            wifiLock = null
        }
    }
    /** UI listeners (called on the main thread). */
    var statusListener: ((LinkStatus) -> Unit)? = null
    var pairingListener: ((host: String, prompt: io.github.yagizerhan.divit.net.PairPrompt) -> Unit)? = null

    override fun onCreate() {
        super.onCreate()
        prefs = Prefs(this)
        // Versions up to 0.1.0 had a foreground service with a "connection" notification channel: remove the leftover.
        getSystemService(android.app.NotificationManager::class.java)?.deleteNotificationChannel("connection")
        // Count visible screens; when none is left (Home, another app, swiped away) close the link after a short grace.
        registerActivityLifecycleCallbacks(object : ActivityLifecycleCallbacks {
            override fun onActivityStarted(a: Activity) { visibleScreens++; main.removeCallbacks(stopWhenHidden) }
            override fun onActivityStopped(a: Activity) {
                if (--visibleScreens <= 0) { visibleScreens = 0; main.postDelayed(stopWhenHidden, HIDDEN_GRACE_MS) }
            }
            override fun onActivityCreated(a: Activity, b: Bundle?) {}
            override fun onActivityResumed(a: Activity) {}
            override fun onActivityPaused(a: Activity) {}
            override fun onActivitySaveInstanceState(a: Activity, b: Bundle) {}
            override fun onActivityDestroyed(a: Activity) {}
        })
        client = TransportClient(
            ring, prefs,
            onStatus = { s ->
                if (s.state == io.github.yagizerhan.divit.net.LinkState.CONNECTED && prefs.wifi && s.hostName.isNotEmpty()) prefs.hostName = s.hostName
                main.post { statusListener?.invoke(s) }
            },
            askPairingCode = { h, prompt -> main.post { pairingListener?.invoke(h, prompt) ?: client.submitPairingCode(null) } },
            rediscover = { ep ->
                // Search by PC ID first (so we do not switch to another PC with the same name); by name if the ID is unknown.
                // The PC found still has to prove its identity on connect; an impostor cannot. Not saved here (see onConnected).
                val id = prefs.expectedHostId
                val name = prefs.hostName
                if (id.isEmpty() && name.isEmpty()) null
                else io.github.yagizerhan.divit.net.Discovery.find(1200).firstOrNull { if (id.isNotEmpty()) it.hostId == id else it.name == name }?.let { pc ->
                    Endpoint(true, pc.address, pc.port)
                }
            },
            // The PC proved itself at this address: now it is safe to remember it (a forged discovery reply cannot get here).
            onConnected = { ep ->
                if (ep.wifi && prefs.wifi && (prefs.host != ep.host || prefs.port != ep.port)) { prefs.host = ep.host; prefs.port = ep.port }
            },
        )
    }
}
