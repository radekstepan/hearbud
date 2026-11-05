using System;
using Xunit;

namespace Hearbud.Tests;

/// <summary>Tests for the Phase 4 mixer DSP (gain → DC → limit + meters + hum).</summary>
public class MixerDspTests
{
    private static (DcBlocker, DcBlocker) Dcs() => (new DcBlocker(2), new DcBlocker(2));

    [Fact]
    public void ProcessMixBlock_Silence_StaysSilent()
    {
        var (dcS, dcM) = Dcs();
        var sys = new float[8];
        var mic = new float[8];
        var mix = new float[8];
        MixerDsp.ProcessMixBlock(sys, mic, mix, 8, 2, 1.0, 1.0, dcS, dcM, null,
            out float sysRms, out float sysPeak, out bool sysCl,
            out float micRms, out float micPeak, out bool micCl);

        Assert.All(mix, v => Assert.Equal(0f, v));
        Assert.Equal(0f, sysRms);
        Assert.Equal(0f, sysPeak);
        Assert.False(sysCl);
        Assert.Equal(0f, micRms);
        Assert.Equal(0f, micPeak);
        Assert.False(micCl);
    }

    [Fact]
    public void ProcessMixBlock_QuietPassthrough_IsTransparent()
    {
        var (dcS, dcM) = Dcs();
        // First samples: DC filter passes x through (zero state), knee untouched.
        var sys = new float[] { 0.4f, 0.4f };
        var mic = new float[] { 0f, 0f };
        var mix = new float[2];
        MixerDsp.ProcessMixBlock(sys, mic, mix, 2, 2, 1.0, 1.0, dcS, dcM, null,
            out _, out _, out _, out _, out _, out _);

        Assert.Equal(0.2f, mix[0], precision: 6);
        Assert.Equal(0.2f, mix[1], precision: 6);
    }

    [Fact]
    public void ProcessMixBlock_GainsAffectMixAndMeters()
    {
        var (dcS, dcM) = Dcs();
        var sys = new float[] { 0.25f, 0.25f };
        var mic = new float[] { 0.125f, 0.125f };
        var mix = new float[2];
        MixerDsp.ProcessMixBlock(sys, mic, mix, 2, 2, 2.0, 4.0, dcS, dcM, null,
            out float sysRms, out float sysPeak, out _,
            out float micRms, out float micPeak, out _);

        // s=0.5, m=0.5 → mix 0.5 (below knee, DC transparent on first samples).
        Assert.Equal(0.5f, mix[0], precision: 5);
        Assert.Equal(0.5f, sysRms, precision: 5);
        Assert.Equal(0.5f, sysPeak, precision: 5);
        Assert.Equal(0.5f, micRms, precision: 5);
        Assert.Equal(0.5f, micPeak, precision: 5);
    }

    [Fact]
    public void ProcessMixBlock_MetersReportRmsPeakAndClip()
    {
        var (dcS, dcM) = Dcs();
        var sys = new float[] { 1.0f, -1.0f, 0.5f, 0f };
        var mic = new float[] { 1.5f, 0f, 0f, 0f };
        var mix = new float[4];
        MixerDsp.ProcessMixBlock(sys, mic, mix, 4, 2, 1.0, 1.0, dcS, dcM, null,
            out float sysRms, out float sysPeak, out bool sysCl,
            out _, out float micPeak, out bool micCl);

        Assert.Equal(MathF.Sqrt(2.25f / 4), sysRms, precision: 4);
        Assert.Equal(1.0f, sysPeak, precision: 5);
        Assert.False(sysCl); // exactly 1.0 is not over
        Assert.Equal(1.5f, micPeak, precision: 5);
        Assert.True(micCl);
    }

    [Fact]
    public void ProcessMixBlock_HotMix_IsLimitedNotClipped()
    {
        var (dcS, dcM) = Dcs();
        var sys = new float[] { 1.0f, 1.0f };
        var mic = new float[] { 1.0f, 1.0f };
        var mix = new float[2];
        MixerDsp.ProcessMixBlock(sys, mic, mix, 2, 2, 1.0, 1.0, dcS, dcM, null,
            out _, out _, out _, out _, out _, out _);

        // (1+1)/2 = 1.0 → kneed to ~0.939, never hard-clipped.
        Assert.InRange(mix[0], 0.93f, 0.95f);
    }

    [Fact]
    public void ProcessMixBlock_HumTap_ObservesTheMix()
    {
        var (dcS, dcM) = Dcs();
        var hum = new HumWatch(48000);
        var sys = new float[48000];
        var mic = new float[48000];
        var mix = new float[48000];
        for (int i = 0; i < sys.Length; i++)
            sys[i] = (float)(0.3 * Math.Sin(2.0 * Math.PI * 60 * i / 48000));
        MixerDsp.ProcessMixBlock(sys, mic, mix, sys.Length, 1, 1.0, 1.0, dcS, dcM, hum,
            out _, out _, out _, out _, out _, out _);

        var (detected, freq, _) = hum.Poll();
        Assert.True(detected);
        Assert.Equal(60, freq);
    }
}
