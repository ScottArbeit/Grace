# Grace — agents.md (Cross-project AI agent instructions)

## Purpose

This document contains repository-wide rules, conventions, and instructions for AI coding agents (Copilot, Claude, or other code-writing assistants) when working across the Grace repository.

> **Primary goals for agents**
>
> - Maintain API stability.
> - Explain any changes in serialization compatibility.
> - Produce small, well-tested, and reviewable changes.
> - Preserve the repository's F# idioms and build/test expectations.
> - Create clear PRs, with tests and documentation.

---

## Repository-wide agent rules

1. **Small, single-responsibility PRs**
   - Each PR should implement one discrete change (feature, bugfix, refactor) with a clear description.
   - Limit file churn; prefer focused edits.
   - Use branch names that reflect the work, e.g. `fix/serialize-directory-event` or `feat/cli-organization-add`.

2. **Tests required for behavior changes**
   - Every change that affects behavior must include unit tests.
   - For serialization/DTO/schema changes, add round-trip serialization tests for all supported serializers (MessagePack, System.Text.Json, and any others referenced).
   - For Orleans grain changes, add unit/integration tests using the in-memory test cluster where practical.

3. **Explain any changes in serialization compatibility**
   IMPORTANT: Because Grace is still in early development, these rules don't have to be enforced right now. When Grace gets closer to production, we will begin to enforce them. Every change that would violate these rules should be thoroughly explained.
   - Never rename or reorder fields of types used for on-the-wire serialization (DUs, records) without versioning or migration.
   - If a type must change, create a new versioned type and implement a migration or compatibility layer and add tests proving compatibility.

4. **API stability & deprecation**
   - Do not remove or change public API signatures without a documented migration plan.
   - When deprecating, add a new API and provide a shim that emits a compile-time or runtime deprecation notice, plus migration documentation.

5. **Documentation & code comments**
   - Public members must have `///` XML documentation summarizing intent and usage examples where helpful.
   - Add a short “why” snippet as a comment for non-obvious implementation decisions, especially around performance, concurrency, or serialization.

6. **Formatting, linting, and commit hygiene**
   - Before creating a PR, run:
     - `dotnet build --configuration Release`
     - `dotnet test --no-build`
     - `fantomas` (or repository-preferred formatter) on changed files.
   - Use Conventional Commits for commit messages (`feat:`, `fix:`, `chore:`, `docs:`).
   - Ensure CI passes on the target branch.

7. **Error handling and results**
   - Favor `Result<'T, 'E>` or discriminated unions to represent recoverable errors in library code.
   - Convert to exceptions only at top-level boundaries (e.g., CLI/host entrypoints) where appropriate.
   - For server-level handlers and HTTP endpoints, return structured error payloads and include logging context for correlation (request id, actor id).

8. **Concurrency and async**
   - Use the `task { }` computation expression for asynchronous operations in F# (consistent across the repository).
   - Avoid implicit synchronization via mutable global state. If mutable state is required, use clear locking strategies or actor/grain isolation.

9.  **Logging and telemetry**
   - Use structured logging (message templates, named properties).
   - Include enough context for debugging (correlation id, user/actor ids, request ids). Do not log secrets or PII.

10. **Security**
    - Never include secrets, credentials, or private configuration values in code or tests.
    - Use environment variables, user secrets, or CI secret stores for credentials.
    - Add validation for any inputs that are deserialized from external sources.

---

## Agent workflow checklist (for every PR / change)

1. Create a small descriptive branch.
2. Implement the change with clear `///` docs for public APIs.
3. Add/modify unit tests and integration tests where relevant.
4. Run formatter (Fantomas) and build/test locally.
5. Push branch and open PR with:
   - Description of change
   - Tests added
   - Risk & migration notes (if applicable)
   - E2E or manual test steps (if integration-level changes)
6. Request at least one reviewer familiar with the impacted area (e.g., Orleans, serialization, CLI).

---

## Copilot / Agent-specific instructions (how to produce edits)

- Be conservative when changing public types. If uncertain whether a type is serialized or referenced across boundaries, search the repo for usages before modifying.
- When introducing a new dependency, add it to the project file and justify its addition in the PR description (size, purpose, license).
- When adding code that interacts with Orleans grains or timers, include explicit notes about activation semantics and reminder behavior.
- Prefer adding new helpers in `Grace.Shared` for cross-cutting reusable logic rather than duplicating code.

---

## Microsoft F# coding guidelines (required)

Agents **must** follow Microsoft’s published F# style guide and coding conventions. Key summary points below — this section is required to be included verbatim (or by link) in every per-project `instructions.md`.

- Use `PascalCase` for types, modules, and union cases.
- Use `camelCase` for functions and values.
- Keep functions small and expressive; prefer composing small functions rather than long imperative sequences.
- Document public APIs with `///` XML comments.
- Favor immutability and use records and discriminated unions for domain modeling.
- Use the `task { }` computation expression for asynchronous code.
- Use `Result<'T,'E>` for recoverable errors; keep exceptions for unrecoverable conditions or top-level adapters.
- Format code with `fantomas` using repository-default settings.

**Authoritative reference:** Microsoft F# style guide and conventions — https://learn.microsoft.com/dotnet/fsharp/style-guide
