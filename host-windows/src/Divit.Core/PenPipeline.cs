// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// PenPipeline: sequence check → state machine → mapping → pressure curve → IPenSink (architecture §3.1, §4.1).
// Hot path: called by a single thread (the receive thread); no allocation, no locks.

using Divit.Protocol;

namespace Divit.Core;

public enum PenTransition : byte { Hover, Down, Move, Up, Leave }

/// <summary>One pen event handed to the platform. Coordinates are screen pixels (virtual desktop).</summary>
/// <param name="NormX">0…1 within the target monitor (full resolution for WinTab; X/Y are the same rounded to pixels).</param>
/// <param name="PressureF">Curve-mapped pressure 0…1 (WinTab carries 4096 levels; Pressure is 0…1024 for Windows Ink).</param>
public readonly record struct PenOutput(PenTransition Transition, int X, int Y, uint Pressure, int TiltX, int TiltY, bool Barrel, bool Eraser,
    float NormX = 0, float NormY = 0, float PressureF = 0);

/// <summary>Windows Ink, WinTab, etc. The pipeline decides the transitions.</summary>
public interface IPenSink : IDisposable
{
    /// <summary>Injects the event; false on failure (the pipeline retries on the next sample).</summary>
    bool Apply(in PenOutput output);
}

public enum PenState : byte { OutOfRange, Hover, Contact }

/// <summary>Side button assignment (IN-07).</summary>
public enum BarrelAction : byte { PenButton, RightClick, Eraser, Shortcut, Off }

/// <param name="ShortcutKeys">Key + modifiers in WinForms Keys layout (Shift 0x10000, Ctrl 0x20000, Alt 0x40000).</param>
public sealed record ButtonSettings(BarrelAction Action = BarrelAction.PenButton, int ShortcutKeys = 0)
{
    public static readonly ButtonSettings Default = new();
}

/// <summary>Actions other than pen events (right click, keyboard shortcut). Implemented by the platform.</summary>
public interface IButtonActions
{
    void RightClick();
    void Shortcut(int keys);
}

public sealed record PipelineSettings(AreaMapping Mapping, PressureCurve Curve, ButtonSettings? Button = null)
{
    public ButtonSettings Buttons => Button ?? ButtonSettings.Default;
}

public sealed class PenPipeline(IPenSink sink, PipelineSettings settings, IButtonActions? actions = null)
{
    private PipelineSettings _settings = settings;
    private PenState _state;
    private int _lastX, _lastY;
    private int _expectedSeq = -1;
    private bool _lastBarrel, _eraserLatched;
    private float _nx, _ny, _pf;   // WinTab values carried into Emit (private to the receive thread)

    public PenState State => _state;
    public PipelineSettings Settings => Volatile.Read(ref _settings);

    // Counters (written only by the receive thread; readers may see approximate values).
    public long Samples, SeqGaps, InjectFailures;
    /// <summary>Indicator of Android batching: share of samples flagged historical (hover and contact separately).</summary>
    public long HoverSamples, HoverHistorical, ContactSamples, ContactHistorical;
    /// <summary>Last raw pressure (0…1) and contact state, for the live indicator in the settings window.</summary>
    public volatile float LastPressure;
    public volatile bool LastInContact;
    public readonly LatencyHistogram Latency = new();

    /// <summary>Settings change: atomic swap of an immutable object (no lock on the hot path).</summary>
    public void UpdateSettings(PipelineSettings s) => Volatile.Write(ref _settings, s);

    /// <param name="hostNowUs">Injection time (host clock).</param>
    /// <param name="clockOffsetUs">host clock − tablet clock; null if unknown (no latency is recorded).</param>
    public void Process(in PenSample s, long hostNowUs, long? clockOffsetUs)
    {
        Samples++;
        LastPressure = s.Pressure; LastInContact = s.InContact;
        bool hist = (s.Flags & PenFlags.Historical) != 0;
        if (s.InContact) { ContactSamples++; if (hist) ContactHistorical++; }
        else if (s.InRange) { HoverSamples++; if (hist) HoverHistorical++; }
        if (_expectedSeq >= 0 && s.Seq != _expectedSeq) SeqGaps++;
        _expectedSeq = (s.Seq + 1) & 0xFFFF;

        var st = Volatile.Read(ref _settings);
        var (nx, ny) = st.Mapping.MapNorm(s.U, s.V);
        var (x, y) = st.Mapping.ToPixel(nx, ny);
        _nx = nx; _ny = ny;
        _pf = s.InContact ? st.Curve.MapF(s.Pressure) : 0f;
        int tx = (int)MathF.Round(s.TiltX), ty = (int)MathF.Round(s.TiltY);
        bool rawBarrel = s.Barrel && s.InRange, eraser = (s.Flags & PenFlags.Eraser) != 0;
        bool barrelPressed = rawBarrel && !_lastBarrel;
        _lastBarrel = rawBarrel;

        // Side button assignment (IN-07). Only "pen button" is passed to Windows as barrel.
        bool barrel = false;
        switch (st.Buttons.Action)
        {
            case BarrelAction.PenButton: barrel = rawBarrel; break;
            case BarrelAction.Eraser:
                // The eraser state is latched at the start of contact so pen/eraser does not flip mid-stroke.
                if (_state == PenState.Contact) eraser |= _eraserLatched;   // Move and Up finish with the same tip
                else eraser |= rawBarrel;
                break;
            case BarrelAction.RightClick:
                if (barrelPressed && !s.InContact) actions?.RightClick();   // while hovering: right click at the cursor
                break;
            case BarrelAction.Shortcut:
                if (barrelPressed && st.Buttons.ShortcutKeys != 0) actions?.Shortcut(st.Buttons.ShortcutKeys);
                break;
        }

        switch (_state)
        {
            case PenState.OutOfRange:
                if (!s.InRange) return;
                if (!Emit(PenTransition.Hover, x, y, 0, tx, ty, barrel, eraser)) return;
                _state = PenState.Hover;
                if (s.InContact && Emit(PenTransition.Down, x, y, st.Curve.Map(s.Pressure, true), tx, ty, barrel, eraser)) { _state = PenState.Contact; _eraserLatched = eraser; }
                break;

            case PenState.Hover:
                if (!s.InRange) { if (Emit(PenTransition.Leave, x, y, 0, 0, 0, false, eraser)) _state = PenState.OutOfRange; }
                else if (s.InContact) { if (Emit(PenTransition.Down, x, y, st.Curve.Map(s.Pressure, true), tx, ty, barrel, eraser)) { _state = PenState.Contact; _eraserLatched = eraser; } }
                else Emit(PenTransition.Hover, x, y, 0, tx, ty, barrel, eraser);
                break;

            case PenState.Contact:
                if (s.InContact) Emit(PenTransition.Move, x, y, st.Curve.Map(s.Pressure, true), tx, ty, barrel, eraser);
                else if (Emit(PenTransition.Up, x, y, 0, tx, ty, barrel, eraser))   // on failure UP is retried with the next sample
                {
                    _state = PenState.Hover;
                    if (!s.InRange && Emit(PenTransition.Leave, x, y, 0, 0, 0, false, eraser)) _state = PenState.OutOfRange;
                }
                break;
        }

        if (clockOffsetUs is long off) Latency.Add(hostNowUs - (s.TimeUs + off));
    }

    /// <summary>
    /// Connection lost / shutdown: UP if in contact, then out of range (CN-03). The pen never stays pressed.
    /// Leave is always sent: the pipeline may think it is out of range while the device (after an error) is still in
    /// contact; the Windows sink turns it into UP + Leave based on the device's real state, or does nothing if the
    /// device is already out of range.
    /// </summary>
    public void ReleaseAll()
    {
        if (_state == PenState.Contact) sink.Apply(new PenOutput(PenTransition.Up, _lastX, _lastY, 0, 0, 0, false, false, _nx, _ny));
        sink.Apply(new PenOutput(PenTransition.Leave, _lastX, _lastY, 0, 0, 0, false, false, _nx, _ny));
        _state = PenState.OutOfRange;
        _expectedSeq = -1;
        _lastBarrel = false;
    }

    private bool Emit(PenTransition t, int x, int y, uint p, int tx, int ty, bool barrel, bool eraser)
    {
        _lastX = x; _lastY = y;
        float pf = t is PenTransition.Down or PenTransition.Move ? _pf : 0f;
        if (sink.Apply(new PenOutput(t, x, y, p, tx, ty, barrel, eraser, _nx, _ny, pf))) return true;
        // Injection failure (architecture §10): the state stays at the sink's last ACCEPTED transition and the next
        // sample retries the same transition. (It used to reset to OutOfRange: Windows still considered the pen in
        // contact, a Hover without UP was rejected, and ReleaseAll sent no UP, so the pen could stay pressed.)
        InjectFailures++;
        return false;
    }
}
