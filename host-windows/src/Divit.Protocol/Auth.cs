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
        using var peer = ImportTabletKey(tabletPublicKey);
        byte[] secret;
        try { secret = mine.DeriveRawSecretAgreement(peer.PublicKey); }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        { throw new ProtocolException(ProtocolError.Range, "tablet public key cannot be used for ECDH"); }
        try { return (mine.ExportSubjectPublicKeyInfo(), PairKey(secret, deviceId, hostId)); }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    /// <summary>Throws <see cref="ProtocolException"/> unless the key is a valid P-256 public key (SubjectPublicKeyInfo).</summary>
    public static void CheckTabletKey(byte[] tabletPublicKey) { using var _ = ImportTabletKey(tabletPublicKey); }

    private const string P256Oid = "1.2.840.10045.3.1.7";

    private static ECDiffieHellman ImportTabletKey(byte[] spki)
    {
        var peer = ECDiffieHellman.Create();
        try
        {
            peer.ImportSubjectPublicKeyInfo(spki, out int read);
            if (read != spki.Length) throw new CryptographicException("trailing bytes");
            // Key size alone is not enough: brainpoolP256r1 is 256 bits too, and ECDH across curves throws.
            var curve = peer.ExportParameters(false).Curve;
            bool p256 = curve.IsNamed && (curve.Oid.Value == P256Oid ||
                        curve.Oid.FriendlyName is "nistP256" or "ECDH_P256" or "ECDSA_P256" or "secp256r1");
            if (!p256) throw new CryptographicException("not P-256");
            return peer;
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException or PlatformNotSupportedException)
        {
            peer.Dispose();
            throw new ProtocolException(ProtocolError.Range, $"invalid tablet public key ({e.Message})");
        }
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var b = new byte[parts.Sum(p => p.Length)];
        int o = 0;
        foreach (var p in parts) { p.CopyTo(b, o); o += p.Length; }
        return b;
    }
}
