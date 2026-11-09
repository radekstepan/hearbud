---
"hearbud": patch
---

Fix include-checkbox glitch: mute the source instead of stopping/restarting the capture

What: `OnIncludeSourceChanged` now only flips the combo state on the UI
  thread and queues `RecorderEngine.MonitorSourcesAsync`, which applies a
  software mute (`SetChannelMute`) for unchecked sources. That method no
  longer tears down an unwanted side; it opens a side only when it is
  wanted and missing (first check after launch or a changed device). A
  recording still starts from the checkbox-derived device set and clears
  the idle mutes in `StartAsync`. Added the missing null guard on the
  XAML name fields: `Checked`/`Unchecked` can fire during
  `InitializeComponent` before every `x:Name` is connected, which threw a
  swallowed `NullReferenceException` at startup.

Why: the instrumented log showed the click handler returning in 1-3 ms
  and the UI thread's worst lag at 10 ms, so the gap was not in Hearbud's
  message loop or restart bookkeeping. The remaining app-side action was
  the WASAPI `Stop()`/`Initialize()`/`Start()` on the checked side: on
  USB mics that can stall inside the driver (and the old code also froze
  the unchecked side's meter at its last peak). Keeping the stream open
  and gating samples removes every audio-device call from the checkbox
  path, so a toggle cannot gap the app or the device.

Verification: `dotnet publish Hearbud/Hearbud.csproj -c Release -r win-x64
  --self-contained -o ./dist/win-x64` succeeded with 0 warnings/errors
  using the runner-pinned SDK 8.0.133; `dotnet test Hearbud.sln -c Release`
  passed 228/228 locally with the same SDK.
