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
    public void ConvertToTarget_StereoToQuad_ZeroFillsSurrounds()
    {
        // 2ch → 4ch: L,R passthrough, surrounds zero (never duplicate R into LFE).
        var srcFmt = new CSCore.WaveFormat(48000, 16, 2);
        float[] src = { 0.1f, 0.9f }; // 1 stereo frame
        float[] dst = new float[16];
        float[] scratch = new float[16];

        int count = InvokeConvertToTarget(src, src.Length, srcFmt, 48000, 4, ref dst, ref scratch);

        Assert.Equal(4, count);
        Assert.Equal(0.1f, dst[0], precision: 6);
        Assert.Equal(0.9f, dst[1], precision: 6);
        Assert.Equal(0f, dst[2], precision: 6);
        Assert.Equal(0f, dst[3], precision: 6);
    }

    [Fact]
    public void ConvertToTarget_SixChannelsToStereo_FoldsCenterAndSurround_LfeExcluded()
    {
        // 6ch L,R,C,LFE,SL,SR → stereo fold: L+0.707C+0.5SL, R+0.707C+0.5SR; LFE excluded.
        var srcFmt = new CSCore.WaveFormat(48000, 16, 6);
        float[] src = { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f };
        float[] dst = new float[16];
        float[] scratch = new float[16];

        int count = InvokeConvertToTarget(src, src.Length, srcFmt, 48000, 2, ref dst, ref scratch);

        Assert.Equal(2, count);
        Assert.Equal(0.1f + 0.7071f * 0.3f + 0.5f * 0.5f, dst[0], precision: 4);
        Assert.Equal(0.2f + 0.7071f * 0.3f + 0.5f * 0.6f, dst[1], precision: 4);
    }

    [Fact]
    public void ConvertToTarget_SixChannels_LfeOnly_Excluded()
    {
        // LFE-only must not leak into stereo (documents intentional drop).
        var srcFmt = new CSCore.WaveFormat(48000, 16, 6);
        float[] src = { 0f, 0f, 0f, 1f, 0f, 0f };
        float[] dst = new float[16];
        float[] scratch = new float[16];

        int count = InvokeConvertToTarget(src, src.Length, srcFmt, 48000, 2, ref dst, ref scratch);

        Assert.Equal(2, count);
        Assert.Equal(0f, dst[0], precision: 6);
        Assert.Equal(0f, dst[1], precision: 6);
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
    public void EnsureRingCapacity_Bounded_DropsOldestAndCounts()
    {
        using var engine = new RecorderEngine();
        var method = GetInstance("EnsureRingCapacity");
        var ringField = EngineType.GetField("_micRing",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var overField = EngineType.GetField("_micOverrunBlocks",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var ring = (float[])ringField.GetValue(engine)!;
        int initialLen = ring.Length;

        // Request far more room than the bounded cap allows: must not grow unbounded,
        // must count an overrun instead.
        method.Invoke(engine, new object[] { initialLen * 10 });

        var newRing = (float[])ringField.GetValue(engine)!;
        Assert.True(newRing.Length <= initialLen * 2,
            $"Bounded FIFO must not grow 10x; got {newRing.Length} from {initialLen}");
        Assert.True((long)overField.GetValue(engine)! > 0,
            "Overrun counter must increment when demand exceeds cap");
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
    public void WriteLog_TruncatesAtMaxSize_KeepsWarnAndSummary()
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
            writeLog.Invoke(engine, new object?[] { "INFO", "routine noise after cap", null });
            writeLog.Invoke(engine, new object?[] { "WARN", "still visible after cap", null });
            writeLog.Invoke(engine, new object?[] { "INFO", "Mic ring underruns: 3. Overruns: 1", null });

            var logField = EngineType.GetField("_log",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            Assert.NotNull(logField.GetValue(engine));

            // Close the handle before reading: the log FileStream is opened
            // with FileShare.Read only, which blocks even File.ReadAllText
            // while the StreamWriter is alive. Dispose is idempotent.
            engine.Dispose();

            var content = File.ReadAllText(tempLog);
            Assert.Contains("LOG TRUNCATED", content);
            Assert.Contains("still visible after cap", content);
            Assert.DoesNotContain("routine noise after cap", content);
            Assert.Contains("underruns", content);
        }
        finally
        {
            // Dispose first: _log holds the file open (FileShare.Read, no
            // FileShare.Delete), so deleting while the engine is alive throws
            // IOException on Windows. Dispose is idempotent (using var re-disposes).
            try { engine.Dispose(); } catch { }
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

    // ─────────────────────────────────────────────────────────────────────────
    // ConvertToTargetChunked — phase carry + cumulative length
    // ─────────────────────────────────────────────────────────────────────────

    private static int InvokeChunked(
        float[] src, int floatCount, CSCore.WaveFormat srcFmt,
        int dstRate, int dstCh,
        ref float[] dst, ref float[] scratch, object state)
    {
        var method = EngineType.GetMethod("ConvertToTargetChunked",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var args = new object[] { src, floatCount, srcFmt, dstRate, dstCh, dst, scratch, state };
        int result = (int)method.Invoke(null, args)!;
        dst = (float[])args[5];
        scratch = (float[])args[6];
        return result;
    }

    private static object NewResamplerState(int srcCh, int srcRate, int dstRate)
    {
        var nested = EngineType.GetNestedType("ResamplerState", BindingFlags.NonPublic)!;
        var st = Activator.CreateInstance(nested)!;
        nested.GetMethod("Reset")!.Invoke(st, new object[] { srcCh, srcRate, dstRate });
        return st;
    }

    [Fact]
    public void ChunkedVsSingle_NoClick_AtSeams()
    {
        // 16 kHz mono sine, chunked vs single-shot must match (no phase-restart click).
        var srcFmt = new CSCore.WaveFormat(16000, 16, 1);
        int total = 4800;
        var src = new float[total];
        for (int i = 0; i < total; i++) src[i] = (float)Math.Sin(2 * Math.PI * 1000 * i / 16000);

        float[] dstSingle = new float[20000];
        float[] scrSingle = new float[20000];
        int nSingle = InvokeConvertToTarget(src, total, srcFmt, 48000, 1, ref dstSingle, ref scrSingle);

        var st = NewResamplerState(1, 16000, 48000);
        var chunked = new System.Collections.Generic.List<float>();
        int chunk = 160;
        for (int off = 0; off < total; off += chunk)
        {
            int n = Math.Min(chunk, total - off);
            var piece = new float[n];
            Array.Copy(src, off, piece, 0, n);
            float[] d = new float[1000];
            float[] s = new float[1000];
            int got = InvokeChunked(piece, n, srcFmt, 48000, 1, ref d, ref s, st);
            for (int i = 0; i < got; i++) chunked.Add(d[i]);
        }

        Assert.Equal(nSingle, chunked.Count);
        double maxDiff = 0;
        for (int i = 0; i < nSingle; i++)
            maxDiff = Math.Max(maxDiff, Math.Abs(dstSingle[i] - chunked[i]));
        Assert.True(maxDiff < 1e-4, $"chunked vs single diff {maxDiff}");
    }

    [Fact]
    public void CumulativeLength_WithinOneFramePerSecond()
    {
        // 44.1 k → 48 k, 100 x 512-frame chunks: total within 1 frame of ideal.
        var srcFmt = new CSCore.WaveFormat(44100, 16, 1);
        var st = NewResamplerState(1, 44100, 48000);
        long totalSrc = 0;
        int totalDst = 0;
        var rnd = new Random(42);
        for (int k = 0; k < 100; k++)
        {
            var piece = new float[512];
            for (int i = 0; i < 512; i++) piece[i] = (float)(rnd.NextDouble() * 2 - 1);
            float[] d = new float[2048];
            float[] s = new float[2048];
            totalDst += InvokeChunked(piece, 512, srcFmt, 48000, 1, ref d, ref s, st);
            totalSrc += 512;
        }
        int ideal = (int)Math.Floor((double)totalSrc * 48000 / 44100);
        Assert.InRange(totalDst, ideal - 1, ideal + 1);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GAP-01 fixed-frame dual-FIFO tick (private instance, reflection)
    // ─────────────────────────────────────────────────────────────────────────

    private static T Field<T>(object o, string name)
        => (T)EngineType.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;

    private static void SetField(object o, string name, object v)
        => EngineType.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(o, v);

    [Fact]
    public void FixedFrameFloats_Stereo_Is960()
    {
        using var engine = new RecorderEngine();
        SetField(engine, "_outChannels", 2);
        int n = (int)GetInstance("FixedFrameFloats").Invoke(engine, null)!;
        Assert.Equal(480 * 2, n);
    }

    [Fact]
    public void DualFifo_FixedFrame_RoundTrips()
    {
        using var engine = new RecorderEngine();
        SetField(engine, "_outChannels", 2);
        SetField(engine, "_outRate", 48000);
        SetField(engine, "_preRollRemaining", 0);
        int n = (int)GetInstance("FixedFrameFloats").Invoke(engine, null)!;
        var src = new float[n];
        for (int i = 0; i < n; i++) src[i] = i * 0.001f;
        GetInstance("PushLoopRing").Invoke(engine, new object[] { src, n });
        // Feed mic ring directly via EnsureRingCapacity + field write.
        var micRing = Field<float[]>(engine, "_micRing");
        Assert.True(micRing.Length >= n);
        Array.Copy(src, 0, micRing, 0, n);
        SetField(engine, "_micW", n % micRing.Length);
        SetField(engine, "_micR", 0);
        SetField(engine, "_micCount", n);
        var loopDst = new float[n];
        var micDst = new float[n];
        bool ok = (bool)GetInstance("TryPopFixedFrame").Invoke(engine, new object[] { loopDst, micDst })!;
        Assert.True(ok);
        Assert.Equal(src[0], loopDst[0], precision: 6);
        Assert.Equal(src[n - 1], micDst[n - 1], precision: 6);
        Assert.Equal(0, Field<int>(engine, "_micCount"));
        Assert.Equal(0, Field<int>(engine, "_loopCount"));
    }

    [Fact]
    public void TryPopFixedFrame_MicShortfall_ZeroFillsAndCountsUnderrun()
    {
        using var engine = new RecorderEngine();
        SetField(engine, "_outChannels", 2);
        SetField(engine, "_outRate", 48000);
        SetField(engine, "_preRollRemaining", 0);
        int n = (int)GetInstance("FixedFrameFloats").Invoke(engine, null)!;
        var src = new float[n];
        for (int i = 0; i < n; i++) src[i] = 0.25f;
        GetInstance("PushLoopRing").Invoke(engine, new object[] { src, n });
        var loopDst = new float[n];
        var micDst = new float[n];
        for (int i = 0; i < n; i++) micDst[i] = 99f;
        bool ok = (bool)GetInstance("TryPopFixedFrame").Invoke(engine, new object[] { loopDst, micDst })!;
        Assert.True(ok);
        Assert.Equal(0f, micDst[0], precision: 6);
        Assert.Equal(0f, micDst[n - 1], precision: 6);
        Assert.True(Field<long>(engine, "_micUnderrunBlocks") >= 1);
    }

    [Fact]
    public void CheckLagResync_SustainedBacklog_DropsToOneFrame()
    {
        using var engine = new RecorderEngine();
        SetField(engine, "_outChannels", 2);
        SetField(engine, "_outRate", 48000);
        int n = (int)GetInstance("FixedFrameFloats").Invoke(engine, null)!;
        var micRing = Field<float[]>(engine, "_micRing");
        int fill = Math.Min(micRing.Length - 1, n * 4);
        SetField(engine, "_micR", 0);
        SetField(engine, "_micW", fill % micRing.Length);
        SetField(engine, "_micCount", fill);
        var m = GetInstance("CheckLagResync");
        Assert.False((bool)m.Invoke(engine, new object[] { 0.5 })!);
        Assert.False((bool)m.Invoke(engine, new object[] { 0.5 })!);
        Assert.True((bool)m.Invoke(engine, new object[] { 0.5 })!);
        Assert.Equal(n, Field<int>(engine, "_micCount"));
        Assert.Equal(1L, Field<long>(engine, "_resyncCount"));
    }

    [Fact]
    public void CheckLagResync_BelowThreshold_NoResync()
    {
        using var engine = new RecorderEngine();
        var m = GetInstance("CheckLagResync");
        Assert.False((bool)m.Invoke(engine, new object[] { 0.01 })!);
        Assert.Equal(0L, Field<long>(engine, "_resyncCount"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TASK-1 sinc resampler (SincPolyphase + ResamplerState)
    // ─────────────────────────────────────────────────────────────────────────

    private static Type SincType()
    {
        var t = EngineType.GetNestedType("SincPolyphase",
            BindingFlags.NonPublic | BindingFlags.Public);
        Assert.True(t != null, "Nested type 'SincPolyphase' not found.");
        return t!;
    }

    [Fact]
    public void Sinc_Table_Dimensions_And_DC_Normalized()
    {
        var t = SincType();
        Assert.Equal(32, (int)t.GetField("Phases")!.GetValue(null)!);
        Assert.Equal(12, (int)t.GetField("Taps")!.GetValue(null)!);
        Assert.Equal(6, (int)t.GetField("Half")!.GetValue(null)!);
        var table = (float[,])t.GetField("Table")!.GetValue(null)!;
        Assert.Equal(32, table.GetLength(0));
        Assert.Equal(12, table.GetLength(1));
        // Each phase must have unity DC gain (flat passband, no level shift).
        for (int p = 0; p < 32; p++)
        {
            double sum = 0;
            for (int k = 0; k < 12; k++) sum += table[p, k];
            Assert.InRange(sum, 0.999, 1.001);
        }
    }

    [Fact]
    public void Sinc_DelayDstFrames_ReflectsHalf()
    {
        // Half=6 src samples -> dst frames: 6 * dst/src.
        var st = NewResamplerState(1, 16000, 48000);
        var prop = st.GetType().GetProperty("DelayDstFrames")!;
        double delay = (double)prop.GetValue(st)!;
        Assert.Equal(6.0 * 48000 / 16000, delay, precision: 6);
    }

    [Fact]
    public void Sinc_ChunkedVsSingle_BitIdentical()
    {
        // 16 kHz mono sine, chunked vs single-shot must be bit-identical
        // (linear-phase kernel centred in the past; old linear impl failed this).
        var srcFmt = new CSCore.WaveFormat(16000, 16, 1);
        int total = 4800;
        var src = new float[total];
        for (int i = 0; i < total; i++) src[i] = (float)Math.Sin(2 * Math.PI * 1000 * i / 16000);

        float[] dstSingle = new float[20000];
        float[] scrSingle = new float[20000];
        int nSingle = InvokeConvertToTarget(src, total, srcFmt, 48000, 1, ref dstSingle, ref scrSingle);

        var st = NewResamplerState(1, 16000, 48000);
        var chunked = new System.Collections.Generic.List<float>();
        int chunk = 160;
        for (int off = 0; off < total; off += chunk)
        {
            int n = Math.Min(chunk, total - off);
            var piece = new float[n];
            Array.Copy(src, off, piece, 0, n);
            float[] d = new float[1000];
            float[] s = new float[1000];
            int got = InvokeChunked(piece, n, srcFmt, 48000, 1, ref d, ref s, st);
            for (int i = 0; i < got; i++) chunked.Add(d[i]);
        }

        Assert.Equal(nSingle, chunked.Count);
        double maxDiff = 0;
        for (int i = 0; i < nSingle; i++)
            maxDiff = Math.Max(maxDiff, Math.Abs(dstSingle[i] - chunked[i]));
        Assert.True(maxDiff == 0.0, $"sinc chunked vs single must be bit-identical, got {maxDiff}");
    }

    [Fact]
    public void Sinc_ShortChunks_HistoryMerge_StillIdentical()
    {
        // Chunks shorter than HistFrames (16) exercise the merge-shift path.
        var srcFmt = new CSCore.WaveFormat(16000, 16, 1);
        int total = 480;
        var src = new float[total];
        for (int i = 0; i < total; i++) src[i] = (float)Math.Sin(2 * Math.PI * 500 * i / 16000);

        float[] dstSingle = new float[4000];
        float[] scrSingle = new float[4000];
        int nSingle = InvokeConvertToTarget(src, total, srcFmt, 48000, 1, ref dstSingle, ref scrSingle);

        var st = NewResamplerState(1, 16000, 48000);
        var chunked = new System.Collections.Generic.List<float>();
        int chunk = 8; // < HistFrames
        for (int off = 0; off < total; off += chunk)
        {
            int n = Math.Min(chunk, total - off);
            var piece = new float[n];
            Array.Copy(src, off, piece, 0, n);
            float[] d = new float[500];
            float[] s = new float[500];
            int got = InvokeChunked(piece, n, srcFmt, 48000, 1, ref d, ref s, st);
            for (int i = 0; i < got; i++) chunked.Add(d[i]);
        }

        Assert.Equal(nSingle, chunked.Count);
        double maxDiff = 0;
        for (int i = 0; i < nSingle; i++)
            maxDiff = Math.Max(maxDiff, Math.Abs(dstSingle[i] - chunked[i]));
        Assert.True(maxDiff == 0.0, $"short-chunk history merge must stay bit-identical, got {maxDiff}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TASK-3 continuous drift trim (TryGentleTrim)
    // ─────────────────────────────────────────────────────────────────────────

    private static double InvokeTrim(object engine, double backlogSec)
        => (double)GetInstance("TryGentleTrim").Invoke(engine, new object[] { backlogSec })!;

    [Fact]
    public void Trim_Deadband_NoOp()
    {
        using var engine = new RecorderEngine();
        SetField(engine, "_outChannels", 2);
        SetField(engine, "_outRate", 48000);
        // Target with zero resampler delay = 1 frame = 960 floats = 0.01 s.
        SetField(engine, "_micCount", 960);
        long before = engine.TrimDropped + engine.TrimInserted;
        double outBacklog = InvokeTrim(engine, 0.01);
        Assert.Equal(0.01, outBacklog, precision: 9);
        Assert.Equal(before, engine.TrimDropped + engine.TrimInserted);
    }

    [Fact]
    public void Trim_SmallPositive_DropsBounded_AndCounts()
    {
        using var engine = new RecorderEngine();
        SetField(engine, "_outChannels", 2);
        SetField(engine, "_outRate", 48000);
        var ring = Field<float[]>(engine, "_micRing");
        Assert.True(ring.Length >= 1920);
        SetField(engine, "_micR", 0);
        SetField(engine, "_micW", 1920 % ring.Length);
        SetField(engine, "_micCount", 1920); // backlog 0.02 s, 10 ms over target
        double outBacklog = InvokeTrim(engine, 0.02);
        // Must trim 8..96 floats (frame-aligned), never burst-drop to one frame.
        long dropped = engine.TrimDropped;
        Assert.InRange(dropped, 8, 96);
        Assert.Equal(1920 - (int)dropped, Field<int>(engine, "_micCount"));
        Assert.True(outBacklog < 0.02 && outBacklog >= 0.018);
        Assert.Equal(0L, Field<long>(engine, "_resyncCount"));
    }

    [Fact]
    public void Trim_LargeBacklog_Passthrough_ToResync()
    {
        using var engine = new RecorderEngine();
        SetField(engine, "_outChannels", 2);
        SetField(engine, "_outRate", 48000);
        var ring = Field<float[]>(engine, "_micRing");
        int fill = Math.Min(ring.Length - 1, 48000);
        SetField(engine, "_micR", 0);
        SetField(engine, "_micW", fill % ring.Length);
        SetField(engine, "_micCount", fill);
        double outBacklog = InvokeTrim(engine, 0.5);
        Assert.Equal(0.5, outBacklog, precision: 9); // >=80 ms: leave to CheckLagResync
        Assert.Equal(0L, engine.TrimDropped);
        Assert.Equal(fill, Field<int>(engine, "_micCount"));
    }

    [Fact]
    public void Trim_Negative_InsertsFade_AndCounts()
    {
        using var engine = new RecorderEngine();
        SetField(engine, "_outChannels", 2);
        SetField(engine, "_outRate", 48000);
        SetField(engine, "_micR", 0);
        SetField(engine, "_micW", 0);
        SetField(engine, "_micCount", 0); // backlog 0, 10 ms under target
        double outBacklog = InvokeTrim(engine, 0.0);
        long inserted = engine.TrimInserted;
        Assert.InRange(inserted, 8, 96);
        Assert.Equal((int)inserted, Field<int>(engine, "_micCount"));
        Assert.True(outBacklog > 0.0 && outBacklog <= 0.002);
    }

    [Fact]
    public void Trim_ZeroCrossDrop_IsFrameAligned_WithinWindow()
    {
        using var engine = new RecorderEngine();
        var m = GetInstance("FindZeroCrossDrop");
        var ring = new float[4096];
        for (int i = 0; i < ring.Length; i++)
            ring[i] = (float)Math.Sin(2 * Math.PI * 440 * i / 48000);
        int got = (int)m.Invoke(engine, new object[] { ring, 100, 2000, 90, 2 })!;
        Assert.Equal(0, got % 2); // frame-aligned for stereo
        Assert.InRange(got, 90 - 16, 90 + 16);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TASK-2 capture scheduling (RecordCallbackTiming / MMCSS helpers)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Capture_RecordTiming_FirstCall_NoGlitch_ThenGap_Counts()
    {
        using var engine = new RecorderEngine();
        var m = GetInstance("RecordCallbackTiming");
        // First call per source: prev == 0, no delta yet.
        SetField(engine, "_lastMicTick", 0L);
        m.Invoke(engine, new object[] { false });
        Assert.Equal(0L, engine.CaptureGlitches);

        // Simulate a 60 ms gap on loopback by back-dating the tick.
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long sixtyMs = (long)(System.Diagnostics.Stopwatch.Frequency * 0.06);
        SetField(engine, "_lastLoopTick", now - sixtyMs);
        m.Invoke(engine, new object[] { true });
        Assert.Equal(1L, engine.CaptureGlitches);

        // Immediate follow-up (<50 ms) must not count again.
        m.Invoke(engine, new object[] { true });
        Assert.Equal(1L, engine.CaptureGlitches);
    }

    [Fact]
    public void Capture_BoostAndRelease_AreSafe_Idempotent()
    {
        using var engine = new RecorderEngine();
        var ensure = GetInstance("EnsureCaptureBoost");
        var release = GetInstance("ReleaseCaptureMmcss");
        // Must never throw, even on Linux where MMCSS is a no-op (returns 0).
        ensure.Invoke(engine, null);
        ensure.Invoke(engine, null);
        release.Invoke(engine, null);
        release.Invoke(engine, null);
        Assert.True(engine.CaptureGlitches >= 0);
    }

    [Fact]
    public void NativeTiming_BeginEnd_RefCounted_Idempotent()
    {
        var nested = EngineType.GetNestedType("NativeTiming",
            BindingFlags.NonPublic | BindingFlags.Public)!;
        var begin = nested.GetMethod("Begin1Ms",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        var end = nested.GetMethod("End1Ms",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        // Unbalanced extra End must not throw nor underflow (idempotent drain).
        begin.Invoke(null, null);
        begin.Invoke(null, null);
        end.Invoke(null, null);
        end.Invoke(null, null);
        end.Invoke(null, null);
    }
}
