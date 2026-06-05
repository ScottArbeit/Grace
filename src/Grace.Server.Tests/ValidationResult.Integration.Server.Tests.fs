namespace Grace.Server.Tests

open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Parameters.Validation
open Grace.Shared.Utilities
open Grace.Types.Common
open Grace.Types.Validation
open NUnit.Framework
open System
open System.Net
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks

module private ValidationResultIntegrationHelpers =
    type RecordedValidationResult =
        {
            ValidationResultId: Guid
            RepositoryId: Guid
            ValidationSetId: Guid option
            PromotionSetId: Guid option
            PromotionSetStepId: Guid option
            StepsComputationAttempt: int option
            Status: string
            Summary: string
            ArtifactIds: Guid list
        }

    let recordParameters repositoryId validationResultId validationSetId promotionSetId promotionSetStepId stepsComputationAttempt artifactIds =
        let parameters = RecordValidationResultParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.ValidationResultId <- validationResultId
        parameters.ValidationSetId <- validationSetId
        parameters.PromotionSetId <- promotionSetId
        parameters.PromotionSetStepId <- promotionSetStepId
        parameters.StepsComputationAttempt <- stepsComputationAttempt
        parameters.ValidationName <- "hosted-proof"
        parameters.ValidationVersion <- "1.0"
        parameters.Status <- "Pass"
        parameters.Summary <- "Hosted validation result route proof."
        parameters.ArtifactIds <- artifactIds
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let postWithCorrelationAsync (route: string) (correlationId: string) parameters =
        let request = new HttpRequestMessage(HttpMethod.Post, route)
        request.Headers.Add(Constants.CorrelationIdHeaderKey, correlationId)
        request.Content <- createJsonContent parameters
        Client.SendAsync(request)

    let private tryGetProperty (name: string) (element: JsonElement) =
        let mutable property = Unchecked.defaultof<JsonElement>

        if element.TryGetProperty(name, &property) then
            property
        elif element.TryGetProperty($"{Char.ToLowerInvariant(name[0])}{name.Substring(1)}", &property) then
            property
        else
            Assert.Fail($"Expected JSON property '{name}' in {element.GetRawText()}.")
            Unchecked.defaultof<JsonElement>

    let private getOptionalGuid (name: string) (element: JsonElement) =
        let property = tryGetProperty name element

        if property.ValueKind = JsonValueKind.Null then
            Option.None
        else
            let value = property.GetString()

            if String.IsNullOrWhiteSpace(value) then
                Option.None
            else
                Option.Some(Guid.Parse value)

    let private getOptionalInt (name: string) (element: JsonElement) =
        let property = tryGetProperty name element

        if property.ValueKind = JsonValueKind.Null then
            Option.None
        else
            Option.Some(property.GetInt32())

    let private parseRecordedValidationResult (body: string) =
        use document = JsonDocument.Parse(body)

        let envelopeReturnValue =
            document.RootElement
            |> tryGetProperty "ReturnValue"

        let returnValue =
            if envelopeReturnValue.ValueKind = JsonValueKind.Object then
                envelopeReturnValue
            elif envelopeReturnValue.ValueKind = JsonValueKind.Null then
                Assert.Fail($"Route returned null returnValue. Body: {body}")
                Unchecked.defaultof<JsonElement>
            else
                document.RootElement

        let output = returnValue |> tryGetProperty "Output"
        let artifactIds = output |> tryGetProperty "ArtifactIds"

        let artifacts =
            artifactIds.EnumerateArray()
            |> Seq.map (fun value -> Guid.Parse(value.GetString()))
            |> Seq.toList

        {
            ValidationResultId =
                Guid.Parse(
                    (returnValue |> tryGetProperty "ValidationResultId")
                        .GetString()
                )
            RepositoryId =
                Guid.Parse(
                    (returnValue |> tryGetProperty "RepositoryId")
                        .GetString()
                )
            ValidationSetId = getOptionalGuid "ValidationSetId" returnValue
            PromotionSetId = getOptionalGuid "PromotionSetId" returnValue
            PromotionSetStepId = getOptionalGuid "PromotionSetStepId" returnValue
            StepsComputationAttempt = getOptionalInt "StepsComputationAttempt" returnValue
            Status = (output |> tryGetProperty "Status").GetString()
            Summary = (output |> tryGetProperty "Summary").GetString()
            ArtifactIds = artifacts
        }

    let recordBodyAsync correlationId parameters =
        task {
            let! response = postWithCorrelationAsync "/validation-result/record" correlationId parameters
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
            return body
        }

    let recordBadRequestContainsAsync correlationId parameters expectedText =
        task {
            let! response = postWithCorrelationAsync "/validation-result/record" correlationId parameters
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), body)
            Assert.That(body, Does.Contain(expectedText))
        }

[<NonParallelizable>]
type ValidationResultRouteIntegrationTests() =

    [<Test>]
    member _.ValidationResultRecordPersistsArtifactLinksAndRejectsDuplicateCorrelationReplay() =
        task {
            let repositoryId = repositoryIds[0]
            let validationResultId = Guid.NewGuid().ToString()
            let validationSetId = Guid.NewGuid().ToString()
            let promotionSetId = Guid.NewGuid().ToString()
            let promotionSetStepId = Guid.NewGuid().ToString()
            let firstArtifactId = Guid.NewGuid()
            let secondArtifactId = Guid.NewGuid()
            let correlationId = generateCorrelationId ()

            let parameters =
                ValidationResultIntegrationHelpers.recordParameters
                    repositoryId
                    validationResultId
                    validationSetId
                    promotionSetId
                    promotionSetStepId
                    1
                    [|
                        firstArtifactId.ToString()
                        secondArtifactId.ToString()
                    |]

            let! recorded = ValidationResultIntegrationHelpers.recordBodyAsync correlationId parameters
            Assert.That(recorded, Does.Contain("ValidationResultId"))
            Assert.That(recorded, Does.Contain(validationResultId))
            Assert.That(recorded, Does.Contain(firstArtifactId.ToString()))
            Assert.That(recorded, Does.Contain(secondArtifactId.ToString()))

            Assert.That(
                recorded,
                Does
                    .Contain("Output\": null")
                    .Or.Contain("output\": null")
            )

            do!
                ValidationResultIntegrationHelpers.recordBadRequestContainsAsync
                    correlationId
                    parameters
                    "Duplicate correlation ID for ValidationResult command."

            let newCorrelationId = generateCorrelationId ()
            let! replayedWithNewCorrelation = ValidationResultIntegrationHelpers.recordBodyAsync newCorrelationId parameters
            Assert.That(replayedWithNewCorrelation, Does.Contain(validationResultId))
            Assert.That(replayedWithNewCorrelation, Does.Contain(firstArtifactId.ToString()))
            Assert.That(replayedWithNewCorrelation, Does.Contain(secondArtifactId.ToString()))
        }
