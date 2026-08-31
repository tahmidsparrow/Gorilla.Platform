<!-- AI-DLC Nano spec 2.0 — this document is duplicated verbatim in every harness folder. Editing it? See "Editing the workflow" in the ai-dlc-nano project README: the same edit must land in every copy, and the version above must be bumped in all of them. -->

## BOOTSTRAP and UPGRADE (Phase 0, in full)

Two one-time paths. **Bootstrap** (sections 1–7) runs the first time the workflow activates in a repo where `ai-dlc-nano-documents/tech-stack.md` does not exist. **Upgrade** (section 8) runs once in a repo whose documents predate this spec. Everything either path produces is committed, so no later run and no other agent repeats it.

### Bootstrap

### 1. Tech stack

- **Brownfield** (repo has code): analyze the repo — languages, frameworks, package manager, test framework and commands, lint/format tools, naming and structure conventions, build/run commands. Draft `tech-stack.md` and present a summary to the user for confirmation/corrections before saving.
- **Greenfield** (empty/near-empty repo): run a short assisted Q&A — one batched set of questions covering: language & framework, package manager, test framework, formatting/linting, project structure preference, target runtime/deployment. Offer sensible defaults for each so the user can just say "defaults are fine". Write `tech-stack.md` from the answers.

`tech-stack.md` format (keep under ~40 lines):

```markdown
# Tech Stack
- Language(s): ...
- Framework(s): ...
- Package manager: ...
- Test: <framework> — run with `<command>`
- Lint/format: <tools> — run with `<command>`
- Size tier: standard · 631 tracked files · recorded 2026-08-21
- Code-intelligence MCP: <name, if one is connected> | none
- Conventions: <bullets: naming, structure, error handling, anything non-obvious>
```

The **`Size tier`** line is load-bearing, not decoration. It records the tier, the tracked-file count, and the date, so that INTAKE can cheaply detect when the repo has outgrown its verdict. A greenfield repo bootstrapped on day one records `trivial · 0 tracked files` — and INTAKE will catch the moment that stops being true.

Consult `tech-stack.md` in every later phase; if the user makes a lasting stack/convention decision mid-work, update it (with their confirmation).

### 2. Codebase map

Goal: give every returning agent — including a *different* one on a different day — reliable orientation to the repo without re-reading it each time. Scale the effort to repo size, but keep tokens bounded. **Never read every file**; even a 20,000-file repo is mapped by reading on the order of a few dozen high-signal files, not thousands.

**`codebase-map.md` is always created**, at every size tier. A trivial repo gets a ten-line stub — because the map is also where the "Do not read in full" guardrail lives, and that guardrail must never be absent.

**Size tiers** (record the verdict, the count and the date in `tech-stack.md`):

- **Trivial** (≲20 source files, single obvious module): a stub map — one areas line, plus the guardrail table. Tell the user that is all you generated.
- **Standard** (up to a few thousand files): the full `codebase-map.md` routing table, about a page. No area files.
- **Large** (many thousands to tens of thousands of files): the same concise routing table **plus** just-in-time deep dives in `codebase-areas/<area>.md`, generated **only** for the areas a work item actually touches, and reused on later runs. This is what bounds tokens — you only ever deep-map the parts you work in.

**Method — breadth-first and token-bounded** (in order; stop when you have enough to route):

1. **Cheap structural signals first:** the top 2–3 levels of the directory tree; file-count and language mix per top-level directory. Use `git ls-files`, never file reads.
2. **Manifests & config:** package/build files (`package.json`, `pyproject.toml`, `go.mod`, `pom.xml`, `Cargo.toml`, …), workspace/monorepo config, CI config, Dockerfiles, framework config → module boundaries, scripts, dependencies.
3. **Entry points:** main/bootstrap, server/router setup, CLI definitions, job schedulers, migration runners.
4. **Existing docs:** README / ARCHITECTURE / docs — high value, low cost; harvest, don't duplicate.
5. **Sample, don't exhaust:** read a couple of representative files per major area to learn its conventions, then infer the rest from naming and layout.

If a code-intelligence MCP (call graph, symbol index, codebase memory) is connected, derive the map from its queries instead of from globbing, and record its name in `tech-stack.md` so later agents on other devices know to use it.

### 3. The "Do not read in full" table — generate it mechanically

Every non-trivial repo contains generated files that are catastrophic to read: OpenAPI dumps, generated API clients, lockfiles, migration bundles, fixtures, vendored code. Two such files can exceed a whole session's budget on most harnesses.

Do not ask the human to remember which files are expensive — they will forget. Measure it:

```
git ls-files | xargs wc -c | sort -rn | head -20
```

(PowerShell: `git ls-files | %{Get-Item $_} | Sort-Object Length -Desc | Select-Object -First 20 Name,Length`)

Filter that list down to genuinely generated or unreadable artifacts using the available signals — a `linguist-generated` attribute in `.gitattributes`, lockfile naming conventions, or being the declared output of a codegen script in the manifest. **Present the candidates to the user for confirmation** rather than deciding alone, then record the confirmed set in the map. Refresh it in WRAP-UP whenever a generated artifact is added.

### 4. Anchor indexes for long project documents

Long specs get read whole because nothing says where their sections start. A reader's guide without line numbers does not save anything.

For any repo document over ~800 lines, generate two indexes into `codebase-map.md`:

- **Section index** — straight out of `grep -n '^## '`: `§6 Billing Engine — lines 348–496`.
- **Identifier index** — one line per decision/ADR/requirement ID with its location. Forty decision IDs then resolve for a couple of hundred tokens instead of forty thousand.

Record the access rule beside them: *read this document with `sed -n`, never whole.* WRAP-UP regenerates an index whenever its document changed — regeneration is a one-liner, so staleness is never a reason to distrust it.

### 5. `codebase-map.md` format

```markdown
# Codebase Map
<!-- generated: <YYYY-MM-DD> @ <short-sha> · tier: standard · coverage: overview-only -->

## Do not read in full
| File | Size | Why | Instead |
|---|---|---|---|
| backend/openapi.json | 489 KB | generated | grep the one symbol; `make api-types` |
| frontend/src/schema.d.ts | 348 KB | generated | same |

## Areas            # the routing table: where to go, how to verify, one block each
- billing (backend/app/billing/**)
  purpose: invoice generation, proration, credit notes
  entry:   service.py · router: api/v1/invoices.py
  tests:   make -C backend test -k billing
  symbols: git grep -n "def <name>" -- backend/app/billing
  spec:    MASTERPLAN §6 (lines 348–496)
  deep:    codebase-areas/billing.md | not yet mapped

## Entry points
- <bootstrap, servers, CLIs, jobs, migration runners>
## Cross-cutting
- <auth, config, logging, data access, shared libs — where each lives>
## Core data models
- <model/table/type>: <one line>
## Long documents
- MASTERPLAN.md (1773 lines) — read with `sed -n`, never whole
  §1 Overview 1–120 · §6 Billing Engine 348–496 · §9 Deploy 1204–1388
  IDs: D-01→L412 · D-02→L455 · ADR-07→L1120
## Gotchas
- <generated code, deprecated zones, do-not-touch areas, surprising couplings>
```

Each area block answers the question an agent actually has — *"I need to change invoicing; where do I go, and how do I verify it?"* — so the path from issue text to the right directory, the right test command and the right spec window costs no exploratory globbing. That is the single most repeated expense on a large repo, and it is fully cacheable.

The `symbols:` line records the *command* to find a symbol rather than a generated symbol table: it never goes stale and costs nothing to maintain.

Keep it a **routing table, not an encyclopedia** — about a page regardless of repo size, and at most ~15 area blocks before you group them. Depth lives in area files, created on demand.

Present the map to the user for confirmation when first generated (for standard repos, that is the whole map).

### 6. Gitignore

Ensure the repo's `.gitignore` excludes the dropped-in AI-DLC Nano platform files while keeping `ai-dlc-nano-documents/` tracked (it must be committed — it is the cross-device resume state). Create `.gitignore` if missing, or append a clearly marked block if the entries are absent:

```gitignore
# --- ai-dlc-nano workflow files (keep ai-dlc-nano-documents/ tracked) ---
.claude/skills/ai-dlc-nano/
.agents/skills/ai-dlc-nano/
.cursor/rules/ai-dlc-nano.mdc
.codex/ai-dlc-nano.md
.codex/ai-dlc-nano-bootstrap.md
.codex/ai-dlc-nano-large-repo.md
.opencode/skills/ai-dlc-nano/
.opencode/commands/ai-dlc-nano.md
.github/prompts/ai-dlc-nano.prompt.md
.github/instructions/ai-dlc-nano.instructions.md
```

Ignore only these specific paths — never blanket-ignore `.claude/`, `.agents/`, `.cursor/`, `.codex/`, `.opencode/`, or `.github/`, since those folders may hold the project's own skills, settings, rules, or CI workflows. Never ignore a root `AGENTS.md` either: it is a shared, user-owned file that other agent tools read. If any of these files are already tracked by git, tell the user to run `git rm --cached <path>` for them (do not run it yourself unless asked).

### 7. Remaining files

Create `state.md`, `audit.md`, `backlog.md`, and `work-items/` if missing, using the formats in the core document. Append a `CREATED` line to `audit.md` recording the tier verdict and the file count.

Bootstrap is then complete and never runs again. Continue to INTAKE.

### 8. Upgrading a repo bootstrapped under an older spec

If `ai-dlc-nano-documents/` already exists but predates these formats, upgrade it in place. These are non-destructive local doc edits and need no confirmation gate:

- **`plan.md` without a resume card** → add one; take `phase` and `branch` from `state.md`, `base` from current HEAD.
- **`state.md` without a `Base SHA` line, or with more than 5 paused items** → add the line, trim the list to the 5 newest. Nothing is lost: the resume cards carry each item's own phase, branch and base.
- **`audit.md`** → leave every existing line untouched. The grammar and the 200-character cap apply to new entries only; never rewrite history to match the new format.
- **`codebase-map.md` missing** → create it at the next INTAKE size re-check, including the "Do not read in full" table.
- **`backlog.md` missing** → create it, seeded from any `## Follow-ups` sections in non-archived work items.
- **More than ten completed work items** → archive the older ones per WRAP-UP retention.

Mention the upgrade to the user in one sentence and continue. It is not a phase, and it never runs twice.
