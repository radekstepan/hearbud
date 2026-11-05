using System;
using System.IO;
using Xunit;

namespace Hearbud.Tests;

/// <summary>
/// Tests for the v2 crash-safe file helpers (<see cref="Hearbud.CrashSafeFiles"/>).
/// </summary>
public class CrashSafeFilesTests : IDisposable
{
    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(), $"hearbud-csf-{Guid.NewGuid()}");

    public CrashSafeFilesTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    // ── TmpPathFor ────────────────────────────────────────────────────────────

    [Fact]
    public void TmpPathFor_AppendsTmpSuffix()
        => Assert.Equal(@"C:\rec\take-system.wav.tmp",
            CrashSafeFiles.TmpPathFor(@"C:\rec\take-system.wav"));

    // ── EnsureUniquePath ──────────────────────────────────────────────────────

    [Fact]
    public void EnsureUniquePath_NonExistent_ReturnsOriginal()
    {
        var path = Path.Combine(_scratch, "take-system.wav");
        Assert.Equal(path, CrashSafeFiles.EnsureUniquePath(path));
    }

    [Fact]
    public void EnsureUniquePath_Existing_ReturnsNumberedVariant()
    {
        var path = Path.Combine(_scratch, "take-system.wav");
        File.WriteAllText(path, "x");
        Assert.Equal(Path.Combine(_scratch, "take-system (1).wav"),
            CrashSafeFiles.EnsureUniquePath(path));
    }

    [Fact]
    public void EnsureUniquePath_TwoExisting_ReturnsTwo()
    {
        File.WriteAllText(Path.Combine(_scratch, "take.wav"), "x");
        File.WriteAllText(Path.Combine(_scratch, "take (1).wav"), "x");
        Assert.Equal(Path.Combine(_scratch, "take (2).wav"),
            CrashSafeFiles.EnsureUniquePath(Path.Combine(_scratch, "take.wav")));
    }

    // ── AtomicPublish ─────────────────────────────────────────────────────────

    [Fact]
    public void AtomicPublish_MovesTmpToFinal()
    {
        var final = Path.Combine(_scratch, "take-system.wav");
        var tmp = CrashSafeFiles.TmpPathFor(final);
        File.WriteAllText(tmp, "audio-bytes");

        var published = CrashSafeFiles.AtomicPublish(tmp, final);

        Assert.Equal(final, published);
        Assert.False(File.Exists(tmp));
        Assert.Equal("audio-bytes", File.ReadAllText(final));
    }

    [Fact]
    public void AtomicPublish_Collision_RenumbersInsteadOfClobbering()
    {
        var final = Path.Combine(_scratch, "take-mix.wav");
        File.WriteAllText(final, "previous-take"); // stale file already there
        var tmp = CrashSafeFiles.TmpPathFor(final);
        File.WriteAllText(tmp, "new-take");

        var published = CrashSafeFiles.AtomicPublish(tmp, final);

        Assert.Equal(Path.Combine(_scratch, "take-mix (1).wav"), published);
        Assert.Equal("previous-take", File.ReadAllText(final));
        Assert.Equal("new-take", File.ReadAllText(published));
    }

    // ── EstimateBytesPerSecond ────────────────────────────────────────────────

    [Fact]
    public void EstimateBytesPerSecond_48kStereo_AllFloat32()
        // Phase 4: sys + mic + mix are all float32 = 12 bytes/frame.
        => Assert.Equal(48000L * 2 * 12,
            CrashSafeFiles.EstimateBytesPerSecond(48000, 2));

    [Fact]
    public void EstimateBytesPerSecond_Mono()
        => Assert.Equal(44100L * 1 * 12,
            CrashSafeFiles.EstimateBytesPerSecond(44100, 1));

    [Theory]
    [InlineData(0, 2)]
    [InlineData(48000, 0)]
    [InlineData(-1, -1)]
    public void EstimateBytesPerSecond_Degenerate_ReturnsZero(int rate, int ch)
        => Assert.Equal(0, CrashSafeFiles.EstimateBytesPerSecond(rate, ch));

    // ── EvaluateSpace (pure rule) ─────────────────────────────────────────────

    [Fact]
    public void EvaluateSpace_NearlyFull_RefusesToRecord()
    {
        var v = CrashSafeFiles.EvaluateSpace(5L * 1024 * 1024, 768_000);
        Assert.False(v.CanRecord);
        Assert.NotNull(v.Warning);
    }

    [Fact]
    public void EvaluateSpace_Plenty_CanRecordWithoutWarning()
    {
        var v = CrashSafeFiles.EvaluateSpace(10L * 1024 * 1024 * 1024, 768_000);
        Assert.True(v.CanRecord);
        Assert.Null(v.Warning);
    }

    [Fact]
    public void EvaluateSpace_Low_CanRecordWithWarning()
    {
        var v = CrashSafeFiles.EvaluateSpace(50L * 1024 * 1024, 768_000);
        Assert.True(v.CanRecord);
        Assert.NotNull(v.Warning);
    }

    // ── Preflight (thin IO wrapper) ───────────────────────────────────────────

    [Fact]
    public void Preflight_TempDir_ReportsAvailableSpace()
    {
        var (verdict, available) = CrashSafeFiles.Preflight(_scratch, 768_000);
        Assert.True(available > 0);
        // Temp volume is expected writable; fail-open only on query errors.
        Assert.True(verdict.CanRecord);
    }

    // ── Orphan sweep (Phase 1) ────────────────────────────────────────────────

    [Fact]
    public void FindOrphanedTmp_EmptyDir_ReturnsEmpty()
        => Assert.Empty(CrashSafeFiles.FindOrphanedTmp(_scratch));

    [Fact]
    public void FindOrphanedTmp_MissingDir_ReturnsEmpty()
        => Assert.Empty(CrashSafeFiles.FindOrphanedTmp(Path.Combine(_scratch, "nope")));

    [Fact]
    public void FindOrphanedTmp_FindsOnlyTmpFiles()
    {
        File.WriteAllText(Path.Combine(_scratch, "take-system.wav.tmp"), "x");
        File.WriteAllText(Path.Combine(_scratch, "take-system.wav"), "x");
        File.WriteAllText(Path.Combine(_scratch, "notes.txt"), "x");

        var found = CrashSafeFiles.FindOrphanedTmp(_scratch);

        Assert.Single(found);
        Assert.EndsWith(".tmp", found[0]);
    }

    [Fact]
    public void RecoverOrphanedTmp_PublishesToFinalNames()
    {
        File.WriteAllText(Path.Combine(_scratch, "take-mix.wav.tmp"), "audio");

        var published = CrashSafeFiles.RecoverOrphanedTmp(_scratch);

        Assert.Single(published);
        Assert.Equal(Path.Combine(_scratch, "take-mix.wav"), published[0]);
        Assert.False(File.Exists(Path.Combine(_scratch, "take-mix.wav.tmp")));
        Assert.Equal("audio", File.ReadAllText(published[0]));
        Assert.Empty(CrashSafeFiles.FindOrphanedTmp(_scratch));
    }

    [Fact]
    public void RecoverOrphanedTmp_Collision_Renumbers()
    {
        File.WriteAllText(Path.Combine(_scratch, "take.wav"), "old");
        File.WriteAllText(Path.Combine(_scratch, "take.wav.tmp"), "orphan");

        var published = CrashSafeFiles.RecoverOrphanedTmp(_scratch);

        Assert.Single(published);
        Assert.Equal(Path.Combine(_scratch, "take (1).wav"), published[0]);
        Assert.Equal("old", File.ReadAllText(Path.Combine(_scratch, "take.wav")));
    }
}
