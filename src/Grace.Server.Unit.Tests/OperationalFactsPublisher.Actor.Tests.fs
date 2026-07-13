namespace Grace.Server.Tests

open Grace.Actors
open Grace.Server
open Grace.Shared
open Grace.Shared.Constants
open Grace.Types.Common
open Grace.Types.Usage
open NodaTime
open NUnit.Framework
open System
open System.IO
open System.Text.Json

/// Covers operational usage fact publisher behavior without booting Aspire or Service Bus.
[<Parallelizable(ParallelScope.All)>]
type OperationalFactsPublisherActorTests() =

    /// Server startup acknowledgement that the durable operational facts processor subscription exists.
    [<Literal>]
    let OperationalFactsProcessorSubscriptionSettingName = "grace__azure_service_bus__operational_facts_processor_subscription"

    /// Durable Service Bus subscription required before publishing operational usage facts.
    [<Literal>]
    let OperationalFactsProcessorSubscriptionName = "operational-facts-processor"

    /// Builds a valid repository storage usage fact for publisher envelope assertions.
    let usageFact usageFactId correlationId =
        UsageFact.RepositoryStorageBytesMinute(
            usageFactId,
            correlationId,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            StoragePoolId "default",
            4096L,
            Instant.FromUtc(2026, 7, 4, 12, 34)
        )

    /// Verifies that usage facts map to the operational topic and required Service Bus envelope fields.
    [<Test>]
    member _.UsageFactMessageUsesOperationalTopicAndUsageEnvelope() =
        let usageFactId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
        let correlationId = "corr-operational-fact"
        let fact = usageFact usageFactId correlationId

        let publication = OperationalFactsPublisher.createPublication Constants.GraceOperationalFactsTopic fact

        Assert.That(publication.TopicName, Is.EqualTo(Constants.GraceOperationalFactsTopic))
        Assert.That(publication.Message.ContentType, Is.EqualTo(OperationalFactsPublisher.JsonContentType))
        Assert.That(publication.Message.Subject, Is.EqualTo(OperationalFactsPublisher.UsageFactSubject))
        Assert.That(publication.Message.MessageId, Is.EqualTo(usageFactId.ToString("D")))
        Assert.That(publication.Message.CorrelationId, Is.EqualTo(correlationId))

        Assert.That(
            publication.Message.ApplicationProperties[OperationalFactsPublisher.UsageFactMessageTypeProperty],
            Is.EqualTo(OperationalFactsPublisher.UsageFactMessageType)
        )

        Assert.That(
            publication.Message.ApplicationProperties[OperationalFactsPublisher.UsageFactKindProperty],
            Is.EqualTo(nameof UsageFactKind.RepositoryStorageBytesMinute)
        )

        use payload = JsonDocument.Parse(publication.Message.Body.ToString())

        Assert.That(
            payload
                .RootElement
                .GetProperty("UsageFactId")
                .GetGuid(),
            Is.EqualTo(usageFactId)
        )

        Assert.That(
            payload
                .RootElement
                .GetProperty("CorrelationId")
                .GetString(),
            Is.EqualTo(correlationId)
        )

    /// Verifies that missing operational topic configuration fails before any event topic fallback can happen.
    [<Test>]
    [<NonParallelizable>]
    member _.MissingOperationalFactsTopicFailsClearly() =
        let previous = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic)

        try
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic, null)

            let ex =
                Assert.Throws<InvalidOperationException>(
                    Action (fun () ->
                        OperationalFactsPublisher.resolveOperationalFactsTopicName ()
                        |> ignore)
                )

            Assert.That(ex.Message, Does.Contain(Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic))
            Assert.That(ex.Message, Does.Not.Contain(Constants.EnvironmentVariables.AzureServiceBusTopic))
        finally
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic, previous)

    /// Verifies that server startup validates the operational facts topic with the Service Bus settings.
    [<Test>]
    [<NonParallelizable>]
    member _.StartupValidationRejectsMissingOperationalFactsTopic() =
        let keys =
            [|
                Constants.EnvironmentVariables.GracePubSubSystem
                Constants.EnvironmentVariables.DebugEnvironment
                Constants.EnvironmentVariables.AzureStorageKey
                Constants.EnvironmentVariables.AzureServiceBusConnectionString
                Constants.EnvironmentVariables.AzureServiceBusNamespace
                Constants.EnvironmentVariables.AzureServiceBusTopic
                Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic
                OperationalFactsProcessorSubscriptionSettingName
                Constants.EnvironmentVariables.AzureServiceBusSubscription
            |]

        let previous =
            keys
            |> Array.map (fun key -> key, Environment.GetEnvironmentVariable key)

        try
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.GracePubSubSystem, nameof GracePubSubSystem.AzureServiceBus)
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.DebugEnvironment, "Local")
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.AzureStorageKey, "Zm9v")

            Environment.SetEnvironmentVariable(
                Constants.EnvironmentVariables.AzureServiceBusConnectionString,
                "Endpoint=sb://localhost/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE"
            )

            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.AzureServiceBusNamespace, "sbemulatorns")
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.AzureServiceBusTopic, "graceeventstream")
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic, null)
            Environment.SetEnvironmentVariable(OperationalFactsProcessorSubscriptionSettingName, OperationalFactsProcessorSubscriptionName)
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.AzureServiceBusSubscription, "grace-server")

            let ex =
                Assert.Throws<InvalidOperationException>(
                    Action (fun () ->
                        ApplicationContext.configurePubSubSettings ()
                        |> ignore)
                )

            Assert.That(ex.Message, Does.Contain(Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic))
            Assert.That(ex.Message, Does.Not.Contain(Constants.EnvironmentVariables.AzureServiceBusTopic))
        finally
            previous
            |> Array.iter (fun (key, value) -> Environment.SetEnvironmentVariable(key, value))

    /// Verifies that DebugAzure Service Bus can use a connection string without a separate namespace setting.
    [<Test>]
    [<NonParallelizable>]
    member _.StartupValidationAcceptsConnectionStringOnlyServiceBusNamespace() =
        let keys =
            [|
                Constants.EnvironmentVariables.GracePubSubSystem
                Constants.EnvironmentVariables.DebugEnvironment
                Constants.EnvironmentVariables.AzureStorageAccountName
                Constants.EnvironmentVariables.AzureStorageKey
                Constants.EnvironmentVariables.AzureServiceBusConnectionString
                Constants.EnvironmentVariables.AzureServiceBusNamespace
                Constants.EnvironmentVariables.AzureServiceBusTopic
                Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic
                OperationalFactsProcessorSubscriptionSettingName
                Constants.EnvironmentVariables.AzureServiceBusSubscription
            |]

        let previous =
            keys
            |> Array.map (fun key -> key, Environment.GetEnvironmentVariable key)

        let connectionString = "Endpoint=sb://grace-debug.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE"

        try
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.GracePubSubSystem, nameof GracePubSubSystem.AzureServiceBus)
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.DebugEnvironment, "Azure")
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.AzureStorageAccountName, "gracedebug")
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.AzureStorageKey, "Zm9v")
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.AzureServiceBusConnectionString, connectionString)
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.AzureServiceBusNamespace, null)
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.AzureServiceBusTopic, "graceeventstream")
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic, "grace-operational-facts")
            Environment.SetEnvironmentVariable(OperationalFactsProcessorSubscriptionSettingName, OperationalFactsProcessorSubscriptionName)
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.AzureServiceBusSubscription, "grace-server")

            let settings = ApplicationContext.configurePubSubSettings ()

            Assert.That(settings.AzureServiceBus.IsSome, Is.True)
            Assert.That(settings.AzureServiceBus.Value.ConnectionString, Is.EqualTo(connectionString))
            Assert.That(settings.AzureServiceBus.Value.FullyQualifiedNamespace, Is.EqualTo("grace-debug.servicebus.windows.net"))
        finally
            previous
            |> Array.iter (fun (key, value) -> Environment.SetEnvironmentVariable(key, value))

    /// Verifies that usage facts cannot be configured onto the GraceEvent topic/subscriber path.
    [<Test>]
    [<NonParallelizable>]
    member _.StartupValidationRejectsOperationalFactsTopicAlias() =
        let keys =
            [|
                Constants.EnvironmentVariables.GracePubSubSystem
                Constants.EnvironmentVariables.DebugEnvironment
                Constants.EnvironmentVariables.AzureStorageKey
                Constants.EnvironmentVariables.AzureServiceBusConnectionString
                Constants.EnvironmentVariables.AzureServiceBusNamespace
                Constants.EnvironmentVariables.AzureServiceBusTopic
                Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic
                OperationalFactsProcessorSubscriptionSettingName
                Constants.EnvironmentVariables.AzureServiceBusSubscription
            |]

        let previous =
            keys
            |> Array.map (fun key -> key, Environment.GetEnvironmentVariable key)

        try
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.GracePubSubSystem, nameof GracePubSubSystem.AzureServiceBus)
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.DebugEnvironment, "Local")
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.AzureStorageKey, "Zm9v")

            Environment.SetEnvironmentVariable(
                Constants.EnvironmentVariables.AzureServiceBusConnectionString,
                "Endpoint=sb://localhost/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE"
            )

            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.AzureServiceBusNamespace, "sbemulatorns")
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.AzureServiceBusTopic, "graceeventstream")
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic, "GraceEventStream")
            Environment.SetEnvironmentVariable(OperationalFactsProcessorSubscriptionSettingName, OperationalFactsProcessorSubscriptionName)
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.AzureServiceBusSubscription, "grace-server")

            let ex =
                Assert.Throws<InvalidOperationException>(
                    Action (fun () ->
                        ApplicationContext.configurePubSubSettings ()
                        |> ignore)
                )

            Assert.That(ex.Message, Does.Contain(Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic))
            Assert.That(ex.Message, Does.Contain(Constants.EnvironmentVariables.AzureServiceBusTopic))
            Assert.That(ex.Message, Does.Contain("must differ"))
        finally
            previous
            |> Array.iter (fun (key, value) -> Environment.SetEnvironmentVariable(key, value))

    /// Verifies that server startup requires acknowledgement of the durable operational facts processor subscription.
    [<Test>]
    [<NonParallelizable>]
    member _.StartupValidationRejectsMissingOperationalFactsProcessorSubscription() =
        let keys =
            [|
                Constants.EnvironmentVariables.GracePubSubSystem
                Constants.EnvironmentVariables.DebugEnvironment
                Constants.EnvironmentVariables.AzureStorageKey
                Constants.EnvironmentVariables.AzureServiceBusConnectionString
                Constants.EnvironmentVariables.AzureServiceBusNamespace
                Constants.EnvironmentVariables.AzureServiceBusTopic
                Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic
                OperationalFactsProcessorSubscriptionSettingName
                Constants.EnvironmentVariables.AzureServiceBusSubscription
            |]

        let previous =
            keys
            |> Array.map (fun key -> key, Environment.GetEnvironmentVariable key)

        try
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.GracePubSubSystem, nameof GracePubSubSystem.AzureServiceBus)
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.DebugEnvironment, "Local")
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.AzureStorageKey, "Zm9v")

            Environment.SetEnvironmentVariable(
                Constants.EnvironmentVariables.AzureServiceBusConnectionString,
                "Endpoint=sb://localhost/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE"
            )

            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.AzureServiceBusNamespace, "sbemulatorns")
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.AzureServiceBusTopic, "graceeventstream")
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic, "grace-operational-facts")
            Environment.SetEnvironmentVariable(OperationalFactsProcessorSubscriptionSettingName, null)
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.AzureServiceBusSubscription, "grace-server")

            let ex =
                Assert.Throws<InvalidOperationException>(
                    Action (fun () ->
                        ApplicationContext.configurePubSubSettings ()
                        |> ignore)
                )

            Assert.That(ex.Message, Does.Contain(OperationalFactsProcessorSubscriptionSettingName))
            Assert.That(ex.Message, Does.Contain(OperationalFactsProcessorSubscriptionName))
            Assert.That(ex.Message, Does.Contain("durable subscription"))
        finally
            previous
            |> Array.iter (fun (key, value) -> Environment.SetEnvironmentVariable(key, value))

    /// Verifies that server startup rejects processor subscription acknowledgements for the wrong durable subscription.
    [<Test>]
    [<NonParallelizable>]
    member _.StartupValidationRejectsInvalidOperationalFactsProcessorSubscription() =
        let keys =
            [|
                Constants.EnvironmentVariables.GracePubSubSystem
                Constants.EnvironmentVariables.DebugEnvironment
                Constants.EnvironmentVariables.AzureStorageKey
                Constants.EnvironmentVariables.AzureServiceBusConnectionString
                Constants.EnvironmentVariables.AzureServiceBusNamespace
                Constants.EnvironmentVariables.AzureServiceBusTopic
                Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic
                OperationalFactsProcessorSubscriptionSettingName
                Constants.EnvironmentVariables.AzureServiceBusSubscription
            |]

        let previous =
            keys
            |> Array.map (fun key -> key, Environment.GetEnvironmentVariable key)

        try
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.GracePubSubSystem, nameof GracePubSubSystem.AzureServiceBus)
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.DebugEnvironment, "Local")
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.AzureStorageKey, "Zm9v")

            Environment.SetEnvironmentVariable(
                Constants.EnvironmentVariables.AzureServiceBusConnectionString,
                "Endpoint=sb://localhost/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE"
            )

            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.AzureServiceBusNamespace, "sbemulatorns")
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.AzureServiceBusTopic, "graceeventstream")
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic, "grace-operational-facts")
            Environment.SetEnvironmentVariable(OperationalFactsProcessorSubscriptionSettingName, "operational-facts-dev")
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.AzureServiceBusSubscription, "grace-server")

            let ex =
                Assert.Throws<InvalidOperationException>(
                    Action (fun () ->
                        ApplicationContext.configurePubSubSettings ()
                        |> ignore)
                )

            Assert.That(ex.Message, Does.Contain(OperationalFactsProcessorSubscriptionSettingName))
            Assert.That(ex.Message, Does.Contain(OperationalFactsProcessorSubscriptionName))
            Assert.That(ex.Message, Does.Contain("durable subscription"))
        finally
            previous
            |> Array.iter (fun (key, value) -> Environment.SetEnvironmentVariable(key, value))

    /// Verifies that the existing GraceEvent publisher keeps the event stream topic and event metadata shape.
    [<Test>]
    member _.GraceEventPublisherStillUsesEventTopicAndMetadata() =
        let servicesSource = File.ReadAllText(Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Actors", "Services.Actor.fs")))

        Assert.That(servicesSource, Does.Contain("CreateSender(pubSubSettings.AzureServiceBus.Value.TopicName)"))
        Assert.That(servicesSource, Does.Contain("message.Subject <- \"GraceEvent\""))
        Assert.That(servicesSource, Does.Contain("message.ApplicationProperties[ \"graceEventType\" ] <- getDiscriminatedUnionFullName graceEvent"))
        Assert.That(servicesSource, Does.Not.Contain("GraceOperationalUsageFact"))
