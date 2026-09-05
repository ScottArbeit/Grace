---
name: grace
description: Route Grace repository work to current workflow, domain, contract, runtime, testing, and documentation guidance.
---

# Grace

Read root and nearest AGENTS.md before editing. For non-trivial tracked work, use the repository's docs/Development process.md and installed `dev-process`. Current source and live tracker evidence determine implementation facts.

## Load the relevant context

| Task | Reference |
| --- | --- |
| Repository layout and project boundaries | [Project map](references/project-map.md) |
| Tracker, branch, review, validation, or cleanup | [Workflow](references/workflow.md) |
| Specification and readiness | [Specification profile](references/specification-profile.md), with installed specification |
| DTOs, events, parameters, serializers, shared code | [Shared contracts](references/contracts-and-shared.md) |
| HTTP, SDK, CLI, authorization at public entrypoints | [Public surfaces](references/public-surfaces.md) |
| Orleans, event sourcing, replay, durable transitions | [Actors and durability](references/actors-and-durability.md) |
| RBAC, PATs, OIDC, TestAuth, path permissions | [Security](references/security-and-auth.md) |
| Uploads, manifests, ContentBlocks, Service Bus, Aspire | [Runtime and storage](references/runtime-and-storage.md) |
| Selecting or changing tests | [Tests](references/tests.md) |
| Contributor, product, or workflow documentation | [Docs](references/docs-and-contributing.md) |

Load only the references needed for the current slice. The installed `specification` owns feature lifecycle and traceability; `design-readiness` resolves open product/domain/architecture choices; `dev-process` owns delivery. Avoid repeating their full checklists here.

## Grace-specific defaults

Grace uses Product V1 unless the owner chooses another profile. It has no production legacy data; add compatibility or migration behavior only for a requirement that actually exists.

Prefer a vertical slice through the nearest stable public boundary. Propagate a changed public or durable contract through the relevant Types, Shared, Server, Actors, SDK, CLI, generated artifacts, docs, and tests. Preserve established Grace vocabulary.

Use focused local evidence and required GitHub Validate on the final PR revision. Local Fast is optional broad preflight; Full is for integration reproduction or diagnosis. The repository owns current commands and project membership.

Planning-only requests remain planning. Tracker setup and implementation follow the user's requested scope. Use PowerShell and Markdown skills for generated scripts and documents, with PowerShell examples before bash / zsh where applicable. Explain unresolved product decisions through concrete user behavior.
