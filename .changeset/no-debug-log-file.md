---
"hearbud": patch
---

Stop writing the debug `logs.txt` file

What:
- `App.xaml.cs`: `CrashLog` no longer creates/appends `logs.txt` in the
  output folder or `%LocalAppData%`; diagnostic text goes to
  `System.Diagnostics.Debug.WriteLine` only, and crash dialogs show the
  exception without a file path. Removed the obsolete `SetLogDirectory`
  API and its call sites in `MainWindow`.
- `CrashLogPathTests` replaced with `CrashLogNoFileTests`, pinning the
  debugger-only sink and asserting no log file is produced.
- Updated `docs/CONTRIBUTING.md`, `docs/LLM_QUICK_REFERENCE.md`,
  `docs/ARCHITECTURE.md`, and `docs/TROUBLESHOOTING.md`.

Why:
- Startup diagnostics were creating and appending `logs.txt` on every run
  even when no recording was active. Debug logging should not leave files
  next to the user's recordings.

Verification:
- `dotnet build Hearbud.sln -c Release`: 0 warnings, 0 errors.
- `dotnet test Hearbud.sln -c Release --no-build`: 238/238 passed.
- Launched the Release build, closed it, and confirmed the existing
  `Music\Recordings\logs.txt` and `%LocalAppData%\Hearbud\logs.txt`
  timestamps did not change.
