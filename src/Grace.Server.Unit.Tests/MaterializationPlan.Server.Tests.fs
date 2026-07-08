namespace Grace.Server.Unit.Tests

open Grace.Actors.DirectoryVersion
open Grace.Server
open Grace.Shared
open Grace.Shared.Parameters.Materialization
open Grace.Types.Common
open Grace.Types.MaterializationPlan
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
            (OwnerId.Parse "44444444-4444-4444-4444-444444444444")
            (OrganizationId.Parse "55555555-5555-5555-5555-555555555555")
            directoryRepositoryId
            (RelativePath relativePath)
            (Sha256Hash "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee")
            (List<DirectoryVersionId>())
            (List<FileVersion>())
            0L

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

    /// Verifies authorized non-root DirectoryVersion selectors keep the path-scoped validation error.
    [<Test>]
    member _.DirectoryVersionSelectorRejectsPathScopedVersionAfterRepositoryScope() =
        let pathScopedDirectoryVersion = directoryVersion targetRootDirectoryVersionId repositoryId "src"

        let result = Materialization.validateDirectoryVersionSelectorScope repositoryId pathScopedDirectoryVersion correlationId

        assertErrorContains "Path-scoped Materialization Plan selectors are not supported" result

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
