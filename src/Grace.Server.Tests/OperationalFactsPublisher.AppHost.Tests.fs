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
                Assert.That(appHostSource, Does.Match("topic\\s+operationalFactsTopicName\\s+\"PT5M\"\\s+true"))

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

                Assert.That(
                    appHostSource,
                    Does.Match(
                        "serviceBus\\s+\\.AddServiceBusTopic\\(ResourceNames\\.GraceEventTopic,\\s*serviceBusTopicName\\)\\s+\\.AddServiceBusSubscription\\(ResourceNames\\.GraceEventSubscription,\\s*graceEventSubscriptionName\\)"
                    )
                )

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
                Assert.That(appHostSource, Does.Contain("let private addCacheProject (builder: IDistributedApplicationBuilder) isTestRun useFixedTestPorts"))
                Assert.That(appHostSource, Does.Not.Contain("Grace.Cache.CLI"))
                Assert.That(appHostSource, Does.Not.Contain("Program.Aspire.AppHost.cs")))
        )

    /// Verifies AppHost keeps emulator endpoint values structured until Aspire allocates the resources.
    [<Test>]
    member _.AppHostPreservesStructuredEmulatorEndpointExpressions() =
        let appHostSource = appHostSource ()

        Assert.Multiple(
            Action (fun () ->
                Assert.That(appHostSource, Does.Contain("let azuriteConnectionBuilder = ReferenceExpressionBuilder()"))

                Assert.That(appHostSource, Does.Contain("azuriteConnectionBuilder.AppendValueProvider(azuriteBlobHostAndPort, null)"))

                Assert.That(appHostSource, Does.Contain("azuriteConnectionBuilder.AppendValueProvider(azuriteQueueHostAndPort, null)"))
                Assert.That(appHostSource, Does.Contain("azuriteConnectionBuilder.AppendValueProvider(azuriteTableHostAndPort, null)"))
                Assert.That(appHostSource, Does.Contain("let serviceBusConnectionBuilder = ReferenceExpressionBuilder()"))

                Assert.That(appHostSource, Does.Contain("serviceBusConnectionBuilder.AppendValueProvider(serviceBusHostAndPort, null)"))

                Assert.That(appHostSource, Does.Match("let\\s+azuriteConnection\\s+=\\s+azuriteConnectionBuilder\\.Build\\(\\)"))
                Assert.That(appHostSource, Does.Match("let\\s+serviceBusConnection\\s+=\\s+serviceBusConnectionBuilder\\.Build\\(\\)"))
                Assert.That(appHostSource, Does.Not.Contain("$\"DefaultEndpointsProtocol"))
                Assert.That(appHostSource, Does.Not.Contain("$\"Endpoint=sb://")))
        )

    /// Verifies publish mode supplies every Azure resource binding that Grace.Server consumes from Aspire.
    [<Test>]
    member _.PublishModeBindsEveryAzureResourceToGraceServer() =
        let appHostSource = appHostSource ()

        Assert.Multiple(
            Action (fun () ->
                Assert.That(appHostSource, Does.Contain(".WithReference(cosmosDatabase :?> IResourceBuilder<IResourceWithConnectionString>)"))
                Assert.That(appHostSource, Does.Contain(".WithReference(blobStorage :?> IResourceBuilder<IResourceWithConnectionString>)"))
                Assert.That(appHostSource, Does.Contain(".WithReference(diffStorage :?> IResourceBuilder<IResourceWithConnectionString>)"))
                Assert.That(appHostSource, Does.Contain(".WithReference(zipStorage :?> IResourceBuilder<IResourceWithConnectionString>)"))
                Assert.That(appHostSource, Does.Contain(".WithReference(serviceBus :?> IResourceBuilder<IResourceWithConnectionString>)"))
                Assert.That(appHostSource, Does.Contain(".WithParentRelationship(redis.Resource)")))
        )

    /// Verifies ordinary test AppHosts allocate a Cache HTTP port instead of colliding with developer port 8080.
    [<Test>]
    member _.AppHostAllocatesDynamicCachePortForOrdinaryTestRuns() =
        let appHostSource = appHostSource ()

        Assert.Multiple(
            Action (fun () ->
                Assert.That(appHostSource, Does.Contain("if isTestRun && not useFixedTestPorts then\n        let cacheTargetPort = getAvailableTcpPort ()"))

                Assert.That(
                    appHostSource,
                    Does.Contain(
                        ".WithHttpEndpoint(targetPort = cacheTargetPort, name = \"http\")\n            .WithEnvironment(\"ASPNETCORE_URLS\", $\"http://127.0.0.1:{cacheTargetPort}\")"
                    )
                )

                Assert.That(
                    appHostSource,
                    Does.Contain(".WithEnvironment(\"ASPNETCORE_URLS\", \"http://+:8080\")\n            .WithHttpEndpoint(targetPort = 8080, name = \"http\")")
                )

                Assert.That(appHostSource, Does.Contain("addCacheProject builder isTestRun useFixedTestPorts")))
        )

    /// Verifies Fast filters to the selected test families while Full runs the unfiltered solution test path.
    [<Test>]
    member _.ValidateScriptSelectsCacheTestsForFastAndFullProfiles() =
        let validationScript = File.ReadAllText(Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "scripts", "validate.ps1")))

        Assert.Multiple(
            Action (fun () ->
                Assert.That(validationScript, Does.Contain("FullyQualifiedName~Grace.Authorization.Tests"))
                Assert.That(validationScript, Does.Contain("FullyQualifiedName~Grace.Cache.Tests"))
                Assert.That(validationScript, Does.Contain("Get-ServerUnitTestFilterTerms"))

                Assert.That(
                    validationScript,
                    Does.Match("(?m)^                dotnet test \"src/Grace.slnx\" -c \\$Configuration --no-build --no-restore --filter \\$[A-Za-z0-9_]+$")
                )

                Assert.That(validationScript, Does.Contain("Invoke-SolutionTests $Configuration $Fast"))

                Assert.That(validationScript, Does.Match("(?m)^                dotnet test \"src/Grace.slnx\" -c \\$Configuration --no-build --no-restore$")))
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
