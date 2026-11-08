---
"hearbud": patch
---

Add GC.SuppressFinalize to MainWindow.Dispose (CA1816)

What:
- `Hearbud/MainWindow.xaml.cs`: one line — `GC.SuppressFinalize(this)` at
  the end of `Dispose()`.

Why:
- Follow-on to the CA1001 `IDisposable` fix: CA1816 requires the
  `SuppressFinalize` call so derived types with finalizers don't need to
  re-implement `IDisposable`.

Verification:
- No local `dotnet` on this Linux box — CI `dotnet test` on the Windows
  runner is the gate. One-line diff reviewed.
