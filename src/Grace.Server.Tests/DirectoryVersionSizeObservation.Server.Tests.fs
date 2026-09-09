namespace Grace.Server.Tests

open System
open System.Collections.Generic
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
open Grace.Types.DirectoryVersion
open Grace.Types.UsageObservation
open Microsoft.Azure.Cosmos
open Microsoft.Data.SqlClient
open NodaTime.Text
open NUnit.Framework

/// Exercises completed observations through hosted HTTP, real Cosmos and the worker-created SQL table.
type DirectoryVersionSizeObservationHttpTests() =
    /// Builds the explicit observation route without choosing an identity for the operator.
    let route id = $"/admin/directory-version-size/observations/{id}"

    /// Requires a complete success and retains the original hosted JSON for the operator checks.
    let capture id parameters =
        task {
            use! response = Client.PostAsync(route id, createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo HttpStatusCode.OK, body)

            return
                (deserialize<GraceReturnValue<DirectoryVersionSizeObservation>> body)
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
            use cosmos = AspireTestHost.createCosmosClient HostState.Value
            let container = cosmos.GetContainer(HostState.Value.CosmosDatabaseName, HostState.Value.CosmosContainerName)
            let directory = DirectoryVersion()
            directory.DirectoryVersionId <- Guid.NewGuid()
            directory.OwnerId <- Guid.Parse ownerId
            directory.OrganizationId <- Guid.Parse organizationId
            directory.RepositoryId <- repositoryId
            directory.CreatedAt <- getCurrentInstant ()
            directory.Files.Add(FileVersion.CreateWithHashes "retained.txt" (String.replicate 64 "a") (String.replicate 64 "b") "" true 37L)

            let metadata =
                {
                    Timestamp = getCurrentInstant ()
                    CorrelationId = "observation-hosted"
                    Principal = testUserId
                    ClientType = None
                    Properties = Dictionary<string, string>()
                }

            let events: DirectoryVersionEvent array =
                [|
                    { Event = Created directory; Metadata = metadata }
                |]

            let document =
                {|
                    id = string directory.DirectoryVersionId
                    PartitionKey = string repositoryId
                    GrainType = Grace.Actors.Constants.StateName.DirectoryVersion
                    State = events
                |}

            let! _ = container.CreateItemAsync(document, PartitionKey(string repositoryId))
            let id = Guid.NewGuid()
            let! original, originalJson = capture id parameters
            Assert.That(original.ObservationId, Is.EqualTo id)
            Assert.That(original.DeclaredLogicalBytes, Is.EqualTo 37L)
            Assert.That(original.DistinctContentCount, Is.EqualTo 1L)
            Assert.That(original.Scope.OwnerId, Is.EqualTo(Guid.Parse ownerId))
            Assert.That(original.Scope.OrganizationId, Is.EqualTo(Guid.Parse organizationId))
            Assert.That(original.Scope.RepositoryId, Is.EqualTo repositoryId)
            Assert.That(original.ObservationId, Is.EqualTo id)
            Assert.That(original.Scope.OwnerId, Is.EqualTo(Guid.Parse ownerId))
            Assert.That(original.Scope.OrganizationId, Is.EqualTo(Guid.Parse organizationId))
            Assert.That(original.Scope.RepositoryId, Is.EqualTo repositoryId)
            Assert.That(original.EnumerationFinishedAt, Is.GreaterThanOrEqualTo original.EnumerationStartedAt)
            let! _ = container.DeleteItemAsync<obj>(document.id, PartitionKey(string repositoryId))
            let! retry, _ = capture id parameters
            Assert.That(retry, Is.EqualTo original)
            let query = $"?OwnerId={ownerId}&OrganizationId={organizationId}&RepositoryId={repositoryId}"
            use! read = Client.GetAsync(route id + query)
            let! readJson = read.Content.ReadAsStringAsync()
            Assert.That(read.StatusCode, Is.EqualTo HttpStatusCode.OK, readJson)

            Assert.That(
                (deserialize<GraceReturnValue<DirectoryVersionSizeObservation>> readJson)
                    .ReturnValue,
                Is.EqualTo original
            )

            let! zero, zeroJson = capture (Guid.NewGuid()) parameters
            Assert.That(zero.DeclaredLogicalBytes, Is.Zero)
            Assert.That(zero.DistinctContentCount, Is.Zero)
            Assert.That(zero.ObservationId, Is.Not.EqualTo id)
            let output = IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, "directory-version-observation-zero-response.json")
            IO.File.WriteAllText(output, zeroJson)

            // NUnit's error channel remains visible for passing tests under the default CI console logger.
            TestContext.Error.WriteLine(
                "DIRECTORY_VERSION_OBSERVATION_HOSTED_JSON_BASE64:"
                + Convert.ToBase64String(Text.Encoding.UTF8.GetBytes zeroJson)
            )

            TestContext.AddTestAttachment(output, "Actual hosted zero envelope for operator validation.")

            IO.File.WriteAllText(
                IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, "directory-version-observation-positive-response.json"),
                originalJson
            )

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
                Assert.That(body, Does.Not.Contain "DeclaredLogicalBytes")
                Assert.That(body, Does.Not.Contain "EnumerationStartedAt")

                use! readCollision =
                    Client.GetAsync(
                        route id
                        + $"?OwnerId={foreign.OwnerId}&OrganizationId={foreign.OrganizationId}&RepositoryId={foreign.RepositoryId}"
                    )

                Assert.That(readCollision.StatusCode, Is.EqualTo HttpStatusCode.Conflict)
                index <- index + 1

            let badId = Guid.NewGuid()

            let invalid =
                {|
                    id = string (Guid.NewGuid())
                    PartitionKey = string repositoryId
                    GrainType = Grace.Actors.Constants.StateName.DirectoryVersion
                    State = [| {| Event = "broken" |} |]
                |}

            let! _ = container.CreateItemAsync(invalid, PartitionKey(string repositoryId))
            use! failed = Client.PostAsync(route badId, createJsonContent parameters)
            let! failureBody = failed.Content.ReadAsStringAsync()
            Assert.That(failed.StatusCode, Is.EqualTo HttpStatusCode.BadRequest, failureBody)
            Assert.That(failureBody, Does.Not.Contain "DeclaredLogicalBytes")

            let! absentFailed =
                DirectoryVersionSizeObservations.lookup HostState.Value.OperationsSqlConnectionString badId original.Scope CancellationToken.None

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
                (deserialize<GraceReturnValue<DirectoryVersionSizeObservation>> historicalJson)
                    .ReturnValue,
                Is.EqualTo original
            )

            use! deletedNew = Client.PostAsync(route (Guid.NewGuid()), createJsonContent parameters)
            Assert.That(deletedNew.StatusCode, Is.EqualTo HttpStatusCode.BadRequest)
            let! count = countRows "ops.DirectoryVersionSizeObservation" repositoryId
            let! rawAfter = countRows "ops.RawUsageFact" repositoryId
            let! aggregateAfter = countRows "ops.UsageAggregateMinute" repositoryId
            Assert.That(count, Is.EqualTo 2)
            Assert.That(rawAfter, Is.EqualTo rawBefore)
            Assert.That(aggregateAfter, Is.EqualTo aggregateBefore)
            use sql = new SqlConnection(HostState.Value.OperationsSqlConnectionString)
            do! sql.OpenAsync()

            use command =
                new SqlCommand(
                    "SELECT DeclaredLogicalBytes,DistinctContentCount,EnumerationStartedAt,EnumerationFinishedAt FROM ops.DirectoryVersionSizeObservation WHERE ObservationId=@id",
                    sql
                )

            command.Parameters.AddWithValue("@id", id)
            |> ignore

            use! stored = command.ExecuteReaderAsync()
            let! exists = stored.ReadAsync()
            Assert.That(exists, Is.True)
            Assert.That(stored.GetInt64 0, Is.EqualTo 37L)
            Assert.That(stored.GetInt64 1, Is.EqualTo 1L)
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
                    DeclaredLogicalBytes = 71L
                    DistinctContentCount = 2L
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
                    DeclaredLogicalBytes = 99L
                    DistinctContentCount = 3L
                    EnumerationStartedAt = first.EnumerationFinishedAt
                    EnumerationFinishedAt = first.EnumerationFinishedAt
                }

            use gateConnection = new SqlConnection(connectionString)
            do! gateConnection.OpenAsync()
            use gateTransaction = gateConnection.BeginTransaction(System.Data.IsolationLevel.Serializable)

            use gateCommand =
                new SqlCommand(
                    "SELECT ObservationId FROM ops.DirectoryVersionSizeObservation WITH(UPDLOCK,HOLDLOCK) WHERE ObservationId=@id",
                    gateConnection,
                    gateTransaction
                )

            gateCommand.Parameters.AddWithValue("@id", first.ObservationId)
            |> ignore

            let! _ = gateCommand.ExecuteScalarAsync()
            let left = DirectoryVersionSizeObservations.accept connectionString first CancellationToken.None
            let right = DirectoryVersionSizeObservations.accept connectionString second CancellationToken.None
            do! gateTransaction.CommitAsync()
            let! results = Task.WhenAll(left, right)
            Assert.That(results[0], Is.EqualTo results[1])
            Assert.That(results[0] = Ok first || results[0] = Ok second, Is.True)
            let! stored = DirectoryVersionSizeObservations.lookup connectionString first.ObservationId first.Scope CancellationToken.None
            Assert.That(stored, Is.EqualTo(Result.map Some results[0]))
            let! count = countRows "ops.DirectoryVersionSizeObservation" first.Scope.RepositoryId
            Assert.That(count, Is.EqualTo 1)
        }
