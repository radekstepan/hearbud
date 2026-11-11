using System;
using System.Diagnostics;
using System.Reflection;
using Xunit;

namespace Hearbud.Tests;

/// <summary>
/// OBS_LESSONS 3.1 + 3.2: packet-clock stamping and silent-loopback detector.
/// Diagnostics only for 3.1 (no trim behavior change); 3.2 guards the trim.
/// </summary>
public class RecorderEngineObsLessonsTests
{
    private static readonly Type EngineType = typeof(RecorderEngine);

    private static MethodInfo GetStatic(string name)
    {
        var m = EngineType.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.True(m != null, $"Private static method '{name}' not found.");
        return m!;
    }

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

    [Fact]
    public void PacketStartTick_InvalidInput_ReturnsNow()
    {
        var m = GetStatic("PacketStartTick");
        long now = Stopwatch.GetTimestamp();
        Assert.Equal(now, (long)m.Invoke(null, new object[] { now, 0, 48000 })!);
        Assert.Equal(now, (long)m.Invoke(null, new object[] { now, 480, 0 })!);
    }

    [Fact]
    public void PacketStartTick_SubtractsPacketDuration()
    {
        var m = GetStatic("PacketStartTick");
        long now = Stopwatch.GetTimestamp();
        // 1 s of audio at the device rate => delta ~= Stopwatch.Frequency.
        long start = (long)m.Invoke(null, new object[] { now, 48000, 48000 })!;
        long delta = now - start;
        Assert.InRange(delta, Stopwatch.Frequency - 2, Stopwatch.Frequency + 2);
    }

    [Fact]
    public void IsSilentBlock_Zeros_True_Signal_False()
    {
        var m = GetStatic("IsSilentBlock");
        var zeros = new float[960];
        Assert.True((bool)m.Invoke(null, new object[] { zeros, zeros.Length })!);
        var tiny = new float[960];
        tiny[500] = 9e-8f;
        Assert.True((bool)m.Invoke(null, new object[] { tiny, tiny.Length })!);
        var loud = new float[960];
        loud[10] = 1.1e-7f;
        Assert.False((bool)m.Invoke(null, new object[] { loud, loud.Length })!);
        var tone = new float[960];
        tone[0] = 0.5f;
        Assert.False((bool)m.Invoke(null, new object[] { tone, tone.Length })!);
    }

    [Fact]
    public void RecordPacketClock_TwoSources_UpdatesError()
    {
        using var engine = new RecorderEngine();
        var rec = GetInstance("RecordPacketClock");
        var propErr = EngineType.GetProperty("LastClockErrorSec", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!;
        var propMax = EngineType.GetProperty("ClockErrorMaxSec", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!;
        long now = Stopwatch.GetTimestamp();
        // Mic packet 10 ms before loop packet => positive loop-minus-mic error ~+10 ms.
        long micStart = now - (long)(0.010 * Stopwatch.Frequency);
        rec.Invoke(engine, new object[] { false, micStart });
        // Only one side so far: error stays zero.
        Assert.Equal(0.0, (double)propErr.GetValue(engine)!);
        rec.Invoke(engine, new object[] { true, now });
        Assert.InRange((double)propErr.GetValue(engine)!, 0.009, 0.011);
        Assert.InRange(Math.Abs((double)propMax.GetValue(engine)!), 0.009, 0.011);
    }

    [Fact]
    public void SilentConstants_MatchDocContract()
    {
        var eps = EngineType.GetField("SilentEpsilon", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!;
        var need = EngineType.GetField("SilentLoopBlocksNeeded", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!;
        Assert.Equal(1e-7f, (float)eps.GetValue(null)!);
        Assert.Equal(3, (int)need.GetValue(null)!);
    }
}
