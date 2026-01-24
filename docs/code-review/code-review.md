# Deep Code Review: Hearbud Production Readiness

## Critical Issues

### 1. RecorderEngine.cs - Thread Safety on Gain Properties
**Lines: 134-135**
```csharp
public double MicGain { get; set; } = 1.0;   
public double LoopGain { get; set; } = 1.0;  
```
**Issue:** These are read from audio callback threads and written from UI thread without synchronization. `double` is not atomic on all platforms.

**Fix:**
```csharp
private volatile double _micGain = 1.0;
private volatile double _loopGain = 1.0;
public double MicGain 
{ 
    get => Volatile.Read(ref _micGain); 
    set => Volatile.Write(ref _micGain, value); 
}
public double LoopGain 
{ 
    get => Volatile.Read(ref _loopGain); 
    set => Volatile.Write(ref _loopGain, value); 
}
```
**Why:** Prevents torn reads on 32-bit systems and ensures visibility across threads.

---

### 2. RecorderEngine.cs - Missing Timeout on StopAsync
**Lines: 298-307**
```csharp
if (_writeQueue != null && _writeTask != null)
{
    Info("Finishing background writes...");
    _writeQueue.CompleteAdding();
    await _writeTask; // Can hang forever!
```
**Issue:** If disk I/O hangs or writer thread deadlocks, `StopAsync` will never complete.

**Fix:**
```csharp
if (_writeQueue != null && _writeTask != null)
{
    Info("Finishing background writes...");
    _writeQueue.CompleteAdding();
    
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    try
    {
        await _writeTask.WaitAsync(cts.Token);
    }
    catch (OperationCanceledException)
    {
        Warn("Write queue drain timed out after 30s");
    }
}
```
**Why:** Prevents application from hanging indefinitely on stop.

---

### 3. RecorderEngine.cs - Silent Data Loss on Queue Full
**Lines: 474-488**
```csharp
if (!_writeQueue.TryAdd(new AudioWriteJob(target, rented, count)))
{
    ArrayPool<byte>.Shared.Return(rented);
    long dropped = Interlocked.Increment(ref _droppedBlocks);
    if (dropped % 100 == 0) // First 99 drops are SILENT!
    {
        Warn($"Write queue full, dropped {dropped} blocks");
    }
}
```
**Issue:** First 99 dropped blocks are not logged. User has no idea data is being lost.

**Fix:**
```csharp
if (!_writeQueue.TryAdd(new AudioWriteJob(target, rented, count)))
{
    ArrayPool<byte>.Shared.Return(rented);
    long dropped = Interlocked.Increment(ref _droppedBlocks);
    if (dropped == 1 || dropped % 100 == 0)
    {
        Warn($"Write queue full, dropped {dropped} block(s) - disk I/O bottleneck!");
    }
}
```
**Why:** First drop is critical for user awareness of disk issues.

---

### 4. RecorderEngine.cs - Writer Exception Not Surfaced During Recording [FIXED]
**Lines: 449-468**
```csharp
private void DiskWriteLoop()
{
    // ...
    catch (Exception ex)
    {
        _writerException = ex;
        Error("DiskWriteLoop fatal", ex);
        // Audio callbacks keep queueing data that will never be written!
    }
}
```
**Issue:** If writer thread crashes, recording continues silently losing all data.

**Fix:**
```csharp
private void DiskWriteLoop()
{
    try
    {
        foreach (var job in _writeQueue.GetConsumingEnumerable())
        {
            // ... existing code ...
        }
    }
    catch (Exception ex)
    {
        _writerException = ex;
        _recording = false; // Stop accepting new data
        Error("DiskWriteLoop fatal", ex);
        RaiseStatus(EngineStatusKind.Error, $"Disk write failed: {ex.Message}");
    }
}
```
**Why:** Immediately notifies user rather than silently losing all recorded audio.

---

### 5. RecorderEngine.cs - Double CompleteAdding Exception [FIXED]
**Lines: 244-250**
```csharp
public void Dispose()
{
    // ...
    try { _writeQueue?.CompleteAdding(); _writeTask?.Wait(1000); } catch { }
```
**Issue:** If `StopAsync` was called before `Dispose`, `CompleteAdding()` throws `InvalidOperationException`.

**Fix:**
```csharp
try 
{ 
    if (_writeQueue != null && !_writeQueue.IsAddingCompleted)
    {
        _writeQueue.CompleteAdding(); 
    }
    _writeTask?.Wait(1000); 
} 
catch { }
```
**Why:** Prevents exception during cleanup.

---

### 6. RecorderEngine.cs - Potential Memory Leak in EnqueueWrite
**Lines: 474-488**
```csharp
private void EnqueueWrite(AudioFileTarget target, byte[] sourceData, int count)
{
    if (!_recording || _writeQueue == null || _writeQueue.IsAddingCompleted) return;

    byte[] rented = ArrayPool<byte>.Shared.Rent(count);
    Array.Copy(sourceData, 0, rented, 0, count);
    
    if (!_writeQueue.TryAdd(new AudioWriteJob(target, rented, count)))
    {
        ArrayPool<byte>.Shared.Return(rented);
```
**Issue:** If exception occurs between `Rent` and `TryAdd`, buffer leaks.

**Fix:**
```csharp
private void EnqueueWrite(AudioFileTarget target, byte[] sourceData, int count)
{
    if (!_recording || _writeQueue == null || _writeQueue.IsAddingCompleted) return;

    byte[] rented = ArrayPool<byte>.Shared.Rent(count);
    bool added = false;
    try
    {
        Array.Copy(sourceData, 0, rented, 0, count);
        added = _writeQueue.TryAdd(new AudioWriteJob(target, rented, count));
    }
    finally
    {
        if (!added) ArrayPool<byte>.Shared.Return(rented);
    }
    
    if (!added)
    {
        long dropped = Interlocked.Increment(ref _droppedBlocks);
        if (dropped == 1 || dropped % 100 == 0)
            Warn($"Write queue full, dropped {dropped} block(s)");
    }
}
```
**Why:** Ensures buffer is always returned on any failure path.

---

### 7. MainWindow.xaml.cs - No Confirmation on Close During Recording
**Lines: 423-433**
```csharp
protected override void OnClosed(EventArgs e)
{
    base.OnClosed(e);
    try { _uiTimer.Stop(); } catch { }
    // ... directly disposes engine without checking recording state
```
**Issue:** User can accidentally close window during recording, losing confirmation.

**Fix:**
```csharp
protected override async void OnClosing(CancelEventArgs e)
{
    base.OnClosing(e);
    
    if (_engine.IsRecording) // Need to add this property to RecorderEngine
    {
        var result = WpfMessageBox.Show(
            "Recording is in progress. Stop recording and exit?",
            "Confirm Exit",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        
        if (result == MessageBoxResult.No)
        {
            e.Cancel = true;
            return;
        }
        
        await _engine.StopAsync();
    }
}
```

**Add to RecorderEngine.cs:**
```csharp
public bool IsRecording => _recording;
```
**Why:** Prevents accidental data loss.

---

### 8. AppSettings.cs - No Validation of Loaded Values
**Lines: 29-51**
```csharp
var s = JsonSerializer.Deserialize<AppSettings>(json);
if (s != null)
{
    // No validation!
    return s;
}
```
**Issue:** Corrupted config could have invalid values (negative gain, huge bitrate, etc.)

**Fix:**
```csharp
public static AppSettings Load()
{
    try
    {
        if (File.Exists(ConfigPath))
        {
            var json = File.ReadAllText(ConfigPath);
            var s = JsonSerializer.Deserialize<AppSettings>(json);
            if (s != null)
            {
                s.Validate();
                return s;
            }
        }
    }
    catch { }
    return new AppSettings();
}

private void Validate()
{
    MicGain = Math.Clamp(MicGain, 0.0, 10.0);
    LoopGain = Math.Clamp(LoopGain, 0.0, 10.0);
    Mp3BitrateKbps = Mp3BitrateKbps == 0 ? 0 : Math.Clamp(Mp3BitrateKbps, 64, 320);
    
    if (string.IsNullOrWhiteSpace(OutputDir))
        OutputDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Recordings");
}
```
**Why:** Prevents crashes from invalid configuration values.

---

## High Priority Issues

### 9. RecorderEngine.cs - FloatToPcm32 Overflow Risk
**Lines: 753-765**
```csharp
private static void FloatToPcm32(float[] src, byte[] dst, int count)
{
    // ...
    int s = (int)Math.Round(v * int.MaxValue); // Potential overflow!
```
**Issue:** When `v = 1.0`, `v * int.MaxValue` can exceed `int.MaxValue` due to rounding.

**Fix:**
```csharp
private static void FloatToPcm32(float[] src, byte[] dst, int count)
{
    int j = 0;
    for (int i = 0; i < count; i++)
    {
        float v = src[i];
        if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
        
        // Use long for intermediate calculation to avoid overflow
        long scaled = (long)Math.Round(v * 2147483647.0);
        scaled = Math.Clamp(scaled, int.MinValue, int.MaxValue);
        int s = (int)scaled;
        
        dst[j++] = (byte)(s & 0xFF);
        dst[j++] = (byte)((s >> 8) & 0xFF);
        dst[j++] = (byte)((s >> 16) & 0xFF);
        dst[j++] = (byte)((s >> 24) & 0xFF);
    }
}
```
**Why:** Prevents audio artifacts from overflow on loud signals.

---

### 10. MainWindow.xaml.cs - Thread.Sleep in OpenDevices
**Lines: 525-528** (in RecorderEngine.cs, but called from UI)
```csharp
if (attempt < maxRetries - 1)
{
    Info($"Device invalidated, retrying in {retryDelayMs}ms...");
    System.Threading.Thread.Sleep(retryDelayMs); // Blocks UI!
```
**Issue:** `Thread.Sleep` in code path called from UI thread freezes the application.

**Fix:** Make device opening async:
```csharp
public async Task MonitorAsync(RecorderStartOptions opts)
{
    // ...
    await OpenDevicesAsync(opts);
    // ...
}

private async Task OpenDevicesAsync(RecorderStartOptions opts)
{
    // ...
    if (attempt < maxRetries - 1)
    {
        Info($"Device invalidated, retrying in {retryDelayMs}ms...");
        await Task.Delay(retryDelayMs);
    }
}
```
**Why:** Keeps UI responsive during device initialization.

---

### 11. App.xaml.cs - Keeping App Alive After Fatal Exception [FIXED]
**Lines: 17-21**
```csharp
DispatcherUnhandledException += (_, args) =>
{
    CrashLog.LogAndShow("Application.DispatcherUnhandledException", args.Exception);
    args.Handled = true; // Keeps app alive in potentially corrupted state!
};
```
**Issue:** After an unhandled exception, app state may be corrupted. Continuing could cause data loss.

**Fix:**
```csharp
DispatcherUnhandledException += (_, args) =>
{
    CrashLog.LogAndShow("Application.DispatcherUnhandledException", args.Exception);
    
    // Only keep alive for non-critical exceptions
    if (args.Exception is NotImplementedException || 
        args.Exception is InvalidOperationException)
    {
        args.Handled = true;
    }
    else
    {
        // For serious exceptions (NullRef, AccessViolation, etc.), let app terminate
        args.Handled = false;
    }
};
```
**Why:** Prevents data corruption from continuing in an invalid state.

---

### 12. RecorderEngine.cs - Missing Cancellation Token Support
**Throughout file**

**Issue:** No way to cancel long-running operations like MP3 encoding.

**Fix:**
```csharp
public async Task StopAsync(CancellationToken cancellationToken = default)
{
    // ...
    await Task.Run(() =>
    {
        // In encoding loop:
        while ((read = source.Read(buf, 0, buf.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            encoder.Write(buf, 0, read);
            // ...
        }
    }, cancellationToken);
}
```
**Why:** Allows user to cancel if encoding takes too long.

---

## Medium Priority Issues

### 13. RecorderEngine.cs - Hardcoded Queue Capacity
**Line: 193**
```csharp
_writeQueue = new BlockingCollection<AudioWriteJob>(2000);
```
**Issue:** 2000 may not be enough for slow network drives or during disk spikes.

**Fix:**
```csharp
private const int DefaultQueueCapacity = 2000;
private const int MaxQueueCapacity = 10000;

// Consider making this configurable
_writeQueue = new BlockingCollection<AudioWriteJob>(
    Math.Clamp(CalculateOptimalQueueSize(), DefaultQueueCapacity, MaxQueueCapacity));

private static int CalculateOptimalQueueSize()
{
    // Based on sample rate and block size, calculate ~10 seconds of buffer
    return Math.Max(2000, (_outRate * _outChannels * 10) / (BlockFrames * _outChannels));
}
```
**Why:** Adapts to system capabilities and recording format.

---

### 14. MainWindow.xaml.cs - Filename Length Not Validated
**Lines: 201-206**
```csharp
var baseName = SanitizeFilename(BaseNameText.Text);
// No length check!
```
**Issue:** Windows path limit is 260 characters; long names cause exceptions.

**Fix:**
```csharp
private void OnStart(object? sender, RoutedEventArgs? e)
{
    // ... existing validation ...
    
    var baseName = SanitizeFilename(BaseNameText.Text);
    if (string.IsNullOrWhiteSpace(baseName))
    {
        baseName = $"rec-{DateTime.Now:yyyyMMdd_HHmmss}";
        BaseNameText.Text = baseName;
    }
    
    // Validate total path length
    var testPath = Path.Combine(outDir, $"{baseName}-system.wav");
    if (testPath.Length > 240) // Leave room for suffixes
    {
        WpfMessageBox.Show(
            "Output path is too long. Please use a shorter folder path or file name.",
            "Path Too Long",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return;
    }
```
**Why:** Prevents cryptic IO exceptions from path length issues.

---

### 15. MainWindow.xaml - Missing Accessibility Attributes
**Throughout file**

**Issue:** No accessibility support for screen readers.

**Fix:**
```xaml
<ComboBox x:Name="MicCombo" 
          Margin="0,4,0,0"
          AutomationProperties.Name="Microphone Selection"
          AutomationProperties.HelpText="Select the microphone to record from"/>

<ProgressBar x:Name="MicBar" 
             AutomationProperties.Name="Microphone Level"
             AutomationProperties.LiveSetting="Polite"/>

<Button x:Name="StartBtn" 
        Content="Start Recording (Ctrl+R)" 
        AutomationProperties.Name="Start Recording"
        AutomationProperties.AcceleratorKey="Ctrl+R"/>
```
**Why:** Required for accessibility compliance and usability.

---

### 16. RecorderEngine.cs - Log File Can Grow Unbounded
**Lines: 797-804**
```csharp
private void WriteLog(string level, string msg, [CallerMemberName] string? where = null)
{
    try
    {
        lock (_logLock)
        {
            _log?.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {level} {where}: {msg}");
        }
```
**Issue:** For long recordings with verbose logging, log file can grow very large.

**Fix:**
```csharp
private const long MaxLogSizeBytes = 10 * 1024 * 1024; // 10 MB
private long _logBytesWritten = 0;

private void WriteLog(string level, string msg, [CallerMemberName] string? where = null)
{
    try
    {
        lock (_logLock)
        {
            if (_log == null) return;
            
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {level} {where}: {msg}";
            _logBytesWritten += line.Length * 2; // Rough estimate
            
            if (_logBytesWritten > MaxLogSizeBytes)
            {
                _log.WriteLine("[LOG TRUNCATED - Maximum size reached]");
                TryDispose(ref _log);
                return;
            }
            
            _log.WriteLine(line);
        }
```
**Why:** Prevents disk exhaustion on very long recordings.

---

### 17. Dbfs.cs - Inconsistent String Formatting
**Lines: 8-12**
```csharp
public static string FormatGain(double value)
{
    if (value <= 0.0) return $" 0.00× ( -inf dB)"; // Leading space
```
**Issue:** Leading space causes inconsistent display alignment.

**Fix:**
```csharp
public static string FormatGain(double value)
{
    if (value <= 0.0) return "0.00× (-∞ dB)";
    var db = 20.0 * Math.Log10(value);
    return $"{value:0.00}× ({db:+0.0;-0.0} dB)";
}
```
**Why:** Cleaner display and proper infinity symbol.

---

### 18. RecorderEngine.cs - Random Seeding Has Limited Entropy
**Lines: 746-747**
```csharp
private static readonly ThreadLocal<Random> _rng =
    new ThreadLocal<Random>(() => new Random(unchecked(Environment.TickCount * 31 + Thread.CurrentThread.ManagedThreadId)));
```
**Issue:** `Environment.TickCount` has limited entropy; dither could have patterns.

**Fix:**
```csharp
private static readonly ThreadLocal<Random> _rng =
    new ThreadLocal<Random>(() => new Random(
        HashCode.Combine(
            Environment.TickCount64,
            Thread.CurrentThread.ManagedThreadId,
            Guid.NewGuid().GetHashCode())));
```
**Why:** Better randomness for dither quality.

---

## Low Priority / Improvements

### 19. Missing Unit Tests
**Issue:** No test project exists.

**Recommendation:** Add `Hearbud.Tests` project with:
- `RecorderEngineTests.cs` - Test buffer management, gain application
- `ConversionTests.cs` - Test resampling, PCM conversion
- `AppSettingsTests.cs` - Test load/save/migration

---

### 20. RecorderEngine.cs - Use Modern .NET APIs
**Lines: 781-789**
```csharp
private static int NextPow2(int n)
{
    if (n <= 0) return 256;
    n--; n |= n >> 1; n |= n >> 2; n |= n >> 4; n |= n >> 8; n |= n >> 16; n++;
```
**Issue:** .NET 6+ has built-in `BitOperations.RoundUpToPowerOf2`.

**Fix:**
```csharp
using System.Numerics;

private static int NextPow2(int n)
{
    if (n <= 256) return 256;
    return (int)BitOperations.RoundUpToPowerOf2((uint)n);
}
```
**Why:** More readable and potentially faster.

---

### 21. MainWindow.xaml.cs - Duplicate Code in TryStartAutoMonitor
**Lines: 147-170**

**Issue:** Retry logic duplicates the entire monitor setup code.

**Fix:** Extract to helper method:
```csharp
private void TryStartAutoMonitor()
{
    ExecuteWithDeviceRetry(() =>
    {
        var micName = MicCombo.SelectedItem as string;
        var spkName = SpeakerCombo.SelectedItem as string;
        if (spkName == null || micName == null) return;

        _engine.MicGain = MicGain.Value;
        _engine.LoopGain = LoopGain.Value;

        _engine.Monitor(new RecorderStartOptions
        {
            LoopbackDeviceId = _spkDict[spkName]!.DeviceID,
            MicDeviceId = _micDict[micName].DeviceID
        });
        StatusText.Text = "Monitoring...";
    });
}

private void ExecuteWithDeviceRetry(Action action)
{
    try
    {
        action();
    }
    catch (CoreAudioAPIException ex) when (ex.HResult == unchecked((int)0x88890004))
    {
        StatusText.Text = "Refreshing devices...";
        SafeRefreshDevices();
        try { action(); }
        catch (Exception retryEx) { CrashLog.LogAndShow("Device retry", retryEx); }
    }
    catch (Exception ex) { CrashLog.LogAndShow("ExecuteWithDeviceRetry", ex); }
}
```
**Why:** DRY principle, easier maintenance.

---

### 22. App.xaml - No High Contrast Theme Support
**Issue:** Hard-coded colors don't adapt to Windows high contrast settings.

**Fix:** Add system color bindings:
```xaml
<Style TargetType="TextBlock">
    <Setter Property="Foreground" Value="{DynamicResource {x:Static SystemColors.ControlTextBrushKey}}"/>
</Style>
```
Or add high contrast resource dictionary.

---

### 23. Build Configuration - Missing Source Link
**Hearbud.csproj**

**Issue:** No source link for debugging distributed binaries.

**Fix:**
```xml
<PropertyGroup>
    <PublishRepositoryUrl>true</PublishRepositoryUrl>
    <EmbedUntrackedSources>true</EmbedUntrackedSources>
    <DebugType>embedded</DebugType>
</PropertyGroup>

<ItemGroup>
    <PackageReference Include="Microsoft.SourceLink.GitHub" Version="8.0.0" PrivateAssets="All"/>
</ItemGroup>
```
**Why:** Enables debugging of release builds.

---

## Summary Table

| Severity | Count | Category |
|----------|-------|----------|
| Critical | 8 | Thread safety, data loss, hangs |
| High | 4 | Overflow, UI freeze, validation |
| Medium | 6 | Logging, accessibility, limits |
| Low | 6 | Code quality, modernization |

**Recommended Priority Order:**
1. Fix thread safety issues (#1, #3, #4)
2. Add timeout to StopAsync (#2)
3. Add recording confirmation on close (#7)
4. Fix memory/overflow issues (#5, #6, #9)
5. Add settings validation (#8)
6. Add cancellation support (#12)
7. Accessibility improvements (#15)
8. Add unit tests (#19)