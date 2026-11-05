using System;
using System.Diagnostics;
using Xunit;

namespace Hearbud.Tests;

/// <summary>
/// Tests for the v2 health-supervision helpers (<see cref="Hearbud.EngineHealth"/>).
/// All cases are hardware-free: pure math plus event wiring.
/// </summary>
public class EngineHealthTests
{
    [Fact]
    public void IntervalMs_IsOneSecond()
        => Assert.Equal(1000.0, EngineHealth.IntervalMs);

    [Fact]
    public void ComputeDriftMs_InSync_ReturnsZero()
    {
        // 1 s of wall clock, exactly 1 s of stream: no drift.
        long qpcStart = 1000;
        long qpcNow = qpcStart + Stopwatch.Frequency;
        double drift = EngineHealth.ComputeDriftMs(qpcStart, qpcNow, 48000, 48000);
        Assert.Equal(0.0, drift, precision: 6);
    }

    [Fact]
    public void ComputeDriftMs_StreamLagging_ReturnsPositiveMs()
    {
        // 1 s wall, 0.5 s stream → stream lags by +500 ms.
        long qpcStart = 0;
        long qpcNow = Stopwatch.Frequency;
        double drift = EngineHealth.ComputeDriftMs(qpcStart, qpcNow, 24000, 48000);
        Assert.Equal(500.0, drift, precision: 3);
    }

    [Fact]
    public void ComputeDriftMs_StreamAhead_ReturnsNegativeMs()
    {
        // 1 s wall, 2 s stream → stream ahead by −1000 ms.
        long qpcStart = 0;
        long qpcNow = Stopwatch.Frequency;
        double drift = EngineHealth.ComputeDriftMs(qpcStart, qpcNow, 96000, 48000);
        Assert.Equal(-1000.0, drift, precision: 3);
    }

    [Fact]
    public void ComputeDriftMs_TypicalCrystalDrift_IsSmall()
    {
        // ±50 ppm over an hour ≈ ±180 ms. Simulate +50 ppm (stream slow).
        long qpcStart = 0;
        long qpcNow = Stopwatch.Frequency * 3600L;
        long streamFrames = (long)(48000L * 3600 * (1.0 - 50e-6));
        double drift = EngineHealth.ComputeDriftMs(qpcStart, qpcNow, streamFrames, 48000);
        Assert.InRange(drift, 170.0, 190.0);
    }

    [Theory]
    [InlineData(0)]    // zero rate
    [InlineData(-48000)] // negative rate
    public void ComputeDriftMs_DegenerateRate_ReturnsZero(int rate)
        => Assert.Equal(0.0, EngineHealth.ComputeDriftMs(0, Stopwatch.Frequency, 48000, rate));

    [Fact]
    public void ComputeDriftMs_NegativeFrames_ReturnsZero()
        => Assert.Equal(0.0, EngineHealth.ComputeDriftMs(0, Stopwatch.Frequency, -1, 48000));

    [Fact]
    public void ComputeDriftMs_ReversedQpc_ReturnsZero()
        => Assert.Equal(0.0, EngineHealth.ComputeDriftMs(Stopwatch.Frequency, 0, 48000, 48000));

    [Fact]
    public void HealthReport_RoundTripsValues()
    {
        var ts = DateTime.UtcNow;
        var r = new HealthReport(7, 3, 0.11, 0.2, 900, 1.5, ts);
        Assert.Equal(7, r.DroppedBlocks);
        Assert.Equal(3, r.MicUnderrunBlocks);
        Assert.Equal(0.11, r.MicBacklogSec, precision: 6);
        Assert.Equal(0.2, r.MicBacklogMaxSec, precision: 6);
        Assert.Equal(900, r.LoopBlocks);
        Assert.Equal(1.5, r.DriftMs, precision: 6);
        Assert.Equal(ts, r.TimestampUtc);
    }

    [Fact]
    public void Health_CanSubscribeAndUnsubscribe()
    {
        using var engine = new RecorderEngine();
        EventHandler<HealthReport>? handler = (_, _) => { };
        engine.Health += handler;
        engine.Health -= handler;
    }

    [Fact]
    public void HealthReport_HumFields_DefaultToClean()
    {
        var r = new HealthReport(0, 0, 0, 0, 0, 0, DateTime.UtcNow);
        Assert.False(r.HumDetected);
        Assert.Equal(0, r.HumFreqHz);
    }

    [Fact]
    public void HealthReport_HumFields_RoundTrip()
    {
        var r = new HealthReport(0, 0, 0, 0, 0, 0, DateTime.UtcNow, HumDetected: true, HumFreqHz: 60);
        Assert.True(r.HumDetected);
        Assert.Equal(60, r.HumFreqHz);
    }

    // ── Phase 1: hum wiring (MaybeEmitHealth via reflection, no hardware) ────

    private static float[] Sine60(int rate = 48000)
    {
        var s = new float[rate];
        for (int i = 0; i < s.Length; i++)
            s[i] = (float)(0.3 * Math.Sin(2.0 * Math.PI * 60 * i / rate));
        return s;
    }

    private static void SetField(object target, string name, object? value)
        => typeof(RecorderEngine).GetField(name,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(target, value);

    private static HealthReport EmitHealth(RecorderEngine engine, double backlogSec = 0.05)
    {
        SetField(engine, "_lastHealthTick", 0L); // force past the 1 s throttle
        SetField(engine, "_monitoring", true);
        HealthReport? got = null;
        engine.Health += (_, r) => got = r;
        typeof(RecorderEngine).GetMethod("MaybeEmitHealth",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(engine, new object[] { System.Diagnostics.Stopwatch.GetTimestamp(), backlogSec });
        Assert.NotNull(got);
        return got!;
    }

    [Fact]
    public void MaybeEmitHealth_MixLegHum_ReportsHum()
    {
        using var engine = new RecorderEngine();
        var watch = new HumWatch(48000);
        foreach (var s in Sine60()) watch.Push(s);
        SetField(engine, "_humWatchLoop", watch);

        var r = EmitHealth(engine);

        Assert.True(r.HumDetected);
        Assert.Equal(60, r.HumFreqHz);
    }

    // (Phase 4: the separate mic-leg hum fields are gone — the single mixer
    // thread feeds one detector with the mix, which always contains the mic.)

    [Fact]
    public void MaybeEmitHealth_Clean_ReportsClean()
    {
        using var engine = new RecorderEngine();
        SetField(engine, "_humWatchLoop", new HumWatch(48000)); // no samples → floor

        var r = EmitHealth(engine);

        Assert.False(r.HumDetected);
    }
}
