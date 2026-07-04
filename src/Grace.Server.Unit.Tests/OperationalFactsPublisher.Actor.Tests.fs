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
                Constants.EnvironmentVariables.AzureServiceBusConnectionString
                Constants.EnvironmentVariables.AzureServiceBusNamespace
                Constants.EnvironmentVariables.AzureServiceBusTopic
                Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic
                Constants.EnvironmentVariables.AzureServiceBusSubscription
            |]

        let previous =
            keys
            |> Array.map (fun key -> key, Environment.GetEnvironmentVariable key)

        try
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.GracePubSubSystem, nameof GracePubSubSystem.AzureServiceBus)

            Environment.SetEnvironmentVariable(
                Constants.EnvironmentVariables.AzureServiceBusConnectionString,
                "Endpoint=sb://localhost/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE"
            )

            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.AzureServiceBusNamespace, "sbemulatorns")
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.AzureServiceBusTopic, "graceeventstream")
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic, null)
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

    /// Verifies that the existing GraceEvent publisher keeps the event stream topic and event metadata shape.
    [<Test>]
    member _.GraceEventPublisherStillUsesEventTopicAndMetadata() =
        let servicesSource = File.ReadAllText(Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Actors", "Services.Actor.fs")))

        Assert.That(servicesSource, Does.Contain("CreateSender(pubSubSettings.AzureServiceBus.Value.TopicName)"))
        Assert.That(servicesSource, Does.Contain("message.Subject <- \"GraceEvent\""))
        Assert.That(servicesSource, Does.Contain("message.ApplicationProperties[ \"graceEventType\" ] <- getDiscriminatedUnionFullName graceEvent"))
        Assert.That(servicesSource, Does.Not.Contain("GraceOperationalUsageFact"))
