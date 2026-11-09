---
"hearbud": patch
---

Align audio/footer rows and make disabled buttons unmistakably inactive

What:
- `MainWindow.xaml`: fixed the Audio gain and level rows at 30 px each (and
  centered the progress bars), so Mic/System meters line up exactly with
  Mic Gain/Output Gain rows.
- `MainWindow.xaml`: fixed the footer at 30 px and vertically centered the
  status/elapsed stack, so "Monitoring...", "Recording..." and the transport
  buttons share the same center line.
- `App.xaml`: added dedicated disabled-button brushes (dark recessed surface,
  low-contrast border/text) and applied them to both the base Button and
  AccentButton disabled states instead of the previous secondary grey.

Why:
- Meter labels sat about 6-11 px above their matching gain rows, footer text
  alignment depended on incidental control heights, and disabled buttons
  still looked like ordinary grey buttons.

Verification:
- `dotnet build Hearbud.sln -c Release`: 0 warnings, 0 errors.
- `dotnet test Hearbud.sln -c Release --no-build`: 239/239 passed.
- Live UI Automation geometry: Mic Gain/Mic/bar centers all 804; Output
  Gain/System/bar centers all 844; Monitoring and transport-button centers
  all 954; Pause/Stop report `IsEnabled=False` while idle.
- Runtime style check: disabled Button and AccentButton resolve to
  `#FF28282A` background, `#FF626262` text, `#FF39393B` border; enabled
  AccentButton still resolves to the orange accent.
