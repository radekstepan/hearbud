using System;
using Xunit;

namespace Hearbud.Tests;

/// <summary>
/// Tests for the staged v2 kneed limiter (<see cref="Hearbud.SoftLimiter"/>).
/// </summary>
public class SoftLimiterTests
{
    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(-0.5f)]
    [InlineData(0.799f)]
    [InlineData(-0.799f)]
    public void Limit_BelowKnee_PassesThroughUntouched(float x)
        => Assert.Equal(x, Hearbud.SoftLimiter.Limit(x));

    [Fact]
    public void Limit_IsOddFunction()
        => Assert.Equal(-Hearbud.SoftLimiter.Limit(0.95f), Hearbud.SoftLimiter.Limit(-0.95f), precision: 6);

    [Fact]
    public void Limit_AtKnee_IsContinuous()
        // Just above the knee the output must be just above the knee (no jump).
        => Assert.InRange(Hearbud.SoftLimiter.Limit(0.81f), 0.8f, 0.82f);

    [Fact]
    public void Limit_HotSignal_StaysUnderCeiling()
    {
        Assert.InRange(Hearbud.SoftLimiter.Limit(1.0f), 0.8f, 1.0f);
        Assert.InRange(Hearbud.SoftLimiter.Limit(10f), 0.8f, 1.0f);
        Assert.InRange(Hearbud.SoftLimiter.Limit(-10f), -1.0f, -0.8f);
    }

    [Fact]
    public void Limit_IsMonotonic()
    {
        float prev = float.NegativeInfinity;
        for (float x = -2f; x <= 2f; x += 0.05f)
        {
            float y = Hearbud.SoftLimiter.Limit(x);
            Assert.True(y >= prev, $"non-monotonic at x={x}");
            prev = y;
        }
    }

    [Fact]
    public void Limit_PositiveInfinity_ReturnsOne()
        => Assert.Equal(1f, Hearbud.SoftLimiter.Limit(float.PositiveInfinity));

    [Fact]
    public void Limit_NegativeInfinity_ReturnsMinusOne()
        => Assert.Equal(-1f, Hearbud.SoftLimiter.Limit(float.NegativeInfinity));

    [Fact]
    public void Limit_NaN_ReturnsNaN()
        => Assert.True(float.IsNaN(Hearbud.SoftLimiter.Limit(float.NaN)));

    [Fact]
    public void Limit_SofterThanHardClip()
        // A hard clip would output exactly 1.0 for x=1.0; the knee must round it off.
        => Assert.True(Hearbud.SoftLimiter.Limit(1.0f) < 1.0f);
}
