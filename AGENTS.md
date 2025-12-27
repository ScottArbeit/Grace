# AGENTS.md (Repository Root)

This repository is designed to be worked on by **AI coding agents** (Codex, Copilot Coding Agent, and similar) operating under **spec-driven development**.

This document defines the **global rules, expectations, and architectural constraints** that apply to *all agents*, regardless of which subdirectory they are working in.

Subdirectories may contain their own `AGENTS.md` files.
When present, those files **extend or specialize** this one.
If there is a conflict, the **more specific (deeper) AGENTS.md wins**.

---

## 1. Core Philosophy

Grace is:

- **Spec-first**
- **Architecture-driven**
- **Event- and actor-oriented**
- **Intentionally not Git-compatible by default**
- **Designed for extreme scale and agentic workflows**

Agents must optimize for:

1. **Correctness over cleverness**
2. **Clarity over concision**
3. **Explicitness over inference**
4. **Long-term evolvability over short-term convenience**

If you are unsure about intent, **stop and ask** or **re-read the specification**.
Do not invent behavior.

---

## 2. Spec-Driven Development (Mandatory)

Agents **must not implement features without a written specification**.

A valid specification:

- Is written in Markdown
- States goals, non-goals, and constraints
- Defines success criteria
- Calls out affected components explicitly
- Mentions test expectations (even if tests are deferred)

### Required Workflow

1. **Read the spec fully**
2. **Generate a plan** (if instructed to do so)
3. **Execute the plan step by step**
4. **Do not merge planning and execution unless explicitly told**

When a spec mentions multiple reasoning levels (for example, a high-reasoning model creating a plan and a lower-reasoning model executing it), **respect that boundary**.

---

## 3. Repository Orientation Rules

Before making changes, agents should orient themselves using:

1. `REPO_INDEX.md` (authoritative jump table)
2. Canonical entry points listed there
3. Existing patterns in nearby code

Do **not** scan the entire repository blindly.

### Canonical Entry Points (Examples)

Depending on task type, prefer:

- Server startup and wiring
- Actor definitions (`*Actor.fs`)
- Domain types and DTOs
- Shared infrastructure helpers
- Integration test harnesses

If unsure where to start, **consult `REPO_INDEX.md` first**.

---

## 4. Language and Style Rules

### Primary Languages

- **F# is the default and preferred language**
- C# is allowed only when:
  - Interfacing with frameworks that require it
  - Existing code in that area is already C#

### Style Expectations

- Favor:
  - Discriminated unions
  - Records
  - Explicit types at module boundaries
- Avoid:
  - Implicit magic
  - Reflection-heavy designs
  - Over-generalized abstractions

Code should read as **executable documentation**.

---

## 5. Architecture Constraints (Global)

Agents must respect these invariants:

- **No Dapr**
  Any Dapr references are legacy and should not be introduced or extended.

- **Actor-first domain modeling**
  Core behavior lives in Orleans grains, not services.

- **Events are durable facts**
  Do not rewrite history unless the spec explicitly says data is disposable.

- **Client and server responsibilities are distinct**
  Do not blur boundaries “for convenience”.

---

## 6. Testing Philosophy

Grace prioritizes **integration tests over unit tests**.

Agents should:

- Prefer tests that exercise real HTTP boundaries
- Use emulators and containers where appropriate
- Avoid mock-heavy unit tests unless explicitly requested

When adding a feature:

- Describe the expected tests in the spec
- Implement tests only if instructed

---

## 7. Safety Rules for Agents

Agents must **never**:

- Delete large sections of code without explicit instruction
- Perform sweeping refactors unless the spec calls for it
- Change public semantics “to improve things”
- Introduce breaking changes silently

If something looks wrong but is not mentioned in the spec:

> **Leave it alone and note it.**

---

## 8. Communication Expectations

When interacting with the human maintainer:

- Be explicit about assumptions
- Call out uncertainty
- Prefer lists, sections, and concrete references
- Cite files and modules precisely

Silence is worse than asking a clarifying question.

---

## 9. Authority Model

The human maintainer is the **final authority**.

Specifications, instructions, and follow-ups from the maintainer always override:

- Repository conventions
- Prior agent output
- Your own prior assumptions

---

## 10. Mental Model for Agents

Treat Grace as:

> A long-lived, research-grade system that is intentionally being built *slower than a startup* and *faster than academia*.

Your job is not to “finish it quickly”.
Your job is to **make the next change correct, legible, and durable**.

---
