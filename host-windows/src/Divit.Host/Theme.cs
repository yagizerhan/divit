// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// Appearance: dark (default) / light / system. The cards we draw take colors from the palette; standard WinForms
// controls (dropdowns, check boxes, scrollbars) follow Application.SetColorMode, and the title bar follows DWM.

using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Divit.Host;

public enum ThemeMode { Dark, Light, System }

internal static class Theme
{
    private sealed record Palette(Color Bg, Color Card, Color Text, Color Muted, Color Accent, Color Line, Color Button, Color Tablet, Color Live);

    private static readonly Palette DarkP = new(
        Bg: Color.FromArgb(24, 26, 30), Card: Color.FromArgb(34, 37, 43), Text: Color.FromArgb(230, 233, 239),
        Muted: Color.FromArgb(154, 161, 173), Accent: Color.FromArgb(91, 141, 239), Line: Color.FromArgb(52, 57, 68),
        Button: Color.FromArgb(44, 48, 56), Tablet: Color.FromArgb(14, 15, 18), Live: Color.FromArgb(240, 98, 88));

    private static readonly Palette LightP = new(
        Bg: Color.FromArgb(243, 244, 247), Card: Color.White, Text: Color.FromArgb(27, 29, 34),
        Muted: Color.FromArgb(95, 102, 115), Accent: Color.FromArgb(61, 111, 216), Line: Color.FromArgb(225, 228, 234),
        Button: Color.FromArgb(247, 248, 250), Tablet: Color.FromArgb(40, 44, 52), Live: Color.FromArgb(214, 69, 59));

    private static Palette _p = DarkP;

    public static bool IsDark { get; private set; } = true;
    public static Color Bg => _p.Bg;
    public static Color Card => _p.Card;
    public static Color Text => _p.Text;
    public static Color Muted => _p.Muted;
    public static Color Accent => _p.Accent;
    public static Color Line => _p.Line;
    public static Color Button => _p.Button;
    public static Color Tablet => _p.Tablet;
    public static Color Live => _p.Live;

    public static readonly Font Body = new("Segoe UI", 10f);
    public static readonly Font Small = new("Segoe UI", 9f);
    public static readonly Font H2 = new("Segoe UI Semibold", 11.5f);
    public static readonly Font H1 = new("Segoe UI Semibold", 15f);
    public static readonly Font Code = new("Segoe UI Semibold", 20f);

    public static ThemeMode Parse(string? s) => Enum.TryParse<ThemeMode>(s, true, out var m) ? m : ThemeMode.Dark;

    /// <summary>Sets the palette and the WinForms color mode. Newly opened windows are drawn with this theme.</summary>
    public static void Use(ThemeMode mode)
    {
        IsDark = mode switch { ThemeMode.Light => false, ThemeMode.System => SystemPrefersDark(), _ => true };
        _p = IsDark ? DarkP : LightP;
#pragma warning disable WFO5001 // SetColorMode is experimental in .NET 9+; it darkens the standard controls.
        Application.SetColorMode(IsDark ? SystemColorMode.Dark : SystemColorMode.Classic);
#pragma warning restore WFO5001
    }

    private static bool SystemPrefersDark()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Match the title bar to the theme (Windows 11: DWMWA_USE_IMMERSIVE_DARK_MODE = 20).</summary>
    public static void ApplyTitleBar(Form f)
    {
        int on = IsDark ? 1 : 0;
        DwmSetWindowAttribute(f.Handle, 20, ref on, sizeof(int));
    }

    /// <summary>Theme colors for the tray menu.</summary>
    public static ToolStripRenderer MenuRenderer() => new ToolStripProfessionalRenderer(new MenuColors()) { RoundedEdges = true };

    private sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Card;
        public override Color ImageMarginGradientBegin => Card;
        public override Color ImageMarginGradientMiddle => Card;
        public override Color ImageMarginGradientEnd => Card;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Accent;
        public override Color MenuItemSelected => IsDark ? Color.FromArgb(50, 56, 70) : Color.FromArgb(230, 236, 250);
        public override Color MenuItemSelectedGradientBegin => MenuItemSelected;
        public override Color MenuItemSelectedGradientEnd => MenuItemSelected;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Line;
        public override Color CheckBackground => MenuItemSelected;
        public override Color CheckSelectedBackground => MenuItemSelected;
        public override Color CheckPressedBackground => MenuItemSelected;
    }
}
