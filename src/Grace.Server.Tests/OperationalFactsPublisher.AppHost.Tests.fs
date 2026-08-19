namespace Grace.Server.Tests

open Grace.Shared
open NUnit.Framework
open System
open System.IO

/// Covers Aspire AppHost wiring for the operational usage facts Service Bus topic.
[<TestFixture>]
type OperationalFactsPublisherAppHostTests() =

    /// Reads the AppHost source so focused wiring assertions stay on the Aspire-facing test surface.
    let appHostSource () = File.ReadAllText(Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Aspire.AppHost", "Program.Aspire.AppHost.cs")))

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

    /// Verifies Aspire forwards explicit authorization bootstrap settings without embedding a fixed Azure identity.
    [<Test>]
    member _.AppHostForwardsExplicitAuthorizationBootstrapSettings() =
        let appHostSource = appHostSource ()

        Assert.Multiple(
            Action (fun () ->
                Assert.That(
                    appHostSource,
                    Does.Contain(
                        "AddOptionalEnvironment(graceServer, configuration, EnvironmentVariables.GraceAuthzBootstrapSystemAdminUsers, forwardedAuthKeys);"
                    )
                )

                Assert.That(
                    appHostSource,
                    Does.Contain(
                        "AddOptionalEnvironment(graceServer, configuration, EnvironmentVariables.GraceAuthzBootstrapSystemAdminGroups, forwardedAuthKeys);"
                    )
                )

                Assert.That(appHostSource, Does.Not.Contain("GraceAuthzBootstrapSystemAdminUsers, \"test-admin\"")))
        )
