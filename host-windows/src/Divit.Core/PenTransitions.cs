// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// Transitions the pen device accepts (architecture §4.1). The platform sink turns the requested transition into a
// valid sequence based on the state the device last ACCEPTED. Why: a single injection failure or a session change can
// separate the pipeline's state from the device's. Example: a Move fails during contact and the pipeline asks for
// Hover; Windows still considers the pen in contact and rejects a Hover without UP. With this planner UP is sent
// first, so the pen never stays pressed in Windows (CN-03).

namespace Divit.Core;

public static class PenTransitions
{
    /// <summary>A plan has at most this many steps.</summary>
    public const int MaxSteps = 3;

    /// <summary>
    /// Writes the steps needed to apply <paramref name="requested"/> to a device in state <paramref name="device"/>
    /// into <paramref name="steps"/>; returns the step count. 0 = the device is already there (counts as success).
    /// </summary>
    public static int Plan(PenState device, PenTransition requested, Span<PenTransition> steps)
    {
        int n = 0;
        switch (requested)
        {
            case PenTransition.Hover:
                if (device == PenState.Contact) steps[n++] = PenTransition.Up;
                steps[n++] = PenTransition.Hover;
                break;
            case PenTransition.Down:
            case PenTransition.Move:
                if (device == PenState.Contact) { steps[n++] = PenTransition.Move; break; }
                if (device == PenState.OutOfRange) steps[n++] = PenTransition.Hover;
                steps[n++] = PenTransition.Down;
                break;
            case PenTransition.Up:
                if (device == PenState.Contact) steps[n++] = PenTransition.Up;
                break;
            case PenTransition.Leave:
                if (device == PenState.Contact) steps[n++] = PenTransition.Up;
                if (device != PenState.OutOfRange) steps[n++] = PenTransition.Leave;
                break;
        }
        return n;
    }

    /// <summary>The device state after a transition was accepted.</summary>
    public static PenState After(PenTransition t) => t switch
    {
        PenTransition.Hover or PenTransition.Up => PenState.Hover,
        PenTransition.Down or PenTransition.Move => PenState.Contact,
        _ => PenState.OutOfRange,
    };
}

/// <summary>
/// Base for sinks that drive a stateful pen device (the Windows synthetic pen): tracks the state the device last
/// accepted and turns every request into valid steps with <see cref="PenTransitions.Plan"/>. Subclasses inject a
/// single step. The state belongs to the device and survives session changes. Hot path: no allocation.
/// </summary>
public abstract class PenDeviceSink : IPenSink
{
    /// <summary>The state the device last accepted.</summary>
    public PenState DeviceState { get; private set; }
    /// <summary>Number of requests that did not match the device state and needed extra steps (diagnostics).</summary>
    public long Repairs { get; private set; }

    public bool Apply(in PenOutput o)
    {
        Span<PenTransition> steps = stackalloc PenTransition[PenTransitions.MaxSteps];
        int n = PenTransitions.Plan(DeviceState, o.Transition, steps);
        if (n > 0 && (n != 1 || steps[0] != o.Transition))   // n = 0: device already in the requested state (e.g. Leave in ReleaseAll)
        {
            Repairs++;
            OnRepair(DeviceState, o.Transition, steps[..n]);
        }
        for (int i = 0; i < n; i++)
        {
            if (!Inject(o, steps[i])) return false;
            DeviceState = PenTransitions.After(steps[i]);
        }
        return true;
    }

    /// <summary>Injects a single transition; <paramref name="t"/> is always valid from <see cref="DeviceState"/>.</summary>
    protected abstract bool Inject(in PenOutput o, PenTransition t);

    protected virtual void OnRepair(PenState device, PenTransition requested, ReadOnlySpan<PenTransition> steps) { }

    public abstract void Dispose();
}
