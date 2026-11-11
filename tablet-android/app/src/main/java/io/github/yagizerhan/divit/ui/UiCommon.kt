// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// Shared by the screens: link status → localized text/color, the pairing dialog, the tablet's IP addresses.

package io.github.yagizerhan.divit.ui

import android.app.Activity
import android.app.AlertDialog
import android.content.Context
import android.content.res.ColorStateList
import android.text.InputFilter
import android.text.InputType
import android.view.Gravity
import android.view.View
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.TextView
import io.github.yagizerhan.divit.DivitApp
import io.github.yagizerhan.divit.R
import io.github.yagizerhan.divit.net.LinkState
import io.github.yagizerhan.divit.net.LinkStatus
import io.github.yagizerhan.divit.net.PairPrompt
import io.github.yagizerhan.divit.net.Reason

data class StatusView(val colorRes: Int, val title: String, val detail: String)

fun Context.describe(s: LinkStatus, wifi: Boolean): StatusView = when (s.state) {
    LinkState.CONNECTED -> StatusView(
        R.color.ok,
        getString(R.string.st_connected, s.hostName),
        buildString {
            append(s.detail)
            if (s.rttUs >= 0) append(" · ").append(getString(R.string.st_rtt, s.rttUs / 1000.0))
            s.area?.monitorName?.takeIf { it.isNotEmpty() }?.let { append(" · ${it.removePrefix("\\\\.\\")}") }
        },
    )
    LinkState.CONNECTING -> StatusView(
        R.color.wait, getString(R.string.st_connecting),
        when {
            s.reason == Reason.FOUND_AT_NEW_ADDRESS -> getString(R.string.st_found_new, s.detail)
            wifi -> getString(R.string.st_connecting_wifi, s.detail)
            else -> getString(R.string.st_connecting_usb)
        },
    )
    LinkState.PAIRING -> StatusView(
        R.color.wait, getString(R.string.st_pairing),
        getString(if (s.reason == Reason.PAIRING_INVALID) R.string.st_pairing_invalid else R.string.st_pairing_detail),
    )
    LinkState.DISCONNECTED -> StatusView(R.color.bad, getString(R.string.st_disconnected), reasonText(s.reason, s.detail))
}

/** Localized explanation for a disconnect reason. */
fun Context.reasonText(r: Reason, detail: String): String = when (r) {
    Reason.PC_NOT_RUNNING_USB -> getString(R.string.r_pc_not_running_usb)
    Reason.WIFI_NOT_ALLOWED -> getString(R.string.r_wifi_not_allowed)
    Reason.UNREACHABLE -> getString(R.string.r_unreachable, detail)
    Reason.NO_ANSWER -> getString(R.string.r_no_answer, detail)
    Reason.TIMEOUT -> getString(R.string.r_timeout)
    Reason.PROTOCOL_ERROR -> getString(R.string.r_protocol_error, detail)
    Reason.VERSION_MISMATCH -> getString(R.string.r_version)
    Reason.DIFFERENT_PC -> getString(R.string.r_different_pc)
    Reason.PC_NOT_VERIFIED -> getString(R.string.r_pc_not_verified)
    Reason.USB_KEY_MISMATCH -> getString(R.string.r_usb_key_mismatch)
    Reason.USB_KEY_PENDING -> getString(R.string.r_usb_key_pending)
    Reason.PC_CLOSED -> getString(R.string.r_pc_closed)
    Reason.REPLACED -> getString(R.string.r_replaced)
    Reason.CONNECTION_LOST -> getString(R.string.r_connection_lost, detail)
    Reason.PAIRING_CANCELLED -> getString(R.string.r_pairing_cancelled)
    Reason.TOO_MANY_WRONG_CODES -> getString(R.string.r_too_many_wrong)
    Reason.NONE, Reason.FOUND_AT_NEW_ADDRESS, Reason.PAIRING_INVALID -> getString(R.string.r_none)
}

fun View.tintDot(colorRes: Int) {
    backgroundTintList = ColorStateList.valueOf(context.getColor(colorRes))
}

/** The tablet's local IPv4 addresses (for the Wi-Fi hint and the IP-conflict warning). */
fun ownIPv4(): List<String> =
    java.net.NetworkInterface.getNetworkInterfaces()?.toList().orEmpty()
        .filter { it.isUp && !it.isLoopback }
        .flatMap { it.inetAddresses.toList() }
        .filterIsInstance<java.net.Inet4Address>()
        .mapNotNull { it.hostAddress }

private var openDialog: AlertDialog? = null

/** Asks for the 6-digit pairing code shown on the PC. Opens on whichever screen is in front. */
fun Activity.askPairingCode(app: DivitApp, host: String, prompt: PairPrompt) {
    openDialog?.dismiss()
    val pad = (24 * resources.displayMetrics.density).toInt()
    val input = EditText(this).apply {
        inputType = InputType.TYPE_CLASS_NUMBER
        filters = arrayOf(InputFilter.LengthFilter(6))
        hint = "••••••"
        textSize = 34f
        letterSpacing = 0.3f
        gravity = Gravity.CENTER
    }
    val body = LinearLayout(this).apply {
        orientation = LinearLayout.VERTICAL
        setPadding(pad, pad / 2, pad, 0)
        addView(TextView(context).apply {
            text = getString(when (prompt) {
                PairPrompt.FIRST -> R.string.pair_info
                PairPrompt.WRONG -> R.string.pair_wrong
                PairPrompt.PAUSED -> R.string.pair_paused
                PairPrompt.PC_FORGOT -> R.string.pair_pc_forgot
            })
            textSize = 15f
        })
        addView(input)
    }
    openDialog = AlertDialog.Builder(this)
        .setTitle(getString(R.string.pair_title, host))
        .setView(body)
        .setPositiveButton(R.string.pair_ok) { _, _ -> app.client.submitPairingCode(input.text.toString().trim().toLongOrNull()) }
        .setNegativeButton(R.string.cancel) { _, _ -> app.client.submitPairingCode(null) }
        .setCancelable(false)
        .show()
    input.requestFocus()
}
