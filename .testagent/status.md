# Issue #1038 test status

## Disposition

The Issue #1038 Product V1 remote-only Libraries candidate includes the frozen R1-01 through R1-07 repair ledger and the narrow R2 closure correction for six-container integration readiness. It is ready for resumed R2 closure review after required GitHub `Validate`. RepositoryLibraryActor owns the Library catalog and serialized Library change lane. Existing Save, Reference, Branch, Watch, and Working Directory Update domains only consume the Library boundary and retain their existing lifecycle and publication behavior. Issue #1039 local participation remains inactive.

## Requirement evidence

| Requirement | Evidence |
| --- | --- |
| Stable identities, DTOs, validation, and empty catalog | `LibraryTypesTests`: `repository creation facts produce one stable empty Library catalog`, `portable path normalization is stable and rejects Grace internal paths`, `root normalization sorts unique libraries and rejects overlap`, `root transitions require exact version and retain prior configuration on rejection`, `change validation enforces exact field combinations`, and `wire scalar bounds reject uppercase hashes oversized tokens and pages`. Types passed 6/6. |
| RepositoryLibraryActor owns the catalog and serialized change lane without a RepositoryActor callback | `RepositoryLibraryActorHasOneWayCatalogOwnership`, `IsInLibraryUsesExactCurrentCatalogSnapshot`, and `IsInLibraryObservesEmptyAndChangedCatalogs`. The latter covers exact root, descendant, sibling prefix, forward slash, backslash, case, empty catalog, and changed catalog behavior. |
| Purpose-specific Cosmos keys and both 100,000 current-projection bounds | `LibraryPersistenceUsesAcceptedPurposeSpecificPartitionKeys`, `CurrentProjectionBoundsCoverItemHeadsAndNamespaceSlots`, and `CurrentProjectionBoundsRunBeforeReservation`. The failure proof leaves pending empty, the cursor unchanged, and all durable change records absent. |
| Canonical change leads repairable projections, receipt, control advancement, and wake | `RepairFailureDoesNotAdvanceControlAndConvergesOnRetry` passed at `canonical`, `item`, `slot`, `history`, `receipt`, and `control`; `LibraryWakeRequiresAcceptedChangeReceipt` and `LibraryWakeFollowsDurableSubmitAndPrecedesResponse` passed. |
| Bootstrap publication is deterministic, bounded, and manifest-last | `BaselinePlanIsDeterministicAndPublishesExactShardMetadata` and `BaselinePlanKeepsEveryShardWithinTheOneMillionByteLimit`. |
| Best-effort wake is authorized and cannot change durable success | `LibraryContentWakeTargetsRepositoryGroup`, `LibraryContentSubscriptionRequiresLibraryReadAuthorization`, `LibraryContentSubscriptionRejectsDeniedReadAuthorization`, `LibraryContentSubscriptionRejectsMismatchedStoredRepositoryIdentity`, and `LibraryContentWakeFailureDoesNotEscape`. |
| Upload completion records one exact Library preparation without changing ordinary uploads | `OrdinaryUploadCompletionDoesNotRecordLibraryPreparation` and `LibraryUploadCompletionReplayRetainsOneExactPreparationResult`. The corrected coordinator, notification, and upload fixture filter passed 56/56. |
| Reader, writer, administrator, and route authorization remain distinct | `LibraryContentRolesHaveIndependentReadAndWriteAuthority` and `LibraryContentRoutesUseAcceptedRepositoryPolicies`. The complete two-fixture authorization run passed 34/34 and covers all 15 routes. |
| Library path ownership uses exact segments while version-control lifecycles remain unchanged | `DirectoryVersionRootOwnershipUsesExactPathSegments`, `LibraryConfigurationOwnsOnlyExactRootSegments`, `read-only current-state scan excludes configured Library`, `topology rejects target beneath Library before mutation`, and the complete `WorkingDirectoryUpdateBranchDirectoryVersionTests` fixture. The focused CLI path-policy runs passed 55/55 and 37/37. |
| Remote SDK, CLI, and machine output expose the accepted vocabulary | `library exposes only remote catalog operations`, `library catalog changes require every concurrency input`, `Libraries SDK exposes complete remote contract`, and `CommandOutputContractRegistryTests`. The output-contract fixture passed 27/27. |
| Static OpenAPI and generated clients are deterministic and current | `generate-openapi-projections.ps1`, `generate-sdk-clients.ps1 -Mode Generate`, `generate-sdk-clients.ps1 -Mode Check`, `invoke-generator-matrix.ps1`, and `prove-openapi.ps1 -Check All -AllowPending` passed. TypeScript, Python, Rust, and .NET facade metadata match the accepted `/libraries` contract. |
| Configuration and named documentation match the remote-only topology | `Grace.Aspire.AppHost` Release build passed with zero warnings and errors for the six exact Library containers and `grace__libraries__token_secret`. Markdown lint reported zero non-MD013 findings; the three MD013 line-length findings are ignored by repository policy. |

## Accepted R1 repair evidence

| Requirement | Evidence |
| --- | --- |
| R1-01: fix RepositoryLibraryActor create initialization key/catalog identity; add hosted create plus exact-retry catalog proof. | `CreateInitializesMatchingEmptyLibraryCatalog` and `LibraryContainersUseAcceptedContractsAndGateTestReadiness` passed 2/2 under one Aspire setup. Integration readiness creates and verifies all six fixed Library containers from the AppHost contract before shared repository setup, `InitialCatalogExactRetryIsIdempotent` passed, and generated Orleans codecs carry stable field IDs for the complete Library actor argument and result graph. |
| R1-02: recheck exact repository-scoped Library permission immediately before reservation; prove block/revoke/release leaves no pending/change/projection/receipt/wake. | `RevokedLibraryWritePermissionPreventsReservationAndReceipt` passed. The actor-owned gate calls the current repository permission evaluator before coordinator submission; denial leaves the submit count at zero, so no reservation or later durable/wake effect can begin. |
| R1-03: make baseline identity, catalog, epoch, cursor, and every page one immutable snapshot, or invalidate V1 before V2 pages return; add regression. | `BaselineManifestCapturesCatalogSnapshotForEveryPage` passed. The manifest persists the catalog, reuse checks catalog version plus epoch and boundary cursor, catalog acceptance clears the published pointer, and every page reads the manifest snapshot. |
| R1-04: projection-backed GetOperation and content-read preparation must repair through repository lane before answering; inject canonical-before-projection restart and prove receipt/content truth. | `ProjectionReadsRepairCanonicalBeforeProjectionRestart` passed and source-order assertions prove both projection-backed handlers call `Repair` before their store read. |
| R1-05: if preparation persists then upload-session start fails, exact retry must resume/verify session before returning success. | `PreparedContentRetryResumesExactUploadSession` passed. The durable preparation retains the exact owner, organization, storage, scope, and sampling-policy inputs needed to recreate the same upload-session start command. |
| R1-06: exact retry of catalog operation A must return its durable original result after later catalog operation B; conflicting reuse remains rejected. | `CatalogOperationRetryReturnsOriginalResultAfterLaterMutation` passed. The immutable operation result shares the repository control partition, accepted catalog mutation and result creation use one transactional batch, and replay returns before consulting later current-item or outgoing-root state. |
| R1-07: repair exact-head Validate failures: runtime/static/generated route classification, two Branch switch fixtures, five startup-validation tests, and unrelated Doctor output unchanged. | `OpenApiRouteCoverageTests` passed 9/9, `BranchCommandTests` passed 63/63, startup validation passed 5/5, and `DoctorCliTests` passed 91/91. The R2 closure correction adds `LibraryContainersUseAcceptedContractsAndGateTestReadiness` and verifies repository creation no longer observes a missing Library control container. |

## Passing commands

PowerShell:

```powershell
dotnet build src/Grace.Types.Tests/Grace.Types.Tests.fsproj -c Release
dotnet test src/Grace.Types.Tests/Grace.Types.Tests.fsproj -c Release --no-build --filter 'FullyQualifiedName~LibraryTypesTests'

dotnet build src/Grace.Authorization.Tests/Grace.Authorization.Tests.fsproj -c Release
dotnet test src/Grace.Authorization.Tests/Grace.Authorization.Tests.fsproj -c Release --no-build --filter 'FullyQualifiedName~EndpointAuthorizationManifestTests|FullyQualifiedName~AuthorizationSemanticsTests'

dotnet build src/Grace.Server.Unit.Tests/Grace.Server.Unit.Tests.fsproj -c Release
dotnet test src/Grace.Server.Unit.Tests/Grace.Server.Unit.Tests.fsproj -c Release --no-build --filter 'FullyQualifiedName~LibraryCoordinatorTests|FullyQualifiedName~NotificationServerTests|Name~OrdinaryUploadCompletionDoesNotRecordLibraryPreparation|Name~LibraryUploadCompletionReplayRetainsOneExactPreparationResult'

dotnet build src/Grace.CLI.Tests/Grace.CLI.Tests.fsproj -c Release
dotnet test src/Grace.CLI.Tests/Grace.CLI.Tests.fsproj -c Release --no-build --filter 'FullyQualifiedName~LibraryCliParsingTests|FullyQualifiedName~CurrentStateCaptureCliTests|FullyQualifiedName~WorkingDirectoryUpdateTopologyTests'
dotnet test src/Grace.CLI.Tests/Grace.CLI.Tests.fsproj -c Release --no-build --filter 'FullyQualifiedName~WorkingDirectoryUpdateBranchDirectoryVersionTests'
dotnet test src/Grace.CLI.Tests/Grace.CLI.Tests.fsproj -c Release --no-build --filter 'FullyQualifiedName~CommandOutputContractRegistryTests'

dotnet build src/Grace.Aspire.AppHost/Grace.Aspire.AppHost.csproj -c Release

dotnet build src/Grace.Server.Tests/Grace.Server.Tests.fsproj -c Release
dotnet test src/Grace.Server.Tests/Grace.Server.Tests.fsproj -c Release --no-build --filter 'Name=LibraryContainersUseAcceptedContractsAndGateTestReadiness|Name=CreateInitializesMatchingEmptyLibraryCatalog'

dotnet build src/Grace.Server.Unit.Tests/Grace.Server.Unit.Tests.fsproj -c Release
dotnet test src/Grace.Server.Unit.Tests/Grace.Server.Unit.Tests.fsproj -c Release --no-build --filter 'FullyQualifiedName~Grace.Server.Tests.LibraryCoordinatorTests'
dotnet test src/Grace.Server.Unit.Tests/Grace.Server.Unit.Tests.fsproj -c Release --no-build --filter 'Name~StartupValidation'

dotnet build src/Grace.CLI.Tests/Grace.CLI.Tests.fsproj -c Release
dotnet test src/Grace.CLI.Tests/Grace.CLI.Tests.fsproj -c Release --no-build --filter 'FullyQualifiedName~Grace.CLI.Tests.BranchCommandTests'
dotnet test src/Grace.CLI.Tests/Grace.CLI.Tests.fsproj -c Release --no-build --filter 'FullyQualifiedName~Grace.CLI.Tests.DoctorCliTests'

dotnet build src/Grace.Authorization.Tests/Grace.Authorization.Tests.fsproj -c Release
dotnet test src/Grace.Authorization.Tests/Grace.Authorization.Tests.fsproj -c Release --no-build --filter 'FullyQualifiedName~Grace.Authorization.Tests.OpenApiRouteCoverageTests'

pwsh ./src/OpenAPI/generate-openapi-projections.ps1
pwsh ./sdk/scripts/generate-sdk-clients.ps1 -Mode Check
pwsh ./sdk/scripts/invoke-generator-matrix.ps1
pwsh ./src/OpenAPI/prove-openapi.ps1 -Check All -AllowPending

git diff --check
```

All matching Release builds completed with zero warnings and zero errors. Focused test totals were 6/6 Types, 34/34 authorization and route policy, 56/56 coordinator, notification, and upload-finalize behavior, 55/55 CLI catalog/current-state/topology behavior, 37/37 WDU branch and DirectoryVersion behavior, and 27/27 machine-output contracts.

## Accepted pending gates

The final OpenAPI proof retains four existing repository-wide pending gates:

- Thirteen Storage operations lack accepted parser-visible tags.
- `GET /openApi` lacks the accepted `400` and `500` pair.
- Stable SDK package export/import evidence remains pending.
- Portable protocol-vector verification remains pending.

TypeScript, Python, and Rust OpenAPI Generator output remains raw-client evidence behind facades. Kiota and NSwag remain rejected on existing schema-shape debt. Generation introduced no PR #700 lineage or unpreservable generated-tree collision.

## Residual validation gaps

- Hosted Aspire/Cosmos Emulator proof now covers exact six-container provisioning/readiness and repository create initialization. It does not cover the complete restart and injected publication-boundary matrix; focused in-memory and source-contract tests cover exact keys, reservation, canonical-first persistence, repair, replay, bounds, and manifest-last planning.
- No hosted HTTP/SignalR plus real immutable-storage test covers prepare, upload completion, change submission, one-use download, and best-effort wake as one external flow. Focused server-unit tests cover each owned seam and failure swallowing.
- GitHub `Validate` run `33233559385` failed on repaired head `23f3450b` because the integration harness did not provision the six Library containers before repository setup. A new exact-head run remains required after this closure correction.

Local `validate.ps1 -Fast` and `-Full` were not run because focused validation was sufficient and no documented escalation applied.

## Self-review

The complete base-to-candidate diff and the focused R1 repair diff were checked against the Issue #1038 outcome, DEC-039 through DEC-043, Product V1 remote-only non-goals, canonical-change-first effect order, RepositoryLibraryActor ownership, exact catalog staleness semantics, authorization, public contract propagation, generated freshness, proof truth, and owned paths. Reference, Cache, Diff, Work Item Attachment, local SQLite, filesystem publication, and Issue #1039 behavior remain unchanged. DirectoryVersion, Save/Branch, Watch, and WDU changes are limited to the accepted Library path boundary; their existing lifecycle, caller, target, completion, and publication semantics are unchanged. R1-01, R1-02, R1-03, R1-05, and R1-06 are shape-affecting and require refresh of Shape Review sections `Catalog configuration`, `Exact observed state and command`, `Accepted change and replay receipt`, `Control, pending work, and canonical commit`, `How the values fit together`, `Repository-scoped actor contract`, and `Additional source declarations and persisted shape`. R1-04 and R1-07 are shape-neutral.
