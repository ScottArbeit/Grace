---
name: code-review-stabilizer
description: >-
  Deprecated Grace compatibility router. Use the installed dev-process skill for Review Discovery Ledgers,
  closure review, review recovery, simplification, splitting, salvage, and supersession.
---

# Code review stabilizer

This skill is no longer an active Grace workflow authority.

Use the installed `dev-process` skill instead, especially:

- `dev-process/SKILL.md`, sections on bounded R1 discovery review, R2 closure review, stop signals, and supersession;
- `dev-process/CODE_REVIEW.md` for Product V1 realistic-producer filtering and review modes;
- `dev-process/TEMPLATES.md` for the Review Discovery Ledger, Closure Review, Decision Packet, and Supersession Salvage
  Map;
- repository `docs/Development process.md` for Grace-specific branch, CI, PR, and cleanup mechanics.

## Compatibility behavior

When an older prompt invokes this skill:

1. Do not resume an unbounded review and repair loop.
2. Load the current `dev-process` authority.
3. Reconstruct one finite R1 Review Discovery Ledger from supported current findings.
4. Classify product decisions, new invariants, authority changes, and state-machine additions as owner stops.
5. Route accepted in-scope findings to the existing issue-owner worker for one consolidated repair pass.
6. Use one targeted R2 closure review.
7. Do not start an automatic R3 whole-diff review. If closure incidentally exposes one supported-world merge blocker
   outside the frozen ledger, record `DISCOVERY ESCAPE` and stop rather than ignoring it or continuing discovery.
8. Simplify, split, or supersede when the finite closure protocol cannot converge.

## Migration

Update prompts or documents that still route Grace review recovery here. New Grace work should reference `dev-process`
directly. Remove this compatibility skill after no active project source links to it.
