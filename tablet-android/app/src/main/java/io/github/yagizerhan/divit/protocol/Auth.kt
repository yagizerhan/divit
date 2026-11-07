// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// Authentication (protocol/SPEC.md §3.2–3.3); identical to Divit.Protocol.Auth on the host. The key never goes on the wire.
//   PC proof     = HMAC-SHA256(K, "DIVIT-HOST1" ‖ deviceId ‖ hostId ‖ clientNonce ‖ serverNonce)
//   tablet proof = HMAC-SHA256(K, "DIVIT-TAB1"  ‖ deviceId ‖ hostId ‖ clientNonce ‖ serverNonce)
//   pairing key  = HMAC-SHA256(ECDH (P-256) shared secret, "DIVIT-PAIR1" ‖ deviceId ‖ hostId)
// Pure JVM (javax.crypto): no Android classes, runs in unit tests.

package io.github.yagizerhan.divit.protocol

import java.security.KeyFactory
import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.spec.ECGenParameterSpec
import java.security.spec.X509EncodedKeySpec
import javax.crypto.KeyAgreement
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

object Auth {
    const val NONCE = 16
    const val ID = 16
    const val PROOF = 32
    const val KEY = 32

    private val rng = SecureRandom()

    fun random(n: Int) = ByteArray(n).also(rng::nextBytes)

    fun hostProof(key: ByteArray, deviceId: ByteArray, hostId: ByteArray, clientNonce: ByteArray, serverNonce: ByteArray) =
        hmac(key, "DIVIT-HOST1".toByteArray(Charsets.US_ASCII), deviceId, hostId, clientNonce, serverNonce)

    fun tabletProof(key: ByteArray, deviceId: ByteArray, hostId: ByteArray, clientNonce: ByteArray, serverNonce: ByteArray) =
        hmac(key, "DIVIT-TAB1".toByteArray(Charsets.US_ASCII), deviceId, hostId, clientNonce, serverNonce)

    fun pairKey(sharedSecret: ByteArray, deviceId: ByteArray, hostId: ByteArray) =
        hmac(sharedSecret, "DIVIT-PAIR1".toByteArray(Charsets.US_ASCII), deviceId, hostId)

    /** Constant-time comparison. */
    fun equal(a: ByteArray, b: ByteArray) = MessageDigest.isEqual(a, b)

    /** Ephemeral P-256 key pair for pairing; the public key is `public.encoded` (SubjectPublicKeyInfo, DER). */
    fun newPairingKeyPair(): KeyPair =
        KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1"), rng) }.generateKeyPair()

    /** ECDH: shared secret with the PC's public key (32 bytes, the x coordinate; same as .NET DeriveRawSecretAgreement). */
    fun agree(mine: KeyPair, hostPublicKey: ByteArray): ByteArray {
        val peer = try {
            KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(hostPublicKey))
        } catch (e: Exception) {
            throw ProtocolException(ProtocolException.Kind.RANGE, "invalid PC public key")
        }
        return KeyAgreement.getInstance("ECDH").run { init(mine.private); doPhase(peer, true); generateSecret() }
    }

    private fun hmac(key: ByteArray, vararg parts: ByteArray): ByteArray =
        Mac.getInstance("HmacSHA256").run { init(SecretKeySpec(key, "HmacSHA256")); parts.forEach(::update); doFinal() }
}
