// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// Monitor list. The process must be Per-Monitor DPI Aware v2 (host manifest), otherwise pixel values are wrong on
// scaled monitors (architecture §4.2).

using Divit.Core;
using static Divit.Platform.Windows.Native;

namespace Divit.Platform.Windows;

public sealed record MonitorInfo(string DeviceName, ScreenRect Bounds, bool Primary)
{
    /// <summary>Display label; <paramref name="primary"/> is the localized "(primary)" suffix.</summary>
    public string Label(string primary) => $"{DeviceName.Replace(@"\\.\", "")} {Bounds.Width}×{Bounds.Height}{(Primary ? " " + primary : "")}";

    public override string ToString() => Label("(primary)");
}

public static class Monitors
{
    private const uint MONITORINFOF_PRIMARY = 1;

    public static IReadOnlyList<MonitorInfo> List()
    {
        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var mi = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(h, ref mi))
            {
                var r = mi.rcMonitor;
                list.Add(new MonitorInfo(mi.szDevice, new ScreenRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), (mi.dwFlags & MONITORINFOF_PRIMARY) != 0));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>The monitor with this name; the primary one if not found.</summary>
    public static MonitorInfo Find(string? deviceName)
    {
        var all = List();
        return all.FirstOrDefault(m => m.DeviceName == deviceName) ?? all.First(m => m.Primary);
    }
}
