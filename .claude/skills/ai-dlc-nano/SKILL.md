---
name: ai-dlc-nano
description: Minimalist AI development lifecycle (AI-DLC Nano) for bug fixes and features. Use when the user invokes /ai-dlc-nano or mentions "ai-dlc-nano" in natural language, e.g. "work on issue #45 with ai-dlc-nano".
---

<!-- AI-DLC Nano spec 2.0 — this document is duplicated verbatim in every harness folder. Editing it? See "Editing the workflow" in the ai-dlc-nano project README: the same edit must land in every copy, and the version above must be bumped in all of them. -->

# AI-DLC Nano Workflow

A minimalist AI-driven development lifecycle for personal projects. It keeps just enough
written state — context, plan, audit trail — that work can be paused and resumed at any
time, on any device, with any AI agent, while spending most of the effort on actually
building and testing code.

## Activation

Activate when the user invokes `/ai-dlc-nano` **or** mentions "ai-dlc-nano" in natural language ("with ai-dlc-nano, let's implement API key support"). Everything after the trigger phrase is the **request**: a feature description, a bug report, or a tracker reference like `issue #45` or `PROJ-123`. Follow this document exactly.

## Core principles

1. **Minimal documentation.** Only tech stack, work-item intent, work-item plan, and the shared state/audit/backlog files. Never produce formal requirement docs, user stories, or architecture documents unless asked.
2. **Human confirms decisions, AI does the work.** Ask rationale questions only when the answer materially changes the implementation — ambiguous requirement, irreversible choice, more than one reasonable approach. Never ask what the repo, `tech-stack.md`, or the request already answers. Batch questions; never drip them one at a time.
3. **Advance through the user, not around them.** Present each phase's result and get a go-ahead before the next. Four gates are non-negotiable — see **Confirmation gates**.
4. **Resumable by design.** Update `state.md` and `audit.md` at every phase boundary so any agent on any device resumes exactly where work stopped.
5. **Token-thrifty on documents and on reading — never on engineering.** Respect the size caps in **File formats**; read narrowly per **Cheap reads**. This thrift covers documentation, ceremony, and *how* you read. It never covers understanding code, root-causing bugs, handling edge cases, or testing. A shorter document or a narrower read is always the right saving; a shallower implementation never is.
6. **Compose, don't replace.** This governs process, not tooling — use any other skills, rules, commands, or MCP tools as you normally would. If a code-intelligence MCP (call graph, symbol index, codebase memory) is available, prefer it over globbing and record it in `tech-stack.md` so later agents use it too.
7. **Test where it matters.** Write or update tests for behavior that can regress. Skipping a ceremony test on a trivial change is fine — but say so in the plan.

## Confirmation gates

Present each phase's result in a line or two and ask before advancing ("Intent captured, 3 decisions recorded — proceed to PLAN?"). These checkpoints want a yes/no, not a re-explanation.

**Four hard gates. Never skipped, batched away, or assumed — not on the fast path, not on resume, not under time pressure:**

1. **Generate code (PLAN → CONSTRUCT).** Never create or modify code until the user has explicitly approved `plan.md`. The single most important gate. On resume into CONSTRUCT with an unapproved plan, get approval first.
2. **Branch.** Before the first code change of a work item, confirm where it lands: stay on the current branch or create a new one (offer a name, e.g. `feat/api-keys`, `fix/issue-45`). Never create, switch, or rename a branch without an explicit yes. If the current branch is the default/protected one (`main`/`master`), actively recommend a new branch. Record the choice in `state.md`, the resume card, and `audit.md`.
3. **External side effects.** See below.
4. **Destructive local actions.** Deleting files or branches, `git reset --hard`, force operations, history rewrites — confirm first, and prefer a reversible alternative.

**No confirmation needed** (local and reversible): reading anything, running tests/build/lint, editing the working tree, writing to `ai-dlc-nano-documents/`, staging, and `git mv` of completed items into the archive.

**Autopilot.** If the user asks for fewer interruptions ("autopilot", "run through without stopping"), skip the *phase-transition* checkpoints only. The four hard gates still apply. Note the choice in `audit.md`.

### External side effects (MCP and remote git)

The boundary is the local dev environment: inside it act freely, crossing it needs a yes.

- **Free:** all reads, including MCP reads — fetching an issue, ticket, page, or record.
- **Always confirm, showing exactly what will happen:** any MCP *write* (creating/editing an issue or ticket, comments, status or label changes, Slack/Notion/Linear messages, calendar or email, remote DB mutations) and any remote git action (`git push`, opening or merging a PR, pushing tags, triggering a deploy).
- A local commit is fine when offered or requested; **pushing** it is gated.
- With no MCP/CLI access to a remote system, never guess its contents or fabricate a result — rely on the human.
- **Commits made as part of this workflow never include AI attribution** — no `Co-Authored-By: Claude ...` trailer, no "Generated with ..." line, no mention of the assistant or tool that wrote the code. Plain, human-style commit message only.

## Directory layout

```
ai-dlc-nano-documents/          # at the repo root; committed — this is the resume state
├── tech-stack.md               # stack, conventions, test commands, repo size tier
├── codebase-map.md             # routing table + "do not read in full" guardrail (always created)
├── codebase-areas/<area>.md    # large repos only: just-in-time deep dives
├── state.md                    # active item + phase + branch (a cache, not the truth)
├── audit.md                    # append-only log — NEVER read in full
├── backlog.md                  # out-of-scope findings, carried across work items
└── work-items/
    ├── <NNN>-<slug>/           # e.g. 001-fix-login-redirect, 002-issue-45-api-keys
    │   ├── intent.md           # what & why: source, request, decisions
    │   └── plan.md             # how: resume card, tasks, tests, files
    └── _archive/<NNN>-<slug>/  # completed items beyond the last ten. Never read these.
```

IDs are zero-padded sequence numbers plus a short slug; include the tracker reference when there is one (`003-issue-45-api-keys`).

## Cheap reads and cheap writes

Not style preferences — this is how the workflow stays affordable on a large repo. **Never assume a POSIX shell on Windows.**

| Need | POSIX / Git Bash | PowerShell |
|---|---|---|
| List / count tracked files | `git ls-files` · `git ls-files \| wc -l` | `git ls-files` · `(git ls-files).Count` |
| Search code | `git grep -n "<pat>"` | same |
| Read a slice | `sed -n '340,420p' <f>` | `Get-Content <f> \| Select-Object -Skip 339 -First 81` |
| Last N lines | `tail -n 15 <f>` | `Get-Content <f> -Tail 15` |
| Append one line | `printf '%s\n' "<line>" >> <f>` | `Add-Content <f> "<line>"` |
| Current commit | `git rev-parse --short HEAD` | same |
| UTC timestamp | `date -u +%Y-%m-%dT%H:%MZ` | `(Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mmZ")` |
| Largest tracked files | `git ls-files \| xargs wc -c \| sort -rn \| head -20` | `git ls-files \| %{Get-Item $_} \| Sort-Object Length -Desc \| Select-Object -First 20 Name,Length` |
| Is a commit present here? | `git cat-file -e <sha>^{commit}` | same |

- Prefer `git ls-files` over `find`/`ls -R` and `git grep -n` over `grep -r`: both skip `node_modules`, `.venv`, build output and vendored code for free.
- **Never read a file listed in the "Do not read in full" table of `codebase-map.md`** — grep the one symbol, or run the regeneration command recorded beside it.
- **Never read any file over ~500 lines in full.** Locate with `git grep -n`, then read a window. For long project documents use the anchor index in `codebase-map.md` and `sed -n` the section.
- `git diff --stat` before `git diff`. Never read `audit.md` in full. Never read `work-items/_archive/` unless the user names an item.

## File formats

Each cap is a ceiling, not a target. If content doesn't fit, cut detail — never raise the cap.

### `state.md` — overwrite, never append. Cap: 30 lines, ≤5 paused items.

```markdown
# AI-DLC Nano State
- Active work item: work-items/003-issue-45-api-keys  (or "none")
- Phase: CLARIFY | PLAN | CONSTRUCT | WRAP-UP  (or "—")
- Branch: fix/issue-45  ("—" until chosen)
- Base SHA: a92c083  (HEAD when the current phase began; "—" until CONSTRUCT)
- Next step: <one sentence: the very next action on resume>
- Paused work items:  (newest first, at most 5; or "none")
  - work-items/002-dark-mode (CONSTRUCT, feat/dark-mode, base 31ff0ac)
- Uncommitted code: yes | no
- Last updated: <ISO datetime> by <agent>
```

A **cache, not the source of truth.** When the paused list is full the oldest entry is *dropped*, losing nothing: each item's resume card carries its own phase, branch and base SHA. Recover a dropped item with `git grep -l "phase:" -- "work-items/*/plan.md"`.

**Precedence:** if `state.md` and a resume card disagree, **the card wins** — it lives beside the work. Say so, then correct `state.md`.

### `plan.md` — resume card first, so `head -6` is a complete resume. Cap: 40 lines.

```markdown
<!-- phase: CONSTRUCT | branch: fix/issue-45 | tasks: 3/7
     base: a92c083 | updated: 2026-07-17
     next: wire the API-key middleware into the router -->
# Plan: <title>
## Tasks
- [ ] <small, verifiable task>
## Tests
- <what will be tested and how, or "none — trivial change, confirmed with user">
## Files
- <files expected to change>
```

Checkboxes are the fine-grained truth; tick them as tasks complete. Refresh the card at **phase transitions and on pause only** — `tasks: n/m` is a hint, not an authority. `base:` is the short SHA of HEAD when the current phase began. The card's `phase:` is the item's own — `CLARIFY`, `PLAN`, `CONSTRUCT`, `WRAP-UP`, or `DONE` once complete.

### `audit.md` — append-only. **Never read in full. Never open with a read-then-edit tool.**

An append-only log is only cheap if appending is cheap. Tools that must read a file before editing it cost the whole file at every phase boundary, and a read-modify-write can silently reorder history. Therefore:

- **To write:** append via the shell — `printf '%s\n' "<line>" >> ai-dlc-nano-documents/audit.md`, or `Add-Content`.
- **To read:** `tail -n 15` for context, `grep` to look something up. Never the whole file.
- Never edit past lines.

One physical line per event, no wrapping, **≤200 characters** total, detail ≤120:

```
- <ISO datetime> [<agent>] <item-id> <EVENT> @<short-sha>: <detail>
```

```markdown
- 2026-07-17T14:32Z [claude] 003-issue-45-api-keys PHASE CLARIFY→PLAN @a92c083: store keys hashed (user confirmed)
- 2026-07-17T14:35Z [claude] 003-issue-45-api-keys BRANCH @a92c083: fix/issue-45 created off main (user confirmed)
```

`EVENT` is exactly one of `CREATED` · `PHASE X→Y` · `DECISION` · `BRANCH` · `SIDE-EFFECT` · `REVISED` · `PAUSED` · `DONE` · `ABANDONED`.

Log: item created, every phase transition, every human-confirmed decision, the branch choice, every external side effect, and completion. **Reasoning does not go here** — the trail and the reasoning have different lifetimes. Rationale belongs in `intent.md` and `plan.md`, which are read deliberately.

### `intent.md` — cap: 30 lines.

```markdown
# <title>
- Source: issue #45 <url> | Jira PROJ-123 | human request
- Type: bug | feature
## Request
<1–5 bullets: what is wanted and why>
## Decisions
- <question asked> → <answer confirmed by user>
## Out of scope
- <anything explicitly excluded>
## Follow-ups
- <deeper issues found during construction; promoted to backlog.md at WRAP-UP>
```

### `backlog.md` — cap: 50 lines, one line each, ≤120 characters.

```markdown
# Backlog
<!-- Out-of-scope findings, newest first. Delete a line when it is resolved. -->
- [2026-08-21] backend/billing — proration rounds before FX conversion (found: 007-invoice-fix)
```

Where, what, which item found it — nothing more. If it would exceed 50 lines, say so and offer to promote the oldest entries to tracker issues (gated) or drop them.

## Phases

### Phase 0 — BOOTSTRAP (first run in a repo only)

Load the setup reference in exactly two situations, both one-time:

- `ai-dlc-nano-documents/tech-stack.md` does not exist → **bootstrap**: create `tech-stack.md`, `codebase-map.md`, `state.md`, `audit.md`, `backlog.md`, `work-items/`, and the `.gitignore` block.
- The documents exist but predate this spec — no resume card in `plan.md`, no `Base SHA` in `state.md`, no `codebase-map.md` or `backlog.md` → **upgrade in place**, then continue.

Otherwise skip straight to INTAKE and never load it.

> **STOP — load the setup reference.** Read `reference/bootstrap.md` in full now, follow it, then return here.
>
> If that file is unavailable, do **not** skip Phase 0. At minimum: create `tech-stack.md` including a `Size tier` line recording the tier, the tracked-file count and the date; create `codebase-map.md` carrying a "Do not read in full" table built from the largest tracked files; create `state.md`, `audit.md`, `backlog.md`, `work-items/`, and the `.gitignore` block. Then tell the user the reference file was missing.

### Phase 1 — INTAKE

Establish what to work on:

1. GitHub issue reference + GitHub MCP or `gh` available → fetch title, body, labels, comments.
2. Jira reference + Jira/Atlassian MCP available → fetch summary, description, comments.
3. No tracker access, or the fetch fails → say so briefly and ask the user to paste details, or work from their description.
4. Plain natural language → use it directly.

Then two one-command checks:

- **Repo size re-check.** Count tracked files; compare with the `Size tier` line in `tech-stack.md`. If the count crossed a tier boundary **or tripled**, say so and offer to generate or upgrade the codebase map. A tier decided on day one against an empty scaffold must never govern the repo forever — this check is what prevents that.
- **Backlog check.** `grep` `backlog.md` for the area(s) in scope. Surface any relevant entry and offer to fold it into this work item.

Create the work item folder, draft `intent.md` (Request section), set `state.md` to CLARIFY, append a `CREATED` line. Confirm before advancing.

**Fast path.** If the request is small AND unambiguous (typo, copy change, config tweak, obvious one-function bug — nothing irreversible, no design choice), collapse CLARIFY and PLAN into one message: assumptions, a 1–3 task micro-plan, and the branch choice together; **one** confirmation; then construct. Still create `intent.md` and `plan.md` (three lines each is fine, resume card included) and still append to `audit.md` — the trail must never have holes; only the ceremony shrinks. The hard gates still hold: the fast path bundles them into that single confirmation, it does not remove them. When in doubt whether something is trivial, it isn't.

### Phase 2 — CLARIFY

Read the request against `tech-stack.md` and `codebase-map.md`. Route first; read source only where the routing table points. For a large repo, note which area(s) are in scope and consult their area files.

- Ask a **single batched list** of only the questions that materially affect implementation — edge cases, ambiguous behavior, irreversible choices, scope boundaries. If nothing is genuinely ambiguous, ask nothing and instead state your assumptions in one short list for a quick "confirm / correct".
- Record every answer and confirmed assumption under `## Decisions` in `intent.md`; append `DECISION` lines. Confirm before advancing.

> **Large repos only.** If `tech-stack.md` records the **large** tier, read `reference/large-repo.md` before PLAN — it defines area files and the freshness protocol. At the trivial and standard tiers, skip it.
>
> If that file is unavailable: create `codebase-areas/<area>.md` just-in-time for touched areas only, stamp each one `verified: <date> @ <short-sha>`, and test its freshness with `git diff --stat <sha> -- <path>` — empty output means the area file is exactly correct.

### Phase 3 — PLAN

Write `plan.md`: resume card, short task checklist, test approach (state the rigor tier), expected files. On a large repo, generate any missing area file for the areas in scope and reflect its key facts in the plan.

Then **hold at two hard gates before any code exists:**

1. Present `plan.md`, get an explicit go-ahead (or apply the user's edits and re-confirm).
2. Confirm the **branch** — recommend a new one if the current branch is default/protected. Create or switch only after the yes.

Once both are satisfied: record `base:` = current short SHA in the card and `state.md`, advance to CONSTRUCT, append the `PHASE PLAN→CONSTRUCT` line.

### Phase 4 — CONSTRUCT

The main event — where the effort saved on documentation gets spent. Confirm you are on the agreed branch, then work `plan.md` tasks in order, ticking each box as it completes, following `tech-stack.md` conventions strictly.

**Proportional rigor** — every change is verified; *how* scales with blast radius:

- **Trivial** (typo, copy, comment, config value, obvious rename): no new test — it would just restate the constant. Verify by observation: run the command, render the page, re-read the changed line in context. Run the suite only if it is cheap or touches the changed path.
- **Standard** (logic changes, bug fixes, features): the rules below.
- **High blast radius** (migrations, deletion paths, auth/permissions, money, concurrency): the rules below **plus** failure and rollback paths tested, and edge cases double-checked with the user if any doubt remains.

State the tier in `plan.md` (`Tests: none — trivial copy change` is a fine entry). Skipping a test is always allowed to be a decision, never an omission.

**Bug fixes:**

- **Reproduce before you fix.** Confirm behavioral bugs with a failing test — or a demonstrated repro where a test is impractical — before changing code. If you cannot reproduce it, say so and ask; never fix blind. (Trivial defects need only be observed.)
- **Root cause, not symptom.** Trace the failure to its actual cause before patching. If the root cause is out of scope, fix the in-scope defect and record the deeper issue under `## Follow-ups` rather than silently band-aiding.
- The repro test stays in the suite as the regression test.
- Check for siblings: flag the same faulty pattern in obviously adjacent code (fix it only if in scope).

**Features:**

- Read the code you're extending before writing new code — reuse existing helpers, follow existing patterns, never introduce a second way to do what the codebase already does.
- Cover the edge cases implied by the Decisions in `intent.md` — empty inputs, error paths, boundary values — in both implementation and tests.
- Handle failures the way the surrounding code does. No bare swallowed exceptions, no TODO-stubbed paths.

**Everything:**

- Test at the tier the plan states. For standard and high-blast-radius work run the **full** suite (plus lint/format if configured), not just the new tests, and fix failures before ticking. Never tick a box on code you haven't run or observed working.
- Verify user-visible behavior end-to-end once — run the CLI command, hit the endpoint, load the page. Green unit tests don't prove the wiring.
- Leave the code clean: no debug prints, no commented-out blocks, no unused imports from experiments.
- A decision CLARIFY missed → pause, ask, record it in `intent.md` and `audit.md`, continue. A plan that turns out wrong → revise `plan.md` and append a `REVISED` line, rather than silently diverging.
- **Out-of-scope findings are never dropped silently.** Record them under `## Follow-ups`; if GitHub/Jira access exists, offer to open an issue — showing the exact content and getting a yes first, since that is an external side effect.
- On a large repo keep touched area files accurate and re-stamp their `verified:` line.
- On pause, update `state.md` "Next step" and the resume card.

Advance to WRAP-UP only when every box is ticked, the full suite passes, and behavior is verified — not merely when code exists.

### Phase 5 — WRAP-UP

1. Summarize: what changed, test results, follow-ups, and the branch the work is on.
2. **Backlog.** Promote each `## Follow-ups` entry into `backlog.md` as one line pointing back at this item. Delete any backlog line this item resolved.
3. **Map upkeep.** If structure changed, update `codebase-map.md` and touched area files and re-stamp them. Keep the overview a routing table. Add any new generated artifact to the "Do not read in full" table; regenerate the anchor index of any long document that changed.
4. **Retention.** Keep at most the **ten most recent completed** items in `work-items/`; `git mv` older ones into `work-items/_archive/<id>/`. Not tidiness: everything under `work-items/` is committed by design, so it lands in every clone, every `git ls-files` sweep and every glob result — while only the active item, the paused items and the last few completions are ever consulted. Archived items stay in git history and stay greppable; they just leave the working set. No confirmation needed.
5. **Tracker update (optional, gated).** If the item came from GitHub/Jira and the MCP or `gh` is available, draft a concise comment capturing decisions and what was implemented. Post **only after explicit confirmation**; append a `SIDE-EFFECT` line.
6. Set the active work item to "none"; append a `DONE` line.
7. Do not commit, push, open PRs, or merge unless asked. Local commits are fine on request; pushing, PRs and merges are gated.

## Pausing

Phase-boundary updates mean nothing extra is normally required. But when the user explicitly stops mid-CONSTRUCT ("let's continue tomorrow"):

1. Update `state.md` — phase, branch, base SHA, next step, and the "Uncommitted code" flag.
2. Refresh the `plan.md` resume card. This is what keeps the item resumable after it falls off the paused list.
3. If code changes are uncommitted, remind the user that **cross-device resume needs the code committed and pushed, not just the documents**, and offer a WIP commit (`wip(ai-dlc-nano): 003-issue-45-api-keys — 2/5 tasks`) on the item's branch. Commit only on a yes; pushing needs its own yes. If they commit, record the new SHA in the card and append a `PAUSED` line.

## Resuming

Read `state.md`, then at most `head -6` of the active item's `plan.md`. That is the entire resume read — roughly 350 tokens. Do **not** reload `intent.md` and the full `plan.md` to work out where you are; load them when you need their content, not to orient.

With an active item, ask nothing: check out the recorded branch if not already on it (switching to an existing branch is free; creating one still needs the gate), announce the position in one or two sentences ("Resuming 003-issue-45-api-keys at CONSTRUCT on fix/issue-45: 2 of 5 tasks done; next: wire the middleware"), and continue from the recorded next step. If the user's new request is clearly about something else, ask whether to switch — the old item moves to the paused list and stays resumable.

**Verify the code matches the plan with one command, never by re-reading source:**

- `git cat-file -e <base-sha>^{commit}` — if the commit isn't in this clone, the other device's work was never pushed. Say exactly that; offer to wait for the push or redo the missing tasks.
- `git diff --stat <base-sha>` — shows precisely what changed since the phase began. If `plan.md` claims tasks are done but the diff is empty or misses the relevant files, say so and ask whether to redo them or adjust the plan. Never silently continue from state the code doesn't support.

Resuming into CONSTRUCT with a never-approved plan → re-confirm the plan (hard gate) before writing code.
