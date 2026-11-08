---
"hearbud": patch
---

Fix UiDiagnostics CI compile errors

What: `MenuFadeEnabled` → `MenuFade`, `SystemParameters.MousePresent`
→ WinForms `SystemInformation.MousePresent`, typed
`TabletDevice` loop variable (non-generic collection), fully qualified
`System.Windows.Controls.ComboBox` (WinForms ambiguity), and
`CultureInfo.InvariantCulture` on all `AppendLine`/`ToString` call sites.

Why: `dotnet test -c Release` failed on the Windows runner with
CS0117/CS1061/CS0104/CA1305 errors.

Verification: CI `dotnet test` on the Windows runner is the gate (no
local dotnet on this Linux box).
