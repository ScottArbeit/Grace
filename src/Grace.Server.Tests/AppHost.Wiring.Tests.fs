namespace Grace.Server.Tests

open Grace.Shared
open Microsoft.Extensions.Configuration
open NUnit.Framework
open System
open System.Collections.Generic
open System.IO

/// Covers how the Aspire AppHost wires Grace Server and its local and Azure resources.
[<TestFixture>]
[<NonParallelizable>]
type AppHostWiringTests() =

    /// Reads the AppHost source so focused wiring assertions stay on the Aspire-facing test surface.
    let appHostSource () = File.ReadAllText(Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Aspire.AppHost", "Program.Aspire.AppHost.cs")))

    /// Reads the integration-host source so readiness wiring remains pinned to the AppHost container contract.
    let aspireTestHostSource () = File.ReadAllText(Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "AspireTestHost.fs")))

    /// Runs an assertion while restoring every process-level environment value it changes.
    let withEnvironmentVariables (values: (string * string) list) assertion =
        let originals =
            values
            |> List.map (fun (name, _) -> name, Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process))

        try
            values
            |> List.iter (fun (name, value) -> Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process))

            assertion ()
        finally
            originals
            |> List.iter (fun (name, value) -> Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process))

    /// Verifies integration readiness creates and validates all fixed Library containers from the AppHost source of truth.
    [<Test>]
    member _.LibraryContainersUseAcceptedContractsAndGateTestReadiness() =
        let expected =
            [
                "grace-library-control", [| "/PartitionKey" |]
                "grace-library-changes", [| "/PartitionKey"; "/PartitionKey2" |]
                "grace-library-current", [| "/PartitionKey"; "/PartitionKey2" |]
                "grace-library-receipts",
                [|
                    "/PartitionKey"
                    "/PartitionKey2"
                    "/PartitionKey3"
                |]
                "grace-library-baselines",
                [|
                    "/PartitionKey"
                    "/PartitionKey2"
                    "/PartitionKey3"
                |]
            ]
            |> Map.ofList

        let actual =
            Program.LibraryContainers
            |> Seq.map (fun (KeyValue (name, paths)) -> name, paths)
            |> Map.ofSeq

        let readinessSource = aspireTestHostSource ()

        Assert.Multiple(
            Action (fun () ->
                Assert.That((actual = expected), Is.True)
                Assert.That(readinessSource, Does.Contain("Program.LibraryContainers"))
                Assert.That(readinessSource, Does.Contain("CreateContainerIfNotExistsAsync(properties)"))
                Assert.That(readinessSource, Does.Contain("actualPaths <> partitionKeyPaths")))
        )

    /// Verifies DebugAzure gives an explicitly configured storage account precedence over ambient connection strings.
    [<Test>]
    member _.DebugAzurePrefersConfiguredStorageAccountName() =
        let appHostSource = appHostSource ()

        Assert.Multiple(
            Action (fun () ->
                Assert.That(appHostSource, Does.Contain("if (!string.IsNullOrWhiteSpace(azureStorageAccountName))"))
                Assert.That(appHostSource, Does.Contain("azureStorageConnectionString = null;"))
                Assert.That(appHostSource, Does.Contain("Using Azure Storage account: {azureStorageAccountName}")))
        )

    /// Verifies DebugAzure forwards every explicit secure Redis setting without logging credential values.
    [<Test>]
    member _.DebugAzureForwardsTlsRedisSettingsWithoutSecretDiagnostics() =
        let appHostSource = appHostSource ()

        Assert.Multiple(
            Action (fun () ->
                Assert.That(appHostSource, Does.Contain(".WithEnvironment(EnvironmentVariables.RedisTls, redisTls)"))
                Assert.That(appHostSource, Does.Contain(".WithEnvironment(EnvironmentVariables.RedisUsername, redisUsername)"))
                Assert.That(appHostSource, Does.Contain(".WithEnvironment(EnvironmentVariables.RedisPassword, redisPassword)"))
                Assert.That(appHostSource, Does.Contain(".WithEnvironment(EnvironmentVariables.RedisCaCertificate, redisCaCertificate)"))
                Assert.That(appHostSource, Does.Not.Contain("Console.WriteLine(redisPassword"))
                Assert.That(appHostSource, Does.Not.Contain("Console.WriteLine(redisCaCertificate")))
        )

    /// Verifies DebugLocal resolves Redis host and port from Aspire's allocated endpoint instead of fixed defaults.
    [<Test>]
    member _.DebugLocalForwardsAllocatedRedisEndpoint() =
        let appHostSource = appHostSource ()

        Assert.Multiple(
            Action (fun () ->
                Assert.That(appHostSource, Does.Contain("var redisEndpoint = redis.GetEndpoint(\"tcp\");"))
                Assert.That(appHostSource, Does.Contain("await redisEndpoint.GetValueAsync(context.CancellationToken)"))
                Assert.That(appHostSource, Does.Contain("context.EnvironmentVariables[EnvironmentVariables.RedisHost] = endpointUri.Host;"))

                Assert.That(appHostSource, Does.Contain("context.EnvironmentVariables[EnvironmentVariables.RedisPort] = endpointUri.Port.ToString();")))
        )

    /// Verifies a fresh exact process setting wins over stale configuration inherited from an earlier lab run.
    [<Test>]
    member _.DebugAzureExactProcessSettingOverridesStaleConfiguration() =
        let settingName = Constants.EnvironmentVariables.AzureStorageAccountName

        let configuration =
            ConfigurationBuilder()
                .AddInMemoryCollection(dict [ Grace.Shared.Utilities.getConfigKey settingName, "gracelab-stale" ])
                .Build()

        withEnvironmentVariables [ settingName, "gracelab-fresh" ] (fun () ->
            Assert.That(Program.ResolveSetting(configuration, settingName), Is.EqualTo("gracelab-fresh")))

    /// Verifies publish mode keeps Aspire resource identities independent from configurable Azure entity names.
    [<Test>]
    member _.PublishModeUsesStableResourceNamesForGraceEventEntities() =
        let appHostSource = appHostSource ()

        Assert.Multiple(
            Action (fun () ->
                Assert.That(appHostSource, Does.Contain("GraceEventTopicResourceName = \"grace-event-topic\""))
                Assert.That(appHostSource, Does.Contain("GraceEventSubscriptionResourceName = \"grace-event-subscription\""))
                Assert.That(appHostSource, Does.Contain("serviceBus.AddServiceBusTopic(GraceEventTopicResourceName, serviceBusTopicName)"))

                Assert.That(appHostSource, Does.Contain(".AddServiceBusSubscription(GraceEventSubscriptionResourceName, graceEventSubscriptionName)"))

                Assert.That(appHostSource, Does.Not.Contain("serviceBus.AddServiceBusTopic(serviceBusTopicName)")))
        )

    /// Verifies wrapper startup suppresses retained principals and retry forwards only the authenticated user.
    [<Test>]
    member _.DebugAzureWrapperOwnsAuthorizationBootstrapPrecedence() =
        let usersName = Constants.EnvironmentVariables.GraceAuthzBootstrapSystemAdminUsers
        let groupsName = Constants.EnvironmentVariables.GraceAuthzBootstrapSystemAdminGroups

        withEnvironmentVariables
            [
                usersName, "configured-user"
                groupsName, "configured-group"
                Program.DebugAzureBootstrapModeEnvironmentVariable, null
                Program.DebugAzureBootstrapUserIdEnvironmentVariable, null
            ]
            (fun () ->
                let configuration = ConfigurationBuilder().Build()

                Environment.SetEnvironmentVariable(Program.DebugAzureBootstrapModeEnvironmentVariable, Program.DebugAzureBootstrapModeSuppress)

                let suppressed = Program.ResolveAuthorizationBootstrapSettings(configuration, true)
                Assert.That(suppressed.Users, Is.Null)
                Assert.That(suppressed.Groups, Is.Null)

                Environment.SetEnvironmentVariable(Program.DebugAzureBootstrapModeEnvironmentVariable, Program.DebugAzureBootstrapModeExactUser)

                Environment.SetEnvironmentVariable(Program.DebugAzureBootstrapUserIdEnvironmentVariable, "authenticated-user")

                let retry = Program.ResolveAuthorizationBootstrapSettings(configuration, true)
                Assert.That(retry.Users, Is.EqualTo("authenticated-user"))
                Assert.That(retry.Users.Split(';'), Has.Length.EqualTo(1))
                Assert.That(retry.Groups, Is.Null)

                Environment.SetEnvironmentVariable(Program.DebugAzureBootstrapUserIdEnvironmentVariable, "first-user;second-user")

                Assert.Throws<InvalidOperationException>(
                    Action (fun () ->
                        Program.ResolveAuthorizationBootstrapSettings(configuration, true)
                        |> ignore)
                )
                |> ignore

                Environment.SetEnvironmentVariable(Program.DebugAzureBootstrapModeEnvironmentVariable, null)
                Environment.SetEnvironmentVariable(Program.DebugAzureBootstrapUserIdEnvironmentVariable, null)

                let directStart = Program.ResolveAuthorizationBootstrapSettings(configuration, true)
                Assert.That(directStart.Users, Is.EqualTo("configured-user"))
                Assert.That(directStart.Groups, Is.EqualTo("configured-group")))

    /// Verifies both server profiles forward the Operations SQL setting without logging its value.
    [<Test>]
    member _.ServerReceivesOperationsSqlInBothProfiles() =
        let source = appHostSource ()
        Assert.That(source, Does.Contain("graceServer.WithEnvironment(async context =>"))
        Assert.That(source, Does.Contain("Initial Catalog=GraceOperations;"))
        Assert.That(source, Does.Contain(".WithEnvironment(OperationsSqlConnectionStringSettingName, operationsSqlConnectionString)"))
        Assert.That(source, Does.Not.Contain("Console.WriteLine(operationsSqlConnectionString"))
        Assert.That(source, Does.Not.Contain("LogInformation(operationsSqlConnectionString"))

    /// Verifies the AppHost runs no Operations worker and provisions only the Grace event topic.
    [<Test>]
    member _.AppHostHasNoOperationsWorkerOrUsageTopic() =
        let source = appHostSource ()

        Assert.Multiple(
            Action (fun () ->
                Assert.That(source, Does.Not.Contain("grace-operations-worker"))
                Assert.That(source, Does.Not.Contain("Grace.Operations.Worker"))
                Assert.That(source, Does.Not.Contain("usage-collector"))
                Assert.That(source, Does.Not.Contain("operational_facts")))
        )
