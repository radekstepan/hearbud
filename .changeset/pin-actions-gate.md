---
"hearbud": patch
---

Pin CI actions to SHAs and strengthen changeset gate

What:
- `build.yml`: pinned all four `uses:` refs from `@v4` tags to full commit
  SHAs (trailing `# v4` comment kept): actions/checkout
  `11d5960a326750d5838078e36cf38b85af677262` (x2), actions/setup-dotnet
  `67a3573c9a986a3f9c594539f4ab511d57bb3ce9`, actions/upload-artifact
  `ea165f8d65b6e75b540449e92b4886f43607fa02`.
- `build.yml`: pinned `dotnet-version` from `8.0.x` to `8.0.1xx` so the band
  matches `global.json` (`8.0.100` + `rollForward latestPatch`).
- `build.yml`: `changeset-check` (still `ubuntu-latest`) upgraded from
  presence-based to diff-based — fetches `origin/master` and fails when
  `git diff --name-only origin/master...HEAD -- .changeset/` adds no
  changeset file (ignores `README.md`; still exempts `refs/heads/master`
  and `[skip ci]`).

Why: SHA pins close the supply-chain tag-move window; the exact SDK band
keeps CI on the global.json feature band; the diff-based gate stops stale
changesets (merged earlier, unrelated to the branch) from satisfying the
check.

Verification: YAML not executed locally — needs CI run; reviewer confirm
both jobs green.
