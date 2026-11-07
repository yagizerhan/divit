// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// Authentication (protocol/SPEC.md §3.2–3.3). The key never goes on the wire: both sides prove themselves with HMAC.
//   PC proof     = HMAC-SHA256(K, "DIVIT-HOST1" ‖ deviceId ‖ hostId ‖ clientNonce ‖ serverNonce)
//   tablet proof = HMAC-SHA256(K, "DIVIT-TAB1"  ‖ deviceId ‖ hostId ‖ clientNonce ‖ serverNonce)
//   pairing key  = HMAC-SHA256(ECDH (P-256) shared secret, "DIVIT-PAIR1" ‖ deviceId ‖ hostId)
// K: the pairing key on Wi-Fi, or the USB key the PC sends to the tablet over adb (both 32 bytes).

using System.Security.Cryptography;
using System.Text;

namespace Divit.Protocol;

public static class Auth
{
    public const int NonceSize = 16, IdSize = 16, ProofSize = 32, KeySize = 32;

    private static readonly byte[] HostLabel = Encoding.ASCII.GetBytes("DIVIT-HOST1");
    private static readonly byte[] TabletLabel = Encoding.ASCII.GetBytes("DIVIT-TAB1");
    private static readonly byte[] PairLabel = Encoding.ASCII.GetBytes("DIVIT-PAIR1");

    public static byte[] HostProof(byte[] key, byte[] deviceId, byte[] hostId, byte[] clientNonce, byte[] serverNonce) =>
        HMACSHA256.HashData(key, Concat(HostLabel, deviceId, hostId, clientNonce, serverNonce));

    public static byte[] TabletProof(byte[] key, byte[] deviceId, byte[] hostId, byte[] clientNonce, byte[] serverNonce) =>
        HMACSHA256.HashData(key, Concat(TabletLabel, deviceId, hostId, clientNonce, serverNonce));

    public static byte[] PairKey(byte[] sharedSecret, byte[] deviceId, byte[] hostId) =>
        HMACSHA256.HashData(sharedSecret, Concat(PairLabel, deviceId, hostId));

    /// <summary>Constant-time comparison (no timing side channel).</summary>
    public static bool Equal(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => CryptographicOperations.FixedTimeEquals(a, b);

    public static byte[] Random(int n) => RandomNumberGenerator.GetBytes(n);

    /// <summary>
    /// The PC side of pairing: ECDH with the tablet's P-256 public key (SubjectPublicKeyInfo, DER).
    /// Returns the PC's public key (sent to the tablet) and the derived pairing key.
    /// A passive eavesdropper cannot obtain the key. There is no protection against an active man-in-the-middle
    /// at the moment of pairing.
    /// </summary>
    public static (byte[] HostPublicKey, byte[] PairKey) PairAsHost(byte[] tabletPublicKey, byte[] deviceId, byte[] hostId)
    {
        using var mine = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var peer = ECDiffieHellman.Create();
        try { peer.ImportSubjectPublicKeyInfo(tabletPublicKey, out _); }
        catch (CryptographicException) { throw new ProtocolException(ProtocolError.Range, "invalid tablet public key"); }
        if (peer.KeySize != 256) throw new ProtocolException(ProtocolError.Range, "tablet public key is not P-256");
        byte[] secret = mine.DeriveRawSecretAgreement(peer.PublicKey);
        try { return (mine.ExportSubjectPublicKeyInfo(), PairKey(secret, deviceId, hostId)); }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var b = new byte[parts.Sum(p => p.Length)];
        int o = 0;
        foreach (var p in parts) { p.CopyTo(b, o); o += p.Length; }
        return b;
    }
}
