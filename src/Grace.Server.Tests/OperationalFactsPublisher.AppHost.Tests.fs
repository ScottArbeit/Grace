namespace Grace.Server.Tests

open Grace.Shared
open NUnit.Framework
open System
open System.IO

/// Covers Aspire AppHost wiring for the operational usage facts Service Bus topic.
[<TestFixture>]
type OperationalFactsPublisherAppHostTests() =

    /// Verifies that AppHost provisions and forwards the same operational facts topic name.
    [<Test>]
    member _.AppHostConfiguresOperationalFactsTopicConsistently() =
        let appHostSource = File.ReadAllText(Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Aspire.AppHost", "Program.Aspire.AppHost.cs")))

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

                Assert.That(appHostSource, Does.Contain("EnsureDistinctServiceBusTopics(serviceBusTopic, operationalFactsTopic)"))
                Assert.That(appHostSource, Does.Contain("ResolveSetting(configuration, Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic)"))
                Assert.That(appHostSource, Does.Contain("EnsureDistinctServiceBusTopics(topicName, operationalFactsTopicName)"))
                Assert.That(appHostSource, Does.Contain("RequiresDuplicateDetection = true"))
                Assert.That(appHostSource, Does.Contain("DuplicateDetectionHistoryTimeWindow = \"PT5M\"")))
        )
