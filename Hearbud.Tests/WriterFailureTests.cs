using System;
using System.IO;
using Xunit;

namespace Hearbud.Tests;

// HResult is protected on Exception; subclass to inject disk-full codes without hardware.
sealed class HrIOException : IOException
{
    public HrIOException(string msg, int hr) : base(msg) { HResult = hr; }
}

public class WriterFailureTests : IDisposable
{
    private readonly string _tmp;
    public WriterFailureTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "hearbud-wf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    [Theory]
    [InlineData(unchecked((int)0x80070070), true)]
    [InlineData(unchecked((int)0x80070027), true)]
    [InlineData(unchecked((int)0x80070005), false)]
    public void IsDiskFull_HResult(int hr, bool expected)
        => Assert.Equal(expected, WriterHealth.IsDiskFull(new HrIOException("x", hr)));

    [Fact]
    public void IsDiskFull_MessageFallback_And_InnerChain()
    {
        Assert.True(WriterHealth.IsDiskFull(new IOException("There is not enough space on the disk.")));
        var wrapped = new InvalidOperationException("mux failed",
            new HrIOException("inner", unchecked((int)0x80070070)));
        Assert.True(WriterHealth.IsDiskFull(wrapped));
        Assert.False(WriterHealth.IsDiskFull(new IOException("device not ready")));
        Assert.False(WriterHealth.IsDiskFull(null));
    }

    [Fact]
    public void Tracker_StickyCounters_And_FirstError()
    {
        var t = new WriterFaultTracker();
        var first = new IOException("boom-a");
        Assert.False(t.Record(AudioFileTarget.Mix, first));
        t.Record(AudioFileTarget.Mix, new IOException("boom-b"));
        t.Record(AudioFileTarget.System, new IOException("boom-c"));
        Assert.Equal(2, t.MixErrors); Assert.Equal(1, t.SysErrors);
        Assert.Equal(0, t.MicErrors); Assert.Equal(3, t.TotalErrors);
        Assert.Same(first, t.FirstError); Assert.Equal(AudioFileTarget.Mix, t.FirstTarget);
        Assert.NotEqual(0, t.FirstErrorTick);
    }

    [Fact]
    public void Tracker_DiskFull_LatchesOnce()
    {
        var t = new WriterFaultTracker();
        Assert.True(t.Record(AudioFileTarget.Mix,
            new HrIOException("full", unchecked((int)0x80070070))));
        Assert.True(t.DiskFullLatched); Assert.NotEqual(0, t.DiskFullTick);
        t.Record(AudioFileTarget.Mic, new HrIOException("full2", unchecked((int)0x80070070)));
        Assert.Equal(AudioFileTarget.Mix, t.FirstTarget);
        t.Reset();
        Assert.Equal(0, t.TotalErrors); Assert.False(t.DiskFullLatched);
        Assert.Null(t.FirstError);
    }

    [Theory]
    [InlineData(74, 100, false)] [InlineData(75, 100, true)] [InlineData(90, 100, true)]
    [InlineData(0, 100, false)] [InlineData(5, 0, false)] [InlineData(3, 4, true)]
    public void HighWater_Threshold(int count, int cap, bool expected)
        => Assert.Equal(expected, WriterHealth.ShouldRaiseHighWater(count, cap));

    [Fact]
    public void Manifest_Begin_Unclosed_ScanFinds_MarkClosed_ScanClears()
    {
        string dir = Path.Combine(_tmp, "s"); Directory.CreateDirectory(dir);
        SessionManifest.Begin(dir, "rec-x", "s.wav", "m.wav", "x.wav", "x.mp3", 48000, 2, 32);
        string mp = SessionManifest.PathFor(dir, "rec-x");
        Assert.True(File.Exists(mp));
        var open = SessionManifest.ScanUnclosed(dir);
        Assert.Single(open); Assert.False(open[0].ClosedCleanly);
        SessionManifest.MarkClosed(dir, "rec-x", mixEnqueued: 42, diskFull: false,
            total: 0, sys: 0, mic: 0, mixErr: 0, firstTarget: null, firstMessage: null);
        Assert.Empty(SessionManifest.ScanUnclosed(dir));
        SessionManifest.MarkClosed(dir, "rec-x", 42, false, 0, 0, 0, 0, null, null);
    }

    [Fact]
    public void Manifest_CorruptJson_Ignored_BestEffort()
    {
        File.WriteAllText(Path.Combine(_tmp, "bad.hearbud-session.json"), "not json{{{");
        Assert.Empty(SessionManifest.ScanUnclosed(_tmp));
        SessionManifest.MarkClosed(_tmp, "missing", 0, false, 0, 0, 0, 0, null, null);
    }
}
