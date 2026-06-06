# Validation Coverage Final Audit

Issue #266 closes the final docs/OpenAPI/generated-artifact audit for validation coverage epic #249. The audit records
how the accepted child issues changed the repo, what remains pending by design, and where every canonical research ID
from the original coverage inventory ended up.

The original research source of truth was the machine-readable dataset embedded in
`artifacts/GraceServerFullValidationCoverageResearch.html` as `grace-test-coverage-data`. This file does not replace
that dataset; it records the final implementation disposition after issues #250 through #265 were merged.

## Audit Scope

- Parent epic: [#249](https://github.com/ScottArbeit/Grace/issues/249).
- Final audit issue: [#266](https://github.com/ScottArbeit/Grace/issues/266).
- Epic integration branch: `epic/249-validation-coverage`.
- Final child branch: `agent/266-docs-openapi-generated-closure-audit`.
- Scope: docs/OpenAPI/generated closure only. This issue did not implement server behavior or change route
  classification decisions.

## Status Vocabulary

- `Implemented and proven`: accepted behavior has a merged PR with validation evidence.
- `Implemented with pending proof`: source artifacts exist and current checks pass with named pending gates.
- `Contract pinned`: the current behavior was intentionally classified or documented instead of expanded.
- `Deferred with reason`: the item remains future work with a named boundary.
- `Rejected with reason`: the item is intentionally unsupported in the current validation profile or product shape.
- `Merged into another item`: the item is covered as part of a broader proof.

## Child Issue And PR Evidence

| Issue | PR | Target | Final evidence |
| ----- | -- | ------ | -------------- |
| [#250](https://github.com/ScottArbeit/Grace/issues/250) W0 route/OpenAPI classification | [#267](https://github.com/ScottArbeit/Grace/pull/267) | `main` | Route classification registry and OpenAPI route coverage tests; `validate -Fast` passed. |
| [#251](https://github.com/ScottArbeit/Grace/issues/251) W1 OpenAPI proof/generated acceptance | [#269](https://github.com/ScottArbeit/Grace/pull/269) | `main` | OpenAPI proof passed with pending gates visible; SDK metadata check and generator matrix passed. |
| [#252](https://github.com/ScottArbeit/Grace/issues/252) W2 Fast/Full split hygiene | [#268](https://github.com/ScottArbeit/Grace/pull/268) | `main` | Pure server tests moved into Fast; `validate -Fast` passed after refresh. |
| [#253](https://github.com/ScottArbeit/Grace/issues/253) W3 Fast security/privacy seams | [#270](https://github.com/ScottArbeit/Grace/pull/270) | `main` | Security/privacy/redaction tests; `validate -Fast` passed after review fixes. |
| [#254](https://github.com/ScottArbeit/Grace/issues/254) W4 hosted assertion depth | [#272](https://github.com/ScottArbeit/Grace/pull/272) | `main` | Owner/repository/approval/webhook/access/operational assertions; `validate -Full` passed. |
| [#255](https://github.com/ScottArbeit/Grace/issues/255) W5a storage CAS and dedupe | [#273](https://github.com/ScottArbeit/Grace/pull/273) | `main` | Manifest upload, ContentBlock download, and dedupe proof; `validate -Full` passed. |
| [#256](https://github.com/ScottArbeit/Grace/issues/256) W5b work item/artifact/permission | [#289](https://github.com/ScottArbeit/Grace/pull/289) | `epic/249-validation-coverage` | Work item permissions and artifact/attachment flows; `validate -Full` passed. |
| [#257](https://github.com/ScottArbeit/Grace/issues/257) W6a branch lifecycle | [#290](https://github.com/ScottArbeit/Grace/pull/290) | `epic/249-validation-coverage` | Hosted branch lifecycle proof; `validate -Full` passed. |
| [#258](https://github.com/ScottArbeit/Grace/issues/258) W6b directory/diff/reminder | [#291](https://github.com/ScottArbeit/Grace/pull/291) | `epic/249-validation-coverage` | Hosted directory, diff, and reminder proof; `validate -Full` passed. |
| [#259](https://github.com/ScottArbeit/Grace/issues/259) W7a policy/queue | [#292](https://github.com/ScottArbeit/Grace/pull/292) | `epic/249-validation-coverage` | Policy and queue route proof; `validate -Full` passed after review fixes. |
| [#260](https://github.com/ScottArbeit/Grace/issues/260) W7b promotion/review/validation | [#293](https://github.com/ScottArbeit/Grace/pull/293) | `epic/249-validation-coverage` | Promotion, review, validation-set, and validation-result proof; `validate -Full` passed. |
| [#261](https://github.com/ScottArbeit/Grace/issues/261) W8a webhook/Service Bus/eventing | [#294](https://github.com/ScottArbeit/Grace/pull/294) | `epic/249-validation-coverage` | Hosted Service Bus eventing and derived event proof; `validate -Full` passed. |
| [#262](https://github.com/ScottArbeit/Grace/issues/262) W8b SignalR/agent session | [#295](https://github.com/ScottArbeit/Grace/pull/295) | `epic/249-validation-coverage` | SignalR and agent-session hosted proof; `validate -Full` passed. |
| [#263](https://github.com/ScottArbeit/Grace/issues/263) W9a durable restart | [#296](https://github.com/ScottArbeit/Grace/pull/296) | `epic/249-validation-coverage` | Durable restart fixture and representative rehydration proof; `validate -Full` passed. |
| [#264](https://github.com/ScottArbeit/Grace/issues/264) W9b process-local restart/loss | [#297](https://github.com/ScottArbeit/Grace/pull/297) | `epic/249-validation-coverage` | Process-local approval/webhook/agent-session restart contracts; `validate -Full` passed. |
| [#265](https://github.com/ScottArbeit/Grace/issues/265) W10 fixture diagnostics/dependencies | [#271](https://github.com/ScottArbeit/Grace/pull/271) | `main` | Redacted diagnostics, dependency decisions, Aspire setup docs; `validate -Full` passed. |

## Backlog Traceability Matrix

| ID | Research item | Final disposition | Evidence |
| -- | ------------- | ----------------- | -------- |
| B-001 | Owner setter persists field after valid update | Implemented and proven | #254 / PR #272 added owner get-after-set and invalid no-mutate assertions; `validate -Full` passed. |
| B-002 | Repository setter persists field after valid update | Implemented and proven | #254 / PR #272 added repository get-after-set and invalid no-mutate assertions; `validate -Full` passed. |
| B-003 | Branch create/get/list/promote/save route proof | Implemented and proven | #257 / PR #290 added hosted branch lifecycle proof; `validate -Full` passed. |
| B-004 | Directory version HTTP happy and missing cases | Implemented and proven | #258 / PR #291 added directory-version hosted proof; `validate -Full` passed. |
| B-005 | Directory zip file route returns usable URI/content | Implemented and proven | #258 / PR #291 included directory route proof for content/URI behavior; `validate -Full` passed. |
| B-006 | Diff populate/get route contract | Implemented and proven | #258 / PR #291 added hosted diff route contract proof; `validate -Full` passed. |
| B-007 | Reminder create/list/get/update/reschedule/delete | Implemented and proven | #258 / PR #291 added hosted reminder proof; `validate -Full` passed. |
| B-008 | Reminder service restart/recovery proof | Implemented and proven | #263 / PR #296 added representative durable restart/rehydration proof including reminder state; `validate -Full` passed. |
| B-009 | Real block upload then manifest finalize | Implemented and proven | #255 / PR #273 proved real block upload, manifest finalize, and download behavior; `validate -Full` passed. |
| B-010 | Manifest upload restart/recovery | Implemented and proven | #263 / PR #296 added durable restart/rehydration proof covering manifest upload state; `validate -Full` passed. |
| B-011 | Dedupe discovery adversarial limits | Implemented and proven | #255 / PR #273 added dedupe adversarial proof and unit coverage; `validate -Full` passed. |
| B-012 | Storage raw response contract lock | Implemented and proven | #255 / PR #273 covered storage raw response behavior; prior OpenAPI audit keeps raw URI responses as `text/plain`. |
| B-013 | Work item repository permission matrix | Implemented and proven | #256 / PR #289 added work item link authorization and endpoint manifest proof; `validate -Full` passed. |
| B-014 | Work item attachment downloads actual content | Implemented and proven | #256 / PR #289 added attachment endpoint proof; `validate -Full` passed. |
| B-015 | Work item restart preserves numeric counter | Implemented and proven | #263 / PR #296 added durable restart proof for work item counter/state; `validate -Full` passed. |
| B-016 | Artifact create/download URI persisted metadata | Implemented and proven | #256 / PR #289 added artifact route/SDK URI proof and metadata fixes; `validate -Full` passed. |
| B-017 | Queue status/enqueue/pause/resume/dequeue HTTP lifecycle | Implemented and proven | #259 / PR #292 added queue route lifecycle proof; `validate -Full` passed. |
| B-018 | Promotion-set HTTP apply/recompute/resolve conflict lifecycle | Implemented and proven | #260 / PR #293 added promotion-set route proof; `validate -Full` passed. |
| B-019 | Review candidate HTTP projection routes | Implemented and proven | #260 / PR #293 added review route projection proof; `validate -Full` passed. |
| B-020 | Review deepen stub contract | Contract pinned | #250 classified route status; #260 / PR #293 kept the current stub contract explicit instead of implementing provider behavior. |
| B-021 | Validation result record persists artifact links | Implemented with pinned drift | #260 / PR #293 added validation-result duplicate-correlation proof and explicitly pinned current `Output = null` persistence drift. |
| B-022 | Approval policy lifecycle persists show/evaluate details | Implemented and proven | #254 / PR #272 added approval policy lifecycle/evaluate snapshot assertions; `validate -Full` passed. |
| B-023 | Approval process-local store restart contract | Implemented and proven | #264 / PR #297 proved approval policy process-local behavior across restart; `validate -Full` passed. |
| B-024 | SeedGenerated route stronger internal gate | Contract pinned | #250 / PR #267 classified `_seedGenerated` as intentionally non-public/internal instead of adding it to public OpenAPI. |
| B-025 | Webhook rule lifecycle deep persisted fields | Implemented and proven | #254 / PR #272 added webhook lifecycle and delivery field assertions; `validate -Full` passed. |
| B-026 | Webhook event-to-dispatch E2E through Service Bus | Implemented and proven | #261 / PR #294 added hosted Service Bus eventing proof; `validate -Full` passed. |
| B-027 | Webhook process-local store restart contract | Implemented and proven | #264 / PR #297 proved HTTP-observable webhook process-local behavior across restart; `validate -Full` passed. |
| B-028 | Service Bus processor retry and ordering behavior | Implemented and proven | #261 / PR #294 added Service Bus eventing/retry proof; `validate -Full` passed. |
| B-029 | SignalR notifications negotiation and event delivery | Implemented and proven | #262 / PR #295 added SignalR notification proof; `validate -Full` passed. |
| B-030 | Agent session start/stop/status/active/listActive | Implemented and proven | #262 / PR #295 added hosted agent-session lifecycle proof; `validate -Full` passed. |
| B-031 | Agent session restart-loss contract | Implemented and proven | #264 / PR #297 proved active agent sessions are process-local across restart; `validate -Full` passed. |
| B-032 | Derived quick-scan event-to-validation result | Implemented and proven | #261 / PR #294 added derived event-to-validation-result proof; `validate -Full` passed. |
| B-033 | Auth PAT malformed/expired/wrong-owner variants | Implemented and proven | #253 / PR #270 added Fast auth/PAT and security seam coverage; `validate -Fast` passed. |
| B-034 | Access revoke/list stored-state verification | Implemented and proven | #254 / PR #272 added access grant/list/check/revoke/list/check proof; `validate -Full` passed. |
| B-035 | Endpoint authorization manifest full route status smoke | Implemented and proven | #250 / PR #267 added route classification/OpenAPI route coverage tests; `validate -Fast` passed. |
| B-036 | ValidateIds not-found status contract | Implemented and proven | #253 / PR #270 added ValidateIds status/body/correlation decision coverage; `validate -Fast` passed. |
| B-037 | Telemetry claim redaction policy | Implemented and proven | #253 / PR #270 added telemetry redaction coverage; `validate -Fast` passed. |
| B-038 | Request logging redacts authorization headers | Implemented and proven | #253 / PR #270 added request header redaction coverage and review fix; `validate -Fast` passed. |
| B-039 | W3C log file smoke and privacy | Implemented and proven | #265 / PR #271 added redacted diagnostics/privacy proof for fixture output; `validate -Full` passed. |
| B-040 | Startup missing Cosmos/storage/Service Bus config failure messages | Implemented and proven | #265 / PR #271 classified required keys and startup diagnostics; `validate -Full` passed. |
| B-041 | DEBUG admin delete routes explicit test contract | Contract pinned | #250 / PR #267 added explicit route classification coverage for debug/admin surfaces; `validate -Fast` passed. |
| B-042 | OpenAPI full route coverage gate | Implemented and proven | #250 / PR #267 added OpenAPI route coverage/exclusion registry tests; `validate -Fast` passed. |
| B-043 | Generated OpenAPI proof manifest freshness | Implemented and proven | #251 / PR #269 refreshed OpenAPI proof manifest; current `prove-openapi.ps1 -Check All -AllowPending` passes with pending gates. |
| B-044 | OpenAPI schema generator acceptance lock | Implemented with pending proof | #251 / PR #269 and this audit confirm generator acceptance remains guardrailed by schema debt and `--skip-validate-spec`. |
| B-045 | Auth/login intentionally unavailable contract | Implemented and proven | #254 / PR #272 added auth/login unavailable response assertions; `validate -Full` passed. |
| B-046 | Outbound URL safety live DNS timeout variant | Implemented and proven | #253 / PR #270 added outbound URL failure/redaction seam coverage without live external calls; `validate -Fast` passed. |
| B-047 | Service Bus skip mode contract | Rejected with reason | #265 / PR #271 classified `GRACE_TEST_SKIP_SERVICEBUS=1` as unsupported for the current shared setup and fails early. |
| B-048 | Redis dependency assertion or removal | Deferred with reason | #265 / PR #271 keeps Redis provisioned/forwarded and records a future runtime/product decision because Redis-backed SignalR is currently commented out. |
| B-049 | Metrics endpoint SystemAdmin success path | Implemented and proven | #254 / PR #272 added metrics/root/health/correlation proof with durable markers; `validate -Full` passed. |
| B-050 | Root and healthz response contract | Merged into another item | #254 / PR #272 covered root/healthz with operational metrics/correlation proof; no standalone behavior change needed. |
| B-051 | Policy current/acknowledge HTTP proof | Implemented and proven | #259 / PR #292 added policy route proof; `validate -Full` passed. |
| B-052 | Validation set CRUD HTTP contract | Implemented and proven | #260 / PR #293 added validation-set CRUD route proof; `validate -Full` passed. |
| B-053 | Model provider failure on review analysis | Deferred with reason | #253 source verification kept this out of scope for Fast seam work because provider behavior should not call live model providers in unit coverage. |
| B-054 | Full-only pure test split verification | Implemented and proven | #252 / PR #268 moved pure no-Aspire tests into Fast and refreshed Fast validation; `validate -Fast` passed. |
| B-055 | Full suite dependency diagnostics smoke | Implemented and proven | #265 / PR #271 added fixture diagnostics tests and redaction coverage; `validate -Full` passed. |
| B-056 | OpenAPI route exclusion registry | Implemented and proven | #250 / PR #267 added machine-readable route classification/exclusion coverage; `validate -Fast` passed. |
| B-057 | Cosmos/Orleans grain rehydration sample across domains | Implemented and proven | #263 / PR #296 added restart fixture and representative rehydration proof; `validate -Full` passed. |
| B-058 | Cross-surface correlation ID contract | Implemented and proven | #254 / PR #272 added representative correlation and response contract assertions; `validate -Full` passed. |

## Drift Disposition Matrix

| ID | Research drift | Final disposition |
| -- | -------------- | ----------------- |
| drift-001 | Static OpenAPI is not route-complete | Resolved by #250 / PR #267 as route classification plus public/OpenAPI exclusion registry, not mechanical route expansion. |
| drift-002 | OpenAPI proof manifest is stale | Resolved by #251 / PR #269. Current proof manifest freshness passes. |
| drift-003 | `/openApi` appears in OpenAPI but no server route was found | Contract pinned by #250. Current OpenAPI proof still records `/openApi` 400/500 coverage as pending rather than hiding it. |
| drift-004 | `/approval/request/_seedGenerated` is an HTTP route but intentionally absent from public OpenAPI | Contract pinned by #250 as non-public/internal. It remains intentionally excluded from public OpenAPI. |
| drift-005 | `/review/deepen` is routed but not implemented | Contract pinned by #250 and #260. It is not implemented in this epic and is not claimed as SDK-grade behavior. |
| drift-006 | SDK/OpenAPI docs mention old OpenAPI version and response shape | Resolved by #251 and the current SDK/OpenAPI docs. Current proof reports canonical OpenAPI 3.2.0. |
| drift-007 | Service Bus management port doc differs from AppHost | Resolved by #265 / PR #271. `src/docs/ASPIRE_SETUP.md` now matches AppHost evidence for management endpoint `5300`. |
| drift-008 | OpenAPI generator acceptance remains guarded by schema defects | Implemented with pending proof. Generator matrix accepts OpenAPI Generator TypeScript/Python/Rust with guardrails; Kiota/NSwag remain rejected; strict generator validation remains future schema cleanup. |

## Surface Closure Matrix

| Surface | Final disposition |
| ------- | ----------------- |
| `surf-host` | Covered by route classification, request/telemetry redaction, operational metrics/root/health proof, diagnostics, and correlation coverage across #250, #253, #254, and #265. |
| `surf-auth-access` | Covered by PAT/auth seams, access revoke/list state, unavailable login contract, and authorization manifest coverage across #253 and #254. |
| `surf-owner-repo-branch` | Covered by owner/repository assertion-depth upgrades and branch lifecycle proof across #254 and #257. |
| `surf-directory-diff-reminder` | Covered by hosted directory, diff, reminder, and durable restart proof across #258 and #263. |
| `surf-storage` | Covered by CAS, manifest upload/finalize, dedupe, raw response contract, and durable restart proof across #255 and #263. |
| `surf-workitem-artifact` | Covered by work item permissions, attachment/artifact URI proof, and restart counter/state proof across #256 and #263. |
| `surf-review-queue-promotion` | Covered by policy/queue, promotion-set, review, validation-set, and validation-result proof across #259 and #260. |
| `surf-approval` | Covered by route classification, approval policy/request assertion upgrades, and process-local restart contract proof across #250, #254, and #264. |
| `surf-webhooks` | Covered by webhook lifecycle assertions, Service Bus event-to-dispatch proof, and process-local restart contract proof across #254, #261, and #264. |
| `surf-events-agent` | Covered by Service Bus eventing, derived quick-scan, SignalR notifications, agent-session lifecycle, and process-local restart contracts across #261, #262, and #264. |
| `surf-startup-admin` | Covered by admin route classification, startup diagnostics, Service Bus skip-mode rejection, Redis deferral, and restart fixture proof across #250, #263, and #265. |

## OpenAPI And Generated Artifact Status

Current command outcome from the epic release branch after the PR #299 final review blocker fix:

```text
pwsh ./src/OpenAPI/prove-openapi.ps1 -Check All -AllowPending
```

Result: passed with four pending gates.

- Freshness: passed for 25 canonical source files.
- Canonical version: passed; source declares OpenAPI 3.2.0.
- Projections: passed for canonical bundle, generator projection, and loss report.
- Operation quality scaffold: scanned 102 operations and passed operationId/header checks.
- Pending: 13 storage operation tags.
- Pending: `/openApi` 400/500 error response coverage.
- Generated-client matrix: passed for OpenAPI Generator TypeScript, Python, and Rust with guardrails.
- Pending: stable SDK package export/import proof.
- Pending: protocol vector proof verifier.

Current generated metadata command from the epic release branch after the PR #299 final review blocker fix:

```text
pwsh ./sdk/scripts/generate-sdk-clients.ps1 -Mode Check
```

Result: passed. The committed generated SDK metadata matches `src/OpenAPI/Grace.OpenAPI.3.1.2.yaml`.

Current generator matrix command:

```text
pwsh ./sdk/scripts/invoke-generator-matrix.ps1
```

Result: passed and produced no tracked diff. OpenAPI Generator TypeScript, Python, and Rust remain accepted with
guardrails behind handwritten facades. Kiota and NSwag remain rejected for the current projection. Strict OpenAPI
Generator validation remains blocked by schema-shape debt, so the generated clients are not SDK-grade public contracts.

## Explicit Pending And Deferred Items

- OpenAPI storage operation tag coverage is still pending in the proof scaffold.
- `/openApi` 400/500 response coverage is still pending in the proof scaffold.
- Stable SDK package export/import proof is still pending.
- Protocol vector proof is still pending until a real verifier consumes vector schema, required coverage, and parity
  execution results.
- OpenAPI Generator acceptance remains guardrailed by `--skip-validate-spec`; strict generator validation is future
  schema cleanup.
- Redis remains provisioned but is deferred as a runtime/product decision because Redis-backed SignalR is currently not
  active.
- `GRACE_TEST_SKIP_SERVICEBUS=1` is explicitly unsupported for the current shared Full setup.
- `/review/deepen` remains a pinned current contract, not a newly implemented provider-backed route.

## Final Validation Commands

Commands run from `C:\Source\GraceWorktrees\epic-249-validation-coverage-release`:

| Command | Outcome |
| ------- | ------- |
| `dotnet tool run fantomas --check src/Grace.Server/Artifact.Server.fs src/Grace.Server.Tests/WorkItem.Integration.Server.Tests.fs` | Passed; no changes required. |
| `dotnet test src/Grace.Server.Tests/Grace.Server.Tests.fsproj --configuration Release --filter "ArtifactRoutesEnforceRepositoryPermissionsAndPreserveDownloadIdentityPathAndBytes"` | Passed; 1 test. |
| `pwsh ./src/OpenAPI/prove-openapi.ps1 -Check All -AllowPending` | Passed with four pending gates recorded above. |
| `pwsh ./sdk/scripts/generate-sdk-clients.ps1 -Mode Check` | Passed. |
| `npx --yes markdownlint-cli2 "docs/Validation coverage final audit.md"` | Passed. |
| `git diff --check` | Passed. |

`pwsh ./scripts/validate.ps1 -Fast` and `pwsh ./scripts/validate.ps1 -Full` were not rerun for this release-blocker
fix. The artifact authorization blocker was validated with the focused hosted regression above, and the child PR table
above records the last relevant Fast/Full validation for each implemented behavior slice.

## Epic Closure Comment Draft

Epic #249 can close after #266 merges into `epic/249-validation-coverage` and the final epic-to-`main` release
candidate passes its review/validation gate.

Suggested issue comment:

```markdown
## Final Validation Coverage Audit

Issue #266 completed the docs/OpenAPI/generated closure audit for epic #249.

Final audit artifact: `docs/Validation coverage final audit.md`

Summary:

- Every `B-001` through `B-058` has a final disposition with issue/PR evidence.
- Every `drift-001` through `drift-008` is resolved, pinned, rejected, or explicitly pending/deferred.
- All `surf-*` surfaces are mapped to merged child issue evidence.
- OpenAPI proof currently passes with four pending gates: storage operation tags, `/openApi` 400/500 coverage, SDK
  package export/import proof, and protocol vector proof.
- Generated SDK metadata freshness passes.
- Generator matrix passes with OpenAPI Generator TypeScript/Python/Rust accepted with guardrails; Kiota and NSwag remain
  rejected for the current projection.
- No new server behavior, route classification, or generated client regeneration was introduced by the closure audit.

Validation run for #266:

- `pwsh ./src/OpenAPI/prove-openapi.ps1 -Check All -AllowPending`: passed with pending gates recorded.
- `pwsh ./sdk/scripts/generate-sdk-clients.ps1 -Mode Check`: passed.
- `pwsh ./sdk/scripts/invoke-generator-matrix.ps1`: passed with no tracked diff.
- `npx --yes markdownlint-cli2 "docs/Validation coverage final audit.md"`: passed.
- `git diff --check`: passed.

Remaining explicit boundaries:

- Do not claim SDK-grade generated clients while strict generator validation still needs schema-shape cleanup.
- Do not claim `/review/deepen` provider-backed behavior; the current contract remains pinned.
- Do not claim Redis-backed SignalR runtime behavior; Redis remains a deferred product/runtime decision.
- Do not use `GRACE_TEST_SKIP_SERVICEBUS=1` as a supported shared Full profile.
```
