using System;
using System.IO;
using System.Reflection;
using Xunit;

namespace Hearbud.Tests;

/// <summary>
/// Tests for the private static helpers inside RecorderEngine that survived the
/// Phase 4 rewrite, exercised via reflection so the production code needs zero changes.
/// </summary>
public class RecorderEngineDspTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // Reflection helpers
    // ─────────────────────────────────────────────────────────────────────────

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

    // ─────────────────────────────────────────────────────────────────────────
    // NextPow2
    // ─────────────────────────────────────────────────────────────────────────

    private static int NextPow2(int n)
        => (int)GetStatic("NextPow2").Invoke(null, new object[] { n })!;

    [Theory]
    [InlineData(1,    256)]
    [InlineData(255,  256)]
    [InlineData(256,  256)]
    [InlineData(257,  512)]
    [InlineData(512,  512)]
    [InlineData(513,  1024)]
    [InlineData(1024, 1024)]
    [InlineData(1025, 2048)]
    [InlineData(4096, 4096)]
    [InlineData(4097, 8192)]
    public void NextPow2_ReturnsCorrectValue(int input, int expected)
        => Assert.Equal(expected, NextPow2(input));

    [Fact]
    public void NextPow2_MinimumIs256()
        => Assert.Equal(256, NextPow2(0));

    // ─────────────────────────────────────────────────────────────────────────
    // SoftClipIfNeeded — REMOVED in Phase 2 (deliberate contract change).
    // Global tanh dulled even quiet transients; the mix now uses the kneed
    // SoftLimiter (transparent below 0.8). Its contract is covered by
    // SoftLimiterTests. See ADR-0010.
    // ─────────────────────────────────────────────────────────────────────────

    // ─────────────────────────────────────────────────────────────────────────
    // FloatToPcm16/32 + dither — REMOVED in Phase 4 (deliberate contract change).
    // The engine is float32 end to end; nothing quantizes to int anymore
    // (MP3 gets 16-bit via NAudio's WaveFloatTo16Provider). See ADR-0012.
    // ─────────────────────────────────────────────────────────────────────────

    // ─────────────────────────────────────────────────────────────────────────
    // ConvertToTarget — REMOVED in Phase 4 (deliberate contract change).
    // Capture happens AT the target format, so channel/rate conversion no
    // longer exists. Drift is absorbed by TimestampedMicBuffer (tested in
    // TimestampedMicBufferTests). See ADR-0012.
    // ─────────────────────────────────────────────────────────────────────────

    // ─────────────────────────────────────────────────────────────────────────
    // UniquePath
    // ─────────────────────────────────────────────────────────────────────────

    private static string UniquePath(string path)
        => (string)GetStatic("UniquePath").Invoke(null, new object[] { path })!;

    [Fact]
    public void UniquePath_NonExistentFile_ReturnsOriginal()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hearbud-test-{Guid.NewGuid()}.wav");
        Assert.Equal(path, UniquePath(path));
    }

    [Fact]
    public void UniquePath_ExistingFile_ReturnsNumberedVariant()
    {
        var path = Path.GetTempFileName(); // creates the file
        try
        {
            var result = UniquePath(path);
            Assert.NotEqual(path, result);
            Assert.Contains("(1)", result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UniquePath_TwoExistingFiles_ReturnsTwo()
    {
        var path = Path.GetTempFileName();
        var dir  = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext  = Path.GetExtension(path);
        var path1 = Path.Combine(dir, $"{name} (1){ext}");

        File.WriteAllText(path1, "");
        try
        {
            var result = UniquePath(path);
            Assert.Contains("(2)", result);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path1);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CalculateOptimalQueueSize — REMOVED in Phase 4 (deliberate contract change).
    // The BlockingCollection write queue is gone; capture uses fixed-cap
    // drop-oldest channels (tested in CaptureHandoffTests). See ADR-0012.
    // ─────────────────────────────────────────────────────────────────────────

    // ─────────────────────────────────────────────────────────────────────────
    // EnsureCapacity  (private static, ref float[])
    // ─────────────────────────────────────────────────────────────────────────

    private static void EnsureCapacityFloat(ref float[] buf, int needed)
    {
        var args = new object?[] { buf, needed };
        GetStatic("EnsureCapacity",
            new[] { typeof(float[]).MakeByRefType(), typeof(int) })
            .Invoke(null, args);
        buf = (float[])args[0]!;
    }

    [Fact]
    public void EnsureCapacity_NullFloat_AllocatesToNextPow2()
    {
        float[] buf = null!;
        EnsureCapacityFloat(ref buf, 100);
        Assert.NotNull(buf);
        // NextPow2(100) = 256
        Assert.Equal(256, buf.Length);
    }

    [Fact]
    public void EnsureCapacity_ExistingFloat_GrowsIfNeeded()
    {
        float[] buf = new float[64];
        var original = buf;
        EnsureCapacityFloat(ref buf, 1000);
        Assert.NotSame(original, buf);
        // NextPow2(1000) = 1024
        Assert.Equal(1024, buf.Length);
    }

    [Fact]
    public void EnsureCapacity_ExistingFloat_NoOpIfSufficient()
    {
        float[] buf = new float[2048];
        var original = buf;
        EnsureCapacityFloat(ref buf, 100);
        Assert.Same(original, buf);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // EnsureRingCapacity — REMOVED in Phase 2 (deliberate contract change).
    // The hand-rolled ring is replaced by TimestampedMicBuffer (timestamp
    // horizon + PLL read rate); growth/overflow are covered by
    // TimestampedMicBufferTests. See ADR-0010.
    // ─────────────────────────────────────────────────────────────────────────

    // ─────────────────────────────────────────────────────────────────────────
    // ReadExactSamples — REMOVED in Phase 4 (deliberate contract change).
    // ISampleSource pumping is gone with CSCore; capture delivers float32
    // spans that are copied straight into the handoff channels. See ADR-0012.
    // ─────────────────────────────────────────────────────────────────────────

    // (Phase 2: SoftClip NaN/Infinity tests removed with tanh — see SoftLimiterTests.)

    // ─────────────────────────────────────────────────────────────────────────
    // WriteLog — truncation at MaxLogSizeBytes
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void WriteLog_TruncatesAtMaxSize()
    {
        using var engine = new RecorderEngine();
        var tempLog = Path.GetTempFileName();
        try
        {
            var logPathField = EngineType.GetField("_logPath",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            logPathField.SetValue(engine, tempLog);

            var openLog = GetInstance("OpenLog");
            openLog.Invoke(engine, null);

            // Force the byte counter to the threshold so the very next write trips truncation
            var logBytesField = EngineType.GetField("_logBytesWritten",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            logBytesField.SetValue(engine, 10L * 1024 * 1024);

            var writeLog = GetInstance("WriteLog");
            writeLog.Invoke(engine, new object?[] { "INFO", "trigger truncation", null });

            var content = File.ReadAllText(tempLog);
            Assert.Contains("LOG TRUNCATED", content);

            var logField = EngineType.GetField("_log",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            Assert.Null(logField.GetValue(engine));
        }
        finally
        {
            File.Delete(tempLog);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UniquePath — directory path edge case
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void UniquePath_PathIsDirectory_ReturnsUnchanged()
    {
        // File.Exists on a directory returns false, so the function returns the path as-is.
        var dir = Path.Combine(Path.GetTempPath(), $"hearbud-test-dir-{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Equal(dir, UniquePath(dir));
        }
        finally
        {
            Directory.Delete(dir);
        }
    }
}
