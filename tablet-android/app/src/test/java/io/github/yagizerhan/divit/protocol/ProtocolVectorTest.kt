// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// Conformance with protocol/test-vectors/vectors.json (the same vectors as the C# tests).

package io.github.yagizerhan.divit.protocol

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test
import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder

class ProtocolVectorTest {

    private val vectors: Map<String, JSONObject> by lazy {
        // Gradle unit tests run in the module folder (app/).
        val f = listOf("../../protocol/test-vectors/vectors.json", "../protocol/test-vectors/vectors.json").map(::File).first { it.exists() }
        val arr = JSONObject(f.readText()).getJSONArray("vectors")
        (0 until arr.length()).map { arr.getJSONObject(it) }.associateBy { it.getString("name") }
    }

    private fun hex(name: String) = vectors.getValue(name).getString("hex").chunked(2).map { it.toInt(16).toByte() }.toByteArray()
    private fun msg(name: String) = vectors.getValue(name).getJSONObject("message")
    private fun ByteArray.toHex() = joinToString("") { "%02x".format(it) }
    private fun bytesOf(h: String) = h.chunked(2).map { it.toInt(16).toByte() }.toByteArray()

    private fun decodeFrame(b: ByteArray): Incoming {
        val bb = ByteBuffer.wrap(b).order(ByteOrder.LITTLE_ENDIAN)
        val h = Header.read(bb)
        if (bb.remaining() < h.length) throw ProtocolException(ProtocolException.Kind.LENGTH, "eksik")
        return Decoder.decode(h.type, ByteBuffer.wrap(b, Wire.HEADER, h.length).slice())
    }

    private fun assertEncodes(name: String, write: FrameWriter.() -> Int) {
        val w = FrameWriter()
        val n = w.write()
        assertEquals(name, vectors.getValue(name).getString("hex"), w.buf.array().copyOf(n).toHex())
    }

    @Test fun encodesHello() {
        val m = msg("hello_usb")
        assertEncodes("hello_usb") {
            hello(m.getInt("transport"), m.getInt("capabilities"), bytesOf(m.getString("deviceId")), bytesOf(m.getString("clientNonce")), m.getString("model"))
        }
    }

    @Test fun encodesPairRequest() = msg("pair_request").let { m ->
        assertEncodes("pair_request") { pairRequest(m.getLong("code"), m.getString("deviceName"), bytesOf(m.getString("tabletKey"))) }
    }

    @Test fun encodesAuth() = assertEncodes("auth") { auth(bytesOf(msg("auth").getString("proof"))) }

    // ---- Cryptography (SPEC §3.2–3.3): same values as the independent Python reference ----

    private val crypto by lazy {
        val f = listOf("../../protocol/test-vectors/vectors.json", "../protocol/test-vectors/vectors.json").map(::File).first { it.exists() }
        JSONObject(f.readText()).getJSONObject("crypto")
    }
    private fun c(name: String) = bytesOf(crypto.getString(name))

    @Test fun proofsMatchReference() {
        assertEquals(crypto.getString("hostProof"), Auth.hostProof(c("key"), c("deviceId"), c("hostId"), c("clientNonce"), c("serverNonce")).toHex())
        assertEquals(crypto.getString("tabletProof"), Auth.tabletProof(c("key"), c("deviceId"), c("hostId"), c("clientNonce"), c("serverNonce")).toHex())
    }

    @Test fun ecdhPairingMatchesReference() {
        // The tablet's fixed private key + the PC's public key (in the form .NET produces).
        val kf = java.security.KeyFactory.getInstance("EC")
        val pair = java.security.KeyPair(
            kf.generatePublic(java.security.spec.X509EncodedKeySpec(c("tabletPublicSpki"))),
            kf.generatePrivate(java.security.spec.PKCS8EncodedKeySpec(c("tabletPrivatePkcs8"))))
        val secret = Auth.agree(pair, c("hostPublicSpki"))
        assertEquals(crypto.getString("sharedSecret"), secret.toHex())
        assertEquals(crypto.getString("pairKey"), Auth.pairKey(secret, c("deviceId"), c("hostId")).toHex())
        assertEquals("the PC must be able to decode this format", crypto.getString("tabletPublicSpki"), pair.public.encoded.toHex())
    }

    @Test fun freshPairingKeyIsSpki() {
        val enc = Auth.newPairingKeyPair().public.encoded
        assertEquals(91, enc.size)   // P-256 SubjectPublicKeyInfo (uncompressed point): what .NET ImportSubjectPublicKeyInfo expects
    }

    @Test fun encodesPingPong() {
        msg("ping").let { m -> assertEncodes("ping") { ping(m.getLong("id"), m.getLong("t0")) } }
        msg("pong").let { m -> assertEncodes("pong") { pong(m.getLong("id"), m.getLong("t0"), m.getLong("t1"), m.getLong("t2")) } }
    }

    @Test fun encodesSurfaceInfo() = msg("surface_info").let { m ->
        assertEncodes("surface_info") {
            surfaceInfo(m.getInt("widthPx"), m.getInt("heightPx"), m.getDouble("widthMm").toFloat(), m.getDouble("heightMm").toFloat())
        }
    }

    @Test fun encodesPenBatch() {
        val s = msg("pen_batch_3").getJSONArray("samples")
        assertEncodes("pen_batch_3") {
            beginBatch(s.length())
            for (i in 0 until s.length()) s.getJSONObject(i).run {
                sample(getLong("t_us"), getDouble("u").toFloat(), getDouble("v").toFloat(), getDouble("pressure").toFloat(),
                    getDouble("tiltX").toFloat(), getDouble("tiltY").toFloat(), getInt("flags"), getInt("seq"))
            }
            endBatch()
        }
    }

    @Test fun encodesBye() = assertEncodes("bye_normal") { bye(0) }

    @Test fun decodesHostMessages() {
        val ack = decodeFrame(hex("hello_ack_authenticate")) as Incoming.HelloAck
        val am = msg("hello_ack_authenticate")
        assertEquals(Wire.ACK_AUTHENTICATE, ack.result)
        assertEquals(am.getString("hostName"), ack.hostName)
        assertEquals(am.getString("hostId"), ack.hostId.toHex())
        assertEquals(am.getString("serverNonce"), ack.serverNonce.toHex())
        assertEquals(am.getString("hostProof"), ack.hostProof.toHex())
        assertEquals(Wire.ACK_PAIRING_REQUIRED, (decodeFrame(hex("hello_ack_pairing_required")) as Incoming.HelloAck).result)
        assertEquals(Incoming.AuthResult(true), decodeFrame(hex("auth_result_ok")))

        val pr = decodeFrame(hex("pair_result_ok")) as Incoming.PairResult
        assertTrue(pr.ok)
        assertEquals(msg("pair_result_ok").getString("hostKey"), pr.hostKey.toHex())
        val prw = decodeFrame(hex("pair_result_wrong_code")) as Incoming.PairResult
        assertTrue(!prw.ok && prw.hostKey.isEmpty())

        val pong = decodeFrame(hex("pong")) as Incoming.Pong
        assertEquals(Incoming.Pong(7, 123456789, -5000000000, -4999999990), pong)

        val hs = decodeFrame(hex("host_status")) as Incoming.HostStatus
        val m = msg("host_status")
        assertEquals(m.getString("monitorName"), hs.monitorName)
        assertEquals(m.getDouble("areaTop").toFloat(), hs.top)
        assertEquals(m.getDouble("areaBottom").toFloat(), hs.bottom)

        assertEquals(Incoming.Unknown(0x7777), decodeFrame(hex("unknown_type_ignored")))
        assertEquals(Incoming.Ping(8, 1), decodeFrame(hex("ping_with_trailing_bytes")))
    }

    @Test fun rejectsInvalidFramesTabletCanReceive() {
        for ((name, kind) in listOf(
            "wrong_version" to ProtocolException.Kind.VERSION,
            "ping_too_short" to ProtocolException.Kind.LENGTH,
            "frame_too_large" to ProtocolException.Kind.LENGTH,
            "string_overrun" to ProtocolException.Kind.LENGTH,
            "hello_ack_missing_proof" to ProtocolException.Kind.LENGTH,
        )) {
            try { decodeFrame(hex(name)); fail("$name reddedilmeliydi") }
            catch (e: ProtocolException) { assertEquals(name, kind, e.kind) }
        }
    }
}
