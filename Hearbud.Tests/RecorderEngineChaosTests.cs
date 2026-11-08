using System;
using System.IO;
using System.Reflection;
using Xunit;

namespace Hearbud.Tests;

/// <summary>
/// Chaos / edge-case matrix tests: deterministic simulations of clock drift,
/// jitter bursts, overruns, gain extremes and truncated WAV trailers.
/// Temp dirs only, no audio hardware. Instance tests construct a bare
/// <see cref="RecorderEngine"/> (no devices started) and drive the same
/// private FIFO/trim members the capture callbacks use, via reflection —
/// the same pattern as <c>RecorderEngineDspTests</c>.
/// </summary>
public class RecorderEngineChaosTests : IDisposable
{
    private readonly string _tmp;

    public RecorderEngineChaosTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "hearbud-chaos-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } GC.SuppressFinalize(this); }

    private static readonly Type EngineType = typeof(RecorderEngine);

    private static MethodInfo GetStatic(string name, Type[]? paramTypes = null)
    {
        var m = paramTypes == null
            ? EngineType.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            : EngineType.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static, null, paramTypes, null);
        Assert.True(m != null, $"Private static method '{name}' not found on RecorderEngine.");
        return m!;
    }

    private static MethodInfo GetInstance(string name)
    {
        var m = EngineType.GetMethod(name,
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(m != null, $"Private instance method '{name}' not found on RecorderEngine.");
        return m!;
    }

    private static T Field<T>(object o, string name)
        => (T)EngineType.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;

    private static void SetField(object o, string name, object v)
        => EngineType.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(o, v);

    // ─────────────────────────────────────────────────────────────────────────
    // 1. Drift absorption: mic 0.5% fast, trim must hold the backlog bounded
    //    without ever reaching the 80 ms resync threshold.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DriftAbsorption_HalfPercentFast_BacklogStaysBounded()
    {
        using var engine = new RecorderEngine();
        SetField(engine, "_outChannels", 2);
        SetField(engine, "_outRate", 48000);
        var trim = GetInstance("TryGentleTrim");

        const int ch = 2;
        const int rate = 48000;
        const int frameFloats = 480 * 2; // FixedFramesPerChannel * ch
        var ring = Field<float[]>(engine, "_micRing");

        // Start at the trim target: exactly one fixed frame buffered.
        SetField(engine, "_micR", 0);
        SetField(engine, "_micW", frameFloats % ring.Length);
        SetField(engine, "_micCount", frameFloats);

        double maxBacklog = 0;
        // 600 ticks x 10 ms. Loopback (master clock) consumes one fixed frame
        // per tick; the mic delivers 0.5% extra: +4.8 floats/tick on average.
        int[] surplus = { 5, 5, 5, 5, 4 };
        for (int tick = 0; tick < 600; tick++)
        {
            int count = Field<int>(engine, "_micCount");
            count -= Math.Min(frameFloats, count); // master-clock consumption
            count += frameFloats + surplus[tick % surplus.Length]; // mic-fast inflow
            SetField(engine, "_micCount", count);
            SetField(engine, "_micR", 0);
            SetField(engine, "_micW", count % ring.Length);

            double backlog = (double)count / (ch * rate);
            double outBacklog = (double)trim.Invoke(engine, new object[] { backlog })!;
            maxBacklog = Math.Max(maxBacklog, outBacklog);
        }

        // 30 ms of accumulated drift absorbed; resync (80 ms) never approached.
        Assert.True(maxBacklog < 0.08, $"drift escaped trim: max backlog {maxBacklog:F4}s");
        Assert.True(engine.TrimDropped > 0, "trim never fired over 600 drifted ticks");
        Assert.Equal(0L, Field<long>(engine, "_resyncCount"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 2. Jitter burst: variable-size mic packets in one tick must still emit
    //    a fixed frame, with the shortfall silence-padded and counted.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void JitterBurst_VariablePackets_FixedFrameEmitWithSilencePad()
    {
        using var engine = new RecorderEngine();
        SetField(engine, "_outChannels", 2);
        SetField(engine, "_outRate", 48000);
        SetField(engine, "_preRollRemaining", 0);
        int n = (int)GetInstance("FixedFrameFloats").Invoke(engine, null)!;

        // Loopback side: one complete fixed frame.
        var loopSrc = new float[n];
        for (int i = 0; i < n; i++) loopSrc[i] = 0.1f + i * 0.0001f;
        GetInstance("PushLoopRing").Invoke(engine, new object[] { loopSrc, n });

        // Mic side: jittered bursts arriving in one tick, 640 of 960 floats.
        int[] bursts = { 137, 311, 89, 103 };
        var ring = Field<float[]>(engine, "_micRing");
        int pos = 0;
        foreach (int b in bursts)
        {
            for (int i = 0; i < b; i++)
                ring[(pos + i) % ring.Length] = 0.5f + (pos + i) * 0.0001f;
            pos += b;
        }
        SetField(engine, "_micR", 0);
        SetField(engine, "_micW", pos % ring.Length);
        SetField(engine, "_micCount", pos);

        long underBefore = Field<long>(engine, "_micUnderrunBlocks");
        var loopDst = new float[n];
        var micDst = new float[n];
        bool ok = (bool)GetInstance("TryPopFixedFrame").Invoke(engine, new object[] { loopDst, micDst })!;

        Assert.True(ok);
        for (int i = 0; i < pos; i++)
            Assert.Equal(0.5f + i * 0.0001f, micDst[i], precision: 6);
        for (int i = pos; i < n; i++)
            Assert.Equal(0f, micDst[i]);
        Assert.Equal(loopSrc[0], loopDst[0], precision: 6);
        Assert.True(Field<long>(engine, "_micUnderrunBlocks") > underBefore);
        Assert.Equal(n - pos, Field<long>(engine, "_silencedSamples"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 3. Overrun: demand beyond the bounded cap drops OLDEST, counts, and
    //    keeps the newest samples.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OverrunDropOldest_DropsOldestCountsAndKeepsNewest()
    {
        using var engine = new RecorderEngine();
        SetField(engine, "_outChannels", 2);
        SetField(engine, "_outRate", 48000);
        var ring = Field<float[]>(engine, "_micRing");
        int len = ring.Length;

        int fill = len - 100;
        for (int i = 0; i < fill; i++) ring[i] = i;
        const float sentinel = 123456f;
        ring[fill - 1] = sentinel; // newest-sample marker
        SetField(engine, "_micR", 0);
        SetField(engine, "_micW", fill % len);
        SetField(engine, "_micCount", fill);

        long overBefore = Field<long>(engine, "_micOverrunBlocks");
        GetInstance("EnsureRingCapacity").Invoke(engine, new object[] { 200 });

        // want = fill + 200 = len + 100 > cap = len: drop-oldest path, no growth.
        Assert.Equal(len, Field<float[]>(engine, "_micRing").Length);
        Assert.Equal(100L, Field<long>(engine, "_micOverrunBlocks") - overBefore);
        Assert.Equal(fill - 100, Field<int>(engine, "_micCount"));
        var ringAfter = Field<float[]>(engine, "_micRing");
        int r = Field<int>(engine, "_micR");
        int c = Field<int>(engine, "_micCount");
        Assert.Equal(sentinel, ringAfter[(r + c - 1) % ringAfter.Length], precision: 3);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 4. Gain sweep: per-sample gain x block normalize x soft-clip never
    //    wraps around or escapes [-1, 1] for gains 0..3 (UI range).
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GainSweep_MixScaleAndSoftClip_NoWrapAround()
    {
        var scaleM = GetStatic("ComputeMixScale", new[] { typeof(float) });
        var clipM = GetStatic("SoftClipIfNeeded");
        float scale(float peak) => (float)scaleM.Invoke(null, new object[] { peak })!;
        float clip(float x) => (float)clipM.Invoke(null, new object[] { x })!;

        float[] gains = { 0f, 0.25f, 0.5f, 1f, 1.5f, 2f, 3f };
        float[] probes = { 0f, 0.25f, -0.25f, 0.5f, -0.9f, 1f, -1f, 1.5f, -1.5f, 100f, -100f };
        foreach (float g in gains)
        {
            foreach (float x in probes)
            {
                float a = g * x;                      // per-sample gain (EmitFixedMix)
                float norm = a * scale(MathF.Abs(a)); // block-peak normalize
                float y = clip(norm);                 // limiter
                Assert.True(float.IsFinite(y), $"non-finite output at gain={g} x={x}");
                Assert.InRange(MathF.Abs(y), 0f, 1.001f);
            }
        }

        // Normalize-only-on-overs still holds across the sweep.
        Assert.Equal(1f, scale(0.5f), precision: 6);
        Assert.Equal(1f, scale(1f), precision: 6);
        Assert.Equal(0.5f, scale(2f), precision: 6);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 5. Mux/trailer validity: truncated WAV headers are detected by scan,
    //    reported invalid, and refused by repair (no silent corruption).
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MuxTrailer_WavRepair_FlagsTruncatedHeaders()
    {
        Assert.Empty(WavRepair.ScanDirectory(_tmp));

        string bad = Path.Combine(_tmp, "rec-x-mix.wav");
        File.WriteAllBytes(bad,
            new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0 });

        Assert.False(WavRepair.IsValidWav(bad));
        Assert.Contains(bad, WavRepair.ScanDirectory(_tmp));
        Assert.False(WavRepair.TryRepair(bad)); // no WAVE/data markers: unrepairable
        Assert.False(WavRepair.IsValidWav(Path.Combine(_tmp, "missing.wav")));
    }
}
