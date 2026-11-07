// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// WinTab bridge end to end: WinTabSink (host) → shared memory → the real wintab32.dll → WTPacketsGet.
// Skipped if the DLL is not built (native/wintab32/out/x64).

using System.Runtime.InteropServices;
using Divit.Core;
using Divit.Platform.Windows;
using Xunit;

namespace Divit.Tests;

public sealed unsafe class WinTabTests
{
    private const uint WTI_DEVICES = 100, DVC_NPRESSURE = 15, DVC_X = 12, WTI_DEFSYSCTX = 4, WTI_INTERFACE = 1, IFC_NCURSORS = 5;
    private const uint PK_STATUS = 0x2, PK_SERIAL = 0x10, PK_CURSOR = 0x20, PK_BUTTONS = 0x40, PK_X = 0x80, PK_Y = 0x100, PK_NPRESS = 0x400;
    private const int LcSizeW = 212;
    // LOGCONTEXTW field offsets (name 80 bytes, then 4 bytes each)
    private const int OffOptions = 80, OffPktData = 104, OffPktMode = 108, OffMoveMask = 112, OffInExtX = 136, OffInExtY = 140, OffOutExtX = 160, OffOutExtY = 164;

    private static string? DllPath()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++, dir = Path.GetDirectoryName(dir))
        {
            var p = Path.Combine(dir, "native", "wintab32", "out", "x64", "wintab32.dll");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Packet { public uint Status, Serial, Cursor, Buttons; public int X, Y; public uint Pressure; }   // in PK_* order

    // The DLL loads once per process and fixes its event name on first attach: the tests in this class use the same name
    // (tests within a class run sequentially).
    private static readonly string Map = $@"Local\Divit.WinTab.test.{Guid.NewGuid():N}";

    [Fact]
    public void HostRestartKeepsEventDrivenDelivery()
    {
        string? dll = DllPath();
        if (dll == null) return;
        Environment.SetEnvironmentVariable("DIVIT_WINTAB_MAP", Map);
        Environment.SetEnvironmentVariable("DIVIT_WINTAB_ALWAYS", "1");

        // The old host will "crash", i.e. is not disposed (magic is not cleared, the section stays open).
        var crashed = new WinTabSink(Map);
        IntPtr lib = NativeLibrary.Load(dll);
        var info = (delegate* unmanaged[Stdcall]<uint, uint, void*, uint>)NativeLibrary.GetExport(lib, "WTInfoW");
        var open = (delegate* unmanaged[Stdcall]<IntPtr, void*, int, IntPtr>)NativeLibrary.GetExport(lib, "WTOpenW");
        var get = (delegate* unmanaged[Stdcall]<IntPtr, int, void*, int>)NativeLibrary.GetExport(lib, "WTPacketsGet");
        var qset = (delegate* unmanaged[Stdcall]<IntPtr, int, int>)NativeLibrary.GetExport(lib, "WTQueueSizeSet");
        var close = (delegate* unmanaged[Stdcall]<IntPtr, int>)NativeLibrary.GetExport(lib, "WTClose");

        byte* lc = stackalloc byte[LcSizeW];
        info(WTI_DEFSYSCTX, 0, lc);
        *(uint*)(lc + OffOptions) &= ~0x4u;
        uint data = PK_STATUS | PK_SERIAL | PK_CURSOR | PK_BUTTONS | PK_X | PK_Y | PK_NPRESS;
        *(uint*)(lc + OffPktData) = data; *(uint*)(lc + OffMoveMask) = data; *(uint*)(lc + OffPktMode) = 0;
        IntPtr ctx = open(IntPtr.Zero, lc, 1);
        Assert.NotEqual(IntPtr.Zero, ctx);
        qset(ctx, 128);
        WaitFor(() => crashed.ConsumerCount == 1, "first registration", 3000);

        // The new host opens the same section (the DLL still holds it): the live app's registration must be kept.
        using var host = new WinTabSink(Map);
        Assert.Equal(1, host.ConsumerCount);

        // If the registration is wiped anyway (e.g. an older host version) the DLL must re-register on its next wake.
        using (var mm = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(Map))
        using (var acc = mm.CreateViewAccessor(WinTabSink.OffConsumers, WinTabSink.MaxConsumers * 8))
            acc.WriteArray(0, new byte[WinTabSink.MaxConsumers * 8], 0, WinTabSink.MaxConsumers * 8);
        Assert.Equal(0, host.ConsumerCount);
        WaitFor(() => host.ConsumerCount == 1, "DLL re-registered", 1500);

        // Event-driven delivery: the packet must arrive without waiting for the reader's 500 ms timeout.
        var buf = new Packet[16];
        fixed (Packet* p = buf) while (get(ctx, buf.Length, p) > 0) { }
        Thread.Sleep(50);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        host.Apply(new PenOutput(PenTransition.Hover, 0, 0, 0, 0, 0, false, false, 0.5f, 0.5f, 0f));
        int n = 0;
        while (n == 0 && sw.ElapsedMilliseconds < 2000) { fixed (Packet* p = buf) n = get(ctx, buf.Length, p); if (n == 0) Thread.Sleep(2); }
        close(ctx);
        GC.KeepAlive(crashed);
        Assert.True(n > 0, "no packet arrived");
        Assert.True(sw.ElapsedMilliseconds < 200, $"packet arrived after {sw.ElapsedMilliseconds} ms (event not signaled, waiting for the timeout)");
    }

    private static void WaitFor(Func<bool> cond, string what, int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond()) { if (DateTime.UtcNow > until) Assert.Fail($"timeout: {what}"); Thread.Sleep(10); }
    }

    [Fact]
    public void HostSamplesReachWinTabClient()
    {
        string? dll = DllPath();
        if (dll == null) return;   // DLL not built (native/wintab32/build.cmd): cannot verify in this environment

        string map = Map;
        Environment.SetEnvironmentVariable("DIVIT_WINTAB_MAP", map);
        Environment.SetEnvironmentVariable("DIVIT_WINTAB_ALWAYS", "1");
        using var sink = new WinTabSink(map);
        sink.SetTarget(new ScreenRect(0, 0, 2560, 1440));

        IntPtr lib = NativeLibrary.Load(dll);
        var info = (delegate* unmanaged[Stdcall]<uint, uint, void*, uint>)NativeLibrary.GetExport(lib, "WTInfoW");
        var open = (delegate* unmanaged[Stdcall]<IntPtr, void*, int, IntPtr>)NativeLibrary.GetExport(lib, "WTOpenW");
        var get = (delegate* unmanaged[Stdcall]<IntPtr, int, void*, int>)NativeLibrary.GetExport(lib, "WTPacketsGet");
        var qset = (delegate* unmanaged[Stdcall]<IntPtr, int, int>)NativeLibrary.GetExport(lib, "WTQueueSizeSet");
        var close = (delegate* unmanaged[Stdcall]<IntPtr, int>)NativeLibrary.GetExport(lib, "WTClose");

        // Device info
        Assert.True(info(0, 0, null) > 0);
        uint ncsr; info(WTI_INTERFACE, IFC_NCURSORS, &ncsr);
        Assert.Equal(3u, ncsr);
        int* axis = stackalloc int[4];
        info(WTI_DEVICES, DVC_NPRESSURE, axis);
        Assert.Equal(4095, axis[1]);                          // 4096 levels
        info(WTI_DEVICES, DVC_X, axis);
        Assert.Equal(32767, axis[1]);

        // Open a context like Qt/Krita does: system context, output = input, Y flipped.
        byte* lc = stackalloc byte[LcSizeW];
        Assert.Equal((uint)LcSizeW, info(WTI_DEFSYSCTX, 0, lc));
        *(uint*)(lc + OffOptions) &= ~0x4u;                   // no CXO_MESSAGES: read by polling
        uint data = PK_STATUS | PK_SERIAL | PK_CURSOR | PK_BUTTONS | PK_X | PK_Y | PK_NPRESS;
        *(uint*)(lc + OffPktData) = data; *(uint*)(lc + OffMoveMask) = data; *(uint*)(lc + OffPktMode) = 0;
        *(int*)(lc + OffOutExtX) = *(int*)(lc + OffInExtX);
        *(int*)(lc + OffOutExtY) = -*(int*)(lc + OffInExtY);
        IntPtr ctx = open(IntPtr.Zero, lc, 1);
        Assert.NotEqual(IntPtr.Zero, ctx);
        Assert.Equal(1, qset(ctx, 128));

        // Wait until the DLL reader attaches to shared memory (it registers itself as a consumer).
        var until = DateTime.UtcNow.AddSeconds(3);
        while (sink.ConsumerCount == 0 && DateTime.UtcNow < until) Thread.Sleep(20);
        Assert.Equal(1, sink.ConsumerCount);

        sink.Apply(new PenOutput(PenTransition.Hover, 0, 0, 0, 0, 0, false, false, 0.25f, 0.75f, 0f));
        sink.Apply(new PenOutput(PenTransition.Down, 0, 0, 512, 0, 0, false, false, 0.25f, 0.75f, 0.5f));
        sink.Apply(new PenOutput(PenTransition.Move, 0, 0, 1024, 0, 0, true, false, 0.5f, 0.5f, 1.0f));
        sink.Apply(new PenOutput(PenTransition.Hover, 0, 0, 0, 0, 0, false, true, 0.5f, 0.5f, 0f));   // eraser tip

        var got = new List<Packet>();
        var buf = new Packet[16];
        until = DateTime.UtcNow.AddSeconds(2);
        while (got.Count < 4 && DateTime.UtcNow < until)
        {
            fixed (Packet* p = buf) { int n = get(ctx, buf.Length, p); got.AddRange(buf.Take(n)); }
            Thread.Sleep(10);
        }
        close(ctx);

        Assert.Equal(4, got.Count);
        string dump = string.Join(" | ", got.Select(g => $"st={g.Status:X} sn={g.Serial} c={g.Cursor} b={g.Buttons} x={g.X} y={g.Y} p={g.Pressure}"));
        Console.WriteLine(dump);
        int ext = 32768;
        Assert.True(Math.Abs(got[0].X - ext / 4) <= 2, dump);
        Assert.True(Math.Abs(got[0].Y - ext * 3 / 4) <= 2, dump);    // Y down (negative output extent)
        Assert.Equal(0u, got[0].Status & 1);                            // in range (no TPS_PROXIMITY)
        Assert.Equal(0u, got[0].Buttons);
        Assert.Equal(1u, got[0].Cursor);                                // pen
        Assert.Equal(1u, got[1].Buttons & 1);                           // tip pressed
        Assert.True(got[1].Pressure is >= 2046 and <= 2049, dump);      // 0.5 × 4095
        Assert.Equal(4095u, got[2].Pressure);
        Assert.Equal(3u, got[2].Buttons);                               // tip + side button
        Assert.Equal(2u, got[3].Cursor);                                // eraser
        Assert.Equal(0x10u, got[3].Status & 0x10);                      // TPS_INVERT
        Assert.True(got.Zip(got.Skip(1)).All(t => t.Second.Serial == t.First.Serial + 1));
    }

    [Fact]
    public void SurfaceSizeAndCounterWrapReachTheDll()
    {
        string? dll = DllPath();
        if (dll == null) return;
        Environment.SetEnvironmentVariable("DIVIT_WINTAB_MAP", Map);
        Environment.SetEnvironmentVariable("DIVIT_WINTAB_ALWAYS", "1");
        using var sink = new WinTabSink(Map);
        sink.SetTarget(new ScreenRect(0, 0, 1920, 1080));

        IntPtr lib = NativeLibrary.Load(dll);
        var info = (delegate* unmanaged[Stdcall]<uint, uint, void*, uint>)NativeLibrary.GetExport(lib, "WTInfoW");
        var open = (delegate* unmanaged[Stdcall]<IntPtr, void*, int, IntPtr>)NativeLibrary.GetExport(lib, "WTOpenW");
        var get = (delegate* unmanaged[Stdcall]<IntPtr, int, void*, int>)NativeLibrary.GetExport(lib, "WTPacketsGet");
        var qset = (delegate* unmanaged[Stdcall]<IntPtr, int, int>)NativeLibrary.GetExport(lib, "WTQueueSizeSet");
        var close = (delegate* unmanaged[Stdcall]<IntPtr, int>)NativeLibrary.GetExport(lib, "WTClose");

        byte* lc = stackalloc byte[LcSizeW];
        info(WTI_DEFSYSCTX, 0, lc);
        *(uint*)(lc + OffOptions) &= ~0x4u;
        uint data = PK_STATUS | PK_SERIAL | PK_CURSOR | PK_BUTTONS | PK_X | PK_Y | PK_NPRESS;
        *(uint*)(lc + OffPktData) = data; *(uint*)(lc + OffMoveMask) = data; *(uint*)(lc + OffPktMode) = 0;
        *(int*)(lc + OffOutExtX) = *(int*)(lc + OffInExtX);   // output in tablet units
        *(int*)(lc + OffOutExtY) = -*(int*)(lc + OffInExtY);
        IntPtr ctx = open(IntPtr.Zero, lc, 1);
        Assert.NotEqual(IntPtr.Zero, ctx);
        qset(ctx, 128);
        WaitFor(() => sink.ConsumerCount == 1, "registration", 3000);

        // Tablet resolution follows the surface the tablet reported (SURFACE_INFO): 32768 units over 30 cm.
        int* axis = stackalloc int[4];
        sink.SetSurfaceMm(300f, 200f);
        info(WTI_DEVICES, DVC_X, axis);
        Assert.Equal((int)(32768 / 30.0 * 65536), axis[3]);
        sink.SetSurfaceMm(float.NaN, 0f);                    // nonsense is ignored: the last good value stays
        info(WTI_DEVICES, DVC_X, axis);
        Assert.Equal((int)(32768 / 30.0 * 65536), axis[3]);

        // The packet counter crosses 2^32 (months of continuous use): no negative index, nothing lost.
        typeof(WinTabSink).GetField("_written", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(sink, uint.MaxValue - 2);
        using (var mm = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(Map))
        using (var acc = mm.CreateViewAccessor(WinTabSink.OffWriteIndex, 4))
            acc.Write(0, uint.MaxValue - 2);
        var buf = new Packet[128];
        Thread.Sleep(700);                                   // the DLL notices the jump (skips ahead) on its next wake
        fixed (Packet* p = buf) while (get(ctx, buf.Length, p) > 0) { }

        for (int i = 0; i < 5; i++)
            sink.Apply(new PenOutput(PenTransition.Hover, 0, 0, 0, 0, 0, false, false, 0.1f * (i + 1), 0.5f, 0f));
        var got = new List<Packet>();
        var until = DateTime.UtcNow.AddSeconds(2);
        while (got.Count < 5 && DateTime.UtcNow < until)
        {
            fixed (Packet* p = buf) { int n = get(ctx, buf.Length, p); got.AddRange(buf.Take(n)); }
            Thread.Sleep(10);
        }
        close(ctx);
        Assert.Equal(5, got.Count);
        for (int i = 0; i < 5; i++) Assert.True(Math.Abs(got[i].X - 32767 * 0.1 * (i + 1)) <= 3, $"packet {i}: x={got[i].X}");
    }
}
