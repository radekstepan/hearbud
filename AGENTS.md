# Agent Rules (binding)

Short rules for subagent work in this repo. Keep it factual and minimal.

1. **Verify by execution.** No claim without running: build, test, or
   script output. If a tool is unavailable (e.g. `dotnet` on a Linux
   box), say so and name CI as the gate.
2. **File-partition discipline.** One worker owns one file set; never
   touch files outside your assignment, even for "obvious" fixes.
3. **Minimal exact edits.** Append-only means append-only; otherwise
   smallest diff that does the job. Match existing style; no drive-by
   refactors.
4. **Changeset per commit.** Every non-`[skip ci]` push on a
   non-master branch adds one `.changeset/<kebab>.md` with
   `"hearbud": patch` front-matter plus What/Why/Verification.
   See `docs/CONTRIBUTING.md` (Changesets).
5. **Conventional subjects.** Commit subjects match
   `^(feat|fix|refactor|perf|docs|style|test|chore)(\(.+\))?: .+`
   (`docs:`, `chore:` etc.). `[skip ci]` and master version-bumps are
   the only exemptions.
6. **Evidence before synthesis.** Read files before stating facts;
   cite `path:line` for claims. If evidence contradicts a prior
   claim, say so plainly.
7. **Logging discipline.** Audio/engine paths log transitions only —
   no per-frame logging, WARN/ERROR plus tail cap, backlog/lag at 1 Hz
   with thread/tick context. See `docs/CONTRIBUTING.md`.
8. **Short factual communication.** Report files created/edited with
   line counts, what was verified, and what was not. No praise,
   no superlatives, no emojis.
