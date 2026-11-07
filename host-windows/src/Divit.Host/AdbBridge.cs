// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// USB connection: keep `adb reverse tcp:PORT tcp:PORT` set up on every tablet.
// Unplugging the cable makes adb drop the rule. `adb track-devices` reports device events immediately and the rule is
// re-created on each event; a fallback check also runs every 5 s. Target: ≤ 2 s after plugging in (NFR-STB-2).
// USB key (SPEC §3.2.1): sent once to each new device over adb, only to Divit's receiver. The receiver is guarded by
// android.permission.DUMP, which the adb shell has and ordinary apps do not.

using System.Diagnostics;
using Divit.Core;
using Divit.Core.Server;

namespace Divit.Host;

public sealed class AdbBridge : IDisposable
{
    /// <summary>Android application ID of the tablet app (the USB key receiver lives there).</summary>
    public const string TabletPackage = "io.github.yagizerhan.divit";

    private enum State { Starting, Missing, NotRunning, Unauthorized, NoDevice, Ready }

    private readonly int _port;
    private readonly HostIdentity _identity;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    /// <summary>Devices (serial numbers) that got the key during this run. A device leaves the set when unplugged.</summary>
    private readonly HashSet<string> _keyed = new();
    private Process? _tracker;
    private State _state = State.Starting;
    private string _readyDevices = "";

    public AdbBridge(int port, HostIdentity identity) { _port = port; _identity = identity; AdbPath = FindAdb(); }

    public string? AdbPath { get; }
    /// <summary>Short status shown in the tray (in the current UI language).</summary>
    public string Status => _state switch
    {
        State.Missing => L.T("adb.missing"),
        State.NotRunning => L.T("adb.notRunning"),
        State.Unauthorized => L.T("adb.unauthorized"),
        State.NoDevice => L.T("adb.noDevice"),
        State.Ready => L.T("adb.ready", _readyDevices),
        _ => L.T("adb.starting"),
    };
    public event Action? StatusChanged;

    /// <summary>The tablet could not prove the USB key (the app may have been reinstalled): resend it to all devices.</summary>
    public void RequestKeyPush()
    {
        lock (_keyed) _keyed.Clear();
        _ = CheckAsync();
    }

    public void Start()
    {
        if (AdbPath == null) { SetState(State.Missing); return; }
        new Thread(TrackLoop) { IsBackground = true, Name = "adb-track" }.Start();
        new Thread(() =>
        {
            while (!_cts.IsCancellationRequested) { _ = CheckAsync(); _cts.Token.WaitHandle.WaitOne(5000); }
        }) { IsBackground = true, Name = "adb-poll" }.Start();
    }

    private void TrackLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                _tracker = Process.Start(Psi("track-devices"));
                var r = _tracker!.StandardOutput;
                var buf = new char[1024];
                // Every chunk of output = the device list changed. No need to parse it; triggering a check is enough.
                while (r.Read(buf, 0, buf.Length) > 0) _ = CheckAsync();
            }
            catch (Exception e) { Log.Info($"adb track-devices: {e.Message}"); }
            _cts.Token.WaitHandle.WaitOne(1000); // reconnect if the adb server restarted
        }
    }

    private async Task CheckAsync()
    {
        if (!await _checkGate.WaitAsync(0)) return;
        try
        {
            var (ok, devOut) = await RunAsync("devices");
            if (!ok) { SetState(State.NotRunning); return; }
            var lines = devOut.Split('\n').Skip(1).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            var ready = lines.Where(l => l.EndsWith("\tdevice")).Select(l => l.Split('\t')[0]).ToList();
            if (lines.Any(l => l.EndsWith("\tunauthorized"))) { SetState(State.Unauthorized); return; }
            if (ready.Count == 0) { SetState(State.NoDevice); return; }

            string rule = $"tcp:{_port} tcp:{_port}";
            lock (_keyed) _keyed.IntersectWith(ready);   // a re-plugged device gets the key again
            foreach (var serial in ready)
            {
                bool needKey; lock (_keyed) needKey = !_keyed.Contains(serial);
                if (needKey && await PushKeyAsync(serial)) lock (_keyed) _keyed.Add(serial);
                var (_, list) = await RunAsync($"-s {serial} reverse --list");
                if (list.Contains(rule)) continue;
                var (added, msg) = await RunAsync($"-s {serial} reverse tcp:{_port} tcp:{_port}");
                Log.Info(added ? $"adb reverse set up: {serial}" : $"adb reverse failed: {serial} — {msg.Trim()}");
            }
            _readyDevices = string.Join(", ", ready);
            SetState(State.Ready, force: true);
        }
        finally { _checkGate.Release(); }
    }

    /// <summary>
    /// Sends the USB key only to Divit's UsbKeyReceiver with an explicit broadcast.
    /// -f 0x20 (FLAG_INCLUDE_STOPPED_PACKAGES): reaches the app even if it was never opened.
    /// </summary>
    private async Task<bool> PushKeyAsync(string serial)
    {
        string id = Convert.ToHexString(_identity.HostId).ToLowerInvariant(), key = Convert.ToHexString(_identity.UsbKey).ToLowerInvariant();
        var (ok, msg) = await RunAsync($"-s {serial} shell am broadcast -a {TabletPackage}.action.USB_KEY -n {TabletPackage}/.net.UsbKeyReceiver -f 0x20 --es hostId {id} --es key {key}");
        // am reports success even when no receiver exists; "result=-1" (RESULT_OK) shows the receiver stored the key.
        bool stored = ok && msg.Contains("result=-1");
        Log.Info(stored ? $"USB key sent: {serial}" : $"could not send the USB key: {serial} — is Divit installed on the tablet? ({msg.Trim()})");
        return stored;
    }

    private void SetState(State s, bool force = false)
    {
        if (s == _state && !force) return;
        bool changed = s != _state;
        _state = s;
        if (changed) Log.Info($"adb: {s} {(s == State.Ready ? _readyDevices : "")}");
        StatusChanged?.Invoke();
    }

    private ProcessStartInfo Psi(string args) => new(AdbPath!, args)
    {
        RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
    };

    private async Task<(bool ok, string output)> RunAsync(string args)
    {
        try
        {
            using var p = Process.Start(Psi(args))!;
            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(5000);
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { p.Kill(); return (false, "timeout"); }
            return (p.ExitCode == 0, await so + await se);
        }
        catch (Exception e) { return (false, e.Message); }
    }

    private static string? FindAdb()
    {
        var roots = new[]
        {
            Environment.GetEnvironmentVariable("ANDROID_HOME"),
            Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk"),
        };
        foreach (var r in roots)
        {
            if (string.IsNullOrEmpty(r)) continue;
            string p = Path.Combine(r, "platform-tools", "adb.exe");
            if (File.Exists(p)) return p;
        }
        // Next to the app (portable install / installer) or on PATH
        string local = Path.Combine(AppContext.BaseDirectory, "platform-tools", "adb.exe");
        if (File.Exists(local)) return local;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try { string p = Path.Combine(dir.Trim(), "adb.exe"); if (File.Exists(p)) return p; } catch { }
        }
        return null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _tracker?.Kill(); } catch { }
    }
}
