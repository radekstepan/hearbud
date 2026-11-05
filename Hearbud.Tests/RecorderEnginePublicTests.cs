using System;
using System.Threading.Tasks;
using Xunit;

namespace Hearbud.Tests;

/// <summary>
/// Tests for the public surface of RecorderEngine that is safe to exercise
/// on any OS without real audio hardware.
/// </summary>
public class RecorderEnginePublicTests : IDisposable
{
    private readonly RecorderEngine _engine = new();

    public void Dispose() => _engine.Dispose();

    // ── Construction ──────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_DoesNotThrow()
    {
        // Implicitly tested — the field initializer above succeeded.
        Assert.NotNull(_engine);
    }

    // ── Initial state ─────────────────────────────────────────────────────────

    [Fact]
    public void IsRecording_Initially_False()
        => Assert.False(_engine.IsRecording);

    [Fact]
    public void MicGain_Default_IsOne()
        => Assert.Equal(1.0, _engine.MicGain);

    [Fact]
    public void LoopGain_Default_IsOne()
        => Assert.Equal(1.0, _engine.LoopGain);

    // ── Gain properties (thread-safe volatile) ────────────────────────────────

    [Fact]
    public void MicGain_SetAndGet_RoundTrips()
    {
        _engine.MicGain = 2.5;
        Assert.Equal(2.5, _engine.MicGain);
    }

    [Fact]
    public void LoopGain_SetAndGet_RoundTrips()
    {
        _engine.LoopGain = 0.25;
        Assert.Equal(0.25, _engine.LoopGain);
    }

    [Fact]
    public void MicGain_SetToZero_ReturnsZero()
    {
        _engine.MicGain = 0.0;
        Assert.Equal(0.0, _engine.MicGain);
    }

    [Fact]
    public void LoopGain_SetToZero_ReturnsZero()
    {
        _engine.LoopGain = 0.0;
        Assert.Equal(0.0, _engine.LoopGain);
    }

    [Fact]
    public void MicGain_SetToMax_RoundTrips()
    {
        _engine.MicGain = 10.0;
        Assert.Equal(10.0, _engine.MicGain);
    }

    // ── Dispose idempotency ───────────────────────────────────────────────────

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        using var engine = new RecorderEngine();
        engine.Dispose(); // first call
        engine.Dispose(); // second call — must be a no-op
    }

    [Fact]
    public void Dispose_OnFreshInstance_DoesNotThrow()
    {
        var engine = new RecorderEngine();
        var ex = Record.Exception(() => engine.Dispose());
        Assert.Null(ex);
    }

    // ── Events wired up (smoke test — no hardware needed) ────────────────────

    [Fact]
    public void LevelChanged_CanSubscribeAndUnsubscribe()
    {
        EventHandler<LevelChangedEventArgs>? handler = (_, _) => { };
        _engine.LevelChanged += handler;
        _engine.LevelChanged -= handler;
    }

    [Fact]
    public void Status_CanSubscribeAndUnsubscribe()
    {
        EventHandler<EngineStatusEventArgs>? handler = (_, _) => { };
        _engine.Status += handler;
        _engine.Status -= handler;
    }

    [Fact]
    public void EncodingProgress_CanSubscribeAndUnsubscribe()
    {
        EventHandler<int>? handler = (_, _) => { };
        _engine.EncodingProgress += handler;
        _engine.EncodingProgress -= handler;
    }

    // ── HandleDeviceDisconnectedAsync — REMOVED in Phase 4 ─────────────────────
    // Capture legs now auto-rebuild (RecoverAsync) instead of stopping the
    // session; device add/remove surfaces as RecorderEngine.DevicesChanged.
    // See ADR-0012.

    // ── StopMonitor on idle engine ────────────────────────────────────────────

    [Fact]
    public void StopMonitor_WhenNotMonitoring_DoesNotThrow()
    {
        var ex = Record.Exception(() => _engine.StopMonitor());
        Assert.Null(ex);
    }
}
