namespace Grace.Server.Unit.Tests

open Grace.Actors.DirectoryVersion
open Grace.Server
open Grace.Shared
open Grace.Shared.Parameters.Materialization
open Grace.Types.Branch
open Grace.Types.Common
open Grace.Types.DirectoryVersion
open Grace.Types.MaterializationPlan
open Grace.Types.Reference
open Grace.Types.Repository
open NUnit.Framework
open System
open System.Collections.Generic
open System.Reflection
open System.Text.Json
open System.Threading.Tasks

/// Covers Materialization Plan route helpers and SDK contract behavior without booting Aspire.
[<Parallelizable(ParallelScope.All)>]
type MaterializationPlanRouteTests() =

    let targetRootDirectoryVersionId = DirectoryVersionId.Parse "11111111-1111-1111-1111-111111111111"
    let repositoryId = RepositoryId.Parse "22222222-2222-2222-2222-222222222222"
    let explicitBranchId = BranchId.Parse "33333333-3333-3333-3333-333333333333"
    let defaultBranchId = BranchId.Parse "44444444-4444-4444-4444-444444444444"
    let referenceId = ReferenceId.Parse "55555555-5555-5555-5555-555555555555"
    let ownerId = OwnerId.Parse "66666666-6666-6666-6666-666666666666"
    let organizationId = OrganizationId.Parse "77777777-7777-7777-7777-777777777777"
    let defaultBranchName = BranchName Constants.InitialBranchName
    let explicitBranchName = BranchName "feature"
    let correlationId = "corr-materialization-plan"

    /// Builds a Direct/Bypass request for a resolved immutable root.
    let directRootRequest () =
        MaterializationPlanRequest.Create(
            MaterializationTargetSelector.ForDirectoryVersion targetRootDirectoryVersionId,
            MaterializationExecutionMode.Direct,
            MaterializationCacheSelection.Bypass,
            [
                MaterializationArtifactKind.DirectoryVersionZip
                MaterializationArtifactKind.RecursiveDirectoryMetadata
            ]
        )

    /// Builds the two V1 root artifact descriptors without using object storage.
    let rootArtifacts () =
        [|
            MaterializationArtifactDescriptor.DirectoryVersionZip(
                targetRootDirectoryVersionId,
                123L,
                Some(Sha256Hash "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
                Some(Blake3Hash "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
                Some(MaterializationArtifactSource.Direct "https://example.invalid/root.zip")
            )
            MaterializationArtifactDescriptor.RecursiveDirectoryMetadata(
                targetRootDirectoryVersionId,
                456L,
                Some(Sha256Hash "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"),
                Some(Blake3Hash "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"),
                Some(MaterializationArtifactSource.Direct "https://example.invalid/root.msgpack")
            )
        |]

    /// Builds projection evidence for the helper seam without requiring object storage.
    let projectionEvidence artifactKind size source =
        {
            ArtifactKind = artifactKind
            SizeInBytes = size
            Sha256Hash = Some(Sha256Hash "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
            Blake3Hash = Some(Blake3Hash "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
            Source = source
        }

    /// Builds a DirectoryVersion contract value for selector-scope validation.
    let directoryVersion directoryVersionId directoryRepositoryId (relativePath: string) =
        Grace.Types.Common.DirectoryVersion.Create
            directoryVersionId
            ownerId
            organizationId
            directoryRepositoryId
            (RelativePath relativePath)
            (Sha256Hash "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee")
            (List<DirectoryVersionId>())
            (List<FileVersion>())
            0L

    /// Builds a DirectoryVersionDto around the supplied root directory contract.
    let directoryVersionDto directoryVersionId directoryRepositoryId relativePath =
        { DirectoryVersionDto.Default with DirectoryVersion = directoryVersion directoryVersionId directoryRepositoryId relativePath }

    /// Builds a repository DTO with a current default branch name.
    let repository defaultBranchName =
        { RepositoryDto.Default with RepositoryId = repositoryId; OwnerId = ownerId; OrganizationId = organizationId; DefaultBranchName = defaultBranchName }

    /// Builds a reference DTO that points at one root directory version.
    let reference referenceId branchId directoryVersionId =
        { ReferenceDto.Default with
            ReferenceId = referenceId
            OwnerId = ownerId
            OrganizationId = organizationId
            RepositoryId = repositoryId
            BranchId = branchId
            DirectoryId = directoryVersionId
        }

    /// Builds a branch DTO with its latest reference already recomputed by the actor.
    let branch branchId branchName latestReference =
        { BranchDto.Default with
            BranchId = branchId
            BranchName = branchName
            OwnerId = ownerId
            OrganizationId = organizationId
            RepositoryId = repositoryId
            LatestReference = latestReference
            ShouldRecomputeLatestReferences = false
        }

    /// Ensures projection evidence becomes the same descriptors consumed by the route planner.
    let ensureProjectionArtifacts zipSource metadataSource =
        ensureRequiredProjectionArtifactsWith
            targetRootDirectoryVersionId
            (fun () -> Task.FromResult(Ok(projectionEvidence MaterializationArtifactKind.DirectoryVersionZip 123L zipSource)))
            (fun () -> Task.FromResult(Ok(projectionEvidence MaterializationArtifactKind.RecursiveDirectoryMetadata 456L metadataSource)))
            correlationId

    /// Asserts an error contains the expected message fragment.
    let assertErrorContains expected (result: Result<'T, GraceError>) =
        match result with
        | Ok _ -> Assert.Fail($"Expected an error containing '{expected}'.")
        | Error error -> Assert.That(error.Error, Does.Contain(expected))

    /// Asserts the route-classification helper maps a failure to retryable server status handling.
    let assertServerProjectionFailureContains expected (result: Result<'T, Materialization.DirectPlanFailure>) =
        match result with
        | Ok _ -> Assert.Fail($"Expected a server projection failure containing '{expected}'.")
        | Error (Materialization.ClientPlanValidation error) ->
            Assert.Fail($"Expected a retryable server projection failure, but got client validation '{error.Error}'.")
        | Error (Materialization.ServerProjectionFailure error) -> Assert.That(error.Error, Does.Contain(expected))

    /// Verifies real projection descriptors with Direct sources satisfy the Direct/Bypass route validation path.
    [<Test>]
    member _.DirectRootRequestAcceptsDirectProjectionHelperDescriptors() =
        task {
            let! artifactResult =
                ensureProjectionArtifacts
                    (Some(MaterializationArtifactSource.Direct "https://example.invalid/root.zip"))
                    (Some(MaterializationArtifactSource.Direct "https://example.invalid/root.msgpack"))

            let artifacts =
                match artifactResult with
                | Error error ->
                    Assert.Fail(error.Error)
                    Array.empty
                | Ok descriptors -> descriptors

            let! result =
                Materialization.createDirectPlanForResolvedRoot
                    (directRootRequest ())
                    targetRootDirectoryVersionId
                    (fun _ -> Task.FromResult(Ok artifacts))
                    correlationId

            match result with
            | Error error -> Assert.Fail(error.Error)
            | Ok plan ->
                Assert.That(plan.RequiredArtifacts, Has.Count.EqualTo(2))

                Assert.That(
                    plan.RequiredArtifacts
                    |> Seq.forall (fun descriptor ->
                        match descriptor.Source with
                        | Some source -> source.SourceKind = MaterializationArtifactSourceKind.DirectUri
                        | None -> false),
                    Is.True
                )
        }

    /// Verifies CacheOnly projection metadata would still fail the Direct/Bypass route validation invariant.
    [<Test>]
    member _.DirectRootRequestRejectsCacheOnlyProjectionHelperMetadata() =
        task {
            let! artifactResult =
                ensureProjectionArtifacts
                    (Some(MaterializationArtifactSource.Direct "https://example.invalid/root.zip"))
                    (Some(MaterializationArtifactSource.CacheOnly "recursive/root.msgpack"))

            let artifacts =
                match artifactResult with
                | Error error ->
                    Assert.Fail(error.Error)
                    Array.empty
                | Ok descriptors -> descriptors

            let! result =
                Materialization.createDirectPlanForResolvedRoot
                    (directRootRequest ())
                    targetRootDirectoryVersionId
                    (fun _ -> Task.FromResult(Ok artifacts))
                    correlationId

            assertErrorContains "Direct/Bypass plans must not require CacheEntry artifact sources." result
        }

    /// Verifies that a valid Direct root request produces a validated Materialization Plan.
    [<Test>]
    member _.DirectRootRequestBuildsValidatedPlanFromProjectionArtifacts() =
        task {
            let! result =
                Materialization.createDirectPlanForResolvedRoot
                    (directRootRequest ())
                    targetRootDirectoryVersionId
                    (fun _ -> Task.FromResult(Ok(rootArtifacts ())))
                    correlationId

            match result with
            | Error error -> Assert.Fail(error.Error)
            | Ok plan ->
                Assert.That(plan.TargetRootDirectoryVersionId, Is.EqualTo(targetRootDirectoryVersionId))
                Assert.That(plan.ExecutionMode, Is.EqualTo(MaterializationExecutionMode.Direct))
                Assert.That(plan.CacheSelection.SelectionKind, Is.EqualTo(MaterializationCacheSelectionKind.BypassCache))
                Assert.That(plan.RequiredArtifacts, Has.Count.EqualTo(2))

                match Validation.validatePlan plan with
                | Ok () -> ()
                | Error errors -> Assert.Fail(String.concat "; " errors)
        }

    /// Verifies malformed selectors are rejected before artifact projection work can run.
    [<Test>]
    member _.InvalidSelectorIsRejectedBeforeProjectionArtifactsRun() =
        task {
            let mutable projectionCalled = false

            let request =
                { directRootRequest () with
                    TargetSelector =
                        { MaterializationTargetSelector.ForDirectoryVersion targetRootDirectoryVersionId with
                            ReferenceId = Some(ReferenceId.Parse "33333333-3333-3333-3333-333333333333")
                        }
                }

            let! result =
                Materialization.createDirectPlanForResolvedRoot
                    request
                    targetRootDirectoryVersionId
                    (fun _ ->
                        projectionCalled <- true
                        Task.FromResult(Ok(rootArtifacts ())))
                    correlationId

            assertErrorContains "TargetSelector.ReferenceId must be empty for DirectoryVersionId selectors." result
            Assert.That(projectionCalled, Is.False)
        }

    /// Verifies route parameters require repository authority before selector materialization.
    [<Test>]
    member _.MissingRepositoryAuthorityIsRejected() =
        let parameters = PlanParameters()
        parameters.Request <- directRootRequest ()

        let result = Materialization.validatePlanParameters RepositoryId.Empty parameters correlationId

        assertErrorContains "RepositoryId is required" result

    /// Verifies null JSON bodies stay on the route validation path instead of escaping as server errors.
    [<Test>]
    member _.NullPlanBodyBindingReturnsBadRequestValidationError() =
        task {
            let! result = Materialization.bindPlanParametersWith (fun () -> Task.FromResult(Unchecked.defaultof<PlanParameters>)) correlationId

            assertErrorContains "Materialization Plan parameters are required." result
        }

    /// Verifies JSON binding failures such as unknown string enums are reported as malformed client bodies.
    [<Test>]
    member _.InvalidPlanBodyBindingReturnsBadRequestValidationError() =
        task {
            let! result =
                Materialization.bindPlanParametersWith
                    (fun () -> raise (JsonException("The JSON value could not be converted to MaterializationExecutionMode.")))
                    correlationId

            assertErrorContains "Materialization Plan request body is invalid." result
        }

    /// Verifies hidden and missing DirectoryVersion selectors share the same public error.
    [<Test>]
    member _.DirectoryVersionSelectorDoesNotDistinguishMissingFromCrossRepository() =
        let hiddenDirectoryVersion =
            directoryVersion targetRootDirectoryVersionId (RepositoryId.Parse "66666666-6666-6666-6666-666666666666") Constants.RootDirectoryPath

        let missingResult = Materialization.validateDirectoryVersionSelectorScope repositoryId Grace.Types.Common.DirectoryVersion.Default correlationId

        let hiddenResult = Materialization.validateDirectoryVersionSelectorScope repositoryId hiddenDirectoryVersion correlationId

        assertErrorContains Materialization.directoryVersionSelectorNotFoundMessage missingResult
        assertErrorContains Materialization.directoryVersionSelectorNotFoundMessage hiddenResult

        match missingResult, hiddenResult with
        | Error missingError, Error hiddenError -> Assert.That(hiddenError.Error, Is.EqualTo(missingError.Error))
        | _ -> Assert.Fail("Expected missing and hidden DirectoryVersion selectors to fail.")

    /// Verifies authorized non-root DirectoryVersion selectors remain supported for scoped Direct plans.
    [<Test>]
    member _.DirectoryVersionSelectorAcceptsPathScopedVersionAfterRepositoryScope() =
        let pathScopedDirectoryVersion = directoryVersion targetRootDirectoryVersionId repositoryId "src"

        let result = Materialization.validateDirectoryVersionSelectorScope repositoryId pathScopedDirectoryVersion correlationId

        match result with
        | Error error -> Assert.Fail(error.Error)
        | Ok () -> ()

    /// Verifies missing selector actor results keep the public-safe 400 message.
    [<Test>]
    member _.DirectoryVersionSelectorActorNotFoundUsesPublicSelectorError() =
        task {
            let! result =
                Materialization.resolveDirectoryVersionSelectorWith
                    repositoryId
                    targetRootDirectoryVersionId
                    (fun () -> raise (KeyNotFoundException("directory version was not found")))
                    correlationId

            assertErrorContains Materialization.directoryVersionSelectorNotFoundMessage result
        }

    /// Verifies missing repository state is rejected before selector resolution.
    [<Test>]
    member _.RepositorySelectorRejectsMissingRepositoryBeforeMovingSelectors() =
        task {
            let! result =
                Materialization.resolveRepositorySelectorWith
                    ownerId
                    organizationId
                    repositoryId
                    (fun () -> Task.FromResult RepositoryDto.Default)
                    correlationId

            assertErrorContains Materialization.repositorySelectorNotFoundMessage result
        }

    /// Verifies repository selectors must match the authorized owner and organization, not only the requested repository id.
    [<Test>]
    member _.RepositorySelectorRejectsCrossOwnerRepositoryBeforeMovingSelectors() =
        task {
            let foreignOwnerRepository = { repository defaultBranchName with OwnerId = OwnerId.Parse "88888888-8888-8888-8888-888888888888" }

            let! result =
                Materialization.resolveRepositorySelectorWith
                    ownerId
                    organizationId
                    repositoryId
                    (fun () -> Task.FromResult foreignOwnerRepository)
                    correlationId

            assertErrorContains Materialization.repositorySelectorNotFoundMessage result
        }

    /// Verifies explicit ReferenceId selectors resolve server-side to one immutable root DirectoryVersionId.
    [<Test>]
    member _.ReferenceSelectorResolvesToImmutableDirectoryVersionRoot() =
        task {
            let! result =
                Materialization.resolveReferenceSelectorWith
                    repositoryId
                    referenceId
                    (fun () -> Task.FromResult(reference referenceId explicitBranchId targetRootDirectoryVersionId))
                    (fun directoryVersionId -> Task.FromResult(directoryVersionDto directoryVersionId repositoryId Constants.RootDirectoryPath))
                    correlationId

            match result with
            | Error error -> Assert.Fail(error.Error)
            | Ok resolvedRoot -> Assert.That(resolvedRoot, Is.EqualTo(targetRootDirectoryVersionId))
        }

    /// Verifies missing ReferenceId selectors use an intentional public selector error.
    [<Test>]
    member _.ReferenceSelectorRejectsMissingReference() =
        task {
            let! result =
                Materialization.resolveReferenceSelectorWith
                    repositoryId
                    referenceId
                    (fun () -> Task.FromResult ReferenceDto.Default)
                    (fun directoryVersionId -> Task.FromResult(directoryVersionDto directoryVersionId repositoryId Constants.RootDirectoryPath))
                    correlationId

            assertErrorContains Materialization.referenceSelectorNotFoundMessage result
        }

    /// Verifies ReferenceType selectors resolve the latest matching branch reference at plan time.
    [<Test>]
    member _.ReferenceTypeSelectorResolvesLatestMatchingReferenceRoot() =
        task {
            let olderReference =
                { reference
                      (ReferenceId.Parse "88888888-8888-8888-8888-888888888888")
                      explicitBranchId
                      (DirectoryVersionId.Parse "99999999-9999-9999-9999-999999999999") with
                    ReferenceType = ReferenceType.Promotion
                    CreatedAt = NodaTime.Instant.FromUtc(2026, 7, 1, 12, 0)
                }

            let latestReference =
                { reference referenceId explicitBranchId targetRootDirectoryVersionId with
                    ReferenceType = ReferenceType.Promotion
                    CreatedAt = NodaTime.Instant.FromUtc(2026, 7, 2, 12, 0)
                }

            let! result =
                Materialization.resolveReferenceTypeSelectorWith
                    repositoryId
                    None
                    (Some explicitBranchName)
                    ReferenceType.Promotion
                    (fun _ -> Task.FromResult(Some explicitBranchId))
                    (fun branchId -> Task.FromResult(branch branchId explicitBranchName latestReference))
                    (fun _ -> Task.FromResult(Some latestReference))
                    (fun directoryVersionId -> Task.FromResult(directoryVersionDto directoryVersionId repositoryId Constants.RootDirectoryPath))
                    correlationId

            match result with
            | Error error -> Assert.Fail(error.Error)
            | Ok resolvedRoot -> Assert.That(resolvedRoot, Is.EqualTo(targetRootDirectoryVersionId))
        }

    /// Verifies ReferenceType selectors fail before planning when the branch has no matching reference.
    [<Test>]
    member _.ReferenceTypeSelectorRejectsMissingMatchingReference() =
        task {
            let! result =
                Materialization.resolveReferenceTypeSelectorWith
                    repositoryId
                    None
                    (Some explicitBranchName)
                    ReferenceType.Promotion
                    (fun _ -> Task.FromResult(Some explicitBranchId))
                    (fun branchId -> Task.FromResult(branch branchId explicitBranchName ReferenceDto.Default))
                    (fun _ -> Task.FromResult None)
                    (fun directoryVersionId -> Task.FromResult(directoryVersionDto directoryVersionId repositoryId Constants.RootDirectoryPath))
                    correlationId

            assertErrorContains Materialization.referenceTypeSelectorNotFoundMessage result
        }

    /// Verifies ReferenceType selectors carrying BranchId do not resolve through a stale or reused branch name.
    [<Test>]
    member _.ReferenceTypeSelectorPreservesBranchIdIdentityWithoutNameLookup() =
        task {
            let selectedReference =
                { reference referenceId explicitBranchId targetRootDirectoryVersionId with
                    ReferenceType = ReferenceType.Promotion
                    CreatedAt = NodaTime.Instant.FromUtc(2026, 7, 2, 12, 0)
                }

            let mutable nameLookupWasUsed = false

            let! result =
                Materialization.resolveReferenceTypeSelectorWith
                    repositoryId
                    (Some explicitBranchId)
                    None
                    ReferenceType.Promotion
                    (fun _ ->
                        nameLookupWasUsed <- true
                        Task.FromResult(Some defaultBranchId))
                    (fun branchId -> Task.FromResult(branch branchId (BranchName "renamed-after-cli-lookup") selectedReference))
                    (fun _ -> Task.FromResult(Some selectedReference))
                    (fun directoryVersionId -> Task.FromResult(directoryVersionDto directoryVersionId repositoryId Constants.RootDirectoryPath))
                    correlationId

            match result with
            | Error error -> Assert.Fail(error.Error)
            | Ok resolvedRoot ->
                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(resolvedRoot, Is.EqualTo(targetRootDirectoryVersionId))
                        Assert.That(nameLookupWasUsed, Is.False))
                )
        }

    /// Verifies ReferenceType selectors skip deleted candidates before returning the newest live reference.
    [<Test>]
    member _.ReferenceTypeSelectorSkipsDeletedNewestReference() =
        task {
            let liveRoot = DirectoryVersionId.Parse "99999999-9999-9999-9999-999999999999"

            let deletedNewest =
                { reference referenceId explicitBranchId targetRootDirectoryVersionId with
                    ReferenceType = ReferenceType.Promotion
                    CreatedAt = NodaTime.Instant.FromUtc(2026, 7, 3, 12, 0)
                    DeletedAt = Some(NodaTime.Instant.FromUtc(2026, 7, 4, 12, 0))
                }

            let liveOlder =
                { reference (ReferenceId.Parse "88888888-8888-8888-8888-888888888888") explicitBranchId liveRoot with
                    ReferenceType = ReferenceType.Promotion
                    CreatedAt = NodaTime.Instant.FromUtc(2026, 7, 2, 12, 0)
                }

            let! result =
                Materialization.resolveReferenceTypeSelectorWith
                    repositoryId
                    None
                    (Some explicitBranchName)
                    ReferenceType.Promotion
                    (fun _ -> Task.FromResult(Some explicitBranchId))
                    (fun branchId -> Task.FromResult(branch branchId explicitBranchName liveOlder))
                    (fun _ -> Task.FromResult(Some liveOlder))
                    (fun directoryVersionId -> Task.FromResult(directoryVersionDto directoryVersionId repositoryId Constants.RootDirectoryPath))
                    correlationId

            match result with
            | Error error -> Assert.Fail(error.Error)
            | Ok resolvedRoot -> Assert.That(resolvedRoot, Is.EqualTo(liveRoot))
        }

    /// Verifies ReferenceType selectors rely on storage paging to skip more deleted references than one list window.
    [<Test>]
    member _.ReferenceTypeSelectorSkipsBeyondDeletedCandidateWindow() =
        task {
            let liveRoot = DirectoryVersionId.Parse "99999999-9999-9999-9999-999999999999"

            let deletedCandidateCount = 51

            let deletedCandidates =
                Array.init deletedCandidateCount (fun index ->
                    let referenceOrdinal = index + 1

                    { reference
                          (ReferenceId.Parse($"aaaaaaaa-aaaa-aaaa-aaaa-{referenceOrdinal.ToString().PadLeft(12, '0')}"))
                          explicitBranchId
                          targetRootDirectoryVersionId with
                        ReferenceType = ReferenceType.Promotion
                        CreatedAt =
                            NodaTime
                                .Instant
                                .FromUtc(2026, 7, 3, 12, 0)
                                .Minus(NodaTime.Duration.FromMinutes(int64 referenceOrdinal))
                        DeletedAt = Some(NodaTime.Instant.FromUtc(2026, 7, 4, 12, 0))
                    })

            let liveOlder =
                { reference (ReferenceId.Parse "88888888-8888-8888-8888-888888888888") explicitBranchId liveRoot with
                    ReferenceType = ReferenceType.Promotion
                    CreatedAt = NodaTime.Instant.FromUtc(2026, 7, 1, 12, 0)
                }

            let storageOrderedCandidates = Array.append deletedCandidates [| liveOlder |]
            let mutable latestLiveLookupSawDeletedWindow = false

            let! result =
                Materialization.resolveReferenceTypeSelectorWith
                    repositoryId
                    None
                    (Some explicitBranchName)
                    ReferenceType.Promotion
                    (fun _ -> Task.FromResult(Some explicitBranchId))
                    (fun branchId -> Task.FromResult(branch branchId explicitBranchName liveOlder))
                    (fun _ ->
                        latestLiveLookupSawDeletedWindow <-
                            storageOrderedCandidates
                            |> Array.take 50
                            |> Array.forall (fun reference -> reference.DeletedAt.IsSome)

                        storageOrderedCandidates
                        |> Array.tryFind (fun reference -> reference.DeletedAt.IsNone)
                        |> Task.FromResult)
                    (fun directoryVersionId -> Task.FromResult(directoryVersionDto directoryVersionId repositoryId Constants.RootDirectoryPath))
                    correlationId

            match result with
            | Error error -> Assert.Fail(error.Error)
            | Ok resolvedRoot ->
                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(resolvedRoot, Is.EqualTo(liveRoot))
                        Assert.That(latestLiveLookupSawDeletedWindow, Is.True))
                )
        }

    /// Verifies ReferenceType selectors choose by creation order instead of mutable UpdatedAt churn.
    [<Test>]
    member _.ReferenceTypeSelectorUsesCreatedAtInsteadOfUpdatedAt() =
        task {
            let olderRoot = DirectoryVersionId.Parse "99999999-9999-9999-9999-999999999999"

            let updatedOlder =
                { reference (ReferenceId.Parse "88888888-8888-8888-8888-888888888888") explicitBranchId olderRoot with
                    ReferenceType = ReferenceType.Promotion
                    CreatedAt = NodaTime.Instant.FromUtc(2026, 7, 1, 12, 0)
                    UpdatedAt = Some(NodaTime.Instant.FromUtc(2026, 7, 4, 12, 0))
                }

            let createdNewer =
                { reference referenceId explicitBranchId targetRootDirectoryVersionId with
                    ReferenceType = ReferenceType.Promotion
                    CreatedAt = NodaTime.Instant.FromUtc(2026, 7, 2, 12, 0)
                }

            let! result =
                Materialization.resolveReferenceTypeSelectorWith
                    repositoryId
                    None
                    (Some explicitBranchName)
                    ReferenceType.Promotion
                    (fun _ -> Task.FromResult(Some explicitBranchId))
                    (fun branchId -> Task.FromResult(branch branchId explicitBranchName createdNewer))
                    (fun _ -> Task.FromResult(Some createdNewer))
                    (fun directoryVersionId -> Task.FromResult(directoryVersionDto directoryVersionId repositoryId Constants.RootDirectoryPath))
                    correlationId

            match result with
            | Error error -> Assert.Fail(error.Error)
            | Ok resolvedRoot -> Assert.That(resolvedRoot, Is.EqualTo(targetRootDirectoryVersionId))
        }

    /// Verifies explicit branch selectors resolve the current branch tip to one immutable root.
    [<Test>]
    member _.BranchNameSelectorResolvesLatestReferenceRoot() =
        task {
            let latestReference = reference referenceId explicitBranchId targetRootDirectoryVersionId

            let! result =
                Materialization.resolveBranchNameSelectorWith
                    repositoryId
                    explicitBranchName
                    (fun () -> Task.FromResult(Some explicitBranchId))
                    (fun branchId -> Task.FromResult(branch branchId explicitBranchName latestReference))
                    (fun _ -> Task.FromResult latestReference)
                    (fun directoryVersionId -> Task.FromResult(directoryVersionDto directoryVersionId repositoryId Constants.RootDirectoryPath))
                    correlationId

            match result with
            | Error error -> Assert.Fail(error.Error)
            | Ok resolvedRoot -> Assert.That(resolvedRoot, Is.EqualTo(targetRootDirectoryVersionId))
        }

    /// Verifies durable branch-name resolver casing variants survive BranchDto revalidation.
    [<Test>]
    member _.BranchNameSelectorAcceptsResolverCasingVariant() =
        task {
            let requestedBranchName = BranchName "Feature"
            let latestReference = reference referenceId explicitBranchId targetRootDirectoryVersionId

            let! result =
                Materialization.resolveBranchNameSelectorWith
                    repositoryId
                    requestedBranchName
                    (fun () -> Task.FromResult(Some explicitBranchId))
                    (fun branchId -> Task.FromResult(branch branchId explicitBranchName latestReference))
                    (fun _ -> Task.FromResult latestReference)
                    (fun directoryVersionId -> Task.FromResult(directoryVersionDto directoryVersionId repositoryId Constants.RootDirectoryPath))
                    correlationId

            match result with
            | Error error -> Assert.Fail(error.Error)
            | Ok resolvedRoot -> Assert.That(resolvedRoot, Is.EqualTo(targetRootDirectoryVersionId))
        }

    /// Verifies default branch selectors use the repository's current default branch name and resolve its latest root.
    [<Test>]
    member _.DefaultBranchSelectorResolvesRepositoryDefaultBranchRoot() =
        task {
            let repositoryDto = repository defaultBranchName
            let latestReference = reference referenceId defaultBranchId targetRootDirectoryVersionId

            match Materialization.validateRepositorySelectorScope ownerId organizationId repositoryId repositoryDto correlationId with
            | Error error -> Assert.Fail(error.Error)
            | Ok () -> ()

            let! result =
                Materialization.resolveBranchNameSelectorWith
                    repositoryId
                    repositoryDto.DefaultBranchName
                    (fun () -> Task.FromResult(Some defaultBranchId))
                    (fun branchId -> Task.FromResult(branch branchId repositoryDto.DefaultBranchName latestReference))
                    (fun _ -> Task.FromResult latestReference)
                    (fun directoryVersionId -> Task.FromResult(directoryVersionDto directoryVersionId repositoryId Constants.RootDirectoryPath))
                    correlationId

            match result with
            | Error error -> Assert.Fail(error.Error)
            | Ok resolvedRoot -> Assert.That(resolvedRoot, Is.EqualTo(targetRootDirectoryVersionId))
        }

    /// Verifies branch-name misses return an intentional selector error instead of falling back to cache or client resolution.
    [<Test>]
    member _.BranchNameSelectorRejectsMissingBranch() =
        task {
            let! result =
                Materialization.resolveBranchNameSelectorWith
                    repositoryId
                    explicitBranchName
                    (fun () -> Task.FromResult(None))
                    (fun branchId -> Task.FromResult(branch branchId explicitBranchName ReferenceDto.Default))
                    (fun _ -> Task.FromResult ReferenceDto.Default)
                    (fun directoryVersionId -> Task.FromResult(directoryVersionDto directoryVersionId repositoryId Constants.RootDirectoryPath))
                    correlationId

            assertErrorContains Materialization.branchNameSelectorNotFoundMessage result
        }

    /// Verifies branch-name selectors use the durable resolver seam before loading branch state.
    [<Test>]
    member _.BranchNameSelectorUsesResolverCallbackOnColdBranchNameCache() =
        task {
            let latestReference = reference referenceId explicitBranchId targetRootDirectoryVersionId
            let mutable resolverCalled = false

            let! result =
                Materialization.resolveBranchNameSelectorWith
                    repositoryId
                    explicitBranchName
                    (fun () ->
                        resolverCalled <- true
                        Task.FromResult(Some explicitBranchId))
                    (fun branchId -> Task.FromResult(branch branchId explicitBranchName latestReference))
                    (fun _ -> Task.FromResult latestReference)
                    (fun directoryVersionId -> Task.FromResult(directoryVersionDto directoryVersionId repositoryId Constants.RootDirectoryPath))
                    correlationId

            match result with
            | Error error -> Assert.Fail(error.Error)
            | Ok resolvedRoot -> Assert.That(resolvedRoot, Is.EqualTo(targetRootDirectoryVersionId))

            Assert.That(resolverCalled, Is.True)
        }

    /// Verifies stale branch-name index entries use the same public selector error as missing branch names.
    [<Test>]
    member _.BranchNameSelectorRejectsStaleBranchNameIndex() =
        task {
            let! result =
                Materialization.resolveBranchNameSelectorWith
                    repositoryId
                    explicitBranchName
                    (fun () -> Task.FromResult(Some explicitBranchId))
                    (fun _ -> raise (KeyNotFoundException("branch actor was not found")))
                    (fun _ -> Task.FromResult ReferenceDto.Default)
                    (fun directoryVersionId -> Task.FromResult(directoryVersionDto directoryVersionId repositoryId Constants.RootDirectoryPath))
                    correlationId

            assertErrorContains Materialization.branchNameSelectorNotFoundMessage result
        }

    /// Verifies branch-name index drift cannot trust branch state that reports another durable branch id.
    [<Test>]
    member _.BranchNameSelectorRejectsBranchDtoIdMismatch() =
        task {
            let latestReference = reference referenceId defaultBranchId targetRootDirectoryVersionId

            let! result =
                Materialization.resolveBranchNameSelectorWith
                    repositoryId
                    explicitBranchName
                    (fun () -> Task.FromResult(Some explicitBranchId))
                    (fun _ -> Task.FromResult(branch defaultBranchId explicitBranchName latestReference))
                    (fun _ -> Task.FromResult latestReference)
                    (fun directoryVersionId -> Task.FromResult(directoryVersionDto directoryVersionId repositoryId Constants.RootDirectoryPath))
                    correlationId

            assertErrorContains Materialization.branchNameSelectorNotFoundMessage result
        }

    /// Verifies branch-name index drift cannot materialize an unintended branch root.
    [<Test>]
    member _.BranchNameSelectorRejectsMismatchedBranchName() =
        task {
            let latestReference = reference referenceId explicitBranchId targetRootDirectoryVersionId

            let! result =
                Materialization.resolveBranchNameSelectorWith
                    repositoryId
                    explicitBranchName
                    (fun () -> Task.FromResult(Some explicitBranchId))
                    (fun branchId -> Task.FromResult(branch branchId (BranchName "other") latestReference))
                    (fun _ -> Task.FromResult latestReference)
                    (fun directoryVersionId -> Task.FromResult(directoryVersionDto directoryVersionId repositoryId Constants.RootDirectoryPath))
                    correlationId

            assertErrorContains Materialization.branchNameSelectorNotFoundMessage result
        }

    /// Verifies a branch with no latest reference never produces a partial or cache-backed plan.
    [<Test>]
    member _.BranchNameSelectorRejectsMissingTipReference() =
        task {
            let! result =
                Materialization.resolveBranchNameSelectorWith
                    repositoryId
                    explicitBranchName
                    (fun () -> Task.FromResult(Some explicitBranchId))
                    (fun branchId -> Task.FromResult(branch branchId explicitBranchName ReferenceDto.Default))
                    (fun _ -> Task.FromResult ReferenceDto.Default)
                    (fun directoryVersionId -> Task.FromResult(directoryVersionDto directoryVersionId repositoryId Constants.RootDirectoryPath))
                    correlationId

            assertErrorContains Materialization.branchTipSelectorNotFoundMessage result
        }

    /// Verifies branch-name selectors re-read the live reference before trusting a branch snapshot's root directory.
    [<Test>]
    member _.BranchNameSelectorRejectsDeletedLiveTipReference() =
        task {
            let snapshotReference = reference referenceId explicitBranchId targetRootDirectoryVersionId

            let deletedLiveReference = { snapshotReference with DeletedAt = Some(NodaTime.Instant.FromUnixTimeSeconds 1L) }

            let! result =
                Materialization.resolveBranchNameSelectorWith
                    repositoryId
                    explicitBranchName
                    (fun () -> Task.FromResult(Some explicitBranchId))
                    (fun branchId -> Task.FromResult(branch branchId explicitBranchName snapshotReference))
                    (fun _ -> Task.FromResult deletedLiveReference)
                    (fun directoryVersionId -> Task.FromResult(directoryVersionDto directoryVersionId repositoryId Constants.RootDirectoryPath))
                    correlationId

            assertErrorContains Materialization.branchTipSelectorNotFoundMessage result
        }

    /// Verifies a stale branch latest-reference snapshot cannot borrow another branch's live tip.
    [<Test>]
    member _.BranchNameSelectorRejectsLiveTipReferenceFromAnotherBranch() =
        task {
            let snapshotReference = reference referenceId explicitBranchId targetRootDirectoryVersionId
            let otherBranchLiveReference = reference referenceId defaultBranchId targetRootDirectoryVersionId

            let! result =
                Materialization.resolveBranchNameSelectorWith
                    repositoryId
                    explicitBranchName
                    (fun () -> Task.FromResult(Some explicitBranchId))
                    (fun branchId -> Task.FromResult(branch branchId explicitBranchName snapshotReference))
                    (fun _ -> Task.FromResult otherBranchLiveReference)
                    (fun directoryVersionId -> Task.FromResult(directoryVersionDto directoryVersionId repositoryId Constants.RootDirectoryPath))
                    correlationId

            assertErrorContains Materialization.branchTipSelectorNotFoundMessage result
        }

    /// Verifies actor, proxy, storage, or runtime selector failures are not collapsed into public 400s.
    [<Test>]
    member _.DirectoryVersionSelectorRuntimeFailureEscapesForRetryableServerStatus() =
        let ex =
            Assert.ThrowsAsync<InvalidOperationException>(
                Func<Task> (fun () ->
                    Materialization.resolveDirectoryVersionSelectorWith
                        repositoryId
                        targetRootDirectoryVersionId
                        (fun () -> raise (InvalidOperationException("directory actor proxy unavailable")))
                        correlationId
                    :> Task)
            )

        Assert.That(ex.Message, Does.Contain("directory actor proxy unavailable"))

    /// Verifies path-scoped artifact requests stay unsupported until a later materialization slice owns them.
    [<Test>]
    member _.PathScopedArtifactRequestIsRejectedBeforeProjectionArtifactsRun() =
        task {
            let mutable projectionCalled = false

            let request =
                MaterializationPlanRequest.Create(
                    MaterializationTargetSelector.ForDirectoryVersion targetRootDirectoryVersionId,
                    MaterializationExecutionMode.Direct,
                    MaterializationCacheSelection.Bypass,
                    [
                        MaterializationArtifactKind.DirectoryVersionZip
                        MaterializationArtifactKind.RecursiveDirectoryMetadata
                        MaterializationArtifactKind.WholeFileContent
                    ]
                )

            let! result =
                Materialization.createDirectPlanForResolvedRoot
                    request
                    targetRootDirectoryVersionId
                    (fun _ ->
                        projectionCalled <- true
                        Task.FromResult(Ok(rootArtifacts ())))
                    correlationId

            assertErrorContains "path-scoped artifact kinds are not supported" result
            Assert.That(projectionCalled, Is.False)
        }

    /// Verifies cache-backed modes stay explicitly unsupported until cache selection is implemented.
    [<Test>]
    member _.CachePreferredRequestIsRejectedBeforeProjectionArtifactsRun() =
        task {
            let mutable projectionCalled = false

            let request =
                MaterializationPlanRequest.Create(
                    MaterializationTargetSelector.ForDirectoryVersion targetRootDirectoryVersionId,
                    MaterializationExecutionMode.CachePreferred,
                    MaterializationCacheSelection.Preferred,
                    [
                        MaterializationArtifactKind.DirectoryVersionZip
                        MaterializationArtifactKind.RecursiveDirectoryMetadata
                    ]
                )

            let! result =
                Materialization.createDirectPlanForResolvedRoot
                    request
                    targetRootDirectoryVersionId
                    (fun _ ->
                        projectionCalled <- true
                        Task.FromResult(Ok(rootArtifacts ())))
                    correlationId

            assertErrorContains "Cache materialization plan selection is not implemented" result
            Assert.That(projectionCalled, Is.False)
        }

    /// Verifies projection failures do not return partial Materialization Plans.
    [<Test>]
    member _.ProjectionFailureDoesNotReturnPartialPlan() =
        task {
            let! result =
                Materialization.createDirectPlanForResolvedRoot
                    (directRootRequest ())
                    targetRootDirectoryVersionId
                    (fun _ -> Task.FromResult(Error(GraceError.Create "projection failed" correlationId)))
                    correlationId

            assertErrorContains "projection failed" result
        }

    /// Verifies a projection descriptor for another root is rejected before a Direct plan is returned.
    [<Test>]
    member _.ProjectionRootMismatchRejectsPlanBeforeResponse() =
        task {
            let otherRootDirectoryVersionId = DirectoryVersionId.Parse "88888888-8888-8888-8888-888888888888"

            let mismatchedArtifacts =
                [|
                    MaterializationArtifactDescriptor.DirectoryVersionZip(
                        otherRootDirectoryVersionId,
                        123L,
                        Some(Sha256Hash "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
                        Some(Blake3Hash "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
                        Some(MaterializationArtifactSource.Direct "https://example.invalid/other.zip")
                    )
                    MaterializationArtifactDescriptor.RecursiveDirectoryMetadata(
                        targetRootDirectoryVersionId,
                        456L,
                        Some(Sha256Hash "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"),
                        Some(Blake3Hash "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"),
                        Some(MaterializationArtifactSource.Direct "https://example.invalid/root.msgpack")
                    )
                |]

            let! result =
                Materialization.createDirectPlanForResolvedRoot
                    (directRootRequest ())
                    targetRootDirectoryVersionId
                    (fun _ -> Task.FromResult(Ok mismatchedArtifacts))
                    correlationId

            assertErrorContains "Artifact TargetRootDirectoryVersionId must match the plan TargetRootDirectoryVersionId." result
        }

    /// Verifies valid Direct/Bypass requests with failed server-side projection map to the route's retryable 5xx path.
    [<Test>]
    member _.ProjectionFailureClassifiesAsRetryableServerFailure() =
        task {
            let! result =
                Materialization.createDirectPlanForResolvedRootWithFailureKind
                    (directRootRequest ())
                    targetRootDirectoryVersionId
                    (fun _ -> Task.FromResult(Error(GraceError.Create "projection failed" correlationId)))
                    correlationId

            assertServerProjectionFailureContains "projection failed" result
        }

    /// Verifies actor, storage, or artifact ensuring exceptions use the same retryable 5xx route classification.
    [<Test>]
    member _.ProjectionExceptionClassifiesAsRetryableServerFailure() =
        task {
            let! result =
                Materialization.createDirectPlanForResolvedRootWithFailureKind
                    (directRootRequest ())
                    targetRootDirectoryVersionId
                    (fun _ -> raise (InvalidOperationException("artifact store unavailable")))
                    correlationId

            assertServerProjectionFailureContains "Materialization Plan projection artifacts could not be ensured." result
        }

    /// Verifies client request-shape failures keep the route's 400 classification and never run projection.
    [<Test>]
    member _.PathScopedArtifactRequestClassifiesAsClientValidationBeforeProjectionArtifactsRun() =
        task {
            let mutable projectionCalled = false

            let request =
                MaterializationPlanRequest.Create(
                    MaterializationTargetSelector.ForDirectoryVersion targetRootDirectoryVersionId,
                    MaterializationExecutionMode.Direct,
                    MaterializationCacheSelection.Bypass,
                    [
                        MaterializationArtifactKind.DirectoryVersionZip
                        MaterializationArtifactKind.RecursiveDirectoryMetadata
                        MaterializationArtifactKind.WholeFileContent
                    ]
                )

            let! result =
                Materialization.createDirectPlanForResolvedRootWithFailureKind
                    request
                    targetRootDirectoryVersionId
                    (fun _ ->
                        projectionCalled <- true
                        Task.FromResult(Ok(rootArtifacts ())))
                    correlationId

            match result with
            | Ok _ -> Assert.Fail("Expected a path-scoped artifact request to fail client validation.")
            | Error (Materialization.ServerProjectionFailure error) ->
                Assert.Fail($"Expected client validation before projection, but got server projection failure '{error.Error}'.")
            | Error (Materialization.ClientPlanValidation error) ->
                Assert.That(error.Error, Does.Contain("path-scoped artifact kinds are not supported"))
                Assert.That(projectionCalled, Is.False)
        }

    /// Verifies the SDK exposes the route facade with the shared parameter contract.
    [<Test>]
    member _.SdkPlanMethodMatchesSharedParameterContract() =
        let parameterType = typeof<PlanParameters>
        let sdkType = typeof<Grace.SDK.AgentSession>.Assembly.GetType ("Grace.SDK.Materialization", throwOnError = false)

        Assert.That(sdkType, Is.Not.Null, "Grace.SDK.Materialization should be available as an SDK module.")

        let methodInfo = sdkType.GetMethod("Plan", BindingFlags.Public ||| BindingFlags.Static)

        Assert.That(methodInfo, Is.Not.Null, "Grace.SDK.Materialization.Plan should be a public SDK method.")

        let parameters = methodInfo.GetParameters()
        Assert.That(parameters, Has.Length.EqualTo(1))
        Assert.That(parameters[0].ParameterType, Is.EqualTo(parameterType))
