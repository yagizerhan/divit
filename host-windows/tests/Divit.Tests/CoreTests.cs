// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors

using Divit.Core;
using Divit.Protocol;
using Xunit;

namespace Divit.Tests;

public class CoreTests
{
    private sealed class RecordingSink : IPenSink
    {
        public readonly List<PenOutput> Out = new();
        public bool Fail;
        public bool Apply(in PenOutput o) { Out.Add(o); return !Fail; }
        public void Dispose() { }
        public string Trace => string.Join(" ", Out.Select(o => o.Transition));
    }

    private static readonly ScreenRect Fhd = new(0, 0, 1920, 1080);

    private static PenPipeline Pipe(RecordingSink sink) =>
        new(sink, new PipelineSettings(new AreaMapping(Fhd, NormRect.Full, 16f / 9, keepAspect: true), new PressureCurve()));

    private static ushort _seq;
    private static PenSample S(PenFlags f, float u = 0.5f, float v = 0.5f, float p = 0.35f) => new(0, u, v, p, 0, 0, f, _seq++);
    private const PenFlags R = PenFlags.InRange, C = PenFlags.InRange | PenFlags.InContact;

    [Fact]
    public void StrokeProducesHoverDownMoveUpLeave()
    {
        var sink = new RecordingSink(); var p = Pipe(sink);
        foreach (var f in new[] { R, R, C, C, R, PenFlags.None }) p.Process(S(f), 0, null);
        Assert.Equal("Hover Hover Down Move Up Leave", sink.Trace);
        Assert.Equal(PenState.OutOfRange, p.State);
    }

    [Fact]
    public void ContactStraightFromOutOfRangeInsertsHover()
    {
        var sink = new RecordingSink(); var p = Pipe(sink);
        p.Process(S(C), 0, null);
        Assert.Equal("Hover Down", sink.Trace);
    }

    [Fact]
    public void LiftOutOfRangeDuringContactSendsUpThenLeave()
    {
        var sink = new RecordingSink(); var p = Pipe(sink);
        p.Process(S(C), 0, null); p.Process(S(PenFlags.None), 0, null);
        Assert.Equal("Hover Down Up Leave", sink.Trace);
    }

    [Fact]
    public void ReleaseAllNeverLeavesPenDown()
    {
        var sink = new RecordingSink(); var p = Pipe(sink);
        p.Process(S(C), 0, null); p.Process(S(C), 0, null);
        sink.Out.Clear();
        p.ReleaseAll();
        Assert.Equal("Up Leave", sink.Trace);
        sink.Out.Clear();
        p.ReleaseAll();
        Assert.Equal("Leave", sink.Trace);   // always: the device sink ignores it when already out of range
    }

    [Fact]
    public void InjectFailureKeepsStateAndRetries()
    {
        var sink = new RecordingSink(); var p = Pipe(sink);
        p.Process(S(R), 0, null);
        sink.Fail = true; p.Process(S(C), 0, null);
        Assert.Equal(PenState.Hover, p.State);   // the sink's last accepted transition
        Assert.Equal(1, p.InjectFailures);
        sink.Fail = false; sink.Out.Clear();
        p.Process(S(C), 0, null);
        Assert.Equal("Down", sink.Trace);         // the same transition is retried; the stroke does not break
    }

    [Fact]
    public void FailedUpIsRetriedOnNextSample()
    {
        var sink = new RecordingSink(); var p = Pipe(sink);
        p.Process(S(C), 0, null);
        sink.Fail = true; p.Process(S(R), 0, null);
        Assert.Equal(PenState.Contact, p.State);
        sink.Fail = false; sink.Out.Clear();
        p.Process(S(R), 0, null);
        Assert.Equal("Up", sink.Trace);
    }

    // ---- Device state guard (PenDeviceSink + PenTransitions): a fake device that rejects invalid transitions like Windows ----

    /// <summary>Like the Windows synthetic pen: accepts only valid transitions and can be told to reject the next ones.</summary>
    private sealed class StrictDevice : PenDeviceSink
    {
        public PenState Windows;              // the device's own (real) state
        public int FailNext;
        public readonly List<PenTransition> Injected = new();
        public string Trace => string.Join(" ", Injected);

        protected override bool Inject(in PenOutput o, PenTransition t)
        {
            if (FailNext > 0) { FailNext--; return false; }
            bool valid = (Windows, t) switch
            {
                (PenState.OutOfRange, PenTransition.Hover) => true,
                (PenState.Hover, PenTransition.Hover or PenTransition.Down or PenTransition.Leave) => true,
                (PenState.Contact, PenTransition.Move or PenTransition.Up) => true,
                _ => false,
            };
            Assert.True(valid, $"invalid transition: {Windows} → {t}");
            Injected.Add(t);
            Windows = PenTransitions.After(t);
            return true;
        }

        public override void Dispose() { }
    }

    [Theory]
    [InlineData(PenState.OutOfRange, PenTransition.Hover, "Hover")]
    [InlineData(PenState.Contact, PenTransition.Hover, "Up Hover")]
    [InlineData(PenState.OutOfRange, PenTransition.Down, "Hover Down")]
    [InlineData(PenState.Hover, PenTransition.Move, "Down")]
    [InlineData(PenState.Contact, PenTransition.Down, "Move")]
    [InlineData(PenState.Hover, PenTransition.Up, "")]
    [InlineData(PenState.Contact, PenTransition.Leave, "Up Leave")]
    [InlineData(PenState.OutOfRange, PenTransition.Leave, "")]
    public void PlanMakesEveryRequestValid(PenState device, PenTransition requested, string expected)
    {
        Span<PenTransition> steps = stackalloc PenTransition[PenTransitions.MaxSteps];
        int n = PenTransitions.Plan(device, requested, steps);
        Assert.Equal(expected, string.Join(" ", steps[..n].ToArray()));
    }

    [Fact]
    public void FailedMoveDuringContactNeverLeavesPenDown()
    {
        // A Move is rejected during contact, then the tablet disconnects: the pen must be lifted in Windows.
        var dev = new StrictDevice(); var p = new PenPipeline(dev, Pipe(new RecordingSink()).Settings);
        p.Process(S(C), 0, null); p.Process(S(C), 0, null);
        dev.FailNext = 1; p.Process(S(C), 0, null);
        p.ReleaseAll();
        Assert.Equal(PenState.OutOfRange, dev.Windows);
        Assert.Equal("Hover Down Move Up Leave", dev.Trace);
    }

    [Fact]
    public void NewSessionRepairsDeviceLeftInContact()
    {
        // In the previous session's ReleaseAll both the UP and Leave's UP were rejected (the device stayed in contact).
        // (One rejection is not enough: the unconditional Leave in ReleaseAll repairs a rejected UP right away.)
        var dev = new StrictDevice();
        var old = new PenPipeline(dev, Pipe(new RecordingSink()).Settings);
        old.Process(S(C), 0, null);
        dev.FailNext = 2; old.ReleaseAll();
        Assert.Equal(PenState.Contact, dev.Windows);

        // The new session's pipeline starts out of range.
        var fresh = new PenPipeline(dev, Pipe(new RecordingSink()).Settings);
        dev.Injected.Clear();
        long repairs = dev.Repairs;
        fresh.Process(S(R), 0, null);
        Assert.Equal("Up Hover", dev.Trace);    // lift first, then hover: no invalid transition
        Assert.Equal(repairs + 1, dev.Repairs);
    }

    [Fact]
    public void SingleRejectedUpOnReleaseIsRepairedByLeave()
    {
        var dev = new StrictDevice(); var p = new PenPipeline(dev, Pipe(new RecordingSink()).Settings);
        p.Process(S(C), 0, null);
        dev.FailNext = 1; p.ReleaseAll();
        Assert.Equal(PenState.OutOfRange, dev.Windows);
    }

    private sealed class Actions : IButtonActions
    {
        public int RightClicks; public readonly List<string> Keys = new();
        public void RightClick() => RightClicks++;
        public void ShortcutDown(int keys) => Keys.Add($"down {keys:X}");
        public void ShortcutUp(int keys) => Keys.Add($"up {keys:X}");
    }

    private static PenPipeline PipeWith(RecordingSink sink, ButtonSettings b, Actions a) =>
        new(sink, new PipelineSettings(new AreaMapping(Fhd, NormRect.Full, 16f / 9, true), new PressureCurve(), b), a);

    private const PenFlags RB = R | PenFlags.Barrel, CB = C | PenFlags.Barrel;

    [Fact]
    public void BarrelAsPenButtonPassesThrough()
    {
        var sink = new RecordingSink(); var p = PipeWith(sink, new ButtonSettings(BarrelAction.PenButton), new Actions());
        p.Process(S(RB), 0, null); p.Process(S(CB), 0, null);
        Assert.All(sink.Out, o => Assert.True(o.Barrel));
    }

    [Fact]
    public void BarrelAsEraserIsLatchedForWholeStroke()
    {
        var sink = new RecordingSink(); var p = PipeWith(sink, new ButtonSettings(BarrelAction.Eraser), new Actions());
        p.Process(S(RB), 0, null);   // button held in the air → the eraser tip approaches
        p.Process(S(CB), 0, null);   // contact: eraser
        p.Process(S(C), 0, null);    // button released mid-stroke → still the eraser
        p.Process(S(R), 0, null);    // lift: Up as eraser, then pen
        p.Process(S(R), 0, null);
        Assert.Equal(new[] { true, true, true, true, false }, sink.Out.Select(o => o.Eraser).ToArray());
        Assert.All(sink.Out, o => Assert.False(o.Barrel));
    }

    [Fact]
    public void BarrelAsRightClickFiresOncePerPressWhileHovering()
    {
        var sink = new RecordingSink(); var a = new Actions(); var p = PipeWith(sink, new ButtonSettings(BarrelAction.RightClick), a);
        foreach (var f in new[] { R, RB, RB, RB, R, RB, CB }) p.Process(S(f), 0, null);
        Assert.Equal(2, a.RightClicks);                     // two separate presses; holding does not click again
        Assert.All(sink.Out, o => Assert.False(o.Barrel));
    }

    [Fact]
    public void BarrelAsShortcutHoldsKeysWhileButtonIsDown()
    {
        const int ctrlZ = 0x20000 | 0x5A;
        var sink = new RecordingSink(); var a = new Actions(); var p = PipeWith(sink, new ButtonSettings(BarrelAction.Shortcut, ctrlZ), a);
        foreach (var f in new[] { R, RB, RB, CB, CB, RB }) p.Process(S(f), 0, null);
        Assert.Equal(new[] { "down 2005A" }, a.Keys);          // held through hover and a stroke
        p.Process(S(R), 0, null);
        p.Process(S(CB), 0, null);                              // pressed again while touching
        Assert.Equal(new[] { "down 2005A", "up 2005A", "down 2005A" }, a.Keys);
        Assert.All(sink.Out, o => Assert.False(o.Barrel));
    }

    [Fact]
    public void HeldShortcutIsReleasedWhenPenLeavesRange()
    {
        const int space = 0x20;
        var a = new Actions(); var p = PipeWith(new RecordingSink(), new ButtonSettings(BarrelAction.Shortcut, space), a);
        p.Process(S(RB), 0, null);
        p.Process(S(PenFlags.Barrel), 0, null);                 // out of range with the button still down
        Assert.Equal(new[] { "down 20", "up 20" }, a.Keys);
    }

    [Fact]
    public void HeldShortcutIsReleasedOnDisconnect()
    {
        const int space = 0x20;
        var a = new Actions(); var p = PipeWith(new RecordingSink(), new ButtonSettings(BarrelAction.Shortcut, space), a);
        p.Process(S(CB), 0, null);
        p.ReleaseAll();
        p.ReleaseAll();                                         // only once
        Assert.Equal(new[] { "down 20", "up 20" }, a.Keys);
    }

    [Fact]
    public void HeldShortcutIsReleasedWithTheKeysItPressed()
    {
        const int space = 0x20, alt = 0x12;
        var a = new Actions(); var p = PipeWith(new RecordingSink(), new ButtonSettings(BarrelAction.Shortcut, space), a);
        p.Process(S(RB), 0, null);
        p.UpdateSettings(p.Settings with { Button = new ButtonSettings(BarrelAction.Shortcut, alt) });
        p.Process(S(R), 0, null);
        p.UpdateSettings(p.Settings with { Button = new ButtonSettings(BarrelAction.Shortcut, space) });
        p.Process(S(RB), 0, null);
        p.UpdateSettings(p.Settings with { Button = new ButtonSettings(BarrelAction.PenButton) });   // assignment changed mid-hold
        p.Process(S(RB), 0, null);
        Assert.Equal(new[] { "down 20", "up 20", "down 20", "up 20" }, a.Keys);
    }

    [Fact]
    public void SeqGapsAreCountedAcrossWrap()
    {
        var p = Pipe(new RecordingSink());
        foreach (ushort q in new ushort[] { 65534, 65535, 0, 1, 5 }) p.Process(new PenSample(0, .5f, .5f, 0, 0, 0, R, q), 0, null);
        Assert.Equal(1, p.SeqGaps);
    }

    [Theory]
    [InlineData(0f, true, 1u)]          // at least 1 in contact
    [InlineData(0.35f, true, 512u)]
    [InlineData(0.70f, true, 1024u)]
    [InlineData(0.95f, true, 1024u)]    // above the upper threshold is clamped
    [InlineData(0.5f, false, 0u)]       // no pressure while hovering
    public void DefaultCurveSaturatesAtSeventyPercent(float pIn, bool contact, uint expected) =>
        Assert.Equal(expected, new PressureCurve().Map(pIn, contact));

    [Fact]
    public void KeepAspectCropsSixteenTenToSixteenNine()
    {
        // Tab S9 FE+ 16:10 → 16:9 monitor: top/bottom are cropped (architecture §4.2).
        var m = new AreaMapping(Fhd, NormRect.Full, 16f / 10, keepAspect: true);
        Assert.Equal(0f, m.EffectiveArea.Left, 4); Assert.Equal(1f, m.EffectiveArea.Right, 4);
        Assert.Equal(0.05f, m.EffectiveArea.Top, 4); Assert.Equal(0.95f, m.EffectiveArea.Bottom, 4);
        Assert.Equal((0, 0), m.Map(0f, 0.05f));
        Assert.Equal((1919, 1079), m.Map(1f, 0.95f));
        Assert.Equal((0, 0), m.Map(-0.01f, 0f));        // the dead zone is clamped to the edge
    }

    [Fact]
    public void MappingHonoursMonitorOffset()
    {
        var m = new AreaMapping(new ScreenRect(-2560, 0, 2560, 1440), NormRect.Full, 16f / 9, keepAspect: true);
        Assert.Equal((-2560, 0), m.Map(0, 0));
        Assert.Equal((-1, 1439), m.Map(1, 1));
    }

    [Fact]
    public void ClockSyncUsesLowestRtt()
    {
        var c = new ClockSync();
        // the tablet clock is 1 000 000 µs behind the host; the first measurement has asymmetric delay (high RTT).
        c.Add(t0: 0, t1: -1_000_000 + 5000, t2: -1_000_000 + 5010, t3: 6000);
        c.Add(t0: 10_000, t1: -990_000 + 500, t2: -990_000 + 510, t3: 11_010);
        Assert.Equal(1_000_000, c.OffsetUs);
        Assert.Equal(1000, c.RttUs);
    }

    [Fact]
    public void HistogramPercentiles()
    {
        var h = new LatencyHistogram();
        for (int i = 0; i < 95; i++) h.Add(1000);
        for (int i = 0; i < 5; i++) h.Add(9000);
        Assert.Equal(1050, h.Percentile(50));
        Assert.Equal(1050, h.Percentile(95));
        Assert.Equal(9050, h.Percentile(99));
    }
}
