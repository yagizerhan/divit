// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// Settings window (HS-02): status, screen/area, pressure curve editor, side button, WinTab, Wi-Fi + pairing,
// start with Windows, appearance and language. WinForms (same library as the tray; no extra dependency).
// Closing hides it; Divit keeps running in the tray.

using System.Drawing.Drawing2D;
using Divit.Core;
using Divit.Core.Server;
using Divit.Platform.Windows;
using Microsoft.Win32;

namespace Divit.Host;

/// <summary>Everything the window needs from the host (implemented by TrayApp).</summary>
public interface IHostControl
{
    HostSettings Settings { get; }
    MonitorInfo Monitor { get; }
    PenServer Server { get; }
    AdbBridge Adb { get; }
    PairingStore Pairing { get; }
    (string Title, string Detail, Color Color) Status();
    void SetMonitor(MonitorInfo m);
    /// <summary>Called after Settings was changed; save=false applies while dragging (without writing to disk).</summary>
    void Apply(bool save);
    bool SetWifi(bool on);
    /// <summary>Appearance changed: rebuild the window with the new theme.</summary>
    void SetTheme(ThemeMode mode);
    /// <summary>Language changed ("System", "en", "tr"): rebuild the window in the new language.</summary>
    void SetLanguage(string preference);
    IEnumerable<string> LocalAddresses();
}

public sealed class SettingsForm : Form
{
    private static readonly string[] LanguageValues = ["System", "en", "tr"];

    private readonly IHostControl _h;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };
    private readonly Label _statusTitle = new(), _statusDetail = new(), _usbStatus = new();
    private readonly Panel _statusDot = new();
    private readonly ComboBox _monitor = new(), _theme = new(), _language = new(), _barrel = new();
    private readonly TextBox _shortcut = new();
    private readonly Label _wtStatus = new();
    private WinTabState _wtState;
    private readonly Button _wtButton = new();
    private readonly CheckBox _keepAspect = new(), _wifi = new(), _autostart = new();
    private readonly AreaPreview _area;
    private readonly CurveEditor _curve;
    private readonly Label _curveValues = new(), _addresses = new(), _paired = new(), _codeLbl = new();
    /// <summary>The pairing code; the user can type their own 6 digits (Enter or leaving the box saves it).</summary>
    private readonly TextBox _code = new();
    private readonly Button _newCode = new(), _forget = new();
    private bool _loading;
    private int _slowTick;

    public SettingsForm(IHostControl host)
    {
        _h = host;
        Text = "Divit";
        Icon = TrayApp.AppIcon;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        HandleCreated += (_, _) => Theme.ApplyTitleBar(this);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1000, 920);
        MinimumSize = new Size(920, 860);
        Padding = new Padding(14);

        _area = new AreaPreview(host);
        _curve = new CurveEditor(host);

        // Everything uses table/flow layouts so nothing is clipped, whatever the DPI or window size.
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = Theme.Bg };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var status = Card(StatusBody(), autoHeight: true);
        grid.Controls.Add(status, 0, 0);
        grid.SetColumnSpan(status, 2);

        var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = Theme.Bg, Margin = Padding.Empty };
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.Controls.Add(Card(ScreenBody(), L.T("card.screen")), 0, 0);
        left.Controls.Add(Card(ConnectionBody(), L.T("card.connection"), autoHeight: true), 0, 1);
        grid.Controls.Add(left, 0, 1);
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = Theme.Bg, Margin = Padding.Empty };
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.Controls.Add(Card(PressureBody(), L.T("card.pressure")), 0, 0);
        right.Controls.Add(Card(ButtonBody(), L.T("card.button"), autoHeight: true), 0, 1);
        right.Controls.Add(Card(WinTabBody(), L.T("card.wintab"), autoHeight: true), 0, 2);
        grid.Controls.Add(right, 1, 1);
        Controls.Add(grid);

        LoadValues();
        _timer.Tick += (_, _) => Tick();
        VisibleChanged += (_, _) => { if (Visible) { LoadValues(); _timer.Start(); } else _timer.Stop(); };
        Shown += (_, _) => ActiveControl = null;   // no control should look selected on open
        // Close = hide; Divit keeps running in the tray.
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }

    // ---- Layout helpers ----

    /// <summary>Vertical stack: each row is (control, size type). Percent rows share the remaining space.</summary>
    private static TableLayoutPanel Stack(params (Control c, SizeType t)[] rows)
    {
        var t = new TableLayoutPanel { ColumnCount = 1, RowCount = rows.Length, BackColor = Color.Transparent, Margin = Padding.Empty, Padding = Padding.Empty };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < rows.Length; i++)
        {
            t.RowStyles.Add(rows[i].t == SizeType.Percent ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            var c = rows[i].c;
            if (rows[i].t == SizeType.Percent) c.Dock = DockStyle.Fill; else c.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            t.Controls.Add(c, 0, i);
        }
        return t;
    }

    private static FlowLayoutPanel Row(params Control[] items)
    {
        var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 2) };
        f.Controls.AddRange(items);
        return f;
    }

    private static Label Text_(string text, Font? font = null, Color? color = null) =>
        new() { Text = text, AutoSize = true, Font = font ?? Theme.Body, ForeColor = color ?? Theme.Muted, Margin = new Padding(0, 3, 0, 3) };

    /// <summary>Multi-line label that wraps to its container's width.</summary>
    private static Label Wrapping(string text)
    {
        var l = new Label { Text = text, AutoSize = true, Font = Theme.Small, ForeColor = Theme.Muted, Margin = new Padding(0, 0, 0, 6) };
        l.ParentChanged += (_, _) =>
        {
            if (l.Parent == null) return;
            void Fit() => l.MaximumSize = new Size(Math.Max(100, l.Parent!.ClientSize.Width - 4), 0);
            l.Parent.Resize += (_, _) => Fit();
            Fit();
        };
        return l;
    }

    private static Control Card(Control body, string? title = null, bool autoHeight = false)
    {
        var card = new RoundedPanel { Dock = DockStyle.Fill, Margin = new Padding(6), Padding = new Padding(18, 12, 18, 14), BackColor = Theme.Card };
        if (autoHeight) { card.AutoSize = true; card.AutoSizeMode = AutoSizeMode.GrowAndShrink; }
        var rows = new List<(Control, SizeType)>();
        if (title != null) rows.Add((new Label { Text = title, Font = Theme.H2, ForeColor = Theme.Text, AutoSize = true, Margin = new Padding(0, 0, 0, 8) }, SizeType.AutoSize));
        rows.Add((body, autoHeight ? SizeType.AutoSize : SizeType.Percent));
        var s = Stack(rows.ToArray());
        if (autoHeight) { s.AutoSize = true; s.Dock = DockStyle.Top; } else s.Dock = DockStyle.Fill;
        card.Controls.Add(s);
        return card;
    }

    private static ComboBox Dropdown(ComboBox c, int width)
    {
        c.DropDownStyle = ComboBoxStyle.DropDownList;
        c.FlatStyle = FlatStyle.Flat;
        c.BackColor = Theme.Button;
        c.ForeColor = Theme.Text;
        c.Width = width;
        return c;
    }

    // ---- Card contents ----

    private Control StatusBody()
    {
        _statusDot.Size = new Size(14, 14);
        _statusDot.Margin = new Padding(0, 10, 10, 0);
        _statusDot.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var b = new SolidBrush(_statusDot.ForeColor);
            e.Graphics.FillEllipse(b, 0, 0, 13, 13);
        };
        _statusTitle.AutoSize = true; _statusTitle.Font = Theme.H1; _statusTitle.ForeColor = Theme.Text;
        _statusDetail.AutoSize = true; _statusDetail.ForeColor = Theme.Muted;
        _usbStatus.AutoSize = true; _usbStatus.ForeColor = Theme.Muted; _usbStatus.Font = Theme.Small;
        var s = Stack((Row(_statusDot, _statusTitle), SizeType.AutoSize), (_statusDetail, SizeType.AutoSize), (_usbStatus, SizeType.AutoSize));
        s.AutoSize = true;
        return s;
    }

    private Control ScreenBody()
    {
        Dropdown(_monitor, 360);
        _monitor.FormattingEnabled = true;
        _monitor.Format += (_, e) => { if (e.ListItem is MonitorInfo m) e.Value = m.Label(L.T("monitor.primary")); };
        _monitor.SelectedIndexChanged += (_, _) =>
        {
            if (_loading || _monitor.SelectedItem is not MonitorInfo m) return;
            _h.SetMonitor(m);
            _area.Invalidate();
        };
        _keepAspect.AutoSize = true;
        _keepAspect.Text = L.T("screen.keepAspect");
        _keepAspect.CheckedChanged += (_, _) => { if (_loading) return; _h.Settings.KeepAspect = _keepAspect.Checked; _h.Apply(true); _area.Invalidate(); };
        _area.MinimumSize = new Size(200, 120);
        _area.Changed += save => _h.Apply(save);
        var reset = FlatButton(L.T("screen.useAll"));
        reset.AutoSize = true;
        reset.Click += (_, _) => _area.Reset();
        return Stack(
            (Text_(L.T("screen.mapsTo")), SizeType.AutoSize),
            (_monitor, SizeType.AutoSize),
            (Row(_keepAspect, reset), SizeType.AutoSize),
            (_area, SizeType.Percent));
    }

    private Control PressureBody()
    {
        _curve.Changed += save => { _h.Apply(save); UpdateCurveLabel(); };
        _curve.MinimumSize = new Size(240, 200);
        _curveValues.AutoSize = true; _curveValues.ForeColor = Theme.Muted;

        var presets = new List<Control>();
        foreach (var (key, min, max, g) in new[] { ("pressure.soft", 0f, 0.6f, 0.7f), ("pressure.linear", 0f, 0.7f, 1f), ("pressure.hard", 0.05f, 0.85f, 1.4f) })
        {
            var b = FlatButton(L.T(key));
            b.AutoSize = true;
            b.Padding = new Padding(10, 2, 10, 2);
            b.Click += (_, _) =>
            {
                _h.Settings.PressureMin = min; _h.Settings.PressureMax = max; _h.Settings.PressureGamma = g;
                _h.Apply(true); _curve.Invalidate(); UpdateCurveLabel();
            };
            presets.Add(b);
        }
        return Stack(
            (Wrapping(L.T("pressure.help")), SizeType.AutoSize),
            (_curve, SizeType.Percent),
            (_curveValues, SizeType.AutoSize),
            (Row(presets.ToArray()), SizeType.AutoSize));
    }

    private Control ButtonBody()
    {
        Dropdown(_barrel, 300);
        // Same order as BarrelAction: PenButton, RightClick, Eraser, Shortcut, Off
        _barrel.Items.AddRange([L.T("button.pen"), L.T("button.right"), L.T("button.eraser"), L.T("button.shortcut"), L.T("button.off")]);
        _barrel.SelectedIndexChanged += (_, _) =>
        {
            _shortcut.Visible = _barrel.SelectedIndex == (int)BarrelAction.Shortcut;
            if (_loading) return;
            _h.Settings.BarrelAction = ((BarrelAction)_barrel.SelectedIndex).ToString();
            _h.Apply(true);
            if (_shortcut.Visible && _h.Settings.ShortcutKeys == 0) _shortcut.Focus();
        };

        _shortcut.ReadOnly = true;
        _shortcut.Width = 200;
        _shortcut.BackColor = Theme.Button;
        _shortcut.ForeColor = Theme.Text;
        _shortcut.BorderStyle = BorderStyle.FixedSingle;
        _shortcut.PlaceholderText = L.T("button.shortcutHint");
        _shortcut.Margin = new Padding(10, 3, 0, 0);
        _shortcut.KeyDown += (_, e) =>
        {
            e.SuppressKeyPress = true; e.Handled = true;
            var key = e.KeyCode;
            if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return;   // modifier only
            _h.Settings.ShortcutKeys = (int)e.KeyData;
            _shortcut.Text = KeyText(_h.Settings.ShortcutKeys);
            _h.Apply(true);
        };
        return Stack(
            (Row(_barrel, _shortcut), SizeType.AutoSize),
            (Wrapping(L.T("button.help")), SizeType.AutoSize));
    }

    private static string KeyText(int keys) => keys == 0 ? "" : new KeysConverter().ConvertToString((Keys)keys) ?? "";

    private Control WinTabBody()
    {
        _wtStatus.AutoSize = true; _wtStatus.ForeColor = Theme.Text; _wtStatus.Margin = new Padding(0, 6, 12, 0);
        Style(_wtButton); _wtButton.AutoSize = true; _wtButton.Padding = new Padding(10, 2, 10, 2);
        _wtButton.Click += (_, _) =>
        {
            bool install = WinTabInstaller.State != WinTabState.Installed;
            string? err = WinTabInstaller.RunElevated(install);
            if (err != null) MessageBox.Show(this, err, "Divit — WinTab", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else if (install) MessageBox.Show(this, L.T("wintab.done"), "Divit — WinTab", MessageBoxButtons.OK, MessageBoxIcon.Information);
            RenderWinTab();
        };
        return Stack(
            (Row(_wtStatus, _wtButton), SizeType.AutoSize),
            (Wrapping(L.T("wintab.help")), SizeType.AutoSize));
    }

    /// <param name="recheck">Re-read the files (only on open and after install; not every tick).</param>
    private void RenderWinTab(bool recheck = true)
    {
        if (recheck) _wtState = WinTabInstaller.State;
        var st = _wtState;
        int users = (_h as TrayApp)?.WinTab?.ConsumerCount ?? 0;
        _wtStatus.Text = st switch
        {
            WinTabState.Installed => users > 0 ? L.T("wintab.installedUsers", users) : L.T("wintab.installed"),
            WinTabState.Outdated => L.T("wintab.outdated"),
            WinTabState.Foreign => L.T("wintab.foreign"),
            WinTabState.Missing => L.T("wintab.missing"),
            _ => L.T("wintab.notInstalled"),
        };
        _wtStatus.ForeColor = st == WinTabState.Installed ? Color.FromArgb(46, 160, 67) : Theme.Text;
        _wtButton.Text = st switch { WinTabState.Installed => L.T("wintab.uninstall"), WinTabState.Outdated => L.T("wintab.update"), _ => L.T("wintab.install") };
        _wtButton.Enabled = st is not (WinTabState.Foreign or WinTabState.Missing);
    }

    private Control ConnectionBody()
    {
        _wifi.AutoSize = true;
        _wifi.Text = L.T("conn.wifi");
        _wifi.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            if (!_h.SetWifi(_wifi.Checked)) { _loading = true; _wifi.Checked = !_wifi.Checked; _loading = false; }
            LoadValues();
        };
        _addresses.AutoSize = true; _addresses.ForeColor = Theme.Muted; _addresses.Font = Theme.Small;
        _code.Font = Theme.Code; _code.ForeColor = Theme.Text; _code.BackColor = Theme.Button;
        _code.BorderStyle = BorderStyle.FixedSingle; _code.MaxLength = 6; _code.Width = 120;
        _code.TextAlign = HorizontalAlignment.Center; _code.Margin = new Padding(0, 0, 12, 0);
        _code.KeyPress += (_, e) => { if (!char.IsControl(e.KeyChar) && !char.IsAsciiDigit(e.KeyChar)) e.Handled = true; };
        _code.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; CommitCode(); }
            else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; _code.Text = _h.Server.PairingCode; }
        };
        _code.Leave += (_, _) => CommitCode();
        _newCode.Text = L.T("conn.newCode"); Style(_newCode); _newCode.AutoSize = true; _newCode.Margin = new Padding(0, 8, 0, 0);
        _newCode.Click += (_, _) => { _h.Server.NewPairingCode(); LoadValues(); };
        _paired.AutoSize = true; _paired.ForeColor = Theme.Muted; _paired.Font = Theme.Small; _paired.Margin = new Padding(0, 6, 8, 0);
        _forget.Text = L.T("conn.forget"); Style(_forget); _forget.AutoSize = true;
        _forget.Click += (_, _) => { _h.Server.ForgetPairings(); LoadValues(); };
        _codeLbl.Text = L.T("conn.codeLabel"); _codeLbl.AutoSize = true; _codeLbl.ForeColor = Theme.Muted; _codeLbl.Font = Theme.Small;
        _codeLbl.Margin = new Padding(0, 6, 0, 0);

        _autostart.AutoSize = true;
        _autostart.Text = L.T("conn.autostart");
        _autostart.Margin = new Padding(3, 10, 3, 0);
        _autostart.CheckedChanged += (_, _) => { if (!_loading) Autostart.Set(_autostart.Checked); };

        Dropdown(_theme, 120);
        _theme.Items.AddRange([L.T("theme.dark"), L.T("theme.light"), L.T("theme.system")]);   // order = ThemeMode
        _theme.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            var mode = (ThemeMode)_theme.SelectedIndex;
            BeginInvoke(() => _h.SetTheme(mode));   // the window is rebuilt
        };
        Dropdown(_language, 120);
        _language.Items.AddRange([L.T("lang.system"), "English", "Türkçe"]);   // order = LanguageValues
        _language.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            string pref = LanguageValues[_language.SelectedIndex];
            BeginInvoke(() => _h.SetLanguage(pref));   // the window is rebuilt
        };
        var themeLbl = Text_(L.T("conn.theme"), Theme.Body, Theme.Text);
        themeLbl.Margin = new Padding(3, 6, 8, 0);
        var langLbl = Text_(L.T("conn.language"), Theme.Body, Theme.Text);
        langLbl.Margin = new Padding(16, 6, 8, 0);
        var s = Stack(
            (_wifi, SizeType.AutoSize),
            (_addresses, SizeType.AutoSize),
            (_codeLbl, SizeType.AutoSize),
            (Row(_code, _newCode), SizeType.AutoSize),
            (Row(_paired, _forget), SizeType.AutoSize),
            (_autostart, SizeType.AutoSize),
            (Row(themeLbl, _theme, langLbl, _language), SizeType.AutoSize));
        s.AutoSize = true;
        return s;
    }

    /// <summary>Save the typed code if it is 6 digits; otherwise show the current code again.</summary>
    private void CommitCode()
    {
        string typed = _code.Text.Trim();
        if (typed != _h.Server.PairingCode && _h.Server.SetPairingCode(typed)) Log.Info("pairing code changed by the user");
        _code.Text = _h.Server.PairingCode;
        RenderCodeLabel();
    }

    private void RenderCodeLabel() =>
        _codeLbl.Text = _h.Server.PairingLocked ? $"{L.T("conn.codeLabel")} — {L.T("conn.locked")}" : L.T("conn.codeLabel");

    private static Button FlatButton(string text) { var b = new Button { Text = text }; Style(b); return b; }

    private static void Style(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = Theme.Line;
        b.BackColor = Theme.Button;
        b.ForeColor = Theme.Text;
        b.Font = Theme.Small;
        b.Cursor = Cursors.Hand;
    }

    // ---- Values ----

    private void LoadValues()
    {
        _loading = true;
        var s = _h.Settings;
        _monitor.Items.Clear();
        foreach (var m in Monitors.List())
        {
            _monitor.Items.Add(m);
            if (m.DeviceName == _h.Monitor.DeviceName) _monitor.SelectedItem = m;
        }
        _keepAspect.Checked = s.KeepAspect;
        _wifi.Checked = s.WifiEnabled;
        bool w = s.WifiEnabled;
        _addresses.Visible = _code.Visible = _newCode.Visible = _codeLbl.Visible = w;
        _addresses.Text = L.T("conn.addresses", string.Join(", ", _h.LocalAddresses()));
        if (!_code.Focused) _code.Text = _h.Server.PairingCode;
        RenderCodeLabel();
        _paired.Text = _h.Pairing.Count == 0 ? L.T("conn.noPaired") : L.T("conn.paired", _h.Pairing.Count);
        _forget.Visible = _h.Pairing.Count > 0;
        _autostart.Checked = Autostart.IsEnabled;
        _theme.SelectedIndex = (int)Theme.Parse(s.Theme);
        _language.SelectedIndex = Math.Max(0, Array.IndexOf(LanguageValues, s.Language));
        RenderWinTab();
        _barrel.SelectedIndex = (int)s.Buttons.Action;
        _shortcut.Visible = s.Buttons.Action == BarrelAction.Shortcut;
        _shortcut.Text = KeyText(s.ShortcutKeys);
        UpdateCurveLabel();
        _loading = false;
        Tick(force: true);
    }

    /// <summary>The tablet reported its surface size: redraw the area preview with the real aspect ratio.</summary>
    public void SurfaceChanged() => _area.Invalidate();

    private void UpdateCurveLabel()
    {
        var s = _h.Settings;
        _curveValues.Text = L.T("pressure.values", s.PressureMin * 100, s.PressureMax * 100, s.PressureGamma);
    }

    private void Tick(bool force = false)
    {
        _curve.Invalidate();                         // live pressure dot (~30 Hz)
        if (!force && ++_slowTick % 15 != 0) return; // status ~2 Hz
        RenderWinTab(recheck: false);
        var (title, detail, color) = _h.Status();
        _statusTitle.Text = title;
        _statusDetail.Text = detail;
        _usbStatus.Text = L.T("menu.usb", _h.Adb.Status);
        _statusDot.ForeColor = color;
        _statusDot.Invalidate();
        if (_h.Settings.WifiEnabled)
        {
            if (!_code.Focused) _code.Text = _h.Server.PairingCode;   // do not overwrite while the user is typing
            RenderCodeLabel();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Card with rounded corners.</summary>
internal sealed class RoundedPanel : Panel
{
    public RoundedPanel() { DoubleBuffered = true; ResizeRedraw = true; }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Bg);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Round(new Rectangle(0, 0, Width - 1, Height - 1), 14);
        using var b = new SolidBrush(BackColor);
        using var pen = new Pen(Theme.Line);
        e.Graphics.FillPath(b, path);
        e.Graphics.DrawPath(pen, path);
    }

    internal static GraphicsPath Round(Rectangle r, int rad)
    {
        var p = new GraphicsPath();
        int d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

/// <summary>
/// Tablet surface: the dashed line is the active area the user chose (IN-09), the blue fill is the part actually used
/// after aspect-ratio cropping. Drag with the mouse to draw a new area.
/// </summary>
internal sealed class AreaPreview : Control
{
    /// <summary>The last connected tablet's aspect ratio (saved from SURFACE_INFO); 16:10 until a tablet has connected.</summary>
    private float SurfaceAspect => _h.Settings.SurfaceAspect is > 0.5f and < 3f ? _h.Settings.SurfaceAspect : 16f / 10;
    private const float MinSize = 0.15f;
    private readonly IHostControl _h;
    private PointF? _dragStart;
    public event Action<bool>? Changed;

    public AreaPreview(IHostControl h) { _h = h; DoubleBuffered = true; ResizeRedraw = true; BackColor = Theme.Card; Cursor = Cursors.Cross; }

    private RectangleF Tablet
    {
        get
        {
            float h = Height - 26, w = h * SurfaceAspect;
            if (w > Width - 2) { w = Width - 2; h = w / SurfaceAspect; }
            return new RectangleF(1, 1, w, h);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var s = _h.Settings;
        var eff = new AreaMapping(_h.Monitor.Bounds, s.ActiveArea, SurfaceAspect, s.KeepAspect).EffectiveArea;
        var tab = Tablet;
        float w = tab.Width, h = tab.Height;

        using var tabBrush = new SolidBrush(Theme.Tablet);
        using var path = RoundedPanel.Round(Rectangle.Round(tab), 10);
        g.FillPath(tabBrush, path);

        var used = new RectangleF(tab.X + eff.Left * w, tab.Y + eff.Top * h, eff.Width * w, eff.Height * h);
        using var usedBrush = new SolidBrush(Color.FromArgb(70, Theme.Accent));
        using var usedPen = new Pen(Theme.Accent, 2);
        g.FillRectangle(usedBrush, used);
        g.DrawRectangle(usedPen, used.X, used.Y, used.Width, used.Height);

        var a = s.ActiveArea;
        if (a != NormRect.Full)
        {
            using var dash = new Pen(Theme.Muted, 1.5f) { DashStyle = DashStyle.Dash };
            g.DrawRectangle(dash, tab.X + a.Left * w, tab.Y + a.Top * h, a.Width * w, a.Height * h);
        }

        using var tb = new SolidBrush(Theme.Muted);
        string area = a == NormRect.Full ? L.T("area.full") : L.T("area.part", a.Width * a.Height * 100);
        g.DrawString(L.T("area.caption", area, _h.Monitor.Bounds.Width, _h.Monitor.Bounds.Height), Theme.Small, tb, 0, h + 6);
    }

    private PointF ToNorm(Point p)
    {
        var t = Tablet;
        return new(Math.Clamp((p.X - t.X) / t.Width, 0, 1), Math.Clamp((p.Y - t.Y) / t.Height, 0, 1));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && Tablet.Contains(e.Location)) _dragStart = ToNorm(e.Location);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragStart is not { } a) return;
        var b = ToNorm(e.Location);
        float l = Math.Min(a.X, b.X), r = Math.Max(a.X, b.X), t = Math.Min(a.Y, b.Y), bo = Math.Max(a.Y, b.Y);
        if (r - l < MinSize || bo - t < MinSize) return;   // too small an area makes the pen far too sensitive
        var s = _h.Settings;
        s.AreaLeft = MathF.Round(l, 3); s.AreaRight = MathF.Round(r, 3); s.AreaTop = MathF.Round(t, 3); s.AreaBottom = MathF.Round(bo, 3);
        Changed?.Invoke(false);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragStart == null) return;
        _dragStart = null;
        Changed?.Invoke(true);
    }

    public void Reset()
    {
        var s = _h.Settings;
        s.AreaLeft = s.AreaTop = 0; s.AreaRight = s.AreaBottom = 1;
        Changed?.Invoke(true);
        Invalidate();
    }
}

/// <summary>
/// Pressure curve editor: p' = clamp((p − min)/(max − min))^γ. Three handles: lower threshold, upper threshold, curve (γ).
/// Applied immediately while dragging (Changed(false)), saved on release (Changed(true)).
/// </summary>
internal sealed class CurveEditor : Control
{
    private readonly IHostControl _h;
    private enum Grip { None, Min, Max, Gamma }
    private Grip _drag;
    public event Action<bool>? Changed;

    public CurveEditor(IHostControl h) { _h = h; DoubleBuffered = true; ResizeRedraw = true; BackColor = Theme.Card; Cursor = Cursors.Hand; }

    private RectangleF Plot => new(34, 8, Width - 46, Height - 40);
    private PointF ToScreen(float x, float y) { var r = Plot; return new(r.X + x * r.Width, r.Bottom - y * r.Height); }
    private float Out(float p) => new PressureCurve(_h.Settings.PressureMin, _h.Settings.PressureMax, _h.Settings.PressureGamma).Map(p, true) / 1024f;

    private (PointF min, PointF max, PointF gamma) Handles()
    {
        var s = _h.Settings;
        float mid = (s.PressureMin + s.PressureMax) / 2;
        return (ToScreen(s.PressureMin, 0), ToScreen(s.PressureMax, 1), ToScreen(mid, MathF.Pow(0.5f, s.PressureGamma)));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = Plot;
        using var grid = new Pen(Theme.Line);
        for (int i = 0; i <= 4; i++)
        {
            float t = i / 4f;
            g.DrawLine(grid, r.X + t * r.Width, r.Y, r.X + t * r.Width, r.Bottom);
            g.DrawLine(grid, r.X, r.Bottom - t * r.Height, r.Right, r.Bottom - t * r.Height);
        }
        using var muted = new SolidBrush(Theme.Muted);
        string xLabel = L.T("pressure.axisX");
        g.DrawString(xLabel, Theme.Small, muted, r.Right - g.MeasureString(xLabel, Theme.Small).Width, r.Bottom + 6);
        g.TranslateTransform(4, r.Y + 140);
        g.RotateTransform(-90);
        g.DrawString(L.T("pressure.axisY"), Theme.Small, muted, 0, 0);
        g.ResetTransform();

        var pts = new PointF[101];
        for (int i = 0; i <= 100; i++) pts[i] = ToScreen(i / 100f, Out(i / 100f));
        using var curve = new Pen(Theme.Accent, 3) { LineJoin = LineJoin.Round };
        g.DrawLines(curve, pts);

        var (hmin, hmax, hg) = Handles();
        using var hb = new SolidBrush(Theme.Card);
        using var hp = new Pen(Theme.Accent, 2.5f);
        foreach (var p in new[] { hmin, hmax, hg })
        {
            g.FillEllipse(hb, p.X - 7, p.Y - 7, 14, 14);
            g.DrawEllipse(hp, p.X - 7, p.Y - 7, 14, 14);
        }

        // Live pressure: while the pen is in contact the dot moves along the curve.
        if (_h.Server.Active?.Pipeline is { } pl && pl.LastInContact)
        {
            float p = pl.LastPressure;
            var at = ToScreen(p, Out(p));
            using var live = new SolidBrush(Theme.Live);
            using var guide = new Pen(Color.FromArgb(120, Theme.Live)) { DashStyle = DashStyle.Dash };
            g.DrawLine(guide, at.X, r.Bottom, at.X, at.Y);
            g.FillEllipse(live, at.X - 6, at.Y - 6, 12, 12);
            g.DrawString($"{p * 100:F0}% → {Out(p) * 100:F0}%", Theme.Small, muted, Math.Min(at.X + 8, r.Right - 80), Math.Max(at.Y - 20, r.Y));
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        var (a, b, c) = Handles();
        static float D(PointF p, Point m) => MathF.Sqrt((p.X - m.X) * (p.X - m.X) + (p.Y - m.Y) * (p.Y - m.Y));
        var best = new[] { (Grip.Min, D(a, e.Location)), (Grip.Max, D(b, e.Location)), (Grip.Gamma, D(c, e.Location)) }.MinBy(t => t.Item2);
        _drag = best.Item2 < 18 ? best.Item1 : Grip.None;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag == Grip.None) return;
        var r = Plot;
        var s = _h.Settings;
        float x = Math.Clamp((e.X - r.X) / r.Width, 0, 1), y = Math.Clamp((r.Bottom - e.Y) / r.Height, 0.02f, 0.98f);
        switch (_drag)
        {
            case Grip.Min: s.PressureMin = MathF.Round(Math.Min(x, s.PressureMax - 0.05f), 2); break;
            case Grip.Max: s.PressureMax = MathF.Round(Math.Max(x, s.PressureMin + 0.05f), 2); break;
            case Grip.Gamma: s.PressureGamma = MathF.Round(Math.Clamp(MathF.Log(y) / MathF.Log(0.5f), 0.3f, 3f), 2); break;
        }
        Changed?.Invoke(false);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_drag != Grip.None) Changed?.Invoke(true);
        _drag = Grip.None;
    }
}

/// <summary>Start with Windows (HS-03): HKCU\...\Run. No admin rights needed.</summary>
internal static class Autostart
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "Divit";

    public static bool IsEnabled
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(Key); return k?.GetValue(Name) is string; }
    }

    public static void Set(bool on)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key, writable: true) ?? Registry.CurrentUser.CreateSubKey(Key);
        if (on) k.SetValue(Name, $"\"{Environment.ProcessPath}\" --tray");
        else k.DeleteValue(Name, throwOnMissingValue: false);
        Log.Info($"start with Windows: {(on ? "on" : "off")}");
    }
}
