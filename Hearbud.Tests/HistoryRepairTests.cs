using System;
using System.IO;
using Xunit;

namespace Hearbud.Tests;

/// <summary>
/// GAP-09 #8: HistoryStore cap/persist/remove + WavRepair detect/repair.
/// Uses temp paths only — never touches the real user profile.
/// </summary>
public class HistoryRepairTests : IDisposable
{
    private readonly string _tmp;

    public HistoryRepairTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "hearbud-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch { }
    }

    private string StorePath => Path.Combine(_tmp, "history.json");

    private static HistoryEntry MakeEntry(string baseName) => new()
    {
        Timestamp = DateTime.Now,
        BaseName = baseName,
        MixPath = baseName + "-mix.wav",
        DurationSeconds = 61,
        SizeBytes = 1024,
    };

    // ── HistoryStore ────────────────────────────────────────────────────────

    [Fact]
    public void Store_Add_CapsAt50_NewestFirst()
    {
        var store = new HistoryStore(StorePath);
        for (int i = 0; i < 55; i++)
            store.Add(MakeEntry($"rec-{i:000}"));

        Assert.Equal(HistoryStore.MaxEntries, store.Entries.Count);
        Assert.Equal("rec-054", store.Entries[0].BaseName); // newest first
        Assert.Equal("rec-005", store.Entries[^1].BaseName); // oldest 5 evicted
    }

    [Fact]
    public void Store_RoundTrips_ThroughFile()
    {
        var store = new HistoryStore(StorePath);
        store.Add(MakeEntry("rec-a"));

        var reloaded = new HistoryStore(StorePath);
        Assert.Single(reloaded.Entries);
        Assert.Equal("rec-a", reloaded.Entries[0].BaseName);
        Assert.Equal(61, reloaded.Entries[0].DurationSeconds);
    }

    [Fact]
    public void Store_Remove_Persists()
    {
        var store = new HistoryStore(StorePath);
        var entry = MakeEntry("rec-del");
        store.Add(entry);

        Assert.True(store.Remove(entry));
        Assert.Empty(store.Entries);
        Assert.Empty(new HistoryStore(StorePath).Entries);
    }

    [Fact]
    public void Store_BadJson_StartsEmpty()
    {
        File.WriteAllText(StorePath, "not json{{{");
        Assert.Empty(new HistoryStore(StorePath).Entries);
    }

    // ── WavRepair ───────────────────────────────────────────────────────────

    /// <summary>Writes a minimal 16-bit stereo PCM WAV; optionally zeroes the RIFF/data sizes (crash simulation).</summary>
    private static void WriteTestWav(string path, bool zeroSizes, int dataBytes = 4410 * 2 * 2)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var w = new BinaryWriter(fs);
        w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
        w.Write(zeroSizes ? 0 : 36 + dataBytes);
        w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });
        w.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
        w.Write(16);
        w.Write((short)1);
        w.Write((short)2);
        w.Write(44100);
        w.Write(44100 * 4);
        w.Write((short)4);
        w.Write((short)16);
        w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
        w.Write(zeroSizes ? 0 : dataBytes);
        w.Write(new byte[dataBytes]);
    }

    [Fact]
    public void Repair_ValidFile_StaysValid()
    {
        string p = Path.Combine(_tmp, "good.wav");
        WriteTestWav(p, zeroSizes: false);

        Assert.True(WavRepair.IsValidWav(p));
        Assert.Empty(WavRepair.ScanDirectory(_tmp));
    }

    [Fact]
    public void Repair_ZeroedSizes_DetectedAndRepaired()
    {
        string p = Path.Combine(_tmp, "crashed.wav");
        WriteTestWav(p, zeroSizes: true);

        Assert.False(WavRepair.IsValidWav(p));
        Assert.Contains(p, WavRepair.ScanDirectory(_tmp));
        Assert.True(WavRepair.TryRepair(p));
        Assert.True(WavRepair.IsValidWav(p));
        Assert.Empty(WavRepair.ScanDirectory(_tmp));
    }

    [Fact]
    public void Repair_TinyFile_Unrepairable()
    {
        string p = Path.Combine(_tmp, "tiny.wav");
        File.WriteAllBytes(p, new byte[20]);

        Assert.False(WavRepair.IsValidWav(p));
        Assert.False(WavRepair.TryRepair(p));
        Assert.Contains(p, WavRepair.ScanDirectory(_tmp));
    }

    [Fact]
    public void Repair_NonWavBytes_Unrepairable()
    {
        string p = Path.Combine(_tmp, "junk.wav");
        var rnd = new Random(42);
        byte[] buf = new byte[1000];
        rnd.NextBytes(buf);
        File.WriteAllBytes(p, buf);

        Assert.False(WavRepair.TryRepair(p));
    }
}
