---
name: grace
description: Grace repository reference. Use when working in Grace to find where a feature lives across projects, or when a change touches authorization, hidden resources, stale authority, idempotency, webhooks, approvals, or manifest-backed storage.
---

# Grace

The root and nearest `AGENTS.md` come first; `CONTRIBUTING.md` covers tracked work.
Current source and the live tracker win over anything written here.

| Task | Reference |
| --- | --- |
| Finding where a feature lives across projects | [Feature map](references/feature-map.md) |
| Authorization, hidden resources, stale authority, secrets | [Authority and exposure](references/authority-and-exposure.md) |
| Grains, retries, webhooks, approvals, manifest-backed storage | [Durable workflows](references/durable-workflows.md) |

## Specifications and open questions

- Cite current code and tests by `path:line`, name the identity tuple and authoritative source for each decision, list
  the contract surfaces it reaches (chain in `src/AGENTS.md`), and pick an early tracer slice before optional
  automation or hardening.
- Explain open product questions in terms of what a user would see.
