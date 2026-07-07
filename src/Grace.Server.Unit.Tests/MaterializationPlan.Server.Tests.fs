namespace Grace.Server.Unit.Tests

open Grace.Actors.DirectoryVersion
open Grace.Server
open Grace.Shared
open Grace.Shared.Parameters.Materialization
open Grace.Types.Common
open Grace.Types.MaterializationPlan
open NUnit.Framework
open System
open System.Reflection
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
