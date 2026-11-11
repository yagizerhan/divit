// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// Area mapping and pressure curve (architecture §4.2–4.3). Immutable objects: on a settings change a new one is swapped in atomically.

namespace Divit.Core;

/// <summary>A rectangle in screen coordinates (virtual desktop, pixels).</summary>
public readonly record struct ScreenRect(int Left, int Top, int Width, int Height)
{
    public override string ToString() => $"{Width}×{Height} @ ({Left},{Top})";
}

/// <summary>A normalized (0…1) part of the tablet surface.</summary>
public readonly record struct NormRect(float Left, float Top, float Right, float Bottom)
{
    public static readonly NormRect Full = new(0, 0, 1, 1);
    public float Width => Right - Left;
    public float Height => Bottom - Top;
}

/// <summary>
/// <c>p' = clamp((p − pMin)/(pMax − pMin))^γ</c> → <c>round(p'·1024)</c>.
/// Default upper threshold 0.70: in testing the hardest press stayed around 0.68–0.70 (decision K-14).
/// </summary>
public sealed record PressureCurve(float PMin = 0f, float PMax = 0.70f, float Gamma = 1f)
{
    public const uint WindowsMax = 1024;

    /// <summary>Values from a hand-edited or corrupt settings file: NaN/∞ or out of range → the defaults / the valid range.</summary>
    public static PressureCurve Safe(float pMin, float pMax, float gamma)
    {
        var d = new PressureCurve();
        float lo = float.IsFinite(pMin) ? Math.Clamp(pMin, 0f, 1f) : d.PMin;
        float hi = float.IsFinite(pMax) ? Math.Clamp(pMax, 0f, 1f) : d.PMax;
        if (hi <= lo) (lo, hi) = (d.PMin, d.PMax);
        float g = float.IsFinite(gamma) ? Math.Clamp(gamma, 0.1f, 10f) : d.Gamma;
        return new PressureCurve(lo, hi, g);
    }

    /// <summary>Curve-mapped pressure, 0…1 (full resolution; for WinTab).</summary>
    public float MapF(float p)
    {
        float span = PMax - PMin;
        float x = span > 1e-6f ? (p - PMin) / span : 1f;
        x = Math.Clamp(x, 0f, 1f);
        return Gamma != 1f ? MathF.Pow(x, Gamma) : x;
    }

    public uint Map(float p, bool inContact)
    {
        if (!inContact) return 0;
        float x = MapF(p);
        uint v = (uint)MathF.Round(x * WindowsMax);
        // Some apps treat pressure 0 during contact as "no contact"; send at least 1.
        return Math.Max(1u, v);
    }
}

/// <summary>Tablet area → monitor mapping. When the aspect ratio is kept the area is cropped around its center.</summary>
public sealed class AreaMapping
{
    public ScreenRect Target { get; }
    /// <summary>The part of the tablet actually used after cropping (reported to the tablet via HOST_STATUS).</summary>
    public NormRect EffectiveArea { get; }

    /// <summary>Narrowest usable active area (fraction of the tablet). Anything smaller, or not finite, means the full surface.</summary>
    public const float MinAreaSize = 0.05f;

    public AreaMapping(ScreenRect target, NormRect activeArea, float surfaceAspect, bool keepAspect)
    {
        Target = target;
        var a = Sanitize(activeArea);
        if (!float.IsFinite(surfaceAspect)) surfaceAspect = 0;
        if (keepAspect && surfaceAspect > 0 && target.Height > 0 && a.Width > 0 && a.Height > 0)
        {
            float areaAspect = a.Width * surfaceAspect / a.Height;   // physical aspect ratio
            float monAspect = (float)target.Width / target.Height;
            if (areaAspect > monAspect)
            {
                float w = a.Width * monAspect / areaAspect, cx = (a.Left + a.Right) / 2;
                a = a with { Left = cx - w / 2, Right = cx + w / 2 };
            }
            else if (areaAspect < monAspect)
            {
                float h = a.Height * areaAspect / monAspect, cy = (a.Top + a.Bottom) / 2;
                a = a with { Top = cy - h / 2, Bottom = cy + h / 2 };
            }
        }
        EffectiveArea = a;
    }

    /// <summary>
    /// A hand-edited or corrupt settings file must not reach the hot path: a 0-wide area divides by zero in
    /// <see cref="MapNorm"/> (NaN coordinates). Edges are clamped to 0…1; a too small or inverted area becomes the full surface.
    /// </summary>
    public static NormRect Sanitize(NormRect r)
    {
        if (!(float.IsFinite(r.Left) && float.IsFinite(r.Top) && float.IsFinite(r.Right) && float.IsFinite(r.Bottom))) return NormRect.Full;
        var c = new NormRect(Math.Clamp(r.Left, 0f, 1f), Math.Clamp(r.Top, 0f, 1f), Math.Clamp(r.Right, 0f, 1f), Math.Clamp(r.Bottom, 0f, 1f));
        return c.Width >= MinAreaSize && c.Height >= MinAreaSize ? c : NormRect.Full;
    }

    /// <summary>u, v → 0…1 within the target monitor. Values outside the area are clamped to the edge.</summary>
    public (float X, float Y) MapNorm(float u, float v)
    {
        var a = EffectiveArea;
        return (Math.Clamp((u - a.Left) / a.Width, 0f, 1f), Math.Clamp((v - a.Top) / a.Height, 0f, 1f));
    }

    /// <summary>Normalized position → screen pixel (last pixel included: 0…Width−1).</summary>
    public (int X, int Y) ToPixel(float x, float y) =>
        (Target.Left + (int)MathF.Round(x * (Target.Width - 1)), Target.Top + (int)MathF.Round(y * (Target.Height - 1)));

    /// <summary>u, v → screen pixel.</summary>
    public (int X, int Y) Map(float u, float v) { var (x, y) = MapNorm(u, v); return ToPixel(x, y); }
}
