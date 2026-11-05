using System;
using System.IO;
using System.Reflection;
using System.Text;
using Xunit;

namespace Hearbud.Tests;

/// <summary>
/// Tests for the Phase 3 owned WAV writer, including the mid-stream "crash"
/// simulation: after a patch, the file on disk must already be a valid WAV.
/// </summary>
public class StreamingWavWriterTests : IDisposable
{
    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(), $"hearbud-wav-{Guid.NewGuid()}");

    public StreamingWavWriterTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    private sealed record WavHeader(
        string Riff, uint RiffSize, string Wave, string Fmt, int FmtSize,
        short AudioFormat, short Channels, int SampleRate, int ByteRate,
        short BlockAlign, short Bits, string Data, uint DataSize);

    private static WavHeader ParseHeader(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var r = new BinaryReader(fs, Encoding.ASCII);
        string tag(int n) => Encoding.ASCII.GetString(r.ReadBytes(n));
        return new WavHeader(
            tag(4), r.ReadUInt32(), tag(4), tag(4), r.ReadInt32(),
            r.ReadInt16(), r.ReadInt16(), r.ReadInt32(), r.ReadInt32(),
            r.ReadInt16(), r.ReadInt16(), tag(4), r.ReadUInt32());
    }

    [Fact]
    public void Constructor_RejectsBadFormat()
    {
        var p = Path.Combine(_scratch, "x.wav.tmp");
        Assert.Throws<ArgumentOutOfRangeException>(() => new StreamingWavWriter(p, 0, 2, 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StreamingWavWriter(p, 48000, 0, 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StreamingWavWriter(p, 48000, 2, 24));
    }

    [Fact]
    public void Write16Bit_Finalize_HeaderAndBytesAreCorrect()
    {
        var p = Path.Combine(_scratch, "s.wav.tmp");
        byte[] pcm = { 0x01, 0x02, 0x03, 0x04 }; // 1 stereo 16-bit frame
        using (var w = new StreamingWavWriter(p, 48000, 2, 16))
        {
            w.Write(pcm, 0, pcm.Length);
            Assert.Equal(4, w.DataBytes);
            w.FinalizeFile();
        }

        var h = ParseHeader(p);
        Assert.Equal("RIFF", h.Riff);
        Assert.Equal("WAVE", h.Wave);
        Assert.Equal("fmt ", h.Fmt);
        Assert.Equal(16, h.FmtSize);
        Assert.Equal(1, h.AudioFormat); // integer PCM, never float
        Assert.Equal(2, h.Channels);
        Assert.Equal(48000, h.SampleRate);
        Assert.Equal(48000 * 4, h.ByteRate);
        Assert.Equal(4, h.BlockAlign);
        Assert.Equal(16, h.Bits);
        Assert.Equal("data", h.Data);
        Assert.Equal(4u, h.DataSize);
        Assert.Equal(40u, h.RiffSize); // file size (48) minus 8
        Assert.Equal(44 + 4, new FileInfo(p).Length);

        byte[] body = new byte[4];
        using (var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            fs.Seek(44, SeekOrigin.Begin);
            Assert.Equal(4, fs.Read(body, 0, 4));
        }
        Assert.Equal(pcm, body);
    }

    [Fact]
    public void WriteFloat32_HeaderMatchesFloatLayout()
    {
        // Phase 4: 32-bit means IEEE float (tag 3), the engine's only format.
        var p = Path.Combine(_scratch, "m.wav.tmp");
        using (var w = new StreamingWavWriter(p, 44100, 1, 32))
        {
            w.Write(new byte[] { 0x00, 0x00, 0x80, 0x3F }, 0, 4); // 1.0f, mono
            w.FinalizeFile();
        }

        var h = ParseHeader(p);
        Assert.Equal(3, h.AudioFormat);
        Assert.Equal(1, h.Channels);
        Assert.Equal(44100, h.SampleRate);
        Assert.Equal(44100 * 4, h.ByteRate);
        Assert.Equal(4, h.BlockAlign);
        Assert.Equal(32, h.Bits);
        Assert.Equal(4u, h.DataSize);
    }

    [Fact]
    public void WriteRf64_Ds64SizesAreCorrect()
    {
        var p = Path.Combine(_scratch, "r64.wav.tmp");
        byte[] payload = new byte[1920];
        new Random(3).NextBytes(payload);
        using (var w = new StreamingWavWriter(p, 48000, 2, 32, rf64: true))
        {
            w.Write(payload, 0, payload.Length);
            w.FinalizeFile();
        }

        Assert.Equal(80 + payload.Length, new FileInfo(p).Length);
        using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var r = new BinaryReader(fs, Encoding.ASCII);
        string tag(int n) => Encoding.ASCII.GetString(r.ReadBytes(n));
        Assert.Equal("RF64", tag(4));
        Assert.Equal(uint.MaxValue, r.ReadUInt32());
        Assert.Equal("WAVE", tag(4));
        Assert.Equal("ds64", tag(4));
        Assert.Equal(28, r.ReadInt32());
        Assert.Equal(72L + payload.Length, r.ReadInt64()); // riffSize64
        Assert.Equal((long)payload.Length, r.ReadInt64()); // dataSize64
        Assert.Equal(payload.Length / 8, r.ReadInt64());   // sampleCount64 (stereo float)
        Assert.Equal(0, r.ReadInt32());                     // tableLength
        Assert.Equal("fmt ", tag(4));
        r.ReadInt32(); // fmt size
        Assert.Equal(3, r.ReadInt16()); // IEEE float
    }

    [Fact]
    public void PatchHeader_MidStream_FileIsAlreadyAValidWav()
    {
        // Simulates a kill after the first patch: the file on disk must parse
        // with sizes matching the bytes written so far (playable prefix).
        var p = Path.Combine(_scratch, "c.wav.tmp");
        using (var w = new StreamingWavWriter(p, 48000, 2, 16))
        {
            w.Write(new byte[192_000], 0, 192_000); // 0.5 s stereo 16-bit
            w.PatchHeader();

            var mid = ParseHeader(p); // read WHILE the writer is still open
            Assert.Equal(192_000u, mid.DataSize);
            Assert.Equal(192_000u + 36u, mid.RiffSize);

            w.Write(new byte[192_000], 0, 192_000);
            w.FinalizeFile();
        }

        var full = ParseHeader(p);
        Assert.Equal(384_000u, full.DataSize);
    }

    [Fact]
    public void Dispose_WithoutFinalize_StillPatchesHeader()
    {
        var p = Path.Combine(_scratch, "d.wav.tmp");
        using (var w = new StreamingWavWriter(p, 48000, 2, 16))
        {
            w.Write(new byte[100], 0, 100);
        }

        Assert.Equal(100u, ParseHeader(p).DataSize);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var p = Path.Combine(_scratch, "i.wav.tmp");
        var w = new StreamingWavWriter(p, 48000, 2, 16);
        w.Write(new byte[8], 0, 8);
        w.Dispose();
        var ex = Record.Exception(() => w.Dispose());
        Assert.Null(ex);
        Assert.Equal(8u, ParseHeader(p).DataSize);
    }

    [Fact]
    public void Write_AfterDispose_Throws()
    {
        var p = Path.Combine(_scratch, "t.wav.tmp");
        var w = new StreamingWavWriter(p, 48000, 2, 16);
        w.Dispose();
        Assert.Throws<ObjectDisposedException>(() => w.Write(new byte[8], 0, 8));
    }

    [Fact]
    public void Output_IsReadableByNAudioWaveFileReader()
    {
        // Guards the MP3 handoff: StopAsync reads the mix back through NAudio.
        var p = Path.Combine(_scratch, "r.wav.tmp");
        byte[] pcm = new byte[1920]; // 480 stereo 16-bit frames
        new Random(7).NextBytes(pcm);
        using (var w = new StreamingWavWriter(p, 48000, 2, 16))
        {
            w.Write(pcm, 0, pcm.Length);
            w.FinalizeFile();
        }

        using var reader = new NAudio.Wave.WaveFileReader(p);
        Assert.Equal(48000, reader.WaveFormat.SampleRate);
        Assert.Equal(2, reader.WaveFormat.Channels);
        Assert.Equal(16, reader.WaveFormat.BitsPerSample);
        Assert.Equal(pcm.Length, reader.Length);
        byte[] back = new byte[pcm.Length];
        int total = 0, n;
        while ((n = reader.Read(back, total, back.Length - total)) > 0) total += n;
        Assert.Equal(pcm.Length, total);
        Assert.Equal(pcm, back);
    }

    [Fact]
    public void Rf64Float_IsReadableByNAudioWaveFileReader()
    {
        // The engine's production format (RF64 float32) must round-trip through
        // the same reader the MP3 step uses — this also proves our ds64.
        var p = Path.Combine(_scratch, "r64f.wav.tmp");
        float[] mix = new float[480 * 2];
        for (int i = 0; i < mix.Length; i++) mix[i] = (i % 7 - 3) * 0.125f;
        byte[] bytes = new byte[mix.Length * 4];
        Buffer.BlockCopy(mix, 0, bytes, 0, bytes.Length);
        using (var w = new StreamingWavWriter(p, 48000, 2, 32, rf64: true))
        {
            w.Write(bytes, 0, bytes.Length);
            w.FinalizeFile();
        }

        using var reader = new NAudio.Wave.WaveFileReader(p);
        Assert.Equal(48000, reader.WaveFormat.SampleRate);
        Assert.Equal(2, reader.WaveFormat.Channels);
        Assert.Equal(32, reader.WaveFormat.BitsPerSample);
        Assert.Equal(bytes.Length, reader.Length);
        byte[] back = new byte[bytes.Length];
        int total = 0, n;
        while ((n = reader.Read(back, total, back.Length - total)) > 0) total += n;
        Assert.Equal(bytes.Length, total);
        Assert.Equal(bytes, back);
    }

    [Fact]
    public void ClampRiffSize_SaturatesAt4GB()
    {
        // Private helper via reflection — writing 4 GB in a test would be silly.
        var m = typeof(StreamingWavWriter).GetMethod("ClampRiffSize",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        uint At(long bytes) => (uint)m.Invoke(null, new object[] { bytes })!;
        Assert.Equal(0u, At(0));
        Assert.Equal(123u, At(123));
        Assert.Equal(uint.MaxValue, At((long)uint.MaxValue));
        Assert.Equal(uint.MaxValue, At((long)uint.MaxValue + 1));
        Assert.Equal(uint.MaxValue, At(8L * 1024 * 1024 * 1024));
        Assert.Equal(0u, At(-5));
    }
}
