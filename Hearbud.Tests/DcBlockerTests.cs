using System;
using Xunit;

namespace Hearbud.Tests;

/// <summary>Tests for the Phase 2 DC blocker (mix legs only).</summary>
public class DcBlockerTests
{
    [Fact]
    public void Constructor_RejectsBadChannels()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new DcBlocker(0));

    [Fact]
    public void Channels_RoundTrips()
        => Assert.Equal(2, new DcBlocker(2).Channels);

    [Fact]
    public void Filter_ConstantDc_DecaysToZero()
    {
        var dc = new DcBlocker(1);
        float last = 0;
        for (int i = 0; i < 20000; i++) last = dc.Filter(1.0f, 0);
        Assert.True(Math.Abs(last) < 1e-3, $"DC leaked: {last}");
    }

    [Fact]
    public void Filter_Sine_PassesNearlyUntouched()
    {
        var dc = new DcBlocker(1);
        double sumIn = 0, sumOut = 0;
        int n = 0;
        for (int i = 0; i < 4800; i++)
        {
            float x = (float)(0.5 * Math.Sin(2.0 * Math.PI * 440 * i / 48000));
            float y = dc.Filter(x, 0);
            if (i < 480) continue; // let the transient settle
            sumIn += x * x;
            sumOut += y * y;
            n++;
        }
        double ratio = Math.Sqrt(sumOut / n) / Math.Sqrt(sumIn / n);
        Assert.True(ratio > 0.95, $"sine attenuated to {ratio:P1}");
    }

    [Fact]
    public void Filter_Channels_AreIndependent()
    {
        var dc = new DcBlocker(2);
        for (int i = 0; i < 100; i++)
        {
            dc.Filter(1.0f, 0);
            Assert.Equal(0f, dc.Filter(0f, 1));
        }
    }

    [Fact]
    public void Filter_ZeroIn_ZeroOut()
    {
        var dc = new DcBlocker(2);
        Assert.Equal(0f, dc.Filter(0f, 0));
        Assert.Equal(0f, dc.Filter(0f, 1));
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var dc = new DcBlocker(1);
        for (int i = 0; i < 100; i++) dc.Filter(1.0f, 0);
        dc.Reset();
        Assert.Equal(0f, dc.Filter(0f, 0));
    }
}
