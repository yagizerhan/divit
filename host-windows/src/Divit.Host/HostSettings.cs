// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// Host settings: %APPDATA%\Divit\settings.json

using System.Text.Json;
using Divit.Core;

namespace Divit.Host;

public sealed class HostSettings
{
    public string? MonitorDevice { get; set; }
    public bool KeepAspect { get; set; } = true;
    public float PressureMin { get; set; } = 0f;
    public float PressureMax { get; set; } = 0.70f;
    public float PressureGamma { get; set; } = 1f;
    /// <summary>Side button: PenButton (default) / RightClick / Eraser / Shortcut / Off.</summary>
    public string BarrelAction { get; set; } = "PenButton";
    /// <summary>Shortcut (WinForms Keys value: key | Shift/Control/Alt flags).</summary>
    public int ShortcutKeys { get; set; }
    /// <summary>Active tablet area (IN-09), 0…1.</summary>
    public float AreaLeft { get; set; } = 0f;
    public float AreaTop { get; set; } = 0f;
    public float AreaRight { get; set; } = 1f;
    public float AreaBottom { get; set; } = 1f;

    [System.Text.Json.Serialization.JsonIgnore]
    public ButtonSettings Buttons => new(Enum.TryParse<BarrelAction>(BarrelAction, out var a) ? a : Core.BarrelAction.PenButton, ShortcutKeys);
    [System.Text.Json.Serialization.JsonIgnore]
    public NormRect ActiveArea => new(AreaLeft, AreaTop, AreaRight, AreaBottom);
    /// <summary>Accept tablets over the local network (pairing code). On by default.</summary>
    public bool WifiEnabled { get; set; } = true;
    /// <summary>Dark (default) / Light / System.</summary>
    public string Theme { get; set; } = "Dark";
    /// <summary>UI language: System (Windows display language) / en / tr.</summary>
    public string Language { get; set; } = "System";
    public int Port { get; set; } = Divit.Protocol.Wire.DefaultPort;
    /// <summary>Wi-Fi pairing code (6 digits). Fixed: created once, changed only by the user.</summary>
    public string? PairingCode { get; set; }
    /// <summary>Width / height of the last connected tablet's drawing surface (SURFACE_INFO); for the area preview.</summary>
    public float SurfaceAspect { get; set; } = 16f / 10;

    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Divit", "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    [System.Text.Json.Serialization.JsonIgnore] public PressureCurve Curve => new(PressureMin, PressureMax, PressureGamma);

    /// <summary>There was no settings file: first run (the window opens by itself).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool IsFirstRun { get; private set; }

    public static HostSettings Load()
    {
        if (!File.Exists(FilePath)) return new() { IsFirstRun = true };
        HostSettings s;
        try { s = JsonSerializer.Deserialize<HostSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { s = new(); }
        s.Normalize();
        return s;
    }

    /// <summary>
    /// A hand-edited or corrupt file must not reach the pen path or the UI: NaN, a 0-wide area, an inverted pressure range
    /// or an invalid port are replaced with valid values (the curve editor and the area preview read these too).
    /// </summary>
    private void Normalize()
    {
        var c = PressureCurve.Safe(PressureMin, PressureMax, PressureGamma);
        (PressureMin, PressureMax, PressureGamma) = (c.PMin, c.PMax, c.Gamma);
        var a = AreaMapping.Sanitize(ActiveArea);
        (AreaLeft, AreaTop, AreaRight, AreaBottom) = (a.Left, a.Top, a.Right, a.Bottom);
        if (!float.IsFinite(SurfaceAspect) || SurfaceAspect is <= 0.5f or >= 3f) SurfaceAspect = 16f / 10;
        if (Port is < 1024 or > 65535) Port = Divit.Protocol.Wire.DefaultPort;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        Divit.Core.Server.AtomicFile.Write(FilePath, JsonSerializer.Serialize(this, Json));
    }
}
