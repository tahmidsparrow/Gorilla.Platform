<!-- AI-DLC Nano spec 2.0 — this document is duplicated verbatim in every harness folder. Editing it? See "Editing the workflow" in the ai-dlc-nano project README: the same edit must land in every copy, and the version above must be bumped in all of them. -->

## Large repos — area files and the freshness protocol

Load this only when `tech-stack.md` records the **large** tier (many thousands to tens of thousands of files). At the trivial and standard tiers, `codebase-map.md` alone is the whole map and none of this applies.

The principle that bounds tokens at this size: **you only ever deep-map the parts you work in.** The routing table in `codebase-map.md` stays about a page no matter how large the repo grows; depth lives in per-area files that are created the first time a work item enters that area, and reused by every later run and every other agent.

### `codebase-areas/<area>.md` format

```markdown
# Area: <name> (path/)
<!-- area: backend/billing | verified: 2026-08-14 @ a92c083 | files: 34 -->
## Responsibility
## Key files & what they do
## Internal flow / important interactions
## External dependencies (other areas, services, tables)
## Gotchas
```

### Freshness is a git fact, not a date

A `last verified` date only tells you when someone looked, not whether it is still true — so "trust it only if it isn't obviously stale" is a judgement an agent cannot actually make without re-reading the area, which defeats the entire point of having the file. Store the verification as a commit instead:

```
verified: 2026-08-14 @ a92c083
```

**The freshness test is one command:**

```
git diff --stat a92c083 -- backend/billing
```

- **Empty output** proves the area file is exactly correct, for about fifty tokens. Trust it completely and move on.
- **Non-empty output** names precisely which files moved — so the refresh is scoped to those files, not to the whole area.

This converts "trust it or re-read it" into a decidable check, which is the difference between a map that gets used and one that gets ignored. Apply the same test to `codebase-map.md` itself using the `@ <short-sha>` in its generated header.

### Working with area files

- **Consult before working.** Trust the routing table for routing. Run the freshness test on an area file before trusting its detail; if it fails, refresh only the files the diff named.
- **Create just in time.** Generate `codebase-areas/<area>.md` the first time a work item enters that area — during PLAN, so its key claims land in the plan and the user can catch a wrong assumption before any code is written. Area files do not each need their own confirmation gate.
- **Update as you change structure.** Whenever construction adds, moves, or removes structure, fix the affected routing-table block and area file, and re-stamp `verified:` with today's date and the current short SHA. Map corrections are local docs and need no confirmation.
- **Never generate a static symbol table.** Record the lookup command in the map's `symbols:` line instead (`git grep -n "def <name>" -- <path>`). A command cannot go stale and costs nothing to maintain; a generated symbol index does both.
- **WRAP-UP check.** When a work item completes in a large repo, make sure every touched area file reflects reality and carries a current `verified:` stamp.

### Drift

If the repo signature has moved substantially since the map's `generated` stamp — many new files, or the routing table clearly predates the current structure — say so and offer to refresh, rather than trusting it blindly. INTAKE's size re-check is what surfaces this cheaply; do not wait to notice it by accident.
