---
"hearbud": patch
---

UI click-glitch diagnostics: env dump, lag watchdog, input trace

What: new `Hearbud/UiDiagnostics.cs` (`InputEventRing`, `LagTracker`,
`BuildEnvReport`); `MainWindow` logs a one-shot env report at startup
(OS, render tier/mode, remote session, animation flags, tablet devices,
DPI), runs a 1 Hz UI-thread lag watchdog (log-only WARN on spikes
>250 ms with thread/tick context + capped input tail), and records raw
mouse/stylus down/up plus dropdown open/close into a 50-entry ring
dumped as a close summary; `CrashLog.LogOnly` added for popup-free
logging; `UiDiagnosticsTests` covers ring/tracker/source-name logic.

Why: glitchy clicks reproduce across Hearbud, hearbud-2, and RecExperts
on the same Radeon-iGPU mini PC, pointing at environment/rendering
rather than app logic — need data (tier, remote/tablet, lag spikes,
down-vs-up counts) to separate UI-thread starvation from system-wide
input issues.

Verification: no local dotnet on this Linux box — CI `dotnet test` on
the Windows runner is the gate; the edited regions were re-read after
editing.
