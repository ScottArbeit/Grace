# Issue #1038 Test Status

## Disposition

The Issue #1038 remote-only Synchronized Content candidate is ready for independent review and required GitHub `Validate`. The final implementation self-review found and corrected the root-management route policy so only `RepositoryAdmin` can add or remove synchronized roots. No Issue #1039 local participation, local SQLite, or filesystem publication behavior is active.

## Requirement Evidence

| Requirement | Evidence |
| --- | --- |
| Domain identities, DTOs, validation, persisted empty root configuration, and exact root transitions | `SynchronizedContentTypesTests`: `repository creation persists one stable empty synchronized root configuration`, `portable path normalization is stable and rejects Grace internal paths`, `root normalization sorts unique roots and rejects overlap`, `root transitions require exact version and retain prior configuration on rejection`, `mutation validation enforces exact field combinations`, and `wire scalar bounds reject uppercase hashes oversized tokens and pages` passed 6/6. |
| Reader, writer, administrator, and route authorization remain distinct | `AuthorizationSemanticsTests.SynchronizedContentRolesHaveIndependentReadAndWriteAuthority` and `EndpointAuthorizationManifestTests.SynchronizedContentRoutesUseAcceptedRepositoryPolicies` passed. The latter checks all 15 routes, including repository-admin root changes and anonymous opaque-grant redemption. The complete two-fixture run passed 34/34. |
| Only one accepted mutation can lead its projections, receipt, cursor, and wake | `SynchronizedContentCoordinatorTests.RepairFailureDoesNotAdvanceControlAndConvergesOnRetry` passed at `canonical`, `item`, `slot`, `history`, `receipt`, and `control`; `SynchronizedWakeRequiresAcceptedMutationReceipt`, `SynchronizedWakeFollowsDurableSubmitAndPrecedesResponse`, and all other coordinator tests passed. The synchronized server-unit run passed 51/51. |
| Bootstrap publication is deterministic, bounded, and manifest-last | `BaselinePlanIsDeterministicAndPublishesExactShardMetadata` and `BaselinePlanKeepsEveryShardWithinTheOneMillionByteLimit` passed. |
| Root policy uses exact path segments and current predecessor state | `DirectoryVersionRootOwnershipUsesExactPathSegments`, `SynchronizedConfigurationOwnsOnlyExactRootSegments`, `RepositoryRootConfigurationRequiresExactCurrentPredecessor`, `read-only current-state scan excludes configured synchronized root`, `shared repository path classifier is boundary aware`, and `topology rejects target beneath synchronized root before mutation` passed. The corrected CLI fixture/category run passed 62/62. |
| Existing upload completion records an exact synchronized preparation without changing ordinary uploads | `OrdinaryUploadCompletionDoesNotRecordSynchronizedPreparation` and `SynchronizedUploadCompletionReplayRetainsOneExactPreparationResult` passed within the 51-test server-unit run. |
| Best-effort wake uses repository read authorization and cannot change durable success | `SynchronizedContentWakeTargetsRepositoryGroup`, `SynchronizedContentSubscriptionRequiresSynchronizedContentReadAuthorization`, `SynchronizedContentSubscriptionRejectsDeniedReadAuthorization`, `SynchronizedContentSubscriptionRejectsMismatchedStoredRepositoryIdentity`, and `SynchronizedContentWakeFailureDoesNotEscape` passed. |
| Remote SDK and root-only CLI expose the accepted public surface | `sync roots exposes only remote root operations`, `sync root mutations require every concurrency input`, and `synchronized content SDK exposes complete remote contract` passed. `CommandOutputContractRegistryTests` passed 27/27. |
| Static OpenAPI and generated clients remain deterministic and current | `prove-openapi.ps1 -Check All -AllowPending` passed freshness, canonical 3.2, projections, quality scaffolding, metadata hashes, and generated-client matrix checks. `generate-sdk-clients.ps1 -Mode Check` passed. TypeScript, Python, and Rust generator probes returned exit code 0 with unchanged deterministic manifests. |
| Configuration and composition compile as one remote-only topology | `Grace.Aspire.AppHost.csproj` Release build passed with zero warnings and errors after the six synchronized containers and token secret were composed. |
| Named documentation states authorization, root ownership, remote status, configuration, SDK/OpenAPI, Watch, and WDU boundaries | Markdown lint reported zero non-MD013 findings across the seven named files. Four MD013 findings were ignored as required. |

## Passing Commands

PowerShell:

```powershell
dotnet fantomas --check <40 changed F# files>

dotnet build src/Grace.Types.Tests/Grace.Types.Tests.fsproj --configuration Release --no-restore
dotnet test src/Grace.Types.Tests/Grace.Types.Tests.fsproj --configuration Release --no-build --filter "FullyQualifiedName~SynchronizedContentTypesTests"

dotnet build src/Grace.Authorization.Tests/Grace.Authorization.Tests.fsproj --configuration Release --no-restore
dotnet test src/Grace.Authorization.Tests/Grace.Authorization.Tests.fsproj --configuration Release --no-build --filter "FullyQualifiedName~EndpointAuthorizationManifestTests|FullyQualifiedName~AuthorizationSemanticsTests"

dotnet build src/Grace.Server.Unit.Tests/Grace.Server.Unit.Tests.fsproj --configuration Release --no-restore
dotnet test src/Grace.Server.Unit.Tests/Grace.Server.Unit.Tests.fsproj --configuration Release --no-build --filter "FullyQualifiedName~SynchronizedContentCoordinatorTests|FullyQualifiedName~NotificationServerTests|Name~OrdinaryUploadCompletionDoesNotRecordSynchronizedPreparation|Name~SynchronizedUploadCompletionReplayRetainsOneExactPreparationResult"

dotnet build src/Grace.CLI.Tests/Grace.CLI.Tests.fsproj --configuration Release --no-restore
dotnet test src/Grace.CLI.Tests/Grace.CLI.Tests.fsproj --configuration Release --no-build --filter "FullyQualifiedName~SynchronizedContentCliParsingTests|FullyQualifiedName~CurrentStateCaptureCliTests|FullyQualifiedName~WorkingDirectoryUpdateTopologyTests|TestCategory=WatchPathClassification"
dotnet test src/Grace.CLI.Tests/Grace.CLI.Tests.fsproj --configuration Release --no-build --filter "FullyQualifiedName~CommandOutputContractRegistryTests"

dotnet build src/Grace.Aspire.AppHost/Grace.Aspire.AppHost.csproj --configuration Release --no-restore

pwsh ./src/OpenAPI/prove-openapi.ps1 -Check All -AllowPending
pwsh ./sdk/scripts/generate-sdk-clients.ps1 -Mode Check
pwsh ./sdk/scripts/invoke-generator-matrix.ps1

git diff --check 5d6e2cbe3c53e4a1d4c058da1eac978c99dce35f...HEAD
```

Build results were zero warnings and zero errors. Focused test totals were 6/6 Types, 34/34 authorization and route policy, 51/51 server unit, 62/62 synchronized CLI/root classification/WDU, and 27/27 machine-output contract.

## OpenAPI Pending Gates

The final OpenAPI check retains four pre-existing repository-wide pending gates without changing them:

- Thirteen existing Storage operations lack accepted parser-visible tags.
- `GET /openApi` lacks the accepted `400` and `500` pair.
- Stable SDK package export/import evidence remains pending.
- Portable protocol-vector verification remains pending.

TypeScript, Python, and Rust OpenAPI Generator output is accepted only as raw-client evidence behind facades. Kiota and NSwag remain rejected on existing schema-shape debt. No generated-tree collision or PR #700 lineage was introduced.

## Residual Validation Gaps

- No `Grace.Server.Tests` hosted Aspire/Cosmos Emulator test was added for the six-container route, restart, and injected publication-boundary matrix. The in-memory coordinator tests cover deterministic reservation, canonical-first persistence, repair, replay, and manifest-last planning, but do not exercise real Cosmos behavior.
- No hosted HTTP/SignalR plus real immutable-storage test covers prepare, upload completion, mutation submission, one-use download, and best-effort wake as one external flow. Focused server-unit tests cover upload completion replay, grant-adjacent seams, wake targeting, authorization, and failure swallowing.

Local `validate.ps1 -Fast` and `-Full` were not run because the Run Charter requires focused validation and reserves those commands for documented escalation. GitHub `Validate` remains the required broad gate for the final pull-request revision.

## Self-Review

The complete base-to-candidate diff was checked against the Issue #1038 outcome, Product V1 remote-only non-goals, mutation effect order, root ownership, authorization, public route/DTO/SDK/OpenAPI propagation, generated freshness, test claims, and owned paths. Reference, Cache, Diff, Work Item Attachment, local SQLite, filesystem publication, and Issue #1039 behavior remain unchanged. DirectoryVersion, Save/Branch, Watch documentation, and WDU changes are limited to the accepted root-policy boundary; WDU caller, target, completion, and publication semantics are unchanged.
