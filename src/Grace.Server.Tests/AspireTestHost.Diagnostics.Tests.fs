namespace Grace.Server.TestDiagnostics

open Grace.Server.Tests
open Grace.Shared
open NUnit.Framework
open System

[<TestFixture>]
type AspireTestHostDiagnosticsTests() =

    [<Test>]
    member _.RedactsConnectionStringSecretsButKeepsActionableEndpoints() =
        let cosmos =
            AspireTestHost.FixtureDiagnostics.redactCosmosConnectionString "AccountEndpoint=https://localhost:8081/;AccountKey=cosmos-secret;Version=1;"

        let storage =
            AspireTestHost.FixtureDiagnostics.redactStorageConnectionString
                "DefaultEndpointsProtocol=http;AccountName=gracevcsdevelopment;AccountKey=storage-secret;BlobEndpoint=http://127.0.0.1:10000/gracevcsdevelopment;"

        let serviceBus =
            AspireTestHost.FixtureDiagnostics.redactServiceBusConnectionString
                "Endpoint=sb://localhost:5672;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=service-bus-secret;UseDevelopmentEmulator=true;"

        Assert.Multiple(
            Action (fun () ->
                Assert.That(cosmos, Does.Contain("AccountEndpoint=https://localhost:8081/"))
                Assert.That(cosmos, Does.Contain("AccountKey=***"))
                Assert.That(cosmos, Does.Not.Contain("cosmos-secret"))
                Assert.That(storage, Does.Contain("BlobEndpoint=http://127.0.0.1:10000/gracevcsdevelopment"))
                Assert.That(storage, Does.Contain("AccountKey=***"))
                Assert.That(storage, Does.Not.Contain("storage-secret"))
                Assert.That(serviceBus, Does.Contain("Endpoint=sb://localhost:5672"))
                Assert.That(serviceBus, Does.Contain("SharedAccessKey=***"))
                Assert.That(serviceBus, Does.Not.Contain("service-bus-secret")))
        )

    [<Test>]
    member _.FormatEnvDiagnosticsUsesSelectedRedactedValuesOnly() =
        let env =
            [
                Constants.EnvironmentVariables.GraceLogDirectory, "C:\\Temp\\GraceLogs"
                Constants.EnvironmentVariables.AzureCosmosDBConnectionString, "AccountEndpoint=https://localhost:8081/;AccountKey=cosmos-secret;"
                Constants.EnvironmentVariables.AzureStorageConnectionString, "DefaultEndpointsProtocol=http;AccountName=grace;AccountKey=storage-secret;"
                Constants.EnvironmentVariables.AzureServiceBusConnectionString, "Endpoint=sb://localhost:5672;SharedAccessKey=service-bus-secret;"
                "unrelated_secret", "must-not-appear"
            ]
            |> Map.ofList

        let diagnostics = AspireTestHost.FixtureDiagnostics.formatEnvDiagnostics env

        Assert.Multiple(
            Action (fun () ->
                Assert.That(diagnostics, Does.Contain($"{Constants.EnvironmentVariables.GraceLogDirectory}=C:\\Temp\\GraceLogs"))

                Assert.That(
                    diagnostics,
                    Does.Contain($"{Constants.EnvironmentVariables.AzureCosmosDBConnectionString}=AccountEndpoint=https://localhost:8081/;AccountKey=***")
                )

                Assert.That(
                    diagnostics,
                    Does.Contain(
                        $"{Constants.EnvironmentVariables.AzureStorageConnectionString}=DefaultEndpointsProtocol=http;AccountName=grace;AccountKey=***"
                    )
                )

                Assert.That(
                    diagnostics,
                    Does.Contain($"{Constants.EnvironmentVariables.AzureServiceBusConnectionString}=Endpoint=sb://localhost:5672;SharedAccessKey=***")
                )

                Assert.That(diagnostics, Does.Not.Contain("cosmos-secret"))
                Assert.That(diagnostics, Does.Not.Contain("storage-secret"))
                Assert.That(diagnostics, Does.Not.Contain("service-bus-secret"))
                Assert.That(diagnostics, Does.Not.Contain("unrelated_secret"))
                Assert.That(diagnostics, Does.Not.Contain("must-not-appear")))
        )

    [<Test>]
    member _.MissingStartupKeysNameRequiredCosmosStorageAndServiceBusSources() =
        let env =
            [
                Constants.EnvironmentVariables.AzureCosmosDBConnectionString, "AccountEndpoint=https://localhost:8081/;AccountKey=cosmos-secret;"
                Constants.EnvironmentVariables.AzureCosmosDBDatabaseName, "grace-dev"
                Constants.EnvironmentVariables.AzureStorageConnectionString, "DefaultEndpointsProtocol=http;AccountName=grace;AccountKey=storage-secret;"
                Constants.EnvironmentVariables.AzureServiceBusTopic, "graceeventstream"
            ]
            |> Map.ofList

        let missing = AspireTestHost.FixtureDiagnostics.getMissingStartupKeys false env
        let diagnostics = AspireTestHost.FixtureDiagnostics.formatEnvDiagnostics env

        let expectedMissing =
            [|
                Constants.EnvironmentVariables.AzureCosmosDBContainerName
                Constants.EnvironmentVariables.AzureServiceBusConnectionString
                Constants.EnvironmentVariables.AzureServiceBusSubscription
            |]

        Assert.Multiple(
            Action (fun () ->
                Assert.That(String.Join("|", missing), Is.EqualTo(String.Join("|", expectedMissing)))
                Assert.That(diagnostics, Does.Contain("AccountKey=***"))
                Assert.That(diagnostics, Does.Not.Contain("cosmos-secret"))
                Assert.That(diagnostics, Does.Not.Contain("storage-secret")))
        )

    [<Test>]
    member _.ServiceBusSkipModeIsClassifiedUnsupportedForSharedServerSetup() =
        let message = AspireTestHost.FixtureDiagnostics.serviceBusSkipModeMessage

        Assert.Multiple(
            Action (fun () ->
                Assert.That(message, Does.Contain("GRACE_TEST_SKIP_SERVICEBUS=1"))
                Assert.That(message, Does.Contain("unsupported for Grace.Server.Tests"))
                Assert.That(message, Does.Contain("Owner Created event")))
        )
