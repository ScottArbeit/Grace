namespace Grace.Server.Tests

open Grace.Server.Tests.Services
open Grace.Server
open Grace.Shared
open Grace.Shared.Parameters.Review
open Grace.Shared.Utilities
open Grace.Types.Common
open Grace.Types.Policy
open Grace.Types.PromotionSet
open Grace.Types.Review
open NUnit.Framework
open System
open System.Net
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks

/// Groups shared helpers for review integration helpers.
module private ReviewIntegrationHelpers =
    /// Posts Async to the running test server.
    let private postAsync (route: string) (content: HttpContent) =
        let request = new HttpRequestMessage(HttpMethod.Post, route)
        request.Headers.Add(Constants.CorrelationIdHeaderKey, generateCorrelationId ())
        request.Content <- content
        Client.SendAsync(request)

    /// Defines review scoped behavior for the surrounding tests used by the server integration review Integration scenario.
    let private reviewScoped<'T when 'T :> ReviewParameters> (parameters: 'T) repositoryId promotionSetId : 'T =
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.PromotionSetId <- promotionSetId
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    /// Defines candidate scoped behavior for the surrounding tests used by the server integration review Integration scenario.
    let private candidateScoped<'T when 'T :> CandidateProjectionParameters> (parameters: 'T) repositoryId candidateId : 'T =
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.CandidateId <- candidateId
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    /// Posts ok return to the running test server.
    let postOkReturnAsync<'T, 'P> route (parameters: 'P) =
        task {
            let! response = postAsync route (createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
            use document = JsonDocument.Parse(body)
            let mutable returnValue = Unchecked.defaultof<JsonElement>

            if (document.RootElement.TryGetProperty("ReturnValue", &returnValue)
                || document.RootElement.TryGetProperty("returnValue", &returnValue))
               && returnValue.ValueKind = JsonValueKind.Object then
                return
                    deserialize<GraceReturnValue<'T>> body
                    |> fun value -> value.ReturnValue
            else
                return deserialize<'T> body
        }

    /// Posts bad request contains to the running test server.
    let postBadRequestContainsAsync<'P> route (parameters: 'P) expectedText =
        task {
            let! response = postAsync route (createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), body)
            Assert.That(body, Does.Contain(expectedText))
            return body
        }

    /// Posts status contains to the running test server.
    let postStatusContainsAsync<'P> route (parameters: 'P) (expectedStatus: HttpStatusCode) expectedText =
        task {
            let! response = postAsync route (createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(expectedStatus), body)
            Assert.That(body, Does.Contain(expectedText))
            return body
        }

    /// Posts ok body contains to the running test server.
    let postOkBodyContainsAsync<'P> route (parameters: 'P) expectedText =
        task {
            let! response = postAsync route (createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
            Assert.That(body, Does.Contain(expectedText))
            return body
        }

    /// Builds a deterministic promotion set for integration setup fixture for the server integration review Integration assertions.
    let createPromotionSetAsync repositoryId targetBranchId =
        task {
            let promotionSetId = Guid.NewGuid().ToString()
            let parameters = Grace.Shared.Parameters.PromotionSet.CreatePromotionSetParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.PromotionSetId <- promotionSetId
            parameters.TargetBranchId <- targetBranchId
            parameters.CorrelationId <- generateCorrelationId ()
            let! response = postAsync "/promotion-set/create" (createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
            return promotionSetId
        }

    /// Seeds policy snapshot for integration test setup.
    let seedPolicySnapshotAsync repositoryId branchId =
        task {
            let policySnapshotId = $"{Guid.NewGuid():N}{Guid.NewGuid():N}"
            let parameters = Grace.Server.Policy.SeedPolicySnapshotParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.TargetBranchId <- branchId
            parameters.PolicySnapshotId <- policySnapshotId
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = postAsync "/policy/_seedSnapshot" (createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
            return policySnapshotId
        }

    /// Defines checkpoint behavior for the surrounding tests used by the server integration review Integration scenario.
    let checkpointAsync repositoryId promotionSetId reviewedUpToReferenceId policySnapshotId =
        task {
            let parameters = reviewScoped (ReviewCheckpointParameters()) repositoryId promotionSetId
            parameters.ReviewedUpToReferenceId <- reviewedUpToReferenceId
            parameters.PolicySnapshotId <- policySnapshotId
            let! response = postAsync "/review/checkpoint" (createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)

            Assert.That(
                body,
                Does
                    .Contain("ReturnValue")
                    .Or.Contain("returnValue")
            )

            return ()
        }

    /// Gets notes from the running test server.
    let getNotesAsync repositoryId promotionSetId =
        let parameters = reviewScoped (GetReviewNotesParameters()) repositoryId promotionSetId
        postOkReturnAsync<ReviewNotes option, GetReviewNotesParameters> "/review/notes" parameters

    /// Gets notes current no notes from the running test server.
    let getNotesCurrentNoNotesAsync repositoryId promotionSetId =
        let parameters = reviewScoped (GetReviewNotesParameters()) repositoryId promotionSetId
        postOkBodyContainsAsync "/review/notes" parameters "ReturnValue"

    /// Defines resolve missing finding behavior for the surrounding tests used by the server integration review Integration scenario.
    let resolveMissingFindingAsync repositoryId promotionSetId findingId =
        let parameters = reviewScoped (ResolveFindingParameters()) repositoryId promotionSetId
        parameters.FindingId <- findingId
        parameters.ResolutionState <- "Approved"
        parameters.Note <- "hosted route proof"
        postBadRequestContainsAsync<ResolveFindingParameters> "/review/resolve" parameters "The review notes do not exist."

    /// Defines candidate identity behavior for the surrounding tests used by the server integration review Integration scenario.
    let candidateIdentityAsync repositoryId candidateId =
        candidateScoped (ResolveCandidateIdentityParameters()) repositoryId candidateId
        |> postOkReturnAsync<CandidateIdentityProjectionResult, ResolveCandidateIdentityParameters> "/review/candidate/resolve"

    /// Defines candidate get behavior for the surrounding tests used by the server integration review Integration scenario.
    let candidateGetAsync repositoryId candidateId =
        candidateScoped (CandidateProjectionParameters()) repositoryId candidateId
        |> postOkReturnAsync<CandidateProjectionSnapshotResult, CandidateProjectionParameters> "/review/candidate/get"

    /// Defines candidate get current failure behavior for the surrounding tests used by the server integration review Integration scenario.
    let candidateGetCurrentFailureAsync repositoryId candidateId =
        candidateScoped (CandidateProjectionParameters()) repositoryId candidateId
        |> fun parameters -> postStatusContainsAsync "/review/candidate/get" parameters HttpStatusCode.InternalServerError "deriveCandidateRequiredActions"

    /// Defines candidate required actions behavior for the surrounding tests used by the server integration review Integration scenario.
    let candidateRequiredActionsAsync repositoryId candidateId =
        candidateScoped (CandidateProjectionParameters()) repositoryId candidateId
        |> postOkReturnAsync<CandidateRequiredActionsResult, CandidateProjectionParameters> "/review/candidate/required-actions"

    /// Defines candidate required actions current failure behavior for the surrounding tests used by the server integration review Integration scenario.
    let candidateRequiredActionsCurrentFailureAsync repositoryId candidateId =
        candidateScoped (CandidateProjectionParameters()) repositoryId candidateId
        |> fun parameters ->
            postStatusContainsAsync "/review/candidate/required-actions" parameters HttpStatusCode.InternalServerError "deriveCandidateRequiredActions"

    /// Defines candidate attestations behavior for the surrounding tests used by the server integration review Integration scenario.
    let candidateAttestationsAsync repositoryId candidateId =
        candidateScoped (CandidateProjectionParameters()) repositoryId candidateId
        |> postOkReturnAsync<CandidateAttestationsResult, CandidateProjectionParameters> "/review/candidate/attestations"

    /// Defines review report behavior for the surrounding tests used by the server integration review Integration scenario.
    let reviewReportAsync repositoryId candidateId =
        candidateScoped (CandidateProjectionParameters()) repositoryId candidateId
        |> postOkReturnAsync<ReviewModels.ReviewReportResult, CandidateProjectionParameters> "/review/report/get"

    /// Defines review report current failure behavior for the surrounding tests used by the server integration review Integration scenario.
    let reviewReportCurrentFailureAsync repositoryId candidateId =
        candidateScoped (CandidateProjectionParameters()) repositoryId candidateId
        |> fun parameters -> postStatusContainsAsync "/review/report/get" parameters HttpStatusCode.InternalServerError "deriveCandidateRequiredActions"

    /// Defines retry behavior for the surrounding tests used by the server integration review Integration scenario.
    let retryAsync repositoryId candidateId =
        let parameters = candidateScoped (CandidateProjectionParameters()) repositoryId candidateId
        postBadRequestContainsAsync<CandidateProjectionParameters> "/review/candidate/retry" parameters "PromotionSet does not exist."

    /// Defines cancel behavior for the surrounding tests used by the server integration review Integration scenario.
    let cancelAsync repositoryId candidateId =
        let parameters = candidateScoped (CandidateProjectionParameters()) repositoryId candidateId
        postBadRequestContainsAsync<CandidateProjectionParameters> "/review/candidate/cancel" parameters "target branch queue is not initialized"

    /// Defines gate rerun behavior for the surrounding tests used by the server integration review Integration scenario.
    let gateRerunAsync repositoryId candidateId =
        let parameters = candidateScoped (CandidateGateRerunParameters()) repositoryId candidateId
        parameters.Gate <- "required-validation"
        postBadRequestContainsAsync<CandidateGateRerunParameters> "/review/candidate/gate-rerun" parameters "PromotionSet does not exist."

    /// Defines deepen stub behavior for the surrounding tests used by the server integration review Integration scenario.
    let deepenStubAsync repositoryId promotionSetId =
        let parameters = reviewScoped (DeepenReviewParameters()) repositoryId promotionSetId
        parameters.ChapterId <- "chapter-proof"
        postBadRequestContainsAsync<DeepenReviewParameters> "/review/deepen" parameters "Deepen is not implemented yet."

/// Covers review route scenarios.
[<NonParallelizable>]
type ReviewRouteIntegrationTests() =

    /// Verifies the review candidate routes preserve projection order checkpoint and current stub contract scenario.
    [<Test>]
    member _.ReviewCandidateRoutesPreserveProjectionOrderCheckpointAndCurrentStubContract() =
        task {
            let repositoryId = repositoryIds[0]
            let targetBranchId = repositoryDefaultBranchIds[0]
            let! promotionSetId = ReviewIntegrationHelpers.createPromotionSetAsync repositoryId targetBranchId
            let! policySnapshotId = ReviewIntegrationHelpers.seedPolicySnapshotAsync repositoryId targetBranchId
            let reviewedUpToReferenceId = Guid.NewGuid().ToString()

            let! identity = ReviewIntegrationHelpers.candidateIdentityAsync repositoryId promotionSetId
            Assert.That(identity.Identity.CandidateId, Is.EqualTo(promotionSetId))
            Assert.That(identity.Identity.PromotionSetId, Is.EqualTo(Guid.Empty.ToString()))
            Assert.That(identity.Identity.IdentityMode, Is.EqualTo(CandidateIdentityModes.DirectPromotionSetProjection))

            let identitySourceSections =
                identity.SourceStates
                |> List.map (fun state -> state.Section)
                |> List.toArray

            Assert.That(identitySourceSections, Does.Contain("identity"))

            let! _ = ReviewIntegrationHelpers.candidateGetCurrentFailureAsync repositoryId promotionSetId

            do! ReviewIntegrationHelpers.checkpointAsync repositoryId promotionSetId reviewedUpToReferenceId policySnapshotId

            let! notesBody = ReviewIntegrationHelpers.getNotesCurrentNoNotesAsync repositoryId promotionSetId
            Assert.That(notesBody, Does.Contain("ReturnValue"))
            Assert.That(notesBody, Does.Contain("null"))

            let! _ = ReviewIntegrationHelpers.resolveMissingFindingAsync repositoryId promotionSetId (Guid.NewGuid().ToString())

            let! _ = ReviewIntegrationHelpers.candidateRequiredActionsCurrentFailureAsync repositoryId promotionSetId

            let! attestations = ReviewIntegrationHelpers.candidateAttestationsAsync repositoryId promotionSetId
            Assert.That(attestations.Identity.CandidateId, Is.EqualTo(promotionSetId))
            Assert.That(attestations.Identity.PromotionSetId, Is.EqualTo(Guid.Empty.ToString()))

            let attestationNames =
                attestations.Attestations
                |> List.map (fun attestation -> attestation.Name)
                |> List.toArray

            Assert.That(attestationNames.Length, Is.EqualTo(2))
            Assert.That(attestationNames[0], Is.EqualTo("PolicySnapshot"))
            Assert.That(attestationNames[1], Is.EqualTo("ReviewCheckpoint"))

            Assert.That(attestations.Attestations[0].Status, Is.EqualTo(ProjectionSourceStates.NotAvailable))
            Assert.That(attestations.Attestations[1].Status, Is.EqualTo(ProjectionSourceStates.NotAvailable))

            let attestationSourceSections =
                attestations.SourceStates
                |> List.map (fun sourceState -> sourceState.Section)
                |> List.toArray

            Assert.That(attestationSourceSections.Length, Is.EqualTo(3))
            Assert.That(attestationSourceSections[0], Is.EqualTo("identity"))
            Assert.That(attestationSourceSections[1], Is.EqualTo("policy"))
            Assert.That(attestationSourceSections[2], Is.EqualTo("checkpoint"))

            let! _ = ReviewIntegrationHelpers.reviewReportCurrentFailureAsync repositoryId promotionSetId
            let! _ = ReviewIntegrationHelpers.retryAsync repositoryId promotionSetId
            let! _ = ReviewIntegrationHelpers.cancelAsync repositoryId promotionSetId
            let! _ = ReviewIntegrationHelpers.gateRerunAsync repositoryId promotionSetId
            let! _ = ReviewIntegrationHelpers.deepenStubAsync repositoryId promotionSetId

            return ()
        }
