---
"hearbud": patch
---

Fix immediate-on-launch UI lag (checkbox/dropdown stalls)

What: `MainWindow` meter pump switched from `System.Timers.Timer` +
  blocking `Dispatcher.Invoke` to a coalesced UI-thread `DispatcherTimer`
  (10 Hz) with brushes resolved once; startup WAV repair scan moved to a
  background `Task` with only the result dialog marshalled back;
  `SafeRefreshDevices` disposes prior `MMDevice` COM objects before
  clearing; MP3 progress coalesced to one `BeginInvoke` per percent point.

Why: lag appeared immediately at startup while monitoring was live.
  The 100 ms threadpool timer queued sync invokes behind device-enum /
  audio-start work and starved input; the repair scan ran disk IO on the
  UI thread; undisposed MMDevices leaked handles on every refresh; the
  encode loop fired a sync UI invoke per 64 KB chunk.

Verification: built with the repo-pinned SDK from the runner tool cache
  (`C:\actions-runner\_work\_tool\dotnet`, resolved 8.0.133):
  `dotnet build Hearbud.sln -c Release` — 0 warnings, 0 errors;
  `dotnet test Hearbud.sln -c Release --no-build` — 228/228 passed.
