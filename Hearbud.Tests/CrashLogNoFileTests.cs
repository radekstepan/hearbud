using System;
using System.IO;
using Xunit;

namespace Hearbud.Tests;

// CrashLog is internal: reflection-driven like RecorderEngineWatchdogTests.
// The diagnostics sink must stay debugger-only; it must not recreate logs.txt.
public class CrashLogNoFileTests
{
    private static readonly Type T =
        typeof(RecorderEngine).Assembly.GetType("Hearbud.CrashLog")!;

    [Fact]
    public void LogOnly_WritesNothingToDisk()
    {
        string tempDir = Path.Combine(
            Path.GetTempPath(), "hearbud-log-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // A file-path setter or a string return would mean the old
            // logs.txt sink is back.
            Assert.Null(T.GetMethod("SetLogDirectory"));
            Assert.Equal(typeof(void), T.GetMethod("LogOnly")!.ReturnType);

            var ex = Record.Exception(() =>
                T.GetMethod("LogOnly")!.Invoke(
                    null, new object[] { "Test.Where", "hello-diagnostics" }));
            Assert.Null(ex);
            Assert.Empty(Directory.GetFiles(tempDir));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
