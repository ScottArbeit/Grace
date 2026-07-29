namespace Grace.Server.TestDiagnostics

open Grace.Server.Tests
open Grace.Shared
open NUnit.Framework
open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json

/// Covers aspire test host diagnostics scenarios.
[<TestFixture>]
type AspireTestHostDiagnosticsTests() =

    /// Verifies the redacts connection string secrets but keeps actionable endpoints scenario.
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

    /// Verifies the format env diagnostics uses selected redacted values only scenario.
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

    /// Verifies the missing startup keys name required cosmos storage and service bus sources scenario.
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
                Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic
                Constants.EnvironmentVariables.AzureServiceBusSubscription
            |]

        Assert.Multiple(
            Action (fun () ->
                Assert.That(String.Join("|", missing), Is.EqualTo(String.Join("|", expectedMissing)))
                Assert.That(diagnostics, Does.Contain("AccountKey=***"))
                Assert.That(diagnostics, Does.Not.Contain("cosmos-secret"))
                Assert.That(diagnostics, Does.Not.Contain("storage-secret")))
        )

    /// Verifies the service bus skip mode is classified unsupported for shared server setup scenario.
    [<Test>]
    member _.ServiceBusSkipModeIsClassifiedUnsupportedForSharedServerSetup() =
        let message = AspireTestHost.FixtureDiagnostics.serviceBusSkipModeMessage

        Assert.Multiple(
            Action (fun () ->
                Assert.That(message, Does.Contain("GRACE_TEST_SKIP_SERVICEBUS=1"))
                Assert.That(message, Does.Contain("unsupported for Grace.Server.Tests"))
                Assert.That(message, Does.Contain("Owner Created event")))
        )

    /// Verifies ordinary resource health progress keeps the startup wording scenario.
    [<Test>]
    member _.ResourceHealthProgressKeepsOrdinaryStartupWording() =
        let startMessage = AspireTestHost.FixtureDiagnostics.formatResourceHealthWaitStartMessage "azurite" None
        let healthyMessage = AspireTestHost.FixtureDiagnostics.formatResourceHealthWaitHealthyMessage "azurite" None

        Assert.Multiple(
            Action (fun () ->
                Assert.That(startMessage, Is.EqualTo("waiting for resource 'azurite' to become healthy."))
                Assert.That(healthyMessage, Is.EqualTo("resource 'azurite' is healthy."))
                Assert.That(startMessage, Does.Not.Contain("restart"))
                Assert.That(healthyMessage, Does.Not.Contain("restart")))
        )

    /// Verifies deliberate Grace.Server restart progress identifies the test operation scenario.
    [<Test>]
    member _.ResourceHealthProgressNamesIntentionalGraceServerRestartContext() =
        let context = "RestartDurabilityServer.DurableActorStateRehydratesAcrossGraceServerProjectRestart"
        let startMessage = AspireTestHost.FixtureDiagnostics.formatResourceHealthWaitStartMessage "grace-server" (Some context)
        let healthyMessage = AspireTestHost.FixtureDiagnostics.formatResourceHealthWaitHealthyMessage "grace-server" (Some context)

        Assert.Multiple(
            Action (fun () ->
                Assert.That(startMessage, Does.Contain("intentional Grace.Server restart"))
                Assert.That(startMessage, Does.Contain(context))
                Assert.That(startMessage, Does.Contain("waiting for resource 'grace-server' to become healthy"))
                Assert.That(healthyMessage, Does.Contain("recovered after intentional Grace.Server restart"))
                Assert.That(healthyMessage, Does.Contain(context)))
        )

    /// Verifies restart context does not relabel non Grace.Server resource waits scenario.
    [<Test>]
    member _.ResourceHealthProgressDoesNotRelabelNonGraceServerResourcesAsRestarts() =
        let startMessage = AspireTestHost.FixtureDiagnostics.formatResourceHealthWaitStartMessage "servicebus-emulator" (Some "RestartDurability")
        let healthyMessage = AspireTestHost.FixtureDiagnostics.formatResourceHealthWaitHealthyMessage "servicebus-emulator" (Some "RestartDurability")

        Assert.Multiple(
            Action (fun () ->
                Assert.That(startMessage, Is.EqualTo("waiting for resource 'servicebus-emulator' to become healthy."))
                Assert.That(healthyMessage, Is.EqualTo("resource 'servicebus-emulator' is healthy."))
                Assert.That(startMessage, Does.Not.Contain("Grace.Server restart"))
                Assert.That(healthyMessage, Does.Not.Contain("Grace.Server restart")))
        )

    /// Verifies built-in resource commands distinguish success, cancellation, and failure before readiness checks.
    [<Test>]
    member _.ResourceCommandClassificationPreservesTerminalResult() =
        let completed = ManifestContributionMeasurementSupport.classifyResourceCommand { Success = true; Canceled = false; Message = "accepted" }

        let canceled = ManifestContributionMeasurementSupport.classifyResourceCommand { Success = false; Canceled = true; Message = "operator canceled" }

        let failed = ManifestContributionMeasurementSupport.classifyResourceCommand { Success = false; Canceled = false; Message = String.Empty }

        Assert.Multiple(
            Action(fun () ->
                Assert.That(completed, Is.EqualTo(ResourceCommandOutcome.Completed))
                Assert.That(canceled, Is.EqualTo(ResourceCommandOutcome.Canceled "operator canceled"))
                Assert.That(failed, Is.EqualTo(ResourceCommandOutcome.Failed "Resource command failed without details.")))
        )

    /// Verifies only start and restart commands wait for healthy runtime state.
    [<Test>]
    member _.ResourceCommandReadinessMatchesBuiltInCommandSemantics() =
        Assert.Multiple(
            Action(fun () ->
                Assert.That(ManifestContributionMeasurementSupport.commandRequiresHealthyResource "resource-start", Is.True)
                Assert.That(ManifestContributionMeasurementSupport.commandRequiresHealthyResource "resource-restart", Is.True)
                Assert.That(ManifestContributionMeasurementSupport.commandRequiresHealthyResource "resource-stop", Is.False)
                Assert.That(ManifestContributionMeasurementSupport.commandRequiresHealthyResource "custom-command", Is.False))
        )

    /// Verifies command failures retain bounded actionable state without leaking connection-string credentials.
    [<Test>]
    member _.ResourceCommandDiagnosticsAreBoundedAndRedacted() =
        let secret = "service-bus-secret"

        let logs =
            [ $"Endpoint=sb://localhost:5672;SharedAccessKey={secret};UseDevelopmentEmulator=true;"
              String('x', ManifestContributionMeasurementSupport.MaximumDiagnosticCharacters * 2) ]

        let diagnostic =
            ManifestContributionMeasurementSupport.formatBoundedDiagnostic "redis restart" "State=Failed; AccountKey=cosmos-secret; Password=sql-secret" logs

        Assert.Multiple(
            Action(fun () ->
                Assert.That(diagnostic.Length, Is.LessThanOrEqualTo(ManifestContributionMeasurementSupport.MaximumDiagnosticCharacters))
                Assert.That(diagnostic, Does.Contain("redis restart"))
                Assert.That(diagnostic, Does.Contain("State=Failed"))
                Assert.That(diagnostic, Does.Contain("SharedAccessKey=***"))
                Assert.That(diagnostic, Does.Contain("AccountKey=***"))
                Assert.That(diagnostic, Does.Contain("Password=***"))
                Assert.That(diagnostic, Does.Not.Contain(secret))
                Assert.That(diagnostic, Does.Not.Contain("cosmos-secret"))
                Assert.That(diagnostic, Does.Not.Contain("sql-secret")))
        )

    /// Verifies evidence records are UTF-8 no-BOM, parseable, and rejected when they exceed the fixture bound.
    [<Test>]
    member _.MeasurementEvidenceIsParseableUtf8AndBounded() =
        let path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"mca-evidence-{Guid.NewGuid():N}.ndjson")

        try
            let measurements = Dictionary<string, obj>()
            measurements["logicalCount"] <- 3L

            let sample: MeasurementSample =
                { schemaVersion = "1.0"
                  scenario = "HotManifest"
                  sampleType = "final-state"
                  sequence = 1
                  timestampUtc = DateTimeOffset.Parse("2026-07-28T12:00:00Z")
                  correlationKey = "hot-manifest"
                  measurements = measurements }

            ManifestContributionMeasurementSupport.appendEvidenceRecord path sample
            let bytes = File.ReadAllBytes path
            let records = ManifestContributionMeasurementSupport.readEvidenceRecords path

            Assert.Multiple(
                Action(fun () ->
                    Assert.That(bytes, Has.Length.GreaterThan(0))
                    Assert.That(bytes[0], Is.Not.EqualTo(0xEFuy), "UTF-8 evidence must not begin with a BOM.")
                    Assert.That(records, Has.Length.EqualTo(1))
                    Assert.That(records[0].GetProperty("scenario").GetString(), Is.EqualTo("HotManifest"))

                    Assert.That(records[0].GetProperty("measurements").GetProperty("logicalCount").GetInt64(), Is.EqualTo(3L)))
            )

            let oversized = {| payload = String('z', ManifestContributionMeasurementSupport.MaximumEvidenceRecordBytes) |}

            Assert.That(Action(fun () -> ManifestContributionMeasurementSupport.appendEvidenceRecord path oversized), Throws.TypeOf<InvalidDataException>())
        finally
            if File.Exists path then File.Delete path

    /// Verifies OpenMetrics timestamps are never mistaken for manifest-accounting sample values.
    [<Test>]
    member _.MeasurementEvidenceParsesOpenMetricsValuesBeforeTimestamps() =
        let metrics =
            """
# TYPE grace_manifest_contribution_messages_total counter
grace_manifest_contribution_messages_total{outcome="completed"} 2 1785304963133
grace_manifest_contribution_messages_total{outcome="ignored"} 1 1785304963133
other_metric_total 99 1785304963133
"""

        let actual = ManifestContributionMeasurementSupport.sumOpenMetricsSamples (fun name -> name = "grace_manifest_contribution_messages_total") metrics

        Assert.That(actual, Is.EqualTo(3.0))
