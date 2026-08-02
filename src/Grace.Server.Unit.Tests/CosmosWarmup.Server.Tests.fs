namespace Grace.Server.Tests

open Grace.Server.Application
open Microsoft.Extensions.Logging.Abstractions
open NUnit.Framework
open System
open System.Threading.Tasks

/// Covers the local Cosmos write-readiness gate without starting Aspire.
[<Parallelizable(ParallelScope.All)>]
type CosmosWarmupServerTests() =

    /// Verifies failed sentinel cleanup prevents readiness until that same sentinel is removed.
    [<Test>]
    member _.CleanupFailurePreventsReadiness() =
        task {
            let mutable writeAttempts = 0
            let mutable deleteAttempts = 0
            let mutable delays = 0

            let writeSentinel _ =
                writeAttempts <- writeAttempts + 1
                Task.CompletedTask

            let deleteSentinel _ =
                deleteAttempts <- deleteAttempts + 1

                if deleteAttempts = 1 then
                    Task.FromException(InvalidOperationException("simulated sentinel cleanup failure"))
                else
                    Task.CompletedTask

            let delay _ =
                delays <- delays + 1
                Task.CompletedTask

            do! waitForLocalCosmosWriteReadiness 3 writeSentinel deleteSentinel delay NullLogger.Instance TestContext.CurrentContext.CancellationToken

            Assert.That(writeAttempts, Is.EqualTo(1), "Cleanup retry must not create another sentinel.")
            Assert.That(deleteAttempts, Is.EqualTo(2), "Readiness requires successful cleanup.")
            Assert.That(delays, Is.EqualTo(1))
        }
