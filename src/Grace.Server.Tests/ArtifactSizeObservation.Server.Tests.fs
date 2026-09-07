namespace Grace.Server.Tests

open System
open System.Net
open System.Net.Http
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Grace.Operations.Data
open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Common
open Grace.Types.Artifact
open Grace.Server
open Grace.Types.UsageObservation
open Microsoft.Azure.Cosmos
open Microsoft.Data.SqlClient
open NodaTime.Text
open NUnit.Framework

/// Exercises completed observations through hosted HTTP, real Cosmos and the worker-created SQL table.
type ArtifactSizeObservationHttpTests() =
    /// Builds the explicit observation route without choosing an identity for the operator.
    let route id = $"/admin/artifact-size/observations/{id}"

    /// Requires a complete success and retains the original hosted JSON for the operator checks.
    let capture id parameters =
        task {
            use! response = Client.PostAsync(route id, createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo HttpStatusCode.OK, body)

            return
                (deserialize<GraceReturnValue<ArtifactSizeObservation>> body)
                    .ReturnValue,
                body
        }

    /// Queries durable row counts independently of the implementation's lookup and acceptance helpers.
    let countRows table repository =
        task {
            use connection = new SqlConnection(HostState.Value.OperationsSqlConnectionString)
            do! connection.OpenAsync()
            use command = new SqlCommand($"SELECT COUNT(*) FROM {table} WHERE RepositoryId=@repository", connection)

            command.Parameters.AddWithValue("@repository", repository)
            |> ignore

            let! count = command.ExecuteScalarAsync()
            return unbox<int> count
        }

    /// Proves authorization before parsing for both methods, invalid input, absent reads and SQL-scope payload isolation.
    [<Test>]
    member _.``observation routes authorize before parsing and reject invalid explicit inputs``() =
        task {
            use nonAdmin = new HttpClient(BaseAddress = Client.BaseAddress)
            nonAdmin.DefaultRequestHeaders.Add("x-grace-user-id", string (Guid.NewGuid()))
            use! denied = nonAdmin.PostAsync(route "not-a-guid", new StringContent("{", Text.Encoding.UTF8, "application/json"))
            Assert.That(denied.StatusCode, Is.EqualTo HttpStatusCode.Forbidden)
            use! deniedRead = nonAdmin.GetAsync(route "not-a-guid")
            Assert.That(deniedRead.StatusCode, Is.EqualTo HttpStatusCode.Forbidden)
            let parameters = Parameters.Repository.GetRepositoryParameters(OwnerId = ownerId, OrganizationId = organizationId, RepositoryId = repositoryIds[0])
            use! invalid = Client.PostAsync(route Guid.Empty, createJsonContent parameters)
            Assert.That(invalid.StatusCode, Is.EqualTo HttpStatusCode.BadRequest)
            use! malformed = Client.PostAsync(route (Guid.NewGuid()), new StringContent("{", Text.Encoding.UTF8, "application/json"))
            Assert.That(malformed.StatusCode, Is.EqualTo HttpStatusCode.BadRequest)
            parameters.RepositoryName <- "forbidden-name"
            use! named = Client.PostAsync(route (Guid.NewGuid()), createJsonContent parameters)
            Assert.That(named.StatusCode, Is.EqualTo HttpStatusCode.BadRequest)
            parameters.RepositoryName <- ""
            parameters.RepositoryId <- string (Guid.NewGuid())
            use! absentRepository = Client.PostAsync(route (Guid.NewGuid()), createJsonContent parameters)
            Assert.That(absentRepository.StatusCode, Is.EqualTo HttpStatusCode.BadRequest)
            let query = $"?OwnerId={ownerId}&OrganizationId={organizationId}&RepositoryId={repositoryIds[0]}"
            use! absent = Client.GetAsync(route (Guid.NewGuid()) + query)
            Assert.That(absent.StatusCode, Is.EqualTo HttpStatusCode.NotFound)

            use! namedRead =
                Client.GetAsync(
                    route (Guid.NewGuid())
                    + query
                    + "&OwnerName=forbidden"
                )

            Assert.That(namedRead.StatusCode, Is.EqualTo HttpStatusCode.BadRequest)
            use! invalidScope = Client.GetAsync(route (Guid.NewGuid()))
            Assert.That(invalidScope.StatusCode, Is.EqualTo HttpStatusCode.BadRequest)
        }

    /// Captures a positive reading, removes its source, retries the exact row, captures zero and reads history after deletion.
    [<Test>]
    member _.``capture retry new ID and historical read preserve immutable SQL observations``() =
        task {
            let repositoryId = Guid.NewGuid()

            let parameters =
                Parameters.Repository.GetRepositoryParameters(OwnerId = ownerId, OrganizationId = organizationId, RepositoryId = string repositoryId)

            let create =
                Parameters.Repository.CreateRepositoryParameters(
                    OwnerId = ownerId,
                    OrganizationId = organizationId,
                    RepositoryId = string repositoryId,
                    RepositoryName = $"Observation{repositoryId:N}"
                )

            use! created = Client.PostAsync("/repository/create", createJsonContent create)
            let! createdBody = created.Content.ReadAsStringAsync()
            Assert.That(created.StatusCode, Is.EqualTo HttpStatusCode.OK, createdBody)
            let! rawBefore = countRows "ops.RawUsageFact" repositoryId
            let! aggregateBefore = countRows "ops.UsageAggregateMinute" repositoryId
            let! directoryBefore = countRows "ops.DirectoryVersionSizeObservation" repositoryId
            let! textBefore = countRows "ops.TextContentSizeObservation" repositoryId
            use cosmos = AspireTestHost.createCosmosClient HostState.Value
            let container = cosmos.GetContainer(HostState.Value.CosmosDatabaseName, HostState.Value.CosmosContainerName)

            /// Creates an unattached declaration through the public route and actual Artifact actor.
            let createArtifact size =
                task {
                    let input =
                        Parameters.Artifact.CreateArtifactParameters(
                            OwnerId = ownerId,
                            OrganizationId = organizationId,
                            RepositoryId = string repositoryId,
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

            /// Reads complete source documents independently, including ETags to detect observation writes.
            let snapshots () =
                task {
                    let query =
                        QueryDefinition("SELECT * FROM c WHERE c.PartitionKey=@repository AND c.GrainType=@grain")
                            .WithParameter("@repository", string repositoryId)
                            .WithParameter("@grain", Grace.Actors.Constants.StateName.Artifact)

                    use iterator =
                        container.GetItemQueryIterator<JsonElement>(
                            query,
                            requestOptions = QueryRequestOptions(PartitionKey = PartitionKey(string repositoryId))
                        )

                    let rows = ResizeArray<JsonElement>()

                    while iterator.HasMoreResults do
                        let! page = iterator.ReadNextAsync()
                        let mutable index = 0
                        let values = page.Resource |> Seq.toArray

                        while index < values.Length do
                            rows.Add(values[ index ].Clone())
                            index <- index + 1

                    return
                        rows.ToArray()
                        |> Array.sortBy (fun row -> row.GetProperty("id").GetString())
                }

            let! positive = createArtifact 13L
            let! zeroArtifact = createArtifact 0L
            let! beforeCapture = snapshots ()
            Assert.That(beforeCapture.Length, Is.EqualTo 2)

            let declared =
                beforeCapture
                |> Array.collect Grace.Actors.Services.decodeArtifactSizeDocument

            Assert.That(
                declared
                |> Array.exists (fun value ->
                    value.ArtifactId = positive.ArtifactId
                    && value.Size = 13L),
                Is.True
            )

            Assert.That(
                declared
                |> Array.exists (fun value ->
                    value.ArtifactId = zeroArtifact.ArtifactId
                    && value.Size = 0L),
                Is.True
            )

            let id = Guid.NewGuid()
            let! original, originalJson = capture id parameters
            Assert.That(original.ObservationId, Is.EqualTo id)
            Assert.That(original.DeclaredArtifactBytes, Is.EqualTo 13L)
            Assert.That(original.DistinctArtifactCount, Is.EqualTo 2L)
            Assert.That(original.Scope.OwnerId, Is.EqualTo(Guid.Parse ownerId))
            Assert.That(original.Scope.OrganizationId, Is.EqualTo(Guid.Parse organizationId))
            Assert.That(original.Scope.RepositoryId, Is.EqualTo repositoryId)
            Assert.That(original.EnumerationFinishedAt, Is.GreaterThanOrEqualTo original.EnumerationStartedAt)
            let! afterCapture = snapshots ()

            Assert.That(
                afterCapture
                |> Array.map (fun row -> row.GetRawText()),
                Is.EqualTo(
                    beforeCapture
                    |> Array.map (fun row -> row.GetRawText())
                    |> box
                )
            )

            let! _ = createArtifact 5L
            let! retry, _ = capture id parameters
            Assert.That(retry, Is.EqualTo original)
            let query = $"?OwnerId={ownerId}&OrganizationId={organizationId}&RepositoryId={repositoryId}"
            use! read = Client.GetAsync(route id + query)
            let! readJson = read.Content.ReadAsStringAsync()
            Assert.That(read.StatusCode, Is.EqualTo HttpStatusCode.OK, readJson)

            Assert.That(
                (deserialize<GraceReturnValue<ArtifactSizeObservation>> readJson)
                    .ReturnValue,
                Is.EqualTo original
            )

            let! current, _ = capture (Guid.NewGuid()) parameters
            Assert.That(current.DeclaredArtifactBytes, Is.EqualTo 18L)
            Assert.That(current.DistinctArtifactCount, Is.EqualTo 3L)
            let! toRemove = snapshots ()
            let mutable removeIndex = 0
            // Fixture removal executes Cosmos deletion, not Artifact actor lifecycle cleanup.
            while removeIndex < toRemove.Length do
                let! _ =
                    container.DeleteItemAsync<obj>(
                        toRemove[removeIndex]
                            .GetProperty("id")
                            .GetString(),
                        PartitionKey(string repositoryId)
                    )

                removeIndex <- removeIndex + 1

            let! removed = snapshots ()
            Assert.That(removed, Is.Empty)
            let! removedRetry, _ = capture id parameters
            Assert.That(removedRetry, Is.EqualTo original)
            use! removedRead = Client.GetAsync(route id + query)
            let! removedJson = removedRead.Content.ReadAsStringAsync()
            Assert.That(removedRead.StatusCode, Is.EqualTo HttpStatusCode.OK, removedJson)

            Assert.That(
                (deserialize<GraceReturnValue<ArtifactSizeObservation>> removedJson)
                    .ReturnValue,
                Is.EqualTo original
            )

            let! zero, zeroJson = capture (Guid.NewGuid()) parameters
            Assert.That(zero.DeclaredArtifactBytes, Is.Zero)
            Assert.That(zero.DistinctArtifactCount, Is.Zero)
            Assert.That(zero.ObservationId, Is.Not.EqualTo id)
            let output = IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, "artifact-observation-zero-response.json")
            IO.File.WriteAllText(output, zeroJson)

            // NUnit's error channel remains visible for passing tests under the default CI console logger.
            TestContext.Error.WriteLine(
                "ARTIFACT_SIZE_OBSERVATION_HOSTED_JSON_BASE64:"
                + Convert.ToBase64String(Text.Encoding.UTF8.GetBytes zeroJson)
            )

            TestContext.AddTestAttachment(output, "Actual hosted zero envelope for operator validation.")

            IO.File.WriteAllText(IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, "artifact-observation-positive-response.json"), originalJson)

            let mutable index = 0

            while index < 3 do
                let foreign =
                    Parameters.Repository.GetRepositoryParameters(OwnerId = ownerId, OrganizationId = organizationId, RepositoryId = string repositoryId)

                match index with
                | 0 -> foreign.OwnerId <- string (Guid.NewGuid())
                | 1 -> foreign.OrganizationId <- string (Guid.NewGuid())
                | _ -> foreign.RepositoryId <- string (Guid.NewGuid())

                use! collision = Client.PostAsync(route id, createJsonContent foreign)
                let! body = collision.Content.ReadAsStringAsync()
                Assert.That(collision.StatusCode, Is.EqualTo HttpStatusCode.Conflict, body)
                Assert.That(body, Does.Not.Contain "DeclaredArtifactBytes")
                Assert.That(body, Does.Not.Contain "EnumerationStartedAt")

                use! readCollision =
                    Client.GetAsync(
                        route id
                        + $"?OwnerId={foreign.OwnerId}&OrganizationId={foreign.OrganizationId}&RepositoryId={foreign.RepositoryId}"
                    )

                Assert.That(readCollision.StatusCode, Is.EqualTo HttpStatusCode.Conflict)
                let! readCollisionBody = readCollision.Content.ReadAsStringAsync()
                Assert.That(readCollisionBody, Does.Not.Contain "DeclaredArtifactBytes")
                Assert.That(readCollisionBody, Does.Not.Contain "EnumerationStartedAt")
                index <- index + 1

            let badId = Guid.NewGuid()

            let invalid =
                {|
                    id = string (Guid.NewGuid())
                    PartitionKey = string repositoryId
                    GrainType = Grace.Actors.Constants.StateName.Artifact
                    State = [| {| Event = "broken" |} |]
                |}

            let! _ = container.CreateItemAsync(invalid, PartitionKey(string repositoryId))
            use! failed = Client.PostAsync(route badId, createJsonContent parameters)
            let! failureBody = failed.Content.ReadAsStringAsync()
            Assert.That(failed.StatusCode, Is.EqualTo HttpStatusCode.BadRequest, failureBody)
            Assert.That(failureBody, Does.Not.Contain "DeclaredArtifactBytes")

            let! absentFailed = ArtifactSizeObservations.lookup HostState.Value.OperationsSqlConnectionString badId original.Scope CancellationToken.None

            Assert.That((absentFailed = Ok None), Is.True)
            let! _ = container.DeleteItemAsync<obj>(invalid.id, PartitionKey(string repositoryId))

            let delete =
                Parameters.Repository.DeleteRepositoryParameters(
                    OwnerId = ownerId,
                    OrganizationId = organizationId,
                    RepositoryId = string repositoryId,
                    Force = true,
                    DeleteReason = "observation historical-read test"
                )

            use! deleted = Client.PostAsync("/repository/delete", createJsonContent delete)
            let! deletedBody = deleted.Content.ReadAsStringAsync()
            Assert.That(deleted.StatusCode, Is.EqualTo HttpStatusCode.OK, deletedBody)
            let! historical, _ = capture id parameters
            Assert.That(historical, Is.EqualTo original)
            use! historicalRead = Client.GetAsync(route id + query)
            let! historicalJson = historicalRead.Content.ReadAsStringAsync()
            Assert.That(historicalRead.StatusCode, Is.EqualTo HttpStatusCode.OK, historicalJson)

            Assert.That(
                (deserialize<GraceReturnValue<ArtifactSizeObservation>> historicalJson)
                    .ReturnValue,
                Is.EqualTo original
            )

            use! deletedNew = Client.PostAsync(route (Guid.NewGuid()), createJsonContent parameters)
            Assert.That(deletedNew.StatusCode, Is.EqualTo HttpStatusCode.BadRequest)
            let! count = countRows "ops.ArtifactSizeObservation" repositoryId
            let! rawAfter = countRows "ops.RawUsageFact" repositoryId
            let! aggregateAfter = countRows "ops.UsageAggregateMinute" repositoryId
            Assert.That(count, Is.EqualTo 3)
            Assert.That(rawAfter, Is.EqualTo rawBefore)
            Assert.That(aggregateAfter, Is.EqualTo aggregateBefore)
            let! directoryAfter = countRows "ops.DirectoryVersionSizeObservation" repositoryId
            Assert.That(directoryAfter, Is.EqualTo directoryBefore)
            let! textAfter = countRows "ops.TextContentSizeObservation" repositoryId
            Assert.That(textAfter, Is.EqualTo textBefore)
            use sql = new SqlConnection(HostState.Value.OperationsSqlConnectionString)
            do! sql.OpenAsync()

            use command =
                new SqlCommand(
                    "SELECT DeclaredArtifactBytes,DistinctArtifactCount,EnumerationStartedAt,EnumerationFinishedAt FROM ops.ArtifactSizeObservation WHERE ObservationId=@id",
                    sql
                )

            command.Parameters.AddWithValue("@id", id)
            |> ignore

            use! stored = command.ExecuteReaderAsync()
            let! exists = stored.ReadAsync()
            Assert.That(exists, Is.True)
            Assert.That(stored.GetInt64 0, Is.EqualTo 13L)
            Assert.That(stored.GetInt64 1, Is.EqualTo 2L)
            Assert.That(stored.GetString 2, Is.EqualTo(InstantPattern.ExtendedIso.Format original.EnumerationStartedAt))
            Assert.That(stored.GetString 3, Is.EqualTo(InstantPattern.ExtendedIso.Format original.EnumerationFinishedAt))
        }

    /// Releases two completed candidates against a real locked SQL ID and requires both production calls to return one winner.
    [<Test>]
    member _.``production SQL acceptance returns one exact winner under concurrent windows``() =
        task {
            let connectionString = HostState.Value.OperationsSqlConnectionString

            let first =
                {
                    ObservationId = Guid.NewGuid()
                    Scope = { OwnerId = Guid.NewGuid(); OrganizationId = Guid.NewGuid(); RepositoryId = Guid.NewGuid() }
                    DeclaredArtifactBytes = 71L
                    DistinctArtifactCount = 2L
                    EnumerationStartedAt =
                        InstantPattern
                            .ExtendedIso
                            .Parse(
                                "2026-09-07T01:02:03.123456789Z"
                            )
                            .Value
                    EnumerationFinishedAt =
                        InstantPattern
                            .ExtendedIso
                            .Parse(
                                "2026-09-07T01:02:03.123456790Z"
                            )
                            .Value
                }

            let second =
                { first with
                    DeclaredArtifactBytes = 99L
                    DistinctArtifactCount = 3L
                    EnumerationStartedAt = first.EnumerationFinishedAt
                    EnumerationFinishedAt = first.EnumerationFinishedAt
                }

            use gateConnection = new SqlConnection(connectionString)
            do! gateConnection.OpenAsync()
            use gateTransaction = gateConnection.BeginTransaction(System.Data.IsolationLevel.Serializable)

            use gateCommand =
                new SqlCommand(
                    "SELECT ObservationId FROM ops.ArtifactSizeObservation WITH(UPDLOCK,HOLDLOCK) WHERE ObservationId=@id",
                    gateConnection,
                    gateTransaction
                )

            gateCommand.Parameters.AddWithValue("@id", first.ObservationId)
            |> ignore

            let! _ = gateCommand.ExecuteScalarAsync()
            let left = ArtifactSizeObservations.accept connectionString first CancellationToken.None
            let right = ArtifactSizeObservations.accept connectionString second CancellationToken.None
            do! gateTransaction.CommitAsync()
            let! results = Task.WhenAll(left, right)
            Assert.That(results[0], Is.EqualTo results[1])
            Assert.That(results[0] = Ok first || results[0] = Ok second, Is.True)
            let! stored = ArtifactSizeObservations.lookup connectionString first.ObservationId first.Scope CancellationToken.None
            Assert.That(stored, Is.EqualTo(Result.map Some results[0]))
            let! count = countRows "ops.ArtifactSizeObservation" first.Scope.RepositoryId
            Assert.That(count, Is.EqualTo 1)
        }
