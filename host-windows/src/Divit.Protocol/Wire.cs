// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// Frame header, message types and error kinds (protocol/SPEC.md §1–3).

using System.Buffers.Binary;

namespace Divit.Protocol;

public static class Wire
{
    public const byte Version = 1;
    /// <summary>1: PAIR_RESULT carries a failure reason (SPEC §3.3).</summary>
    public const ushort Minor = 1;
    public const int HeaderSize = 8;
    public const int MaxPayload = 65_536;
    public const int PenSampleSize = 32;
    public const int MaxBatch = 64;
    public const int DefaultPort = 47470;
    /// <summary>Edge tolerance: u, v ∈ [−0.01, 1.01] are accepted (SPEC §3.6).</summary>
    public const float EdgeTolerance = 0.01f;
    /// <summary>SURFACE_INFO size limits in mm (SPEC §3.5).</summary>
    public const float MinSurfaceMm = 1f, MaxSurfaceMm = 10_000f;
}

public enum Channel : byte { Control = 0, Pen = 1 }

public enum MessageType : ushort
{
    Hello = 0x0001,
    HelloAck = 0x0002,
    PairRequest = 0x0003,
    PairResult = 0x0004,
    Auth = 0x0005,
    AuthResult = 0x0006,
    Ping = 0x0010,
    Pong = 0x0011,
    SurfaceInfo = 0x0020,
    PenBatch = 0x0030,
    HostStatus = 0x0040,
    Bye = 0x00FF,
}

public enum ProtocolError { Version, Length, Range }

/// <summary>A protocol error that drops the connection (SPEC §1).</summary>
public sealed class ProtocolException(ProtocolError error, string message) : Exception(message)
{
    public ProtocolError Error { get; } = error;
}

public readonly record struct FrameHeader(byte Version, Channel Channel, MessageType Type, int Length)
{
    /// <summary>Reads and validates the 8-byte header (version, maximum payload).</summary>
    public static FrameHeader Read(ReadOnlySpan<byte> src)
    {
        if (src.Length < Wire.HeaderSize) throw new ProtocolException(ProtocolError.Length, "header too short");
        byte version = src[0];
        if (version != Wire.Version) throw new ProtocolException(ProtocolError.Version, $"version {version}, expected {Wire.Version}");
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(src[4..]);
        if (length > Wire.MaxPayload) throw new ProtocolException(ProtocolError.Length, $"payload too large: {length}");
        return new FrameHeader(version, (Channel)src[1], (MessageType)BinaryPrimitives.ReadUInt16LittleEndian(src[2..]), (int)length);
    }

    public void Write(Span<byte> dst)
    {
        dst[0] = Version;
        dst[1] = (byte)Channel;
        BinaryPrimitives.WriteUInt16LittleEndian(dst[2..], (ushort)Type);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[4..], (uint)Length);
    }
}

/// <summary>Bounds-checked sequential reader. Overrun → <see cref="ProtocolError.Length"/>.</summary>
public ref struct SpanReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _pos;

    public int Position => _pos;
    /// <summary>Bytes not read yet (optional fields added at the end in a later minor version, SPEC §6).</summary>
    public int Remaining => _data.Length - _pos;

    private ReadOnlySpan<byte> Take(int n)
    {
        if (_pos + n > _data.Length) throw new ProtocolException(ProtocolError.Length, $"message too short ({_data.Length} bytes)");
        var s = _data.Slice(_pos, n);
        _pos += n;
        return s;
    }

    public byte U8() => Take(1)[0];
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public long I64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
    public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
    public byte[] Bytes(int n) => Take(n).ToArray();
    /// <summary>u8 length + bytes (at most 255).</summary>
    public byte[] Bytes8() => Take(U8()).ToArray();
    public string Str() => System.Text.Encoding.UTF8.GetString(Take(U8()));
}

/// <summary>Sequential writer. A too-small destination throws <see cref="ArgumentException"/> (a programming error).</summary>
public ref struct SpanWriter(Span<byte> dst)
{
    private readonly Span<byte> _dst = dst;
    private int _pos;

    public int Position => _pos;

    private Span<byte> Take(int n)
    {
        if (_pos + n > _dst.Length) throw new ArgumentException("destination buffer too small");
        var s = _dst.Slice(_pos, n);
        _pos += n;
        return s;
    }

    public void U8(byte v) => Take(1)[0] = v;
    public void U16(ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(Take(2), v);
    public void U32(uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Take(4), v);
    public void I64(long v) => BinaryPrimitives.WriteInt64LittleEndian(Take(8), v);
    public void F32(float v) => BinaryPrimitives.WriteSingleLittleEndian(Take(4), v);
    public void Bytes(ReadOnlySpan<byte> v) => v.CopyTo(Take(v.Length));

    public void Bytes8(ReadOnlySpan<byte> v)
    {
        if (v.Length > 255) throw new ArgumentException("bytes8 is at most 255 bytes");
        U8((byte)v.Length);
        Bytes(v);
    }

    public void Str(string s)
    {
        int n = System.Text.Encoding.UTF8.GetByteCount(s);
        if (n > 255) throw new ArgumentException("str is at most 255 bytes");
        U8((byte)n);
        System.Text.Encoding.UTF8.GetBytes(s, Take(n));
    }
}
