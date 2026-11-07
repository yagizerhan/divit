// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// Host side of the WinTab bridge (docs/wintab.md): writes pen samples to shared memory and wakes the wintab32.dll
// instances loaded inside drawing apps through events.
// Hot path: no allocation (an event handle is opened once when a new DLL instance registers).

using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using Divit.Core;

namespace Divit.Platform.Windows;

public sealed unsafe class WinTabSink : IPenSink
{
    public const string MapName = @"Local\Divit.WinTab.v1";
    public const uint Magic = 0x54575644;   // "DVWT"
    public const int RingSize = 1024, PacketSize = 32, MaxConsumers = 16;
    public const int OffWriteIndex = 64, OffConsumers = 72, OffRing = 256;
    public const int TotalSize = OffRing + RingSize * PacketSize;

    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _p;
    private readonly object _gate = new();
    private readonly EventWaitHandle?[] _events = new EventWaitHandle?[MaxConsumers];
    private readonly uint[] _eventPid = new uint[MaxConsumers];
    private readonly Timer _reaper;
    // Unsigned: the counter wraps at 2^32 without going negative; the ring index is n & (RingSize - 1).
    private uint _written;
    private readonly string _name;

    /// <param name="mapName">A different name for tests only (matches DIVIT_WINTAB_MAP in the DLL).</param>
    public WinTabSink(string mapName = MapName)
    {
        _name = mapName;
        _map = MemoryMappedFile.CreateOrOpen(mapName, TotalSize, MemoryMappedFileAccess.ReadWrite);
        _view = _map.CreateViewAccessor(0, TotalSize);
        byte* p = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _p = p + _view.PointerOffset;

        // The header and write index are reset; consumer registrations are KEPT. When the host crashes and restarts, the
        // memory is the same section the DLLs in open apps still hold: wiping their entries would stop their events from
        // ever being signaled again. Reap() removes entries of dead processes. (The DLL also re-registers on every wake.)
        new Span<byte>(_p, OffConsumers).Clear();
        U32(4) = 1;                                            // version
        U32(8) = (uint)Environment.ProcessId;
        U32(32) = 4096;                                        // pressureLevels
        U32(44) = (uint)Random.Shared.Next() | 1;              // generation: the DLLs see that the host restarted
        Volatile.Write(ref U32(OffWriteIndex), 0u);
        Volatile.Write(ref U32(0), Magic);                     // last: DLLs see a complete header
        _reaper = new Timer(_ => Reap(), null, 2000, 2000);
    }

    private ref uint U32(int off) => ref *(uint*)(_p + off);
    private ref int I32(int off) => ref *(int*)(_p + off);

    /// <summary>The monitor the pen is mapped to (the DLL reports it as lcSysOrg/Ext of WTI_DEFSYSCTX).</summary>
    public void SetTarget(ScreenRect r)
    {
        I32(16) = r.Left; I32(20) = r.Top; I32(24) = r.Width; I32(28) = r.Height;
    }

    /// <summary>
    /// The tablet's drawing surface in mm (from SURFACE_INFO). The DLL reports the tablet resolution from it
    /// (DVC_X/DVC_Y); 0 = unknown, the DLL then uses its built-in default. Out-of-range values are ignored.
    /// </summary>
    public void SetSurfaceMm(float widthMm, float heightMm)
    {
        static bool Ok(float mm) => mm is >= 20f and <= 2000f;
        if (!Ok(widthMm) || !Ok(heightMm)) return;
        Volatile.Write(ref U32(36), (uint)(widthMm * 1000f));
        Volatile.Write(ref U32(40), (uint)(heightMm * 1000f));
    }

    public void SetConnected(bool on) => Volatile.Write(ref U32(12), on ? 1u : 0u);

    public bool Apply(in PenOutput o)
    {
        uint n = _written;
        byte* pk = _p + OffRing + (int)(n & (RingSize - 1)) * PacketSize;
        *(uint*)pk = n;
        *(uint*)(pk + 4) = (uint)Environment.TickCount;
        *(float*)(pk + 8) = o.NormX;
        *(float*)(pk + 12) = o.NormY;
        *(float*)(pk + 16) = o.PressureF;
        *(short*)(pk + 20) = (short)o.TiltX;
        *(short*)(pk + 22) = (short)o.TiltY;
        ushort flags = 0;
        if (o.Transition != PenTransition.Leave) flags |= 1;
        if (o.Transition is PenTransition.Down or PenTransition.Move) flags |= 2;
        if (o.Barrel) flags |= 4;
        if (o.Eraser) flags |= 8;
        *(ushort*)(pk + 24) = flags;
        _written = n + 1;
        Volatile.Write(ref U32(OffWriteIndex), n + 1);   // publish after the packet is written (release)
        Signal();
        return true;   // writing to WinTab cannot fail; the state machine follows Windows Ink
    }

    /// <summary>Signal the event of every registered DLL instance.</summary>
    private void Signal()
    {
        lock (_gate)
        {
            for (int i = 0; i < MaxConsumers; i++)
            {
                uint pid = Volatile.Read(ref U32(OffConsumers + i * 8));
                if (pid == 0) continue;
                if (_eventPid[i] != pid)
                {
                    _events[i]?.Dispose();
                    _events[i] = EventWaitHandle.TryOpenExisting($"{_name}.{pid}", out var h) ? h : null;
                    _eventPid[i] = pid;
                }
                _events[i]?.Set();
            }
        }
    }

    /// <summary>Remove entries of apps that exited (or crashed).</summary>
    private void Reap()
    {
        lock (_gate)
        {
            for (int i = 0; i < MaxConsumers; i++)
            {
                uint pid = Volatile.Read(ref U32(OffConsumers + i * 8));
                if (pid == 0) continue;
                bool alive;
                try { using var pr = Process.GetProcessById((int)pid); alive = !pr.HasExited; }
                catch { alive = false; }
                if (alive) continue;
                Interlocked.CompareExchange(ref U32(OffConsumers + i * 8), 0, pid);
                _events[i]?.Dispose(); _events[i] = null; _eventPid[i] = 0;
            }
        }
    }

    /// <summary>Number of apps currently using WinTab (for the UI).</summary>
    public int ConsumerCount
    {
        get
        {
            int c = 0;
            for (int i = 0; i < MaxConsumers; i++) if (Volatile.Read(ref U32(OffConsumers + i * 8)) != 0) c++;
            return c;
        }
    }

    public void Dispose()
    {
        _reaper.Dispose();
        Volatile.Write(ref U32(12), 0u);
        Volatile.Write(ref U32(0), 0u);   // let the DLLs see that the host is gone
        lock (_gate) foreach (var e in _events) e?.Dispose();
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _map.Dispose();
    }
}
