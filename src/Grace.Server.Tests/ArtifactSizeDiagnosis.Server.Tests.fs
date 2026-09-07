namespace Grace.Server.Tests

open System
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Grace.Server.ArtifactSizeDiagnosis
open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Artifact
open Grace.Types.Common
open Microsoft.Azure.Cosmos
open Microsoft.Data.SqlClient
open NUnit.Framework

/// Exercises public Artifact creation and the internal diagnostic against actual hosted actor persistence.
type ArtifactSizeDiagnosisHttpTests() =
    let route = "/admin/artifact-size/diagnose"

    /// Retains the exact successful server envelope for byte-preserving operator replay.
    let diagnose parameters =
        task {
            use! response = Client.PostAsync(route, createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo HttpStatusCode.OK, body)

            return
                (deserialize<GraceReturnValue<ArtifactSizeDiagnostic>> body)
                    .ReturnValue,
                body
        }

    /// Requires an error envelope with neither diagnostic quantity.
    let rejected parameters =
        task {
            use! response = Client.PostAsync(route, createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo HttpStatusCode.BadRequest, body)
            Assert.That((deserialize<GraceError> body).Error, Is.Not.Empty)
            Assert.That(body, Does.Not.Contain "DeclaredArtifactBytes")
            Assert.That(body, Does.Not.Contain "DistinctArtifactCount")
        }

    /// Confirms SystemAdmin authorization precedes parsing and all unsupported selectors fail.
    [<Test>]
    member _.``artifact diagnostic authorizes before parsing and rejects invalid scope``() =
        task {
            use nonAdmin = new HttpClient(BaseAddress = Client.BaseAddress)
            nonAdmin.DefaultRequestHeaders.Add("x-grace-user-id", string (Guid.NewGuid()))
            use! denied = nonAdmin.PostAsync(route, new StringContent("{", Encoding.UTF8, "application/json"))
            Assert.That(denied.StatusCode, Is.EqualTo HttpStatusCode.Forbidden)
            use! invalidJson = Client.PostAsync(route, new StringContent("{", Encoding.UTF8, "application/json"))
            Assert.That(invalidJson.StatusCode, Is.EqualTo HttpStatusCode.BadRequest)
            let parameters = Parameters.Repository.GetRepositoryParameters(OwnerId = ownerId, OrganizationId = organizationId, RepositoryId = repositoryIds[0])
            parameters.OwnerName <- "name"
            do! rejected parameters
            parameters.OwnerName <- ""
            parameters.OrganizationName <- "name"
            do! rejected parameters
            parameters.OrganizationName <- ""
            parameters.RepositoryName <- "name"
            do! rejected parameters
            parameters.RepositoryName <- ""
            parameters.OwnerId <- string (Guid.NewGuid())
            do! rejected parameters
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- string (Guid.NewGuid())
            do! rejected parameters
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- string (Guid.NewGuid())
            do! rejected parameters
            parameters.RepositoryId <- string Guid.Empty
            do! rejected parameters
            parameters.RepositoryId <- "invalid"
            do! rejected parameters
        }

    /// Proves actual public positive/zero creation, complete pages, retained seeded lifecycle states and no storage accounting mutation.
    [<Test>]
    member _.``public Artifact creates feed a complete paged declaration diagnostic``() =
        task {
            let repository = Guid.NewGuid()
            let parameters = Parameters.Repository.GetRepositoryParameters(OwnerId = ownerId, OrganizationId = organizationId, RepositoryId = string repository)

            let createRepository =
                Parameters.Repository.CreateRepositoryParameters(
                    OwnerId = ownerId,
                    OrganizationId = organizationId,
                    RepositoryId = string repository,
                    RepositoryName = $"ArtifactDiagnostic{repository:N}"
                )

            use! repositoryResponse = Client.PostAsync("/repository/create", createJsonContent createRepository)
            let! repositoryBody = repositoryResponse.Content.ReadAsStringAsync()
            Assert.That(repositoryResponse.StatusCode, Is.EqualTo HttpStatusCode.OK, repositoryBody)
            let! empty, _ = diagnose parameters
            Assert.That(empty.DeclaredArtifactBytes, Is.Zero)
            Assert.That(empty.DistinctArtifactCount, Is.Zero)

            /// Calls the real Create route without uploading or attaching the declared content.
            let create size =
                task {
                    let input =
                        Parameters.Artifact.CreateArtifactParameters(
                            OwnerId = ownerId,
                            OrganizationId = organizationId,
                            RepositoryId = string repository,
                            ArtifactType = "Prompt",
                            MimeType = "text/plain",
                            Size = size
                        )

                    use! response = Client.PostAsync("/artifact/create", createJsonContent input)
                    let! body = response.Content.ReadAsStringAsync()
                    Assert.That(response.StatusCode, Is.EqualTo HttpStatusCode.OK, body)

                    return
                        (deserialize<GraceReturnValue<ArtifactCreateResult>> body)
                            .ReturnValue
                }

            let! positive = create 13L
            let! zero = create 0L
            let! createdResult, _ = diagnose parameters
            Assert.That(createdResult.DeclaredArtifactBytes, Is.EqualTo 13L)
            Assert.That(createdResult.DistinctArtifactCount, Is.EqualTo 2L)
            use cosmos = AspireTestHost.createCosmosClient HostState.Value
            let container = cosmos.GetContainer(HostState.Value.CosmosDatabaseName, HostState.Value.CosmosContainerName)
            let partition = PartitionKey(string repository)

            /// Reads all scoped stored documents, retaining exact snapshots and ETags to detect diagnostic writes.
            let stored () =
                task {
                    let query =
                        QueryDefinition("SELECT * FROM c WHERE c.PartitionKey=@repo")
                            .WithParameter("@repo", string repository)

                    use iterator =
                        container.GetItemQueryIterator<JsonElement>(query, requestOptions = QueryRequestOptions(PartitionKey = partition, MaxItemCount = 256))

                    let documents = ResizeArray<JsonElement>()
                    let mutable pages = 0

                    while iterator.HasMoreResults do
                        let! page = iterator.ReadNextAsync()
                        pages <- pages + 1

                        for document in page.Resource do
                            documents.Add(document.Clone())

                    return documents.ToArray(), pages
                }

            let! initial, _ = stored ()

            let artifactDocuments =
                initial
                |> Array.filter (fun doc -> doc.GetProperty("GrainType").GetString() = "Artifact")

            Assert.That(artifactDocuments.Length, Is.EqualTo 2, "Both documents must originate from public Create/actor persistence.")

            let positiveDocument =
                artifactDocuments
                |> Array.find (fun doc -> (decodeDocument doc).[0].ArtifactId = positive.ArtifactId)

            let zeroDocument =
                artifactDocuments
                |> Array.find (fun doc -> (decodeDocument doc).[0].ArtifactId = zero.ArtifactId)

            Assert.That(
                zeroDocument.GetProperty("State").[0]
                    .GetProperty("Size")
                    .GetInt64(),
                Is.Zero
            )

            let realEvents = decodeDocument positiveDocument
            let inserted = ResizeArray<string>()

            /// Seeds duplicate and lifecycle envelopes using the real public event shape; this does not execute actor cleanup.
            let seed grain events =
                task {
                    let id = string (Guid.NewGuid())
                    let document = {| id = id; PartitionKey = string repository; GrainType = grain; State = events |}
                    let! _ = container.CreateItemAsync(document, partition)
                    inserted.Add id
                    return id
                }

            for _ in 1..257 do
                let! _ = seed "Artifact" realEvents
                ()

            let current = realEvents[ 0 ].ToMetadata()

            let retained =
                { current with
                    ArtifactId = Guid.NewGuid()
                    Size = 7L
                    BlobPath = "grace-artifacts/seeded-lifecycle"
                    DeletedAt = Some(getCurrentInstant ())
                    BlobDeleted = true
                    WorkItemLinkRemoved = true
                }

            let lifecycle =
                [|
                    ArtifactEvent.FromMetadata(
                        ArtifactEventNames.Created,
                        { retained with DeletedAt = None; BlobDeleted = false; WorkItemLinkRemoved = false },
                        realEvents[0].Metadata
                    )
                    ArtifactEvent.FromMetadata(ArtifactEventNames.LogicalDeleted, retained, realEvents[0].Metadata)
                    ArtifactEvent.FromMetadata(ArtifactEventNames.BlobDeleted, retained, realEvents[0].Metadata)
                    ArtifactEvent.FromMetadata(ArtifactEventNames.WorkItemLinkRemoved, retained, realEvents[0].Metadata)
                |]

            let! _ = seed "Artifact" lifecycle
            let! _ = seed "Artifact" ([||]: ArtifactEvent array)
            let! _ = seed "excluded-artifact-diagnostic-grain" [| { realEvents[0] with Size = 999L } |]
            let! blobs = AspireTestHost.getAzureStorageContainerClientAsync HostState.Value (string repository)

            let! positiveBlobBefore =
                blobs
                    .GetBlobClient(positive.BlobPath)
                    .ExistsAsync()

            let! zeroBlobBefore = blobs.GetBlobClient(zero.BlobPath).ExistsAsync()
            Assert.That(positiveBlobBefore.Value, Is.False)
            Assert.That(zeroBlobBefore.Value, Is.False)

            /// Independently counts the existing usage and observation SQL tables for this isolated repository.
            let sqlCounts () =
                task {
                    use connection = new SqlConnection(HostState.Value.OperationsSqlConnectionString)
                    do! connection.OpenAsync()
                    let counts = ResizeArray<int>()

                    for table in
                        [
                            "ops.RawUsageFact"
                            "ops.UsageAggregateMinute"
                            "ops.DirectoryVersionSizeObservation"
                            "ops.TextContentSizeObservation"
                        ] do
                        use command = new SqlCommand($"SELECT COUNT(*) FROM {table} WHERE RepositoryId=@repository", connection)

                        command.Parameters.AddWithValue("@repository", repository)
                        |> ignore

                        let! count = command.ExecuteScalarAsync()
                        counts.Add(unbox<int> count)

                    return counts.ToArray()
                }

            let! sqlBefore = sqlCounts ()
            let! before, pages = stored ()
            Assert.That(pages, Is.GreaterThan 1, "The source exceeds the production page hint.")
            let! result, wire = diagnose parameters
            Assert.That(result.DeclaredArtifactBytes, Is.EqualTo 20L)
            Assert.That(result.DistinctArtifactCount, Is.EqualTo 3L)
            Assert.That(result.Scope.RepositoryId, Is.EqualTo repository)
            Assert.That(result.Scope.OwnerId, Is.EqualTo(Guid.Parse ownerId))
            Assert.That(result.Scope.OrganizationId, Is.EqualTo(Guid.Parse organizationId))
            Assert.That(result.EnumerationFinishedAt, Is.GreaterThanOrEqualTo result.EnumerationStartedAt)
            let! after, _ = stored ()

            /// Sorts complete stored snapshots for a stable no-write comparison.
            let wires (values: JsonElement array) =
                values
                |> Array.map (fun value -> value.GetRawText())
                |> Array.sort

            Assert.That(wires after, Is.EqualTo(box (wires before)), "Diagnostic must not mutate source, usage or counter documents.")
            let! sqlAfter = sqlCounts ()
            Assert.That(sqlAfter, Is.EqualTo(box sqlBefore))

            let! positiveBlobAfter =
                blobs
                    .GetBlobClient(positive.BlobPath)
                    .ExistsAsync()

            let! zeroBlobAfter = blobs.GetBlobClient(zero.BlobPath).ExistsAsync()
            Assert.That(positiveBlobAfter.Value, Is.False)
            Assert.That(zeroBlobAfter.Value, Is.False)

            TestContext.Error.WriteLine(
                "ARTIFACT_SIZE_DIAGNOSTIC_HOSTED_JSON_BASE64:"
                + Convert.ToBase64String(Encoding.UTF8.GetBytes wire)
            )

            let output = IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, "artifact-size-diagnostic-hosted-response.json")
            IO.File.WriteAllText(output, wire)
            TestContext.AddTestAttachment(output, "Exact hosted positive and zero Artifact declaration envelope for Windows operator replay.")

            let! malformedId = seed "Artifact" realEvents
            let! original = container.ReadItemAsync<JsonElement>(malformedId, partition)

            for kind in
                [
                    "missing-state"
                    "null-state"
                    "missing-size"
                    "foreign-owner"
                    "foreign-organization"
                    "foreign-repository"
                    "changed-identity"
                    "duplicate-size"
                ] do
                let node = JsonNode.Parse(original.Resource.GetRawText())

                match kind with
                | "missing-state" -> node.AsObject().Remove("State") |> ignore
                | "null-state" -> node.["State"] <- null
                | "missing-size" ->
                    node.["State"].[0].AsObject().Remove("Size")
                    |> ignore
                | "duplicate-size" -> node.["State"].[0].["Size"] <- JsonValue.Create 14L
                | _ ->
                    let changed = node.["State"].[0].DeepClone()
                    changed.["Event"] <- JsonValue.Create ArtifactEventNames.LogicalDeleted

                    let field =
                        match kind with
                        | "foreign-owner" -> "OwnerId"
                        | "foreign-organization" -> "OrganizationId"
                        | "foreign-repository" -> "RepositoryId"
                        | _ -> "ArtifactId"

                    changed.[field] <- JsonValue.Create(string (Guid.NewGuid()))
                    node.["State"].AsArray().Add changed

                let! _ = container.UpsertItemAsync<JsonNode>(node, partition)
                do! rejected parameters

            let! all, _ = stored ()

            for doc in all do
                if doc.GetProperty("GrainType").GetString() = "Artifact" then
                    let! _ = container.DeleteItemAsync<obj>(doc.GetProperty("id").GetString(), partition)
                    ()

            let! cleared, _ = diagnose parameters
            Assert.That(cleared.DeclaredArtifactBytes, Is.Zero)
            Assert.That(cleared.DistinctArtifactCount, Is.Zero)
        }
