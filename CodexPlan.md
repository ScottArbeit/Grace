# CodexPlan.md

## Implementation Plan

### Step 1: Baseline the contract break and inventory every current dependency

Status: Not started

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

Pending. Record the final dependency inventory, code-search evidence, and any newly discovered references here.

### Step 2: Introduce canonical external-event domain types and stable name registry

Status: Not started

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

Pending. Record the chosen type representations and any JSON serialization caveats here once implemented.

### Step 3: Replace validation trigger contracts with canonical event names

Status: Not started

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

Pending. Record the final persisted/API rule shape and validation behavior here after implementation.

### Step 4: Encode the exhaustive registry and classification rules for every covered event

Status: Not started

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

Pending. Record the final registry shape and any deviations avoided during implementation here.

### Step 5: Build the canonical event builder and targeted context-resolution layer

Status: Not started

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

Pending. Record the builder module boundaries, lookup choices, and size-enforcement implementation here.

### Step 6: Rewire the server publication pipeline to use canonical events end to end

Status: Not started

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

Pending. Record the final server flow and any retained SignalR routing decisions here.

### Step 7: Replace agent runtime event emission and remove forbidden lifecycle names

Status: Not started

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

Pending. Record the final runtime-source persistence shape and agent lifecycle payload decisions here.

### Step 8: Add replay source resolution and the admin resend surface

Status: In progress

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

Added the client-side admin resend slice: `ResendExternalEventParameters` in `Grace.Shared`,
`Admin.ExternalEvent.Resend` in `Grace.SDK`, the CLI command path
`grace admin external-event resend --event-id <eventId>`, and grouped admin help
output so the command is discoverable. Assumed a future server route of
`POST /external-event/resend` returning `string`, because the server already has
canonical `ExternalEventPublication` replay helpers but no route is wired yet.

### Step 9: Update SignalR consumers, CLI watch behavior, and any retained outward clients

Status: Not started

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

Pending. Record the final SignalR method name and CLI payload handling approach here.

### Step 10: Remove legacy automation constructs and finish coordinated test coverage

Status: Not started

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

Pending. Record deleted legacy surfaces, final validation results, and any residual follow-ups here.

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

## Surprises & discoveries

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

## Outcomes & follow-ups

- Primary outcome: implement a single authoritative canonical external-event system used for Service Bus,
  SignalR if retained, validation triggering, agent runtime lifecycle events, and resend support.
- Primary risk to manage: this is a coordinated breaking change across types, server, CLI, SDK, and tests,
  so partial migration must be avoided.
- Follow-up to capture during implementation: if any additional raw event families or helper-generated
  pseudo-events are discovered outside the current inventory, add them to the registry and exhaustiveness
  tests immediately before proceeding.
