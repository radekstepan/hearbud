using Xunit;

namespace Hearbud.Tests;

/// <summary>Tests for the lightweight model / event-args types.</summary>
public class ModelTests
{
    // ── LevelChangedEventArgs ─────────────────────────────────────────────────

    [Fact]
    public void LevelChangedEventArgs_StoresSource()
    {
        var e = new LevelChangedEventArgs(LevelSource.Mic, 0.1f, 0.2f, false);
        Assert.Equal(LevelSource.Mic, e.Source);
    }

    [Fact]
    public void LevelChangedEventArgs_StoresSystemSource()
    {
        var e = new LevelChangedEventArgs(LevelSource.System, 0f, 0f, false);
        Assert.Equal(LevelSource.System, e.Source);
    }

    [Fact]
    public void LevelChangedEventArgs_StoresRms()
    {
        var e = new LevelChangedEventArgs(LevelSource.Mic, 0.42f, 0f, false);
        Assert.Equal(0.42f, e.Rms);
    }

    [Fact]
    public void LevelChangedEventArgs_StoresPeak()
    {
        var e = new LevelChangedEventArgs(LevelSource.Mic, 0f, 0.99f, false);
        Assert.Equal(0.99f, e.Peak);
    }

    [Fact]
    public void LevelChangedEventArgs_StoresClipped_True()
    {
        var e = new LevelChangedEventArgs(LevelSource.System, 0f, 1.1f, true);
        Assert.True(e.Clipped);
    }

    [Fact]
    public void LevelChangedEventArgs_StoresClipped_False()
    {
        var e = new LevelChangedEventArgs(LevelSource.System, 0f, 0.5f, false);
        Assert.False(e.Clipped);
    }

    // ── EngineStatusEventArgs ─────────────────────────────────────────────────

    [Fact]
    public void EngineStatusEventArgs_StoresKind()
    {
        var e = new EngineStatusEventArgs(EngineStatusKind.Info, "hello");
        Assert.Equal(EngineStatusKind.Info, e.Kind);
    }

    [Fact]
    public void EngineStatusEventArgs_StoresMessage()
    {
        var e = new EngineStatusEventArgs(EngineStatusKind.Error, "boom");
        Assert.Equal("boom", e.Message);
    }

    [Fact]
    public void EngineStatusEventArgs_DefaultSuccess_IsFalse()
    {
        var e = new EngineStatusEventArgs(EngineStatusKind.Info, "x");
        Assert.False(e.Success);
    }

    [Fact]
    public void EngineStatusEventArgs_StoresSuccess_True()
    {
        var e = new EngineStatusEventArgs(EngineStatusKind.Stopped, "done", success: true);
        Assert.True(e.Success);
    }

    [Fact]
    public void EngineStatusEventArgs_NullPaths_BecomeEmptyStrings()
    {
        var e = new EngineStatusEventArgs(EngineStatusKind.Stopped, "done");
        Assert.Equal("", e.OutputPathSystem);
        Assert.Equal("", e.OutputPathMic);
        Assert.Equal("", e.OutputPathMix);
        Assert.Equal("", e.OutputPathMp3);
    }

    [Fact]
    public void EngineStatusEventArgs_StoresOutputPaths()
    {
        var e = new EngineStatusEventArgs(
            EngineStatusKind.Stopped, "done", success: true,
            outputPathSystem: "/a/sys.wav",
            outputPathMic: "/a/mic.wav",
            outputPathMix: "/a/mix.wav",
            outputPathMp3: "/a/out.mp3");

        Assert.Equal("/a/sys.wav", e.OutputPathSystem);
        Assert.Equal("/a/mic.wav", e.OutputPathMic);
        Assert.Equal("/a/mix.wav", e.OutputPathMix);
        Assert.Equal("/a/out.mp3", e.OutputPathMp3);
    }

    [Fact]
    public void EngineStatusEventArgs_EncodingKind()
    {
        var e = new EngineStatusEventArgs(EngineStatusKind.Encoding, "Encoding MP3…");
        Assert.Equal(EngineStatusKind.Encoding, e.Kind);
    }

    // ── RecorderStartOptions ──────────────────────────────────────────────────

    [Fact]
    public void RecorderStartOptions_Default_IncludeMic_IsTrue()
        => Assert.True(new RecorderStartOptions().IncludeMic);

    [Fact]
    public void RecorderStartOptions_Default_Mp3BitrateKbps_Is192()
        => Assert.Equal(192, new RecorderStartOptions().Mp3BitrateKbps);

    [Fact]
    public void RecorderStartOptions_Default_OutputPath_IsEmpty()
        => Assert.Equal("", new RecorderStartOptions().OutputPath);

    [Fact]
    public void RecorderStartOptions_Default_LoopbackDeviceId_IsEmpty()
        => Assert.Equal("", new RecorderStartOptions().LoopbackDeviceId);

    [Fact]
    public void RecorderStartOptions_Default_MicDeviceId_IsNull()
        => Assert.Null(new RecorderStartOptions().MicDeviceId);

    [Fact]
    public void RecorderStartOptions_SetProperties_RoundTrip()
    {
        var opts = new RecorderStartOptions
        {
            OutputPath = "/tmp/test",
            LoopbackDeviceId = "dev-123",
            MicDeviceId = "mic-456",
            IncludeMic = false,
            Mp3BitrateKbps = 320
        };

        Assert.Equal("/tmp/test", opts.OutputPath);
        Assert.Equal("dev-123", opts.LoopbackDeviceId);
        Assert.Equal("mic-456", opts.MicDeviceId);
        Assert.False(opts.IncludeMic);
        Assert.Equal(320, opts.Mp3BitrateKbps);
    }

    // ── LevelSource enum ─────────────────────────────────────────────────────

    [Fact]
    public void LevelSource_HasThreeMembers_IncludingMixBus()
    {
        var values = Enum.GetValues<LevelSource>();
        Assert.Equal(3, values.Length);
        Assert.Contains(LevelSource.Mix, values);
    }

    // ── EngineStatusKind enum ─────────────────────────────────────────────────

    [Fact]
    public void EngineStatusKind_HasFourMembers()
    {
        var values = Enum.GetValues<EngineStatusKind>();
        Assert.Equal(4, values.Length);
    }
}
