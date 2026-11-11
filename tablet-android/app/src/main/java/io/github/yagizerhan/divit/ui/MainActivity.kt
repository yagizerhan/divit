// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// Drawing screen: an almost empty surface. Status pill top left (fades while the pen is near), settings top right,
// an explanatory card in the middle while disconnected.

package io.github.yagizerhan.divit.ui

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.View
import android.view.WindowInsets
import android.view.WindowInsetsController
import android.view.WindowManager
import android.widget.FrameLayout
import android.widget.TextView
import io.github.yagizerhan.divit.DivitApp
import io.github.yagizerhan.divit.R
import io.github.yagizerhan.divit.net.LinkState
import io.github.yagizerhan.divit.net.LinkStatus

class MainActivity : Activity() {

    private val app get() = application as DivitApp
    private lateinit var surface: DrawingSurface
    private lateinit var pill: View
    private lateinit var dot: View
    private lateinit var statusText: TextView
    private lateinit var statusDetail: TextView
    private lateinit var offline: View
    private lateinit var offlineTitle: TextView
    private lateinit var offlineMessage: TextView
    private val ui = Handler(Looper.getMainLooper())
    private var link = LinkStatus(LinkState.DISCONNECTED)
    private var notConnectedSince = 0L
    /** Status pill: only a colored dot by default; tapping shows the details (easy on the eyes). */
    private var expanded = false
    private val collapse = Runnable { setExpanded(false) }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        setContentView(R.layout.activity_main)

        surface = DrawingSurface(this, app.ring) { w, h, wMm, hMm -> app.client.setSurface(w, h, wMm, hMm) }
        findViewById<FrameLayout>(R.id.root).addView(surface, 0)
        pill = findViewById(R.id.statusPill)
        dot = findViewById(R.id.statusDot)
        statusText = findViewById(R.id.statusText)
        statusDetail = findViewById(R.id.statusDetail)
        offline = findViewById(R.id.offlineCard)
        offlineTitle = findViewById(R.id.offlineTitle)
        offlineMessage = findViewById(R.id.offlineMessage)
        (pill as android.view.ViewGroup).layoutTransition = android.animation.LayoutTransition()
        pill.setOnClickListener { setExpanded(!expanded) }
        setExpanded(false)
        val openSettings = View.OnClickListener { startActivity(Intent(this, SettingsActivity::class.java)) }
        findViewById<View>(R.id.settingsButton).setOnClickListener(openSettings)
        findViewById<View>(R.id.offlineButton).setOnClickListener(openSettings)
        // Tapping the card reconnects: after the PC picks another tablet (BYE 3) the client does not reconnect by itself.
        offline.setOnClickListener { connectIfConfigured() }
    }

    override fun onResume() {
        super.onResume()
        window.insetsController?.let {
            it.hide(WindowInsets.Type.systemBars())
            it.systemBarsBehavior = WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        }
        window.attributes = window.attributes.apply { screenBrightness = app.prefs.brightness }

        app.statusListener = { s -> link = s; surface.area = s.area; render() }
        app.pairingListener = { h, prompt -> askPairingCode(app, h, prompt) }
        link = app.client.status
        surface.area = link.area

        if (!connectIfConfigured())
            startActivity(Intent(this, SettingsActivity::class.java))   // first launch: choose a connection type first
        ui.post(ticker)
    }

    /** Connects if a connection type was chosen (does nothing if already connected). */
    private fun connectIfConfigured(): Boolean {
        val p = app.prefs
        if (!p.configured || (p.wifi && p.host.isEmpty())) return false
        app.startLink(p.endpoint())
        return true
    }

    override fun onPause() {
        ui.removeCallbacks(ticker)
        ui.removeCallbacks(collapse)
        app.statusListener = null
        app.pairingListener = null
        super.onPause()
    }

    @Deprecated("Activity.onBackPressed")
    override fun onBackPressed() {
        // Back = leave the app and close the connection (the PC releases the pen).
        app.stopLink()
        @Suppress("DEPRECATION") super.onBackPressed()
    }

    private fun setExpanded(on: Boolean) {
        expanded = on
        statusText.visibility = if (on) View.VISIBLE else View.GONE
        statusDetail.visibility = if (on) View.VISIBLE else View.GONE
        val dp = resources.displayMetrics.density
        val h = (12 * dp).toInt()
        if (on) pill.setPadding((14 * dp).toInt(), (8 * dp).toInt(), (16 * dp).toInt(), (8 * dp).toInt()) else pill.setPadding(h, h, h, h)
        ui.removeCallbacks(collapse)
        if (on) ui.postDelayed(collapse, 10_000)   // closes by itself if left open
    }

    private val ticker = object : Runnable {
        override fun run() { render(); ui.postDelayed(this, 150) }
    }

    private fun render() {
        val s = link
        val v = describe(s, app.prefs.wifi)
        dot.tintDot(v.colorRes)
        statusText.text = if (s.state == LinkState.CONNECTED) s.hostName else v.title
        statusDetail.text = if (s.state == LinkState.CONNECTED) v.detail else ""

        // The pill fades while the pen is near, so it does not get in the way of drawing.
        val penNear = surface.penPresence == PenPresence.HOVER || surface.penPresence == PenPresence.CONTACT
        pill.animate().alpha(if (penNear) 0.25f else 1f).setDuration(200).start()

        // Short drops (reconnect < 1.5 s) must not make the card blink.
        val now = System.currentTimeMillis()
        if (s.state == LinkState.CONNECTED) notConnectedSince = 0 else if (notConnectedSince == 0L) notConnectedSince = now
        val showCard = s.state != LinkState.CONNECTED && now - notConnectedSince > 1500
        offline.visibility = if (showCard) View.VISIBLE else View.GONE
        if (showCard) {
            offlineTitle.text = v.title
            offlineMessage.text = v.detail
        }
    }
}
