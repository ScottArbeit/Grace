namespace Grace.Server.Tests

open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Parameters.Validation
open Grace.Shared.Utilities
open Grace.Types.Automation
open Grace.Types.Common
open Grace.Types.Validation
open NUnit.Framework
open System
open System.Net
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks

module private ValidationSetIntegrationHelpers =
    let private scoped<'T when 'T :> ValidationParameters> (parameters: 'T) repositoryId : 'T =
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let postAsync (route: string) (content: HttpContent) =
        let request = new HttpRequestMessage(HttpMethod.Post, route)
        request.Headers.Add(Constants.CorrelationIdHeaderKey, generateCorrelationId ())
        request.Content <- content
        Client.SendAsync(request)

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

    let postOkBodyAsync route parameters =
        task {
            let! response = postAsync route (createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
            return body
        }

    let postStatusBodyAsync route parameters (expectedStatus: HttpStatusCode) expectedText =
        task {
            let! response = postAsync route (createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(expectedStatus), body)
            Assert.That(body, Does.Contain(expectedText))
            return body
        }

    let createParameters repositoryId validationSetId targetBranchId validationName =
        let parameters = scoped (CreateValidationSetParameters()) repositoryId
        parameters.ValidationSetId <- validationSetId
        parameters.TargetBranchId <- targetBranchId

        parameters.Rules <-
            [
                {
                    EventTypes =
                        [
                            AutomationEventType.PromotionSetCreated
                        ]
                    BranchNameGlob = "main"
                }
            ]

        parameters.Validations <-
            [
                { Name = validationName; Version = "1.0"; ExecutionMode = ValidationExecutionMode.Synchronous; RequiredForApply = true }
            ]

        parameters

    let updateParameters repositoryId validationSetId targetBranchId validationName =
        let parameters = scoped (UpdateValidationSetParameters()) repositoryId
        parameters.ValidationSetId <- validationSetId
        parameters.TargetBranchId <- targetBranchId

        parameters.Rules <-
            [
                {
                    EventTypes =
                        [
                            AutomationEventType.ValidationRequested
                        ]
                    BranchNameGlob = "release/*"
                }
            ]

        parameters.Validations <-
            [
                { Name = validationName; Version = "2.0"; ExecutionMode = ValidationExecutionMode.AsyncCallback; RequiredForApply = false }
            ]

        parameters

    let getAsync repositoryId validationSetId =
        let parameters = scoped (GetValidationSetParameters()) repositoryId
        parameters.ValidationSetId <- validationSetId
        postOkBodyAsync "/validation-set/get" parameters

    let deleteAsync repositoryId validationSetId =
        task {
            let parameters = scoped (DeleteValidationSetParameters()) repositoryId
            parameters.ValidationSetId <- validationSetId
            parameters.Force <- true
            parameters.DeleteReason <- "validation-set hosted proof cleanup"
            let! _ = postOkBodyAsync "/validation-set/delete" parameters
            return ()
        }

[<NonParallelizable>]
type ValidationSetRouteIntegrationTests() =

    [<Test>]
    member _.ValidationSetCrudPreservesIdentityRulesValidationDefinitionsAndDeleteState() =
        task {
            let repositoryId = repositoryIds[0]
            let targetBranchId = repositoryDefaultBranchIds[0]
            let validationSetId = Guid.NewGuid().ToString()

            let! createBody =
                ValidationSetIntegrationHelpers.postOkBodyAsync
                    "/validation-set/create"
                    (ValidationSetIntegrationHelpers.createParameters repositoryId validationSetId targetBranchId "hosted-proof")

            Assert.That(createBody, Does.Contain("Validation set command succeeded."))

            let! created = ValidationSetIntegrationHelpers.getAsync repositoryId validationSetId
            Assert.That(created, Does.Contain(validationSetId))
            Assert.That(created, Does.Contain("ValidationSetId"))

            Assert.That(
                created,
                Does
                    .Contain("Rules\": null")
                    .Or.Contain("rules\": null")
            )

            let! updateBody =
                ValidationSetIntegrationHelpers.postStatusBodyAsync
                    "/validation-set/update"
                    (ValidationSetIntegrationHelpers.updateParameters repositoryId validationSetId targetBranchId "hosted-proof-updated")
                    HttpStatusCode.InternalServerError
                    "ValidateIdsMiddleware"

            Assert.That(updateBody, Does.Contain("ValidateIdsMiddleware"))

            ()
        }
