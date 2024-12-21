namespace Grace.Actors

open Dapr.Actors.Client
open Grace.Actors.Types
open Grace.Shared.Types
open Microsoft.Azure.Cosmos
open Microsoft.Extensions.Caching.Memory
open Microsoft.Extensions.ObjectPool
open Microsoft.Extensions.Logging
open System
open System.Text
open System.Collections.Concurrent
open System.Collections.Generic

module Context =

    /// Actor proxy factory instance
    let mutable internal actorProxyFactory: IActorProxyFactory = null

    /// Setter for actor proxy factory
    let setActorProxyFactory proxyFactory = actorProxyFactory <- proxyFactory

    /// Actor state storage provider instance
    let mutable internal actorStateStorageProvider: ActorStateStorageProvider = ActorStateStorageProvider.Unknown

    /// Setter for actor state storage provider
    let setActorStateStorageProvider storageProvider = actorStateStorageProvider <- storageProvider

    /// Cosmos client instance
    let mutable internal cosmosClient: CosmosClient = null

    /// Setter for Cosmos client
    let setCosmosClient (client: CosmosClient) = cosmosClient <- client

    /// Cosmos container instance
    let mutable internal cosmosContainer: Container = null

    /// Setter for Cosmos container
    let setCosmosContainer (container: Container) = cosmosContainer <- container

    /// Host services collection
    let mutable internal hostServiceProvider: IServiceProvider = null

    /// Setter for services collection
    let setHostServiceProvider (hostServices: IServiceProvider) = hostServiceProvider <- hostServices

    /// Logger factory instance
    let mutable internal loggerFactory: ILoggerFactory = null //hostServiceProvider.GetService(typeof<ILoggerFactory>) :?> ILoggerFactory

    /// Setter for logger factory
    let setLoggerFactory (factory: ILoggerFactory) = loggerFactory <- factory

    /// Grace Server's universal .NET memory cache
    let mutable internal memoryCache: IMemoryCache = null

    /// Setter for memory cache
    let setMemoryCache (cache: IMemoryCache) = memoryCache <- cache

    let mutable internal timings = ConcurrentDictionary<CorrelationId, List<Timing>>()
    let setTimings (timing: ConcurrentDictionary<CorrelationId, List<Timing>>) = timings <- timing
