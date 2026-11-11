using System;
using System.Diagnostics;
using System.Reflection;
using Xunit;

namespace Hearbud.Tests;

/// <summary>
/// Pair-by-count sync: the loop quantum clocks the mix, mic is consumed
/// take-oldest. No windowed pull, no stale-drop, no trim — a steady device
/// offset rides as latency. Only sustained sample-count depth past 80 ms
/// resyncs. (Stale-prefix/overlap tests were deleted with the window path.)
/// </summary>
public class RecorderEngineWindowedPullTests
{
    private static readonly Type EngineType = typeof(RecorderEngine);

    private static MethodInfo GetInstance(string name)
    {
        var m = EngineType.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(m != null, $"Private instance method '{name}' not found.");
        return m!;
    }

    private static void SetField(object o, string name, object v)
        => EngineType.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(o, v);

    private static T GetField<T>(object o, string name)
        => (T)EngineType.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;

    private static double Tps => (double)Stopwatch.Frequency / 48000;
    private static int QuantumFloats => 480 * 2; // FixedFramesPerChannel * stereo
    private static double QuantumTicks => QuantumFloats * Tps;

    private static RecorderEngine NewEngine()
    {
        var engine = new RecorderEngine();
        SetField(engine, "_outRate", 48000);
        SetField(engine, "_outChannels", 2);
        return engine;
    }

    private static void FillRing(object engine, string ringName, int r, int count, Func<int, float> value)
    {
        var ring = GetField<float[]>(engine, ringName);
        Assert.True(count <= ring.Length, "test fill exceeds ring");
        for (int i = 0; i < count; i++) ring[(r + i) % ring.Length] = value(i);
    }

    private static void InvokeTicksPush(object engine, double startTick, int frames)
        => GetInstance("MicTicksPush").Invoke(engine, new object[] { startTick, frames });

    private static bool InvokeTryPop(object engine, float[] loopDst, float[] micDst)
        => (bool)GetInstance("TryPopFixedFrame").Invoke(engine, new object[] { loopDst, micDst })!;

    [Fact]
    public void PairByCount_Aligned_ConsumesOldest()
    {
        using var engine = NewEngine();
        double h = 1_000_000_000.0;
        int n = QuantumFloats;
        FillRing(engine, "_loopRing", 0, n, i => 0.1f);
        SetField(engine, "_loopR", 0);
        SetField(engine, "_loopW", n % GetField<float[]>(engine, "_loopRing").Length);
        SetField(engine, "_loopCount", n);
        SetField(engine, "_loopHeadTick", h);
        SetField(engine, "_loopHeadValid", true);

        FillRing(engine, "_micRing", 0, n, i => i);
        SetField(engine, "_micR", 0);
        SetField(engine, "_micW", n % GetField<float[]>(engine, "_micRing").Length);
        SetField(engine, "_micCount", n);
        InvokeTicksPush(engine, h, n);

        var loopDst = new float[n];
        var micDst = new float[n];
        Assert.True(InvokeTryPop(engine, loopDst, micDst));
        for (int i = 0; i < n; i++) Assert.Equal((float)i, micDst[i]);
        Assert.Equal(0, GetField<int>(engine, "_micCount"));
        Assert.Equal(h + QuantumTicks, GetField<double>(engine, "_loopHeadTick"), precision: 3);
    }

    [Fact]
    public void PairByCount_FarAheadWindow_PairsByCount()
    {
        using var engine = NewEngine();
        double h = 4_000_000_000.0;
        int n = QuantumFloats;
        FillRing(engine, "_loopRing", 0, n, i => 0.1f);
        SetField(engine, "_loopR", 0);
        SetField(engine, "_loopW", n);
        SetField(engine, "_loopCount", n);
        SetField(engine, "_loopHeadTick", h + 10 * QuantumTicks); // far ahead
        SetField(engine, "_loopHeadValid", true);

        FillRing(engine, "_micRing", 0, n, i => i);
        SetField(engine, "_micR", 0);
        SetField(engine, "_micW", n);
        SetField(engine, "_micCount", n);
        InvokeTicksPush(engine, h, n);

        var loopDst = new float[n];
        var micDst = new float[n];
        Assert.True(InvokeTryPop(engine, loopDst, micDst));
        // Pair-by-count regardless of stamp lead: take oldest.
        for (int i = 0; i < n; i++) Assert.Equal((float)i, micDst[i]);
    }

    [Fact]
    public void PairByCount_FullyAhead_PairsByCount()
    {
        using var engine = NewEngine();
        double h = 3_000_000_000.0;
        int n = QuantumFloats;
        FillRing(engine, "_loopRing", 0, n, i => 0.1f);
        SetField(engine, "_loopR", 0);
        SetField(engine, "_loopW", n);
        SetField(engine, "_loopCount", n);
        SetField(engine, "_loopHeadTick", h);
        SetField(engine, "_loopHeadValid", true);

        // Mic entirely in the future: pairs by count regardless of stamp
        // lead — a steady delivery offset rides as latency, never cut.
        FillRing(engine, "_micRing", 0, n, i => 0.5f);
        SetField(engine, "_micR", 0);
        SetField(engine, "_micW", n);
        SetField(engine, "_micCount", n);
        InvokeTicksPush(engine, h + 2 * QuantumTicks, n);

        var loopDst = new float[n];
        var micDst = new float[n];
        Assert.True(InvokeTryPop(engine, loopDst, micDst));
        for (int i = 0; i < n; i++) Assert.Equal(0.5f, micDst[i]);
        Assert.Equal(0, GetField<int>(engine, "_micCount")); // consumed take-oldest
        Assert.Equal(0L, GetField<long>(engine, "_micUnderrunBlocks"));
    }

    [Fact]
    public void Resync_FullyAheadWindow_NoTrigger()
    {
        using var engine = NewEngine();
        double h = 5_000_000_000.0;
        int n = QuantumFloats;
        SetField(engine, "_loopHeadTick", h);
        SetField(engine, "_loopHeadValid", true);

        // Even a huge stamp lead with healthy depth (5000 floats ~ 52 ms <
        // 80 ms budget) must not wipe: depth is ground truth, stamps diverge
        // routinely on real hardware. The mic pairs by count instead.
        int fill = 5000;
        var ring = GetField<float[]>(engine, "_micRing");
        Assert.True(fill < ring.Length);
        FillRing(engine, "_micRing", 0, fill, i => 0.25f);
        SetField(engine, "_micR", 0);
        SetField(engine, "_micW", fill);
        SetField(engine, "_micCount", fill);
        InvokeTicksPush(engine, h + 10 * QuantumTicks, fill); // fully ahead

        var resync = GetInstance("CheckLagResync");
        for (int i = 0; i < 10; i++)
            Assert.False((bool)resync.Invoke(engine, new object[] { 0.0 })!);
        Assert.Equal(fill, GetField<int>(engine, "_micCount"));
        Assert.Equal(0L, GetField<long>(engine, "_resyncCount"));
    }

    [Fact]
    public void Resync_SmallAheadWindow_NoTrigger()
    {
        using var engine = NewEngine();
        double h = 5_500_000_000.0;
        int n = QuantumFloats;
        SetField(engine, "_loopHeadTick", h);
        SetField(engine, "_loopHeadValid", true);

        // Lead of 2 quanta (1920 floats) is inside the 80 ms budget:
        // steady delivery offset, pairs by count, never wipes.
        int fill = 5000;
        var ring = GetField<float[]>(engine, "_micRing");
        Assert.True(fill < ring.Length);
        FillRing(engine, "_micRing", 0, fill, i => 0.25f);
        SetField(engine, "_micR", 0);
        SetField(engine, "_micW", fill);
        SetField(engine, "_micCount", fill);
        InvokeTicksPush(engine, h + 2 * QuantumTicks, fill); // fully ahead, small lead

        var resync = GetInstance("CheckLagResync");
        for (int i = 0; i < 10; i++)
            Assert.False((bool)resync.Invoke(engine, new object[] { 0.0 })!);
        Assert.Equal(fill, GetField<int>(engine, "_micCount"));
        Assert.Equal(0L, GetField<long>(engine, "_resyncCount"));
    }

    [Fact]
    public void TicksPushConsume_TrackedCountMatchesRing()
    {
        using var engine = NewEngine();
        double h = 6_000_000_000.0;
        SetField(engine, "_micR", 0);
        SetField(engine, "_micW", 0);
        SetField(engine, "_micCount", 960);
        InvokeTicksPush(engine, h, 960);
        GetInstance("MicTicksConsume").Invoke(engine, new object[] { 100 });
        SetField(engine, "_micCount", 860);
        Assert.True((bool)GetInstance("MicTicksCoverRing_NeedsLock").Invoke(engine, null)!);
    }
}
