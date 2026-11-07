using System;
using System.IO;
using System.Reflection;
using CSCore;
using CSCore.Streams;
using Xunit;

namespace Hearbud.Tests;

/// <summary>
/// Tests for the private static DSP / conversion helpers inside RecorderEngine,
/// exercised via reflection so the production code needs zero changes.
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

    // Helper: decode little-endian 16-bit PCM bytes → int
    private static int DecodePcm16(byte[] buf, int byteOffset)
    {
        short s = (short)(buf[byteOffset] | (buf[byteOffset + 1] << 8));
        return s;
    }

    // Helper: decode little-endian 32-bit PCM bytes → int
    private static int DecodePcm32(byte[] buf, int byteOffset)
    {
        return buf[byteOffset]
             | (buf[byteOffset + 1] << 8)
             | (buf[byteOffset + 2] << 16)
             | (buf[byteOffset + 3] << 24);
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
    // SoftClipIfNeeded
    // ─────────────────────────────────────────────────────────────────────────

    private static float SoftClip(float x)
        => (float)GetStatic("SoftClipIfNeeded").Invoke(null, new object[] { x })!;

    [Fact]
    public void SoftClip_Zero_ReturnsZero()
        => Assert.Equal(0f, SoftClip(0f), precision: 6);

    [Fact]
    public void SoftClip_IsOddFunction()
        => Assert.Equal(-SoftClip(0.5f), SoftClip(-0.5f), precision: 6);

    [Fact]
    public void SoftClip_MatchesTanh_OnlyAboveOne()
    {
        // Inside [-1,1] the limiter is bit-transparent (no tanh coloration).
        Assert.Equal(0.7f, SoftClip(0.7f), precision: 6);
        Assert.Equal(-0.7f, SoftClip(-0.7f), precision: 6);
        // Above 1 the tanh soft-knee engages.
        float x = 1.5f;
        Assert.Equal(MathF.Sign(x) * MathF.Tanh(MathF.Abs(x)), SoftClip(x), precision: 6);
    }

    [Fact]
    public void ComputeMixScale_NormalizesOnlyOnOvers()
    {
        var m = GetStatic("ComputeMixScale", new[] { typeof(float) });
        float scale(float peak) => (float)m.Invoke(null, new object[] { peak })!;
        Assert.Equal(1f, scale(0.5f), precision: 6);
        Assert.Equal(1f, scale(1f), precision: 6);
        Assert.Equal(0.5f, scale(2f), precision: 6);
    }

    [Fact]
    public void SoftClip_LargePositive_ClampsNearOne()
    {
        float result = SoftClip(100f);
        Assert.InRange(result, 0.999f, 1.001f);
    }

    [Fact]
    public void SoftClip_LargeNegative_ClampsNearMinusOne()
    {
        float result = SoftClip(-100f);
        Assert.InRange(result, -1.001f, -0.999f);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FloatToPcm32
    // ─────────────────────────────────────────────────────────────────────────

    private static void FloatToPcm32(float[] src, byte[] dst, int count)
        => GetStatic("FloatToPcm32").Invoke(null, new object[] { src, dst, count });

    [Fact]
    public void FloatToPcm32_Zero_AllBytesZero()
    {
        var src = new float[] { 0f, 0f, 0f, 0f };
        var dst = new byte[src.Length * 4];
        FloatToPcm32(src, dst, src.Length);
        Assert.All(dst, b => Assert.Equal(0, b));
    }

    [Fact]
    public void FloatToPcm32_PlusOne_EqualsIntMaxValue()
    {
        var src = new float[] { 1f };
        var dst = new byte[4];
        FloatToPcm32(src, dst, 1);
        int decoded = DecodePcm32(dst, 0);
        Assert.Equal(int.MaxValue, decoded);
    }

    [Fact]
    public void FloatToPcm32_MinusOne_EqualsIntMinValue()
    {
        var src = new float[] { -1f };
        var dst = new byte[4];
        FloatToPcm32(src, dst, 1);
        int decoded = DecodePcm32(dst, 0);
        Assert.Equal(int.MinValue, decoded);
    }

    [Fact]
    public void FloatToPcm32_Clipping_ClampsToIntMax()
    {
        var src = new float[] { 99f };
        var dst = new byte[4];
        FloatToPcm32(src, dst, 1);
        int decoded = DecodePcm32(dst, 0);
        Assert.Equal(int.MaxValue, decoded);
    }

    [Fact]
    public void FloatToPcm32_NegativeClipping_ClampsToIntMin()
    {
        var src = new float[] { -99f };
        var dst = new byte[4];
        FloatToPcm32(src, dst, 1);
        int decoded = DecodePcm32(dst, 0);
        Assert.Equal(int.MinValue, decoded);
    }

    [Fact]
    public void FloatToPcm32_LittleEndian_ByteOrder()
    {
        // 0x00800000 = 8388608. Test that bytes are in LE order.
        var src = new float[] { 1f };
        var dst = new byte[4];
        FloatToPcm32(src, dst, 1);
        // LSB is dst[0], MSB is dst[3]
        int manual = dst[0] | (dst[1] << 8) | (dst[2] << 16) | (dst[3] << 24);
        Assert.Equal(int.MaxValue, manual);
    }

    [Fact]
    public void FloatToPcm32_MultiSample_EachProcessedIndependently()
    {
        var src = new float[] { 0f, 1f, -1f };
        var dst = new byte[src.Length * 4];
        FloatToPcm32(src, dst, src.Length);
        Assert.Equal(0,            DecodePcm32(dst, 0));
        Assert.Equal(int.MaxValue, DecodePcm32(dst, 4));
        Assert.Equal(int.MinValue, DecodePcm32(dst, 8));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FloatToPcm16  (uses dithering → results are ±1 from ideal)
    // ─────────────────────────────────────────────────────────────────────────

    private static void FloatToPcm16(float[] src, byte[] dst, int count)
        => GetStatic("FloatToPcm16").Invoke(null, new object[] { src, dst, count });

    [Fact]
    public void FloatToPcm16_Zero_IsWithinDitherRange()
    {
        // Dither is triangular ±1 LSB, so result should be in [-1, 1]
        var src = new float[] { 0f };
        var dst = new byte[2];
        FloatToPcm16(src, dst, 1);
        int decoded = DecodePcm16(dst, 0);
        Assert.InRange(decoded, -1, 1);
    }

    [Fact]
    public void FloatToPcm16_PlusOne_IsNearShortMax()
    {
        // 1.0 * 32767 = 32767, dither can reduce to 32766 at most
        var src = new float[] { 1f };
        var dst = new byte[2];
        FloatToPcm16(src, dst, 1);
        int decoded = DecodePcm16(dst, 0);
        Assert.InRange(decoded, 32766, 32767);
    }

    [Fact]
    public void FloatToPcm16_MinusOne_IsNearShortMin()
    {
        var src = new float[] { -1f };
        var dst = new byte[2];
        FloatToPcm16(src, dst, 1);
        int decoded = DecodePcm16(dst, 0);
        Assert.InRange(decoded, -32768, -32766);
    }

    [Fact]
    public void FloatToPcm16_Clipping_ClampsToShortRange()
    {
        var src = new float[] { 99f, -99f };
        var dst = new byte[4];
        FloatToPcm16(src, dst, 2);
        Assert.InRange(DecodePcm16(dst, 0), 32766, 32767);
        Assert.InRange(DecodePcm16(dst, 2), -32768, -32766);
    }

    [Fact]
    public void FloatToPcm16_LargeArray_AllSamplesInShortRange()
    {
        // Use a DC signal at 0.5 → all decoded values should be near 16383
        int count = 256;
        var src = new float[count];
        Array.Fill(src, 0.5f);
        var dst = new byte[count * 2];
        FloatToPcm16(src, dst, count);

        for (int i = 0; i < count; i++)
        {
            int v = DecodePcm16(dst, i * 2);
            Assert.InRange(v, short.MinValue, short.MaxValue);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ConvertToTarget  (channel / rate conversion)
    // ─────────────────────────────────────────────────────────────────────────

    // ConvertToTarget signature:
    //   private static int ConvertToTarget(
    //       float[] src, int floatCount, CSCore.WaveFormat srcFmt,
    //       int dstRate, int dstCh, ref float[] dst, ref float[] scratch)

    private static int InvokeConvertToTarget(
        float[] src, int floatCount, CSCore.WaveFormat srcFmt,
        int dstRate, int dstCh,
        ref float[] dst, ref float[] scratch)
    {
        var method = GetStatic("ConvertToTarget");
        var args = new object[] { src, floatCount, srcFmt, dstRate, dstCh, dst, scratch };
        int result = (int)method.Invoke(null, args)!;
        dst     = (float[])args[5];
        scratch = (float[])args[6];
        return result;
    }

    [Fact]
    public void ConvertToTarget_SameRateSameCh_IsPassthrough()
    {
        // stereo 48 kHz → stereo 48 kHz: samples copied unchanged
        var srcFmt = new CSCore.WaveFormat(48000, 16, 2);
        float[] src = { 0.1f, 0.2f, 0.3f, 0.4f }; // 2 stereo frames
        float[] dst = new float[16];
        float[] scratch = new float[16];

        int count = InvokeConvertToTarget(src, src.Length, srcFmt, 48000, 2, ref dst, ref scratch);

        Assert.Equal(src.Length, count);
        for (int i = 0; i < src.Length; i++)
            Assert.Equal(src[i], dst[i], precision: 6);
    }

    [Fact]
    public void ConvertToTarget_MonoToStereo_DuplicatesSamples()
    {
        // mono 48 kHz → stereo 48 kHz
        var srcFmt = new CSCore.WaveFormat(48000, 16, 1);
        float[] src = { 0.5f, 0.8f }; // 2 mono frames
        float[] dst = new float[16];
        float[] scratch = new float[16];

        int count = InvokeConvertToTarget(src, src.Length, srcFmt, 48000, 2, ref dst, ref scratch);

        // 2 mono frames → 2 stereo frames = 4 floats
        Assert.Equal(4, count);
        Assert.Equal(0.5f, dst[0], precision: 6); // L
        Assert.Equal(0.5f, dst[1], precision: 6); // R (duplicated)
        Assert.Equal(0.8f, dst[2], precision: 6); // L
        Assert.Equal(0.8f, dst[3], precision: 6); // R (duplicated)
    }

    [Fact]
    public void ConvertToTarget_StereoToMono_AveragesChannels()
    {
        // stereo 48 kHz → mono 48 kHz
        var srcFmt = new CSCore.WaveFormat(48000, 16, 2);
        float[] src = { 0.2f, 0.8f, 0.4f, 0.6f }; // 2 stereo frames: (L=0.2,R=0.8), (L=0.4,R=0.6)
        float[] dst = new float[16];
        float[] scratch = new float[16];

        int count = InvokeConvertToTarget(src, src.Length, srcFmt, 48000, 1, ref dst, ref scratch);

        Assert.Equal(2, count);
        Assert.Equal(0.5f, dst[0], precision: 6); // (0.2 + 0.8) / 2
        Assert.Equal(0.5f, dst[1], precision: 6); // (0.4 + 0.6) / 2
    }

    [Fact]
    public void ConvertToTarget_Resampling_OutputFrameCountIsCorrect()
    {
        // mono 44100 → mono 48000: 2 source frames → ≈2 dest frames
        var srcFmt = new CSCore.WaveFormat(44100, 16, 1);
        float[] src = { 0.5f, 0.6f };
        float[] dst = new float[64];
        float[] scratch = new float[64];

        int count = InvokeConvertToTarget(src, src.Length, srcFmt, 48000, 1, ref dst, ref scratch);

        int expectedFrames = (int)((long)src.Length * 48000 / 44100);
        Assert.Equal(expectedFrames, count);
    }

    [Fact]
    public void ConvertToTarget_EmptyInput_ReturnsZero()
    {
        var srcFmt = new CSCore.WaveFormat(48000, 16, 2);
        float[] src = Array.Empty<float>();
        float[] dst = new float[16];
        float[] scratch = new float[16];

        int count = InvokeConvertToTarget(src, 0, srcFmt, 48000, 2, ref dst, ref scratch);

        Assert.Equal(0, count);
    }

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
    // CalculateOptimalQueueSize  (private instance method)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CalculateOptimalQueueSize_ReturnsPositiveValue()
    {
        using var engine = new RecorderEngine();
        var method = GetInstance("CalculateOptimalQueueSize");
        int size = (int)method.Invoke(engine, null)!;
        Assert.True(size > 0, $"Expected positive queue size but got {size}");
    }

    [Fact]
    public void CalculateOptimalQueueSize_IsAtLeast2000()
    {
        // DefaultQueueCapacity constant in the engine is 2000
        using var engine = new RecorderEngine();
        var method = GetInstance("CalculateOptimalQueueSize");
        int size = (int)method.Invoke(engine, null)!;
        Assert.True(size >= 2000, $"Expected >= 2000 but got {size}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FloatToPcm16 dither sanity (statistical)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FloatToPcm16_Dither_ProducesNonZeroSpread()
    {
        // With triangular dither in [-1, +1] applied to zero input, we expect
        // some samples to decode as +1 and some as -1, plus zeros. With N=1000
        // the probability of seeing zero of either is effectively zero.
        int count = 1000;
        var src = new float[count];
        var dst = new byte[count * 2];
        FloatToPcm16(src, dst, count);

        var seen = new HashSet<int>();
        for (int i = 0; i < count; i++)
            seen.Add(DecodePcm16(dst, i * 2));

        Assert.True(seen.Count >= 2,
            $"Dither should produce spread; got {seen.Count} unique values");
        Assert.Contains(1, seen);
        Assert.Contains(-1, seen);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ConvertToTarget — multi-channel edge cases (exercises the else branch)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ConvertToTarget_StereoToQuad_ExpandsToQuad()
    {
        // 2ch → 4ch hits the "else" branch (srcCh=2, dstCh=4).
        // For each source frame, dst gets L, R, R, R (last channels duplicate).
        var srcFmt = new CSCore.WaveFormat(48000, 16, 2);
        float[] src = { 0.1f, 0.9f }; // 1 stereo frame
        float[] dst = new float[16];
        float[] scratch = new float[16];

        int count = InvokeConvertToTarget(src, src.Length, srcFmt, 48000, 4, ref dst, ref scratch);

        Assert.Equal(4, count);
        Assert.Equal(0.1f, dst[0], precision: 6);
        Assert.Equal(0.9f, dst[1], precision: 6);
        Assert.Equal(0.9f, dst[2], precision: 6);
        Assert.Equal(0.9f, dst[3], precision: 6);
    }

    [Fact]
    public void ConvertToTarget_SixChannelsToStereo_Truncates()
    {
        // 6ch → 2ch also hits the "else" branch.
        var srcFmt = new CSCore.WaveFormat(48000, 16, 6);
        float[] src = { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f }; // 1 6-ch frame
        float[] dst = new float[16];
        float[] scratch = new float[16];

        int count = InvokeConvertToTarget(src, src.Length, srcFmt, 48000, 2, ref dst, ref scratch);

        Assert.Equal(2, count);
        Assert.Equal(0.1f, dst[0], precision: 6);
        Assert.Equal(0.2f, dst[1], precision: 6);
    }

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
    // EnsureRingCapacity  (private instance)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EnsureRingCapacity_GrowsWhenFull()
    {
        using var engine = new RecorderEngine();
        var method = GetInstance("EnsureRingCapacity");
        var ringField = EngineType.GetField("_micRing",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var ring = (float[])ringField.GetValue(engine)!;
        int initialLen = ring.Length;

        // Request far more room than the buffer currently has
        method.Invoke(engine, new object[] { initialLen * 10 });

        var newRing = (float[])ringField.GetValue(engine)!;
        Assert.NotSame(ring, newRing);
        Assert.True(newRing.Length >= initialLen * 10,
            $"Expected >= {initialLen * 10}, got {newRing.Length}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ReadExactSamples  (private static, ISampleSource consumer)
    // ─────────────────────────────────────────────────────────────────────────

    private sealed class FakeSampleSource : ISampleSource
    {
        private readonly float[] _samples;
        private int _pos;
        private readonly int _readPerCall;

        public FakeSampleSource(float[] samples, int readPerCall)
        {
            _samples = samples;
            _readPerCall = readPerCall;
            WaveFormat = new CSCore.WaveFormat(48000, 32, 2);
        }

        public CSCore.WaveFormat WaveFormat { get; }
        public bool CanSeek => false;
        public long Position { get => _pos; set => throw new NotSupportedException(); }
        public long Length => _samples.Length;

        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(Math.Min(count, _readPerCall), _samples.Length - _pos);
            if (n <= 0) return 0;
            Array.Copy(_samples, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }

        public void Dispose() { }
    }

    private static int ReadExactSamplesInvoke(ISampleSource src, float[] dst, int count)
        => (int)GetStatic("ReadExactSamples").Invoke(null, new object[] { src, dst, count })!;

    [Fact]
    public void ReadExactSamples_ShortRead_LoopsUntilAllRead()
    {
        // Fake source returns at most 3 samples per call.
        // Asking for 10 should produce 3+3+3+1 = 10 via the internal loop.
        var src = new FakeSampleSource(new float[10], readPerCall: 3);
        var dst = new float[10];
        int got = ReadExactSamplesInvoke(src, dst, 10);
        Assert.Equal(10, got);
    }

    [Fact]
    public void ReadExactSamples_EOF_ReturnsAvailableCount()
    {
        // Source has 5 samples; asking for 10 should return 5 (no padding).
        var src = new FakeSampleSource(new float[5], readPerCall: int.MaxValue);
        var dst = new float[10];
        int got = ReadExactSamplesInvoke(src, dst, 10);
        Assert.Equal(5, got);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SoftClip — NaN / Infinity propagation
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SoftClip_NaN_ReturnsZero()
        => Assert.Equal(0f, SoftClip(float.NaN));

    [Fact]
    public void SoftClip_PositiveInfinity_ReturnsOne()
        => Assert.Equal(1f, SoftClip(float.PositiveInfinity), precision: 6);

    [Fact]
    public void SoftClip_NegativeInfinity_ReturnsMinusOne()
        => Assert.Equal(-1f, SoftClip(float.NegativeInfinity), precision: 6);

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
