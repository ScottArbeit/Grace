# Grace specification profile

Load this profile with the installed `specification` skill whenever creating, updating, or auditing a non-trivial Grace
specification.

This file extends the portable specification contract with Grace-specific defaults, evidence sources, contract surfaces,
and readiness traps. It does not duplicate the portable lifecycle states, quality-contract definitions, full
specification structure, issue mechanics, validation commands, or review workflow.

## Authority

Use this profile after:

1. the current user request
2. root and nearest applicable `AGENTS.md`
3. `docs/Development process.md`, repository templates, and review guidance
4. the active `dev-process` quality contract
5. the portable `specification` skill and contract

Repository-local guidance owns tracked implementation mechanics. The portable `specification` skill owns the lifecycle
and Plan-ready audit. This profile adds Grace-specific criteria to that audit.

## Grace default quality posture

Grace defaults to **Product V1** unless the user, specification, or issue explicitly chooses Discovery, Personal
reliable, or Hardened.

Grace is not in production unless explicitly stated. Existing data is normally development data and may be reset. This
permits the simplest correct current serialized or persisted shape when a production migration contract has not been
promised.

Development-data reset does not waive:

- correct current serialization and deserialization
- actor or storage restart behavior included by the capability
- event replay and projection behavior included by the capability
- public and generated contract correctness
- truthful failure and status behavior
- data integrity and authorization

Do not build migration or compatibility machinery for imaginary production data. Do state any required local reset,
fixture regeneration, or test-data replacement.

## Evidence profile

A non-trivial Grace specification should inspect only relevant evidence, but must normally include:

- root and nearest applicable `AGENTS.md`
- `docs/Development process.md` for plan and implementation handoff assumptions
- relevant declarations before implementations
- current implementations for behavior, side effects, ordering, and persistence
- existing tests and validation seams
- affected static and generated contracts
- affected docs and examples

Use concrete anchors such as `path:line`, `path:line-line`, or `path` plus symbol when line numbers are unavailable.
Label inference and state how to verify it.

When using an exported source bundle, state which snapshot it represents. Do not treat a `main` bundle as evidence for
an active pull-request branch. Inspect GitHub or the branch directly when the request concerns current issue, PR, review,
or branch behavior.

## Grace domain language

Prefer existing Grace nouns and identities. Verify current usage before introducing nearby alternatives.

Common public and domain concepts include:

- Repository, Branch, Reference, Promotion, Commit, Checkpoint, Save, and Tag
- DirectoryVersion and FileVersion
- Sha256Hash and BLAKE3-related identities
- work items, promotion sets, queues, gates, policies, attestations, and review reports
- webhooks, approval policies, and approval requests
- UploadSessions, FileManifests, ContentBlocks, and ManifestContributionWorkflows
- materialization plans, projection artifacts, caches, grants, and execution modes

A new domain noun, durable state, state machine, identity role, authority, or public lifecycle requires explicit owner
decision closure. Do not let an implementing or reviewing agent create one because it makes a local fix convenient.

## Grace propagation surface inventory

For every applicable row, give a disposition: updated; unchanged with reason; waived with reason; deferred to named
work; or not applicable with reason.

### Shared domain and contracts

- `Grace.Types` DTOs, parameters, discriminated unions, enums, identifiers, and serialization
- `Grace.Shared` commands, domain events, validators, envelopes, hashing, and shared helpers
- persisted actor or storage state and event names
- canonical registries, manifests, routing tables, and executable metadata

### Public behavior

- Giraffe HTTP routes, handlers, authorization, status codes, and error envelopes
- CLI command names, flags, defaults, human output, `--output json`, and machine projection
- SDK facades and public client behavior
- static OpenAPI and generated clients
- webhook, approval, SignalR, Watch, search, and projection behavior

### Runtime, storage, and operations

- Orleans actors, reminders, durable transitions, idempotency, and replay
- Cosmos DB, Blob Storage, Service Bus, Redis, SQLite, filesystem, or cache state
- hosted services, Aspire, Docker, runtime configuration, deployment scripts, and Azure resources
- observability, structured logging, status, health, diagnostics, and operator guidance

### Proof and documentation

- unit, property, authorization, actor, server integration, CLI, SDK, generated-contract, and Aspire tests
- test vectors and fixtures
- README, contributor docs, command help, examples, architecture docs, and relevant `AGENTS.md`
- freshness or generation workflows

An accepted flag, field, event, mode, route, or configuration value must be implemented, rejected, or intentionally
informational. Do not leave half-active Grace contracts.

## Authority and stale-identity profile

For runtime, storage, materialization, Watch, auth, actor, eventing, and cache work, the specification should state as
applicable:

- authoritative source
- complete identity tuple
- current versus stale versus duplicate versus conflicting state
- point of revalidation before side effects
- durability and publication ordering
- side-effect authority
- behavior when authority changes during the workflow

Typical identity dimensions may include repository, branch, reference, root DirectoryVersion, artifact descriptor,
cache registration, grant, account, owner, correlation, or operation identity. Do not assume that matching one GUID or
hash proves current authority.

When later slices consume a fact, trust predicate, persisted field, status flag, or authority signal produced by the
current slice, the current slice owns enough reliability and proof for that downstream use.

## State, time, and distributed outcome profile

Explicit state/time/outcome modeling is required when a Grace capability includes:

- Orleans durable transitions or reminders
- timers, leases, expiry, or background refresh
- retries or idempotency
- multiple authorities or stores
- server-to-client, actor-to-storage, or cache-to-server coordination
- destructive mutation or materialization commit
- unknown network outcomes
- cleanup that can fail after durable truth changes

Specify authoritative clocks, terminal truth, restart/replay, stale-work rejection, and cleanup ordering. Persist truthful
terminal or recovery state before best-effort cleanup when cleanup failure must not hide the real outcome.

Product V1 may defer optional rotation, reconciliation, prefetch, broad recovery, or multi-platform custody. It may not
include those capabilities while waiving their intrinsic correctness.

## Security and privacy profile

Apply the active quality contract and relevant Grace guidance. At minimum, specifications touching auth or public
surfaces should state:

- actor and caller authorization
- repository/path/resource scope
- no-oracle behavior when required
- credential, token, PAT, certificate, private-key, and secret handling
- PII and user-content logging boundaries
- cross-repository, cross-branch, cross-owner, and stale-grant negatives

Do not automatically import every Hardened adversary into Product V1. Do not weaken authorization, integrity, or secret
handling because Grace data is developmental.

## Proof profile

Prefer proof at the highest stable public or behavioral seam.

As applicable, require:

- RED evidence before implementation
- positive, negative, regression, and boundary proof
- false-positive-resistant tests for authority, freshness, and integrity
- explicit JSON and public wire-shape proof
- actor restart, replay, ordering, or concurrency proof when selected capabilities create those states
- cross-project or integration proof when a contract crosses services
- current static/generated OpenAPI or SDK proof
- live or captured witnesses for external platform behavior
- documentation or help-output proof for user-facing workflow changes

Repository guidance owns exact validation profiles and commands. The specification names the expected profile and proof
seams without embedding command choreography that can drift.

## Grace Plan-ready additions

In addition to the portable Plan-ready audit, a Grace specification is not Plan-ready until applicable items are
explicit:

- current code, test, contract, and documentation evidence
- Grace terminology and complete identity tuples
- affected project and contract surfaces
- accepted/rejected/informational inputs and modes
- actor, storage, event, API, CLI, SDK, OpenAPI/generated, Watch, webhook, SignalR, search, or materialization propagation
- current versus stale authority and revalidation
- persistence, replay, restart, ordering, partial-failure, and cleanup semantics created by included capabilities
- development-data reset or compatibility posture
- proof seams and repository validation profile
- an early value-bearing tracer before optional automation or hardening
- owner-interruption triggers for new domain constructs, state machines, authorities, public lifecycle, or quality changes

## Grace planning handoff

The Specification Handoff Packet to `dev-process` should call out:

- candidate Grace projects and likely owned paths
- shared contract and generated-artifact touchpoints
- high-conflict files or registries that constrain parallelism
- epic integration considerations when multiple slices are required
- requirement IDs and capability dispositions inherited by each candidate slice
- final integration and completion-audit obligations
- known review-churn families from relevant prior work when evidence exists

Do not create issues from this profile. `dev-process` and repository guidance own implementation-plan and issue
publication mechanics.
