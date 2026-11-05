using Xunit;

namespace Hearbud.Tests;

/// <summary>Tests for the Phase 4 capture handoff (bounded drop-oldest channels).</summary>
public class CaptureHandoffTests
{
    [Fact]
    public void TryWrite_RoundTripsInOrder()
    {
        var ch = CaptureHandoff.Create<int>(4);
        long dropped = 0;
        Assert.True(CaptureHandoff.TryWrite(ch, 1, 4, ref dropped));
        Assert.True(CaptureHandoff.TryWrite(ch, 2, 4, ref dropped));
        Assert.Equal(0, dropped);
        Assert.True(ch.Reader.TryRead(out int a));
        Assert.True(ch.Reader.TryRead(out int b));
        Assert.Equal(1, a);
        Assert.Equal(2, b);
    }

    [Fact]
    public void TryWrite_WhenFull_DropsOldestAndCounts()
    {
        var ch = CaptureHandoff.Create<int>(4);
        long dropped = 0;
        for (int i = 0; i < 6; i++)
            Assert.True(CaptureHandoff.TryWrite(ch, i, 4, ref dropped));

        Assert.Equal(2, dropped);
        // Oldest two evicted; the survivors are the freshest four, in order.
        for (int expect = 2; expect < 6; expect++)
        {
            Assert.True(ch.Reader.TryRead(out int got));
            Assert.Equal(expect, got);
        }
        Assert.False(ch.Reader.TryRead(out _));
    }

    [Fact]
    public void TryWrite_CompletedChannel_ReturnsFalse()
    {
        var ch = CaptureHandoff.Create<int>(4);
        ch.Writer.Complete();
        long dropped = 0;
        Assert.False(CaptureHandoff.TryWrite(ch, 1, 4, ref dropped));
    }
}
