namespace Grace.Server.Tests

open Grace.Shared
open Microsoft.Extensions.Configuration
open NUnit.Framework
open System
open System.IO

/// Covers Aspire AppHost wiring for the operational usage facts Service Bus topic.
[<TestFixture>]
[<NonParallelizable>]
type OperationalFactsPublisherAppHostTests() =

    /// Reads the AppHost source so focused wiring assertions stay on the Aspire-facing test surface.
    let appHostSource () = File.ReadAllText(Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Aspire.AppHost", "Program.Aspire.AppHost.cs")))

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

    /// Verifies that AppHost provisions and forwards the same operational facts topic name.
    [<Test>]
    member _.AppHostConfiguresOperationalFactsTopicConsistently() =
        let appHostSource = appHostSource ()

        Assert.Multiple(
            Action (fun () ->
                Assert.That(appHostSource, Does.Contain("ResolveSetting(configuration, EnvironmentVariables.AzureServiceBusOperationalFactsTopic)"))

                Assert.That(
                    appHostSource,
                    Does.Contain(".WithEnvironment(EnvironmentVariables.AzureServiceBusOperationalFactsTopic, operationalFactsTopicName)")
                )

                Assert.That(
                    appHostSource,
                    Does.Contain("var operationalFactsTopic = GetRequiredSetting(configuration, EnvironmentVariables.AzureServiceBusOperationalFactsTopic);")
                )

                Assert.That(appHostSource, Does.Contain("EnsureDistinctServiceBusTopics(serviceBusTopicName, operationalFactsTopicName);"))
                Assert.That(appHostSource, Does.Contain("EnsureDistinctServiceBusTopics(serviceBusTopic, operationalFactsTopic)"))
                Assert.That(appHostSource, Does.Contain("ResolveSetting(configuration, Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic)"))
                Assert.That(appHostSource, Does.Contain("EnsureDistinctServiceBusTopics(topicName, operationalFactsTopicName)"))
                Assert.That(appHostSource, Does.Contain("GraceUsageCollectorSubscriptionName = \"grace-usage-collector\""))
                Assert.That(appHostSource, Does.Not.Contain("var operationalFactsSubscriptionName = $\"{operationalFactsTopicName}-processor\";"))
                Assert.That(appHostSource, Does.Contain("RequiresDuplicateDetection = true"))
                Assert.That(appHostSource, Does.Contain("DuplicateDetectionHistoryTimeWindow = \"PT5M\""))

                Assert.That(appHostSource, Does.Contain("graceUsageCollectorSubscriptionName"))
                Assert.That(appHostSource, Does.Contain("Name = graceUsageCollectorSubscriptionName")))
        )

    /// Verifies publish mode keeps Aspire resource identities independent from configurable Azure entity names.
    [<Test>]
    member _.PublishModeUsesStableResourceNamesForOperationalFactEntities() =
        let appHostSource = appHostSource ()

        Assert.Multiple(
            Action (fun () ->
                Assert.That(appHostSource, Does.Contain("GraceEventTopicResourceName = \"grace-event-topic\""))
                Assert.That(appHostSource, Does.Contain("GraceEventSubscriptionResourceName = \"grace-event-subscription\""))
                Assert.That(appHostSource, Does.Contain("OperationalFactsTopicResourceName = \"grace-operational-facts-topic\""))

                Assert.That(
                    appHostSource,
                    Does.Contain("GraceUsageCollectorSubscriptionResourceName = \"grace-usage-collector-subscription\"")
                )

                Assert.That(appHostSource, Does.Contain("serviceBus.AddServiceBusTopic(GraceEventTopicResourceName, serviceBusTopicName)"))

                Assert.That(appHostSource, Does.Contain(".AddServiceBusSubscription(GraceEventSubscriptionResourceName, graceEventSubscriptionName)"))

                Assert.That(appHostSource, Does.Contain("OperationalFactsTopicResourceName,"))
                Assert.That(appHostSource, Does.Contain("operationalFactsTopicName)"))

                Assert.That(appHostSource, Does.Contain("GraceUsageCollectorSubscriptionResourceName,"))

                Assert.That(appHostSource, Does.Not.Contain("serviceBus.AddServiceBusTopic(serviceBusTopicName)"))
                Assert.That(appHostSource, Does.Not.Contain("\"operational-facts\",\r\n                        operationalFactsTopicName"))
                Assert.That(appHostSource, Does.Not.Contain(".AddServiceBusSubscription(GraceUsageCollectorSubscriptionName)")))
        )

    /// Verifies DebugAzure forwards the configured Grace usage collector subscription.
    [<Test>]
    member _.DebugAzureForwardsConfiguredGraceUsageCollectorSubscription() =
        let appHostSource = appHostSource ()

        Assert.Multiple(
            Action (fun () ->
                Assert.That(
                    appHostSource,
                    Does.Contain("OperationalFactsProcessorSubscriptionSettingName = \"grace__azure_service_bus__operational_facts_processor_subscription\"")
                )

                Assert.That(appHostSource, Does.Contain("var operationalFactsProcessorSubscription ="))

                Assert.That(appHostSource, Does.Contain("GetRequiredSetting(configuration, OperationalFactsProcessorSubscriptionSettingName);"))

                Assert.That(
                    appHostSource,
                    Does.Contain(".WithEnvironment(OperationalFactsProcessorSubscriptionSettingName, operationalFactsProcessorSubscription)")
                ))
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

                Environment.SetEnvironmentVariable(
                    Program.DebugAzureBootstrapModeEnvironmentVariable,
                    Program.DebugAzureBootstrapModeSuppress
                )

                let suppressed = Program.ResolveAuthorizationBootstrapSettings(configuration, true)
                Assert.That(suppressed.Users, Is.Null)
                Assert.That(suppressed.Groups, Is.Null)

                Environment.SetEnvironmentVariable(
                    Program.DebugAzureBootstrapModeEnvironmentVariable,
                    Program.DebugAzureBootstrapModeExactUser
                )

                Environment.SetEnvironmentVariable(Program.DebugAzureBootstrapUserIdEnvironmentVariable, "authenticated-user")

                let retry = Program.ResolveAuthorizationBootstrapSettings(configuration, true)
                Assert.That(retry.Users, Is.EqualTo("authenticated-user"))
                Assert.That(retry.Users.Split(';'), Has.Length.EqualTo(1))
                Assert.That(retry.Groups, Is.Null)

                Environment.SetEnvironmentVariable(Program.DebugAzureBootstrapUserIdEnvironmentVariable, "first-user;second-user")

                Assert.Throws<InvalidOperationException>(
                    Action(fun () -> Program.ResolveAuthorizationBootstrapSettings(configuration, true) |> ignore)
                )
                |> ignore

                Environment.SetEnvironmentVariable(Program.DebugAzureBootstrapModeEnvironmentVariable, null)
                Environment.SetEnvironmentVariable(Program.DebugAzureBootstrapUserIdEnvironmentVariable, null)

                let directStart = Program.ResolveAuthorizationBootstrapSettings(configuration, true)
                Assert.That(directStart.Users, Is.EqualTo("configured-user"))
                Assert.That(directStart.Groups, Is.EqualTo("configured-group")))
