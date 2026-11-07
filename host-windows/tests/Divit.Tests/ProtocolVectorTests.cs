// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// Conformance with protocol/test-vectors/vectors.json: every valid vector must decode to the same fields and encode
// back to the same bytes; every invalid vector must be rejected with the right error kind.

using System.Text.Json;
using Divit.Protocol;
using Xunit;

namespace Divit.Tests;

public class ProtocolVectorTests
{
    private static readonly JsonElement Root =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors.json"))).RootElement;

    public static TheoryData<string> Valid() => Names(true);
    public static TheoryData<string> Invalid() => Names(false);

    private static TheoryData<string> Names(bool valid)
    {
        var d = new TheoryData<string>();
        foreach (var v in Root.GetProperty("vectors").EnumerateArray())
            if (v.GetProperty("valid").GetBoolean() == valid) d.Add(v.GetProperty("name").GetString()!);
        return d;
    }

    private static JsonElement Vector(string name) =>
        Root.GetProperty("vectors").EnumerateArray().Single(v => v.GetProperty("name").GetString() == name);

    [Fact]
    public void VersionMatches() => Assert.Equal(Wire.Version, Root.GetProperty("protocolVersion").GetInt32());

    [Theory, MemberData(nameof(Valid))]
    public void DecodesAndReencodes(string name)
    {
        var v = Vector(name);
        byte[] bytes = Convert.FromHexString(v.GetProperty("hex").GetString()!);
        var msg = Codec.DecodeFrame(bytes);
        AssertFields(v.GetProperty("message"), msg);

        // An unknown type and a message with trailing bytes cannot re-encode byte for byte (by design).
        if (msg is not UnknownMessage && name != "ping_with_trailing_bytes")
            Assert.Equal(Convert.ToHexString(bytes), Convert.ToHexString(Codec.Encode(msg)));
    }

    [Theory, MemberData(nameof(Invalid))]
    public void Rejects(string name)
    {
        var v = Vector(name);
        byte[] bytes = Convert.FromHexString(v.GetProperty("hex").GetString()!);
        var ex = Assert.Throws<ProtocolException>(() => Codec.DecodeFrame(bytes));
        var expected = v.GetProperty("error").GetString() switch
        {
            "version" => ProtocolError.Version,
            "length" => ProtocolError.Length,
            "range" => ProtocolError.Range,
            var e => throw new InvalidOperationException(e),
        };
        Assert.Equal(expected, ex.Error);
    }

    [Fact]
    public void PenBatchHotPathDoesNotAllocate()
    {
        byte[] bytes = Convert.FromHexString(Vector("pen_batch_3").GetProperty("hex").GetString()!);
        var payload = bytes.AsSpan(Wire.HeaderSize);
        var buf = new PenSample[Wire.MaxBatch];
        PenBatch.Read(payload, buf); // warm-up
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) PenBatch.Read(payload, buf);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static void AssertFields(JsonElement expected, Message msg)
    {
        string type = expected.GetProperty("type").GetString()!;
        Assert.Equal(type, TypeName(msg));
        foreach (var p in expected.EnumerateObject())
        {
            if (p.Name == "type") continue;
            if (p.Name == "samples")
            {
                var samples = ((PenBatchMessage)msg).Samples;
                var exp = p.Value.EnumerateArray().ToArray();
                Assert.Equal(exp.Length, samples.Length);
                for (int i = 0; i < exp.Length; i++)
                    foreach (var f in exp[i].EnumerateObject()) AssertValue(f.Value, SampleField(samples[i], f.Name), $"samples[{i}].{f.Name}");
                continue;
            }
            AssertValue(p.Value, Field(msg, p.Name), p.Name);
        }
    }

    private static void AssertValue(JsonElement e, object actual, string what)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Number: Assert.True(e.GetDouble() == Convert.ToDouble(actual), $"{what}: {e} ≠ {actual}"); break;
            case JsonValueKind.String:
                string s = actual is byte[] b ? Convert.ToHexString(b).ToLowerInvariant() : (string)actual;
                Assert.Equal(e.GetString(), s);
                break;
            default: throw new InvalidOperationException(what);
        }
    }

    private static string TypeName(Message m) => m switch
    {
        Hello => "HELLO", HelloAck => "HELLO_ACK", PairRequest => "PAIR_REQUEST", PairResult => "PAIR_RESULT",
        AuthRequest => "AUTH", AuthResult => "AUTH_RESULT",
        Ping => "PING", Pong => "PONG", SurfaceInfo => "SURFACE_INFO", PenBatchMessage => "PEN_BATCH",
        HostStatus => "HOST_STATUS", Bye => "BYE", UnknownMessage => "UNKNOWN", _ => "?",
    };

    private static object Field(Message m, string name) => (m, name) switch
    {
        (Hello h, "minor") => h.Minor, (Hello h, "transport") => (byte)h.Transport, (Hello h, "capabilities") => (byte)h.Caps,
        (Hello h, "deviceId") => h.DeviceId, (Hello h, "clientNonce") => h.ClientNonce, (Hello h, "model") => h.Model,
        (HelloAck a, "result") => (byte)a.Result, (HelloAck a, "minor") => a.Minor, (HelloAck a, "hostName") => a.HostName,
        (HelloAck a, "hostId") => a.HostId, (HelloAck a, "serverNonce") => a.ServerNonce, (HelloAck a, "hostProof") => a.HostProof,
        (AuthRequest a, "proof") => a.Proof, (AuthResult a, "ok") => a.Ok ? 1 : 0,
        (PairRequest r, "code") => r.Code, (PairRequest r, "deviceName") => r.DeviceName, (PairRequest r, "tabletKey") => r.TabletKey,
        (PairResult r, "ok") => r.Ok ? 1 : 0, (PairResult r, "hostKey") => r.HostKey,
        (Ping p, "id") => p.Id, (Ping p, "t0") => p.T0,
        (Pong p, "id") => p.Id, (Pong p, "t0") => p.T0, (Pong p, "t1") => p.T1, (Pong p, "t2") => p.T2,
        (SurfaceInfo s, "widthPx") => s.WidthPx, (SurfaceInfo s, "heightPx") => s.HeightPx,
        (SurfaceInfo s, "widthMm") => s.WidthMm, (SurfaceInfo s, "heightMm") => s.HeightMm,
        (HostStatus s, "monitorName") => s.MonitorName, (HostStatus s, "areaLeft") => s.AreaLeft, (HostStatus s, "areaTop") => s.AreaTop,
        (HostStatus s, "areaRight") => s.AreaRight, (HostStatus s, "areaBottom") => s.AreaBottom,
        (Bye b, "reason") => (byte)b.Reason,
        (UnknownMessage u, "rawType") => u.RawType,
        _ => throw new InvalidOperationException($"bilinmeyen alan {name}"),
    };

    // ---- Cryptography (SPEC §3.2–3.3): same values as the independent Python reference ----

    private static byte[] C(string name) => Convert.FromHexString(Root.GetProperty("crypto").GetProperty(name).GetString()!);

    [Fact]
    public void ProofsMatchReference()
    {
        Assert.Equal(C("hostProof"), Auth.HostProof(C("key"), C("deviceId"), C("hostId"), C("clientNonce"), C("serverNonce")));
        Assert.Equal(C("tabletProof"), Auth.TabletProof(C("key"), C("deviceId"), C("hostId"), C("clientNonce"), C("serverNonce")));
    }

    [Fact]
    public void EcdhPairingMatchesReference()
    {
        // With the PC's fixed private key, against the tablet's public key in SubjectPublicKeyInfo form (as Java produces it).
        using var host = System.Security.Cryptography.ECDiffieHellman.Create();
        host.ImportPkcs8PrivateKey(C("hostPrivatePkcs8"), out _);
        using var tablet = System.Security.Cryptography.ECDiffieHellman.Create();
        tablet.ImportSubjectPublicKeyInfo(C("tabletPublicSpki"), out _);
        byte[] secret = host.DeriveRawSecretAgreement(tablet.PublicKey);
        Assert.Equal(C("sharedSecret"), secret);
        Assert.Equal(C("pairKey"), Auth.PairKey(secret, C("deviceId"), C("hostId")));
        Assert.Equal(C("hostPublicSpki"), host.ExportSubjectPublicKeyInfo());   // the tablet must be able to decode this format
    }

    [Fact]
    public void PairAsHostRejectsGarbageKey() =>
        Assert.Equal(ProtocolError.Range, Assert.Throws<ProtocolException>(() => Auth.PairAsHost(new byte[] { 1, 2, 3 }, new byte[16], new byte[16])).Error);

    private static object SampleField(PenSample s, string name) => name switch
    {
        "t_us" => s.TimeUs, "u" => s.U, "v" => s.V, "pressure" => s.Pressure, "tiltX" => s.TiltX, "tiltY" => s.TiltY,
        "flags" => (ushort)s.Flags, "seq" => s.Seq, _ => throw new InvalidOperationException(name),
    };
}
