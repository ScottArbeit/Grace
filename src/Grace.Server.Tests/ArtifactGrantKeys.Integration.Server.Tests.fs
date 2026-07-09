namespace Grace.Server.Tests

open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Types.ArtifactGrant
open NUnit.Framework
open System.Net
open System.Text.Json

/// Resolves the shared Aspire host state needed to prove signing-key recovery after server restart.
module private ArtifactGrantKeysIntegrationHelpers =

    /// Restarts Grace Server while preserving the Cosmos-backed Orleans state.
    let restartGraceServerAsync () =
        let state =
            match App with
            | Some app ->
                {
                    App = app
                    Client = Client
                    GraceServerBaseAddress = graceServerBaseAddress
                    ServiceBusConnectionString = serviceBusConnectionString
                    ServiceBusTopic = serviceBusTopic
                    ServiceBusServerSubscription = serviceBusServerSubscription
                    ServiceBusTestSubscription = serviceBusTestSubscription
                    OperationalFactsTopic = operationalFactsTopic
                    OperationsSqlConnectionString = operationsSqlConnectionString
                }
            | None ->
                Assert.Fail("Aspire test host was not started by the shared setup fixture.")
                Unchecked.defaultof<TestHostState>

        AspireTestHost.restartGraceServerAsync state

/// Proves that the HTTP publication route uses the durable Orleans signing-key owner.
type ArtifactGrantKeysIntegrationTests() =

    [<Test>]
    member _.``validation key publication recovers the same durable keys after server restart``() =
        task {
            let! firstResponse = Services.Client.GetAsync("/cache/validation-keys")
            let! firstJson = firstResponse.Content.ReadAsStringAsync()
            Assert.That(firstResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), firstJson)

            do! ArtifactGrantKeysIntegrationHelpers.restartGraceServerAsync ()

            let! secondResponse = Services.Client.GetAsync("/cache/validation-keys")
            let! secondJson = secondResponse.Content.ReadAsStringAsync()
            Assert.That(secondResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), secondJson)

            let first = JsonSerializer.Deserialize<ArtifactGrantValidationKeySet>(firstJson, Constants.JsonSerializerOptions)
            let second = JsonSerializer.Deserialize<ArtifactGrantValidationKeySet>(secondJson, Constants.JsonSerializerOptions)

            Assert.That(first, Is.Not.Null)
            Assert.That(second, Is.Not.Null)
            Assert.That(first.Keys, Is.Not.Null, firstJson)
            Assert.That(second.Keys, Is.Not.Null, secondJson)
            Assert.That(first.Keys.Count, Is.GreaterThan 0)
            Assert.That(second.Keys |> Seq.map (fun key -> key.KeyId), Is.EquivalentTo(first.Keys |> Seq.map (fun key -> key.KeyId)))
            Assert.That(first.CacheTtl, Is.EqualTo ArtifactGrantContract.ValidationKeyCacheTtl)
        }
