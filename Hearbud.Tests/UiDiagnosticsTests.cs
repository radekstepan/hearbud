using Xunit;

namespace Hearbud.Tests;

/// <summary>Tests for the click-glitch diagnostics helpers (pure logic, no UI thread).</summary>
public class UiDiagnosticsTests
{
    [Fact]
    public void InputEventRing_CapsAtCapacity()
    {
        var ring = new InputEventRing(3);
        ring.Record("a"); ring.Record("b"); ring.Record("c"); ring.Record("d");
        Assert.Equal(4, ring.Total);
        Assert.Equal(3, ring.Kept);
        Assert.Equal(new[] { "b", "c", "d" }, ring.Tail(10));
    }

    [Fact]
    public void InputEventRing_TailReturnsLastN_OldestFirst()
    {
        var ring = new InputEventRing(50);
        ring.Record("a"); ring.Record("b"); ring.Record("c");
        Assert.Equal(new[] { "b", "c" }, ring.Tail(2));
        Assert.Empty(ring.Tail(0));
    }

    [Fact]
    public void InputEventRing_IgnoresEmpty()
    {
        var ring = new InputEventRing(10);
        ring.Record("");
        Assert.Equal(0, ring.Total);
    }

    [Fact]
    public void LagTracker_ObserveTracksMaxAndSpikes()
    {
        var lag = new LagTracker();
        lag.Observe(1005.0, 1000.0);
        double big = lag.Observe(1400.0, 1000.0);
        Assert.Equal(400.0, big, precision: 6);
        Assert.Equal(2, lag.Samples);
        Assert.Equal(400.0, lag.MaxLagMs, precision: 6);
        Assert.Equal(1, lag.SpikeCount);
    }

    [Fact]
    public void LagTracker_EarlyTick_NegativeLag_NoSpike()
    {
        var lag = new LagTracker();
        double v = lag.Observe(990.0, 1000.0);
        Assert.Equal(-10.0, v, precision: 6);
        Assert.Equal(0, lag.SpikeCount);
    }

    [Fact]
    public void SourceName_Null_ReturnsNullLabel()
    {
        Assert.Equal("null", UiDiagnostics.SourceName(null));
    }

    [Fact]
    public void SourceName_PlainObject_ReturnsTypeName()
    {
        Assert.Equal("Object", UiDiagnostics.SourceName(new object()));
    }
}
