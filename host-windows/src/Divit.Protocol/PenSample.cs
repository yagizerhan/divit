// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// PenSample and PEN_BATCH: the hot path. No allocation (SPEC §3.6, architecture §3.1).

using System.Buffers.Binary;

namespace Divit.Protocol;

[Flags]
public enum PenFlags : ushort
{
    None = 0,
    InRange = 1 << 0,
    InContact = 1 << 1,
    Barrel = 1 << 2,
    Historical = 1 << 3,
    Eraser = 1 << 4,
}

public readonly record struct PenSample(long TimeUs, float U, float V, float Pressure, float TiltX, float TiltY, PenFlags Flags, ushort Seq)
{
    public bool InRange => (Flags & PenFlags.InRange) != 0;
    public bool InContact => (Flags & PenFlags.InContact) != 0;
    public bool Barrel => (Flags & PenFlags.Barrel) != 0;

    public static PenSample Read(ReadOnlySpan<byte> s) => new(
        BinaryPrimitives.ReadInt64LittleEndian(s),
        BinaryPrimitives.ReadSingleLittleEndian(s[8..]),
        BinaryPrimitives.ReadSingleLittleEndian(s[12..]),
        BinaryPrimitives.ReadSingleLittleEndian(s[16..]),
        BinaryPrimitives.ReadSingleLittleEndian(s[20..]),
        BinaryPrimitives.ReadSingleLittleEndian(s[24..]),
        (PenFlags)BinaryPrimitives.ReadUInt16LittleEndian(s[28..]),
        BinaryPrimitives.ReadUInt16LittleEndian(s[30..]));

    public void Write(Span<byte> d)
    {
        BinaryPrimitives.WriteInt64LittleEndian(d, TimeUs);
        BinaryPrimitives.WriteSingleLittleEndian(d[8..], U);
        BinaryPrimitives.WriteSingleLittleEndian(d[12..], V);
        BinaryPrimitives.WriteSingleLittleEndian(d[16..], Pressure);
        BinaryPrimitives.WriteSingleLittleEndian(d[20..], TiltX);
        BinaryPrimitives.WriteSingleLittleEndian(d[24..], TiltY);
        BinaryPrimitives.WriteUInt16LittleEndian(d[28..], (ushort)Flags);
        BinaryPrimitives.WriteUInt16LittleEndian(d[30..], Seq);
    }

    /// <summary>Range check (SPEC §3.6). NaN comparisons are false, so NaN is rejected too.</summary>
    public void Validate()
    {
        const float lo = -Wire.EdgeTolerance, hi = 1 + Wire.EdgeTolerance;
        if (!(U >= lo && U <= hi && V >= lo && V <= hi)) throw Range($"u,v out of range ({U}, {V})");
        if (!(Pressure >= 0 && Pressure <= 1)) throw Range($"pressure out of range ({Pressure})");
        if (!(TiltX >= -90 && TiltX <= 90 && TiltY >= -90 && TiltY <= 90)) throw Range($"tilt out of range ({TiltX}, {TiltY})");
        if (InContact && !InRange) throw Range("contact without range");
    }

    private static ProtocolException Range(string m) => new(ProtocolError.Range, m);
}

public static class PenBatch
{
    /// <summary>
    /// Decodes and validates a PEN_BATCH payload into <paramref name="dst"/>; returns the sample count.
    /// No allocation. <paramref name="dst"/> must hold at least <see cref="Wire.MaxBatch"/> samples.
    /// </summary>
    public static int Read(ReadOnlySpan<byte> payload, Span<PenSample> dst)
    {
        if (payload.Length < 4) throw new ProtocolException(ProtocolError.Length, "PEN_BATCH too short");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        if (count is < 1 or > Wire.MaxBatch) throw new ProtocolException(ProtocolError.Range, $"sample count {count}");
        if (payload.Length != 4 + count * Wire.PenSampleSize)
            throw new ProtocolException(ProtocolError.Length, $"PEN_BATCH length {payload.Length}, expected {4 + count * Wire.PenSampleSize}");
        for (int i = 0; i < count; i++)
        {
            var s = PenSample.Read(payload.Slice(4 + i * Wire.PenSampleSize, Wire.PenSampleSize));
            s.Validate();
            dst[i] = s;
        }
        return count;
    }

    /// <summary>Writes the full frame including the header; returns the number of bytes written.</summary>
    public static int WriteFrame(Span<byte> dst, ReadOnlySpan<PenSample> samples)
    {
        int len = 4 + samples.Length * Wire.PenSampleSize;
        new FrameHeader(Wire.Version, Channel.Pen, MessageType.PenBatch, len).Write(dst);
        var p = dst.Slice(Wire.HeaderSize, len);
        BinaryPrimitives.WriteUInt16LittleEndian(p, (ushort)samples.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(p[2..], 0);
        for (int i = 0; i < samples.Length; i++) samples[i].Write(p.Slice(4 + i * Wire.PenSampleSize));
        return Wire.HeaderSize + len;
    }
}
