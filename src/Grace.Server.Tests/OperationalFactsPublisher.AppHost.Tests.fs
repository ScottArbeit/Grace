namespace Grace.Server.Tests

open Aspire.Hosting.Testing
open Grace.Shared
open NUnit.Framework
open Projects
open System
open System.IO

/// Covers Aspire AppHost wiring for the operational usage facts Service Bus topic.
[<TestFixture>]
type OperationalFactsPublisherAppHostTests() =

    /// Reads the F# AppHost source so focused wiring assertions stay on the Aspire-facing test surface.
    let appHostSource () = File.ReadAllText(Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Aspire.AppHost", "Program.Aspire.AppHost.fs")))

    /// Verifies that AppHost provisions and forwards the same operational facts topic name.
    [<Test>]
    member _.AppHostConfiguresOperationalFactsTopicConsistently() =
        let appHostSource = appHostSource ()

        Assert.Multiple(
            Action (fun () ->
                Assert.That(appHostSource, Does.Contain("resolveSetting configuration Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic"))

                Assert.That(
                    appHostSource,
                    Does.Contain(".WithEnvironment(Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic, operationalFactsTopicName)")
                )

                Assert.That(
                    appHostSource,
                    Does.Contain(
                        "let operationalFactsTopic = getRequiredSetting configuration Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic"
                    )
                )

                Assert.That(appHostSource, Does.Contain("ensureDistinctServiceBusTopics (Some serviceBusTopicName) (Some operationalFactsTopicName)"))
                Assert.That(appHostSource, Does.Contain("ensureDistinctServiceBusTopics serviceBusTopic (Some operationalFactsTopic)"))
                Assert.That(appHostSource, Does.Contain("resolveSetting configuration Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic"))
                Assert.That(appHostSource, Does.Contain("ensureDistinctServiceBusTopics (Some topicName) (Some operationalFactsTopicName)"))
                Assert.That(appHostSource, Does.Contain("let OperationalFactsProcessorSubscription = \"operational-facts-processor\""))
                Assert.That(appHostSource, Does.Not.Contain("operationalFactsTopicName}-processor"))
                Assert.That(appHostSource, Does.Contain("topic.RequiresDuplicateDetection <- true"))
                Assert.That(appHostSource, Does.Contain("topic operationalFactsTopicName \"PT5M\" true"))

                Assert.That(appHostSource, Does.Contain("ResourceNames.OperationalFactsProcessorSubscription"))
                Assert.That(appHostSource, Does.Contain("subscription ResourceNames.OperationalFactsProcessorSubscription")))
        )

    /// Verifies publish mode keeps Aspire resource identities independent from configurable Azure entity names.
    [<Test>]
    member _.PublishModeUsesStableResourceNamesForOperationalFactEntities() =
        let appHostSource = appHostSource ()

        Assert.Multiple(
            Action (fun () ->
                Assert.That(appHostSource, Does.Contain("let GraceEventTopic = \"grace-event-topic\""))
                Assert.That(appHostSource, Does.Contain("let GraceEventSubscription = \"grace-event-subscription\""))
                Assert.That(appHostSource, Does.Contain("let OperationalFactsTopic = \"grace-operational-facts-topic\""))

                Assert.That(
                    appHostSource,
                    Does.Contain("let OperationalFactsProcessorSubscriptionResource = \"grace-operational-facts-processor-subscription\"")
                )

                Assert.That(appHostSource, Does.Contain("serviceBus.AddServiceBusTopic(ResourceNames.GraceEventTopic, serviceBusTopicName)"))

                Assert.That(appHostSource, Does.Contain(".AddServiceBusSubscription(ResourceNames.GraceEventSubscription, graceEventSubscriptionName)"))

                Assert.That(appHostSource, Does.Contain("ResourceNames.OperationalFactsTopic, operationalFactsTopicName"))

                Assert.That(
                    appHostSource,
                    Does.Contain(
                        ".AddServiceBusSubscription(ResourceNames.OperationalFactsProcessorSubscriptionResource, ResourceNames.OperationalFactsProcessorSubscription)"
                    )
                )

                Assert.That(appHostSource, Does.Not.Contain("AddServiceBusTopic(serviceBusTopicName)"))
                Assert.That(appHostSource, Does.Not.Contain("AddServiceBusSubscription(ResourceNames.OperationalFactsProcessorSubscription)")))
        )

    /// Verifies DebugAzure requires the durable operational facts processor subscription to exist.
    [<Test>]
    member _.DebugAzureRequiresOperationalFactsProcessorSubscription() =
        let appHostSource = appHostSource ()

        Assert.Multiple(
            Action (fun () ->
                Assert.That(
                    appHostSource,
                    Does.Contain("let OperationalFactsProcessorSubscriptionSetting = \"grace__azure_service_bus__operational_facts_processor_subscription\"")
                )

                Assert.That(
                    appHostSource,
                    Does.Contain(
                        "let operationalFactsProcessorSubscription = getRequiredSetting configuration ResourceNames.OperationalFactsProcessorSubscriptionSetting"
                    )
                )

                Assert.That(appHostSource, Does.Contain("ensureOperationalFactsProcessorSubscription (Some operationalFactsProcessorSubscription)"))

                Assert.That(
                    appHostSource,
                    Does.Contain(
                        "Set '{ResourceNames.OperationalFactsProcessorSubscriptionSetting}' to '{ResourceNames.OperationalFactsProcessorSubscription}'"
                    )
                ))
        )

    /// Verifies the F# AppHost directly composes the narrowly scoped Cache process without changing its service boundary.
    [<Test>]
    member _.AppHostComposesGraceCacheThroughFSharpProjectInput() =
        let appHostSource = appHostSource ()

        Assert.Multiple(
            Action (fun () ->
                Assert.That(appHostSource, Does.Contain(".AddProject(\"grace-cache\", \"..\\\\Grace.Cache\\\\Grace.Cache.fsproj\")"))
                Assert.That(appHostSource, Does.Contain(".WithEnvironment(\"GRACE_CACHE_INSTANCE_NAME\", \"aspire-cache\")"))
                Assert.That(appHostSource, Does.Contain(".WithHttpEndpoint(targetPort = 8080, name = \"http\")"))
                Assert.That(appHostSource, Does.Not.Contain("Grace.Cache.CLI"))
                Assert.That(appHostSource, Does.Not.Contain("Program.Aspire.AppHost.cs")))
        )

    /// Verifies the hand-authored F# marker preserves Aspire's generated AppHost test-entry contract.
    [<Test>]
    member _.FSharpAppHostTestingMarkerIsAcceptedByDistributedApplicationTestingBuilder() =
        task {
            use! builder = DistributedApplicationTestingBuilder.CreateAsync<Grace_Aspire_AppHost>()
            let markerType = typeof<Grace_Aspire_AppHost>
            let expectedProjectPath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Aspire.AppHost"))

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(markerType.IsClass, Is.True)
                    Assert.That(markerType.GetConstructors(), Is.Empty)
                    Assert.That(markerType.Assembly.EntryPoint, Is.Not.Null)
                    Assert.That(Grace_Aspire_AppHost.ProjectPath, Is.EqualTo(expectedProjectPath))
                    Assert.That(builder.AppHostAssembly, Is.SameAs(markerType.Assembly)))
            )
        }
