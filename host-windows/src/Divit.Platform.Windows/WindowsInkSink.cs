// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// Windows Ink: PT_PEN injection with the Synthetic Pointer API (decision K-04 path A, verified in experiment S1).
// No admin rights needed. Hot path: no allocation, one call per step.

using System.Runtime.InteropServices;
using Divit.Core;
using static Divit.Platform.Windows.Native;

namespace Divit.Platform.Windows;

/// <summary>
/// The Windows synthetic pen. Requests are turned into valid steps from the device's real state by the
/// <see cref="PenDeviceSink"/> base: e.g. if Windows still considers the pen in contact and Hover/Leave is requested, UP goes first (CN-03).
/// </summary>
public sealed unsafe class WindowsInkSink : PenDeviceSink
{
    private IntPtr _device;                // touched only by the injecting thread (and Dispose at exit)
    private bool _tapFeedback;             // feedback mode _device was created with
    private volatile bool _wantTapFeedback;
    private int _vsLeft, _vsTop;
    private int _failuresLogged, _repairsLogged;

    public int LastError { get; private set; }

    /// <param name="tapFeedback">Windows' own touch ring/ripple where the pen touches (Windows' "visual effects").</param>
    public WindowsInkSink(bool tapFeedback = false)
    {
        _device = Create(tapFeedback);
        if (_device == IntPtr.Zero)
            throw new InvalidOperationException($"CreateSyntheticPointerDevice failed (error {Marshal.GetLastWin32Error()})");
        _tapFeedback = _wantTapFeedback = tapFeedback;
        RefreshDisplayLayout();
    }

    private static IntPtr Create(bool tapFeedback) =>
        CreateSyntheticPointerDevice(PT_PEN, 1, tapFeedback ? POINTER_FEEDBACK_DEFAULT : POINTER_FEEDBACK_NONE);

    /// <summary>
    /// Turns Windows' tap ring on or off. The mode is fixed when the device is created, so the device is recreated —
    /// on the injecting thread, the next time the pen comes into range (a fresh device starts out of range too).
    /// </summary>
    public void SetTapFeedback(bool on) => _wantTapFeedback = on;

    private void SwapDevice(bool on)
    {
        var fresh = Create(on);
        if (fresh == IntPtr.Zero)
        {
            Log.Info($"tap feedback change failed (error {Marshal.GetLastWin32Error()}); keeping the current pen device");
            _wantTapFeedback = _tapFeedback;
            return;
        }
        var old = _device;
        _device = fresh;
        _tapFeedback = on;
        DestroySyntheticPointerDevice(old);
        Log.Info($"tap feedback {(on ? "on" : "off")}");
    }

    /// <summary>
    /// Finding from S1: ptPixelLocation is relative to the top-left corner of the virtual desktop. Must be re-read when
    /// the monitor layout changes (WM_DISPLAYCHANGE).
    /// </summary>
    public void RefreshDisplayLayout()
    {
        Volatile.Write(ref _vsLeft, GetSystemMetrics(SM_XVIRTUALSCREEN));
        Volatile.Write(ref _vsTop, GetSystemMetrics(SM_YVIRTUALSCREEN));
    }

    protected override void OnRepair(PenState device, PenTransition requested, ReadOnlySpan<PenTransition> steps)
    {
        if (++_repairsLogged <= 50)   // rare: allocating is fine here
            Log.Info($"pen state repaired: device {device}, requested {requested} → {string.Join("+", steps.ToArray())}");
    }

    protected override bool Inject(in PenOutput o, PenTransition t)
    {
        bool want = _wantTapFeedback;
        if (want != _tapFeedback && t == PenTransition.Hover && DeviceState == PenState.OutOfRange) SwapDevice(want);   // rare
        // S1: the "Full" flag set closest to real pens — PRIMARY | CONFIDENCE, FIRSTBUTTON while in contact.
        uint flags = POINTER_FLAG_PRIMARY | POINTER_FLAG_CONFIDENCE | t switch
        {
            PenTransition.Hover => POINTER_FLAG_INRANGE | POINTER_FLAG_UPDATE,
            PenTransition.Down => POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_DOWN | POINTER_FLAG_FIRSTBUTTON,
            PenTransition.Move => POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_UPDATE | POINTER_FLAG_FIRSTBUTTON,
            PenTransition.Up => POINTER_FLAG_INRANGE | POINTER_FLAG_UP,
            _ /* Leave */ => POINTER_FLAG_UPDATE,
        };
        if (t == PenTransition.Leave) flags &= ~POINTER_FLAG_CONFIDENCE;

        POINTER_TYPE_INFO info = default;
        info.type = PT_PEN;
        ref var pen = ref info.penInfo;
        pen.pointerInfo.pointerType = PT_PEN;
        pen.pointerInfo.pointerFlags = flags;
        pen.pointerInfo.ptPixelLocation = new POINT { X = o.X - _vsLeft, Y = o.Y - _vsTop };
        pen.penFlags = (o.Barrel ? PEN_FLAG_BARREL : 0) | (o.Eraser ? PEN_FLAG_ERASER | PEN_FLAG_INVERTED : 0);
        pen.penMask = PEN_MASK_PRESSURE | PEN_MASK_TILT_X | PEN_MASK_TILT_Y;
        // For inserted steps (e.g. the UP before a requested Hover) pressure is carried only on contact steps.
        pen.pressure = t is PenTransition.Down or PenTransition.Move ? Math.Clamp(o.Pressure, 1u, PressureCurve.WindowsMax) : 0;
        pen.tiltX = Math.Clamp(o.TiltX, -90, 90);
        pen.tiltY = Math.Clamp(o.TiltY, -90, 90);

        if (InjectSyntheticPointerInput(_device, &info, 1)) return true;
        LastError = Marshal.GetLastWin32Error();
        if (++_failuresLogged <= 50)   // keep the log from overflowing
            Log.Info($"injection failed {LastError}: {t} (requested {o.Transition}, device {DeviceState}) ({o.X},{o.Y}) p={o.Pressure} flags=0x{flags:X}");
        return false;
    }

    public override void Dispose()
    {
        if (_device != IntPtr.Zero) DestroySyntheticPointerDevice(_device);
    }
}
