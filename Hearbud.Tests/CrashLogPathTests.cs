using System;
using System.IO;
using System.Reflection;
using Xunit;

namespace Hearbud.Tests;

// CrashLog is internal: reflection-driven like RecorderEngineWatchdogTests.
// Verifies the log follows the configured output folder, with no-throw reset.
public class CrashLogPathTests : IDisposable
{
    private static readonly Type T =
        typeof(RecorderEngine).Assembly.GetType("Hearbud.CrashLog")!;
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "hearbud-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { T.GetMethod("SetLogDirectory")!.Invoke(null, new object?[] { null }); } catch { }
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void LogOnly_WritesToConfiguredDirectory()
    {
        Directory.CreateDirectory(_tempDir);
        T.GetMethod("SetLogDirectory")!.Invoke(null, new object?[] { _tempDir });
        string? written = (string?)T.GetMethod("LogOnly")!
            .Invoke(null, new object[] { "Test.Where", "hello-diagnostics" });
        Assert.Equal(Path.Combine(_tempDir, "logs.txt"), written);
        Assert.Contains("hello-diagnostics", File.ReadAllText(written!));
    }

    [Fact]
    public void SetLogDirectory_NullResets_WithoutThrowing()
    {
        T.GetMethod("SetLogDirectory")!.Invoke(null, new object?[] { _tempDir });
        var ex = Record.Exception(() =>
            T.GetMethod("SetLogDirectory")!.Invoke(null, new object?[] { null }));
        Assert.Null(ex);
    }
}
