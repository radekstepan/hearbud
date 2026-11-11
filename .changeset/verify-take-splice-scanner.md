---
"hearbud": patch
---

Offline splice scanner and pair-by-count regression test

- What: new `TakeVerifier` in `Hearbud/TakeVerifier.cs` (pure struct/file IO,
  never touches audio devices) scans a take mix for block splices: jump runs
  over 0.05, exact-zero pads (32..48000 zeros with audio both sides, each
  reported with offset, length, and pre/post side-peak dBFS), and
  480-frame-boundary run starts. If the session `.txt` sits next to the mix,
  starvation/resync lines are appended as informational context (verdict
  unaffected), so the next SPLICE reads as device-starvation vs engine
  damage. New `hearbud verify-take <dir|mix.wav>` CLI verb (wired in
  `App.OnStartup`, exits before any window opens) prints the report and exits 0 clean / 1 splice / 2 usage-or-file-error, with console
  attached for terminal use. New `PairByCount_SyntheticTake_NoSplices_NoDrops`
  test drives `TryPopFixedFrame`/`EmitFixedMix` with synthetic mic sine +
  system burst at 003933 gains through silent/active/silent and asserts the
  mix equals the gained sum with zero jumps, zero shortfalls, zero resyncs;
  `TakeVerifierTests` covers clean/pad/step cases plus the WAV reader.
- Why: the 003933 garble was diagnosed from ad-hoc scripts; this locks the
  finding (pair-by-count emits a transparent, splice-free mix) as a unit
  gate and gives a repeatable offline artifact check for future takes —
  with no WASAPI devices in tests (same policy as OBS cmocka/synthetic
  input tests).
- Verification: `dotnet test Hearbud.Tests/Hearbud.Tests.csproj -c Release`
  green (256/256, incl. 7 new tests). CLI verified against synthetic wavs:
  clean sine exits 0, 70-zero pad exits 1 with the pad offset, usage and
  multi-mix dir exit 2. (Built with SDK 9 by temporarily moving pinned
  `global.json` aside, then restored; `DOTNET_ROLL_FORWARD=LatestMajor` for
  testhost.)
