---
"hearbud": patch
---

Crash/diagnostic log follows the output folder

What: `CrashLog` defaults to `<Music>/Recordings/logs.txt` (same folder
as the per-session logs) instead of `%LocalAppData%`; `MainWindow` points
it at the configured output folder after settings load, folder browse,
and `Start` fallback; writes fall back to `%LocalAppData%/Hearbud` when
the output folder is unset or unwritable; `LogOnly` returns the path
actually written so the crash dialog names the right file. Docs updated;
`CrashLogPathTests` (reflection-driven, temp dir) covers override + reset.

Why: session logs already live in the output folder, so diagnostics and
crashes belong alongside them — one folder to check. LocalAppData is kept
only as a never-lose-a-crash fallback.

Verification: no local dotnet on this Linux box — CI `dotnet test` on
the Windows runner is the gate; the edited regions were re-read after
editing.
