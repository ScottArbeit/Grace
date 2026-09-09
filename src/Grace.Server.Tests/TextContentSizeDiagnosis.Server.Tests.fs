namespace Grace.Server.Tests

open System
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Text.Json
open System.Text.Json.Nodes
open Grace.Server
open Grace.Server.WorkItem
open Grace.Actors.Services
open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Common
open Grace.Types.TextContent
open Grace.Types.WorkItem
open Microsoft.Azure.Cosmos
open NUnit.Framework

/// Exercises the TextContent admin boundary against real retained event documents in isolated Aspire repositories.
type TextContentSizeDiagnosisHttpTests() =
    let route = "/admin/text-content-size/diagnose"

    /// Requires a successful standard envelope before exposing its source-specific quantity to assertions.
    let diagnose parameters =
        task {
            use! response = Client.PostAsync(route, createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo HttpStatusCode.OK, body)

            return
                (deserialize<GraceReturnValue<TextContentSizeDiagnostic>> body)
                    .ReturnValue
        }

    /// Rejects non-admin callers before parsing and refuses names, foreign ownership, absent repositories and empty identifiers.
    [<Test>]
    member _.``text diagnostic requires admin and explicit verified scope``() =
        task {
            use nonAdmin = new HttpClient(BaseAddress = Client.BaseAddress)
            nonAdmin.DefaultRequestHeaders.Add("x-grace-user-id", string (Guid.NewGuid()))
            use! denied = nonAdmin.PostAsync(route, new StringContent("{", System.Text.Encoding.UTF8, "application/json"))
            Assert.That(denied.StatusCode, Is.EqualTo HttpStatusCode.Forbidden)
            let parameters = Parameters.Repository.GetRepositoryParameters(OwnerId = ownerId, OrganizationId = organizationId, RepositoryId = repositoryIds[0])
            parameters.RepositoryName <- "must-not-be-ignored"
            use! named = Client.PostAsync(route, createJsonContent parameters)
            Assert.That(named.StatusCode, Is.EqualTo HttpStatusCode.BadRequest)
            parameters.RepositoryName <- ""
            parameters.OwnerId <- string (Guid.NewGuid())
            use! foreign = Client.PostAsync(route, createJsonContent parameters)
            let! foreignBody = foreign.Content.ReadAsStringAsync()
            Assert.That(foreign.StatusCode, Is.EqualTo HttpStatusCode.BadRequest, foreignBody)
            Assert.That((deserialize<GraceError> foreignBody).Error, Does.Contain "scope")
            Assert.That(foreignBody, Does.Not.Contain "DeclaredTextContentUtf8Bytes")
            parameters.OwnerId <- ownerId
            parameters.RepositoryId <- string (Guid.NewGuid())
            use! absent = Client.PostAsync(route, createJsonContent parameters)
            Assert.That(absent.StatusCode, Is.EqualTo HttpStatusCode.BadRequest)
            parameters.RepositoryId <- string Guid.Empty
            use! empty = Client.PostAsync(route, createJsonContent parameters)
            Assert.That(empty.StatusCode, Is.EqualTo HttpStatusCode.BadRequest)
        }

    /// Counts retained superseded text over real Cosmos pages, validates malformed sources, and returns known zero after removal.
    [<Test>]
    member _.``text diagnostic counts paged retained declarations without claiming blob presence``() =
        task {
            let repositoryId = Guid.NewGuid()

            let create =
                Parameters.Repository.CreateRepositoryParameters(
                    OwnerId = ownerId,
                    OrganizationId = organizationId,
                    RepositoryId = string repositoryId,
                    RepositoryName = $"TextDiagnostic{repositoryId:N}"
                )

            use! created = Client.PostAsync("/repository/create", createJsonContent create)
            let! createdBody = created.Content.ReadAsStringAsync()
            Assert.That(created.StatusCode, Is.EqualTo HttpStatusCode.OK, createdBody)

            let parameters =
                Parameters.Repository.GetRepositoryParameters(OwnerId = ownerId, OrganizationId = organizationId, RepositoryId = string repositoryId)

            let! empty = diagnose parameters
            Assert.That(empty.DeclaredTextContentUtf8Bytes, Is.Zero)
            Assert.That(empty.DistinctTextContentCount, Is.Zero)
            use cosmos = AspireTestHost.createCosmosClient HostState.Value
            let container = cosmos.GetContainer(HostState.Value.CosmosDatabaseName, HostState.Value.CosmosContainerName)
            let producerId = Guid.NewGuid()
            let first = TextContentStorage.createDescription repositoryId producerId "first" "é🙂"
            let second = TextContentStorage.createDescription repositoryId producerId "second" "four"
            let sameTextNewId = TextContentStorage.createDescription repositoryId producerId "distinct-id" "é🙂"
            let cleared: Description = { DescriptionId = Guid.NewGuid(); TextContent = None }
            let inserted = ResizeArray<string * Guid>()

            /// Writes actual event types and Grace serialization without uploading the declared content objects.
            let persist partition grain initial subsequent =
                task {
                    let workItemId = Guid.NewGuid()

                    let metadata =
                        {
                            Timestamp = getCurrentInstant ()
                            CorrelationId = "text-diagnostic-integration"
                            Principal = testUserId
                            ClientType = None
                            Properties = Dictionary<string, string>()
                        }

                    let events: WorkItemEvent array =
                        Array.append
                            [|
                                {
                                    Event = Created(workItemId, 1L, Guid.Parse ownerId, Guid.Parse organizationId, partition, "fixture", initial)
                                    Metadata = metadata
                                }
                            |]
                            (subsequent
                             |> Array.map (fun value -> { Event = value; Metadata = metadata }))

                    let document = {| id = string workItemId; PartitionKey = string partition; GrainType = grain; State = events |}
                    let! _ = container.CreateItemAsync(document, PartitionKey(string partition))
                    inserted.Add(document.id, partition)
                    return document.id
                }

            let mutable index = 0

            while index < 257 do
                let! _ =
                    persist
                        repositoryId
                        Grace.Actors.Constants.StateName.WorkItem
                        (Some first)
                        [|
                            DescriptionSet second
                            DescriptionCleared cleared
                        |]

                index <- index + 1

            let! _ = persist repositoryId Grace.Actors.Constants.StateName.WorkItem (Some sameTextNewId) [||]
            let excluded = TextContentStorage.createDescription repositoryId producerId "excluded" "must be excluded"
            let! _ = persist repositoryId "excluded-text-diagnostic-grain" (Some excluded) [||]
            let! _ = persist (Guid.NewGuid()) Grace.Actors.Constants.StateName.WorkItem (Some first) [||]
            let! result = diagnose parameters
            Assert.That(result.DeclaredTextContentUtf8Bytes, Is.EqualTo 16L)
            Assert.That(result.DistinctTextContentCount, Is.EqualTo 3L)
            Assert.That(result.Scope.RepositoryId, Is.EqualTo repositoryId)
            Assert.That(result.Scope.OwnerId, Is.EqualTo(Guid.Parse ownerId))
            Assert.That(result.Scope.OrganizationId, Is.EqualTo(Guid.Parse organizationId))
            Assert.That(result.EnumerationFinishedAt, Is.GreaterThanOrEqualTo result.EnumerationStartedAt)
            let conflict = { first with TextContent = Some { first.TextContent.Value with Utf8ByteLength = 7L } }
            let! conflictId = persist repositoryId Grace.Actors.Constants.StateName.WorkItem (Some conflict) [||]
            use! conflictResponse = Client.PostAsync(route, createJsonContent parameters)
            let! conflictBody = conflictResponse.Content.ReadAsStringAsync()
            Assert.That(conflictResponse.StatusCode, Is.EqualTo HttpStatusCode.BadRequest, conflictBody)
            Assert.That((deserialize<GraceError> conflictBody).Error, Does.Contain "disagree")
            Assert.That(conflictBody, Does.Not.Contain "DeclaredTextContentUtf8Bytes")
            let! _ = container.DeleteItemAsync<obj>(conflictId, PartitionKey(string repositoryId))

            inserted.Remove(conflictId, repositoryId)
            |> ignore

            let! malformedId = persist repositoryId Grace.Actors.Constants.StateName.WorkItem (Some first) [||]
            let! original = container.ReadItemAsync<JsonElement>(malformedId, PartitionKey(string repositoryId))
            let originalJson = original.Resource.GetRawText()

            let controls =
                [|
                    "missing length"
                    "invalid identity"
                    "foreign scope"
                |]

            index <- 0

            while index < controls.Length do
                let control = controls[index]
                let document = JsonNode.Parse originalJson
                let state = document["State"]
                let firstEvent = state[0]
                let event = firstEvent["Event"]
                let createdData = event["created"]

                if control = "missing length" then
                    let description = createdData["description"]
                    let content = description["TextContent"]
                    Assert.That(content.AsObject().Remove "Utf8ByteLength", Is.True)
                elif control = "invalid identity" then
                    createdData["workItemId"] <- JsonValue.Create "not-a-guid"
                else
                    createdData["ownerId"] <- JsonValue.Create(string (Guid.NewGuid()))

                let! _ = container.UpsertItemAsync<JsonNode>(document, PartitionKey(string repositoryId))
                use! invalid = Client.PostAsync(route, createJsonContent parameters)
                let! invalidBody = invalid.Content.ReadAsStringAsync()
                Assert.That(invalid.StatusCode, Is.EqualTo HttpStatusCode.BadRequest, invalidBody)

                let expectedMessage =
                    if control = "missing length" then "Utf8ByteLength"
                    elif control = "invalid identity" then "decoded"
                    else "scope"

                Assert.That((deserialize<GraceError> invalidBody).Error, Does.Contain expectedMessage)
                Assert.That(invalidBody, Does.Not.Contain "DeclaredTextContentUtf8Bytes")
                index <- index + 1

            index <- 0

            while index < inserted.Count do
                let id, partition = inserted[index]
                let! _ = container.DeleteItemAsync<obj>(id, PartitionKey(string partition))
                index <- index + 1

            let! _ = persist repositoryId Grace.Actors.Constants.StateName.WorkItem None [||]
            use! zeroResponse = Client.PostAsync(route, createJsonContent parameters)
            let! zeroJson = zeroResponse.Content.ReadAsStringAsync()
            Assert.That(zeroResponse.StatusCode, Is.EqualTo HttpStatusCode.OK, zeroJson)

            let zero =
                (deserialize<GraceReturnValue<TextContentSizeDiagnostic>> zeroJson)
                    .ReturnValue

            Assert.That(zero.DeclaredTextContentUtf8Bytes, Is.Zero)
            Assert.That(zero.DistinctTextContentCount, Is.Zero)
            let responsePath = IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, "text-content-size-zero-response.json")
            IO.File.WriteAllText(responsePath, zeroJson)
            TestContext.AddTestAttachment(responsePath, "Actual admin known-zero response for the PowerShell operator contract check.")
        }
