# Contributing to Hearbud

This document provides guidelines for contributing to Hearbud, including setup, testing, and code conventions.

## Table of Contents
- [Getting Started](#getting-started)
- [Development Workflow](#development-workflow)
- [Code Style and Conventions](#code-style-and-conventions)
- [Testing Guidelines](#testing-guidelines)
- [Debugging](#debugging)
- [Releasing](#releasing)

---

## Getting Started

### Prerequisites

- **Windows OS** (Hearbud is Windows-only due to WASAPI dependency)
- **.NET 8 SDK** - [Download from Microsoft](https://dotnet.microsoft.com/download/dotnet/8.0)
- **Visual Studio 2022** (recommended) or VS Code with C# extension
- **Git** for version control

### Repository Structure

```
hearbud/
├── build.sh                          # Build script
├── Hearbud.sln                       # Visual Studio solution
├── Directory.Build.props             # Shared MSBuild props (app version lives here)
├── global.json                       # Pinned .NET SDK
├── .changeset/                       # Pending release notes (one file per change)
├── Hearbud/                          # Main project directory
│   ├── App.xaml / .xaml.cs           # App entry point and global handlers
│   ├── AppSettings.cs                # Settings persistence
│   ├── Dbfs.cs                       # Audio level calculations
│   ├── Hearbud.csproj                # Project file
│   ├── MainWindow.xaml               # Main UI window layout
│   ├── MainWindow.xaml.cs            # Main window code-behind
│   ├── SettingsWindow.xaml           # Settings dialog layout
│   ├── SettingsWindow.xaml.cs        # Settings dialog code-behind
│   ├── RecorderEngine.cs             # Core audio engine
│   ├── UsbTopology.cs                # Shared-USB-hub probe
│   ├── UiDiagnostics.cs              # UI-health diagnostics helpers
│   ├── HistoryStore.cs               # Session history JSON (unwired from UI)
│   ├── SessionManifest.cs            # Crash bookkeeping
│   ├── WavRepair.cs                  # WAV header fix / recovery
│   ├── WriterHealth.cs               # Disk-full / high-water tracking
│   └── hearbud.ico                   # App icon
├── Hearbud.Tests/                    # xUnit test project (net8.0-windows)
├── docs/                             # Documentation
│   ├── ARCHITECTURE.md               # Architecture overview
│   ├── WORKING_WITH_AUDIO.md         # Audio/DSP specifics
│   ├── EDGE_CASES.md                 # Edge-case matrix (rule/test/probe)
│   ├── LLM_QUICK_REFERENCE.md        # Condensed LLM reference
│   ├── TROUBLESHOOTING.md            # Common issues and solutions
│   ├── WINDOWS-RUNNER.md             # Self-hosted runner notes
│   ├── README.md                     # Docs index
│   └── CONTRIBUTING.md               # This file
└── README.md                          # Project README
```

### Building the Project

#### Using Visual Studio

1. Open `Hearbud.sln` in Visual Studio 2022
2. Restore NuGet packages (automatic on first build)
3. Build → Build Solution (F6)
4. Run with F5

#### Using CLI (dotnet)

```powershell
# Restore dependencies
dotnet restore

# Build debug version
dotnet build

# Run in development mode
dotnet run --project Hearbud/Hearbud.csproj
```

### Development Setup

1. **Clone the repository:**
   ```bash
   git clone https://github.com/radekstepan/hearbud.git
   cd hearbud
   ```

2. **Open in your editor:**
   - VS 2022: Open `Hearbud.sln`
   - VS Code: `code .`

3. **Build and run:**
   ```bash
   dotnet run --project Hearbud/Hearbud.csproj
   ```

### Recommended VS Code Extensions

- `ms-dotnettools.csdevkit` - C# Dev Kit
- `ms-dotnettools.csharp` - Official C# extension
- `visualstudioexptteam.vscodeintellicode` - IntelliCode AI assistance
- `bierner.markdown-mermaid` - Mermaid diagrams in Markdown

---

## Development Workflow

### Branch Naming

- `feature/` - New features (e.g., `feature/equalizer`)
- `bugfix/` - Bug fixes (e.g., `bugfix/mute-glitch`)
- `refactor/` - Code refactoring (e.g., `refactor/async-rewrite`)
- `docs/` - Documentation updates (e.g., `docs/api-changes`)

### Pull Request Process

1. **Fork the repository** (if you're not a maintainer)
2. **Create a feature branch** from `master`
3. **Make your changes** with clear, atomic commits
4. **Update documentation** if needed
5. **Test thoroughly** (see [Testing Guidelines](#testing-guidelines))
6. **Submit a PR** with:
   - Clear title and description
   - Reference related issues (e.g., `Fixes #123`)
   - Screenshots for UI changes
   - Test notes or reproduction steps

### Commit Message Style

Follow conventional commits:

```
<type>(<scope>): <subject>

<body>

<footer>
```

**Types:**
- `feat` - New feature
- `fix` - Bug fix
- `refactor` - Refactoring (no behavior change)
- `perf` - Performance improvement
- `docs` - Documentation
- `style` - Code style (formatting, no logic change)
- `test` - Adding or updating tests
- `chore` - Build process, dependencies

**Examples:**
```
feat(audio): add support for 96 kHz output formats

Implement higher sample rate options for improved
audio quality in professional recording scenarios.

Closes #45
```

```
fix(engine): resolve mic underflow during prolonged silence

Mic ring buffer was not being cleared when loopback
resumed after >200ms silence, causing old audio to
play over new system audio.
```

### Changesets

Every non-`[skip ci]` push on a non-master branch includes one
`.changeset/<kebab>.md` file with `"hearbud": patch` front-matter:

```markdown
---
"hearbud": patch
---

<Title>

- What: changed files and behavior.
- Why: reason for the change.
- Verification: what was run (or "CI gate" if not runnable locally).
```

Changesets are consumed on merge to master (release flow owns
versioning there); master itself is exempt from the changeset gate.

---

## Code Style and Conventions

### C# Style Guide

Hearbud follows standard .NET conventions with some project-specific rules:

#### Naming Conventions

| Element | Convention | Example |
|---------|------------|---------|
| Classes | PascalCase | `RecorderEngine`, `MainWindow` |
| Methods | PascalCase | `StartRecording()`, `EnqueueWrite()` |
| Properties | PascalCase | `MicGain`, `LoopGain` |
| Fields (private) | _camelCase | `_micIn`, `_wavMix`, `_disposed` |
| Constants | PascalCase | `LoopSilentMsThreshold`, `FixedFramesPerChannel` |
| Fields (private static readonly) | _camelCase | `_rng` |
| Local variables | camelCase | `blockPeak`, `conv`, `srcCh` |
| Parameters | camelCase | `opts`, `sourceData`, `count` |
| Events | PascalCase | `LevelChanged`, `Status`, `EncodingProgress` |
| Enums | PascalCase | `LevelSource`, `EngineStatusKind` |
| Interfaces | IPascalCase | (No interfaces currently in Hearbud) |
| Private methods | PascalCase | `OnLoopbackData()`, `ConvertToTargetChunked()` |

### File Organization

Each file should contain a single public class with related helpers.

**Example:**
```csharp
// RecorderEngine.cs
using System;
// ... usings ...

namespace Hearbud
{
    // Helper enums/structs first
    public enum LevelSource { Mic, System }

    public sealed class LevelChangedEventArgs : EventArgs
    {
        // ...
    }

    // Main class
    public sealed class RecorderEngine : IDisposable
    {
        // Private nested types (if any)
        internal enum AudioFileTarget { System, Mic, Mix }

        // Public API first
        public void Monitor(...) { }

        // Public properties
        public double MicGain { get; set; } = 1.0;

        // Public events
        public event EventHandler<LevelChangedEventArgs>? LevelChanged;

        // Private fields
        private bool _monitoring;

        // Private methods (grouped logically)
        // Audio callbacks
        // Helper methods
        // DSP methods
        // I/O methods
    }
}
```

### Using Directives

- Keep `using` statements at the top of the file
- Sort: System → Third-party → Local namespaces
- Remove unused `using` statements

**Good:**
```csharp
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using CSCore;
using CSCore.CoreAudioAPI;
using CSCore.SoundIn;
using CSCore.Streams;

namespace Hearbud
{
    // ...
}
```

### Nullable Reference Types

Hearbud uses C# nullable reference types:

```csharp
// Non-nullable (default)
public event EventHandler<LevelChangedEventArgs>? LevelChanged;

// Explicitly nullable
private StreamWriter? _log;

// nullable enable in project file
<Nullable>enable</Nullable>
```

**Rules:**
- Use nullable annotations where null is a valid value
- Prefer `!.` operator over `?` when you're confident value is non-null (after null check)
- Add null guards at method entry points:

```csharp
public void Monitor(RecorderStartOptions opts)
{
    if (opts == null) throw new ArgumentNullException(nameof(opts));
    // ...
}
```

### Async/Await Patterns

**Do:** Use `async/await` for I/O-bound operations

```csharp
public async Task StopAsync()
{
    if (_writeTask != null)
    {
        await _writeTask;
        _writeTask.Dispose();
    }
}
```

**Don't:** Use `async void` except for event handlers

```csharp
// Event handlers only
private async void OnStop(object? sender, RoutedEventArgs e)
{
    await _engine.StopAsync();
}
```

### Exception Handling

**Global Level:**
- `App.xaml.cs` has handlers for AppDomain, Dispatcher, and TaskScheduler exceptions

**Local Level:**
- Wrap specific operations in try/catch
- Log errors, optionally notify user

```csharp
try
{
    _engine.Start(options);
}
catch (Exception ex)
{
    CrashLog.LogAndShow("OnStart", ex);
    StatusText.Text = "Failed to start recording";
}
```

**Resource Cleanup:**
```csharp
private static void TryStopDispose(ref WasapiCapture? cap)
{
    try { cap?.Stop(); } catch { }
    try { cap?.Dispose(); } catch { }
    cap = null;
}
```

### Resource Management

**IDisposable Pattern:**

```csharp
public sealed class RecorderEngine : IDisposable
{
    private volatile bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _loopCap?.Dispose(); } catch { }
        try { _micCap?.Dispose(); } catch { }
        // ...
    }
}
```

**Key Points:**
- Make `Dispose` idempotent (safe to call multiple times)
- Set disposed flag before cleanup
- Catch and suppress exceptions in dispose

### Comments and Documentation

- **Public API:** Use XML documentation comments
- **Complex Logic:** Add inline comments explaining "why", not "what"
- **TODOs:** Mark with `// TODO:` and optionally file an issue

**Example:**
```csharp
/// <summary>
/// Starts recording with the specified options.
/// </summary>
/// <param name="opts">Recording options including output path and device IDs.</param>
/// <exception cref="ObjectDisposedException">Thrown if engine has been disposed.</exception>
public void Start(RecorderStartOptions opts)
{
    if (_disposed) throw new ObjectDisposedException(nameof(RecorderEngine));
    // ...
}

// IMPORTANT: Loopback is the clock source. Mic is buffered and pulled to match.
// If mic is late, gaps are zero-filled to preserve timing.
```

### Performance Guidelines

#### Audio Callbacks (Critical Path)

These methods are called ~100 times per second. **Optimize heavily:**

```csharp
private void OnLoopbackData(object? sender, DataAvailableEventArgs e)
{
    // ✓ OK: Local calculations, array indexing
    // ✓ OK: Array.Copy, Math operations
    // ✗ NO: allocations, I/O, locks (unless quick), exceptions
    // ✗ NO: LINQ (allocates), reflection, serialization

    // Good (straight sum; the real mix then normalizes only on overs)
    for (int i = 0; i < count; i++)
    {
        _mixBufF[i] = (float)(_loopBufF[i] * LoopGain + _micBufF[i] * MicGain);
    }

    // Bad (allocates)
    var mixed = _loopBufF.Zip(_micBufF, (l, m) => l + m).ToArray();
}
```

#### Memory Allocation

- Use `ArrayPool<T>.Shared` for temporary buffers
- Reuse buffers instead of `new byte[]`
- Avoid allocations in hot paths

```csharp
// Good: Pooling
byte[] rented = ArrayPool<byte>.Shared.Rent(count);
Array.Copy(source, rented, count);
_queue.Add(new AudioWriteJob(rented, count));

// Later (consumer side)
ArrayPool<byte>.Shared.Return(job.Data);

// Bad: Per-call allocation
byte[] buffer = new byte[count];
```

---

## Testing Guidelines

### Testing Environment

Since Hearbud is a desktop app interacting with audio hardware, testing requires:

1. **Physical Audio Devices:**
   - At least one microphone
   - One speaker/audio output
   - (Optional) Virtual audio driver (Voicemeeter, VB-Audio Cable) for loopback-only testing

2. **Windows OS:**
   - Requires Windows for WASAPI loopback
   - Test on Windows 10/11 minimum

3. **Test Audio Sources:**
   - System audio (YouTube Spotify, etc.) for loopback
   - Speaking into mic for microphone capture
   - Mix of both

### Manual Testing Checklist

#### Basic Recording

- [ ] Select mic and speaker devices
- [ ] Set output folder and base name
- [ ] Press Record (Ctrl+R)
- [ ] Play system audio, speak into mic
- [ ] Observe meters: Both should show activity
- [ ] Stop recording (Ctrl+S)
- [ ] Verify 3 files created: `*-system.wav`, `*-mic.wav`, `*-mix.wav`
- [ ] Play each file to confirm content

#### Gain Controls

- [ ] Increase mic gain to 2.0x, record, verify louder mic in mix
- [ ] Decrease mic gain to 0.5x, record, verify quieter mic
- [ ] Test loop gain similarly
- [ ] Set both to max (3.0x), verify no clipping (soft clip active)

#### MP3 Encoding

- [ ] Set Settings → Output Format to "WAV + MP3", bitrate to "192 kbps"
- [ ] Record, verify `*.mp3` file created
- [ ] Compare mix.wav and mix.mp3: should sound similar
- [ ] Test "WAV master" format: Only WAVs, no MP3
- [ ] Verify progress bar shows during encoding

#### Edge Cases

- [ ] Record with **mic only**: Play no system audio, speak for 10s
  - Verify system.wav contains silence
  - Verify mix.wav contains only mic audio
- [ ] Record with **system only**: Mute mic, play audio for 10s
  - Verify mic.wav contains silence
  - Verify mix.wav contains only system audio
- [ ] **Silence to activity:** Start recording silent, then play audio
  - Verify mix stays synced (no old mic over new system)

#### Device Changes

- [ ] Change speaker selection mid-session (while monitoring)
- [ ] Verify meters update to new device
- [ ] Refresh devices button works
- [ ] Auto-select defaults work on first load

#### Crash Recovery

- [ ] Simulate crash: Kill app during recording
- [ ] Restart
- [ ] Verify partial files exist (useful for recovery)
- [ ] Verify no file corruption warnings on system

### Load Testing

- [ ] Record for 30+ minutes continuously
- [ ] Verify no memory leaks (monitor in Task Manager)
- [ ] Verify no underrun spikes in session log
- [ ] Verify ~150 MB file size for 30 min @ 48 kHz (approx)

### Disk Stress Tests

- [ ] Record to slow USB drive
- [ ] Record to network share
- [ ] Verify no audio glitches (async I/O should protect)

---

## Debugging

### Enabling Diagnostic Logging

Hearbud writes session logs alongside output files (e.g., `rec-20250623_143022.txt`):

```
[2026-11-08 14:30:22.123] INFO Info: Recording started
[2026-11-08 14:30:22.456] INFO Info: lagTime=0.052s micDepth=… over=0 loopOver=0 under=0 silenced=0 backlogMax=0.120s masterFrames=… resyncs=0 trim=… lagWatch=ok
[2026-11-08 14:30:25.789] WARN Warn: Master-clock resync: dropped 4800 mic samples (backlog 0.100s > 0.08s, resync #1)
```

(The doubled `INFO Info:` / `WARN Warn:` tag is real: the `Info()`/`Warn()`/`Error()` wrappers forward to `WriteLog`, whose `[CallerMemberName]` captures the wrapper name, not the original call site.)

**Log Categories:**
- `INFO` - Normal operations
- `WARN` - Non-fatal issues (underruns, near-clip)
- `ERROR` - Exception/failure

### Logging Discipline

Audio and engine hot paths log transitions only:

- No per-frame or per-callback logging. Steady-state work stays silent;
  log when state changes (start/stop, device lost/regained, resync,
  first underrun in a window — not every tick).
- Severity: routine transitions are INFO; degraded-but-recoverable is
  WARN; failures are ERROR. Cap log tails (summarize counts, do not
  dump unbounded buffers).
- Backlog/lag counters (ring backlog, overrun/underrun blocks, resyncs)
  are logged at most at 1 Hz as a one-line summary, not per tick.
- Include thread/tick context: callback name (`OnLoopbackData`,
  `OnMicData`), tick or timestamp, and the relevant counter values, so
  a log line is diagnosable without a debugger attached.

### Crash Diagnostics

Hearbud does not write crash or diagnostic log files. Fatal errors are shown
in the crash dialog with the full exception text, and diagnostic output is
routed to the debugger only (`System.Diagnostics.Debug.WriteLine`). Per-session
recording logs (`<base>.txt`) are separate and still written next to the audio
files.

### Visual Studio Debugging

#### Attaching to Process

1. Run Hearbud from Explorer or CLI
2. VS → Debug → Attach to Process
3. Select `Hearbud.exe`
4. Set breakpoints

#### Launching in Debugger

1. Open `Hearbud.sln`
2. Set `Hearbud` as startup project (already default)
3. F5 or Debug → Start Debugging

#### Conditional Breakpoints

Set conditions on breakpoints for specific scenarios:

```csharp
// Break only on underrun
if (_micUnderrunBlocks > 0) Debugger.Break();

// Break only on specific device
if (_loopDevName == "Realtek") Debugger.Break();
```

### Debugging Audio Callbacks

Audio callbacks are on background threads. Use thread-aware debugging:

1. **Break** inside `OnLoopbackData` or `OnMicData`
2. **Threads Window** (Debug → Windows → Threads)
3. **Freeze** the thread you want to inspect
4. Use **Immediate Window** (`Debug → Immediate Window`):

```
? _micCount
? _wavSys?.Length
? _loopBufF[0]
```

### Common Debug Scenarios

#### Mic/Loopback Not Synced

**Check:**
- `_micRing` buffer depth in session log
- `_micUnderrunBlocks` counter
- `_micBacklogSecMax` peak backlog

**Diagnosis:**
- Consistent underruns: Mic is too slow relative to loopback
- Consistent near-max backlog: Mic is too fast
- Frequent drops: Device timing variance

#### Audio Glitches/Dropouts

**Check:**
- Session log for queue-full warnings
- Task Manager: Disk I/O during high activity
- Antivirus software interference

**Diagnosis:**
- Glitches coincident with disk activity: I/O bottleneck
- Glitches at high CPU: Processing bottleneck
- Random glitches: Possibly driver issues

#### Memory Leaks

**Check:**
- Visual Studio Diagnostic Tools (Debug → Windows → Diagnostic Tools)
- Monitor private bytes, GC heaps over long recording

**Common Sources:**
- Forgetting to return ArrayPool buffers
- Event handler leaks (not unsubscribing)
- Disposal not called

---

## Releasing

### Bumping Version

The version lives in `Directory.Build.props` (single source of truth) and is bumped automatically by CI on every push to master — do not bump it by hand in feature branches:

```xml
<Version>0.4.0</Version>
```

Version format: `Major.Minor.Build` (no revision). The auto-bump is a patch bump by default; put `[minor]` or `[major]` in the pushed head commit **subject line** for a minor/major release instead (`[major]` wins if both are present; body text never counts, so bodies can mention the markers freely):

- `[minor]`: `0.3.3` → `0.4.0`
- `[major]`: `0.3.3` → `1.0.0`

To publish the tree version exactly as-is (e.g. to correct a bad bump), set `<Version>` by hand and put `[no-bump]` in the head commit message: CI skips the bump commit and builds, tests, publishes, and uploads that exact version.

Otherwise the bumped version is committed back to master (`chore: bump version to X.Y.Z [skip ci]`), then the Windows job builds, tests, publishes, and uploads the `Hearbud-X.Y.Z-windows` artifact with the exe.

**Update Criteria:**
- Major Breaking changes, major features
- Minor: New features, API additions
- Build: Bug fixes, patches

### Building Production Binaries

```bash
# Clean first
dotnet clean -c Release

# Build x64
dotnet publish Hearbud/Hearbud.csproj -c Release -r win-x64 \
  --self-contained -o ./dist/win-x64

# Build ARM64
dotnet publish Hearbud/Hearbud.csproj -c Release -r win-arm64 \
  --self-contained -o ./dist/win-arm64
```

**Output:**
- `dist/win-x64/Hearbud.exe` (self-contained single file)
- `dist/win-arm64/Hearbud.exe` (self-contained single file)

### Creating Release Archive

```powershell
# Create .zip for distribution (use the current version from Directory.Build.props)
Compress-Archive -Path dist/win-x64\* -DestinationPath Hearbud-v0.3.3-win-x64.zip
Compress-Archive -Path dist/win-arm64\* -DestinationPath Hearbud-v0.3.3-win-arm64.zip
```

### Release Notes

Release notes come from the `.changeset/` entries merged since the last release (see [Changesets](#changesets)), not from a hand-edited changelog:

```markdown
## [0.3.3] - 2026-11-08

### Added
- Settings dialog (output format, auto-stop limits, countdown, hotkeys)
- USB shared-hub warning banner

### Fixed
- Include-checkbox glitch (soft-mute instead of device restart)

### Changed
- Compact main window with footer transport; crash diagnostics without log file
```

### Git Tag (publishes the GitHub Release)

Pushing a `vX.Y.Z` tag runs the `release` job: it checks the tag matches `<Version>` in `Directory.Build.props`, then builds, tests, publishes win-x64, and creates the GitHub Release with a `Hearbud-X.Y.Z-win-x64.zip` asset (auto-generated notes):

```bash
git tag -a v0.4.0 -m "Release v0.4.0"
git push origin v0.4.0
```

---

## Resources

- **Architecture Overview:** See `docs/ARCHITECTURE.md`
- **Audio/DSP Reference:** See `docs/WORKING_WITH_AUDIO.md`
- **WASAPI Documentation:** [Microsoft Docs](https://docs.microsoft.com/en-us/windows/win32/coreaudio/wasapi)
- **CSCore Library:** [GitHub Repository](https://github.com/filoe/cscore)
- **.NET 8 Documentation:** [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/8.0)

---

## Questions?

Open an issue on GitHub or reach out to the maintainers. Happy coding!
