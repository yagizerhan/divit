// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// TransportClient: sahte bir PC (ServerSocket) ile.
//  - stop() + start() back to back: the old thread must not come back to life and open a second connection.
//  - BYE(3) "another tablet connected": the client must not reconnect by itself.
//  - Authentication (SPEC §3.2): with a wrong PC proof the tablet sends no proof; it does not connect to an unexpected PC.

package io.github.yagizerhan.divit.net

import io.github.yagizerhan.divit.capture.SampleRing
import io.github.yagizerhan.divit.protocol.Auth
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.DataInputStream
import java.io.OutputStream
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.SocketTimeoutException
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.atomic.AtomicInteger

class TransportClientTest {

    companion object {
        val KEY = ByteArray(32) { (it + 1).toByte() }
        val HOST_ID = ByteArray(16) { 0x30 }
    }

    /**
     * Answers HELLO with a proven HELLO_ACK, verifies AUTH and returns AUTH_RESULT(1), then sends a PING every 200 ms
     * (the client drops after 1 s of silence).
     */
    private class FakePc(
        private val byeReplacedAfterAck: Boolean = false,
        private val forgeProof: Boolean = false,
        /** Answer HELLO_ACK(2) "pairing required" without any proof (what an impostor of a paired PC would do). */
        private val pairingRequired: Boolean = false,
    ) : AutoCloseable {
        val server = ServerSocket(0, 16, InetAddress.getLoopbackAddress())
        val accepted = AtomicInteger()
        val open = AtomicInteger()
        /** Did the tablet send AUTH (it must NOT in the fake-PC test)? */
        val authReceived = AtomicInteger()
        private val sockets = CopyOnWriteArrayList<Socket>()
        @Volatile private var closed = false

        val port get() = server.localPort

        fun acceptLoop() = Thread {
            while (!closed) {
                val s = try { server.accept() } catch (_: Exception) { return@Thread }
                accepted.incrementAndGet(); open.incrementAndGet(); sockets += s
                Thread { serve(s) }.apply { isDaemon = true }.start()
            }
        }.apply { isDaemon = true; start() }

        /** Wait [ms] for a second connection; true if one arrives. */
        fun secondConnectionWithin(ms: Int): Boolean {
            server.soTimeout = ms
            return try { server.accept().close(); true } catch (_: SocketTimeoutException) { false }
        }

        fun serveOne() = Thread {
            val s = server.accept(); accepted.incrementAndGet(); open.incrementAndGet(); sockets += s
            serve(s)
        }.apply { isDaemon = true; start() }

        private fun serve(s: Socket) {
            try {
                val inp = DataInputStream(s.getInputStream())
                val out = s.getOutputStream()
                val hello = readFrame(inp).second                       // HELLO
                val deviceId = hello.copyOfRange(4, 20)
                val clientNonce = hello.copyOfRange(20, 36)
                val serverNonce = ByteArray(16) { 0x20 }
                val proof = if (forgeProof) ByteArray(32) { 0x55 } else Auth.hostProof(KEY, deviceId, HOST_ID, clientNonce, serverNonce)
                val name = "PC".toByteArray()
                frame(out, 0x0002, ByteBuffer.allocate(4 + name.size + 64).order(ByteOrder.LITTLE_ENDIAN)
                    .put(if (pairingRequired) 2 else 0).putShort(0).put(name.size.toByte()).put(name).put(HOST_ID).put(serverNonce).put(proof).array())
                val (type, auth) = readFrame(inp)
                if (type != 0x0005) return
                authReceived.incrementAndGet()
                val ok = Auth.equal(auth, Auth.tabletProof(KEY, deviceId, HOST_ID, clientNonce, serverNonce))
                frame(out, 0x0006, byteArrayOf(if (ok) 1 else 0))
                if (!ok) return
                if (byeReplacedAfterAck) {
                    // Like the real host (PenServer Session.Stop): BYE, shut down the send side, read until the tablet closes.
                    frame(out, 0x00FF, byteArrayOf(3))
                    s.shutdownOutput()
                    while (true) readFrame(inp)
                }
                Thread {
                    try {
                        while (!s.isClosed) {
                            frame(out, 0x0010, ByteBuffer.allocate(12).order(ByteOrder.LITTLE_ENDIAN).putInt(1).putLong(0).array())
                            Thread.sleep(200)
                        }
                    } catch (_: Exception) {}
                }.apply { isDaemon = true; start() }
                while (true) readFrame(inp)                        // PING/PONG/SURFACE_INFO… until EOF
            } catch (_: Exception) {
            } finally {
                open.decrementAndGet()
                try { s.close() } catch (_: Exception) {}
            }
        }

        private fun readFrame(inp: DataInputStream): Pair<Int, ByteArray> {
            val h = ByteArray(8); inp.readFully(h)
            val bb = ByteBuffer.wrap(h).order(ByteOrder.LITTLE_ENDIAN)
            val type = bb.getShort(2).toInt() and 0xFFFF
            val p = ByteArray(bb.getInt(4)); inp.readFully(p)
            return type to p
        }

        private fun frame(out: OutputStream, type: Int, payload: ByteArray) = synchronized(out) {
            val b = ByteBuffer.allocate(8 + payload.size).order(ByteOrder.LITTLE_ENDIAN)
            b.put(1).put(0).putShort(type.toShort()).putInt(payload.size).put(payload)
            out.write(b.array()); out.flush()
        }

        override fun close() { closed = true; server.close(); sockets.forEach { try { it.close() } catch (_: Exception) {} } }
    }

    private class Id : Identity {
        override val deviceId = ByteArray(16) { 7 }
        override val model = "test"
        override fun key(usb: Boolean, hostId: ByteArray) = if (hostId.contentEquals(HOST_ID)) KEY else null
        override fun savePairKey(hostId: ByteArray, key: ByteArray) {}
        override fun forgetPairKey(hostId: ByteArray) {}
        override var expectedHostId = ""
    }

    private val id = Id()
    private val statuses = CopyOnWriteArrayList<LinkStatus>()
    private val prompts = CopyOnWriteArrayList<PairPrompt>()
    private val connectedAt = CopyOnWriteArrayList<Endpoint>()
    private lateinit var client: TransportClient
    init {
        client = TransportClient(SampleRing(), id, onStatus = { statuses += it },
            askPairingCode = { _, p -> prompts += p; client.submitPairingCode(null) },   // the user cancels
            onConnected = { connectedAt += it })
    }

    @After fun tearDown() { client.stop() }

    private fun eventually(what: String, ms: Long = 3000, cond: () -> Boolean) {
        val until = System.currentTimeMillis() + ms
        while (!cond()) {
            if (System.currentTimeMillis() > until) throw AssertionError("timeout: $what")
            Thread.sleep(10)
        }
    }

    @Test fun stopThenStartLeavesExactlyOneConnection() {
        FakePc().use { pc ->
            pc.acceptLoop()
            val ep = Endpoint(wifi = false, host = "127.0.0.1", port = pc.port)
            client.start(ep)
            eventually("first connection") { client.status.state == LinkState.CONNECTED }
            // Like reconnect() on the settings screen: stop + start with no pause in between. Several times.
            repeat(5) { client.stop(); client.start(ep) }
            eventually("reconnected") { client.status.state == LinkState.CONNECTED }
            Thread.sleep(1500)   // had the old thread come back, a second connection would stay open meanwhile
            assertEquals("open connections on the PC", 1, pc.open.get())
            val before = pc.accepted.get()
            Thread.sleep(1200)
            assertEquals("the connection must not drop and re-establish itself", before, pc.accepted.get())
        }
    }

    @Test fun onConnectedReportsTheEndpointOnlyAfterAuthentication() {
        FakePc().use { pc ->
            pc.acceptLoop()
            val ep = Endpoint(wifi = false, host = "127.0.0.1", port = pc.port)
            client.start(ep)
            eventually("connected") { client.status.state == LinkState.CONNECTED }
            assertEquals(listOf(ep), connectedAt.toList())
        }
        FakePc(forgeProof = true).use { pc ->
            pc.acceptLoop()
            client.stop(); connectedAt.clear()
            client.start(Endpoint(wifi = false, host = "127.0.0.1", port = pc.port))
            eventually("refused") { client.status.reason == Reason.USB_KEY_MISMATCH }
            assertTrue("an unproven PC must not be reported", connectedAt.isEmpty())
        }
    }

    @Test fun pairedPcAskingToPairAgainWarnsTheUser() {
        // We hold a key for this PC, yet it asks for the pairing code without proving itself: legitimate after
        // "Forget pairings" on the PC, but also what an impostor would do. The dialog must carry the warning.
        id.expectedHostId = "30".repeat(16)
        FakePc(pairingRequired = true).use { pc ->
            pc.acceptLoop()
            client.start(Endpoint(wifi = true, host = "127.0.0.1", port = pc.port))
            eventually("asked for the code") { prompts.isNotEmpty() }
            assertEquals(PairPrompt.PC_FORGOT, prompts.first())
            eventually("cancelled") { client.status.reason == Reason.PAIRING_CANCELLED }
            assertEquals("no proof is sent to an unproven PC", 0, pc.authReceived.get())
        }
    }

    @Test fun failedRetriesKeepStatusSteadyAtDisconnected() {
        // No PC: retries must not flip between "Connecting…" and "Not connected" (the screen used to flicker).
        val port = ServerSocket(0, 1, InetAddress.getLoopbackAddress()).use { it.localPort }
        client.start(Endpoint(wifi = false, host = "127.0.0.1", port = port))
        Thread.sleep(2500)   // several retries (backoff at most 1 s)
        val firstFail = statuses.indexOfFirst { it.state == LinkState.DISCONNECTED }
        assertTrue("never failed", firstFail >= 0)
        val after = statuses.drop(firstFail + 1)
        assertTrue("status changed after the failure: $after", after.none { it.state != LinkState.DISCONNECTED })
    }

    @Test fun replacedByAnotherTabletDoesNotReconnect() {
        FakePc(byeReplacedAfterAck = true).use { pc ->
            pc.serveOne()
            client.start(Endpoint(wifi = false, host = "127.0.0.1", port = pc.port))
            eventually("stopped after BYE(3)") { !client.running }
            assertFalse("no second connection attempt", pc.secondConnectionWithin(1500))
            assertEquals(LinkState.DISCONNECTED, client.status.state)
            assertEquals(Reason.REPLACED, client.status.reason)
        }
    }

    @Test fun fakePcWithoutKeyGetsNoProof() {
        // Another app on the tablet imitates the PC on localhost:47470: it does not know the key, so its proof is wrong.
        FakePc(forgeProof = true).use { pc ->
            pc.acceptLoop()
            client.start(Endpoint(wifi = false, host = "127.0.0.1", port = pc.port))
            eventually("refused") { client.status.state == LinkState.DISCONNECTED && client.status.reason == Reason.USB_KEY_MISMATCH }
            Thread.sleep(300)
            assertEquals("the tablet must not send a proof to a fake PC", 0, pc.authReceived.get())
            assertTrue(statuses.none { it.state == LinkState.CONNECTED })
        }
    }

    @Test fun wifiRefusesADifferentPc() {
        // Another PC answers at the saved PC's address (DHCP or impersonation).
        id.expectedHostId = "ff".repeat(16)
        FakePc().use { pc ->
            pc.acceptLoop()
            client.start(Endpoint(wifi = true, host = "127.0.0.1", port = pc.port))
            eventually("refused") { client.status.reason == Reason.DIFFERENT_PC }
            assertEquals(0, pc.authReceived.get())
            // Retries back off (100, 200, 400, 800, 1000 ms…) instead of reconnecting every 100 ms.
            val before = pc.accepted.get()
            Thread.sleep(2500)
            val retries = pc.accepted.get() - before
            assertTrue("too many retries in 2.5 s: $retries", retries in 1..5)
        }
    }
}
