# SDK and OpenAPI architecture specification

## Summary and full description

Grace will treat OpenAPI as the authoritative HTTP contract for Grace Server while exposing stable,
language-native SDK facades as the supported public client surface. Generated clients are still important:
they must prove that the OpenAPI contract is sufficiently precise to drive raw endpoint clients in .NET,
TypeScript Node, Python, Rust, and future languages. They are not, however, the primary public SDK contract.

The architecture has four durable pillars:

- OpenAPI owns the HTTP wire contract.
- Handwritten SDK facades own the public SDK API and compatibility promise.
- Grace Protocol documentation and golden vectors own portable local protocol semantics.
- Generated raw clients are deterministic, validated implementation artifacts that sit behind the facades.

This specification defines the intended SDK layering, support maturity ladder, API and package versioning
rules, client lifecycle signaling, generator prototype matrix, monorepo layout, proof strategy, and completion
gates. It is a design and proof specification, not a GitHub issue, implementation branch, or pull request.
Tracked implementation must still follow the repo's issue-owned workflow.

## Intent and scope

The intent is to make Grace's OpenAPI and SDK story best-in-class rather than merely route-complete. The
OpenAPI contract should be strong enough to generate useful raw clients across languages, support precise
wire compatibility testing, document Grace-specific transport policy, and expose enough examples and schema
shape information for humans and generators to understand the API without reading server code.

The SDK story should let users write idiomatic code in their language while keeping Grace-native behavior
correct. The supported SDK surface is the handwritten facade. The generated raw client is an internal
implementation dependency and diagnostic escape hatch unless a future decision explicitly publishes it with
its own compatibility promise.

The scope includes:

- Canonical and derived OpenAPI artifacts.
- Generated raw clients and handwritten facades.
- SDK support maturity tiers from raw generated clients through advanced local integration.
- Versioning for SDK packages, HTTP API contracts, and protocol vector suites.
- Client identity, correlation, auth, and SDK lifecycle signaling headers.
- Grace Protocol documentation and golden vector strategy.
- TypeScript Node as the first non-.NET SDK target.
- Python and Rust as early follow-up/prototype targets.
- Generator evaluation and acceptance gates.
- Testing, validation, and implementation handoff requirements.

## User Stories

1. As a Grace API consumer, I want the OpenAPI document to be the authoritative HTTP contract, so that I can
   understand request and response behavior without inspecting server code.
1. As a .NET SDK user, I want a stable public facade, so that generated-client churn does not break my code.
1. As a TypeScript Node user, I want an idiomatic `GraceClient`, so that I can use Grace from Node without
   learning F# or .NET internals.
1. As a Python user, I want the Python SDK to expose Grace concepts naturally, so that I can automate Grace
   workflows from scripts and services.
1. As a Rust user, I want Rust generator feasibility evaluated early, so that Rust support is not designed as
   an afterthought.
1. As an SDK maintainer, I want generated clients to live behind facades, so that I can regenerate or switch
   generators without creating accidental public API breaks.
1. As an API maintainer, I want operation IDs to be stable and language-friendly, so that generated clients do
   not produce vague or colliding method names.
1. As an SDK maintainer, I want every operation grouped by a primary SDK tag, so that generated raw clients are
   navigable and facade work has a clear grouping model.
1. As a contract reviewer, I want every response shape and media type documented, so that client behavior is
   driven by the contract rather than guesswork.
1. As a user uploading small files, I want simple server-mediated file transfer available early, so that I can
   adopt Grace before full local manifest protocol support is complete.
1. As a user uploading large files, I want Tier 4 SDKs to implement Grace-native local behavior, so that large
   file uploads get chunking, dedupe, cache, progress, and resumability benefits.
1. As a protocol implementer, I want golden vectors for addresses, ContentBlock bytes, FileManifest
   reconstruction, and corruption rejection, so that language SDKs can prove protocol compatibility.
1. As a server maintainer, I want SDK packages to send client identity headers, so that Grace can diagnose and
   manage SDK compatibility in production-like environments.
1. As an SDK user, I want the server to warn me before my SDK version stops being supported, so that I can
   update on my own schedule.
1. As an SDK user, I want unsupported-client failures to be clear and graceful, so that I know to update Grace
   Client instead of debugging a vague HTTP failure.
1. As a CLI user, I want lifecycle warnings to be visible but not noisy, so that I notice required updates
   without having every command flooded with repeated messages.
1. As a library user, I want lifecycle warnings exposed through callbacks or diagnostics, so that my application
   can decide how to surface update guidance.
1. As an API client maintainer, I want SDK package versions and HTTP API versions separated, so that upgrading
   a package is not confused with opting into a new wire contract.
1. As an SDK maintainer, I want SDK packages to default to a pinned API contract version, so that released
   clients do not silently track `latest` or `edge`.
1. As a developer testing new server behavior, I want an explicit override for API version, so that I can opt
   into preview, `latest`, or `edge` behavior deliberately.
1. As a release manager, I want each SDK package to publish a compatibility matrix, so that users know the
   supported server API versions and protocol vector suites.
1. As an OpenAPI maintainer, I want canonical 3.2 source and scripted compatibility projections, so that Grace
   can use current standards while still evaluating generators that lag behind.
1. As a docs reader, I want examples for success, failure, and raw media responses, so that I can understand
   realistic use without reverse engineering tests.
1. As a security reviewer, I want auth, client identity, correlation, and lifecycle headers modeled as reusable
   OpenAPI components, so that transport policy is visible and reviewable.
1. As a compatibility reviewer, I want additive versus breaking contract rules documented, so that changes are
   classified consistently.
1. As a generator evaluator, I want deterministic regeneration and compile/import gates, so that generator
   selection is based on evidence rather than taste.
1. As an implementation agent, I want likely seams and proof seams named, so that each issue can be sliced with
   clear owned paths and validation.
1. As a review-only agent, I want prior review and proof expectations captured, so that I can audit gaps rather
   than repeat settled design decisions.
1. As an operator, I want unsupported-client policy to avoid leaking sensitive deployment details, so that update
   guidance is useful without exposing internals.
1. As a future browser SDK designer, I want browser TypeScript explicitly out of scope for the first milestone,
   so that Node filesystem and auth assumptions do not accidentally become browser promises.
1. As a Grace maintainer, I want SDK tiers to be a fast maturity ladder, so that Tier 0 through Tier 3 checkpoints
   do not become long-lived product segmentation.
1. As a final auditor, I want every success criterion mapped to implementation and proof touchpoints, so that
   architectural seams are not mistaken for completed behavior.

## Non-goals / out of scope

- Browser TypeScript is out of scope for the first SDK milestone.
- Generated raw clients are not the primary public SDK surface.
- TypeScript, Python, Rust, or browser SDKs must not depend on the F#/.NET implementation at runtime.
- OpenAPI generation is not a substitute for Grace Protocol documentation or golden vectors.
- SDK support tiers are not Grace domain glossary terms and should not be added to `CONTEXT.md`.
- This specification does not choose a final generator.
- This specification does not create GitHub issues, branches, worktrees, commits, pull requests, package
  manifests, or generated clients.
- This specification does not require production data migrations. Grace is still development-only unless a
  future request states otherwise.
- This specification does not require long-lived support for lower SDK tiers. Tiers below Tier 4 are maturity
  checkpoints for rapid progression once the SDK contract stabilizes.

## Current-state research

### Paths reviewed

- `AGENTS.md`
- `src/AGENTS.md`
- `src/Grace.SDK/AGENTS.md`
- `src/Grace.Shared/AGENTS.md`
- `skills/grace/SKILL.md`
- `docs/adr/0002-durable-approval-requests.md`
- `docs/.markdownlint.jsonc`
- `docs/Build versioning.md`
- `src/Directory.Build.props`
- `src/OpenAPI/Main.OpenAPI.yaml`
- `src/OpenAPI/Grace.OpenAPI.yaml`
- `src/OpenAPI/Responses.OpenAPI.yaml`
- `src/OpenAPI/Approval.Paths.OpenAPI.yaml`
- `src/OpenAPI/Storage.Paths.OpenAPI.yaml`
- `src/Grace.Server/Startup.Server.fs`
- `src/Grace.Shared/Constants.Shared.fs`
- `src/Grace.Types/Common.Types.fs`
- `src/Grace.SDK/ClientIdentity.SDK.fs`
- `src/Grace.SDK/Common.SDK.fs`
- `src/Grace.CLI/Command/Services.CLI.fs`
- `src/Grace.Authorization.Tests/OpenApiRouteCoverage.Tests.fs`
- `CONTEXT.md`
- `artifacts/Testing generated clients.html`
- Official OpenAPI publications page and OpenAPI 3.2 upgrade guidance.

### Relevant current behavior and gaps

- Product/build/package identity is centralized at `src/Directory.Build.props:3-25`, with base version `0.2.0`.
  `docs/Build versioning.md:3-35` documents the current build version model and distinguishes runtime identity
  from configuration schema identity at `docs/Build versioning.md:59-72`.
- Static OpenAPI source currently declares `openapi: 3.1.0` and `info.version: "2023-10-01"` in the committed
  entrypoints at `src/OpenAPI/Main.OpenAPI.yaml` and `src/OpenAPI/Grace.OpenAPI.yaml`. That value is the HTTP API
  contract version, not the Grace product build version or an SDK package version.
- Server API versioning packages and wiring already exist. `src/Grace.Server/Startup.Server.fs` sets API version
  readers and explorer defaults from the shared `2023-10-01` contract. `src/Grace.Shared/Constants.Shared.fs` defines
  `X-Api-Version`, and `src/Grace.Shared/ApiContractVersion.Shared.fs` defines the current released date plus explicit
  `latest` and `edge` aliases. `src/Grace.Server/Middleware/ApiVersionAlias.Middleware.fs` maps those aliases to the
  current released date before ASP.NET API version parsing.
- Client identity headers already exist. `src/Grace.Shared/Constants.Shared.fs:193-197` defines
  `X-Grace-Client-Type` and `X-Grace-Client-Version`; `src/Grace.SDK/ClientIdentity.SDK.fs:21-37` applies
  configured client identity headers to SDK HTTP clients. The current `ClientType` mapping only showed CLI
  identity in the inspected slice. `src/Grace.CLI/Command/Services.CLI.fs:109-111` configures CLI SDK identity
  from the CLI assembly file version.
- Correlation already has a transport header constant. `src/Grace.Shared/Constants.Shared.fs:448` defines
  `X-Correlation-Id`. The OpenAPI contract should distinguish this transport header from body DTO fields named
  `CorrelationId`.
- SDK public stability is already an explicit repo concern. `src/Grace.SDK/AGENTS.md` says SDK wrappers should
  keep DTOs aligned with server/domain types, prefer additive expansions or versioned members, deprecate before
  removal, and coordinate breaking changes with CLI and external clients.
- Current SDK HTTP helpers often deserialize non-success responses as `GraceError` or create a `GraceError` from
  error text. See `src/Grace.SDK/Common.SDK.fs:60-65` and `src/Grace.SDK/Common.SDK.fs:101-125`. Static shared
  OpenAPI responses currently document `400` and `500` as `ProblemDetails` at
  `src/OpenAPI/Responses.OpenAPI.yaml:9-20`. This is a contract mismatch to audit and resolve.
- `src/OpenAPI/Main.OpenAPI.yaml:52-240` references modular path files across approval, webhook, branch, diff,
  directory, organization, owner, repository, and storage routes. `src/OpenAPI/Approval.Paths.OpenAPI.yaml:1-32`
  shows improved operation IDs and tags for approval routes. The checked-in bundle scan found no operation tags,
  no `examples`, and no `externalDocs`, `links`, `callbacks`, `webhooks`, or `deprecated` markers. The modular
  files contain many single `example` fields but no plural `examples` fields in the scan.
- `src/OpenAPI/Storage.Paths.OpenAPI.yaml:24-110` already distinguishes `getUploadUri` as an `application/json`
  map and `getDownloadUri`, `getContentBlockUploadUri`, and `getContentBlockDownloadUri` as `text/plain` URI
  strings. This is the level of media-type precision the SDK-grade audit should apply across the API.
- `src/Grace.Authorization.Tests/OpenApiRouteCoverage.Tests.fs:11-50` is a high-signal existing guard for
  ensuring OpenAPI contains the ADR-0001 storage routes, but it is not a full SDK-grade OpenAPI quality gate.
- Protocol-like .NET implementation currently lives in `src/Grace.Shared`: `ContentAddress.Shared.fs`,
  `ContentBlockFormat.Shared.fs`, `DedupeIndex.Shared.fs`, `ManifestEligibility.Shared.fs`,
  `ManifestValidation.Shared.fs`, and `RabinChunking.Shared.fs`.
- Manifest upload/download orchestration currently lives in `src/Grace.SDK`: `LocalPlanner.SDK.fs`,
  `ManifestUpload.SDK.fs`, and `ManifestDownload.SDK.fs`.
- `CONTEXT.md` already defines Grace domain terms such as `FileManifest`, `ContentBlock`, `UploadSession`, and
  `ManifestContributionWorkflow`. SDK tiers and generator planning vocabulary should remain in this specification,
  not in the glossary.
- `sdk/`, `docs/protocol/`, and `test-vectors/` did not exist in the inspected checkout.
- The [official OpenAPI publications page](https://spec.openapis.org/oas/) lists `v3.2.0` and `v3.1.2` as
  published versions. The [OpenAPI 3.2.0 specification](https://spec.openapis.org/oas/v3.2.0.html) identifies
  version 3.2.0, and the
  [OpenAPI 3.1 to 3.2 upgrade guidance](https://learn.openapis.org/upgrading/v3.1-to-v3.2.html) describes 3.2 as
  backward-compatible with 3.1 while adding features such as enhanced tags, richer examples, and server identity
  fields. This supports a canonical 3.2 source with loss-aware 3.1.2 projections when generator tooling requires them.

### Inference and verification plan

- Inference: the current static OpenAPI bundle is not fresh enough or rich enough for SDK generator selection.
  Verification: implementation must run a deterministic bundle/freshness command, compare bundle output to source,
  and run SDK-grade quality checks before choosing generators.
- Inference: the server can implement SDK lifecycle policy by inspecting existing client identity headers.
  Verification: implementation must add server tests proving warning headers and unsupported-client errors are
  emitted for recognized client type/version combinations.
- Inference: TypeScript Node can reach Tier 4 without depending on .NET by implementing protocol semantics from
  docs and vectors. Verification: implementation must create protocol vector suites and run TypeScript protocol
  tests against them before claiming Tier 3 or Tier 4.

## Requirements ledger / traceability map

| ID | Requirement statement | Type | Why it exists | Implementation touchpoints | Proof touchpoints | Status | Residual risk |
| -- | --------------------- | ---- | ------------- | -------------------------- | ----------------- | ------ | ------------- |
| SDK-001 | OpenAPI is the authoritative Grace Server HTTP contract. | Behavior | Keeps wire behavior discoverable and generator-ready. | `src/OpenAPI`, server route metadata, OpenAPI bundle/publish path | OpenAPI lint, route coverage, runtime contract smoke tests | Required | Server and static docs can drift without freshness gates. |
| SDK-002 | Generated raw clients sit behind handwritten facades. | Behavior | Prevents generator churn from becoming the public SDK contract. | `sdk/*/generated`, `sdk/*/facade`, .NET SDK packaging | Facade API tests, package import tests | Required | Raw diagnostic access may still be mistaken for supported surface. |
| SDK-003 | Public SDK compatibility attaches to the handwritten facade, not generated raw client shape. | Behavior | Gives maintainers freedom to regenerate or switch generators. | SDK package READMEs, facade exports, generated module visibility | Package surface snapshot tests, docs review | Required | Language package conventions may make internal modules importable. |
| SDK-004 | SDK tiers are maturity gates and serious SDKs should progress quickly to Tier 4. | Workflow | Avoids long-lived lower-tier product segmentation. | Spec, issue DAGs, milestone docs | Epic checklist, tier acceptance evidence | Required | Teams may stop at Tier 1 unless Tier 4 gates are tracked. |
| SDK-005 | Tier 2 includes simple server-mediated file transfer. | Behavior | Enables early adoption before manifest protocol parity. | Server storage routes, facades, docs | Whole-file upload/download tests | Required | Users may confuse Tier 2 with high-performance Grace-native behavior. |
| SDK-006 | Tier 3 protocol parity is defined by Grace Protocol docs and golden vectors. | Proof | Prevents .NET runtime dependency for non-.NET SDKs. | `docs/protocol`, `test-vectors/protocol`, protocol implementations | Cross-language vector tests | Required | Vectors can be incomplete if not tied to invariants. |
| SDK-007 | Tier 4 adds advanced local integration such as cache, resumability, progress, watch/offline, and direct storage. | Behavior | Defines the intended serious SDK endpoint. | Language facades, protocol packages, local cache modules | Integration and protocol tests | Required | Tier 4 may overgrow without per-language scope control. |
| SDK-008 | TypeScript Node is the first non-.NET SDK target. | Workflow | Aligns the first milestone with filesystem and server-client needs. | `sdk/typescript`, npm package metadata | TypeScript build, Node import, facade tests | Required | Browser needs could pull scope sideways. |
| SDK-009 | Browser TypeScript is excluded from the first SDK milestone. | Workflow | Browser auth, filesystem, and upload semantics are separate. | Spec, issue bodies | Review checklist | Required | Shared TypeScript code may accidentally imply browser support. |
| SDK-010 | Python is an early follow-up target after TypeScript Node. | Workflow | Keeps a common automation language in the roadmap. | `sdk/python` | Python import/build tests | Required | Python generator quality may lag TypeScript. |
| SDK-011 | Rust is included in the generator prototype matrix. | Proof | Prevents Rust feasibility from being discovered too late. | `sdk/rust/generated`, generator matrix scripts | Rust compile/import probe | Required | Rust may remain Tier 0 or Tier 1 until demand and vectors justify more. |
| SDK-012 | Canonical OpenAPI source targets OpenAPI 3.2.0. | Non-functional | Uses the current published standard and 3.2 documentation affordances. | `src/OpenAPI` | OpenAPI 3.2 validation | Required | Some generators may lag 3.2. |
| SDK-013 | Generator-specific projections may target OpenAPI 3.1.2 only as scripted derived artifacts. | Non-functional | Keeps generator compatibility from weakening the canonical contract. | Projection scripts, generated artifacts | Loss report, projection freshness tests | Required | Projection loss can hide important 3.2 metadata. |
| SDK-014 | Every operation has a stable, globally unique, language-friendly `operationId`. | Behavior | Makes generated raw clients usable. | OpenAPI path files | Operation ID lint/check | Required | Existing generic IDs need broad audit. |
| SDK-015 | Every operation has a primary SDK grouping tag and root tag metadata. | Behavior | Supports facade grouping and navigable docs. | OpenAPI root tags and operation tags | Tag coverage lint/check | Required | Bundle or renderer may drop modular tag metadata. |
| SDK-016 | Response envelopes, raw media responses, status codes, and `GraceError` shapes are modeled explicitly. | Behavior | Prevents SDKs from guessing error and response semantics. | `Responses.OpenAPI.yaml`, path files, SDK error handling | Runtime smoke tests and schema checks | Required | Framework auth/challenge shapes may differ from Grace domain errors. |
| SDK-017 | Auth, correlation, client identity, and lifecycle headers are reusable OpenAPI components. | Security | Makes transport policy visible and reviewable. | OpenAPI components, server middleware, SDK transport code | Header contract tests | Required | Header casing and defaults can drift across clients. |
| SDK-018 | SDK packages use language-native SemVer. | Behavior | Lets package consumers reason about facade compatibility. | NuGet/npm/PyPI/crates package metadata | Package metadata tests, release checklist | Required | Pre-1.0 packages still need clear breaking-change notes. |
| SDK-019 | HTTP API contract versions are date-based and separate from SDK package versions. | Behavior | Avoids confusing product, package, API, and protocol versions. | `X-Api-Version`, OpenAPI `info.version`, docs | Version parsing and compatibility tests | Required | Existing aliases and old enum values need cleanup or mapping. |
| SDK-020 | Released SDK facades default to a pinned API contract version, not `latest` or `edge`. | Behavior | Makes package upgrades safer and API opt-in deliberate. | SDK constructors, config defaults | Client header tests | Required | Developer workflows may still prefer `edge`; override must be easy. |
| SDK-021 | SDK facades may support multiple API versions only when one coherent public facade shape remains. | Behavior | Prevents one package from becoming a museum of incompatible behavior. | Facade version routing, generated raw clients per API version | Multi-version facade tests | Required | Some changes may require package major boundaries. |
| SDK-022 | Server lifecycle metadata is response-header-first for soft SDK deprecation warnings. | Behavior | Warns real users during ordinary API use. | Server response policy, SDK header parser | Successful-response warning tests | Required | Warnings can become noisy without suppression rules. |
| SDK-023 | Unsupported SDK versions fail with a structured `GraceError`, such as `UnsupportedClientVersion`. | Behavior | Lets clients tell users to update instead of showing vague HTTP failures. | Server compatibility policy, `GraceError`, SDK error handling | Unsupported-client integration tests | Required | Error-code naming must align with final `GraceError` shape. |
| SDK-024 | A compatibility/status endpoint is optional and diagnostic, not the primary runtime mechanism. | Behavior | Supports tooling without relying on preflight polling. | Optional server route, docs | Endpoint tests if implemented | Deferred | Endpoint shape can distract from response-header mechanism. |
| SDK-025 | Generated raw clients are not separately published as stable packages initially. | Workflow | Avoids accidental public API promises. | Package layout and exports | Package contents/import tests | Required | Some package managers make private generated modules discoverable. |
| SDK-026 | Generator selection waits for an SDK-grade OpenAPI audit. | Workflow | Avoids blaming tools for a weak contract. | Audit issue, generator matrix | Audit report and generator evidence | Required | Lightweight probes may be mistaken for final decisions. |
| SDK-027 | Generator matrix includes Kiota, OpenAPI Generator, and NSwag comparison points. | Proof | Creates evidence across target languages and .NET comparison. | Prototype scripts | Compile/import matrix | Required | Tool versions can change results; pin versions in evidence. |
| SDK-028 | `sdk/`, `docs/protocol/`, and `test-vectors/protocol/` are monorepo-first and extraction-compatible. | Workflow | Keeps early cross-language work close to the contract. | New directories, package boundaries | Repo layout review | Required | Monorepo convenience can bleed into package coupling. |
| SDK-029 | OpenAPI examples and documentation affordances are part of readiness. | Documentation | Makes the contract human-usable and generator-reviewable. | OpenAPI examples, `externalDocs`, links, deprecation markers | Example validation and docs review | Required | Example sprawl can slow inner-loop validation. |
| SDK-030 | Completion requires a final OpenAPI quality/freshness gate, not repeated inner-loop gauntlets. | Workflow | Balances rigor with implementation throughput. | Validation scripts, CI boundary, issue DOD | Final audit evidence | Required | Too much can move into normal validation and slow all work. |

## Dependencies and touchpoints

### Likely implementation seams

- `src/OpenAPI`: canonical OpenAPI 3.2 source, reusable components, path files, response shapes, examples,
  tags, version metadata, and derived bundle/projection generation.
- `src/Grace.Server`: API version policy, response lifecycle headers, unsupported-client error behavior,
  runtime OpenAPI publishing, and route behavior evidence.
- `src/Grace.Shared`: transport header constants, `GraceError` contract, protocol helper extraction seams, and
  shared version parsing helpers if added.
- `src/Grace.Types`: public DTOs, client type/version model if type-safe server policy needs it, error-code
  vocabulary, and any API-version identifiers that remain public.
- `src/Grace.SDK`: .NET facade behavior, raw generated-client boundary if introduced, client identity defaults,
  lifecycle warning parsing, and current manifest protocol implementation.
- `src/Grace.CLI`: CLI client identity configuration, lifecycle warning display, and user-facing update message
  behavior.
- `src/Grace.Authorization.Tests`: OpenAPI route coverage and contract guard expansion.
- `src/Grace.Server.Tests`: HTTP surface tests for API versioning, lifecycle headers, unsupported-client errors,
  storage media types, and wire compatibility.
- `src/Grace.Server.Unit.Tests`: pure helpers for lifecycle policy, version parsing, and OpenAPI source checks
  that do not require hosted server behavior.
- `src/Grace.SDK.Tests` or a future SDK test project: facade compatibility and lifecycle warning behavior.
- `sdk/typescript`, `sdk/python`, and `sdk/rust`: generated raw clients, facades, protocol implementations, and
  language-specific package metadata.
- `docs/protocol`: protocol documents for addresses, ContentBlocks, FileManifests, eligibility, upload/download,
  corruption rejection, and vector interpretation.
- `test-vectors/protocol`: golden vector inputs and expected outputs for protocol-capable SDKs.

### Optional or deferred seams

- A server compatibility/status endpoint for tooling and diagnostics.
- A future `Grace.Protocol` .NET project/package once the protocol surface stabilizes.
- Browser TypeScript packages or browser-compatible submodules.
- Separate repositories for language SDKs after the monorepo shape stabilizes.

### Out-of-scope seams

- Production data migrations.
- Browser storage, browser auth, service-worker, or web-upload UX.
- Publishing standalone stable raw generated-client packages.
- Replacing existing Grace domain terminology in `CONTEXT.md`.

## Interfaces and contracts

### SDK support tiers

| Tier | Name | Capability | Compatibility promise |
| ---: | ---- | ---------- | --------------------- |
| 0 | Raw Generated Client | Generated endpoint client and DTOs from OpenAPI. | Regenerable, compile/import clean, useful for diagnostics; not the stable public SDK facade. |
| 1 | Grace SDK Facade | Idiomatic public API, auth, base URL, correlation, identity, errors, retries. | Public facade SemVer compatibility. |
| 2 | Simple File Transfer | Whole-file or server-mediated upload/download usable by early SDKs. | Stable facade behavior for simple file workflows. |
| 3 | Manifest Protocol | Local planning, chunking, addressing, ContentBlock format, dedupe, upload/download, vectors. | Protocol docs and vector-suite compatibility. |
| 4 | Advanced Local Integration | Cache integration, resumability, progress, watch/offline, direct storage. | Full serious SDK target for language ecosystems that Grace supports. |

Tiers are maturity checkpoints. Tier 0 through Tier 3 should not be treated as a slow, long-lived product support
roadmap once the SDK contract is stable.

### Versioning contracts

| Version axis | Shape | Owner | Compatibility rule |
| ------------ | ----- | ----- | ------------------ |
| SDK package version | Language-native SemVer, for example `0.4.0` or `1.2.0` | SDK facade package | Breaking facade changes require major version after `1.0`; pre-1.0 breaking changes require explicit release notes. |
| HTTP API contract version | Date-based, for example `2026-09-01` or `2026-09-01-preview` | OpenAPI and server API policy | Breaking wire changes require a new API contract version. |
| Product/build version | Existing Grace build identity, for example `0.2.0-ci.1234` | Grace build and packaging | Does not define HTTP API compatibility by itself. |
| Protocol vector suite version | Explicit suite identity, for example a future `manifest-protocol-v1` | Grace Protocol docs and vectors | Tier 3+ SDKs declare supported vector suites and must pass them. |

Released SDK facades must default to an explicit API contract version. `latest` and `edge` may exist as aliases or
developer conveniences, but must be opt-in. OpenAPI `info.version` should identify the API contract version
represented by that document or bundle, not the SDK package version and not the Grace product build version.

### OpenAPI source and derived artifacts

Canonical source lives under `src/OpenAPI` and targets OpenAPI 3.2.0. The canonical bundle, such as a future
fresh `Grace.OpenAPI.yaml`, is generated from that source for docs, generators, server publishing, and external
consumers. Generator-specific compatibility projections may target OpenAPI 3.1.2 only as scripted, validated,
loss-aware derived artifacts.

Derived artifacts must never be edited by hand as a substitute for changing canonical source. Completion requires
freshness checks for every committed or published derived artifact.

### Generated and facade package layout

The target monorepo shape is:

    sdk/
      dotnet/
        generated/
        facade/
      typescript/
        generated/
        facade/
        protocol/
      python/
        generated/
        facade/
        protocol/
      rust/
        generated/
        facade/
        protocol/
    docs/
      protocol/
    test-vectors/
      protocol/

Package boundaries must remain extraction-compatible. Generated raw clients should be internal generated modules
inside the language package. Public exports should be facade-first, such as a TypeScript Node package exposing a
`GraceClient`-style facade rather than requiring users to import generated endpoint classes.

### Transport headers

| Header | Direction | Required behavior |
| ------ | --------- | ----------------- |
| `Authorization` | Request | Bearer token scheme via reusable OpenAPI security components. |
| `X-Api-Version` | Request | Explicit HTTP API contract version. SDK defaults to a pinned date version; aliases are opt-in. |
| `X-Correlation-Id` | Request and response | Optional request correlation value and documented response correlation value when emitted. |
| `X-Grace-Client-Type` | Request | SDK facade sends a stable client type such as CLI, .NET SDK, TypeScript Node SDK, Python SDK, or Rust SDK. |
| `X-Grace-Client-Version` | Request | SDK facade sends its package/client version. |

Header matching is case-insensitive at the HTTP layer, but OpenAPI should use the canonical names above.

### SDK lifecycle response headers

The server may emit lifecycle metadata on any response when the client identity is known and the server has a
support policy for that client type/version.

| Header | Direction | Accepted values or shape | Behavior |
| ------ | --------- | ------------------------ | -------- |
| `X-Grace-Client-Support-Status` | Response | `supported`, `deprecated`, `unsupported` | SDK facade parses lifecycle state. Unknown values are diagnostic warnings, not crashes. |
| `X-Grace-Client-Unsupported-After` | Response | ISO date, for example `2026-12-01` | SDK facade warns users when support will end. |
| `X-Grace-Client-Min-Version` | Response | Language package version string | Minimum version accepted by the server. |
| `X-Grace-Client-Recommended-Version` | Response | Language package version string | Recommended update target. |
| `X-Grace-Client-Update-Url` | Response | HTTPS URL | User-facing update destination. |

When the status is `deprecated`, the API call may still succeed. The facade should surface a warning through a
language-appropriate callback, logger, diagnostic event, or CLI message, and should suppress repeated warnings
enough to avoid noisy output.

When the status is `unsupported`, the server should return a structured `GraceError` with a stable error code such
as `UnsupportedClientVersion`. The SDK facade should turn that into clear user guidance to update to the latest
supported Grace Client instead of presenting it as a generic HTTP or deserialization failure.

### Grace Protocol contract

Grace Protocol is the portable local protocol layer for Tier 3 and Tier 4 SDK claims. It includes, at minimum:

- `ChunkAddress`
- `ContentBlockAddress`
- `ManifestAddress`
- ContentBlock physical format
- FileManifest reconstruction rules
- Corruption rejection
- Manifest download verification
- Upload transcript and idempotency semantics where clients participate
- Canonical manifest eligibility behavior for SDKs claiming automatic manifest-backed upload parity

The .NET implementation is the reference implementation for producing and validating vectors, but non-.NET SDKs
must implement the protocol from docs and vectors rather than depending on the .NET runtime at execution time.

### Generator prototype matrix

| Generator | Languages | Purpose |
| --------- | --------- | ------- |
| Kiota | .NET, TypeScript | Evaluate Microsoft ecosystem generator ergonomics, auth extensibility, and facade fit. |
| OpenAPI Generator | TypeScript, Python, Rust | Evaluate broad language coverage and package ecosystem fit. |
| NSwag | .NET | Provide a .NET comparison point against Kiota and existing SDK expectations. |
| Grace-specific tooling | As needed | Allowed only after off-the-shelf limits are proven by the matrix. |

No generator is accepted unless generated output can be regenerated cleanly, compiles/imports without hand edits,
supports Grace auth/correlation/client identity policy, and can sit behind the handwritten facade.

## Functional requirements

1. The OpenAPI audit must run before final generator selection.
1. The OpenAPI audit must verify operation IDs, tags, request bodies, response bodies, status codes, media types,
   error shapes, transport headers, lifecycle headers, examples, external docs, deprecation markers, and freshness.
1. The canonical OpenAPI source must move to OpenAPI 3.2.0 when this workstream implements the contract upgrade.
1. Compatibility projections must be generated from canonical source and must report any lost 3.2 semantics.
1. SDK packages must expose handwritten facades as the supported public API.
1. Generated raw clients must be regenerated deterministically and must compile or import without hand edits.
1. Generated raw clients must be internal package implementation modules by default.
1. Raw diagnostic access, if exposed, must be clearly documented as compatibility-limited.
1. Each SDK package release must declare its package version, default API contract version, supported API contract
   versions, and supported protocol vector suites when applicable.
1. SDK constructors must allow an explicit API contract version override.
1. SDK facades must send `X-Api-Version`, `X-Grace-Client-Type`, and `X-Grace-Client-Version` by default.
1. SDK facades must support `X-Correlation-Id` request behavior and expose response correlation when available.
1. SDK facades must parse lifecycle response headers on successful and failing responses.
1. SDK facades must surface deprecated-client warnings without repeatedly spamming users.
1. SDK facades must handle `UnsupportedClientVersion` as a clear update-required outcome.
1. Server lifecycle policy must support soft warnings before an SDK support end date and hard blocks after the
   unsupported threshold.
1. The TypeScript Node SDK milestone must include Tier 1 facade behavior and Tier 2 simple file transfer before
   claiming broader Grace-native behavior.
1. TypeScript Node Tier 3 must not be claimed until protocol docs and vectors exist and TypeScript passes them.
1. Tier 4 must not be claimed until advanced local integration behavior is implemented and proven in that language.
1. Protocol-capable SDKs must implement protocol semantics from docs and vectors, not by shelling out to .NET or
   loading .NET assemblies at runtime.
1. Rust generator feasibility must be evaluated during the generator prototype matrix, even if Rust facade support
   remains deferred.

### Current server lifecycle contract

The first server-side lifecycle policy recognizes Grace CLI/SDK client identity from `X-Grace-Client-Type` and
`X-Grace-Client-Version`. The identity is diagnostic and lifecycle input only; it is not authentication proof.

Recognized deprecated clients continue through normal request handling and receive response headers:

- `X-Grace-Client-Support-Status: deprecated`
- `X-Grace-Client-Unsupported-After`
- `X-Grace-Client-Min-Version`
- `X-Grace-Client-Recommended-Version`
- `X-Grace-Client-Update-Url`

Recognized unsupported clients receive `426 Upgrade Required` with the same lifecycle headers and a structured
`GraceError` whose `Error` value is `UnsupportedClientVersion`. Missing, unknown, or malformed client identity
continues without lifecycle headers so raw HTTP clients are not blocked by lifecycle diagnostics.

## Non-functional requirements

- The OpenAPI contract should be standard-conforming and OpenAPI 3.2-forward.
- OpenAPI components should be reusable, readable, and generator-friendly.
- Public enum, union, optional, nullable, and time shapes must be intentional across languages.
- Simple closed sets should use string enum shapes.
- Payload-bearing unions should use a generator-friendly tagged object shape.
- Open extension cases must have deliberate schema representation.
- `Instant` values must be string/date-time shaped with UTC expectations and realistic examples.
- Internal actor command/event unions must not leak into public OpenAPI merely because they exist in `Grace.Types`.
- Generator and projection scripts must be deterministic and pin tool versions or report tool versions.
- SDK packages must keep public surface small and idiomatic.
- SDK warnings must be observable without requiring stdout in library contexts.
- Validation should be layered so inner-loop work uses cheap focused checks and final completion uses the full
  OpenAPI/generator/protocol gauntlet.
- Documentation should use stable Grace vocabulary from `CONTEXT.md` for domain concepts.

## Security, abuse, privacy, and data-durability considerations

- Auth must remain a first-class OpenAPI security scheme rather than informal prose.
- Client identity headers are diagnostic and lifecycle inputs. They must not be treated as authentication or
  authorization proof.
- Lifecycle response headers must not disclose sensitive deployment details, tenant data, or private policy
  internals.
- Update URLs must be HTTPS and should point to a trusted Grace-controlled destination.
- SDK lifecycle warnings should avoid logging tokens, request bodies, or user file paths.
- Unsupported-client errors should be clear but should not reveal internal policy implementation details.
- API-version override must not bypass authorization, validation, or server-side compatibility checks.
- Protocol vectors must include corruption and tampering cases, not only happy paths.
- Tier 3 and Tier 4 SDKs must reject invalid ContentBlocks, invalid FileManifest reconstruction, and mismatched
  addresses rather than accepting server or storage data blindly.
- Simple file transfer remains an adoption path, not a security downgrade. It must use the same auth, correlation,
  and client identity policy as other SDK operations.
- Grace is currently development-only. If implementation changes persisted development data shapes, issues should
  prefer simple reset instructions over elaborate migrations unless the user asks for migration work.

## Scenarios and user/developer workflows

### Released SDK defaults to stable API

1. A user installs a released TypeScript Node SDK package.
1. The SDK package exposes an idiomatic facade.
1. The facade sends `X-Api-Version` with its pinned date-based API contract version.
1. The facade sends `X-Grace-Client-Type` and `X-Grace-Client-Version`.
1. The server accepts the request and returns normal response data.
1. The user does not need to know which raw generated client module handled the endpoint.

### Developer opts into edge API behavior

1. A developer constructing a local test client explicitly chooses `edge`.
1. The SDK sends `X-Api-Version: edge`.
1. The facade treats the choice as opt-in and may mark behavior as preview or compatibility-limited.
1. Tests and examples make clear that released SDK defaults do not silently use `edge`.

### Server warns about SDK end of support

1. The SDK sends client identity headers.
1. The server recognizes the SDK version as still supported but deprecated.
1. The response succeeds and includes lifecycle headers with an unsupported-after date and recommended version.
1. The SDK facade parses the headers.
1. The CLI displays a concise update message. A library host receives the same signal through a callback, logger,
   or diagnostic event.
1. The facade suppresses repeated warnings according to language-appropriate policy.

### Server blocks unsupported SDK version

1. The SDK sends client identity headers for an unsupported version.
1. The server returns a structured `GraceError` with an unsupported-client error code and update metadata.
1. The facade recognizes the error code.
1. The user sees a clear update-required message.
1. The facade does not misclassify the result as a generic network, auth, JSON, or server failure.

### TypeScript Node progresses from Tier 0 to Tier 4

1. The OpenAPI audit passes.
1. The TypeScript raw client is generated deterministically and imports cleanly.
1. The handwritten facade exposes stable Tier 1 behavior.
1. Tier 2 simple file transfer works through the facade.
1. Protocol docs and vectors are added.
1. TypeScript protocol code passes vector tests and claims Tier 3.
1. Cache, progress, resumability, and advanced local integration are added and proven before claiming Tier 4.

### Generator is rejected

1. A generator produces output for one target language.
1. Output requires hand edits, fails import/compile, cannot support Grace headers, or cannot sit behind the facade.
1. The matrix records the failure with command output and rationale.
1. The workstream either tries another generator or proposes Grace-specific tooling after off-the-shelf limits are
   proven.

### Protocol vector catches a cross-language bug

1. A non-.NET SDK implements ContentBlock parsing.
1. The vector suite includes valid and corrupt ContentBlock bytes.
1. The SDK accepts valid bytes and rejects corrupt bytes.
1. A future implementation bug changes parsing behavior.
1. The vector test fails before the SDK claims Tier 3 compatibility.

## Testing requirements

| ID | Public behavior or invariant | Expected RED evidence | Likely test file or project | Validation command |
| -- | ---------------------------- | --------------------- | --------------------------- | ------------------ |
| T-001 | Canonical OpenAPI source validates as OpenAPI 3.2.0. | Current source declares 3.1.0. | OpenAPI validation script or test project | Focused OpenAPI validation, then `pwsh ./scripts/validate.ps1 -Fast` at completion. |
| T-002 | Derived OpenAPI bundles and projections are fresh. | Editing source without regenerating bundle should fail freshness. | OpenAPI freshness script/test | Focused freshness command. |
| T-003 | Every operation has a unique, stable, language-friendly `operationId`. | Current generic legacy IDs should fail once included in the audit. | OpenAPI quality test | Focused OpenAPI audit command. |
| T-004 | Every operation has a primary SDK tag and root tag metadata. | Current bundle scan has no operation tags. | OpenAPI quality test | Focused OpenAPI audit command. |
| T-005 | `400` and normal Grace failures use the intended `GraceError` shape. | Current shared OpenAPI response references `ProblemDetails`. | Server and OpenAPI contract tests | Focused server/OpenAPI tests. |
| T-006 | Raw URI responses are modeled as `text/plain`. | Regression would document URI values as JSON envelopes. | Storage route contract tests | Existing or expanded server tests. |
| T-007 | SDK clients send pinned `X-Api-Version` by default. | Current helper behavior may use `Edge` in some shared paths. | SDK/CLI transport tests | Focused SDK/CLI tests. |
| T-008 | SDK clients send `X-Grace-Client-Type` and `X-Grace-Client-Version`. | Unconfigured SDK client should fail identity expectation where required. | SDK and CLI tests | Focused SDK/CLI tests. |
| T-009 | Server emits lifecycle warning headers for deprecated SDK versions. | No current lifecycle policy exists. | `Grace.Server.Tests` or unit policy tests | Focused server test. |
| T-010 | SDK facade surfaces lifecycle warnings once or with suppression. | No current lifecycle parser exists. | SDK facade tests and CLI tests | Focused SDK/CLI tests. |
| T-011 | Server returns `UnsupportedClientVersion` for unsupported SDK versions. | No current unsupported-client error exists. | Server integration tests | Focused server test. |
| T-012 | SDK facade converts unsupported-client error into update guidance. | Current generic `GraceError` handling lacks lifecycle-specific UX. | SDK and CLI tests | Focused SDK/CLI tests. |
| T-013 | Generated raw clients regenerate deterministically. | Hand-edited or nondeterministic output should fail. | Generator matrix scripts | Generator matrix command. |
| T-014 | Generated raw clients compile or import without hand edits. | Generator output fails build/import. | Language package test suites | Language-specific build/import commands. |
| T-015 | TypeScript Node Tier 1 facade works through the generated raw client boundary. | Direct raw-client-only behavior is insufficient. | TypeScript facade tests | npm test/build command. |
| T-016 | Tier 2 simple file transfer works through facade. | Missing server-mediated transfer support blocks early adoption. | Server plus language SDK tests | Focused server and SDK tests. |
| T-017 | Protocol vectors prove ContentBlock address and format behavior. | Non-.NET implementation fails vector suite. | Protocol vector tests | Language-specific vector command. |
| T-018 | Protocol vectors prove FileManifest reconstruction and corruption rejection. | Invalid data is accepted or reconstruction differs. | Protocol vector tests | Language-specific vector command. |
| T-019 | Tier 3 SDKs implement canonical eligibility automation before claiming parity. | SDK claims manifest parity but chooses different eligibility. | Protocol and facade tests | Language-specific vector command. |
| T-020 | Rust generator output compiles or the failure is recorded. | Rust generator probe fails. | Rust prototype project | Cargo check/test command. |
| T-021 | OpenAPI examples validate against schemas. | Example/schema mismatch fails. | OpenAPI example validation | Focused OpenAPI example validation. |
| T-022 | Public package exports do not expose generated raw client as the primary surface. | Package users can import only generated client in documented path. | Package surface tests | Language-specific package test. |

The implementing agent should critique and improve these tests before implementation. Tests should prove regression
resistance, not merely smoke that a happy path can run once.

## Validation criteria and commands

Final validation profile should be chosen by the implementation issue and current repo guidance. The expected ladder is:

- Run targeted formatting before build/test validation for any touched F# files.
- Run focused OpenAPI quality, freshness, and generation checks for contract work.
- Run focused SDK package build/import/facade tests for language SDK work.
- Run protocol vector tests for any Tier 3 or Tier 4 claim.
- Use exactly one final broad build/test gate for a completed slice, normally `pwsh ./scripts/validate.ps1 -Fast`.
- Use `pwsh ./scripts/validate.ps1 -Full` when Aspire, emulators, storage, Service Bus, Cosmos DB, Redis,
  deployment/runtime behavior, or cross-service integration is materially affected.
- Run MarkdownLint for documentation-only changes.

PowerShell:

    npx --yes markdownlint-cli2 "docs/SDK and OpenAPI architecture.md" "docs/adr/0003-generated-raw-sdk-clients-behind-facades.md"
    pwsh ./scripts/validate.ps1 -Fast

bash / zsh:

    npx --yes markdownlint-cli2 "docs/SDK and OpenAPI architecture.md" "docs/adr/0003-generated-raw-sdk-clients-behind-facades.md"
    pwsh ./scripts/validate.ps1 -Fast

Skipped or deferred validation must state the reason and residual risk. The full OpenAPI/generator/protocol gauntlet
belongs at completion or CI boundaries, not as a repeated inner-loop cost for every small worker edit.

## Issue and PR handoff alignment

This specification is not itself a GitHub issue or pull request. If the user asks for tracked implementation, create
or confirm a GitHub issue first. For multi-step implementation, use an epic parent issue with linked sub-issues and a
DAG showing dependencies and parallelism.

### Suggested issue framing

- Objective: stabilize Grace's SDK/OpenAPI architecture by upgrading the OpenAPI contract, validating generator
  candidates, and creating facade/protocol/versioning/lifecycle foundations.
- Context and evidence: cite this specification, the new ADR, current OpenAPI version mismatch, existing SDK
  identity headers, existing SDK stability guidance, and the generated-client validation artifact.
- Candidate owned paths: `src/OpenAPI`, `src/Grace.Server`, `src/Grace.Shared`, `src/Grace.Types`, `src/Grace.SDK`,
  `src/Grace.CLI`, relevant test projects, `sdk/`, `docs/protocol/`, `test-vectors/protocol/`, package metadata,
  and docs.
- Candidate forbidden or sensitive paths: unrelated domain docs, unrelated ADRs, unrelated workflow/process files,
  secrets, user-specific config, and generated artifacts unless the issue explicitly owns freshness.
- Risk surfaces: HTTP wire compatibility, generated client usability, SDK public API stability, protocol
  correctness, lifecycle UX, auth/correlation/client identity behavior, package exports, and validation cost.
- Suggested validation profile: focused OpenAPI/SDK/protocol tests plus `validate -Fast`; `validate -Full` only for
  slices affecting hosted runtime/storage integration.
- Public behavior under test: stable facade API, explicit API-version selection, lifecycle warnings and unsupported
  errors, generated raw client regeneration/import, simple file transfer, and protocol vector parity.
- Expected RED evidence: current OpenAPI 3.1 source, version mismatch, missing SDK lifecycle policy, missing
  `sdk/`, `docs/protocol/`, and `test-vectors/` directories, and raw generator probes before acceptance.
- Docs impact: update OpenAPI docs, SDK docs, protocol docs, package READMEs, and nearby `AGENTS.md` files when
  behavior changes.
- Definition of done: every claimed tier, API version, generated client, facade behavior, protocol vector suite,
  and lifecycle mechanism is implemented and proven or explicitly deferred with residual risk.

### Suggested PR evidence

- Link the issue or epic.
- Summarize behavior-level changes, not only file changes.
- State which SDK tier and language target the PR claims.
- State touched-path compliance against the issue.
- Include generator version/tool evidence when generation is involved.
- Include OpenAPI freshness and quality evidence when OpenAPI changes.
- Include facade tests and package import/build evidence when SDK packages change.
- Include protocol vector evidence when Tier 3 or Tier 4 behavior changes.
- Include lifecycle warning/error evidence when client support policy changes.
- Record skipped validation and residual risks.
- Update the PR body's `Review Status` section when review comments are added or resolved.

### Handoff boundaries

Implementation agents must follow current repo templates and issue-owned workflow. Do not create alternate durable
task ledgers. The main orchestrator should delegate coding/fixing to worker subagents and use review-only sibling
subagents for code review when tracked implementation begins.

## Completion semantics and final audit instructions

The work is not complete because an OpenAPI file, generated client, package directory, protocol document, or ADR
exists. Each success criterion must end as one of:

- Implemented and proven.
- Implemented but proof incomplete, with named residual risk.
- Intentionally deferred.
- Explicitly out of scope.
- Rejected because it conflicts with current design or repo guidance.

The final audit must compare shipped behavior against every requirement in the ledger, shipped proof against every
testing requirement, changed paths against issue-owned paths if tracked implementation is used, docs impact handled
or waived, skipped validation with reasons, and follow-up work that should become a new issue.

Do not describe a Tier 3 or Tier 4 SDK as complete unless protocol vectors exist and pass. Do not describe a
generator as accepted unless deterministic regeneration, compile/import, transport policy, and facade-fit gates pass.
Do not describe OpenAPI as SDK-grade unless response shapes, media types, errors, headers, examples, tags, versioning,
source/derived freshness, and runtime evidence are all covered.

The final S17 audit for epic #211 is recorded in
[SDK OpenAPI final audit](SDK%20OpenAPI%20final%20audit.md). That audit is the release matrix and traceability record
for the current `epic/211-sdk-openapi` branch. It intentionally keeps TypeScript Tier 4 scoped to the proven
`facade-transfer-progress-diagnostics` capability, keeps Python and Rust at proof-harness maturity, and does not claim
browser TypeScript support or SDK-grade OpenAPI completion while quality gates remain pending.

### Specification self-critique

- Strongest part: the compatibility model cleanly separates SDK SemVer, date-based HTTP API versions, and protocol
  vector suite versions.
- Most likely wrong assumption: response-header-first lifecycle signaling may need an additional endpoint sooner
  than expected for package-manager tooling and release automation.
- Highest-risk dependency: OpenAPI generator quality for TypeScript, Python, and Rust may be uneven even after the
  contract is strong.
- Easiest way to overbuild: creating full Tier 4 infrastructure before the facade and OpenAPI contract have stopped
  changing.
- Easiest way to under-test: accepting generator output because it compiles, without proving wire behavior,
  lifecycle headers, facade compatibility, and protocol vectors.
- Alternative rejected: publishing raw generated clients as standalone stable packages first. That would create
  accidental support promises and make generator replacement much harder.
