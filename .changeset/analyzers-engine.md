---
"hearbud": patch
---

What: Fix Roslyn analyzer errors-as-warnings in Hearbud/RecorderEngine.cs
  (CA1840 Thread.CurrentThread.ManagedThreadId to
  Environment.CurrentManagedThreadId; CA1806 discards for timeBeginPeriod,
  timeEndPeriod, plus sibling AvRevertMmThreadCharacteristics call sites;
  CA1513 ObjectDisposedException.ThrowIf in MonitorAsync/StartAsync;
  CA1822 FindZeroCrossDrop marked static).

Why: CI treats these analyzers as errors; no behavior change intended.

Verification: No local dotnet on this Linux box — CI `dotnet test` on
  Windows runner is the gate. The diff was re-read after editing to confirm
  no logic change (guard semantics preserved, discards only, static verified
  against method body using params plus const only).

Addendum (main agent): `FindZeroCrossDrop` going static broke its reflection
in `RecorderEngineDspTests.Trim_ZeroCrossDrop_IsFrameAligned_WithinWindow`
(`GetInstance` returns null for statics), so that test now uses `GetStatic`
+ `Invoke(null, …)`. Same assertion, no behavior change.
