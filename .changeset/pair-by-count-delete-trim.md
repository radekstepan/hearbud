---
"hearbud": patch
---

Delete windowed pull and drift trim; pair by count

- What: `TryPopFixedFrame` in `Hearbud/RecorderEngine.cs` is now pure
  pair-by-count (take-oldest); the windowed-overlap branch, stale-prefix
  drop, `TryGentleTrim` (with its learned baseline, PI gains, deadband and
  target constants), and `FindZeroCrossDrop` are deleted, as is the trim
  call in `DoMixerTick`. The diagnostic `DisableSyncPathsForTest` flag is
  gone — this is the permanent path, not a bypass. Kept: resync as the
  single bounded-loss path (>80 ms sustained), zero-fill on true FIFO
  shortfall, and tick bookkeeping. The `trim=`/`trimTot=`/`staleD=` log
  columns are removed with the mechanism they measured.
- Why: measured `rec-20261111_003933` (peaks clean, mic 0.126 / mix 0.320)
  still garbled exactly while system audio played (`under` 1→47, `silenced`
  369→5750, `trimTot` 4790, all flat before/after). The window/trim path cut
  and padded healthy audio chasing a constant delivery offset; the one-take
  diagnostic with both disabled confirmed the cure, so the path is deleted
  rather than gated.
- Verification: `dotnet test Hearbud.Tests/Hearbud.Tests.csproj -c Release`
  green (windowed-pull tests replaced with pair-by-count equivalents;
  trim/Chaos drift-absorption tests deleted with the mechanism). (Built with
  SDK 9 by temporarily moving pinned `global.json` aside, then restored;
  `DOTNET_ROLL_FORWARD=LatestMajor` for testhost.)
