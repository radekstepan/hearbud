---
"hearbud": patch
---

Fix Roslyn analyzer warnings-as-errors in UI/settings/history files (CA1305/CA1869/CA1725/CA1822/CA1001).

What: pass `CultureInfo.InvariantCulture` to bare `ToString()` calls (HistoryStore, MiniBar, MainWindow `LoadSettingsToUi`); hoist per-call `new JsonSerializerOptions { WriteIndented = true }` into one `private static readonly` field per file (HistoryStore, AppSettings, SessionManifest — all three had identical settings, so one cached instance each); rename `IMMNotificationClient` params to match the interface (`defaultDeviceId` to `deviceId`, `newState` to `deviceState`); mark `CheckDiskSpaceAndWritable` static (verified: no instance state); implement idempotent `IDisposable.Dispose()` on `MainWindow` reusing the existing `OnClosed` cleanup path for `_engine`, `_notifEnumerator`, `_trayIcon`, `_uiTimer`.

Why: CI treats these analyzer warnings as errors; no behavior change beyond what each rule requires (InvariantCulture preserves current output for these display/persistence strings).

Verification: no local dotnet on this Linux box — CI `dotnet test` on the Windows runner is the gate; the diff was re-read after editing.
