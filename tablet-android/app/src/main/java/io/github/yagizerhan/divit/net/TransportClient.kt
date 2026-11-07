// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// TransportClient (architecture §2, §5; SPEC §3–4): blocking socket writes on a separate thread, automatic reconnect,
// heartbeat, authentication and pairing. The main thread only writes to the ring buffer.
// User-visible texts are not produced here: the status carries a Reason that the UI turns into a localized string.

package io.github.yagizerhan.divit.net

import android.os.Process
import android.os.SystemClock
import io.github.yagizerhan.divit.capture.SampleRing
import io.github.yagizerhan.divit.protocol.Auth
import io.github.yagizerhan.divit.protocol.Decoder
import io.github.yagizerhan.divit.protocol.FrameWriter
import io.github.yagizerhan.divit.protocol.Header
import io.github.yagizerhan.divit.protocol.Incoming
import io.github.yagizerhan.divit.protocol.ProtocolException
import io.github.yagizerhan.divit.protocol.Wire
import java.io.EOFException
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.nio.ByteBuffer
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit
import java.util.concurrent.locks.LockSupport

/** Connection settings. On USB host = 127.0.0.1 (`adb reverse`, set up by the PC). */
data class Endpoint(val wifi: Boolean, val host: String, val port: Int = Wire.PORT)

/** Persistent identity and keys (stored in Prefs). Keys are kept per PC ID (hostId). */
interface Identity {
    val deviceId: ByteArray
    val model: String
    /** USB: the USB key the PC sent over adb; Wi-Fi: the pairing key. Null if none. */
    fun key(usb: Boolean, hostId: ByteArray): ByteArray?
    fun savePairKey(hostId: ByteArray, key: ByteArray)
    fun forgetPairKey(hostId: ByteArray)
    /**
     * Wi-Fi: the PC ID (hex) expected at this address. Empty means the user just picked a new PC and pairing with
     * whichever PC answers is allowed. When set, another PC (or someone imitating the name/address) is refused.
     */
    var expectedHostId: String
}

enum class LinkState { DISCONNECTED, CONNECTING, PAIRING, CONNECTED }

/** Why the link is in its state. The UI maps each value to a localized message (UiCommon.describe). */
enum class Reason {
    NONE,
    // CONNECTING
    FOUND_AT_NEW_ADDRESS,
    // PAIRING
    PAIRING_INVALID,
    // DISCONNECTED
    PC_NOT_RUNNING_USB, WIFI_NOT_ALLOWED, UNREACHABLE, NO_ANSWER, TIMEOUT,
    PROTOCOL_ERROR, VERSION_MISMATCH,
    DIFFERENT_PC, PC_NOT_VERIFIED, USB_KEY_MISMATCH, USB_KEY_PENDING,
    PC_CLOSED, REPLACED, CONNECTION_LOST, PAIRING_CANCELLED, TOO_MANY_WRONG_CODES,
}

/** A connection failure with a reason for the UI. [detail] is technical context (address, error text), not prose. */
open class LinkException(val reason: Reason, val detail: String = "") : IOException("$reason $detail".trim())

/** Authentication failure: the PC could not prove itself, or it is not the expected PC. No secret is sent to it. */
class AuthException(reason: Reason) : LinkException(reason)

/**
 * @param detail technical context for [reason] (e.g. the address); for CONNECTED the transport ("USB"/"Wi-Fi").
 */
data class LinkStatus(
    val state: LinkState,
    val reason: Reason = Reason.NONE,
    val detail: String = "",
    val hostName: String = "",
    val rttUs: Long = -1,
    val area: Incoming.HostStatus? = null,
)

class TransportClient(
    private val ring: SampleRing,
    private val identity: Identity,
    private val onStatus: (LinkStatus) -> Unit,
    /** Ask the UI for the pairing code. Called on the TX thread; waits at most 2 minutes. */
    private val askPairingCode: (hostName: String, wrong: Boolean) -> Unit,
    /** Wi-Fi: if the saved address is unreachable, find the PC again by ID/name (SPEC §7). Null if not found. Called on the TX thread. */
    private val rediscover: ((Endpoint) -> Endpoint?)? = null,
) {
    companion object {
        const val HEARTBEAT_MS = 250L
        const val DEAD_AFTER_MS = 1000
        private const val MAX_BACKOFF_MS = 1000L
    }

    /**
     * The lifetime of one start() call. A stopped run never becomes active again: when stop() and start() are called
     * back to back, the old thread cannot pick up the new run's flag and keep going. (There used to be one shared
     * `running` flag; two TX threads then read the same ring and knocked each other off the PC with BYE(3).)
     */
    private class Run(val previous: Thread?) {
        @Volatile var active = true
        @Volatile var socket: Socket? = null
        /** The PC said "another tablet connected" (BYE 3): do not reconnect by ourselves (two tablets must not fight). */
        @Volatile var replaced = false
        /** The current connection got through the handshake (CONNECTED). Only then does the retry delay start over. */
        @Volatile var established = false
        lateinit var thread: Thread
    }

    @Volatile private var endpoint: Endpoint? = null
    @Volatile private var current: Run? = null
    val running: Boolean get() = current?.active == true
    @Volatile private var surface: FloatArray? = null       // wPx, hPx, wMm, hMm
    @Volatile private var surfaceDirty = false
    @Volatile var status = LinkStatus(LinkState.DISCONNECTED); private set
    // Not a SynchronousQueue: its offer() silently dropped the code when nobody was waiting at that moment.
    private val pairingCodes = LinkedBlockingQueue<Long>()

    @Synchronized fun start(ep: Endpoint) {
        val cur = current
        if (cur != null && cur.active) {
            if (ep == endpoint) return               // already connected / connecting to this address (onResume repeats)
            endpoint = ep
            cur.socket?.close()                      // reconnect to the new address
            return
        }
        endpoint = ep
        val r = Run(previous = cur?.thread)
        r.thread = Thread({ run(r) }, "divit-tx")
        current = r
        ring.consumer = r.thread
        r.thread.start()
    }

    @Synchronized fun stop() {
        current?.let { r ->
            r.active = false
            r.socket?.close()
            r.thread.interrupt()
        }
        publish(LinkStatus(LinkState.DISCONNECTED))
    }

    fun setSurface(wPx: Int, hPx: Int, wMm: Float, hMm: Float) {
        surface = floatArrayOf(wPx.toFloat(), hPx.toFloat(), wMm, hMm)
        surfaceDirty = true
        ring.signal()
    }

    /** From the UI: the code the user entered (null = cancelled). */
    fun submitPairingCode(code: Long?) { pairingCodes.offer(code ?: -1L) }

    private fun publish(s: LinkStatus) { status = s; onStatus(s) }

    /** A stopped run must not overwrite the status (the old thread's last messages after stop()). */
    private fun publish(r: Run, s: LinkStatus) { if (r.active) publish(s) }

    private fun run(r: Run) {
        // Do not touch the ring before the previous run is done: SampleRing has a single reader (SPSC). The old
        // thread's socket is closed and its flag cleared, so it finishes within about a second.
        r.previous?.let { try { it.join() } catch (_: InterruptedException) { return } }
        Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_DISPLAY)
        var backoff = 100L
        var wifiFailures = 0
        var lastRediscover = 0L
        // After one failed attempt "Connecting…" is not published again: the screen stays at "Not connected" until a
        // connection is made (it used to flip between the two states on every attempt).
        var failing = false
        while (r.active) {
            val ep = endpoint ?: break
            if (!failing) publish(r, LinkStatus(LinkState.CONNECTING, detail = if (ep.wifi) "${ep.host}:${ep.port}" else "USB"))
            try {
                Socket().use { s ->
                    r.socket = s
                    if (!r.active) return   // stop() read the socket before we set it: leave by ourselves
                    s.tcpNoDelay = true
                    s.connect(InetSocketAddress(ep.host, ep.port), 1500)
                    s.soTimeout = DEAD_AFTER_MS
                    wifiFailures = 0          // the address answers: no need to look for the PC elsewhere
                    r.established = false
                    session(r, s, ep)
                }
            } catch (e: Exception) {
                if (r.active) {
                    val (reason, detail) = classify(e, ep)
                    failing = true
                    if (status.state != LinkState.DISCONNECTED || status.reason != reason || status.detail != detail)   // do not republish the same message
                        publish(r, LinkStatus(LinkState.DISCONNECTED, reason, detail))
                    // The PC's IP may have changed (DHCP): after two failures look for it again (at most every 5 s).
                    if (ep.wifi && e !is ProtocolException && ++wifiFailures >= 2 && rediscover != null &&
                        SystemClock.uptimeMillis() - lastRediscover > 5000) {
                        lastRediscover = SystemClock.uptimeMillis()
                        val ne = rediscover.invoke(ep)
                        if (ne != null && ne != ep && r.active) {
                            endpoint = ne
                            wifiFailures = 0
                            publish(r, LinkStatus(LinkState.CONNECTING, Reason.FOUND_AT_NEW_ADDRESS, ne.host))
                            continue
                        }
                    }
                }
            } finally {
                r.socket = null
            }
            // A TCP connection alone is not success: a PC that refuses us (another PC, wrong key) must not be retried
            // ten times a second. The delay starts over only after a session was really established.
            if (r.established) { backoff = 100L; failing = false; r.established = false }
            if (r.replaced) {
                publish(r, LinkStatus(LinkState.DISCONNECTED, Reason.REPLACED))
                r.active = false
                break
            }
            if (r.active) {
                try { Thread.sleep(backoff) } catch (_: InterruptedException) {}   // so stop() can wake us
                backoff = minOf(backoff * 2, MAX_BACKOFF_MS)
            }
        }
    }

    private fun classify(e: Exception, ep: Endpoint): Pair<Reason, String> = when (e) {
        is LinkException -> e.reason to e.detail
        is ProtocolException -> (if (e.kind == ProtocolException.Kind.VERSION) Reason.VERSION_MISMATCH else Reason.PROTOCOL_ERROR) to (e.message ?: "")
        // Refused: the PC answered but the port is closed. On Wi-Fi the most likely cause is Wi-Fi being off in Divit on the PC.
        is java.net.ConnectException -> when {
            !ep.wifi -> Reason.PC_NOT_RUNNING_USB to ""
            e.message?.contains("ECONNREFUSED") == true -> Reason.WIFI_NOT_ALLOWED to ""
            else -> Reason.UNREACHABLE to ep.host
        }
        // Timeout: packets are dropped without an answer → wrong address or firewall.
        is java.net.SocketTimeoutException -> if (ep.wifi) Reason.NO_ANSWER to ep.host else Reason.TIMEOUT to ""
        is EOFException -> Reason.PC_CLOSED to ""
        else -> Reason.CONNECTION_LOST to (e.message ?: e.javaClass.simpleName)
    }

    /** One connection: handshake, then the send loop. Reading happens on a separate thread. */
    private fun session(r: Run, s: Socket, ep: Endpoint) {
        val out = s.getOutputStream()
        val inp = s.getInputStream()
        val w = FrameWriter()
        val rbuf = ByteArray(Wire.HEADER + Wire.MAX_PAYLOAD)

        val caps = Wire.CAP_PRESSURE or Wire.CAP_TILT or Wire.CAP_BARREL or Wire.CAP_HOVER
        val deviceId = identity.deviceId
        val clientNonce = Auth.random(Auth.NONCE)
        send(out, w, w.hello(if (ep.wifi) 1 else 0, caps, deviceId, clientNonce, identity.model))

        // Handshake (SPEC §3.2): the PC proves itself first, then the tablet. The key never goes on the wire.
        var host = ""
        var hostId = ByteArray(0)
        handshake@ while (true) {
            when (val m = read(inp, rbuf, out)) {
                is Incoming.HelloAck -> {
                    host = m.hostName
                    hostId = m.hostId
                    if (m.result == Wire.ACK_VERSION) throw LinkException(Reason.VERSION_MISMATCH)
                    val hostHex = hex(m.hostId)
                    if (ep.wifi && identity.expectedHostId.isNotEmpty() && identity.expectedHostId != hostHex)
                        throw AuthException(Reason.DIFFERENT_PC)
                    val key = identity.key(usb = !ep.wifi, hostId = m.hostId)
                    when {
                        m.result == Wire.ACK_AUTHENTICATE && key != null -> {
                            val expect = Auth.hostProof(key, deviceId, m.hostId, clientNonce, m.serverNonce)
                            if (!Auth.equal(expect, m.hostProof)) throw AuthException(if (ep.wifi) Reason.PC_NOT_VERIFIED else Reason.USB_KEY_MISMATCH)
                            send(out, w, w.auth(Auth.tabletProof(key, deviceId, m.hostId, clientNonce, m.serverNonce)))
                        }
                        // We have no key: on USB the PC notices and resends it over adb; on Wi-Fi we go on to pairing.
                        m.result == Wire.ACK_AUTHENTICATE || !ep.wifi -> send(out, w, w.auth(ByteArray(Auth.PROOF)))
                        else -> if (pair(r, out, inp, w, rbuf, host, hostId, invalidKey = false)) break@handshake else return
                    }
                }
                is Incoming.AuthResult -> {
                    if (m.ok) {
                        if (ep.wifi) identity.expectedHostId = hex(hostId)
                        break@handshake
                    }
                    if (!ep.wifi) throw LinkException(Reason.USB_KEY_PENDING)
                    // Wi-Fi: the PC did not recognize our key (its pairings may have been cleared): pair again.
                    identity.forgetPairKey(hostId)
                    if (pair(r, out, inp, w, rbuf, host, hostId, invalidKey = true)) break@handshake else return
                }
                is Incoming.Bye -> throw LinkException(Reason.PC_CLOSED, "BYE ${m.reason}")
                else -> Unit
            }
        }

        ring.clear()                // stale samples gathered while disconnected are not sent
        surfaceDirty = true
        publish(r, LinkStatus(LinkState.CONNECTED, detail = if (ep.wifi) "Wi-Fi" else "USB", hostName = host))
        r.established = true

        val alive = java.util.concurrent.atomic.AtomicBoolean(true)
        val reader = Thread({
            Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_DISPLAY)
            try {
                while (alive.get()) when (val m = read(inp, rbuf, out)) {
                    is Incoming.HostStatus -> publish(r, status.copy(area = m))
                    is Incoming.Pong -> {
                        val t3 = nowUs()
                        publish(r, status.copy(rttUs = (t3 - m.t0) - (m.t2 - m.t1)))
                    }
                    is Incoming.Bye -> {
                        if (m.reason == Wire.BYE_REPLACED) r.replaced = true
                        throw LinkException(if (m.reason == Wire.BYE_REPLACED) Reason.REPLACED else Reason.PC_CLOSED)
                    }
                    else -> Unit
                }
            } catch (e: Exception) {
                if (alive.get()) {
                    val (reason, detail) = classify(e, ep)
                    publish(r, LinkStatus(LinkState.DISCONNECTED, reason, detail))
                }
            } finally {
                alive.set(false)
                try { s.close() } catch (_: IOException) {}
            }
        }, "divit-rx")
        reader.start()

        var nextPing = 0L
        var pingId = 0L
        try {
            while (r.active && alive.get()) {
                val now = nowUs()
                if (surfaceDirty) {
                    surfaceDirty = false
                    surface?.let { send(out, w, w.surfaceInfo(it[0].toInt(), it[1].toInt(), it[2], it[3])) }
                }
                if (now >= nextPing) { send(out, w, w.ping(++pingId and 0xFFFFFFFFL, now)); nextPing = now + HEARTBEAT_MS * 1000 }

                val n = minOf(ring.available(), Wire.MAX_BATCH)
                if (n > 0) {
                    w.beginBatch(n)
                    for (k in 0 until n) {
                        val i = ring.indexAt(k)
                        w.sample(ring.t[i], ring.u[i], ring.v[i], ring.p[i], ring.tx[i], ring.ty[i], ring.flags[i], ring.seq[i])
                    }
                    ring.consume(n)
                    send(out, w, w.endBatch())
                } else {
                    LockSupport.parkNanos((nextPing - nowUs()).coerceIn(100, HEARTBEAT_MS * 1000) * 1000)
                }
            }
            if (!r.active) try { send(out, w, w.bye(Wire.BYE_NORMAL)) } catch (_: IOException) {}
        } finally {
            alive.set(false)
            s.close()
            reader.join(500)
        }
    }

    /**
     * Wi-Fi pairing (SPEC §3.3): the user enters the code shown on the PC and both sides derive a common key with
     * ECDH (P-256). The key never goes on the wire, so a passive eavesdropper cannot get it.
     */
    private fun pair(r: Run, out: OutputStream, inp: InputStream, w: FrameWriter, rbuf: ByteArray, host: String, hostId: ByteArray, invalidKey: Boolean): Boolean {
        publish(r, LinkStatus(LinkState.PAIRING, if (invalidKey) Reason.PAIRING_INVALID else Reason.NONE, hostName = host))
        var wrong = false
        while (r.active) {
            pairingCodes.clear()   // an answer left over from an earlier question must not leak into this one
            askPairingCode(host, wrong)
            // Keep the connection alive while the user types: answer the host's PINGs (read only when a full frame is
            // there, so a read is never cut in half) and send our own PING (the host drops us after 1 s of silence).
            var code: Long? = null
            val deadline = SystemClock.uptimeMillis() + 120_000
            while (code == null && SystemClock.uptimeMillis() < deadline && r.active) {
                code = pairingCodes.poll(150, TimeUnit.MILLISECONDS)
                while (inp.available() >= Wire.HEADER) {
                    if (read(inp, rbuf, out) is Incoming.Bye) throw LinkException(Reason.PC_CLOSED)
                }
                send(out, w, w.ping(0, nowUs()))
            }
            if (code == null || code < 0) { publish(r, LinkStatus(LinkState.DISCONNECTED, Reason.PAIRING_CANCELLED)); r.active = false; return false }

            val keys = Auth.newPairingKeyPair()
            send(out, w, w.pairRequest(code, identity.model, keys.public.encoded))
            while (true) {
                when (val m = read(inp, rbuf, out)) {
                    is Incoming.PairResult -> {
                        if (m.ok) {
                            val secret = Auth.agree(keys, m.hostKey)
                            identity.savePairKey(hostId, Auth.pairKey(secret, identity.deviceId, hostId))
                            secret.fill(0)
                            identity.expectedHostId = hex(hostId)
                            return true
                        }
                        wrong = true; break
                    }
                    is Incoming.Bye -> throw LinkException(Reason.TOO_MANY_WRONG_CODES)
                    else -> Unit
                }
            }
        }
        return false
    }

    /** Reads one frame. PINGs are answered here (both threads may use it; writes are locked). */
    private fun read(inp: InputStream, buf: ByteArray, out: OutputStream): Incoming {
        readFully(inp, buf, 0, Wire.HEADER)
        val h = Header.read(ByteBuffer.wrap(buf, 0, Wire.HEADER))
        readFully(inp, buf, Wire.HEADER, h.length)
        val t1 = nowUs()
        val m = Decoder.decode(h.type, ByteBuffer.wrap(buf, Wire.HEADER, h.length).slice())
        if (m is Incoming.Ping) {
            val pw = pongWriter.get()!!
            send(out, pw, pw.pong(m.id, m.t0, t1, nowUs()))
        }
        return m
    }

    private val pongWriter = ThreadLocal.withInitial { FrameWriter(64) }

    private fun readFully(inp: InputStream, b: ByteArray, off: Int, len: Int) {
        var o = 0
        while (o < len) {
            val n = inp.read(b, off + o, len - o)
            if (n < 0) throw EOFException("PC closed the connection")
            o += n
        }
    }

    private fun send(out: OutputStream, w: FrameWriter, n: Int) {
        synchronized(out) { out.write(w.buf.array(), 0, n) }
    }

    private fun hex(b: ByteArray) = b.joinToString("") { "%02x".format(it) }

    // CLOCK_MONOTONIC: the same clock as MotionEvent times (uptimeMillis base).
    private fun nowUs() = System.nanoTime() / 1000
}
