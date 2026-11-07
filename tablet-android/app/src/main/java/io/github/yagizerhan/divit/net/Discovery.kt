// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// Automatic PC discovery on Wi-Fi (protocol/SPEC.md §7). Network I/O: do not call on the main thread.

package io.github.yagizerhan.divit.net

import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.NetworkInterface
import java.net.SocketTimeoutException

/** [hostId]: PC ID (hex); empty for PCs running an older version. */
data class FoundPc(val name: String, val address: String, val port: Int, val hostId: String = "")

object Discovery {
    const val PORT = 47471
    private const val REQUEST = "DIVIT-DISCOVER 1"

    /** Finds Divit PCs on the network (waits at most [timeoutMs]). Empty list if none. */
    fun find(timeoutMs: Long = 1500): List<FoundPc> {
        val found = LinkedHashMap<String, FoundPc>()
        DatagramSocket().use { s ->
            s.broadcast = true
            val req = REQUEST.toByteArray(Charsets.US_ASCII)
            val targets = broadcastAddresses()
            val buf = ByteArray(512)
            val start = System.currentTimeMillis()
            var sent = 0
            while (true) {
                val elapsed = System.currentTimeMillis() - start
                if (elapsed >= timeoutMs) break
                // The request is repeated at 0 / 300 / 600 ms (UDP may be lost).
                if (sent < 3 && elapsed >= sent * 300L) {
                    for (t in targets) runCatching { s.send(DatagramPacket(req, req.size, t, PORT)) }
                    sent++
                }
                s.soTimeout = 100
                try {
                    val p = DatagramPacket(buf, buf.size)
                    s.receive(p)
                    parse(String(p.data, 0, p.length, Charsets.UTF_8), p.address.hostAddress ?: continue)?.let { found[it.address] = it }
                } catch (_: SocketTimeoutException) {}
            }
        }
        return found.values.toList()
    }

    /** "DIVIT-HERE 2 <port> <hostId> <ad>" (ya da eski "DIVIT-HERE 1 <port> <ad>") → FoundPc. */
    internal fun parse(text: String, address: String): FoundPc? {
        val t = text.trim()
        if (t.startsWith("DIVIT-HERE 2 ")) {
            val parts = t.split(' ', limit = 5)
            if (parts.size < 5) return null
            val port = parts[2].toIntOrNull()?.takeIf { it in 1..65535 } ?: return null
            val id = parts[3].lowercase().takeIf { it.length == 32 && it.all { c -> c in '0'..'9' || c in 'a'..'f' } } ?: return null
            return FoundPc(parts[4], address, port, id)
        }
        val parts = t.split(' ', limit = 4)
        if (parts.size < 4 || parts[0] != "DIVIT-HERE" || parts[1] != "1") return null
        val port = parts[2].toIntOrNull()?.takeIf { it in 1..65535 } ?: return null
        return FoundPc(parts[3], address, port)
    }

    private fun broadcastAddresses(): List<InetAddress> {
        val list = mutableListOf<InetAddress>(InetAddress.getByName("255.255.255.255"))
        runCatching {
            for (ni in NetworkInterface.getNetworkInterfaces()) {
                if (!ni.isUp || ni.isLoopback) continue
                for (ia in ni.interfaceAddresses) if (ia.address is Inet4Address) ia.broadcast?.let(list::add)
            }
        }
        return list.distinct()
    }
}
