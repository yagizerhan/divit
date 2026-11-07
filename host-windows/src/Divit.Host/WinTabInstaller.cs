// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// WinTab setup (decision K-06): wintab32.dll → System32 (64-bit) and SysWOW64 (32-bit). Needs administrator rights;
// Divit runs itself as a separate process through UAC with "--wintab install|uninstall".
// Rule: another driver's (Wacom etc.) wintab32.dll is never overwritten or deleted.

using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using Divit.Core;

namespace Divit.Host;

public enum WinTabState { NotInstalled, Installed, Outdated, Foreign, Missing /* no DLL in the package */ }

public static class WinTabInstaller
{
    private static readonly (string Arch, string Dest)[] Targets =
    [
        ("x64", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wintab32.dll")),
        ("x86", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), "wintab32.dll")),
    ];

    private static string Source(string arch) => Path.Combine(AppContext.BaseDirectory, "wintab", arch, "wintab32.dll");

    /// <summary>Is it Divit's DLL? Looks for the marker export name (other drivers do not have it).</summary>
    private static bool IsOurs(string path)
    {
        try { return Encoding.ASCII.GetString(File.ReadAllBytes(path)).Contains("DivitWinTabVersion"); }
        catch { return false; }
    }

    public static WinTabState State
    {
        get
        {
            if (!File.Exists(Source("x64"))) return WinTabState.Missing;
            var states = Targets.Select(t =>
            {
                if (!File.Exists(t.Dest)) return WinTabState.NotInstalled;
                if (!IsOurs(t.Dest)) return WinTabState.Foreign;
                return File.ReadAllBytes(t.Dest).AsSpan().SequenceEqual(File.ReadAllBytes(Source(t.Arch))) ? WinTabState.Installed : WinTabState.Outdated;
            }).ToList();
            if (states.Contains(WinTabState.Foreign)) return WinTabState.Foreign;
            if (states.All(s => s == WinTabState.Installed)) return WinTabState.Installed;
            if (states.All(s => s == WinTabState.NotInstalled)) return WinTabState.NotInstalled;
            return WinTabState.Outdated;
        }
    }

    /// <summary>Runs the UAC-elevated helper. Returns an explanation if the user declines or something fails.</summary>
    public static string? RunElevated(bool install)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--wintab {(install ? "install" : "uninstall")}")
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            })!;
            p.WaitForExit();
            return p.ExitCode switch
            {
                0 => null,
                2 => L.T("wintab.err.foreign"),
                3 => L.T("wintab.err.inUse"),
                _ => L.T("wintab.err.other", p.ExitCode, Log.FilePath),
            };
        }
        catch (System.ComponentModel.Win32Exception) { return L.T("wintab.err.denied"); }
    }

    /// <summary>Runs in the elevated process (Program.Main). Exit code: 0 done, 2 foreign DLL, 3 file in use, 1 other.</summary>
    public static int ElevatedMain(string verb)
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return 1;
        try
        {
            foreach (var (arch, dest) in Targets)
            {
                if (File.Exists(dest) && !IsOurs(dest)) { Log.Info($"WinTab: {dest} belongs to another driver, left untouched"); return 2; }
                if (verb == "install")
                {
                    File.Copy(Source(arch), dest, overwrite: true);
                    Log.Info($"WinTab installed: {dest}");
                }
                else if (File.Exists(dest))
                {
                    File.Delete(dest);
                    Log.Info($"WinTab removed: {dest}");
                }
            }
            return 0;
        }
        catch (IOException e) { Log.Info($"WinTab {verb}: {e.Message}"); return 3; }
        catch (Exception e) { Log.Info($"WinTab {verb}: {e}"); return 1; }
        finally { Thread.Sleep(300); }   // let the log queue flush
    }
}
