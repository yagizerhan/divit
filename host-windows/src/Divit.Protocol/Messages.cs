// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// Control messages (SPEC §3). Not on the hot path, so records are used for readability.
// The hot path decodes PEN_BATCH with PenBatch.Read; PenBatchMessage here is only for tests and tools.

namespace Divit.Protocol;

public enum TransportKind : byte { Usb = 0, Wifi = 1 }

[Flags]
public enum Capabilities : byte
{
    None = 0, Pressure = 1, Tilt = 2, Barrel = 4, Hover = 8, Eraser = 16,
}

/// <summary>0: the PC knows a key for this device and attaches its proof; the tablet then proves itself with AUTH.
/// 2: no key (Wi-Fi: pairing required). Value 3 ("invalid token") is retired; that case is now AUTH_RESULT(0).</summary>
public enum HelloResult : byte { Authenticate = 0, VersionMismatch = 1, PairingRequired = 2 }

public enum ByeReason : byte { Normal = 0, ProtocolError = 1, PairingFailed = 2, Replaced = 3 }

public abstract record Message
{
    public abstract MessageType Type { get; }
    public virtual Channel Channel => Channel.Control;
    internal virtual void WritePayload(ref SpanWriter w) => throw new NotSupportedException();
}

public sealed record Hello(ushort Minor, TransportKind Transport, Capabilities Caps, byte[] DeviceId, byte[] ClientNonce, string Model) : Message
{
    public override MessageType Type => MessageType.Hello;
    internal override void WritePayload(ref SpanWriter w)
    {
        w.U16(Minor); w.U8((byte)Transport); w.U8((byte)Caps);
        w.Bytes(Fixed(DeviceId, 16)); w.Bytes(Fixed(ClientNonce, 16)); w.Str(Model);
    }
    internal static byte[] Fixed(byte[] b, int n) => b.Length == n ? b : throw new ArgumentException($"must be {n} bytes");
}

/// <param name="HostId">The PC's permanent ID (the tablet picks its key by it and tells impostors apart).</param>
/// <param name="HostProof">The PC's proof (Auth.HostProof) when Result = Authenticate, otherwise zero.</param>
public sealed record HelloAck(HelloResult Result, ushort Minor, string HostName, byte[] HostId, byte[] ServerNonce, byte[] HostProof) : Message
{
    public override MessageType Type => MessageType.HelloAck;
    internal override void WritePayload(ref SpanWriter w)
    {
        w.U8((byte)Result); w.U16(Minor); w.Str(HostName);
        w.Bytes(Hello.Fixed(HostId, 16)); w.Bytes(Hello.Fixed(ServerNonce, 16)); w.Bytes(Hello.Fixed(HostProof, 32));
    }
}

/// <summary>The tablet's proof (Auth.TabletProof). Zero if the tablet has no key for this PC.</summary>
public sealed record AuthRequest(byte[] Proof) : Message
{
    public override MessageType Type => MessageType.Auth;
    internal override void WritePayload(ref SpanWriter w) => w.Bytes(Hello.Fixed(Proof, 32));
}

public sealed record AuthResult(bool Ok) : Message
{
    public override MessageType Type => MessageType.AuthResult;
    internal override void WritePayload(ref SpanWriter w) => w.U8(Ok ? (byte)1 : (byte)0);
}

/// <param name="TabletKey">The tablet's ephemeral P-256 public key (SubjectPublicKeyInfo, DER).</param>
public sealed record PairRequest(uint Code, string DeviceName, byte[] TabletKey) : Message
{
    public override MessageType Type => MessageType.PairRequest;
    internal override void WritePayload(ref SpanWriter w) { w.U32(Code); w.Str(DeviceName); w.Bytes8(TabletKey); }
}

/// <summary>Why pairing failed (PAIR_RESULT, minor 1). Older hosts send no reason: read as <see cref="WrongCode"/>.</summary>
public enum PairFailure : byte { WrongCode = 0, Paused = 1, WifiOff = 2 }

/// <param name="HostKey">On success the PC's ephemeral P-256 public key; empty on failure.</param>
/// <param name="Reason">On failure: why (minor 1). Ignored on success.</param>
public sealed record PairResult(bool Ok, byte[] HostKey, PairFailure Reason = PairFailure.WrongCode) : Message
{
    public override MessageType Type => MessageType.PairResult;
    internal override void WritePayload(ref SpanWriter w) { w.U8(Ok ? (byte)1 : (byte)0); w.Bytes8(HostKey); w.U8((byte)Reason); }
}

public sealed record Ping(uint Id, long T0) : Message
{
    public override MessageType Type => MessageType.Ping;
    internal override void WritePayload(ref SpanWriter w) { w.U32(Id); w.I64(T0); }
}

public sealed record Pong(uint Id, long T0, long T1, long T2) : Message
{
    public override MessageType Type => MessageType.Pong;
    internal override void WritePayload(ref SpanWriter w) { w.U32(Id); w.I64(T0); w.I64(T1); w.I64(T2); }
}

public sealed record SurfaceInfo(uint WidthPx, uint HeightPx, float WidthMm, float HeightMm) : Message
{
    public override MessageType Type => MessageType.SurfaceInfo;
    internal override void WritePayload(ref SpanWriter w) { w.U32(WidthPx); w.U32(HeightPx); w.F32(WidthMm); w.F32(HeightMm); }
}

public sealed record HostStatus(string MonitorName, float AreaLeft, float AreaTop, float AreaRight, float AreaBottom) : Message
{
    public override MessageType Type => MessageType.HostStatus;
    internal override void WritePayload(ref SpanWriter w)
    {
        w.Str(MonitorName); w.F32(AreaLeft); w.F32(AreaTop); w.F32(AreaRight); w.F32(AreaBottom);
    }
}

public sealed record Bye(ByeReason Reason) : Message
{
    public override MessageType Type => MessageType.Bye;
    internal override void WritePayload(ref SpanWriter w) => w.U8((byte)Reason);
}

public sealed record PenBatchMessage(PenSample[] Samples) : Message
{
    public override MessageType Type => MessageType.PenBatch;
    public override Channel Channel => Channel.Pen;
}

/// <summary>Unknown type: the receiver ignores it (SPEC §1).</summary>
public sealed record UnknownMessage(ushort RawType) : Message
{
    public override MessageType Type => (MessageType)RawType;
}

public static class Codec
{
    /// <summary>Decodes the payload after the header. Extra bytes at the end of a known message are ignored (SPEC §6).</summary>
    public static Message Decode(MessageType type, ReadOnlySpan<byte> payload)
    {
        var r = new SpanReader(payload);
        switch (type)
        {
            case MessageType.Hello:
                return new Hello(r.U16(), (TransportKind)r.U8(), (Capabilities)r.U8(), r.Bytes(16), r.Bytes(16), r.Str());
            case MessageType.HelloAck:
                return new HelloAck((HelloResult)r.U8(), r.U16(), r.Str(), r.Bytes(16), r.Bytes(16), r.Bytes(32));
            case MessageType.Auth:
                return new AuthRequest(r.Bytes(32));
            case MessageType.AuthResult:
                return new AuthResult(r.U8() == 1);
            case MessageType.PairRequest:
                {
                    uint code = r.U32();
                    if (code > 999_999) throw new ProtocolException(ProtocolError.Range, "code has more than 6 digits");
                    return new PairRequest(code, r.Str(), r.Bytes8());
                }
            case MessageType.PairResult:
                {
                    bool ok = r.U8() == 1;
                    byte[] key = r.Bytes8();
                    return new PairResult(ok, key, r.Remaining > 0 ? (PairFailure)r.U8() : PairFailure.WrongCode);
                }
            case MessageType.Ping:
                return new Ping(r.U32(), r.I64());
            case MessageType.Pong:
                return new Pong(r.U32(), r.I64(), r.I64(), r.I64());
            case MessageType.SurfaceInfo:
                {
                    var m = new SurfaceInfo(r.U32(), r.U32(), r.F32(), r.F32());
                    // 1…10 000 mm: keeps the aspect ratio finite and sane (∞ or a 0-wide area would break the mapping).
                    static bool Mm(float v) => v is >= Wire.MinSurfaceMm and <= Wire.MaxSurfaceMm;
                    if (m.WidthPx == 0 || m.HeightPx == 0 || !Mm(m.WidthMm) || !Mm(m.HeightMm))
                        throw new ProtocolException(ProtocolError.Range, "invalid surface size");
                    return m;
                }
            case MessageType.HostStatus:
                return new HostStatus(r.Str(), r.F32(), r.F32(), r.F32(), r.F32());
            case MessageType.Bye:
                return new Bye((ByeReason)r.U8());
            case MessageType.PenBatch:
                {
                    Span<PenSample> buf = stackalloc PenSample[Wire.MaxBatch];
                    int n = PenBatch.Read(payload, buf);
                    return new PenBatchMessage(buf[..n].ToArray());
                }
            default:
                return new UnknownMessage((ushort)type);
        }
    }

    /// <summary>Decodes a full frame (header + payload).</summary>
    public static Message DecodeFrame(ReadOnlySpan<byte> frame)
    {
        var h = FrameHeader.Read(frame);
        if (frame.Length - Wire.HeaderSize < h.Length) throw new ProtocolException(ProtocolError.Length, "frame truncated");
        return Decode(h.Type, frame.Slice(Wire.HeaderSize, h.Length));
    }

    /// <summary>Encodes a control message as a full frame.</summary>
    public static byte[] Encode(Message m)
    {
        if (m is PenBatchMessage pb)
        {
            var b = new byte[Wire.HeaderSize + 4 + pb.Samples.Length * Wire.PenSampleSize];
            PenBatch.WriteFrame(b, pb.Samples);
            return b;
        }
        Span<byte> tmp = stackalloc byte[1024];
        var w = new SpanWriter(tmp[Wire.HeaderSize..]);
        m.WritePayload(ref w);
        new FrameHeader(Wire.Version, m.Channel, m.Type, w.Position).Write(tmp);
        return tmp[..(Wire.HeaderSize + w.Position)].ToArray();
    }
}
