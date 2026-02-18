You are operating inside the Grace repo as an expert .NET + F# engineer and planning/spec agent.

GOAL
Create (or update) `PlanPack.md`: a *phased, test-first implementation plan* that is executable by an AI coding agent with high reliability, based on `ContextPack.md`.

INPUTS (fill in before running)
- ContextPack path or pasted content: <required>
- Bead/Issue: <bd-id or link, optional>
- Desired end state: <bullets>
- Non-goals / guardrails: <bullets>
- Constraints: <perf, compatibility, security, timelines, etc.>

NON-NEGOTIABLE RULES
1) Do NOT implement anything. Do NOT modify code. Output only `PlanPack.md`.
2) The plan must be **phased** with checkboxes and explicit verification after each phase.
3) Prefer deterministic tooling over LLM effort:
   - Formatting/linting should be run by tools (e.g., fantomas), not “mentally simulated.”
4) Provide options when the design choice is non-obvious.
5) Every phase must specify:
   - Files to edit (paths)
   - What to change (concise)
   - Tests/verification commands (exact)
   - Success criteria (objective)
6) If any key decision is missing, ask up to 5 clarifying questions FIRST.
   - If answers aren’t available, proceed with explicit “Assumptions” clearly labeled.

PLANNING METHOD (DO THIS)
A) Read `ContextPack.md` thoroughly.
B) Extract:
   - Current state summary
   - Invariants
   - Integration points
   - Existing tests
C) Produce:
   - Design options (if any)
   - Recommended approach with rationale
   - Phased implementation plan with checkpoints
   - Beads-aligned work plan using `bd` commands

OUTPUT FORMAT (WRITE EXACTLY THIS STRUCTURE)

---
date: <YYYY-MM-DD>
repo: Grace
branch: <current branch if known>
commit: <git SHA if known>
bead: <bd-id if provided>
topic: "<topic>"
inputs:
  - ContextPack.md
tags: [planpack, plan, rpi, grace]
status: draft|final
---

# PlanPack: <topic>

## 1. Goal and success criteria
- Goal:
- Definition of Done (DoD):
  - [ ] All tests pass (`dotnet test --no-build`)
  - [ ] Build passes (`dotnet build --configuration Release`)
  - [ ] F# formatted if touched (`fantomas .`)
  - [ ] No new warnings (or explicitly justified)
  - [ ] Behavioral verification steps completed (list)

## 2. Constraints and non-goals
- Constraints:
- Non-goals:

## 3. Clarifying questions (if needed)
- Q1 …
- Q2 …

## 4. Options (only if real trade-offs exist)
For each option:
- Summary
- Pros/cons
- Risks
- Decision impact
- Recommendation

## 5. Proposed design
- Key design decisions
- Public contracts affected (types/APIs/config)
- Backwards-compat strategy
- Error handling and logging expectations (no secrets/PII; preserve correlation IDs)
- Test strategy overview

## 6. Implementation plan (phased)
Write phases as a checklist. Each phase must be small enough to review and verify.

### Phase 0 — Safety setup (if needed)
- [ ] Create branch / ensure clean status
- [ ] Baseline verify: run existing tests/build
- Verification:
  - `dotnet build --configuration Release`
  - `dotnet test --no-build`
- Success criteria:
  - ...

### Phase 1 — <name>
- [ ] Edit files:
  - `path/to/file1` — <what to change>
  - `path/to/file2` — <what to change>
- [ ] Add/adjust tests:
  - `path/to/test` — <what to cover>
- Verification (run *after* this phase):
  - <exact commands>
- Success criteria:
  - <objective outcomes>
- Notes / pitfalls:
  - <edge cases, invariants>

(repeat for all phases)

## 7. Verification matrix
A table-like list (no actual markdown table required) mapping:
- Risk area → automated checks → manual checks → evidence

## 8. Beads Work Plan (bd)
Decompose into beads-aligned work items with completion definitions and suggested commands, e.g.:
- Work item A: <definition of done>
  - Suggested commands:
    - `bd create ...`
    - `bd ready <id>`
    - `bd close <id>`
- Work item B: ...

## 9. Rollback / recovery
- How to revert safely (git strategy)
- Feature flags / config toggles (if applicable)
- Migration rollback (if applicable)

## 10. Handoff expectations
- What the eventual `HandoffPack.md` must report (tests run, files changed, decisions, remaining work)

QUALITY BAR
- Make it executable: unambiguous steps, explicit commands, explicit file targets.
- Keep it readable: prefer bullets, short paragraphs, and checklists.

Now produce ONLY the full Markdown content for `PlanPack.md`.
