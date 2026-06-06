using Xunit;

namespace Hearbud.Tests;

/// <summary>Tests for the Dbfs utility class (pure static math, no I/O).</summary>
public class DbfsTests
{
    // ── FormatGain ────────────────────────────────────────────────────────────

    [Fact]
    public void FormatGain_Zero_ReturnsMinusInfinityString()
    {
        Assert.Equal("0.00× (-∞ dB)", Dbfs.FormatGain(0.0));
    }

    [Fact]
    public void FormatGain_Negative_ReturnsMinusInfinityString()
    {
        Assert.Equal("0.00× (-∞ dB)", Dbfs.FormatGain(-5.0));
    }

    [Fact]
    public void FormatGain_One_ReturnsZeroDb()
    {
        var result = Dbfs.FormatGain(1.0);
        Assert.Contains("1.00×", result);
        Assert.Contains("+0.0", result);
    }

    [Fact]
    public void FormatGain_Two_ReturnsApproxPlusSixDb()
    {
        // 20 * log10(2) ≈ 6.021 dB → formatted as "+6.0"
        var result = Dbfs.FormatGain(2.0);
        Assert.Contains("2.00×", result);
        Assert.Contains("+6.0", result);
    }

    [Fact]
    public void FormatGain_Half_ReturnsApproxMinusSixDb()
    {
        // 20 * log10(0.5) ≈ -6.021 dB → formatted as "-6.0"
        var result = Dbfs.FormatGain(0.5);
        Assert.Contains("0.50×", result);
        Assert.Contains("-6.0", result);
    }

    [Fact]
    public void FormatGain_Ten_ReturnsTwentyDb()
    {
        // 20 * log10(10) = 20.0 dB
        var result = Dbfs.FormatGain(10.0);
        Assert.Contains("10.00×", result);
        Assert.Contains("+20.0", result);
    }

    // ── ToDbfs ────────────────────────────────────────────────────────────────

    [Fact]
    public void ToDbfs_Zero_ReturnsMinus60()
    {
        Assert.Equal(-60.0, Dbfs.ToDbfs(0.0));
    }

    [Fact]
    public void ToDbfs_BelowThreshold_ReturnsMinus60()
    {
        // Threshold is 1e-6; anything below it is clamped to -60
        Assert.Equal(-60.0, Dbfs.ToDbfs(1e-7));
    }

    [Fact]
    public void ToDbfs_AtThreshold_ReturnsMinus60()
    {
        // 1e-6 is NOT strictly greater than 1e-6, so still clamped
        Assert.Equal(-60.0, Dbfs.ToDbfs(1e-6));
    }

    [Fact]
    public void ToDbfs_JustAboveThreshold_IsNegative()
    {
        var result = Dbfs.ToDbfs(0.1);
        Assert.True(result > -60.0 && result < 0.0);
    }

    [Fact]
    public void ToDbfs_One_ReturnsZero()
    {
        Assert.Equal(0.0, Dbfs.ToDbfs(1.0), precision: 10);
    }

    [Fact]
    public void ToDbfs_Half_IsApproxMinusSixDb()
    {
        var result = Dbfs.ToDbfs(0.5);
        Assert.InRange(result, -6.1, -6.0);
    }

    [Fact]
    public void ToDbfs_Two_IsApproxPlusSixDb()
    {
        var result = Dbfs.ToDbfs(2.0);
        Assert.InRange(result, 6.0, 6.1);
    }

    [Fact]
    public void ToDbfs_ClampFloorIsMinusSixty()
    {
        // Result is always >= -60
        Assert.True(Dbfs.ToDbfs(0.0) >= -60.0);
        Assert.True(Dbfs.ToDbfs(1e-10) >= -60.0);
    }

    // ── FormatGain — edge values ─────────────────────────────────────────────

    [Fact]
    public void FormatGain_VeryLargeValue_ShowsActualDecibels()
    {
        // 20 * log10(1000) = 60 dB
        var result = Dbfs.FormatGain(1000.0);
        Assert.Contains("1000.00×", result);
        Assert.Contains("+60.0", result);
    }

    [Fact]
    public void FormatGain_VerySmallPositiveValue_ShowsActualDecibels()
    {
        // 20 * log10(0.001) = -60 dB
        var result = Dbfs.FormatGain(0.001);
        Assert.Contains("-60.0", result);
    }
}
