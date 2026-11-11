using System;
using Xunit;

namespace Hearbud.Tests;

/// <summary>
/// Unit tests for the offline splice scanner. Pure buffers in, verdicts out —
/// no audio hardware, no files, no devices (same policy as the engine tests).
/// </summary>
public class TakeVerifierTests
{
    private const int Rate = 48000;
    private const int Ch = 2;

    private static float[] StereoSine(double freq, double amp, double seconds, double phase = 0)
    {
        int frames = (int)(seconds * Rate);
        var f = new float[frames * Ch];
        for (int t = 0; t < frames; t++)
        {
            float v = (float)(amp * Math.Sin(2 * Math.PI * freq * t / Rate + phase));
            f[t * Ch] = v;
            f[t * Ch + 1] = v;
        }
        return f;
    }

    [Fact]
    public void Scan_CleanSine_NoSplice()
    {
        var s = StereoSine(440, 0.126, 3.0);
        var r = TakeVerifier.Scan(s, Ch, Rate);
        Assert.Equal(0, r.JumpRuns);
        Assert.Equal(0, r.BoundaryRuns);
        Assert.Empty(r.PadRuns);
        Assert.False(r.HasSplice);
    }

    [Fact]
    public void Scan_LeadingSilenceThenSpeech_NoSplice()
    {
        // A pause (long exact-zero run) is silence, not a pad.
        var s = StereoSine(440, 0.126, 3.0);
        var buf = new float[(int)(4.0 * Rate) * Ch];
        Array.Copy(s, 0, buf, (int)(1.0 * Rate) * Ch, s.Length);
        var r = TakeVerifier.Scan(buf, Ch, Rate);
        Assert.Single(r.SilenceRuns);
        Assert.Empty(r.PadRuns);
        Assert.False(r.HasSplice);
    }

    [Fact]
    public void Scan_EmbeddedZeroPad_Detected()
    {
        // 70 exact zeros mid-speech with audio both sides: the pad signature.
        var s = StereoSine(440, 0.126, 3.0);
        int at = 72000 * Ch; // 1.5 s in
        for (int i = 0; i < 70; i++) s[at + i] = 0f;
        var r = TakeVerifier.Scan(s, Ch, Rate);
        Assert.Single(r.PadRuns);
        Assert.True(r.HasSplice);
    }

    [Fact]
    public void Scan_FrameEdgeSteps_Detected()
    {
        // 60 step discontinuities on 480-frame edges over low bed sine.
        var s = StereoSine(440, 0.01, 8.0);
        for (int k = 1; k <= 60; k++)
        {
            int at = k * 960;
            if (at + 1 < s.Length) { s[at] = 0.5f; s[at + 1] = 0.5f; }
        }
        var r = TakeVerifier.Scan(s, Ch, Rate);
        Assert.True(r.JumpRuns >= 50);
        Assert.True(r.BoundaryRuns >= TakeVerifier.MinBoundaryRunsFail);
        Assert.True(r.HasSplice);
    }

    [Fact]
    public void IsSplice_Rule()
    {
        Assert.False(TakeVerifier.IsSplice(0, 0, 0));
        Assert.True(TakeVerifier.IsSplice(0, 0, 1)); // any pad fails
        Assert.False(TakeVerifier.IsSplice(49, 49, 0)); // below min runs
        Assert.False(TakeVerifier.IsSplice(60, 4, 0)); // below min boundary count
        Assert.False(TakeVerifier.IsSplice(600, 5, 0)); // below 1% fraction
        Assert.True(TakeVerifier.IsSplice(60, 60, 0));
    }

    [Fact]
    public void Scan_Pad_ReportsSidePeaks()
    {
        // Loud bed so the pad's neighbors are well above the floor.
        var s = StereoSine(440, 0.5, 3.0);
        int at = 72000 * Ch;
        for (int i = 0; i < 70; i++) s[at + i] = 0f;
        var r = TakeVerifier.Scan(s, Ch, Rate);
        var pad = Assert.Single(r.PadRuns);
        Assert.True(Math.Abs(pad.PrePeak - 0.5) < 0.02, $"pre {pad.PrePeak}");
        Assert.True(Math.Abs(pad.PostPeak - 0.5) < 0.02, $"post {pad.PostPeak}");
        Assert.True(r.HasSplice);
    }

    private static void WriteWav(string path, float[] s)
    {
        using var fs = new System.IO.FileStream(path, System.IO.FileMode.Create);
        using var bw = new System.IO.BinaryWriter(fs);
        bw.Write("RIFF".ToCharArray());
        bw.Write(36 + s.Length * 2);
        bw.Write("WAVE".ToCharArray());
        bw.Write("fmt ".ToCharArray());
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)Ch);
        bw.Write(Rate);
        bw.Write(Rate * Ch * 2);
        bw.Write((short)(Ch * 2));
        bw.Write((short)16);
        bw.Write("data".ToCharArray());
        bw.Write(s.Length * 2);
        foreach (var v in s) bw.Write((short)(v * 32767));
    }

    [Fact]
    public void ReadWav_RoundTrip()
    {
        // Writer-compatible parse: int16 stereo reads back at the right level.
        string tmp = System.IO.Path.GetTempFileName();
        try
        {
            var s = StereoSine(440, 0.5, 0.1);
            WriteWav(tmp, s);
            var back = TakeVerifier.ReadWavSamples(tmp, out int ch, out int rate);
            Assert.Equal(Ch, ch);
            Assert.Equal(Rate, rate);
            Assert.Equal(s.Length, back.Length);
            var r = TakeVerifier.Scan(back, ch, rate);
            Assert.True(Math.Abs(r.Peak - 0.5) < 0.001);
            Assert.False(r.HasSplice);
        }
        finally { System.IO.File.Delete(tmp); }
    }

    [Fact]
    public void VerifyTake_SpliceWithSessionLog_SurfacesStarvationContext()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hearbud-vt-" + System.Guid.NewGuid());
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            string mix = System.IO.Path.Combine(dir, "rec-test-mix.wav");
            // Embed a pad so the verdict is SPLICE.
            var s = StereoSine(440, 0.5, 3.0);
            int at = 72000 * Ch;
            for (int i = 0; i < 70; i++) s[at + i] = 0f;
            WriteWav(mix, s);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "rec-test.txt"),
                "[2026-11-11 09:09:23.431] WARN Warn: Capture starvation: loopback gap=4880.5ms (glitch #1)\n"
                + "[2026-11-11 09:09:29.680] INFO Info: Recording stopped\n");
            var sw = new System.IO.StringWriter();
            int code = TakeVerifier.VerifyTake(new[] { mix }, sw);
            string output = sw.ToString();
            Assert.Equal(1, code);
            Assert.Contains("gap=4880.5ms", output);
            Assert.Contains("informational, verdict unaffected", output);
        }
        finally { System.IO.Directory.Delete(dir, true); }
    }
}
