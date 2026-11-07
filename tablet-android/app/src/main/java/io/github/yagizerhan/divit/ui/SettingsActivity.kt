// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// Connection screen: live status, USB / Wi-Fi choice (manual, decision K-05), PC address, pairing, drawing screen
// brightness. The UI follows the system language. Choices apply immediately; there is no separate "Save".

package io.github.yagizerhan.divit.ui

import android.app.Activity
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.View
import android.view.inputmethod.EditorInfo
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.SeekBar
import android.widget.TextView
import android.widget.Toast
import io.github.yagizerhan.divit.DivitApp
import io.github.yagizerhan.divit.R
import io.github.yagizerhan.divit.net.Discovery
import io.github.yagizerhan.divit.net.FoundPc

class SettingsActivity : Activity() {

    private val app get() = application as DivitApp
    private lateinit var usbCard: View
    private lateinit var wifiCard: View
    private lateinit var wifiBox: View
    private lateinit var hostField: EditText
    private lateinit var wifiHint: TextView
    private lateinit var foundList: LinearLayout
    private lateinit var searchStatus: TextView
    @Volatile private var searching = false
    private val ui = Handler(Looper.getMainLooper())

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_settings)
        val p = app.prefs

        val choices = findViewById<LinearLayout>(R.id.choices)
        usbCard = choiceCard(choices, getString(R.string.choice_usb_title), getString(R.string.choice_usb_body)) { select(false) }
        val gap = View(this).apply { layoutParams = LinearLayout.LayoutParams((12 * resources.displayMetrics.density).toInt(), 1) }
        choices.addView(gap)
        wifiCard = choiceCard(choices, getString(R.string.choice_wifi_title), getString(R.string.choice_wifi_body)) { select(true) }

        wifiBox = findViewById(R.id.wifiBox)
        wifiHint = findViewById(R.id.wifiHint)
        hostField = findViewById<EditText>(R.id.hostField).apply {
            setText(p.host)
            setOnEditorActionListener { _, action, _ ->
                if (action == EditorInfo.IME_ACTION_DONE) { applyHost(); false } else false
            }
            setOnFocusChangeListener { _, has -> if (!has) applyHost() }
        }

        foundList = findViewById(R.id.foundList)
        searchStatus = findViewById(R.id.searchStatus)
        findViewById<Button>(R.id.searchButton).setOnClickListener { search() }

        findViewById<Button>(R.id.forgetButton).setOnClickListener {
            // Guard against an accidental tap: after deleting, the code shown on the PC must be entered again.
            android.app.AlertDialog.Builder(this)
                .setTitle(R.string.forget_title)
                .setMessage(R.string.forget_message)
                .setPositiveButton(R.string.forget_confirm) { _, _ ->
                    p.forgetPairings()
                    Toast.makeText(this, R.string.forget_done, Toast.LENGTH_SHORT).show()
                    reconnect()
                    renderChoice()
                }
                .setNegativeButton(R.string.cancel, null)
                .show()
        }
        findViewById<Button>(R.id.drawButton).setOnClickListener { applyHost(); finish() }

        findViewById<SeekBar>(R.id.brightness).apply {
            progress = if (p.brightness < 0) 100 else (p.brightness * 100).toInt()
            setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
                override fun onProgressChanged(sb: SeekBar, v: Int, fromUser: Boolean) {
                    if (!fromUser) return
                    // 100 = system brightness; below: specific to the drawing screen (at least 2%, never fully dark)
                    p.brightness = if (v >= 100) -1f else maxOf(v, 2) / 100f
                    window.attributes = window.attributes.apply { screenBrightness = p.brightness }
                }
                override fun onStartTrackingTouch(sb: SeekBar) {}
                override fun onStopTrackingTouch(sb: SeekBar) {}
            })
        }

        findViewById<TextView>(R.id.helpText).setText(R.string.help_text)

        if (!p.configured) { p.configured = true; select(p.wifi) }   // first launch: save the default (USB)
        renderChoice()
    }

    override fun onResume() {
        super.onResume()
        app.statusListener = { renderStatus() }
        app.pairingListener = { h, wrong -> askPairingCode(app, h, wrong) }
        renderStatus()
        ui.post(ticker)
        if (app.prefs.wifi) search()
    }

    override fun onPause() {
        ui.removeCallbacks(ticker)
        app.statusListener = null
        app.pairingListener = null
        super.onPause()
    }

    private val ticker = object : Runnable { override fun run() { renderStatus(); ui.postDelayed(this, 500) } }

    private fun choiceCard(parent: LinearLayout, title: String, body: String, onClick: () -> Unit): View {
        val v = layoutInflater.inflate(R.layout.choice_card, parent, false)
        v.findViewById<TextView>(R.id.choiceTitle).text = title
        v.findViewById<TextView>(R.id.choiceBody).text = body
        v.setOnClickListener { onClick() }
        parent.addView(v)
        return v
    }

    private fun select(wifi: Boolean) {
        val p = app.prefs
        val changed = p.wifi != wifi
        p.wifi = wifi
        renderChoice()
        if (wifi) search()
        if (wifi && p.host.isEmpty()) return   // wait for a search result or a typed address
        if (changed || !app.client.running) reconnect()
    }

    private fun applyHost() {
        val p = app.prefs
        val h = hostField.text.toString().trim()
        if (h == p.host) return
        if (h.isNotEmpty() && h in ownIPv4()) {
            hostField.error = getString(R.string.own_address_error)
            return
        }
        p.host = h
        p.port = io.github.yagizerhan.divit.protocol.Wire.PORT
        p.hostName = ""         // typed by hand: the name is unknown (saved once connected)
        p.expectedHostId = ""   // new address: allow pairing with the first PC that answers (then lock to it)
        if (p.wifi && h.isNotEmpty()) reconnect()
        renderChoice()
    }

    /** Look for Divit PCs on the network (SPEC §7) and list them. */
    private fun search() {
        if (searching) return
        searching = true
        searchStatus.setText(R.string.searching)
        Thread {
            val pcs = runCatching { Discovery.find() }.getOrDefault(emptyList())
            ui.post {
                searching = false
                if (isFinishing || isDestroyed) return@post
                showFound(pcs)
            }
        }.start()
    }

    private fun showFound(pcs: List<FoundPc>) {
        val p = app.prefs
        foundList.removeAllViews()
        searchStatus.setText(if (pcs.isEmpty()) R.string.search_none else R.string.search_tap)
        for (pc in pcs) {
            val v = layoutInflater.inflate(R.layout.found_pc, foundList, false)
            v.findViewById<TextView>(R.id.pcName).text = pc.name
            v.findViewById<TextView>(R.id.pcAddress).text = pc.address
            v.isSelected = pc.address == p.host
            v.setOnClickListener {
                p.host = pc.address; p.port = pc.port; p.hostName = pc.name
                p.expectedHostId = pc.hostId   // lock to the chosen PC (empty for an older PC: learned on first connect)
                hostField.setText(pc.address)
                reconnect()
                showFound(pcs)
                renderChoice()
            }
            foundList.addView(v)
        }
    }

    /** Connection type / address changed: reconnect (the Wi-Fi lock follows the connection type). */
    private fun reconnect() {
        val p = app.prefs
        app.stopLink()
        if (p.wifi && p.host.isEmpty()) return
        app.startLink(p.endpoint())
    }

    private fun renderChoice() {
        val p = app.prefs
        usbCard.isSelected = !p.wifi
        wifiCard.isSelected = p.wifi
        wifiBox.visibility = if (p.wifi) View.VISIBLE else View.GONE
        wifiHint.text = getString(R.string.wifi_hint_address, ownIPv4().joinToString().ifEmpty { "—" }) + "\n" +
            getString(if (p.pairedWithExpected) R.string.wifi_hint_paired else R.string.wifi_hint_unpaired)
    }

    private fun renderStatus() {
        val v = describe(app.client.status, app.prefs.wifi)
        findViewById<View>(R.id.stateDot).tintDot(v.colorRes)
        findViewById<TextView>(R.id.stateTitle).text = v.title
        findViewById<TextView>(R.id.stateDetail).text = v.detail
    }
}
