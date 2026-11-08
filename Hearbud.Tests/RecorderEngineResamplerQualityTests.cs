// Hearbud.Tests/RecorderEngineResamplerQualityTests.cs
// Pure-DSP validation for the 12-tap Blackman-Harris polyphase resampler
// (RecorderEngine.SincPolyphase + ConvertToTargetChunked).
// No audio hardware, no WASAPI/CSCore device calls, no WaveWriter.
// Reflection-only access — production code needs zero changes.
using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace Hearbud.Tests;

/// <summary>
/// libswresample-class quality bar for the custom resampler:
/// (a) chunked == single-shot bit-identity under random splits;
/// (b) sine-sweep flatness, no alias blowup;
/// (c) DC step / impulse sanity + measured group delay == Half src samples;
/// (d) TotalSrcFrames/TotalDstFrames ratio converges to src/dst;
/// (e) remaining ITU fold spot-checks (3/4/5/8ch, mono→quad).
/// </summary>
public class RecorderEngineResamplerQualityTests
{
    private static readonly Type EngineType = typeof(RecorderEngine);

    private static MethodInfo GetStatic(string name)
    {
        var m = EngineType.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.True(m != null, $"Private static method '{name}' not found on RecorderEngine.");
        return m!;
    }

    private static Type SincType()
    {
        var t = EngineType.GetNestedType("SincPolyphase", BindingFlags.NonPublic | BindingFlags.Public);
        Assert.True(t != null, "Nested type 'SincPolyphase' not found.");
        return t!;
    }

    private static int InvokeConvertToTarget(
        float[] src, int floatCount, CSCore.WaveFormat srcFmt,
        int dstRate, int dstCh, ref float[] dst, ref float[] scratch)
    {
        var args = new object[] { src, floatCount, srcFmt, dstRate, dstCh, dst, scratch };
        int result = (int)GetStatic("ConvertToTarget").Invoke(null, args)!;
        dst = (float[])args[5];
        scratch = (float[])args[6];
        return result;
    }

    private static int InvokeChunked(
        float[] src, int floatCount, CSCore.WaveFormat srcFmt,
        int dstRate, int dstCh, ref float[] dst, ref float[] scratch, object state)
    {
        var args = new object[] { src, floatCount, srcFmt, dstRate, dstCh, dst, scratch, state };
        int result = (int)GetStatic("ConvertToTargetChunked").Invoke(null, args)!;
        dst = (float[])args[5];
        scratch = (float[])args[6];
        return result;
    }

    private static object NewResamplerState(int srcCh, int srcRate, int dstRate)
    {
        var nested = EngineType.GetNestedType("ResamplerState", BindingFlags.NonPublic)!;
        var st = Activator.CreateInstance(nested)!;
        nested.GetMethod("Reset")!.Invoke(st, new object[] { srcCh, srcRate, dstRate });
        return st;
    }

    private static (long totalSrc, long totalDst) Totals(object st)
    {
        var t = st.GetType();
        long s = (long)t.GetField("TotalSrcFrames")!.GetValue(st)!;
        long d = (long)t.GetField("TotalDstFrames")!.GetValue(st)!;
        return (s, d);
    }

    private static float[] RunSingle(float[] src, int srcCh, int srcRate, int dstRate, int dstCh, out int n)
    {
        var fmt = new CSCore.WaveFormat(srcRate, 16, srcCh);
        float[] dst = new float[Math.Max(64, (int)((long)src.Length / Math.Max(1, srcCh) * dstRate / srcRate * dstCh) + 64)];
        float[] scr = new float[dst.Length];
        n = InvokeConvertToTarget(src, src.Length, fmt, dstRate, dstCh, ref dst, ref scr);
        var result = new float[n];
        Array.Copy(dst, result, n);
        return result;
    }

    private static float[] RunChunked(float[] src, int srcCh, int srcRate, int dstRate, int dstCh, int[] splits)
    {
        var fmt = new CSCore.WaveFormat(srcRate, 16, srcCh);
        var st = NewResamplerState(srcCh, srcRate, dstRate);
        var acc = new List<float>();
        int totalFrames = src.Length / srcCh;
        int off = 0;
        foreach (int len in splits)
        {
            int take = Math.Min(len, totalFrames - off);
            if (take <= 0) break;
            var piece = new float[take * srcCh];
            Array.Copy(src, off * srcCh, piece, 0, take * srcCh);
            float[] d = new float[Math.Max(64, (int)((long)take * dstRate / srcRate * dstCh) + 64)];
            float[] s = new float[d.Length];
            int got = InvokeChunked(piece, piece.Length, fmt, dstRate, dstCh, ref d, ref s, st);
            for (int i = 0; i < got; i++) acc.Add(d[i]);
            off += take;
        }
        while (off < totalFrames)
        {
            int take = Math.Min(256, totalFrames - off);
            var piece = new float[take * srcCh];
            Array.Copy(src, off * srcCh, piece, 0, take * srcCh);
            float[] d = new float[Math.Max(64, (int)((long)take * dstRate / srcRate * dstCh) + 64)];
            float[] s = new float[d.Length];
            int got = InvokeChunked(piece, piece.Length, fmt, dstRate, dstCh, ref d, ref s, st);
            for (int i = 0; i < got; i++) acc.Add(d[i]);
            off += take;
        }
        return acc.ToArray();
    }

    private static int[] RandomSplits(Random rnd, int totalFrames, int min = 1, int max = 700)
    {
        var list = new List<int>();
        int left = totalFrames;
        while (left > 0)
        {
            int take = Math.Min(rnd.Next(min, max + 1), left);
            list.Add(take);
            left -= take;
        }
        return list.ToArray();
    }

    private static double MaxAbsDiff(float[] a, float[] b)
    {
        Assert.Equal(a.Length, b.Length);
        double m = 0;
        for (int i = 0; i < a.Length; i++)
            m = Math.Max(m, Math.Abs(a[i] - b[i]));
        return m;
    }

    // ── (a) chunked vs single-shot bit-identity, random splits ──

    [Theory]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(1234)]
    public void ChunkedVsSingle_RandomSplits_Mono16kTo48k_BitIdentical(int seed)
    {
        int srcRate = 16000, dstRate = 48000, srcCh = 1;
        int total = 4800;
        var src = new float[total];
        for (int i = 0; i < total; i++)
            src[i] = (float)(0.7 * Math.Sin(2 * Math.PI * 1000 * i / srcRate)
                           + 0.3 * Math.Sin(2 * Math.PI * 333 * i / srcRate));
        var single = RunSingle(src, srcCh, srcRate, dstRate, 1, out _);
        var chunked = RunChunked(src, srcCh, srcRate, dstRate, 1,
            RandomSplits(new Random(seed), total));
        Assert.Equal(single.Length, chunked.Length);
        Assert.True(MaxAbsDiff(single, chunked) == 0.0,
            $"mono random-split chunked must be bit-identical, maxDiff={MaxAbsDiff(single, chunked)}");
    }

    [Theory]
    [InlineData(11)]
    [InlineData(99)]
    public void ChunkedVsSingle_RandomSplits_Stereo16kTo48k_BitIdentical(int seed)
    {
        int srcRate = 16000, dstRate = 48000, srcCh = 2;
        int frames = 3000;
        var src = new float[frames * srcCh];
        for (int i = 0; i < frames; i++)
        {
            src[i * 2] = (float)Math.Sin(2 * Math.PI * 700 * i / srcRate);
            src[i * 2 + 1] = (float)(0.5 * Math.Sin(2 * Math.PI * 1400 * i / srcRate));
        }
        var single = RunSingle(src, srcCh, srcRate, dstRate, 2, out _);
        var chunked = RunChunked(src, srcCh, srcRate, dstRate, 2,
            RandomSplits(new Random(seed), frames, min: 1, max: 500));
        Assert.Equal(single.Length, chunked.Length);
        Assert.True(MaxAbsDiff(single, chunked) == 0.0,
            $"stereo random-split chunked must be bit-identical, maxDiff={MaxAbsDiff(single, chunked)}");
    }

    [Fact]
    public void ChunkedVsSingle_OneSampleChunks_Mono16kTo48k_BitIdentical()
    {
        int srcRate = 16000, dstRate = 48000;
        int total = 480;
        var src = new float[total];
        for (int i = 0; i < total; i++)
            src[i] = (float)Math.Sin(2 * Math.PI * 500 * i / srcRate);
        var single = RunSingle(src, 1, srcRate, dstRate, 1, out _);
        var ones = new int[total];
        Array.Fill(ones, 1);
        var chunked = RunChunked(src, 1, srcRate, dstRate, 1, ones);
        Assert.Equal(single.Length, chunked.Length);
        Assert.True(MaxAbsDiff(single, chunked) == 0.0,
            $"1-sample chunks must stay bit-identical, maxDiff={MaxAbsDiff(single, chunked)}");
    }

    // ── (b) sine response, no alias blowup ──
    // The 12-tap cutoff=0.90 kernel is flat deep in the passband and rolls off
    // through its transition band (verified against a Python replica of the
    // table: 16k->48k gains 1.000@1k, 0.999@3k, 0.936@5k, 0.793@6k, 0.566@7k).
    // Benign in practice: 16k sources (e.g. Bluetooth HFP mics) are band-limited
    // by the codec anyway, and 44.1k/48k mics stay flat past 10k (see below).
    // 10 kHz at 16 kHz input is ABOVE Nyquist (8 kHz) and aliases to 6 kHz
    // even before resampling, so that case asserts boundedness, not unity gain.

    private static double SteadyRms(float[] y, int skip)
    {
        double sum = 0; int n = 0;
        for (int i = skip; i < y.Length; i++) { sum += (double)y[i] * y[i]; n++; }
        return Math.Sqrt(sum / Math.Max(1, n));
    }

    private static double SineGain16k(int freqHz)
    {
        int srcRate = 16000, dstRate = 48000;
        int total = srcRate;
        var src = new float[total];
        for (int i = 0; i < total; i++)
            src[i] = (float)Math.Sin(2 * Math.PI * freqHz * i / srcRate);
        var y = RunSingle(src, 1, srcRate, dstRate, 1, out _);
        int half = (int)SincType().GetField("Half")!.GetValue(null)!;
        int skip = (int)Math.Ceiling(half * (double)dstRate / srcRate) + dstRate / 100;
        return SteadyRms(y, skip) / (1.0 / Math.Sqrt(2.0));
    }

    [Theory]
    [InlineData(100, 0.97, 1.03)]
    [InlineData(1000, 0.97, 1.03)]
    [InlineData(3000, 0.97, 1.03)]
    [InlineData(5000, 0.90, 0.98)]
    [InlineData(6000, 0.74, 0.85)]
    [InlineData(7000, 0.50, 0.64)]
    public void SineResponse_16kTo48k_MatchesDocumentedBands(int freqHz, double lo, double hi)
        => Assert.InRange(SineGain16k(freqHz), lo, hi);

    [Fact]
    public void SineFlatness_441kTo48k_10kHz_FullBandClean()
    {
        // Music path: 44.1k mic -> 48k mix stays flat past 10 kHz (replica: 0.995).
        int srcRate = 44100, dstRate = 48000;
        var src = new float[srcRate];
        for (int i = 0; i < src.Length; i++)
            src[i] = (float)Math.Sin(2 * Math.PI * 10000 * i / srcRate);
        var y = RunSingle(src, 1, srcRate, dstRate, 1, out _);
        int half = (int)SincType().GetField("Half")!.GetValue(null)!;
        int skip = (int)Math.Ceiling(half * (double)dstRate / srcRate) + dstRate / 100;
        double gain = SteadyRms(y, skip) / (1.0 / Math.Sqrt(2.0));
        Assert.InRange(gain, 0.97, 1.03);
    }

    [Fact]
    public void AboveNyquist_10kHzAt16k_Bounded_NoBlowup()
    {
        int srcRate = 16000, dstRate = 48000;
        int total = srcRate;
        var src = new float[total];
        for (int i = 0; i < total; i++)
            src[i] = (float)Math.Sin(2 * Math.PI * 10000 * i / srcRate);
        var y = RunSingle(src, 1, srcRate, dstRate, 1, out _);
        int half = (int)SincType().GetField("Half")!.GetValue(null)!;
        int skip = (int)Math.Ceiling(half * (double)dstRate / srcRate) + dstRate / 100;
        float peak = 0;
        for (int i = skip; i < y.Length; i++)
            peak = Math.Max(peak, Math.Abs(y[i]));
        Assert.InRange(peak, 0.5f, 1.2f);
    }

    [Fact]
    public void DCSweep_NoAliasBlowup()
    {
        int srcRate = 16000, dstRate = 48000, total = 4000;
        var src = new float[total];
        Array.Fill(src, 1.0f);
        var y = RunSingle(src, 1, srcRate, dstRate, 1, out _);
        int skip = 3 * dstRate / srcRate * 6;
        for (int i = skip; i < y.Length; i++)
            Assert.InRange(y[i], 0.99f, 1.01f);
    }

    // ── (c) DC step / impulse sanity, group delay ──

    [Fact]
    public void DcStep_SettlesWithinTwoDelays_BoundedOvershoot()
    {
        int srcRate = 16000, dstRate = 48000;
        int total = 1600;
        var src = new float[total];
        for (int i = 800; i < total; i++) src[i] = 1.0f;
        var y = RunSingle(src, 1, srcRate, dstRate, 1, out int n);
        int delayDst = (int)Math.Ceiling(6.0 * dstRate / srcRate);
        for (int i = 0; i < delayDst / 2 && i < n; i++)
            Assert.InRange(y[i], -0.05f, 0.05f);
        foreach (var v in y) Assert.InRange(v, -0.15f, 1.15f);
        int edge = (int)((long)800 * dstRate / srcRate) + delayDst;
        for (int i = edge + 2 * delayDst; i < n; i++)
            Assert.InRange(y[i], 0.99f, 1.01f);
    }

    [Fact]
    public void Impulse_FiniteRing_BoundedPeak()
    {
        int srcRate = 16000, dstRate = 48000;
        int total = 1600;
        var src = new float[total];
        src[800] = 1.0f;
        var y = RunSingle(src, 1, srcRate, dstRate, 1, out int n);
        float peak = 0;
        foreach (var v in y) peak = Math.Max(peak, Math.Abs(v));
        Assert.InRange(peak, 0.1f, 3.0f * srcRate / dstRate + 1.0f);
        int delayDst = (int)Math.Ceiling(6.0 * dstRate / srcRate);
        int imax = 0; float m = 0;
        for (int i = 0; i < n; i++)
            if (Math.Abs(y[i]) > m) { m = Math.Abs(y[i]); imax = i; }
        for (int i = imax + 4 * delayDst; i < n; i++)
            Assert.True(Math.Abs(y[i]) < 0.001f, $"ring not decayed at {i}: {y[i]}");
    }

    [Fact]
    public void GroupDelay_MeasuredMatchesHalfSrcSamples()
    {
        int srcRate = 16000, dstRate = 48000;
        int total = srcRate;
        var src = new float[total];
        for (int i = 0; i < total; i++)
            src[i] = (float)Math.Sin(2 * Math.PI * 200 * i / srcRate);
        var y = RunSingle(src, 1, srcRate, dstRate, 1, out int n);
        var st = NewResamplerState(1, srcRate, dstRate);
        double expect = (double)st.GetType().GetProperty("DelayDstFrames")!.GetValue(st)!;
        int skip = (int)expect + 100, len = Math.Min(n - skip - 100, dstRate / 2);
        int best = 0; double bestErr = double.MaxValue;
        for (int d = 0; d <= (int)expect + 8; d++)
        {
            double err = 0;
            for (int i = 0; i < len; i++)
            {
                double ideal = Math.Sin(2 * Math.PI * 200 * (skip + i - d) / dstRate);
                double e = y[skip + i] - ideal;
                err += e * e;
            }
            if (err < bestErr) { bestErr = err; best = d; }
        }
        Assert.InRange(best, (int)expect - 2, (int)expect + 2);
    }

    // ── (d) TotalSrcFrames/TotalDstFrames ratio ──

    [Theory]
    [InlineData(16000, 48000)]
    [InlineData(44100, 48000)]
    [InlineData(48000, 16000)]
    public void TotalsRatio_ConvergesToSrcOverDst(int srcRate, int dstRate)
    {
        var fmt = new CSCore.WaveFormat(srcRate, 16, 1);
        var st = NewResamplerState(1, srcRate, dstRate);
        var rnd = new Random(1234);
        long wantSrc = 0;
        float[] d = new float[4096]; float[] s = new float[4096];
        for (int k = 0; k < 200; k++)
        {
            int frames = rnd.Next(64, 513);
            var piece = new float[frames];
            for (int i = 0; i < frames; i++) piece[i] = (float)(rnd.NextDouble() * 2 - 1);
            InvokeChunked(piece, frames, fmt, dstRate, 1, ref d, ref s, st);
            wantSrc += frames;
        }
        var (ts, td) = Totals(st);
        Assert.Equal(wantSrc, ts);
        double ratio = (double)td / ts;
        double expect = (double)dstRate / srcRate;
        Assert.InRange(ratio, expect * 0.999, expect * 1.001);
        long ideal = (long)Math.Floor((double)wantSrc * dstRate / srcRate);
        Assert.InRange(td, ideal - 8, ideal + 1);
    }

    // ── (e) fold spot-checks not covered by existing tests ──

    private static float[] FoldOnly(int srcCh, float[] frame, int dstCh)
    {
        var fmt = new CSCore.WaveFormat(48000, 16, srcCh);
        float[] dst = new float[16]; float[] scr = new float[16];
        int n = InvokeConvertToTarget(frame, frame.Length, fmt, 48000, dstCh, ref dst, ref scr);
        var result = new float[n];
        Array.Copy(dst, result, n);
        return result;
    }

    private static void AssertFold(float[] frame, int srcCh, int dstCh, float[] expected)
    {
        var got = FoldOnly(srcCh, frame, dstCh);
        Assert.Equal(expected.Length, got.Length);
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], got[i], precision: 4);
    }

    [Fact]
    public void Fold_3ch_ToStereo_CenterAtMinus3dB()
        => AssertFold(new[] { 0.2f, 0.4f, 1.0f }, 3, 2,
            new[] { 0.2f + 0.7071f, 0.4f + 0.7071f });

    [Fact]
    public void Fold_4ch_ToStereo_SurroundsAtMinus6dB_NoCenter()
        => AssertFold(new[] { 0.1f, 0.2f, 0.5f, 0.6f }, 4, 2,
            new[] { 0.1f + 0.5f * 0.5f, 0.2f + 0.5f * 0.6f });

    [Fact]
    public void Fold_4ch_ToMono_AveragesFoldedStereo()
        => AssertFold(new[] { 0.1f, 0.2f, 0.5f, 0.6f }, 4, 1,
            new[] { 0.5f * (0.1f + 0.5f * 0.5f + 0.2f + 0.5f * 0.6f) });

    [Fact]
    public void Fold_5ch_Maps_SL_SR_FromIndex3()
        => AssertFold(new[] { 0.1f, 0.2f, 0.3f, 0.5f, 0.6f }, 5, 2,
            new[] { 0.1f + 0.7071f * 0.3f + 0.5f * 0.5f,
                    0.2f + 0.7071f * 0.3f + 0.5f * 0.6f });

    [Fact]
    public void Fold_8ch_UsesFirst6_IgnoresExtra()
        => AssertFold(new[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 9f, 9f }, 8, 2,
            new[] { 0.1f + 0.7071f * 0.3f + 0.5f * 0.5f,
                    0.2f + 0.7071f * 0.3f + 0.5f * 0.6f });

    [Fact]
    public void Fold_MonoToQuad_DuplicatesFront_ZeroFillsRest()
        => AssertFold(new[] { 0.7f }, 1, 4, new[] { 0.7f, 0.7f, 0f, 0f });
}
