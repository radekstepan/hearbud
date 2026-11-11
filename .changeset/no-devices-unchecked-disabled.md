---
"hearbud": patch
---

Uncheck and disable source when no device present

- What: `SafeRefreshDevices` in `Hearbud/MainWindow.xaml.cs` now handles empty device lists: when no input (or output) device is found, the matching checkbox (`IncludeMicCheck` / `RecordSysCheck`) is unchecked and its dropdown is disabled with an empty item list and cleared selection. When devices exist, prior selection/first-item fallback is kept and the dropdown enabled state follows its checkbox.
- Why: with no devices the UI previously left the source checked with a stale/blank selection; now the state is honest and unusable controls are disabled.
- Verification: `dotnet build Hearbud.sln -c Release` green (0 warnings, 0 errors). `dist` republish running separately.
