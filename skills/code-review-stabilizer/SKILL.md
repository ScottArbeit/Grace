---
name: code-review-stabilizer
description: Compatibility entrypoint for older Grace review-recovery prompts; route to the current dev-process review protocol.
---

# Code review stabilizer

This compatibility name does not own a separate workflow. Use the installed `dev-process` entrypoint and its `references/CODE_REVIEW.md`, `references/REVIEW-AND-LANDING.md`, and relevant `references/TEMPLATES.md` section, plus Grace's repository process.

For an older recovery prompt, recover the existing finite R1 ledger and accepted dispositions. If no ledger exists, reconstruct it from current supported findings without silently launching another whole-diff review. Continue the same implementation owner for accepted repairs and use bounded R2 closure.

Preserve owner decisions, quality contract, non-goals, current-head evidence, and the DISCOVERY ESCAPE stop. Do not interpret this alias as permission for another writer, a new product rule, or an automatic R3.

New callers should name dev-process directly. Keep this alias until active callers have been migrated.
