---
"hearbud": patch
---

Scope out CA1861 for tests (inline assert literals)

What:
- `Hearbud.Tests.csproj`: `NoWarn` extended with CA1861, commented.

Why:
- The 7 findings are small `new[]` literals at xUnit assert sites in
  `RecorderEngineResamplerQualityTests` (fold-down vectors). The literal
  at the assert site *is* the test documentation; hoisting to
  `static readonly` fields would obscure the tests for zero runtime
  benefit (allocated once per test run). Same rationale as the CA1707
  scope-out: test ergonomics, not shipped code. Production keeps both
  rules at error severity.

Verification:
- No local `dotnet` on this Linux box — CI `dotnet test` on the Windows
  runner is the gate. One-line csproj diff reviewed.
