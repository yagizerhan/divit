// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// USB key receiver (protocol/SPEC.md §3.2.1). When the tablet is plugged in, the PC sends its key over adb:
//   adb shell am broadcast -a io.github.yagizerhan.divit.action.USB_KEY -n io.github.yagizerhan.divit/.net.UsbKeyReceiver --es hostId <hex> --es key <hex>
// Guarded by android.permission.DUMP in the manifest: the adb shell (and the system) hold it; an ordinary app cannot
// request it (only `adb shell pm grant` could give it, by someone who already controls the device). So in practice no
// other app on the tablet can write a fake key (none can read it either), and none can inject input into the PC
// through the adb reverse tunnel.

package io.github.yagizerhan.divit.net

import android.app.Activity
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import io.github.yagizerhan.divit.DivitApp
import io.github.yagizerhan.divit.protocol.Auth

class UsbKeyReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val hostId = intent.getStringExtra("hostId")?.let(::unhex)
        val key = intent.getStringExtra("key")?.let(::unhex)
        if (hostId?.size != Auth.ID || key?.size != Auth.KEY) { resultCode = Activity.RESULT_CANCELED; return }
        val ok = (context.applicationContext as DivitApp).prefs.saveUsbKey(hostId, key)
        resultCode = if (ok) Activity.RESULT_OK else Activity.RESULT_CANCELED   // the PC expects "result=-1"
    }

    private fun unhex(s: String): ByteArray? =
        if (s.length % 2 != 0) null else runCatching { ByteArray(s.length / 2) { s.substring(it * 2, it * 2 + 2).toInt(16).toByte() } }.getOrNull()
}
