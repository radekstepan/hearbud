---
"hearbud": patch
---

Default speaker Console first with Multimedia fallback

- What: `SafeRefreshDevices` in `Hearbud/MainWindow.xaml.cs` now resolves the default speaker via `Role.Console` first (matching OBS Desktop Audio `eRender/eConsole`) and falls back to `Role.Multimedia` when Console misses or is not in the device list. Mic stays `Capture/Communications`; existing try/catch, `_spkDict.ContainsKey` guard, and first-item fallback kept.
- Why: current code picks the wrong default when Console != Multimedia; OBS `win-wasapi.cpp:613-615` uses `eConsole` for Desktop Audio.
- Verification: `dotnet build Hearbud.sln -c Release` green (0 warnings, 0 errors); `Hearbud.exe` produced. (Built with SDK 9 by temporarily moving pinned `global.json` aside, then restored.)
