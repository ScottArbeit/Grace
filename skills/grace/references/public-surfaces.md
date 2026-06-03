# Public Surfaces

Load this reference for HTTP routes, server handlers, route metadata, endpoint authorization, SDK calls, CLI commands,
option parsing, user-visible output, and API docs.

## HTTP API

Key files:

- `src/Grace.Server/Startup.Server.fs`
- `src/Grace.Server/Security/EndpointAuthorizationManifest.Server.fs`
- `src/Grace.Authorization.Tests/EndpointAuthorizationManifest.Tests.fs`
- Domain server modules such as `WorkItem.Server.fs`, `Review.Server.fs`, `Queue.Server.fs`,
  `PromotionSet.Server.fs`, `Approval*.fs`, and `Webhook*.fs`.

Rules:

- Keep handler, parser, validation, route metadata, authorization, correlation ID, and logging changes together.
- Update `EndpointAuthorizationManifest.Server.fs` whenever route shape or authorization changes.
- Keep endpoint manifest tests green; they check route coverage, extra entries, and duplicates.
- For ID-based route/query surfaces, authorize the stored object after loading it. List surfaces must filter before
  returning collections. Do not rely only on route-scope validation when the stored object owns the authoritative scope.
- Keep errors precise and user-safe; do not leak secrets, tokens, or credential-bearing URLs.

## SDK

Key files:

- `src/Grace.SDK/*.SDK.fs`
- `src/Grace.SDK/ClientIdentity.SDK.fs`

Rules:

- Keep SDK methods thin and aligned with server route names and DTOs.
- Prefer additive APIs or versioned alternatives over breaking public methods.
- Configure client identity headers for non-CLI clients; CLI sets `ClientType.CLI(<file version>)`.
- Manifest-backed upload is default-on for eligible files. Whole-file fallback is for ineligible files or recoverable
  manifest-upload failures; eligible files should not upload twice.

## CLI

Key files:

- `src/Grace.CLI/Program.CLI.fs`
- `src/Grace.CLI/Command/*.CLI.fs`
- `src/Grace.CLI.Tests/*.CLI.Tests.fs`

Rules:

- Keep command parsing thin. Move complex behavior to services/helpers.
- Prefer named options; preserve existing option names and switches.
- Update root and selected subcommand help grouping in `Program.CLI.fs` when adding or renaming commands.
- Use Spectre.Console for presentation, but keep core logic testable.
- `GRACE_TOKEN` accepts Grace PATs only; Auth0 access tokens are not valid there.
- Local token file storage is disabled; do not revive it without an explicit product decision.

Current high-signal command groups:

- `grace auth` for login, status, whoami, PAT create/list/revoke/status.
- `grace access` for role assignments and path permissions.
- `grace workitem` with aliases `work`, `work-item`, and `wi`.
- `grace review` for candidate review operations and review report output.
- `grace candidate` for candidate-first reviewer projections.
- `grace queue` for status, enqueue, pause, resume, and dequeue.
- `grace promotion-set` for promotion set lifecycle.
- `grace webhook` for webhook rule management and webhook delivery inspection.
- `grace approval policy` for approval policy management.
- `grace approval request` for workflow-generated approval request inspection and approve/reject/wait/history.

Do not add public `hook`, `subscription`, `notification rule`, or `approval request create` commands unless a current
product decision explicitly reopens those boundaries.

## Static OpenAPI

Static OpenAPI source lives under `src/OpenAPI`. Keep it aligned when adding or changing public HTTP routes, including
the webhook and approval route families. Internal workflow/test seams such as `/approval/request/_seedGenerated` should
stay out of public OpenAPI unless they become supported public API.

## Work Item, Review, Queue, And Promotion Flow

The newer workflow model spans:

- `src/Grace.Types/WorkItem.Types.fs`
- `src/Grace.Types/Review.Types.fs`
- `src/Grace.Types/Queue.Types.fs`
- `src/Grace.Types/Policy.Types.fs`
- `src/Grace.Types/PromotionSet.Types.fs`
- `src/Grace.Server/WorkItem.Server.fs`
- `src/Grace.Server/Review.Server.fs`
- `src/Grace.Server/Queue.Server.fs`
- `src/Grace.Server/Policy.Server.fs`
- `src/Grace.Server/PromotionSet.Server.fs`

When changing one of these, keep policy snapshots, candidate identity, review notes, queue state, promotion-set state,
work-item links, and CLI/report output aligned.

## Validation

- Server route changes: focused `Grace.Server.Tests` or `Grace.Authorization.Tests`.
- CLI changes: parsing/handler tests in `Grace.CLI.Tests` and manual command checks when practical.
- SDK changes: SDK consumer tests or server contract tests.
- Public behavior changes: update README, docs, or nearby `AGENTS.md` if future agents/users need the new behavior.
