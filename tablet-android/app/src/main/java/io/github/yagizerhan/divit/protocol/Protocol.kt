// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// Divit protocol, version 1 (protocol/SPEC.md). Pure Kotlin + java.nio: no Android classes, tested on the JVM.

package io.github.yagizerhan.divit.protocol

import java.nio.ByteBuffer
import java.nio.ByteOrder

object Wire {
    const val VERSION = 1
    const val MINOR = 0
    const val HEADER = 8
    const val MAX_PAYLOAD = 65_536
    const val SAMPLE = 32
    const val MAX_BATCH = 64
    const val PORT = 47470

    const val CH_CONTROL = 0
    const val CH_PEN = 1

    const val HELLO = 0x0001
    const val HELLO_ACK = 0x0002
    const val PAIR_REQUEST = 0x0003
    const val PAIR_RESULT = 0x0004
    const val AUTH = 0x0005
    const val AUTH_RESULT = 0x0006
    const val PING = 0x0010
    const val PONG = 0x0011
    const val SURFACE_INFO = 0x0020
    const val PEN_BATCH = 0x0030
    const val HOST_STATUS = 0x0040
    const val BYE = 0x00FF

    // PenSample.flags
    const val IN_RANGE = 1
    const val IN_CONTACT = 2
    const val BARREL = 4
    const val HISTORICAL = 8
    const val ERASER = 16

    // HELLO.capabilities
    const val CAP_PRESSURE = 1
    const val CAP_TILT = 2
    const val CAP_BARREL = 4
    const val CAP_HOVER = 8
    const val CAP_ERASER = 16

    // HELLO_ACK.result
    /** The PC knows a key for this device; its proof is attached and the tablet proves itself with AUTH. */
    const val ACK_AUTHENTICATE = 0
    const val ACK_VERSION = 1
    /** The PC has no key for this device (Wi-Fi: pairing required). */
    const val ACK_PAIRING_REQUIRED = 2

    // BYE.reason
    const val BYE_NORMAL = 0
    const val BYE_PROTOCOL = 1
    const val BYE_PAIRING_FAILED = 2
    const val BYE_REPLACED = 3
}

class ProtocolException(val kind: Kind, message: String) : Exception(message) {
    enum class Kind { VERSION, LENGTH, RANGE }
}

/** Incoming (host → tablet) messages. */
sealed interface Incoming {
    class HelloAck(val result: Int, val minor: Int, val hostName: String,
                   val hostId: ByteArray, val serverNonce: ByteArray, val hostProof: ByteArray) : Incoming
    data class AuthResult(val ok: Boolean) : Incoming
    /** [hostKey]: on success the PC's ephemeral P-256 public key (SubjectPublicKeyInfo), otherwise empty. */
    class PairResult(val ok: Boolean, val hostKey: ByteArray) : Incoming
    data class Ping(val id: Long, val t0: Long) : Incoming
    data class Pong(val id: Long, val t0: Long, val t1: Long, val t2: Long) : Incoming
    data class HostStatus(val monitorName: String, val left: Float, val top: Float, val right: Float, val bottom: Float) : Incoming
    data class Bye(val reason: Int) : Incoming
    data class Unknown(val type: Int) : Incoming
}

/** Frame header (SPEC §2). */
data class Header(val version: Int, val channel: Int, val type: Int, val length: Int) {
    companion object {
        fun read(b: ByteBuffer): Header {
            b.order(ByteOrder.LITTLE_ENDIAN)
            val version = b.get().toInt() and 0xFF
            if (version != Wire.VERSION) throw ProtocolException(ProtocolException.Kind.VERSION, "version $version")
            val channel = b.get().toInt() and 0xFF
            val type = b.short.toInt() and 0xFFFF
            val length = b.int.toLong() and 0xFFFFFFFFL
            if (length > Wire.MAX_PAYLOAD) throw ProtocolException(ProtocolException.Kind.LENGTH, "payload too large: $length")
            return Header(version, channel, type, length.toInt())
        }
    }
}

object Decoder {
    /** Decodes the payload. Trailing extra bytes are ignored (SPEC §6); missing bytes → LENGTH. */
    fun decode(type: Int, payload: ByteBuffer): Incoming = try {
        val p = payload.order(ByteOrder.LITTLE_ENDIAN)
        when (type) {
            Wire.HELLO_ACK -> Incoming.HelloAck(p.get().toInt() and 0xFF, p.short.toInt() and 0xFFFF, str(p),
                bytes(p, Auth.ID), bytes(p, Auth.NONCE), bytes(p, Auth.PROOF))
            Wire.AUTH_RESULT -> Incoming.AuthResult((p.get().toInt() and 0xFF) == 1)
            Wire.PAIR_RESULT -> Incoming.PairResult((p.get().toInt() and 0xFF) == 1, bytes(p, p.get().toInt() and 0xFF))
            Wire.PING -> Incoming.Ping(p.int.toLong() and 0xFFFFFFFFL, p.long)
            Wire.PONG -> Incoming.Pong(p.int.toLong() and 0xFFFFFFFFL, p.long, p.long, p.long)
            Wire.HOST_STATUS -> Incoming.HostStatus(str(p), p.float, p.float, p.float, p.float)
            Wire.BYE -> Incoming.Bye(p.get().toInt() and 0xFF)
            else -> Incoming.Unknown(type)
        }
    } catch (e: java.nio.BufferUnderflowException) {
        throw ProtocolException(ProtocolException.Kind.LENGTH, "message too short (type 0x${type.toString(16)})")
    }

    private fun bytes(p: ByteBuffer, n: Int) = ByteArray(n).also { p.get(it) }

    private fun str(p: ByteBuffer): String {
        val n = p.get().toInt() and 0xFF
        val b = ByteArray(n).also { p.get(it) }
        return String(b, Charsets.UTF_8)
    }
}

/**
 * Writes outgoing frames into one reused buffer. The hot path (PEN_BATCH) does not allocate.
 * Used by one thread (TX); a separate instance is used for control replies.
 */
class FrameWriter(capacity: Int = Wire.HEADER + 4 + Wire.MAX_BATCH * Wire.SAMPLE + 512) {
    val buf: ByteBuffer = ByteBuffer.allocate(capacity).order(ByteOrder.LITTLE_ENDIAN)
    private var payloadStart = 0

    /** Start a new frame; then write the fields and call [end]. */
    fun begin(channel: Int, type: Int): FrameWriter {
        buf.clear()
        buf.put(Wire.VERSION.toByte()).put(channel.toByte()).putShort(type.toShort()).putInt(0)
        payloadStart = buf.position()
        return this
    }

    /** Writes the length; returns the number of bytes to send (buf.array()[0 until n]). */
    fun end(): Int {
        val n = buf.position()
        buf.putInt(4, n - payloadStart)
        return n
    }

    fun u8(v: Int) = apply { buf.put(v.toByte()) }
    fun u16(v: Int) = apply { buf.putShort(v.toShort()) }
    fun u32(v: Long) = apply { buf.putInt(v.toInt()) }
    fun i64(v: Long) = apply { buf.putLong(v) }
    fun f32(v: Float) = apply { buf.putFloat(v) }
    fun bytes(b: ByteArray) = apply { buf.put(b) }
    /** u8 length + bytes. */
    fun bytes8(b: ByteArray) = apply {
        require(b.size <= 255) { "bytes8 is at most 255 bytes" }
        buf.put(b.size.toByte()).put(b)
    }
    fun str(s: String) = apply {
        val b = s.toByteArray(Charsets.UTF_8)
        require(b.size <= 255) { "str is at most 255 bytes" }
        buf.put(b.size.toByte()).put(b)
    }

    fun hello(transport: Int, caps: Int, deviceId: ByteArray, clientNonce: ByteArray, model: String): Int {
        require(deviceId.size == Auth.ID && clientNonce.size == Auth.NONCE)
        return begin(Wire.CH_CONTROL, Wire.HELLO).u16(Wire.MINOR).u8(transport).u8(caps).bytes(deviceId).bytes(clientNonce).str(model).end()
    }

    fun auth(proof: ByteArray): Int {
        require(proof.size == Auth.PROOF)
        return begin(Wire.CH_CONTROL, Wire.AUTH).bytes(proof).end()
    }

    fun pairRequest(code: Long, deviceName: String, tabletKey: ByteArray) =
        begin(Wire.CH_CONTROL, Wire.PAIR_REQUEST).u32(code).str(deviceName).bytes8(tabletKey).end()
    fun ping(id: Long, t0: Long) = begin(Wire.CH_CONTROL, Wire.PING).u32(id).i64(t0).end()
    fun pong(id: Long, t0: Long, t1: Long, t2: Long) = begin(Wire.CH_CONTROL, Wire.PONG).u32(id).i64(t0).i64(t1).i64(t2).end()
    fun surfaceInfo(wPx: Int, hPx: Int, wMm: Float, hMm: Float) =
        begin(Wire.CH_CONTROL, Wire.SURFACE_INFO).u32(wPx.toLong()).u32(hPx.toLong()).f32(wMm).f32(hMm).end()
    fun bye(reason: Int) = begin(Wire.CH_CONTROL, Wire.BYE).u8(reason).end()

    /** Start a PEN_BATCH; then [sample] × count, then [endBatch]. */
    fun beginBatch(count: Int): FrameWriter {
        require(count in 1..Wire.MAX_BATCH)
        return begin(Wire.CH_PEN, Wire.PEN_BATCH).u16(count).u16(0)
    }

    fun sample(tUs: Long, u: Float, v: Float, pressure: Float, tiltX: Float, tiltY: Float, flags: Int, seq: Int) {
        buf.putLong(tUs).putFloat(u).putFloat(v).putFloat(pressure).putFloat(tiltX).putFloat(tiltY)
            .putShort(flags.toShort()).putShort(seq.toShort())
    }

    fun endBatch() = end()
}
