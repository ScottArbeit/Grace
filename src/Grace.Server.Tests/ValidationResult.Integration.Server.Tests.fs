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

/// Groups shared helpers for validation result integration helpers.
module private ValidationResultIntegrationHelpers =
    /// Captures recorded validation result values used by the test suite.
    type RecordedValidationResult = { OutputKind: JsonValueKind; PropertiesRaw: string }

    /// Builds record parameters for route calls.
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

    /// Posts with correlation to the running test server.
    let postWithCorrelationAsync (route: string) (correlationId: string) parameters =
        let request = new HttpRequestMessage(HttpMethod.Post, route)
        request.Headers.Add(Constants.CorrelationIdHeaderKey, correlationId)
        request.Content <- createJsonContent parameters
        Client.SendAsync(request)

    /// Tries to resolve get property without failing the caller.
    let private tryGetProperty (name: string) (element: JsonElement) =
        let mutable property = Unchecked.defaultof<JsonElement>

        if element.TryGetProperty(name, &property) then
            property
        elif element.TryGetProperty($"{Char.ToLowerInvariant(name[0])}{name.Substring(1)}", &property) then
            property
        else
            Assert.Fail($"Expected JSON property '{name}' in {element.GetRawText()}.")
            Unchecked.defaultof<JsonElement>

    /// Parses recorded validation result from response content.
    let parseRecordedValidationResult (body: string) =
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

        let properties =
            document.RootElement
            |> tryGetProperty "Properties"

        { OutputKind = output.ValueKind; PropertiesRaw = properties.GetRawText() }

    /// Defines record body behavior for the surrounding tests used by the server integration validation Result Integration scenario.
    let recordBodyAsync correlationId parameters =
        task {
            let! response = postWithCorrelationAsync "/validation-result/record" correlationId parameters
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
            return body
        }

    /// Defines record bad request contains behavior for the surrounding tests used by the server integration validation Result Integration scenario.
    let recordBadRequestContainsAsync correlationId parameters expectedText =
        task {
            let! response = postWithCorrelationAsync "/validation-result/record" correlationId parameters
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), body)
            Assert.That(body, Does.Contain(expectedText))
        }

/// Covers validation result route scenarios.
[<NonParallelizable>]
type ValidationResultRouteIntegrationTests() =

    /// Verifies the validation result record pins current null output drift and rejects duplicate correlation replay scenario.
    [<Test>]
    member _.ValidationResultRecordPinsCurrentNullOutputDriftAndRejectsDuplicateCorrelationReplay() =
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
            let parsed = ValidationResultIntegrationHelpers.parseRecordedValidationResult recorded

            Assert.That(parsed.OutputKind, Is.EqualTo(JsonValueKind.Null), "Current hosted response does not expose persisted ValidationOutput.")
            Assert.That(parsed.PropertiesRaw, Does.Contain(validationResultId))
            Assert.That(parsed.PropertiesRaw, Does.Contain(firstArtifactId.ToString()))
            Assert.That(parsed.PropertiesRaw, Does.Contain(secondArtifactId.ToString()))

            do!
                ValidationResultIntegrationHelpers.recordBadRequestContainsAsync
                    correlationId
                    parameters
                    "Duplicate correlation ID for ValidationResult command."

            let newCorrelationId = generateCorrelationId ()
            let! replayedWithNewCorrelation = ValidationResultIntegrationHelpers.recordBodyAsync newCorrelationId parameters
            let parsedReplay = ValidationResultIntegrationHelpers.parseRecordedValidationResult replayedWithNewCorrelation

            Assert.That(
                parsedReplay.OutputKind,
                Is.EqualTo(JsonValueKind.Null),
                "Current hosted replay response still does not expose persisted ValidationOutput."
            )

            Assert.That(parsedReplay.PropertiesRaw, Does.Contain(validationResultId))
            Assert.That(parsedReplay.PropertiesRaw, Does.Contain(firstArtifactId.ToString()))
            Assert.That(parsedReplay.PropertiesRaw, Does.Contain(secondArtifactId.ToString()))
        }
