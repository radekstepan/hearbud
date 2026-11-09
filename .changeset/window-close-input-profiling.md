---
"hearbud": patch
---

Window activation/placement plus non-client close and render-thread diagnostics

What: `MainWindow` now opens `CenterScreen` and calls `Activate()` after
  layout, so a window launched by a script/agent cannot sit behind the
  foreground window or off the expected spot. The first auto-monitor is
  deferred to `DispatcherPriority.ApplicationIdle` so the first frame is
  painted before WASAPI init runs. Added a Win32 hook for
  `WM_NCLBUTTONDOWN`/`WM_NCLBUTTONUP`/`WM_MOUSEACTIVATE`/`WM_ACTIVATE`/
  `WM_SYSCOMMAND SC_CLOSE`/`WM_CLOSE`, `OnClosing` entry/exit timings, a
  `ContentRendered` timestamp, and a `CompositionTarget.Rendering` frame
  counter with max frame gap (summarised at close).

Why: the last log (`13:08:15` start, `13:08:23` close) shows a healthy UI
  thread (`ticks=7 maxLag=18ms spikes=0`) and zero client-area clicks,
  while the close took ~8.5 s to register. The WPF input ring cannot see
  the title-bar close button, so there was no evidence whether those
  clicks ever reached the window. The new lines show the exact
  click -> WM_CLOSE -> OnClosing timeline and whether the render thread
  is presenting frames.

  Result on the next run (`13:30:47` start, `13:31:23` close): 1863
  frames, `maxLag=144ms`, and a single `WM_NCLBUTTONDOWN hit=0x14`
  (HTCLOSE) at `13:31:23.117` followed by `WM_CLOSE` at `.222` and
  `OnClosing done +0.8ms`. No earlier click, activation change, or
  non-client message reached the window, so the app was not swallowing
  input and the close handler was not delayed. The machine's mouse,
  keyboard receiver and Razer Seiren Mini all sit behind the same
  generic USB hub (`VID_1A40&PID_0101`), which is the layer that dropped
  the earlier clicks.

Verification: `dotnet publish Hearbud/Hearbud.csproj -c Release -r win-x64
  --self-contained -o ./dist/win-x64` succeeded with 0 warnings/errors
  (runner-pinned SDK 8.0.133). `dotnet test Hearbud.sln -c Release`
  passed 228/228; one earlier run hit a transient
  `logs.txt` file-lock in `CrashLogPathTests`, and the immediate re-run
  passed all 228.
