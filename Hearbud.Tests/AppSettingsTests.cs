using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace Hearbud.Tests;

/// <summary>
/// Tests for AppSettings load / save / validate.
/// We back up and restore ~/.hearbud_config.json around each test so the
/// user's real config is never corrupted.
/// </summary>
public class AppSettingsTests : IDisposable
{
    private static string ConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     ".hearbud_config.json");

    private readonly string? _backup;

    public AppSettingsTests()
    {
        // Back up existing config before each test
        if (File.Exists(ConfigPath))
            _backup = File.ReadAllText(ConfigPath);
        else
            _backup = null;
    }

    public void Dispose()
    {
        // Restore config after each test
        if (_backup != null)
            File.WriteAllText(ConfigPath, _backup);
        else if (File.Exists(ConfigPath))
            File.Delete(ConfigPath);
        GC.SuppressFinalize(this);
    }

    // ── Default values ────────────────────────────────────────────────────────

    [Fact]
    public void Defaults_MicGain_IsOne()
        => Assert.Equal(1.0, new AppSettings().MicGain);

    [Fact]
    public void Defaults_LoopGain_IsOne()
        => Assert.Equal(1.0, new AppSettings().LoopGain);

    [Fact]
    public void Defaults_IncludeMic_IsTrue()
        => Assert.True(new AppSettings().IncludeMic);

    [Fact]
    public void Defaults_Mp3BitrateKbps_Is192()
        => Assert.Equal(192, new AppSettings().Mp3BitrateKbps);

    [Fact]
    public void Defaults_OutputDir_IsNotEmpty()
        => Assert.False(string.IsNullOrWhiteSpace(new AppSettings().OutputDir));

    [Fact]
    public void Defaults_OutputDir_ContainsMusicFolder()
    {
        var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        Assert.StartsWith(music, new AppSettings().OutputDir,
                          StringComparison.OrdinalIgnoreCase);
    }

    // ── Save → Load round-trips ───────────────────────────────────────────────

    [Fact]
    public void SaveLoad_RoundTrips_MicGain()
    {
        var s = new AppSettings { MicGain = 2.5 };
        s.Save();
        Assert.Equal(2.5, AppSettings.Load().MicGain);
    }

    [Fact]
    public void SaveLoad_RoundTrips_LoopGain()
    {
        var s = new AppSettings { LoopGain = 0.75 };
        s.Save();
        Assert.Equal(0.75, AppSettings.Load().LoopGain);
    }

    [Fact]
    public void SaveLoad_RoundTrips_Mp3BitrateKbps()
    {
        var s = new AppSettings { Mp3BitrateKbps = 128 };
        s.Save();
        Assert.Equal(128, AppSettings.Load().Mp3BitrateKbps);
    }

    [Fact]
    public void SaveLoad_ZeroBitrate_IsPreserved()
    {
        var s = new AppSettings { Mp3BitrateKbps = 0 };
        s.Save();
        Assert.Equal(0, AppSettings.Load().Mp3BitrateKbps);
    }

    [Fact]
    public void SaveLoad_RoundTrips_IncludeMic()
    {
        var s = new AppSettings { IncludeMic = false };
        s.Save();
        Assert.False(AppSettings.Load().IncludeMic);
    }

    [Fact]
    public void SaveLoad_RoundTrips_OutputDir()
    {
        var s = new AppSettings { OutputDir = "/tmp/test-recordings" };
        s.Save();
        Assert.Equal("/tmp/test-recordings", AppSettings.Load().OutputDir);
    }

    [Fact]
    public void Save_ClearsLegacyMp3Quality()
    {
        var s = new AppSettings { Mp3Quality = "320 kbps" };
        s.Save();
        var json = File.ReadAllText(ConfigPath);
        // Mp3Quality is nulled out and written as null (or omitted)
        var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("Mp3Quality", out var prop))
            Assert.Equal(JsonValueKind.Null, prop.ValueKind);
    }

    // ── Validation / clamping ─────────────────────────────────────────────────

    [Fact]
    public void Load_ClampsMicGainAbove3()
    {
        var json = """{"MicGain":99.0,"LoopGain":1.0,"Mp3BitrateKbps":192,"IncludeMic":true,"OutputDir":"/tmp"}""";
        File.WriteAllText(ConfigPath, json);
        var s = AppSettings.Load();
        Assert.Equal(3.0, s.MicGain);
    }

    [Fact]
    public void Load_ClampsGainBelowZero()
    {
        var json = """{"MicGain":-5.0,"LoopGain":1.0,"Mp3BitrateKbps":192,"IncludeMic":true,"OutputDir":"/tmp"}""";
        File.WriteAllText(ConfigPath, json);
        var s = AppSettings.Load();
        Assert.Equal(0.0, s.MicGain);
    }

    [Fact]
    public void Load_ClampsBitrateBelow64_To64()
    {
        var json = """{"MicGain":1.0,"LoopGain":1.0,"Mp3BitrateKbps":32,"IncludeMic":true,"OutputDir":"/tmp"}""";
        File.WriteAllText(ConfigPath, json);
        var s = AppSettings.Load();
        Assert.Equal(64, s.Mp3BitrateKbps);
    }

    [Fact]
    public void Load_ClampsBitrateAbove320_To320()
    {
        var json = """{"MicGain":1.0,"LoopGain":1.0,"Mp3BitrateKbps":999,"IncludeMic":true,"OutputDir":"/tmp"}""";
        File.WriteAllText(ConfigPath, json);
        var s = AppSettings.Load();
        Assert.Equal(320, s.Mp3BitrateKbps);
    }

    [Fact]
    public void Load_EmptyOutputDir_FallsBackToDefault()
    {
        var json = """{"MicGain":1.0,"LoopGain":1.0,"Mp3BitrateKbps":192,"IncludeMic":true,"OutputDir":""}""";
        File.WriteAllText(ConfigPath, json);
        var s = AppSettings.Load();
        Assert.False(string.IsNullOrWhiteSpace(s.OutputDir));
    }

    [Fact]
    public void Load_CorruptJson_ReturnsDefaults()
    {
        File.WriteAllText(ConfigPath, "{{{{ not valid json %%%%");
        var s = AppSettings.Load();
        Assert.Equal(1.0, s.MicGain);
        Assert.Equal(192, s.Mp3BitrateKbps);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
        var s = AppSettings.Load();
        Assert.Equal(1.0, s.MicGain);
        Assert.Equal(192, s.Mp3BitrateKbps);
    }

    // ── Independent clamping ──────────────────────────────────────────────────

    [Fact]
    public void Load_BothGainsOutOfRange_ClampsIndependently()
    {
        var json = """{"MicGain":99.0,"LoopGain":-5.0,"Mp3BitrateKbps":192,"IncludeMic":true,"OutputDir":"/tmp"}""";
        File.WriteAllText(ConfigPath, json);
        var s = AppSettings.Load();
        Assert.Equal(3.0, s.MicGain);  // clamped to upper bound (3×: above 2× is noise floor, not signal)
        Assert.Equal(0.0,  s.LoopGain); // clamped to lower bound
    }
}
