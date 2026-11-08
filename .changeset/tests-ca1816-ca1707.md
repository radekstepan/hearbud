---
"hearbud": patch
---

Test-project analyzer fallout: SuppressFinalize + CA1707 scope-out

What:
- 6 test teardowns (`AppSettingsTests`, `HistoryRepairTests`,
  `RecorderEngineChaosTests`, `WriterFailureTests`,
  `RecorderEnginePublicTests`, `RecorderEngineWatchdogTests`): added
  `GC.SuppressFinalize(this)` (CA1816). The last two weren't flagged —
  expression-bodied Disposes seem to escape this analyzer version — but
  uniform treatment costs nothing and survives analyzer updates.
- `Hearbud.Tests.csproj`: `NoWarn` extended with CA1707, commented.

Why:
- CA1816 is mechanical and correct everywhere. CA1707 is deliberately
  scoped out for tests only: underscore `Unit_State_Expected` names are
  the xUnit convention and test methods are never public API. Production
  code keeps the rule at error severity.

Verification:
- No local `dotnet` on this Linux box — CI `dotnet test` on the Windows
  runner is the gate.
