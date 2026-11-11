---
"hearbud": patch
---

Cap mic gain at 3x with high-gain hint

- What: mic slider `Maximum` 10.0 to 3.0 (`MainWindow.xaml`, tooltip
  updated), `AppSettings.Validate` mic clamp 0-10 to 0-3 (persisted
  8.756-style settings snap down on load; slider coerces too), gain
  label appends "— high, move mic closer" above 2x. Loop untouched.
  `EDGE_CASES.md` max-gain probe note updated. Tests pinning 10x
  updated (`RoundTrips` 3.5 to 2.5, `ClampsGainAbove10` to
  `ClampsMicGainAbove3`, independent-clamp bound to 3.0).
- Why: mic at 8.7x peaks 0.79 post-gain so CLIP stays dark while the
  noise floor gets +18 dB of static. CLIP means no digital overs, not
  clean audio; above ~2x is floor, not signal.
- Verification: `dotnet test Hearbud.Tests -c Release` green (276/276).
