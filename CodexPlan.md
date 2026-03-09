# CodexPlan.md

<!-- markdownlint-disable MD013 -->

## Implementation Plan

### Step 1: Baseline the contract break and inventory every current dependency

Status: Completed

Establish the implementation baseline before code movement begins so the change set stays coherent.
Confirm every remaining reference to `AutomationEventEnvelope`, `AutomationEventType`, `DataJson`,
`NotifyAutomationEvent`, and `ValidationSetRule.EventTypes`, then freeze the list of replacement touchpoints
across `Grace.Types`, `Grace.Server`, `Grace.CLI`, `Grace.SDK`, shared parameter contracts, and tests.
Use this step to capture the current raw-event surface and the exact runtime seams that need to change
together.

Includes:

- confirm the current outward path:
  raw `GraceEvent` published from `Grace.Actors/Services.Actor.fs`, consumed by
  `Grace.Server/Notification.Server.fs`, converted by `Grace.Server/Eventing.Server.fs`, then sent to
  SignalR and used for validation triggers
- confirm agent-session emission points in `Grace.Server/Startup.Server.fs`, including the forbidden
  `AgentBootstrapped` path
- confirm CLI watch SignalR consumption in `Grace.CLI/Command/Watch.CLI.fs`
- confirm validation-set API and persisted contract usage in `Grace.Types/Validation.Types.fs`,
  `Grace.Shared/Parameters/Validation.Parameters.fs`, and `Grace.Server/ValidationSet.Server.fs`
- inventory existing tests that must be replaced rather than updated in place

#### Step 1 work summary

Completed dependency inventory across `Grace.Types`, `Grace.Server`, `Grace.CLI`, `Grace.SDK`, shared parameters, and impacted tests. Confirmed the current outward path as raw `GraceEvent` publication from `Grace.Actors/Services.Actor.fs`, conversion in `Grace.Server/Eventing.Server.fs`, Service Bus and SignalR fan-out in `Grace.Server/Notification.Server.fs`, runtime automation emission in `Grace.Server/Startup.Server.fs`, and CLI watch consumption in `Grace.CLI/Command/Watch.CLI.fs`.

Confirmed the forbidden `AgentBootstrapped` emission point, the direct validation dependency on `AutomationEventType`, and the remaining test suites that must be replaced rather than carried forward unchanged.

### Step 2: Introduce canonical external-event domain types and stable name registry

Status: Completed

Create the shared type system for the new external contract in `Grace.Types`. This step defines the public
wire schema, stable canonical event names, registry metadata types, replay-source types, and JSON-friendly
payload helpers that all downstream components will consume. The goal is to make the new contract explicit
before any server logic is rewritten.

Includes:

- add `src/Grace.Types/ExternalEvents.Types.fs` for:
  `CanonicalExternalEventEnvelope`,
  canonical actor identity representation,
  canonical event-name representation,
  envelope creation helpers,
  payload serialization helpers, and
  size-check support contracts
- add `src/Grace.Types/ExternalEvents.Registry.fs` for:
  raw-event classification types,
  registry entry structure,
  canonical event-name constants or discriminated unions,
  rationale/source-resolution metadata, and
  registry enumeration helpers for exhaustiveness tests
- add `src/Grace.Types/ExternalEvents.Replay.Types.fs` for replayable runtime source records used by
  `grace.agent.work-started` and `grace.agent.work-stopped`
- decide whether canonical event names should be modeled as a DU plus string conversion helpers or as a
  constrained string module; prefer the option that makes validation-rule validation and test enumeration
  easiest
- define the normative reusable payload summary records from the spec:
  `referenceLinkSummary`,
  `linksSummary`,
  `matchedRule`,
  `rulesSummary`,
  `validationsSummary`,
  `ruleSummary`, and
  `stepsSummary`
- encode envelope rules:
  `eventVersion = 1`,
  deterministic `eventId = ${actorType}_${actorId}_${correlationId}`,
  omitted empty GUID fields,
  omitted blank actor fields, and
  `payload` as object-only JSON

#### Step 2 work summary

Added `ExternalEvents.Types.fs`, `ExternalEvents.Registry.fs`, and `ExternalEvents.Replay.Types.fs` under `Grace.Types`, and wired them into `Grace.Types.fsproj`. Chose a DU-backed canonical event-name model with deterministic string conversion helpers and authoritative published-name enumeration.

Added the canonical envelope, reusable summary-record contracts, runtime replay-source records, deterministic `eventId` generation, object-only payload serialization helpers, camelCase JSON settings, and serialized-byte measurement for the `< 1,000,000` byte rule.

### Step 3: Replace validation trigger contracts with canonical event names

Status: Completed

Break the old validation rule contract early so the rest of the stack can stop depending on
`AutomationEventType`. The core outcome is that validation-set create, update, persistence, and trigger
matching all use published canonical event names only.

Includes:

- replace `ValidationSetRule.EventTypes: AutomationEventType list` with a canonical event-name collection
  in `src/Grace.Types/Validation.Types.fs`
- update parameter and serialization surfaces in
  `src/Grace.Shared/Parameters/Validation.Parameters.fs`
- add server-side validation in `src/Grace.Server/ValidationSet.Server.fs` so create/update rejects
  unpublished or invalid canonical event names
- preserve `branchNameGlob` matching behavior while changing only the trigger identity model
- add helper validation in the new external-event registry/types so `ValidationSet.Server` can validate
  against the authoritative published event-name set instead of duplicating string lists
- update any affected SDK, CLI, or test fixtures that currently construct validation rules using
  `AutomationEventType`

#### Step 3 work summary

Replaced `ValidationSetRule.EventTypes` with `EventNames: string list` in `Grace.Types/Validation.Types.fs`, added canonical event-name validation errors in `Grace.Shared/Validation/Errors.Validation.fs`, and updated `Grace.Server/ValidationSet.Server.fs` to validate rules against `Grace.Types.ExternalEvents.Registry.isPublishedEventName`.

Persisted and API-visible validation rules now depend on canonical published event names instead of `AutomationEventType`, while preserving existing branch-glob behavior.

### Step 4: Encode the exhaustive registry and classification rules for every covered event

Status: Completed

Implement the authoritative registry that classifies every current covered raw event case exactly once, plus
the approved non-`GraceEvent` lifecycle events. This is the backbone of the entire specification and should
be completed before the builder logic is considered done.

Includes:

- encode every mapping from the spec for:
  `OwnerEventType`,
  `OrganizationEventType`,
  `RepositoryEventType`,
  `BranchEventType`,
  `ReferenceEventType`,
  `DirectoryVersionEventType`,
  `WorkItemEventType`,
  `PolicyEventType`,
  `PromotionQueueEventType`,
  `PromotionSetEventType`,
  `ValidationSetEventType`,
  `ValidationResultEventType`,
  `ReviewEventType`,
  `ArtifactEventType`,
  `grace.validation.requested`,
  `grace.agent.work-started`, and
  `grace.agent.work-stopped`
- represent `PublishCanonical`, `PublishDerived`, and `InternalOnly` explicitly
- attach required metadata per registry entry:
  canonical name,
  version,
  actor rules,
  rationale,
  required payload fields,
  overflow strategy,
  source resolution rule, and
  notes
- make the registry authoritative for both runtime mapping and tests so drift cannot happen between docs,
  code, and validation
- encode the critical edge cases directly in the registry or adjacent decision logic:
  terminal promotion reference creation,
  `PromotionSetEventType.Applied` internal-only classification,
  all `DirectoryVersionEventType` cases internal-only,
  physical deletes internal-only, and
  `WorkItemEventType.ArtifactLinked` mapping to `grace.work-item.updated`

#### Step 4 work summary

Added the authoritative registry in `Grace.Types/ExternalEvents.Registry.fs` with explicit `PublishCanonical`, `PublishDerived`, and `InternalOnly` classification for every covered raw event case plus approved runtime lifecycle events.

Encoded the critical edge cases in registry metadata: terminal `ReferenceEventType.Created` as the sole authoritative source for `grace.promotion-set.applied`, `PromotionSetEventType.Applied` as internal-only, all `DirectoryVersionEventType` cases as internal-only, physical deletes as internal-only, `grace.validation.requested` as derived, and `WorkItemEventType.ArtifactLinked` mapped to `grace.work-item.updated`.

### Step 5: Build the canonical event builder and targeted context-resolution layer

Status: Completed

Replace the current thin envelope mapper in `Grace.Server/Eventing.Server.fs` with a canonical builder that
produces the public envelope plus structured payload objects. The builder must stay deterministic, avoid
serialize-parse loops, prefer raw event data first, and only perform targeted authoritative lookups when
required fields cannot be derived otherwise.

Includes:

- add `src/Grace.Server/ExternalEvents.Server.fs` or equivalent to host:
  canonical build entry points,
  actor identity resolution,
  payload builders per event family,
  size measurement,
  failure classification, and
  source-resolution helpers
- keep `Grace.Server/Eventing.Server.fs` only if it still adds value; otherwise retire or reduce it to a
  thin compatibility-free facade over the new builder
- implement payload builders for each published event family with compact `changed` objects for update
  events rather than raw DTO dumps
- implement conditional reference mapping so a single `ReferenceEventType.Created` emits exactly one of
  `grace.reference.created` or `grace.promotion-set.applied`
- implement compact overflow behavior for `stepsSummary`, preserving counts and ordered prefix entries while
  setting `truncated = true`
- implement lookup helpers for cases where repository, branch, promotion-set, or work-item resolution is
  required but not fully present on the raw event
- fail closed when required actor identity or required payload fields cannot be resolved
- measure final serialized UTF-8 bytes for the full canonical document and reject anything at or above
  `1_000_000`

#### Step 5 work summary

Completed the canonical builder in `src/Grace.Server/ExternalEvents.Server.fs` with explicit builders for each published event family plus fail-closed source resolution helpers. The builder now produces deterministic `CanonicalExternalEventEnvelope` documents, emits compact `changed` payloads for update events, conditionally maps terminal `ReferenceEventType.Created` to `grace.promotion-set.applied`, keeps `PromotionSetEventType.Applied` internal-only, and leaves `DirectoryVersion` events internal-only.

Implemented strict full-document UTF-8 size enforcement at `< 1_000_000` bytes, including compact overflow handling for `stepsSummary` so oversize `grace.promotion-set.steps-updated` payloads truncate safely instead of publishing invalid documents. Required actor identity, repository, branch, promotion-set, review, and work-item context now resolve through targeted lookups and fail closed when the canonical document cannot be built safely.

### Step 6: Rewire the server publication pipeline to use canonical events end to end

Status: Completed

Move the outward-facing server pipeline to the new canonical contract while keeping raw `GraceEvent`
publication untouched. The raw-event subscriber must continue consuming internal bus messages, but its public
outputs must become canonical Service Bus publications, canonical SignalR notifications if retained, and
canonical validation triggers.

Includes:

- update `src/Grace.Server/Notification.Server.fs` to:
  consume raw `GraceEvent`,
  build canonical external events through the new builder,
  publish canonical documents to Azure Service Bus,
  optionally fan them out to SignalR using the same envelope,
  remove recompute-success cloning, and
  emit validation-requested events only from successfully built canonical source events
- add Service Bus publisher helpers that send the canonical JSON body with:
  `MessageId = eventId`,
  `CorrelationId = correlationId`,
  `Subject = eventName`, and
  application properties for event metadata
- keep the existing internal raw `GraceEvent` publication in `src/Grace.Actors/Services.Actor.fs` unchanged
- update logging so build failures, oversize rejections, resend attempts, and publish failures include
  structured correlation context without logging sensitive payload content
- ensure all publication failures fail closed and continue the subscriber pipeline
- decide whether `routeAutomationEvent` and duplicated SignalR helpers should be renamed or collapsed into a
  canonical-event routing module to avoid split logic

#### Step 6 work summary

Notification.Server.fs now consumes raw `GraceEvent` messages, builds canonical external events through `ExternalEvents.Server.fs`, publishes canonical JSON through `ExternalEventPublication.publishCanonicalEvent`, and fans out the same canonical envelope over retained SignalR notifications. Validation triggering now keys off canonical published event names rather than `AutomationEventType`, synthetic recompute-success duplication has been removed, and invalid or oversize canonical documents fail closed without stopping the subscriber pipeline.

Completed the outward publication cutover without changing raw internal `graceeventstream` publication semantics in `Grace.Actors/Services.Actor.fs`. Also added the missing authorization manifest entry for `POST /external-event/resend`, so the canonical resend surface participates in the same secured publication flow as the rest of the admin path.

### Step 7: Replace agent runtime event emission and remove forbidden lifecycle names

Status: Completed

Update the agent-session HTTP flow so runtime lifecycle events use the canonical envelope and the spec's
payloads, while explicitly eliminating `grace.agent.bootstrapped`.

Includes:

- replace `AutomationEventType.AgentWorkStarted`, `AutomationEventType.AgentWorkStopped`, and
  `AutomationEventType.AgentBootstrapped` usage in `src/Grace.Server/Startup.Server.fs`
- emit only `grace.agent.work-started` and `grace.agent.work-stopped`
- persist replayable runtime source records before outward publication for these approved non-`GraceEvent`
  lifecycle events
- map work-item locator data according to the spec:
  include `workItemId` only when confidently resolved,
  otherwise carry `workItemLocator`
- preserve idempotent replay information and operation IDs in the canonical payload
- remove any tests or code paths that treat bootstrap as a runtime external event

#### Step 7 work summary

Startup.Server.fs now emits only canonical grace.agent.work-started and grace.agent.work-stopped runtime records, builds canonical envelopes through ExternalEventPublication.buildRuntimeEnvelope, remembers replayable runtime sources before publication, and routes the same canonical envelope through Service Bus and SignalR. AgentBootstrapped has been removed from the runtime emission path; the only remaining AgentBootstrapped reference is the legacy type definition in Automation.Types.fs, which is slated for deletion in Step 10.

### Step 8: Add replay source resolution and the admin resend surface

Status: Completed

Implement replay/resend for canonical events by event ID. This step spans type support, server source
resolution, API surface, SDK plumbing, and CLI admin command wiring.

Includes:

- design source resolution for raw-event-derived canonical events using actor type, actor ID, and
  correlation ID
- add storage/query helpers to find the authoritative source event without reusing stale serialized public
  JSON
- add server handlers and routing for canonical resend operations, likely in
  `src/Grace.Server/Startup.Server.fs` plus a focused module such as
  `src/Grace.Server/ExternalEvents.Server.fs`
- add parameter contracts in `Grace.Shared.Parameters` if needed
- add SDK methods in `src/Grace.SDK/Admin.SDK.fs`
- add a new CLI path under `src/Grace.CLI/Command/Admin.CLI.fs` for:
  `grace admin external-event resend --event-id <eventId>`
- update `src/Grace.CLI/Program.CLI.fs` help-group wiring if new admin subcommands need explicit grouping
- make resend fail clearly when the source is missing, not replayable, or now classified as `InternalOnly`

#### Step 8 work summary

Completed replay and resend support by canonical `eventId` across `ExternalEventPublication.Server.fs`, `Startup.Server.fs`, `Grace.Shared`, `Grace.SDK`, and `Grace.CLI`. The server now exposes `POST /external-event/resend`, rebuilds from remembered replay sources by canonical `eventId`, republishes canonical JSON with the same outward contract, and re-routes the resent envelope through retained SignalR notifications.

The CLI admin surface now supports `grace admin external-event resend --event-id <eventId>`, and resend fails clearly when the replay source is missing, not replayable, or no longer classifies as a published canonical event.

### Step 9: Update SignalR consumers, CLI watch behavior, and any retained outward clients

Status: Completed

Migrate remaining consumers from automation envelopes to the canonical external contract. The main visible
consumer in-tree is CLI watch, but SignalR interfaces and any shared DTOs must also align.

Includes:

- replace `NotifyAutomationEvent: AutomationEventEnvelope -> Task` with a canonical event contract in
  `src/Grace.Server/Notification.Server.fs`
- update `src/Grace.CLI/Command/Watch.CLI.fs` to subscribe to canonical events and branch on canonical
  `eventName` values instead of `AutomationEventType`
- replace `DataJson` parsing with direct payload inspection through the new payload object structure
- preserve the existing watch behavior that reacts to terminal promotion application and triggers auto-rebase
- update any CLI or test utilities that still import `Grace.Types.Automation`
- assess whether any other in-tree clients need canonical contract updates beyond watch and admin flows

#### Step 9 work summary

`Notification.Server.fs` now exposes `NotifyExternalEvent : CanonicalExternalEventEnvelope -> Task`, and `Watch.CLI.fs` now subscribes to canonical external events, branches on canonical `eventName` values, and reads `targetBranchId` / `terminalPromotionReferenceId` directly from the canonical payload object. This preserves the existing parent-branch auto-rebase behavior without any `DataJson` reparse path.

Updated retained in-tree consumers and helper surfaces so outward clients now depend on the canonical contract rather than `AutomationEventEnvelope` or `AutomationEventType`.

### Step 10: Remove legacy automation constructs and finish coordinated test coverage

Status: Completed

Once the new pipeline is wired, remove the old outward contract completely and replace the current tests
with coverage that proves the spec's invariants. This step is where the breaking change becomes complete and
enforced.

Includes:

- delete or fully repurpose `src/Grace.Types/Automation.Types.fs` so `AutomationEventEnvelope`,
  `AutomationEventType`, and `DataJson` no longer exist in the source tree
- update or replace tests in:
  `src/Grace.Types.Tests/Automation.Types.Tests.fs`,
  `src/Grace.Server.Tests/Eventing.Server.Tests.fs`,
  `src/Grace.Server.Tests/Notification.Server.Tests.fs`,
  `src/Grace.CLI.Tests/Watch.Tests.fs`, and
  any additional impacted SDK or server tests
- add the required coverage from the spec:
  registry exhaustiveness,
  canonical serialization,
  deterministic `eventId`,
  size-limit enforcement,
  targeted mapping tests,
  validation-requested behavior,
  resend behavior,
  service-bus publish failure handling,
  unknown SignalR event-name tolerance at the consumer edge, and
  negative proof that `grace.agent.bootstrapped` is not published
- critique and extend the spec's suggested test list before implementation, with emphasis on:
  queue and promotion-set context lookups,
  payload redaction and security checks,
  logical-delete versus physical-delete behavior,
  actor identity omission rules, and
  property-based registry invariants
- run validation commands:
  `pwsh ./scripts/validate.ps1 -Fast`,
  `dotnet build --configuration Release`,
  `dotnet test --configuration Release --no-build`, and
  `dotnet tool run fantomas --recurse .`

#### Step 10 work summary

Removed the legacy outward automation constructs as live source: `src/Grace.Types/Automation.Types.fs` is now a retired stub module, `AutomationEventEnvelope`, `AutomationEventType`, and `DataJson` no longer exist in the source tree, and the remaining legacy-name assertions are explicit negative tests proving that `grace.agent.bootstrapped` is not published. Added and updated coverage across canonical envelope creation, registry enumeration, builder behavior, validation-name enforcement, resend authorization, and SignalR/watch consumption.

Final validation completed with `pwsh ./scripts/validate.ps1 -Fast`, `dotnet build --configuration Release`, `dotnet test --configuration Release --no-build`, and `dotnet tool run fantomas --recurse .`. The validate script still reports format as skipped by design, so Fantomas was run separately to satisfy the required formatting pass.

## Decision Log

- 2026-03-09 03:33:17 PDT: Treat this as a full breaking-contract replacement, not a compatibility layer,
  because the spec explicitly forbids preserving `AutomationEventEnvelope`, `AutomationEventType`, and
  `DataJson`.
- 2026-03-09 03:33:17 PDT: Sequence the work so type-level canonical contracts and registry decisions land
  before server rewiring; that minimizes drift and gives the validation, CLI, and server layers a single
  source of truth.
- 2026-03-09 03:33:17 PDT: Keep raw internal `graceeventstream` publication intact in
  `Grace.Actors/Services.Actor.fs`; the new canonical Service Bus publication belongs in the server-side
  outward pipeline, not in the internal event-sourcing publisher.
- 2026-03-09 03:33:17 PDT: Plan replay and resend as a first-class feature with explicit runtime source
  records for agent lifecycle events instead of trying to infer them from transient in-memory state.

- 2026-03-09 12:33:15 PDT: Wire the admin resend SDK and CLI against
  `POST /external-event/resend` rather than `/admin/...`, because reminder
  administration is already grouped under the CLI `admin` command while server
  HTTP resources are routed by top-level resource name and
  `ExternalEventPublication.Server.fs` already establishes `ExternalEvent` as the
  canonical resource concept.

- 2026-03-09 12:44:00 PDT: `apply_patch` continues to fail for large file rewrites in this session with exit code 1 and no patch diagnostics.
  File edits may need to fall back to PowerShell write paths or subagent-owned changes to keep progress moving safely.
- 2026-03-09 12:44:00 PDT: The admin resend client surface is already implemented and validated in-tree
  (`Grace.Shared`, `Grace.SDK`, `Grace.CLI`, and `Program.CLI`), but the server route and resend execution path were still unverified at that point, so Step 8 remained only partially complete.
- 2026-03-09 12:57:00 PDT: Keep Step 8 marked `In progress` even though `POST /external-event/resend` is now wired in `Startup.Server.fs`.
  Resend exhaustiveness still depends on finishing the raw-event canonical builder and its replay coverage.
- 2026-03-09 12:57:00 PDT: Treat Step 7 as complete before Step 10 because the runtime emission path no longer publishes `AgentBootstrapped`.
  The remaining `Automation.Types.fs` definition is legacy dead surface scheduled for removal, not active behavior.
- 2026-03-09 12:44:00 PDT: The canonical builder/publication cutover is currently split across partially landed files.
  `ExternalEvents.Server.fs` only handles `OwnerEvent` today, while `Notification.Server.fs` already has partially renamed canonical hub methods. That mixed state is the main integration risk and the current critical path.

- 2026-03-09 14:05:42 PDT: Infer `AzureServiceBus` in `ApplicationContext.Server.fs` when Service Bus configuration is present even if `grace__pubsub__system` is unset, because the previous `value.Equals(...)` path null-dereferenced during server startup and blocked the entire subscriber host.
- 2026-03-09 14:05:42 PDT: Fall back to `NullLoggerFactory.Instance` when `ApplicationContext.loggerFactory` is still null during `Startup.Server.fs` construction so canonical runtime event routing can initialize without depending on later host wiring.
- 2026-03-09 14:05:42 PDT: Add `POST /external-event/resend` to `EndpointAuthorizationManifest.Server.fs` rather than weakening tests or bypassing manifest coverage; the resend surface is a real secured outward endpoint and needs first-class authorization metadata.
- 2026-03-09 14:05:42 PDT: Treat the one-time `ConcurrentCreatesProduceUniqueNumbersWithoutCollisions` failure as test flakiness, not a canonical-event regression, because the isolated rerun passed and the subsequent full solution `dotnet test --configuration Release --no-build` run passed cleanly.`r`n`r`n## Surprises & discoveries

- The current outward event layer is narrower than the spec assumes:
  `Grace.Server/Eventing.Server.fs` only maps a subset of raw events today, and several top-level families
  such as owner, organization, repository, branch, and policy currently return `None`.
- Validation triggering is coupled directly to the old contract:
  `Grace.Types/Validation.Types.fs` stores `ValidationSetRule.EventTypes: AutomationEventType list`, and
  `Grace.Server/Notification.Server.fs` matches those values directly.
- The current notification pipeline still emits a synthetic recompute-success duplicate:
  `Grace.Server/Notification.Server.fs` clones `PromotionSetStepsUpdated` into
  `PromotionSetRecomputeSucceeded`, which the new spec explicitly forbids.
- Agent bootstrap is currently emitted as a runtime outward event:
  `Grace.Server/Startup.Server.fs` calls `emitAgentSessionEvent` with `AutomationEventType.AgentBootstrapped`.
- CLI watch currently depends on the old transport shape:
  `Grace.CLI/Command/Watch.CLI.fs` subscribes to `AutomationEventEnvelope`, checks `AutomationEventType`,
  and reparses `DataJson`.
- The existing admin command surface has a natural extension point:
  `Grace.CLI/Command/Admin.CLI.fs` already hosts reminder administration, and `Grace.SDK/Admin.SDK.fs`
  already exists for matching admin transport methods.

- Canonical replay support is already partially implemented server-side:
  `Grace.Server/ExternalEventPublication.Server.fs` maintains an in-memory
  `replaySources` dictionary, exposes `tryGetReplaySource`, and can
  `rebuildForReplay eventId`, but `Grace.Server/Startup.Server.fs` does not yet
  expose an `/external-event/resend` endpoint.

- Server startup had a hidden null-configuration trap unrelated to the canonical event spec: a manual `dotnet run --project src\Grace.Server\Grace.Server.fsproj --configuration Release --no-build` failed because `configurePubSubSettings ()` called `value.Equals(...)` when `grace__pubsub__system` was unset, which blocked `Grace.Server.Tests` until `ApplicationContext.Server.fs` was made null-safe.
- `Startup.Server.fs` assumed `ApplicationContext.loggerFactory` was already populated during startup construction. Server-test host initialization exposed that this can still be null, so canonical runtime event logging now uses `NullLoggerFactory.Instance` as a safe fallback.
- The resend route was functionally implemented before authorization metadata was complete: the first full solution test run failed `EndpointAuthorizationManifest` coverage until `POST /external-event/resend` was added to `src/Grace.Server/Security/EndpointAuthorizationManifest.Server.fs`.
- `dotnet tool run fantomas --recurse .` initially failed because a leftover scratch file, `src/Grace.Server/ExternalEvents.Server.rebuilt.fs`, was still present and unparsable. Removing that transient rebuild artifact and rerunning Fantomas produced a clean formatting pass.
- One concurrent work-item integration test showed transient instability during the final cycle: `ConcurrentCreatesProduceUniqueNumbersWithoutCollisions` returned HTTP 400 once in a full-server run, then passed immediately in isolation and the following full solution rerun passed. The evidence points to existing test flakiness rather than a repeatable canonical external-event defect.`r`n`r`n## Outcomes & follow-ups

- Primary outcome: implement a single authoritative canonical external-event system used for Service Bus,
  SignalR if retained, validation triggering, agent runtime lifecycle events, and resend support.
- Primary risk to manage: this is a coordinated breaking change across types, server, CLI, SDK, and tests,
  so partial migration must be avoided.
- Follow-up to capture during implementation: if any additional raw event families or helper-generated
  pseudo-events are discovered outside the current inventory, add them to the registry and exhaustiveness
  tests immediately before proceeding.
