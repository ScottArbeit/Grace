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
                Assert.That(appHostSource, Does.Contain("OperationalFactsProcessorSubscriptionName = \"operational-facts-processor\""))
                Assert.That(appHostSource, Does.Not.Contain("var operationalFactsSubscriptionName = $\"{operationalFactsTopicName}-processor\";"))
                Assert.That(appHostSource, Does.Contain("RequiresDuplicateDetection = true"))
                Assert.That(appHostSource, Does.Contain("DuplicateDetectionHistoryTimeWindow = \"PT5M\""))

                Assert.That(appHostSource, Does.Contain("OperationalFactsProcessorSubscriptionName,"))
                Assert.That(appHostSource, Does.Contain("Name = OperationalFactsProcessorSubscriptionName")))
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
                    Does.Contain("OperationalFactsProcessorSubscriptionResourceName = \"grace-operational-facts-processor-subscription\"")
                )

                Assert.That(appHostSource, Does.Contain("serviceBus.AddServiceBusTopic(GraceEventTopicResourceName, serviceBusTopicName)"))

                Assert.That(appHostSource, Does.Contain(".AddServiceBusSubscription(GraceEventSubscriptionResourceName, graceEventSubscriptionName)"))

                Assert.That(appHostSource, Does.Contain("OperationalFactsTopicResourceName,"))
                Assert.That(appHostSource, Does.Contain("operationalFactsTopicName)"))

                Assert.That(appHostSource, Does.Contain("OperationalFactsProcessorSubscriptionResourceName,"))

                Assert.That(appHostSource, Does.Not.Contain("serviceBus.AddServiceBusTopic(serviceBusTopicName)"))
                Assert.That(appHostSource, Does.Not.Contain("\"operational-facts\",\r\n                        operationalFactsTopicName"))
                Assert.That(appHostSource, Does.Not.Contain(".AddServiceBusSubscription(OperationalFactsProcessorSubscriptionName)")))
        )

    /// Verifies DebugAzure requires the durable operational facts processor subscription to exist.
    [<Test>]
    member _.DebugAzureRequiresOperationalFactsProcessorSubscription() =
        let appHostSource = appHostSource ()

        Assert.Multiple(
            Action (fun () ->
                Assert.That(
                    appHostSource,
                    Does.Contain("OperationalFactsProcessorSubscriptionSettingName = \"grace__azure_service_bus__operational_facts_processor_subscription\"")
                )

                Assert.That(appHostSource, Does.Contain("var operationalFactsProcessorSubscription ="))

                Assert.That(appHostSource, Does.Contain("GetRequiredSetting(configuration, OperationalFactsProcessorSubscriptionSettingName);"))

                Assert.That(appHostSource, Does.Contain("EnsureOperationalFactsProcessorSubscription(operationalFactsProcessorSubscription);"))

                Assert.That(
                    appHostSource,
                    Does.Contain("Set '{OperationalFactsProcessorSubscriptionSettingName}' to '{OperationalFactsProcessorSubscriptionName}'")
                ))
        )
