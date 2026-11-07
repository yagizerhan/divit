// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// Clock sync (SPEC §3.4) and latency histogram (SPEC §5).

using System.Diagnostics;

namespace Divit.Core;

public static class MonoClock
{
    private static readonly double UsPerTick = 1e6 / Stopwatch.Frequency;
    /// <summary>Monotonic clock, µs (Stopwatch). Multiplies in double so ticks × 10⁶ cannot overflow a long.</summary>
    public static long NowUs() => (long)(Stopwatch.GetTimestamp() * UsPerTick);
}

/// <summary>
/// "host clock − tablet clock" from the PINGs the host sends. The lowest-RTT measurement of the last 16 is used.
/// Written and read by the receive thread; <see cref="OffsetUs"/> is an atomic long so the UI can read it too.
/// </summary>
public sealed class ClockSync
{
    private const int Window = 16;
    private readonly (long rtt, long offset)[] _ring = new (long, long)[Window];
    private int _count, _next;
    private long _offset = long.MinValue, _rtt = -1;

    /// <summary>host − tablet (µs); null until the first measurement.</summary>
    public long? OffsetUs { get { long o = Interlocked.Read(ref _offset); return o == long.MinValue ? null : o; } }
    public long RttUs => Interlocked.Read(ref _rtt);

    /// <param name="t0">PING sent (host)</param><param name="t1">PING received (tablet)</param>
    /// <param name="t2">PONG sent (tablet)</param><param name="t3">PONG received (host)</param>
    public void Add(long t0, long t1, long t2, long t3)
    {
        long rtt = (t3 - t0) - (t2 - t1);
        if (rtt < 0) return;
        long tabletMinusHost = ((t1 - t0) + (t2 - t3)) / 2;
        _ring[_next] = (rtt, -tabletMinusHost);
        _next = (_next + 1) % Window;
        if (_count < Window) _count++;
        var best = _ring[0];
        for (int i = 1; i < _count; i++) if (_ring[i].rtt < best.rtt) best = _ring[i];
        Interlocked.Exchange(ref _offset, best.offset);
        Interlocked.Exchange(ref _rtt, rtt);
    }

    public void Reset() { _count = 0; _next = 0; Interlocked.Exchange(ref _offset, long.MinValue); Interlocked.Exchange(ref _rtt, -1); }
}

/// <summary>50 µs buckets from 0 to 100 ms plus an overflow bucket. Allocation-free, single writer.</summary>
public sealed class LatencyHistogram
{
    private const int BucketUs = 50, Buckets = 2000;
    private readonly long[] _b = new long[Buckets + 1];
    private long _n;

    public long Count => Interlocked.Read(ref _n);

    public void Add(long us)
    {
        int i = us < 0 ? 0 : (int)Math.Min(us / BucketUs, Buckets);
        Interlocked.Increment(ref _b[i]);
        Interlocked.Increment(ref _n);
    }

    /// <summary>Percentile (µs, bucket upper bound). -1 if there is no data.</summary>
    public long Percentile(double p)
    {
        long n = Count;
        if (n == 0) return -1;
        long target = (long)Math.Ceiling(p / 100 * n), acc = 0;
        for (int i = 0; i <= Buckets; i++)
        {
            acc += Interlocked.Read(ref _b[i]);
            if (acc >= target) return (i + 1L) * BucketUs;
        }
        return (Buckets + 1L) * BucketUs;
    }

    public void Reset() { Array.Clear(_b); Interlocked.Exchange(ref _n, 0); }
}
