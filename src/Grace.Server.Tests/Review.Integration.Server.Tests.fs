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

module private ReviewIntegrationHelpers =
    let private postAsync (route: string) (content: HttpContent) =
        let request = new HttpRequestMessage(HttpMethod.Post, route)
        request.Headers.Add(Constants.CorrelationIdHeaderKey, generateCorrelationId ())
        request.Content <- content
        Client.SendAsync(request)

    let private reviewScoped<'T when 'T :> ReviewParameters> (parameters: 'T) repositoryId promotionSetId : 'T =
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.PromotionSetId <- promotionSetId
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let private candidateScoped<'T when 'T :> CandidateProjectionParameters> (parameters: 'T) repositoryId candidateId : 'T =
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.CandidateId <- candidateId
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

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

    let postBadRequestContainsAsync<'P> route (parameters: 'P) expectedText =
        task {
            let! response = postAsync route (createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), body)
            Assert.That(body, Does.Contain(expectedText))
            return body
        }

    let postStatusContainsAsync<'P> route (parameters: 'P) (expectedStatus: HttpStatusCode) expectedText =
        task {
            let! response = postAsync route (createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(expectedStatus), body)
            Assert.That(body, Does.Contain(expectedText))
            return body
        }

    let postOkBodyContainsAsync<'P> route (parameters: 'P) expectedText =
        task {
            let! response = postAsync route (createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
            Assert.That(body, Does.Contain(expectedText))
            return body
        }

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

    let getNotesAsync repositoryId promotionSetId =
        let parameters = reviewScoped (GetReviewNotesParameters()) repositoryId promotionSetId
        postOkReturnAsync<ReviewNotes option, GetReviewNotesParameters> "/review/notes" parameters

    let getNotesCurrentNoNotesAsync repositoryId promotionSetId =
        let parameters = reviewScoped (GetReviewNotesParameters()) repositoryId promotionSetId
        postOkBodyContainsAsync "/review/notes" parameters "ReturnValue"

    let resolveMissingFindingAsync repositoryId promotionSetId findingId =
        let parameters = reviewScoped (ResolveFindingParameters()) repositoryId promotionSetId
        parameters.FindingId <- findingId
        parameters.ResolutionState <- "Approved"
        parameters.Note <- "hosted route proof"
        postBadRequestContainsAsync<ResolveFindingParameters> "/review/resolve" parameters "The review notes do not exist."

    let candidateIdentityAsync repositoryId candidateId =
        candidateScoped (ResolveCandidateIdentityParameters()) repositoryId candidateId
        |> postOkReturnAsync<CandidateIdentityProjectionResult, ResolveCandidateIdentityParameters> "/review/candidate/resolve"

    let candidateGetAsync repositoryId candidateId =
        candidateScoped (CandidateProjectionParameters()) repositoryId candidateId
        |> postOkReturnAsync<CandidateProjectionSnapshotResult, CandidateProjectionParameters> "/review/candidate/get"

    let candidateGetCurrentFailureAsync repositoryId candidateId =
        candidateScoped (CandidateProjectionParameters()) repositoryId candidateId
        |> fun parameters -> postStatusContainsAsync "/review/candidate/get" parameters HttpStatusCode.InternalServerError "deriveCandidateRequiredActions"

    let candidateRequiredActionsAsync repositoryId candidateId =
        candidateScoped (CandidateProjectionParameters()) repositoryId candidateId
        |> postOkReturnAsync<CandidateRequiredActionsResult, CandidateProjectionParameters> "/review/candidate/required-actions"

    let candidateRequiredActionsCurrentFailureAsync repositoryId candidateId =
        candidateScoped (CandidateProjectionParameters()) repositoryId candidateId
        |> fun parameters ->
            postStatusContainsAsync "/review/candidate/required-actions" parameters HttpStatusCode.InternalServerError "deriveCandidateRequiredActions"

    let candidateAttestationsAsync repositoryId candidateId =
        candidateScoped (CandidateProjectionParameters()) repositoryId candidateId
        |> postOkReturnAsync<CandidateAttestationsResult, CandidateProjectionParameters> "/review/candidate/attestations"

    let reviewReportAsync repositoryId candidateId =
        candidateScoped (CandidateProjectionParameters()) repositoryId candidateId
        |> postOkReturnAsync<ReviewModels.ReviewReportResult, CandidateProjectionParameters> "/review/report/get"

    let reviewReportCurrentFailureAsync repositoryId candidateId =
        candidateScoped (CandidateProjectionParameters()) repositoryId candidateId
        |> fun parameters -> postStatusContainsAsync "/review/report/get" parameters HttpStatusCode.InternalServerError "deriveCandidateRequiredActions"

    let retryAsync repositoryId candidateId =
        let parameters = candidateScoped (CandidateProjectionParameters()) repositoryId candidateId
        postBadRequestContainsAsync<CandidateProjectionParameters> "/review/candidate/retry" parameters "PromotionSet does not exist."

    let cancelAsync repositoryId candidateId =
        let parameters = candidateScoped (CandidateProjectionParameters()) repositoryId candidateId
        postBadRequestContainsAsync<CandidateProjectionParameters> "/review/candidate/cancel" parameters "target branch queue is not initialized"

    let gateRerunAsync repositoryId candidateId =
        let parameters = candidateScoped (CandidateGateRerunParameters()) repositoryId candidateId
        parameters.Gate <- "required-validation"
        postBadRequestContainsAsync<CandidateGateRerunParameters> "/review/candidate/gate-rerun" parameters "PromotionSet does not exist."

    let deepenStubAsync repositoryId promotionSetId =
        let parameters = reviewScoped (DeepenReviewParameters()) repositoryId promotionSetId
        parameters.ChapterId <- "chapter-proof"
        postBadRequestContainsAsync<DeepenReviewParameters> "/review/deepen" parameters "Deepen is not implemented yet."

[<NonParallelizable>]
type ReviewRouteIntegrationTests() =

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
