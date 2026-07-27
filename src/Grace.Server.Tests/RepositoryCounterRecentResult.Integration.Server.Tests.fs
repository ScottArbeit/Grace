namespace Grace.Server.Tests

open Aspire.Hosting
open Aspire.Hosting.ApplicationModel
open Grace.Actors
open Grace.Server
open Grace.Server.Tests.Services
open Grace.Types.Common
open Grace.Types.RepositoryContentCounter
open Microsoft.Extensions.DependencyInjection
open NUnit.Framework
open StackExchange.Redis
open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks

/// Proves the bounded Redis accelerator against the Redis version and lifecycle supplied by Grace Aspire.
[<NonParallelizable>]
type RepositoryCounterRecentResultIntegrationTests() =

    /// Verifies the exact TTL, cache-loss behavior, and native reconnect path without making Redis authoritative.
    [<Test>]
    member _.``Redis recent result expires in ten minutes and reconnects after loss``() =
        task {
            let! state = AspireTestHost.startAsync testUserId
            let repositoryId = Guid.NewGuid()
            let storagePoolId = StoragePoolId "integration"
            let manifestAddress = ManifestAddress(String.replicate 64 "a")
            let operationId = RepositoryContentCounterOperationId $"redis-witness:{Guid.NewGuid():N}"

            let change =
                { OperationId = operationId; Operation = RepositoryContentCounterChangeOperation.Added; PreviousCount = 0L; CurrentCount = 1L; Revision = 1L }

            let recentResult = RepositoryCounterRecentResult.RedisRepositoryCounterRecentResult("127.0.0.1", 6379) :> IRepositoryCounterRecentResult

            let! stored = recentResult.TrySetAsync(repositoryId, storagePoolId, manifestAddress, change, CancellationToken.None)

            Assert.That(stored, Is.True, "The Aspire Redis instance must accept the recent result.")

            let! rawConnection = ConnectionMultiplexer.ConnectAsync("127.0.0.1:6379,abortConnect=false")
            use rawConnection = rawConnection
            let database = rawConnection.GetDatabase()
            let redisKey = RepositoryCounterRecentResult.key repositoryId storagePoolId manifestAddress operationId
            let! ttl = database.KeyTimeToLiveAsync redisKey

            let ttlValue =
                ttl
                |> Option.ofNullable
                |> Option.defaultWith (fun () -> failwith "Redis did not report a TTL for the recent result.")

            Assert.That(ttlValue, Is.LessThanOrEqualTo(RepositoryCounterRecentResult.expiry))

            Assert.That(
                ttlValue,
                Is.GreaterThan(
                    RepositoryCounterRecentResult.expiry
                    - TimeSpan.FromSeconds(10.0)
                )
            )

            let redisVersion =
                rawConnection.GetServers()
                |> Seq.tryHead
                |> Option.map (fun server -> string server.Version)
                |> Option.defaultValue "unavailable"

            Assert.That(redisVersion, Does.StartWith("8.6.3"))

            let! deleted = database.KeyDeleteAsync redisKey
            Assert.That(deleted, Is.True, "Deleting the accelerator entry must simulate Redis result loss.")

            let! lostResult = recentResult.TryGetAsync(repositoryId, storagePoolId, manifestAddress, operationId, CancellationToken.None)

            Assert.That(lostResult.IsNone, Is.True, "A lost Redis entry must remain a nonauthoritative miss.")

            let commandService = state.App.Services.GetRequiredService<ResourceCommandService>()
            use commandCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2.0))
            let! stopResult = commandService.ExecuteCommandAsync("redis", KnownResourceCommands.StopCommand, commandCancellation.Token)
            Assert.That(stopResult.Success, Is.True, stopResult.Message)

            let! unavailableResult = recentResult.TryGetAsync(repositoryId, storagePoolId, manifestAddress, operationId, CancellationToken.None)

            Assert.That(unavailableResult.IsNone, Is.True, "Redis unavailability must be observed as a bounded cache miss.")

            let! startResult = commandService.ExecuteCommandAsync("redis", KnownResourceCommands.StartCommand, commandCancellation.Token)
            Assert.That(startResult.Success, Is.True, startResult.Message)

            let reconnectTimer = Stopwatch.StartNew()
            let mutable recovered = false

            while not recovered
                  && reconnectTimer.Elapsed < TimeSpan.FromSeconds(30.0) do
                let! recoveredWrite = recentResult.TrySetAsync(repositoryId, storagePoolId, manifestAddress, change, CancellationToken.None)

                if recoveredWrite then
                    let! recoveredResult = recentResult.TryGetAsync(repositoryId, storagePoolId, manifestAddress, operationId, CancellationToken.None)

                    recovered <- recoveredResult = Some change

                if not recovered then do! Task.Delay(TimeSpan.FromMilliseconds(250.0))

            reconnectTimer.Stop()
            Assert.That(recovered, Is.True, "The existing Redis client must reconnect after the Aspire resource restarts.")

            let! cleaned = database.KeyDeleteAsync redisKey

            TestContext.Progress.WriteLine(
                $"RedisVersion={redisVersion}; InitialTtl={ttlValue.TotalSeconds:F3}s; LossObserved={lostResult.IsNone}; "
                + $"UnavailableObserved={unavailableResult.IsNone}; ReconnectElapsed={reconnectTimer.Elapsed.TotalSeconds:F3}s; CleanupDeleted={cleaned}"
            )
        }
