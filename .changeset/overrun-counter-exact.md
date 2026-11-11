---
"hearbud": patch
---

Exact overrun accounting in ring capacity guards

- What: `EnsureRingCapacity` and `EnsureRingCapacity_NoLog_NeedsLock`
  (mic) plus `EnsureLoopRingCapacity` (loop) in
  `Hearbud/RecorderEngine.cs` now report and count the samples actually
  evicted (`drop`, clamped to the buffered count) instead of the headroom
  shortfall (`want - cap`). Previously a huge request against a near-empty
  ring (e.g. `ring.Length * 10` with `_micCount == 0`) returned and counted
  ~1.7 M "dropped" samples while evicting nothing. The drop-oldest behavior
  itself is unchanged (still by design under sustained mic-faster-than-loop
  drift or loop bursts). `S05_MicOverrun` / `S06_LoopOverrun` probe asserts
  now pin exact accounting: empty-ring requests report 0, and a near-full
  ring evicts exactly the overflow.
- Why: the inflated counters corrupt the 1 Hz log tail
  (`over=`/`loopOver=`) and the `over == scanned drops` scanner contract,
  making real drops indistinguishable from accounting noise.
- Verification: `dotnet test Hearbud.Tests/Hearbud.Tests.csproj --filter
  "FullyQualifiedName~ContinuityProbe"` green 17/17, incl. strengthened
  S05/S06 (empty-ring 0 + near-full exact-overflow cases). (Built with SDK
  9 by temporarily moving pinned `global.json` aside, then restored;
  `DOTNET_ROLL_FORWARD=LatestMajor` for testhost.)
