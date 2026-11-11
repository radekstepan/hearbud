using System;
using System.Reflection;
using System.Threading;
using Xunit;

namespace Hearbud.Tests;

/// <summary>
/// OBS_LESSONS 3.3: decoupled mixer thread. Lifecycle is idempotent and
/// safe without devices; the tick drains loop quanta exactly like the old
/// callback-embedded path (emits guard on _recording internally).
/// </summary>
public class RecorderEngineMixerTests
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

    private static bool WaitFor(Func<bool> cond, int timeoutMs = 2000)
    {
        int waited = 0;
        while (!cond() && waited < timeoutMs) { Thread.Sleep(20); waited += 20; }
        return cond();
    }

    [Fact]
    public void Mixer_StartStop_Idempotent()
    {
        using var engine = new RecorderEngine();
        var start = GetInstance("EnsureMixerRunning");
        var stop = GetInstance("StopMixer");
        start.Invoke(engine, null);
        Assert.True(WaitFor(() =>
        {
            var t = GetField<Thread?>(engine, "_mixerThread");
            return t != null && t.IsAlive;
        }), "mixer thread should be alive after start");
        var first = GetField<Thread?>(engine, "_mixerThread");
        start.Invoke(engine, null); // second start: no duplicate
        Assert.Same(first, GetField<Thread?>(engine, "_mixerThread"));
        stop.Invoke(engine, null);
        Assert.Null(GetField<Thread?>(engine, "_mixerThread"));
        stop.Invoke(engine, null); // second stop: safe
    }

    [Fact]
    public void Mixer_Tick_Idle_NoThrow_NoProgress()
    {
        using var engine = new RecorderEngine();
        SetField(engine, "_outRate", 48000);
        SetField(engine, "_outChannels", 2);
        SetField(engine, "_monitoring", true);
        GetInstance("DoMixerTick").Invoke(engine, null);
        Assert.Equal(0L, engine.MasterFramesEmitted);
    }

    [Fact]
    public void Mixer_Tick_DrainsLoopQuantum_LikeCallbackPath()
    {
        using var engine = new RecorderEngine();
        SetField(engine, "_outRate", 48000);
        SetField(engine, "_outChannels", 2);
        SetField(engine, "_monitoring", true);
        SetField(engine, "_preRollRemaining", 0);
        int n = 480 * 2;
        var loopRing = GetField<float[]>(engine, "_loopRing");
        var micRing = GetField<float[]>(engine, "_micRing");
        for (int i = 0; i < n; i++) { loopRing[i] = 0.1f; micRing[i] = 0.2f; }
        SetField(engine, "_loopR", 0);
        SetField(engine, "_loopW", n);
        SetField(engine, "_loopCount", n);
        SetField(engine, "_micR", 0);
        SetField(engine, "_micW", n);
        SetField(engine, "_micCount", n);
        GetInstance("DoMixerTick").Invoke(engine, null);
        Assert.Equal(480L, engine.MasterFramesEmitted);
        Assert.Equal(0, GetField<int>(engine, "_loopCount"));
        Assert.Equal(0, GetField<int>(engine, "_micCount"));
    }

    [Fact]
    public void PairByCount_SyntheticTake_NoSplices_NoDrops()
    {
        // Locks the rec-20261111_003933 finding: pair-by-count must emit a
        // bit-transparent mix with no splices and no drops. Synthetic sources
        // only (no WASAPI devices): 440 Hz mic sine at the take's 0.126 peak,
        // 1 kHz system burst at the take's 0.184 peak on s4-s6, 003933 gains
        // (MicGain 5.078, LoopGain 0.435). Silent before/after.
        const int rate = 48000, ch = 2, n = 480 * 2, framesPerSec = 100, seconds = 13;
        using var engine = new RecorderEngine();
        SetField(engine, "_outRate", rate);
        SetField(engine, "_outChannels", ch);
        SetField(engine, "_preRollRemaining", 0);
        engine.MicGain = 5.078;
        engine.LoopGain = 0.435;
        var tryPop = GetInstance("TryPopFixedFrame");
        var emit = GetInstance("EmitFixedMix");
        var pushLoop = GetInstance("PushLoopRing");
        var micRing = GetField<float[]>(engine, "_micRing");
        var loopDst = new float[n];
        var micDst = new float[n];
        var mix = new System.Collections.Generic.List<float>(seconds * framesPerSec * n);
        var predicted = new System.Collections.Generic.List<float>(seconds * framesPerSec * n);
        for (int f = 0; f < seconds * framesPerSec; f++)
        {
            int sec = f / framesPerSec;
            bool active = sec >= 4 && sec <= 6;
            // Mic: always captured (then drained like the mic-only path when
            // the loop is silent, so backlog never builds in the test).
            long base_ = (long)f * (rate / framesPerSec);
            AppendMic(engine, micRing, n, base_, rate, ch);
            if (!active)
            {
                DrainMic(engine, n);
                Assert.False((bool)tryPop.Invoke(engine, new object[] { loopDst, micDst })!);
                continue;
            }
            var sys = SysFrame(base_, n, rate, ch);
            pushLoop.Invoke(engine, new object[] { sys, n, 0.0 });
            Assert.True((bool)tryPop.Invoke(engine, new object[] { loopDst, micDst })!);
            emit.Invoke(engine, new object[] { loopDst, micDst, n });
            var buf = GetField<float[]>(engine, "_mixBufF");
            for (int i = 0; i < n; i++)
            {
                mix.Add(buf[i]);
                float m = MicSample(base_ + i / ch, rate);
                float s = sys[i];
                predicted.Add((s * 0.435f + m * 5.078f) * 0.5f);
            }
        }

        // 3 active seconds emitted, nothing else touched the counters.
        Assert.Equal(3L * framesPerSec * 480, engine.MasterFramesEmitted);
        Assert.Equal(0L, GetField<long>(engine, "_micUnderrunBlocks"));
        Assert.Equal(0L, GetField<long>(engine, "_silencedSamples"));
        Assert.Equal(0L, GetField<long>(engine, "_resyncCount"));

        // Bit-transparent: the mix is exactly the gained sum (no cap engaged:
        // predicted peak (0.184*0.435 + 0.126*5.078)*0.5 ~= 0.36 < 1).
        double worst = 0;
        for (int i = 0; i < mix.Count; i++)
            worst = Math.Max(worst, Math.Abs(mix[i] - predicted[i]));
        Assert.True(worst < 1e-5, $"mix must equal the gained sum, worst diff {worst}");

        // The 003933 gate, via the same scanner the CLI uses.
        var scan = TakeVerifier.Scan(mix.ToArray(), ch, rate);
        Assert.Equal(0, scan.JumpRuns);
        Assert.Equal(0, scan.BoundaryRuns);
        Assert.Empty(scan.PadRuns);
        Assert.False(scan.HasSplice);
    }

    private static float MicSample(long t, int rate)
        => 0.126f * (float)Math.Sin(2 * Math.PI * 440 * t / rate);

    private static float[] SysFrame(long base_, int n, int rate, int ch)
    {
        var f = new float[n];
        for (int i = 0; i < n; i++)
            f[i] = 0.184f * (float)Math.Sin(2 * Math.PI * 1000 * (base_ + i / ch) / rate);
        return f;
    }

    private static void AppendMic(object engine, float[] ring, int n, long base_, int rate, int ch)
    {
        int w = GetField<int>(engine, "_micW");
        int count = GetField<int>(engine, "_micCount");
        for (int i = 0; i < n; i++)
            ring[(w + i) % ring.Length] = MicSample(base_ + i / ch, rate);
        SetField(engine, "_micW", (w + n) % ring.Length);
        SetField(engine, "_micCount", count + n);
    }

    private static void DrainMic(object engine, int n)
    {
        // Mirrors the mic-only drive: consume one frame when available so the
        // synthetic silent stretch holds no backlog.
        int count = GetField<int>(engine, "_micCount");
        if (count < n) return;
        int r = GetField<int>(engine, "_micR");
        var ring = GetField<float[]>(engine, "_micRing");
        SetField(engine, "_micR", (r + n) % ring.Length);
        SetField(engine, "_micCount", count - n);
    }

    [Fact]
    public void ResumeFade_FirstFramesRamp_NoTick()
    {
        // Join-tick mask: silence -> signal arms a ~5 ms linear fade so the
        // stale-buffer + limiter-attack transient is inaudible, then the bus
        // returns to bit-transparent. No devices.
        using var engine = new RecorderEngine();
        SetField(engine, "_outRate", 48000);
        SetField(engine, "_outChannels", 2);
        SetField(engine, "_monitoring", true);
        SetField(engine, "_preRollRemaining", 0);
        engine.MicGain = 1.0;
        engine.LoopGain = 1.0;
        int n = 480 * 2;
        var loopRing = GetField<float[]>(engine, "_loopRing");
        var micRing = GetField<float[]>(engine, "_micRing");
        for (int i = 0; i < n; i++) { loopRing[i] = 0.2f; micRing[i] = 0.1f; }
        SetField(engine, "_loopR", 0);
        SetField(engine, "_loopW", n);
        SetField(engine, "_loopCount", n);
        SetField(engine, "_micR", 0);
        SetField(engine, "_micW", n);
        SetField(engine, "_micCount", n);
        // Simulate: mixer saw silence, loopback just resumed.
        SetField(engine, "_prevLoopSilent", true);
        EngineType.GetField("_loopWasSilent", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(engine, false);

        GetInstance("DoMixerTick").Invoke(engine, null);

        var buf = GetField<float[]>(engine, "_mixBufF");
        // Unfaded steady-state would be (0.2+0.1)*0.5 = 0.15 everywhere.
        // Faded: first frame ~0, ramps linearly over 240 frames (5 ms @48k).
        Assert.True(Math.Abs(buf[0]) < 1e-6, $"first frame must start at ~0, got {buf[0]}");
        float mid = buf[120 * 2]; // frame 120 of 240 -> gain ~0.5
        Assert.True(Math.Abs(mid - 0.15f * 0.5f) < 1e-4, $"mid-fade must be ~half gain, got {mid}");
        // Fade consumed within the first quantum (480 frames > 240 fade).
        Assert.Equal(0, GetField<int>(engine, "_resumeFadeLeft"));
        // Tail of the same quantum is transparent again.
        Assert.True(Math.Abs(buf[n - 2] - 0.15f) < 1e-6, $"post-fade must be transparent, got {buf[n - 2]}");
    }
}
