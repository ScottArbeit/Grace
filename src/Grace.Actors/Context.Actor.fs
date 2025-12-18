namespace Grace.Actors

open Azure.Core
open Azure.Identity
open Azure.Storage.Blobs
open Grace.Actors.Types
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types.Types
open Microsoft.Azure.Cosmos
open Microsoft.Extensions.Caching.Memory
open Microsoft.Extensions.ObjectPool
open Microsoft.Extensions.Logging
open Orleans
open System
open System.Text
open System.Collections.Concurrent
open System.Collections.Generic
open Azure.Storage.Blobs.Models
open NodaTime.Serialization.SystemTextJson
open MessagePack
open MessagePack.Resolvers
open MessagePack.FSharp
open MessagePack.NodaTime
open MessagePack.Resolvers

open Grace.Shared.AzureEnvironment

module Context =

    /// Actor state storage provider instance
    let mutable internal actorStateStorageProvider = ActorStateStorageProvider.Unknown

    /// Setter for actor state storage provider
    let setActorStateStorageProvider storageProvider =
        logToConsole $"In Context.Actor.setActorStateStorageProvider: Setting actor state storage provider to {storageProvider}."
        actorStateStorageProvider <- storageProvider

    /// Orleans client instance for the application.
    let mutable internal orleansClient: IGrainFactory = null

    /// Sets the Orleans client for the application.
    let setGrainFactory (client: IGrainFactory) = orleansClient <- client

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

    /// Pub-sub settings for Grace.
    let mutable internal pubSubSettings: GracePubSubSettings = GracePubSubSettings.Empty
    let setPubSubSettings (settings: GracePubSubSettings) = pubSubSettings <- settings

    let mutable internal timings = ConcurrentDictionary<CorrelationId, List<Timing>>()
    let setTimings (timing: ConcurrentDictionary<CorrelationId, List<Timing>>) = timings <- timing

    let private defaultAzureCredential = lazy (DefaultAzureCredential())

    /// Azure Blob Storage client
    let blobServiceClient =
        if AzureEnvironment.useManagedIdentityForStorage then
            BlobServiceClient(AzureEnvironment.storageEndpoints.BlobEndpoint, defaultAzureCredential.Value)
        else
            match AzureEnvironment.storageEndpoints.ConnectionString with
            | Some connectionString -> BlobServiceClient(connectionString)
            | None ->
                invalidOp
                    "Azure Storage connection string must be configured when running in local debug mode without a managed identity."

    /// Diff cache container client
    let diffCacheContainerClient = blobServiceClient.GetBlobContainerClient(Environment.GetEnvironmentVariable Constants.EnvironmentVariables.DiffContainerName)

    /// Recursive DirectoryVersion cache container client
    let directoryVersionContainerClient =
        blobServiceClient.GetBlobContainerClient(Environment.GetEnvironmentVariable Constants.EnvironmentVariables.DirectoryVersionContainerName)

    /// Diff cache container client
    let zipFileContainerClient =
        blobServiceClient.GetBlobContainerClient(Environment.GetEnvironmentVariable Constants.EnvironmentVariables.ZipFileContainerName)
