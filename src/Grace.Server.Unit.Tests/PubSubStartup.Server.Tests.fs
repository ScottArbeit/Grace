namespace Grace.Server.Tests

open Grace.Server
open Grace.Shared
open Grace.Shared.Constants
open Grace.Types.Common
open NUnit.Framework
open System
open System.IO

/// Covers Grace Server's Service Bus startup settings without booting Aspire or Service Bus.
[<NonParallelizable>]
type PubSubStartupTests() =

    /// Lists the Service Bus settings these tests change, so each test can restore them.
    let serviceBusKeys =
        [|
            Constants.EnvironmentVariables.GracePubSubSystem
            Constants.EnvironmentVariables.DebugEnvironment
            Constants.EnvironmentVariables.AzureStorageAccountName
            Constants.EnvironmentVariables.AzureStorageKey
            Constants.EnvironmentVariables.AzureServiceBusConnectionString
            Constants.EnvironmentVariables.AzureServiceBusNamespace
            Constants.EnvironmentVariables.AzureServiceBusTopic
            Constants.EnvironmentVariables.AzureServiceBusSubscription
        |]

    /// Runs an assertion with the given Service Bus settings, then restores every changed process value.
    let withServiceBusSettings (values: (string * string) list) assertion =
        let previous =
            serviceBusKeys
            |> Array.map (fun key -> key, Environment.GetEnvironmentVariable key)

        try
            serviceBusKeys
            |> Array.iter (fun key -> Environment.SetEnvironmentVariable(key, null))

            values
            |> List.iter (fun (key, value) -> Environment.SetEnvironmentVariable(key, value))

            assertion ()
        finally
            previous
            |> Array.iter (fun (key, value) -> Environment.SetEnvironmentVariable(key, value))

    /// Verifies Grace Server accepts Service Bus with only the Grace event topic and subscription configured.
    [<Test>]
    member _.StartupValidationAcceptsServiceBusWithOnlyGraceEventSettings() =
        withServiceBusSettings
            [
                Constants.EnvironmentVariables.GracePubSubSystem, nameof GracePubSubSystem.AzureServiceBus
                Constants.EnvironmentVariables.DebugEnvironment, "Local"
                Constants.EnvironmentVariables.AzureStorageKey, "Zm9v"
                Constants.EnvironmentVariables.AzureServiceBusConnectionString,
                "Endpoint=sb://localhost/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE"
                Constants.EnvironmentVariables.AzureServiceBusNamespace, "sbemulatorns"
                Constants.EnvironmentVariables.AzureServiceBusTopic, "graceeventstream"
                Constants.EnvironmentVariables.AzureServiceBusSubscription, "grace-server"
            ]
            (fun () ->
                let settings = ApplicationContext.configurePubSubSettings ()

                Assert.That(settings.AzureServiceBus.IsSome, Is.True)
                Assert.That(settings.AzureServiceBus.Value.TopicName, Is.EqualTo("graceeventstream"))
                Assert.That(settings.AzureServiceBus.Value.SubscriptionName, Is.EqualTo("grace-server")))

    /// Verifies that DebugAzure Service Bus can use a connection string without a separate namespace setting.
    [<Test>]
    member _.StartupValidationAcceptsConnectionStringOnlyServiceBusNamespace() =
        let connectionString = "Endpoint=sb://grace-debug.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE"

        withServiceBusSettings
            [
                Constants.EnvironmentVariables.GracePubSubSystem, nameof GracePubSubSystem.AzureServiceBus
                Constants.EnvironmentVariables.DebugEnvironment, "Azure"
                Constants.EnvironmentVariables.AzureStorageAccountName, "gracedebug"
                Constants.EnvironmentVariables.AzureStorageKey, "Zm9v"
                Constants.EnvironmentVariables.AzureServiceBusConnectionString, connectionString
                Constants.EnvironmentVariables.AzureServiceBusTopic, "graceeventstream"
                Constants.EnvironmentVariables.AzureServiceBusSubscription, "grace-server"
            ]
            (fun () ->
                let settings = ApplicationContext.configurePubSubSettings ()

                Assert.That(settings.AzureServiceBus.IsSome, Is.True)
                Assert.That(settings.AzureServiceBus.Value.ConnectionString, Is.EqualTo(connectionString))
                Assert.That(settings.AzureServiceBus.Value.FullyQualifiedNamespace, Is.EqualTo("grace-debug.servicebus.windows.net")))

    /// Verifies that the GraceEvent publisher keeps the event stream topic and event metadata shape.
    [<Test>]
    member _.GraceEventPublisherUsesEventTopicAndMetadata() =
        let servicesSource = File.ReadAllText(Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Actors", "Services.Actor.fs")))

        Assert.That(servicesSource, Does.Contain("CreateSender(pubSubSettings.AzureServiceBus.Value.TopicName)"))
        Assert.That(servicesSource, Does.Contain("message.Subject <- \"GraceEvent\""))
        Assert.That(servicesSource, Does.Contain("message.ApplicationProperties[ \"graceEventType\" ] <- getDiscriminatedUnionFullName graceEvent"))
