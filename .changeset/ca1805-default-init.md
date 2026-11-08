---
"hearbud": patch
---

Clear CA1805 default-initializer warnings-as-errors

What:
- `Hearbud/RecorderEngine.cs` (37) + `Hearbud/AppSettings.cs` (4):
  removed redundant `= 0` / `= 0.0` / `= 0f` / `= false` initializers on
  field declarations and auto-properties. C# zero-initializes these;
  behavior-preserving by language guarantee.

Why:
- `AnalysisMode=Recommended` + `TreatWarningsAsErrors` (build hardening)
  escalates CA1805 to a build error. These initializers pre-date the gate.

Verification:
- No local `dotnet` on this Linux box — CI `dotnet test` on the Windows
  runner is the gate. Declaration-only diff reviewed (`git diff` shows
  initializer removals and nothing else).
