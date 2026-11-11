---
"hearbud": patch
---

Memoryless mix-bus cap plus mix CLIP light

- What: `ApplyMixLimit` in `Hearbud/RecorderEngine.cs` is now a memoryless
  per-frame cap (`ComputeMixScale` peak target applied directly, no ~150 ms
  release envelope), so an overshooting frame caps at 0 dBFS without ducking
  following audio; the sticky `_mixLimitGain` state is gone. Both emit paths
  (`EmitFixedMix`, `EmitMicOnlyFixed`) report a post-gain/post-headroom mix
  meter (`LevelSource.Mix`, throttled like the per-source meters), and the UI
  gains a Mix bar/dB/CLIP row (`Hearbud/MainWindow.xaml`) so persistent overs
  at hot gains (e.g. persisted MicGain 5.078) light the Mix CLIP indicator
  instead of being ducked away. Stems untouched (still raw pre-gain writes).
- Why: measured `rec-20261110_221510` (mic stem peak 0.374 raw x 5.078 gain x
  0.5 headroom + system = ~1.03 over ceiling, mix file pinned at int32 full
  scale) proved the fixed -6 dB headroom cannot cover +14 dB gains, and the
  sticky release ducked audio after every overshoot while no meter showed
  the post-sum peak. The cap keeps the file at <= 0 dBFS; the light tells
  the user to turn the gains down.
- Verification: `dotnet test Hearbud.Tests/Hearbud.Tests.csproj -c Release`
  green (full suite, incl. new `MixCap_*`/`MixMeter_*` tests replacing the
  two sticky-release tests). (Built with SDK 9 by temporarily moving pinned
  `global.json` aside, then restored; `DOTNET_ROLL_FORWARD=LatestMajor` for
  testhost.)
