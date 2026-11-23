---
name: grace
description: Grace repository reference. Use when working in Grace to find project ownership, domain rules, contract surfaces, security, runtime and storage guardrails, or docs conventions.
---

# Grace

Start with the root and nearest `AGENTS.md`; `docs/Development process.md` covers tracked work. Current source and the
live tracker win over anything written here.

## Load what the task needs

| Task | Reference |
| --- | --- |
| Finding code or choosing an owning project | [Project map](references/project-map.md) |
| DTOs, events, parameters, serializers, shared helpers | [Contracts and shared](references/contracts-and-shared.md) |
| HTTP routes, SDK, CLI, OpenAPI | [Public surfaces](references/public-surfaces.md) |
| Orleans grains, event sourcing, idempotency, reminders | [Actors and durability](references/actors-and-durability.md) |
| RBAC, PATs, OIDC, path permissions, secrets | [Security and auth](references/security-and-auth.md) |
| Uploads, manifests, webhooks, Service Bus, Aspire, stale authority | [Runtime and storage](references/runtime-and-storage.md) |
| README, CONTRIBUTING, AGENTS.md, process docs | [Docs and contributing](references/docs-and-contributing.md) |

## Grace defaults

- Grace is pre-production. Build the right current shape; add migration or compatibility only when an issue asks.
  Resetting development data does not excuse broken serialization, replay, authorization, or truthful failure.
- Use existing Grace vocabulary (`CONTEXT.md`). A new domain noun, durable state, state machine, authority, or public
  lifecycle is Scott's decision, not a local convenience.
- When writing a specification, cite current code and tests by `path:line`, name the identity tuple and authoritative
  source for each decision, list the contract surfaces it reaches (chain in `src/AGENTS.md`), and pick an early tracer
  slice before optional automation or hardening.
- Explain open product questions in terms of what a user would see.
