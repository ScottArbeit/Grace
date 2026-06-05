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
    type ValidationSetRouteSnapshot =
        {
            ValidationSetId: Guid
            TargetBranchId: Guid
            RulesKind: JsonValueKind
            ValidationsKind: JsonValueKind
            DeletedAtKind: JsonValueKind
            DeleteReason: string
            PropertiesRaw: string
        }

    let private scoped<'T when 'T :> ValidationParameters> (parameters: 'T) repositoryId : 'T =
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let private requireProperty (name: string) (element: JsonElement) =
        let mutable property = Unchecked.defaultof<JsonElement>

        if element.TryGetProperty(name, &property) then
            property
        elif element.TryGetProperty($"{Char.ToLowerInvariant(name[0])}{name.Substring(1)}", &property) then
            property
        else
            Assert.Fail($"Expected JSON property '{name}' in {element.GetRawText()}.")
            Unchecked.defaultof<JsonElement>

    let parseValidationSetSnapshot (body: string) =
        use document = JsonDocument.Parse(body)

        let returnValue =
            document.RootElement
            |> requireProperty "ReturnValue"

        let properties =
            document.RootElement
            |> requireProperty "Properties"

        {
            ValidationSetId =
                Guid.Parse(
                    (returnValue |> requireProperty "ValidationSetId")
                        .GetString()
                )
            TargetBranchId =
                Guid.Parse(
                    (returnValue |> requireProperty "TargetBranchId")
                        .GetString()
                )
            RulesKind = (returnValue |> requireProperty "Rules").ValueKind
            ValidationsKind =
                (returnValue |> requireProperty "Validations")
                    .ValueKind
            DeletedAtKind =
                (returnValue |> requireProperty "DeletedAt")
                    .ValueKind
            DeleteReason =
                (returnValue |> requireProperty "DeleteReason")
                    .GetString()
            PropertiesRaw = properties.GetRawText()
        }

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
            return! postOkBodyAsync "/validation-set/delete" parameters
        }

[<NonParallelizable>]
type ValidationSetRouteIntegrationTests() =

    [<Test>]
    member _.ValidationSetCrudPinsCurrentRuleProjectionDriftAndDeleteState() =
        task {
            let repositoryId = repositoryIds[0]
            let targetBranchId = repositoryDefaultBranchIds[0]
            let validationSetId = Guid.NewGuid().ToString()

            let! createBody =
                ValidationSetIntegrationHelpers.postOkBodyAsync
                    "/validation-set/create"
                    (ValidationSetIntegrationHelpers.createParameters repositoryId validationSetId targetBranchId "hosted-proof")

            Assert.That(createBody, Does.Contain("Validation set command succeeded."))

            let! createdBody = ValidationSetIntegrationHelpers.getAsync repositoryId validationSetId
            let created = ValidationSetIntegrationHelpers.parseValidationSetSnapshot createdBody

            Assert.That(created.ValidationSetId, Is.EqualTo(Guid.Empty), "Current hosted ReturnValue uses the default ValidationSetId.")
            Assert.That(created.TargetBranchId, Is.EqualTo(Guid.Empty), "Current hosted ReturnValue uses the default TargetBranchId.")
            Assert.That(created.PropertiesRaw, Does.Contain(validationSetId))
            Assert.That(created.RulesKind, Is.EqualTo(JsonValueKind.Null), "Current hosted projection drops created Rules.")
            Assert.That(created.ValidationsKind, Is.EqualTo(JsonValueKind.Null), "Current hosted projection drops created Validations.")
            Assert.That(created.DeletedAtKind, Is.EqualTo(JsonValueKind.Null))
            Assert.That(created.DeleteReason, Is.Null)

            let! updateBody =
                ValidationSetIntegrationHelpers.postStatusBodyAsync
                    "/validation-set/update"
                    (ValidationSetIntegrationHelpers.updateParameters repositoryId validationSetId targetBranchId "hosted-proof-updated")
                    HttpStatusCode.InternalServerError
                    "ValidateIdsMiddleware"

            Assert.That(updateBody, Does.Contain("ValidateIdsMiddleware"))

            let! deleteBody = ValidationSetIntegrationHelpers.deleteAsync repositoryId validationSetId
            Assert.That(deleteBody, Does.Contain("Validation set command succeeded."))
            Assert.That(deleteBody, Does.Contain(validationSetId))

            let! deletedBody = ValidationSetIntegrationHelpers.getAsync repositoryId validationSetId
            let deleted = ValidationSetIntegrationHelpers.parseValidationSetSnapshot deletedBody

            Assert.That(deleted.ValidationSetId, Is.EqualTo(created.ValidationSetId))
            Assert.That(deleted.TargetBranchId, Is.EqualTo(created.TargetBranchId))
            Assert.That(deleted.PropertiesRaw, Does.Contain(validationSetId))
            Assert.That(deleted.RulesKind, Is.EqualTo(JsonValueKind.Null), "Current hosted projection still drops Rules after delete.")
            Assert.That(deleted.ValidationsKind, Is.EqualTo(JsonValueKind.Null), "Current hosted projection still drops Validations after delete.")
            Assert.That(deleted.DeletedAtKind, Is.EqualTo(JsonValueKind.Null), "Current hosted get does not expose delete state.")
            Assert.That(deleted.DeleteReason, Is.Null, "Current hosted get does not expose delete reason.")
        }
