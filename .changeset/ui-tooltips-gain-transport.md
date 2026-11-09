---
"hearbud": patch
---

Keep audio transport in the main window and fix unreadable/detail UI

What:
- `App.xaml`: themed implicit `ToolTip` (dark surface, light text, border,
  padding, max width) so tooltips no longer inherit the light foreground on
  the default light tooltip.
- `MainWindow.xaml`: widened the gain value column (90 -> 130) and level
  dBFS column (70 -> 80), and set `TextWrapping="NoWrap"` on both so
  "1.00x (+0.0 dB)" and "-60.0 dBFS" stay on one line.
- `MainWindow.xaml.cs` / `MainWindow.xaml`: removed the separate `MiniBar`
  window opened on Start; elapsed recording time now appears in the main
  footer while recording.
- `MainWindow.xaml.cs`: the 100 ms UI tick disables Pause and Stop whenever
  `IsRecording` is false (idle, stopping/encoding, auto-stop) or an auto
  stop/split owns the transition; tray Pause/Stop follow the same rule.
- Removed the now-unused `MiniBar.xaml` / `MiniBar.xaml.cs` and updated
  `docs/ARCHITECTURE.md`.

Why:
- Tooltips were light grey with light text, gain values wrapped mid-unit,
  recording opened a second always-on-top window, and Pause/Stop remained
  enabled in states where clicking them did nothing.

Verification:
- `dotnet build Hearbud.sln -c Release` (SDK 8.0.133): 0 warnings, 0 errors.
- `dotnet test Hearbud.sln -c Release --no-build`: 239/239 passed.
- Launched the Release build and queried the live window with UI Automation:
  gain labels render as single-line `1.00x (+0.0 dB)`, meters as
  `-60.0 dBFS`, and the recording UI is in the main window only.
