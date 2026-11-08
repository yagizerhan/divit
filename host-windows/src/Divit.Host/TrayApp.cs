// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// System tray (HS-01): connection status, latency, monitor choice, Wi-Fi + pairing code.
// Detailed settings live in SettingsForm (HS-02); the tray is for quick actions.

using System.Drawing.Drawing2D;
using Divit.Core;
using Divit.Core.Server;
using Divit.Platform.Windows;
using Divit.Protocol;
using Microsoft.Win32;

namespace Divit.Host;

public sealed class TrayApp : ApplicationContext, IHostControl
{
    public static readonly Icon AppIcon = MakeIcon(Color.FromArgb(61, 111, 216));
    private SettingsForm? _form;
    private readonly HostSettings _settings = HostSettings.Load();
    private readonly PairingStore _pairing = new();
    private readonly WindowsInkSink _sink = new();
    private readonly WinTabSink? _wintab = TryWinTab();
    public WinTabSink? WinTab => _wintab;
    private readonly PenServer _server;
    private readonly InputSender _keys = new();
    private readonly AdbBridge _adb;
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _uiTimer;
    private readonly Icon _iconIdle = MakeIcon(Color.FromArgb(140, 140, 140)), _iconOk = MakeIcon(Color.FromArgb(46, 160, 67)), _iconWait = MakeIcon(Color.FromArgb(214, 158, 46));
    private readonly SynchronizationContext _ui;
    private MonitorInfo _monitor;
    private Font? _menuBold;

    public TrayApp(bool showWindow)
    {
        L.Use(_settings.Language);
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _monitor = Monitors.Find(_settings.MonitorDevice);
        IPenSink output = _wintab != null ? new CompositeSink(_sink, _wintab) : _sink;
        _server = new PenServer(output, _pairing, PipelineFactory(), HostIdentity.LoadOrCreate(), _settings.PairingCode) { Actions = _keys };
        _server.PairingCodeChanged += code => _ui.Post(_ => { _settings.PairingCode = code; _settings.Save(); }, null);
        if (_settings.PairingCode != _server.PairingCode) { _settings.PairingCode = _server.PairingCode; _settings.Save(); }   // first run: keep the new code
        _server.SurfaceChanged += (w, h) =>
        {
            _wintab?.SetSurfaceMm(w, h);
            _ui.Post(_ => OnSurface(w / h), null);
        };
        _wintab?.SetTarget(_monitor.Bounds);
        _server.ApplySettings(PipelineFactory(), _monitor.DeviceName);
        _server.StateChanged += () => { _wintab?.SetConnected(_server.Active?.Pipeline != null); _ui.Post(_ => Refresh(), null); };
        _adb = new AdbBridge(_settings.Port, _server.Identity);
        _adb.StatusChanged += () => _ui.Post(_ => Refresh(), null);
        _server.UsbKeyRejected += _adb.RequestKeyPush;

        _tray = new NotifyIcon { Icon = _iconIdle, Visible = true, Text = "Divit" };
        Theme.Use(Theme.Parse(_settings.Theme));
        _tray.ContextMenuStrip = new ContextMenuStrip { Renderer = Theme.MenuRenderer() };
        // WinForms cancels opening a menu that has no items *before* this handler runs; the menu is filled here, so
        // undo that (otherwise the first right click shows nothing and only the second one opens the menu).
        _tray.ContextMenuStrip.Opening += (_, e) => { BuildMenu(); e.Cancel = false; };
        BuildMenu();
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowSettings(); };

        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;

        try { _server.Start(_settings.Port, _settings.WifiEnabled); }
        catch (Exception e)
        {
            Log.Info($"could not start the server: {e.Message}");
            MessageBox.Show(L.T("port.error", _settings.Port, e.Message), "Divit", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        _adb.Start();

        _uiTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _uiTimer.Tick += (_, _) => Refresh();
        _uiTimer.Start();
        Refresh();
        Log.Info($"Divit started · monitor {_monitor} · port {_settings.Port} · Wi-Fi {(_settings.WifiEnabled ? "on" : "off")} · language {L.Current}");

        // First run: Windows 11 hides new tray icons, so the window opens by itself.
        if (_settings.IsFirstRun) _settings.Save();
        if (showWindow || _settings.IsFirstRun) ShowSettings();
    }

    /// <summary>Show / bring the settings window to front (starting Divit a second time triggers this too).</summary>
    public void ShowSettings()
    {
        _form ??= new SettingsForm(this);
        if (!_form.Visible) _form.Show();
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        _form.Activate();
    }

    // ---- IHostControl ----
    public HostSettings Settings => _settings;
    public MonitorInfo Monitor => _monitor;
    public PenServer Server => _server;
    public AdbBridge Adb => _adb;
    public PairingStore Pairing => _pairing;
    IEnumerable<string> IHostControl.LocalAddresses() => LocalAddresses();

    public void SetTheme(ThemeMode mode)
    {
        _settings.Theme = mode.ToString();
        _settings.Save();
        Theme.Use(mode);
        _tray.ContextMenuStrip!.Renderer = Theme.MenuRenderer();
        RebuildWindow();
        Log.Info($"appearance: {mode}");
    }

    public void SetLanguage(string preference)
    {
        _settings.Language = preference;
        _settings.Save();
        L.Use(preference);
        RebuildWindow();
        Refresh();
        Log.Info($"language: {preference} → {L.Current}");
    }

    /// <summary>The WinForms color mode and texts apply only to newly created windows: reopen it in the same place.</summary>
    private void RebuildWindow()
    {
        var old = _form; var loc = old?.Location;
        _form = null;
        old?.Hide(); old?.Dispose();
        ShowSettings();
        if (loc is { } l) _form!.Location = l;
    }

    public void SetMonitor(MonitorInfo m) { _monitor = m; _settings.MonitorDevice = m.DeviceName; ApplyAndSave(); }

    public void Apply(bool save)
    {
        if (save) _settings.Save();
        _wintab?.SetTarget(_monitor.Bounds);
        _server.ApplySettings(PipelineFactory(), _monitor.DeviceName);
        Refresh();
    }

    private string MonitorLabel => _monitor.Label(L.T("monitor.primary"));

    public (string Title, string Detail, Color Color) Status()
    {
        var s = _server.Active;
        if (s?.Info is { } info && s.Pipeline is { } p)
        {
            string lat = p.Latency.Count > 0
                ? L.T("status.latency", p.Latency.Percentile(50) / 1000.0, p.Latency.Percentile(95) / 1000.0)
                : "";
            return (L.T("status.connected", info.Model, info.Transport == TransportKind.Usb ? "USB" : "Wi-Fi"),
                    L.T("status.samples", lat, p.Samples, MonitorLabel), Color.FromArgb(46, 160, 67));
        }
        if (_server.Pending?.Info is { } pi)
            return (L.T("status.pairing", pi.Model), L.T("status.pairingDetail", _server.PairingCode), Color.FromArgb(214, 158, 46));
        return (L.T("status.waiting"),
                _settings.WifiEnabled ? L.T("status.waitingWifi") : L.T("status.waitingUsb"),
                Color.FromArgb(140, 140, 140));
    }

    /// <summary>
    /// A snapshot of the current settings, taken on the UI thread. The session threads call the returned function
    /// (e.g. when SURFACE_INFO arrives), so they never read HostSettings while the UI is changing it.
    /// </summary>
    private Func<float, PipelineSettings> PipelineFactory()
    {
        var bounds = _monitor.Bounds;
        var area = _settings.ActiveArea;
        bool keep = _settings.KeepAspect;
        var curve = _settings.Curve;
        var buttons = _settings.Buttons;
        return aspect => new(new AreaMapping(bounds, area, aspect, keep), curve, buttons);
    }

    private void ApplyAndSave() => Apply(save: true);

    /// <summary>Remember the tablet's real aspect ratio so the area preview matches it (also before it connects).</summary>
    private void OnSurface(float aspect)
    {
        if (!float.IsFinite(aspect) || aspect is < 0.5f or > 3f || Math.Abs(aspect - _settings.SurfaceAspect) < 0.001f) return;
        _settings.SurfaceAspect = aspect;
        _settings.Save();
        _form?.SurfaceChanged();
    }

    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        _sink.RefreshDisplayLayout();
        _monitor = Monitors.Find(_settings.MonitorDevice);
        _wintab?.SetTarget(_monitor.Bounds);
        Log.Info($"display layout changed → {_monitor}");
        _server.ApplySettings(PipelineFactory(), _monitor.DeviceName);
    }

    private string StatusLine(out Icon icon)
    {
        var s = _server.Active;
        if (s?.Info is { } info && s.Pipeline is { } p)
        {
            icon = _iconOk;
            string lat = p.Latency.Count > 0 ? L.T("tray.latency", p.Latency.Percentile(50) / 1000.0, p.Latency.Percentile(95) / 1000.0) : "";
            return L.T("tray.connected", info.Model, info.Transport == TransportKind.Usb ? "USB" : "Wi-Fi", lat);
        }
        if (_server.Pending != null) { icon = _iconWait; return L.T("tray.pairing", _server.PairingCode); }
        icon = _iconIdle;
        return L.T("status.waiting");
    }

    private void Refresh()
    {
        string line = StatusLine(out var icon);
        _tray.Icon = icon;
        string text = $"Divit — {line}";
        _tray.Text = text.Length > 127 ? text[..127] : text;
    }

    private void BuildMenu()
    {
        var m = _tray.ContextMenuStrip!;
        // The menu is rebuilt on every open: dispose the old items (Clear() alone leaks their handles).
        var old = m.Items.Cast<ToolStripItem>().ToArray();
        m.Items.Clear();
        foreach (var item in old) item.Dispose();
        m.ForeColor = Theme.Text;
        _menuBold ??= new Font(m.Font, FontStyle.Bold);
        var open = new ToolStripMenuItem(L.T("menu.settings")) { Font = _menuBold };
        open.Click += (_, _) => ShowSettings();
        m.Items.Add(open);
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem(StatusLine(out _)) { Enabled = false });
        m.Items.Add(new ToolStripMenuItem(L.T("menu.usb", _adb.Status)) { Enabled = false });
        if (_server.Active?.Pipeline is { } p)
            m.Items.Add(new ToolStripMenuItem(L.T("menu.stats", p.Samples, p.SeqGaps, p.InjectFailures)) { Enabled = false });
        m.Items.Add(new ToolStripSeparator());

        var mon = new ToolStripMenuItem(L.T("menu.monitor"));
        foreach (var mi in Monitors.List())
        {
            var item = new ToolStripMenuItem(mi.Label(L.T("monitor.primary"))) { Checked = mi.DeviceName == _monitor.DeviceName };
            item.Click += (_, _) => { _monitor = mi; _settings.MonitorDevice = mi.DeviceName; ApplyAndSave(); };
            mon.DropDownItems.Add(item);
        }
        m.Items.Add(mon);

        var aspect = new ToolStripMenuItem(L.T("menu.keepAspect")) { Checked = _settings.KeepAspect };
        aspect.Click += (_, _) => { _settings.KeepAspect = !_settings.KeepAspect; ApplyAndSave(); };
        m.Items.Add(aspect);

        var pressure = new ToolStripMenuItem(L.T("menu.pressure"));
        foreach (var (key, max) in new[] { ("menu.pressure.light", 0.55f), ("menu.pressure.medium", 0.70f), ("menu.pressure.firm", 0.85f), ("menu.pressure.full", 1.0f) })
        {
            var item = new ToolStripMenuItem(L.T(key)) { Checked = Math.Abs(_settings.PressureMax - max) < 0.001f };
            item.Click += (_, _) => { _settings.PressureMax = max; ApplyAndSave(); };
            pressure.DropDownItems.Add(item);
        }
        m.Items.Add(pressure);
        m.Items.Add(new ToolStripSeparator());

        var wifi = new ToolStripMenuItem(L.T("menu.wifi")) { Checked = _settings.WifiEnabled };
        wifi.Click += (_, _) => SetWifi(!_settings.WifiEnabled);
        m.Items.Add(wifi);
        if (_settings.WifiEnabled)
        {
            foreach (var ip in LocalAddresses())
                m.Items.Add(new ToolStripMenuItem(L.T("menu.pcAddress", ip)) { Enabled = false });
            string code = _server.PairingLocked ? $"{_server.PairingCode} · {L.T("menu.locked")}" : _server.PairingCode;
            m.Items.Add(new ToolStripMenuItem(L.T("menu.pairingCode", code)) { Enabled = false });
            var newCode = new ToolStripMenuItem(L.T("menu.newCode"));
            newCode.Click += (_, _) => _server.NewPairingCode();
            m.Items.Add(newCode);
        }
        if (_pairing.Count > 0)
        {
            var forget = new ToolStripMenuItem(L.T("menu.forget", _pairing.Count));
            forget.Click += (_, _) => _server.ForgetPairings();
            m.Items.Add(forget);
        }
        m.Items.Add(new ToolStripSeparator());

        var resetLat = new ToolStripMenuItem(L.T("menu.resetLatency")) { Enabled = _server.Active?.Pipeline != null };
        resetLat.Click += (_, _) => _server.Active?.Pipeline?.Latency.Reset();
        m.Items.Add(resetLat);
        var log = new ToolStripMenuItem(L.T("menu.openLog"));
        log.Click += (_, _) => { if (File.Exists(Log.FilePath)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Log.FilePath) { UseShellExecute = true }); };
        m.Items.Add(log);
        var exit = new ToolStripMenuItem(L.T("menu.exit"));
        exit.Click += (_, _) => ExitThread();
        m.Items.Add(exit);
    }

    /// <summary>Turns the Wi-Fi listener on/off. Shows a warning when turning on; false if the user cancels.</summary>
    public bool SetWifi(bool on)
    {
        if (on == _settings.WifiEnabled) return true;
        if (on && MessageBox.Show(L.T("wifi.warn"), L.T("wifi.warn.title"), MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
            return false;
        _settings.WifiEnabled = on;
        _settings.Save();
        try { _server.Start(_settings.Port, _settings.WifiEnabled); }
        catch (Exception e) { MessageBox.Show(e.Message, "Divit", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        Refresh();
        return true;
    }

    /// <summary>Interfaces with a gateway (virtual adapters such as vEthernet are not on the tablet's network).</summary>
    private static IEnumerable<string> LocalAddresses() =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .Where(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .Select(a => a.Address.ToString());

    /// <summary>WinTab bridge. If it cannot be opened, Divit works with Windows Ink only.</summary>
    private static WinTabSink? TryWinTab()
    {
        try { return new WinTabSink(); }
        catch (Exception e) { Log.Info($"could not open the WinTab bridge: {e.Message}"); return null; }
    }

    private static Icon MakeIcon(Color c)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            // Pen tip: a slanted bar + a dot
            using var pen = new Pen(c, 6) { StartCap = LineCap.Round, EndCap = LineCap.Triangle };
            g.DrawLine(pen, 26, 4, 9, 21);
            using var b = new SolidBrush(c);
            g.FillEllipse(b, 3, 23, 7, 7);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void ExitThreadCore()
    {
        Log.Info("exiting");
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        _uiTimer.Stop();
        _tray.Visible = false;
        _form?.Dispose();
        _server.Dispose();
        _keys.ReleaseHeld();   // a shortcut held by the side button must not stay down after exit
        _adb.Dispose();
        _sink.Dispose();
        _wintab?.Dispose();
        _menuBold?.Dispose();
        base.ExitThreadCore();
    }
}
