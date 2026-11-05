using System;
using Xunit;

namespace Hearbud.Tests;

/// <summary>
/// Tests for the v2 hum watchdog (<see cref="Hearbud.Goertzel"/>,
/// <see cref="Hearbud.HumWatch"/>). Tones are synthesized, so no audio hardware needed.
/// </summary>
public class GoertzelTests
{
    private const int Rate = 48000;

    private static float[] Sine(int freqHz, double amplitude, double seconds)
    {
        int n = (int)(Rate * seconds);
        var s = new float[n];
        for (int i = 0; i < n; i++)
            s[i] = (float)(amplitude * Math.Sin(2.0 * Math.PI * freqHz * i / Rate));
        return s;
    }

    [Fact]
    public void LevelDb_Silence_IsBelowDetectionThreshold()
    {
        var g = new Goertzel(Rate, 60);
        for (int i = 0; i < Rate; i++) g.Push(0f);
        Assert.Equal(Rate, g.SampleCount);
        Assert.True(g.LevelDb() < HumWatch.DetectAboveDb);
    }

    [Fact]
    public void LevelDb_NoSamples_ReturnsFloor()
        => Assert.Equal(-90.0, new Goertzel(Rate, 60).LevelDb());

    [Fact]
    public void LevelDb_OnFrequencyTone_IsHot_OffFrequency_IsCold()
    {
        var tone = Sine(60, 0.5, seconds: 1.0);

        var on = new Goertzel(Rate, 60);
        var off = new Goertzel(Rate, 50);
        foreach (var s in tone) { on.Push(s); off.Push(s); }

        Assert.True(on.LevelDb() > -20.0, $"on-bin {on.LevelDb():F1} dB should be hot");
        Assert.True(off.LevelDb() < -40.0, $"off-bin {off.LevelDb():F1} dB should be cold");
        Assert.True(on.LevelDb() - off.LevelDb() > 20.0, "bins should separate by >20 dB");
    }

    [Fact]
    public void Reset_ClearsAccumulation()
    {
        var g = new Goertzel(Rate, 60);
        foreach (var s in Sine(60, 0.5, seconds: 0.1)) g.Push(s);
        Assert.True(g.SampleCount > 0);
        g.Reset();
        Assert.Equal(0, g.SampleCount);
        Assert.Equal(-90.0, g.LevelDb());
    }

    [Fact]
    public void HumWatch_MainsHum_DetectsExactFrequency()
    {
        var watch = new HumWatch(Rate);
        foreach (var s in Sine(60, 0.3, seconds: 1.0)) watch.Push(s);

        var (detected, freq, level) = watch.Poll();

        Assert.True(detected);
        Assert.Equal(60, freq);
        Assert.True(level > HumWatch.DetectAboveDb);
    }

    [Fact]
    public void HumWatch_50HzHum_Reports50()
    {
        var watch = new HumWatch(Rate);
        foreach (var s in Sine(50, 0.3, seconds: 1.0)) watch.Push(s);

        var (detected, freq, _) = watch.Poll();

        Assert.True(detected);
        Assert.Equal(50, freq);
    }

    [Fact]
    public void HumWatch_CleanSignal_ReportsClean()
    {
        var watch = new HumWatch(Rate);
        // 440 Hz music-ish tone: energy far from any mains bin.
        foreach (var s in Sine(440, 0.3, seconds: 1.0)) watch.Push(s);

        var (detected, _, level) = watch.Poll();

        Assert.False(detected);
        Assert.True(level < HumWatch.DetectAboveDb);
    }

    [Fact]
    public void HumWatch_Poll_ResetsBins()
    {
        var watch = new HumWatch(Rate);
        foreach (var s in Sine(60, 0.3, seconds: 0.5)) watch.Push(s);
        watch.Poll();

        var (detected, _, _) = watch.Poll(); // no new samples → silence floor
        Assert.False(detected);
    }
}
