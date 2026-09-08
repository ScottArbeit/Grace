namespace Grace.Server.Tests

open System
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Text.Json
open System.Text.Json.Nodes
open Grace.Server.DirectoryVersion
open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Common
open Grace.Types.DirectoryVersion
open Microsoft.Azure.Cosmos
open NUnit.Framework

/// Covers the actual SystemAdmin route against the shared Aspire host and isolated repository event documents.
type DirectoryVersionSizeDiagnosisHttpTests() =
    let route = "/admin/directory-version-size/diagnose"

    /// Asserts the standard success envelope and returns its declaration result.
    let diagnose parameters =
        task {
            use! response = Client.PostAsync(route, createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo HttpStatusCode.OK, body)

            return
                (deserialize<GraceReturnValue<DirectoryVersionSizeDiagnostic>> body)
                    .ReturnValue
        }

    /// Requires authorization before parsing, and rejects invalid explicit scopes through the existing error envelope.
    [<Test>]
    member _.``admin diagnostic denies non admins and invalid scopes``() =
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
            Assert.That(foreign.StatusCode, Is.EqualTo HttpStatusCode.BadRequest)
            let! errorBody = foreign.Content.ReadAsStringAsync()
            Assert.That(errorBody, Does.Not.Contain("DeclaredLogicalBytes"))
            Assert.That((deserialize<GraceError> errorBody).Error, Does.Contain("scope"))
            parameters.OwnerId <- ownerId
            parameters.RepositoryId <- string (Guid.NewGuid())
            use! absent = Client.PostAsync(route, createJsonContent parameters)
            Assert.That(absent.StatusCode, Is.EqualTo HttpStatusCode.BadRequest)
            parameters.RepositoryId <- string Guid.Empty
            use! empty = Client.PostAsync(route, createJsonContent parameters)
            Assert.That(empty.StatusCode, Is.EqualTo HttpStatusCode.BadRequest)
        }

    /// Reads multiple real Cosmos pages, validates direct content, and observes removal without counting other grains or partitions.
    [<Test>]
    member _.``admin diagnostic counts retained paged declarations and observes source removal``() =
        task {
            let repositoryId = Guid.NewGuid()

            let create =
                Parameters.Repository.CreateRepositoryParameters(
                    OwnerId = ownerId,
                    OrganizationId = organizationId,
                    RepositoryId = string repositoryId,
                    RepositoryName = $"Diagnostic{repositoryId:N}"
                )

            use! created = Client.PostAsync("/repository/create", createJsonContent create)
            let! createdBody = created.Content.ReadAsStringAsync()
            Assert.That(created.StatusCode, Is.EqualTo HttpStatusCode.OK, createdBody)

            let parameters =
                Parameters.Repository.GetRepositoryParameters(OwnerId = ownerId, OrganizationId = organizationId, RepositoryId = string repositoryId)

            let! empty = diagnose parameters
            Assert.That(empty.DeclaredLogicalBytes, Is.Zero)
            Assert.That(empty.DistinctContentCount, Is.Zero)
            use cosmos = AspireTestHost.createCosmosClient HostState.Value
            let container = cosmos.GetContainer(HostState.Value.CosmosDatabaseName, HostState.Value.CosmosContainerName)

            let manifest =
                FileManifest.Create(
                    "",
                    "suite",
                    String.replicate 64 "b",
                    20L,
                    "diagnostic-pool",
                    [
                        ContentBlock.Create(String.replicate 64 "c", 0L, 20L)
                    ]
                )

            let manifest = { manifest with ManifestAddress = ContentAddress.computeManifestAddressForManifest manifest }
            let inserted = ResizeArray<string * Guid>()

            /// Writes actual Grace event documents only into this test's repository partitions.
            let persist partition grain size includeExtras =
                task {
                    let directory = DirectoryVersion()
                    directory.DirectoryVersionId <- Guid.NewGuid()
                    directory.OwnerId <- Guid.Parse ownerId
                    directory.OrganizationId <- Guid.Parse organizationId
                    directory.RepositoryId <- partition
                    directory.CreatedAt <- getCurrentInstant ()
                    directory.Files.Add(FileVersion.CreateWithHashes "a.txt" (String.replicate 64 "a") (String.replicate 64 "b") "" true size)

                    if includeExtras then
                        let manifestFile = FileVersion.CreateWithHashes "manifest.txt" (String.replicate 64 "a") (String.replicate 64 "b") "" true 20L
                        manifestFile.ContentReference <- FileContentReference.FileManifest manifest
                        directory.Files.Add manifestFile
                        directory.Files.Add(FileVersion.CreateWithHashes "b.txt" (String.replicate 64 "a") (String.replicate 64 "b") "" true 10L)
                        directory.Files.Add(FileVersion.CreateWithHashes "empty.txt" (String.replicate 64 "a") (String.replicate 64 "b") "" true 0L)

                    let metadata =
                        {
                            Timestamp = getCurrentInstant ()
                            CorrelationId = "diagnostic-integration"
                            Principal = testUserId
                            ClientType = None
                            Properties = Dictionary<string, string>()
                        }

                    let events: DirectoryVersionEvent array =
                        [|
                            { Event = Created directory; Metadata = metadata }
                            { Event = LogicalDeleted "retained"; Metadata = metadata }
                        |]

                    let document = {| id = string directory.DirectoryVersionId; PartitionKey = string partition; GrainType = grain; State = events |}
                    let! _ = container.CreateItemAsync(document, PartitionKey(string partition))
                    inserted.Add(document.id, partition)
                    return document.id
                }

            let mutable index = 0

            while index < 257 do
                let! _ = persist repositoryId Grace.Actors.Constants.StateName.DirectoryVersion 10L true
                index <- index + 1

            let! _ = persist repositoryId "excluded-diagnostic-grain" 999L false
            let! _ = persist (Guid.NewGuid()) Grace.Actors.Constants.StateName.DirectoryVersion 888L false
            let! result = diagnose parameters
            Assert.That(result.DeclaredLogicalBytes, Is.EqualTo 40L)
            Assert.That(result.DistinctContentCount, Is.EqualTo 4L)
            Assert.That(result.Scope.RepositoryId, Is.EqualTo repositoryId)
            Assert.That(result.Scope.OwnerId, Is.EqualTo(Guid.Parse ownerId))
            Assert.That(result.Scope.OrganizationId, Is.EqualTo(Guid.Parse organizationId))
            Assert.That(result.EnumerationFinishedAt, Is.GreaterThanOrEqualTo result.EnumerationStartedAt)
            let! conflictId = persist repositoryId Grace.Actors.Constants.StateName.DirectoryVersion 11L false
            use! conflict = Client.PostAsync(route, createJsonContent parameters)
            let! conflictBody = conflict.Content.ReadAsStringAsync()
            Assert.That(conflict.StatusCode, Is.EqualTo HttpStatusCode.BadRequest, conflictBody)
            Assert.That(conflictBody, Does.Not.Contain("DeclaredLogicalBytes"))
            Assert.That((deserialize<GraceError> conflictBody).Error, Does.Contain("disagree"))
            let! _ = container.DeleteItemAsync<obj>(conflictId, PartitionKey(string repositoryId))

            inserted.Remove(conflictId, repositoryId)
            |> ignore

            let! incompleteId = persist repositoryId Grace.Actors.Constants.StateName.DirectoryVersion 0L false
            let! original = container.ReadItemAsync<JsonElement>(incompleteId, PartitionKey(string repositoryId))
            let originalJson = original.Resource.GetRawText()
            let mutable missingIndex = 0

            let missingFields =
                [|
                    "Files"
                    "null Size"
                    "malformed Size"
                |]

            while missingIndex < missingFields.Length do
                let field = missingFields[missingIndex]
                let document = JsonNode.Parse originalJson
                let state = document["State"]
                let first = state[0]
                let event = first["Event"]
                let createdData = event["created"]
                let files = createdData["Files"]

                if field = "Files" then
                    Assert.That(createdData.AsObject().Remove field, Is.True)
                else
                    let file = files[0]
                    file["Size"] <- if field = "null Size" then null else JsonValue.Create("invalid")

                let! _ = container.UpsertItemAsync<JsonNode>(document, PartitionKey(string repositoryId))
                use! incomplete = Client.PostAsync(route, createJsonContent parameters)
                let! incompleteBody = incomplete.Content.ReadAsStringAsync()
                Assert.That(incomplete.StatusCode, Is.EqualTo HttpStatusCode.BadRequest, incompleteBody)
                Assert.That((deserialize<GraceError> incompleteBody).Error, Does.Contain(if field = "Files" then "Files" else "Size"))
                Assert.That(incompleteBody, Does.Not.Contain("DeclaredLogicalBytes"))
                missingIndex <- missingIndex + 1

            index <- 0

            while index < inserted.Count do
                let id, partition = inserted[index]
                let! _ = container.DeleteItemAsync<obj>(id, PartitionKey(string partition))
                index <- index + 1

            let! removed = diagnose parameters
            Assert.That(removed.DeclaredLogicalBytes, Is.Zero)
            Assert.That(removed.DistinctContentCount, Is.Zero)
            let! zeroId = persist repositoryId Grace.Actors.Constants.StateName.DirectoryVersion 0L false
            use! zeroResponse = Client.PostAsync(route, createJsonContent parameters)
            let! zeroJson = zeroResponse.Content.ReadAsStringAsync()
            Assert.That(zeroResponse.StatusCode, Is.EqualTo HttpStatusCode.OK, zeroJson)

            let zeroLength =
                (deserialize<GraceReturnValue<DirectoryVersionSizeDiagnostic>> zeroJson)
                    .ReturnValue

            Assert.That(zeroLength.DeclaredLogicalBytes, Is.Zero)
            Assert.That(zeroLength.DistinctContentCount, Is.EqualTo 1L)
            let responsePath = IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, "directory-version-size-zero-response.json")
            IO.File.WriteAllText(responsePath, zeroJson)
            TestContext.AddTestAttachment(responsePath, "Actual admin zero response for the PowerShell operator contract check.")
            let! _ = container.DeleteItemAsync<obj>(zeroId, PartitionKey(string repositoryId))
            return ()
        }
