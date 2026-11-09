---
"hearbud": patch
---

What: Slimmed the main window (transport in the footer; format/stems/limits/countdown/hotkeys in a Settings dialog), then removed History and Test buttons, merged gains + levels into one Audio group, and themed the ScrollBar. This round: window auto-sizes to content (SizeToContent) so the footer can no longer be clipped and there is no dead padding; moved the include-checkbox work off the UI thread (serialized chain, pool thread); added timestamped AudioEngine/AudioRestart logging (entry, per-phase device timings, restart-to-first-audio gap) to logs.txt so the audio stall can be traced with evidence. The follow-up in `include-toggle-soft-mute.md` replaces the per-toggle device reopen with an in-place software mute.

Why: Footer buttons were clipped by fixed window height; the click froze the UI because device teardown/reopen ran synchronously on it and needlessly tore down the untouched source.

Verification: `dotnet publish Hearbud/Hearbud.csproj -c Release -r win-x64 --self-contained -o ./dist/win-x64` succeeds with 0 warnings/errors (temporary local SDK rollForward override, reverted afterwards); fresh binary verified to contain the new logging/restart code and none of the removed controls. Tests could not run locally (no .NET 8 runtime on this machine); CI is the gate. To diagnose the stall: toggle an include checkbox, then send the `AudioEngine`/`AudioRestart` lines from `<output folder>/logs.txt` — they show exactly where the milliseconds go.
