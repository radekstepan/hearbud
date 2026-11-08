using System;
using System.Diagnostics;
using System.Reflection;
using Xunit;

namespace Hearbud.Tests;

// No audio hardware: pure-helper tests + reflection-driven decision tests.
// PollCaptureWatchdog is only exercised on no-op paths (never triggers a real reopen).
public class RecorderEngineWatchdogTests : IDisposable
{
    private readonly RecorderEngine _engine = new();
    private static readonly Type T = typeof(RecorderEngine);
    public void Dispose() => _engine.Dispose();

    private static long MsAgo(double ms) => Stopwatch.GetTimestamp() - (long)(ms * Stopwatch.Frequency / 1000.0);
    private void Set(string field, object? v) => T.GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(_engine, v);
    private bool Check(long now) => (bool)T.GetMethod("CheckCaptureStarvation", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_engine, new object[] { now })!;
    private static bool Stale(long now, long last, long baseline)
        => (bool)T.GetMethod("IsCaptureStale", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { now, last, baseline, 2000.0 })!;

    private void PrimeRecording()
    {
        Set("_recording", true); Set("_monitoring", true); Set("_paused", false); Set("_disposed", false);
        Set("_activeLoopDeviceId", "loop"); Set("_activeMicDeviceId", "mic");
        Set("_masterStartTick", MsAgo(60_000));
    }

    [Fact] public void Stale_GapAboveThreshold_True() => Assert.True(Stale(Stopwatch.GetTimestamp(), MsAgo(2500), MsAgo(60_000)));
    [Fact] public void Stale_GapBelowThreshold_False() => Assert.False(Stale(Stopwatch.GetTimestamp(), MsAgo(500), MsAgo(60_000)));
    [Fact] public void Stale_ZeroBaseline_False() => Assert.False(Stale(Stopwatch.GetTimestamp(), 0, 0));
    [Fact] public void Stale_ZeroLastTick_FallsBackToBaseline() => Assert.True(Stale(Stopwatch.GetTimestamp(), 0, MsAgo(5000)));

    [Fact] public void Check_IdleEngine_False() => Assert.False(Check(Stopwatch.GetTimestamp()));

    [Fact]
    public void Check_Paused_False()
    {
        PrimeRecording(); Set("_paused", true);
        Set("_lastLoopTick", MsAgo(5000)); Set("_lastMicTick", MsAgo(5000));
        Assert.False(Check(Stopwatch.GetTimestamp()));
    }

    [Fact]
    public void Check_BothStale_True_ThenCooldown_BlocksSecond()
    {
        PrimeRecording();
        Set("_lastLoopTick", MsAgo(5000)); Set("_lastMicTick", MsAgo(5000));
        Assert.True(Check(Stopwatch.GetTimestamp()));
        Assert.False(Check(Stopwatch.GetTimestamp()));
    }

    [Fact]
    public void Check_OnlyOneStale_False()
    {
        PrimeRecording();
        Set("_lastLoopTick", MsAgo(5000)); Set("_lastMicTick", Stopwatch.GetTimestamp());
        Assert.False(Check(Stopwatch.GetTimestamp()));
    }

    [Fact]
    public void Check_RecoveryInFlight_False()
    {
        PrimeRecording();
        Set("_lastLoopTick", MsAgo(5000)); Set("_lastMicTick", MsAgo(5000));
        Set("_loopLost", true);
        Assert.False(Check(Stopwatch.GetTimestamp()));
    }

    [Fact]
    public void Check_MicOnlyConfig_LoopIgnored()
    {
        PrimeRecording(); Set("_activeLoopDeviceId", "");
        Set("_lastLoopTick", MsAgo(9000)); Set("_lastMicTick", MsAgo(9000));
        Assert.True(Check(Stopwatch.GetTimestamp()));
    }

    [Fact]
    public void Poll_IdleEngine_DoesNotThrow()
    {
        var ex = Record.Exception(() => _engine.PollCaptureWatchdog());
        Assert.Null(ex);
    }
}
