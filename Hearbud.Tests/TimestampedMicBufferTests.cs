using System;
using System.Diagnostics;
using Xunit;

namespace Hearbud.Tests;

/// <summary>
/// Tests for the Phase 2 timestamped mic buffer (horizon + PLL + fractional pull).
/// Single-threaded and deterministic: QPC values are synthetic tick counts.
/// </summary>
public class TimestampedMicBufferTests
{
    private const int Rate = 48000;
    private const int Ch = 2;

    private static long Ms(double ms) => (long)(ms / 1000.0 * Stopwatch.Frequency);

    private static float[] Ramp(int frames, int ch = Ch)
    {
        var s = new float[frames * ch];
        for (int f = 0; f < frames; f++)
            for (int c = 0; c < ch; c++)
                s[f * ch + c] = f; // value == frame index: makes drops/consumes visible
        return s;
    }

    [Fact]
    public void Constructor_RejectsBadFormat()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimestampedMicBuffer(0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimestampedMicBuffer(48000, 0));
    }

    [Fact]
    public void Read_EmptyBuffer_ZeroFills()
    {
        var b = new TimestampedMicBuffer(Rate, Ch);
        var dst = new float[480 * Ch];
        b.Read(dst, 0, 480, Ms(10_000), out int zeroFilled);
        Assert.Equal(480, zeroFilled);
        Assert.All(dst, v => Assert.Equal(0f, v));
        Assert.Equal(0.0, b.DepthSec);
    }

    [Fact]
    public void Read_AtTargetDepth_IsBitExactPassthrough()
    {
        // Depth exactly 120 ms → zero PLL error → ratio exactly 1.0 → integer
        // cursor → no interpolation. The common locked case must be transparent.
        var b = new TimestampedMicBuffer(Rate, Ch);
        b.Write(Ramp(5760), 5760 * Ch, 0); // 5760 frames == 120 ms @ 48 kHz
        Assert.Equal(1.0, b.Ratio);

        var dst = new float[480 * Ch];
        b.Read(dst, 0, 480, Ms(130), out int zeroFilled);

        Assert.Equal(0, zeroFilled);
        Assert.Equal(1.0, b.Ratio);
        for (int f = 0; f < 480; f++)
            for (int c = 0; c < Ch; c++)
                Assert.Equal(f, dst[f * Ch + c]);
    }

    [Fact]
    public void DepthSec_ReflectsBufferedAudio()
    {
        var b = new TimestampedMicBuffer(Rate, Ch);
        b.Write(Ramp(4800), 4800 * Ch, 0);
        Assert.Equal(0.1, b.DepthSec, precision: 6);
    }

    [Fact]
    public void Read_Partial_RealPrefixThenZeros()
    {
        var b = new TimestampedMicBuffer(Rate, Ch);
        b.Write(Ramp(100), 100 * Ch, 0);
        var dst = new float[480 * Ch];
        b.Read(dst, 0, 480, Ms(130), out int zeroFilled);

        Assert.Equal(380, zeroFilled);
        for (int f = 0; f < 100; f++)
            Assert.Equal(f, dst[f * Ch], precision: 2);
        for (int f = 100; f < 480; f++)
            Assert.Equal(0f, dst[f * Ch]);
    }

    [Fact]
    public void Read_StaleData_DroppedByHorizon()
    {
        var b = new TimestampedMicBuffer(Rate, Ch);
        b.Write(Ramp(4800), 4800 * Ch, 0); // 100 ms of mic at t=0

        // Loop block 1.2 s later: mic is >1 s stale → horizon drops it all.
        var dst = new float[480 * Ch];
        b.Read(dst, 0, 480, Ms(1200), out int zeroFilled);

        Assert.Equal(480, zeroFilled);
        Assert.Equal(0.0, b.DepthSec);
    }

    [Fact]
    public void Read_RecentData_SurvivesHorizon()
    {
        var b = new TimestampedMicBuffer(Rate, Ch);
        b.Write(Ramp(4800), 4800 * Ch, Ms(1100));

        var dst = new float[480 * Ch];
        b.Read(dst, 0, 480, Ms(1200), out int zeroFilled);

        Assert.Equal(0, zeroFilled);
        Assert.Equal(0f, dst[0], precision: 3);
    }

    [Fact]
    public void Read_PartiallyStale_DropsOnlyStalePrefix()
    {
        var b = new TimestampedMicBuffer(Rate, Ch);
        b.Write(Ramp(48000), 48000 * Ch, 0); // 1 s of mic at t=0

        // At loop t=1.5 s the horizon is t=0.5 s: first 24000 frames must go.
        var dst = new float[480 * Ch];
        b.Read(dst, 0, 480, Ms(1500), out int zeroFilled);

        Assert.Equal(0, zeroFilled);
        Assert.InRange(dst[0], 23998.0, 24002.0);
    }

    [Fact]
    public void Pll_DeepBuffer_SpeedsUp_Clamped()
    {
        var b = new TimestampedMicBuffer(Rate, Ch);
        b.Write(Ramp(48000), 48000 * Ch, 0); // 1 s deep vs 120 ms target
        var dst = new float[480 * Ch];

        b.Read(dst, 0, 480, Ms(130), out _);
        Assert.True(b.Ratio > 1.0, $"expected speedup, got {b.Ratio}");

        // Hold depth high; the servo must clamp at MaxRatio, never beyond.
        for (int i = 0; i < 2000; i++)
        {
            b.Write(Ramp(480), 480 * Ch, Ms(130 + i));
            b.Read(dst, 0, 480, Ms(130 + i), out _);
        }
        Assert.Equal(TimestampedMicBuffer.MaxRatio, b.Ratio);
    }

    [Fact]
    public void Pll_ShallowBuffer_SlowsDown_Clamped()
    {
        var b = new TimestampedMicBuffer(Rate, Ch);
        b.Write(Ramp(480), 480 * Ch, 0); // 10 ms vs 120 ms target
        var dst = new float[480 * Ch];

        b.Read(dst, 0, 480, Ms(130), out _);

        Assert.True(b.Ratio < 1.0, $"expected slowdown, got {b.Ratio}");
        Assert.True(b.Ratio >= TimestampedMicBuffer.MinRatio);
    }

    [Fact]
    public void Write_BeyondMaxCapacity_DropsOldestAndCounts()
    {
        var b = new TimestampedMicBuffer(Rate, Ch);
        for (int i = 0; i < 40; i++) // 40 s into a 30 s buffer
            b.Write(Ramp(48000), 48000 * Ch, Ms(i * 1000));

        Assert.True(b.OverflowFramesDropped > 0);
        Assert.True(b.DepthSec <= 30.0 + 1e-6);
    }

    [Fact]
    public void Clear_ResetsEverything()
    {
        var b = new TimestampedMicBuffer(Rate, Ch);
        b.Write(Ramp(48000), 48000 * Ch, 0);
        var dst = new float[480 * Ch];
        b.Read(dst, 0, 480, Ms(130), out _);

        b.Clear();

        Assert.Equal(0.0, b.DepthSec);
        Assert.Equal(1.0, b.Ratio);
        Assert.Equal(0, b.OverflowFramesDropped);
    }

    [Fact]
    public void Read_ConsecutiveReads_AreContinuous()
    {
        var b = new TimestampedMicBuffer(Rate, Ch);
        b.Write(Ramp(5760), 5760 * Ch, 0);
        var d1 = new float[480 * Ch];
        var d2 = new float[480 * Ch];
        b.Read(d1, 0, 480, Ms(130), out _);
        b.Read(d2, 0, 480, Ms(140), out int z2);

        Assert.Equal(0, z2);
        Assert.InRange(d2[0] - (d1[479 * Ch] + 1), -2.0, 2.0);
    }

    [Fact]
    public void Read_ManySmallWrites_PreservesOrder()
    {
        var b = new TimestampedMicBuffer(Rate, Ch);
        for (int i = 0; i < 110; i++) // 10 % headroom: the PLL consumes slightly fast when deep
        {
            var chunk = new float[96 * Ch];
            for (int f = 0; f < 96; f++)
                for (int c = 0; c < Ch; c++)
                    chunk[f * Ch + c] = i * 96 + f;
            b.Write(chunk, chunk.Length, Ms(i * 2)); // realtime-paced 2 ms chunks
        }

        var dst = new float[480 * Ch];
        for (int i = 0; i < 20; i++)
        {
            b.Read(dst, 0, 480, Ms(320 + i * 10), out int z);
            Assert.Equal(0, z);
            Assert.InRange(dst[0], i * 480 - 2.0, i * 480 + 2.0);
        }
    }

    [Fact]
    public void Read_Stereo_KeepsChannelsSeparate()
    {
        var b = new TimestampedMicBuffer(Rate, 2);
        int frames = 5760; // target depth → exact pull
        var s = new float[frames * 2];
        for (int f = 0; f < frames; f++) { s[f * 2] = f; s[f * 2 + 1] = -f; }
        b.Write(s, s.Length, 0);

        var dst = new float[480 * 2];
        b.Read(dst, 0, 480, Ms(130), out int z);

        Assert.Equal(0, z);
        Assert.Equal(100f, dst[100 * 2]);
        Assert.Equal(-100f, dst[100 * 2 + 1]);
    }

    [Fact]
    public void Read_NonPositive_Throws()
    {
        var b = new TimestampedMicBuffer(Rate, Ch);
        var dst = new float[16];
        Assert.Throws<ArgumentOutOfRangeException>(() => b.Read(dst, 0, 0, Ms(0), out _));
    }

    [Fact]
    public void Constructor_RejectsBadQpcFrequency()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new TimestampedMicBuffer(Rate, Ch, 0));

    // ── Phase 4: 100-ns NAudio clock domain (qpcFrequency = 10 MHz) ───────────

    private const double Qpc100Ns = 10_000_000.0;

    [Fact]
    public void Read_100NsDomain_HorizonAndDepthWork()
    {
        var b = new TimestampedMicBuffer(Rate, Ch, Qpc100Ns);
        b.Write(Ramp(4800), 4800 * Ch, 0); // 100 ms at t=0
        Assert.Equal(0.1, b.DepthSec, precision: 6);

        // Read at t=200 ms: fresh (horizon t=-800 ms) → data, no shortfall.
        var dst = new float[480 * Ch];
        b.Read(dst, 0, 480, (long)(0.2 * Qpc100Ns), out int z);
        Assert.Equal(0, z);
        Assert.Equal(0f, dst[0], precision: 2);

        // Read at t=1.5 s: the rest is >1 s stale → horizon drops everything.
        b.Read(dst, 0, 480, (long)(1.5 * Qpc100Ns), out z);
        Assert.Equal(480, z);
        Assert.Equal(0.0, b.DepthSec);
    }
}
