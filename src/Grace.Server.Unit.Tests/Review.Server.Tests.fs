namespace Grace.Server.Tests

open FsUnit
open Grace.Server
open Grace.Types.Queue
open Grace.Types.Review
open Grace.Shared.Parameters.Review
open Grace.Types.PromotionSet
open NUnit.Framework
open System
open System.Threading.Tasks

/// Covers review Projection behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type ReviewProjectionTests() =
    /// Constructs parameters fixtures used by the server unit review assertions.
    let createParameters (candidateId: string) =
        let parameters = ResolveCandidateIdentityParameters(CandidateId = candidateId)
        parameters.OwnerId <- Guid.NewGuid().ToString()
        parameters.OrganizationId <- Guid.NewGuid().ToString()
        parameters.RepositoryId <- Guid.NewGuid().ToString()
        parameters.CorrelationId <- Guid.NewGuid().ToString()
        parameters

    /// Verifies that resolve Candidate Identity Projection With Uses Direct Promotion Set Projection.
    [<Test>]
    member _.ResolveCandidateIdentityProjectionWithUsesDirectPromotionSetProjection() =
        let candidateId = Guid.NewGuid()
        let parameters = createParameters $"  {candidateId.ToString().ToUpperInvariant()}  "

        /// Resolves promotion Set through the same path exercised by the server unit review scenario.
        let resolvePromotionSet (promotionSetId: Guid) : Task<Grace.Types.PromotionSet.PromotionSetDto option> =
            task {
                let promotionSet = { PromotionSetDto.Default with PromotionSetId = promotionSetId }
                return Option.Some promotionSet
            }

        let resultTask = Review.resolveCandidateIdentityProjectionWith resolvePromotionSet parameters
        let result = resultTask.GetAwaiter().GetResult()

        match result with
        | Error error -> Assert.Fail($"Expected projection resolution to succeed, but received error: {error.Error}")
        | Ok projection ->
            Assert.That(projection.Identity.CandidateId, Is.EqualTo(candidateId.ToString()))
            Assert.That(projection.Identity.PromotionSetId, Is.EqualTo(candidateId.ToString()))
            Assert.That(projection.Identity.IdentityMode, Is.EqualTo(CandidateIdentityModes.DirectPromotionSetProjection))
            Assert.That(projection.Identity.Scope.OwnerId, Is.EqualTo(parameters.OwnerId))
            Assert.That(projection.Identity.Scope.OrganizationId, Is.EqualTo(parameters.OrganizationId))
            Assert.That(projection.Identity.Scope.RepositoryId, Is.EqualTo(parameters.RepositoryId))
            Assert.That(projection.SourceStates.Length, Is.EqualTo(1))
            Assert.That(projection.SourceStates[0].Section, Is.EqualTo("identity"))
            Assert.That(projection.SourceStates[0].SourceState, Is.EqualTo(ProjectionSourceStates.Authoritative))

    /// Verifies that resolve Candidate Identity Projection With Returns Deterministic Projection For Same Input.
    [<Test>]
    member _.ResolveCandidateIdentityProjectionWithReturnsDeterministicProjectionForSameInput() =
        let candidateId = Guid.NewGuid()
        let parameters = createParameters (candidateId.ToString())

        /// Resolves promotion Set through the same path exercised by the server unit review scenario.
        let resolvePromotionSet (promotionSetId: Guid) : Task<Grace.Types.PromotionSet.PromotionSetDto option> =
            task {
                let promotionSet = { PromotionSetDto.Default with PromotionSetId = promotionSetId }
                return Option.Some promotionSet
            }

        let firstResultTask = Review.resolveCandidateIdentityProjectionWith resolvePromotionSet parameters
        let firstResult = firstResultTask.GetAwaiter().GetResult()

        let secondResultTask = Review.resolveCandidateIdentityProjectionWith resolvePromotionSet parameters
        let secondResult = secondResultTask.GetAwaiter().GetResult()

        /// Extracts deterministic Shape from the scenario result so assertions stay focused on server unit review behavior.
        let getDeterministicShape (result: Result<CandidateIdentityProjectionResult, Grace.Types.Common.GraceError>) =
            match result with
            | Error error -> $"error:{error.Error}:{error.CorrelationId}:{error.Properties.Count}"
            | Ok projection ->
                String.Join(
                    "|",
                    [
                        projection.Identity.CandidateId
                        projection.Identity.PromotionSetId
                        projection.Identity.IdentityMode
                        projection.Identity.Scope.OwnerId
                        projection.Identity.Scope.OrganizationId
                        projection.Identity.Scope.RepositoryId
                        projection.SourceStates
                        |> List.map (fun source -> $"{source.Section}:{source.SourceState}:{source.Detail}")
                        |> String.concat ";"
                    ]
                )

        Assert.That(getDeterministicShape firstResult, Is.EqualTo(getDeterministicShape secondResult))

    /// Verifies that resolve Candidate Identity Projection With Rejects Invalid Candidate Id Without Lookup.
    [<Test>]
    member _.ResolveCandidateIdentityProjectionWithRejectsInvalidCandidateIdWithoutLookup() =
        let parameters = createParameters "  not-a-guid  "
        let mutable resolveCalls = 0

        /// Resolves promotion Set through the same path exercised by the server unit review scenario.
        let resolvePromotionSet (_: Guid) : Task<Grace.Types.PromotionSet.PromotionSetDto option> =
            task {
                resolveCalls <- resolveCalls + 1
                return Option.None
            }

        let resultTask = Review.resolveCandidateIdentityProjectionWith resolvePromotionSet parameters
        let result = resultTask.GetAwaiter().GetResult()

        match result with
        | Ok _ -> Assert.Fail("Expected invalid candidate id to return an error.")
        | Error error ->
            Assert.That(resolveCalls, Is.EqualTo(0))
            Assert.That(error.Error, Is.EqualTo("CandidateId must be a valid non-empty Guid."))
            Assert.That(error.CorrelationId, Is.EqualTo(parameters.CorrelationId))
            Assert.That(error.Properties.ContainsKey("NormalizedCandidateId"), Is.True)
            Assert.That(error.Properties["NormalizedCandidateId"], Is.EqualTo("not-a-guid"))

    /// Verifies that resolve Candidate Identity Projection With Returns Deterministic Not Found Error.
    [<Test>]
    member _.ResolveCandidateIdentityProjectionWithReturnsDeterministicNotFoundError() =
        let candidateId = Guid.NewGuid()
        let parameters = createParameters (candidateId.ToString())

        let resolvePromotionSet (_: Guid) : Task<Grace.Types.PromotionSet.PromotionSetDto option> = task { return Option.None }

        let resultTask = Review.resolveCandidateIdentityProjectionWith resolvePromotionSet parameters
        let result = resultTask.GetAwaiter().GetResult()

        match result with
        | Ok _ -> Assert.Fail("Expected missing candidate projection to return an error.")
        | Error error ->
            Assert.That(error.Error, Is.EqualTo($"Candidate '{candidateId}' was not found in repository scope."))
            Assert.That(error.CorrelationId, Is.EqualTo(parameters.CorrelationId))
            Assert.That(error.Properties.ContainsKey("RepositoryId"), Is.True)
            Assert.That(error.Properties["RepositoryId"], Is.EqualTo(parameters.RepositoryId))
            Assert.That(error.Properties["NormalizedCandidateId"], Is.EqualTo(candidateId.ToString()))

    /// Verifies that derive Candidate Required Actions Returns Ordered Deterministic Actions.
    [<Test>]
    member _.DeriveCandidateRequiredActionsReturnsOrderedDeterministicActions() =
        let actions, diagnostics =
            Review.deriveCandidateRequiredActions PromotionSetStatus.Blocked StepsComputationStatus.ComputeFailed (Option.Some QueueState.Paused) 2 false

        actions
        |> should
            equal
            [
                "RetryComputation"
                "ResolveConflicts"
                "ResumeQueue"
                "ResolveFindings"
                "ConfirmValidationSummary"
            ]

        Assert.That(diagnostics, Is.Empty)

    /// Verifies that build Candidate Projection Snapshot Includes Not Available Diagnostics.
    [<Test>]
    member _.BuildCandidateProjectionSnapshotIncludesNotAvailableDiagnostics() =
        let promotionSet =
            { PromotionSetDto.Default with
                PromotionSetId = Guid.NewGuid()
                Status = PromotionSetStatus.Ready
                StepsComputationStatus = StepsComputationStatus.Computed
            }

        let identity = CandidateIdentityProjection()
        identity.CandidateId <- promotionSet.PromotionSetId.ToString()
        identity.PromotionSetId <- promotionSet.PromotionSetId.ToString()
        identity.IdentityMode <- CandidateIdentityModes.DirectPromotionSetProjection
        let scope = CandidateProjectionScope()
        scope.OwnerId <- Guid.NewGuid().ToString()
        scope.OrganizationId <- Guid.NewGuid().ToString()
        scope.RepositoryId <- Guid.NewGuid().ToString()
        identity.Scope <- scope

        let snapshot = Review.buildCandidateProjectionSnapshot identity promotionSet Option.None Option.None

        snapshot.RequiredActions
        |> should equal [ "ConfirmValidationSummary" ]

        snapshot.Diagnostics
        |> should
            equal
            [
                "Queue state is unavailable for this candidate."
                "Review notes are not available for this candidate."
            ]

        Assert.That(snapshot.QueueState, Is.EqualTo(ProjectionSourceStates.NotAvailable))

        snapshot.SourceStates
        |> List.map (fun source -> source.Section)
        |> should
            equal
            [
                "identity"
                "promotionSet"
                "queue"
                "review"
            ]

    /// Verifies that build Candidate Attestation Entries Marks Missing Sources As Not Available.
    [<Test>]
    member _.BuildCandidateAttestationEntriesMarksMissingSourcesAsNotAvailable() =
        let attestations, diagnostics, sourceStates = Review.buildCandidateAttestationEntries Option.None Option.None

        Assert.That(attestations.Length, Is.EqualTo(2))
        Assert.That(attestations[0].Name, Is.EqualTo("PolicySnapshot"))
        Assert.That(attestations[0].Status, Is.EqualTo(ProjectionSourceStates.NotAvailable))
        Assert.That(attestations[1].Name, Is.EqualTo("ReviewCheckpoint"))
        Assert.That(attestations[1].Status, Is.EqualTo(ProjectionSourceStates.NotAvailable))

        diagnostics
        |> should
            equal
            [
                "Policy snapshot context is unavailable for this candidate."
                "Review checkpoint context is unavailable for this candidate."
            ]

        sourceStates
        |> List.map (fun source -> source.Section)
        |> should equal [ "identity"; "policy"; "checkpoint" ]
