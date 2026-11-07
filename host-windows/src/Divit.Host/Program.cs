// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// Divit host entry point: single instance, low-latency GC, tray app + settings window.
//   Divit.exe          → runs in the tray and opens the settings window
//   Divit.exe --tray   → tray only (start with Windows)
// Starting it a second time brings the running instance's window to the front.

using System.Runtime;
using Divit.Core;

namespace Divit.Host;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // The UAC-elevated WinTab setup helper (WinTabInstaller.RunElevated): no UI, one job.
        if (args.Length == 2 && args[0] == "--wintab") Environment.Exit(WinTabInstaller.ElevatedMain(args[1]));

        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Divit.Host.Show");
        using var mutex = new Mutex(true, @"Local\Divit.Host", out bool first);
        if (!first) { show.Set(); return; }
        bool trayOnly = args.Contains("--tray");

        // Hot path: avoid long GC pauses (architecture §3.1).
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        Application.ThreadException += (_, e) => Log.Info($"UI error: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Info($"unexpected error: {e.ExceptionObject}");

        ApplicationConfiguration.Initialize();
        WindowsFormsSynchronizationContext.AutoInstall = true;
        var ui = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(ui);
        var app = new TrayApp(showWindow: !trayOnly);
        new Thread(() => { while (show.WaitOne()) ui.Post(_ => app.ShowSettings(), null); })
            { IsBackground = true, Name = "show-signal" }.Start();
        Application.Run(app);
    }
}
