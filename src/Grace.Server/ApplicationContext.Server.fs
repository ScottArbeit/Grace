namespace Grace.Server

open Azure.Storage
open Azure.Storage.Blobs
open CosmosJsonSerializer
open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Types
open Grace.Shared
open Grace.Types.Types
open Grace.Shared.Utilities
open Microsoft.Azure.Cosmos
open Microsoft.Extensions.Caching.Memory
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.Extensions.ObjectPool
open NodaTime
open Orleans
open Orleans.Runtime
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Linq
open System.Net.Http
open System.Net.Sockets
open System.Reflection
open System.Text
open System.Threading
open System.Threading.Tasks

module ApplicationContext =

    let mutable private configuration: IConfiguration = null
    let Configuration () : IConfiguration = configuration
    let mutable private log: ILogger = null

    /// Global dictionary of timing information for each request.
    let timings = ConcurrentDictionary<CorrelationId, List<Timing>>()

    /// Orleans client instance for the application.
    let mutable grainFactory: IGrainFactory = null
    //let orleansClient = ServiceCollection().FirstOrDefault(fun service -> service.ServiceType = typeof<IGrainFactory>).ImplementationInstance :?> IGrainFactory

    /// Dapr actor state storage provider instance
    let mutable actorStateStorageProvider: ActorStateStorageProvider = ActorStateStorageProvider.Unknown

    /// Logger factory instance
    let mutable loggerFactory: ILoggerFactory = null

    /// Grace Server's universal .NET memory cache
    let mutable memoryCache: IMemoryCache = null

    /// CosmosDB client instance
    let mutable cosmosClient: CosmosClient = null

    /// CosmosDB container client instance
    let mutable cosmosContainer: Container = null

    /// Sets the Application global configuration.
    let setConfiguration (config: IConfiguration) =
        let assembly = Assembly.GetExecutingAssembly()
        let version = assembly.GetName().Version
        let fileInfo = FileInfo(assembly.Location)

        logToConsole $"Grace Server version: {version}; build time (UTC): {fileInfo.LastWriteTimeUtc}."

        configuration <- config
    //configuration.AsEnumerable() |> Seq.iter (fun kvp -> logToConsole $"{kvp.Key}: {kvp.Value}")

    /// Sets the Orleans client (IGrainFactory instance) for the application.
    let setGrainFactory client =
        grainFactory <- client
        setGrainFactory grainFactory

    /// Sets the ActorStateStorageProvider for the application.
    let setActorStateStorageProvider actorStateStorage =
        actorStateStorageProvider <- actorStateStorage
        setActorStateStorageProvider actorStateStorageProvider

    /// Sets the ILoggerFactory for the application.
    let setLoggerFactory logFactory =
        loggerFactory <- logFactory
        log <- loggerFactory.CreateLogger("ApplicationContext.Server")
        setLoggerFactory loggerFactory

    /// Holds information about each Azure Storage Account used by the application.
    type StorageAccount = { StorageAccountName: string; StorageAccountConnectionString: string }

    let graceServerAppId = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DaprAppId)

    let daprHttpEndpoint =
        $"{Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DaprServerUri)}:{Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DaprHttpPort)}"

    let daprGrpcEndpoint =
        $"{Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DaprServerUri)}:{Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DaprGrpcPort)}"

    logToConsole $"daprHttpEndpoint: {daprHttpEndpoint}; daprGrpcEndpoint: {daprGrpcEndpoint}"

    let mutable sharedKeyCredential: StorageSharedKeyCredential = null
    let mutable grpcPortListener: TcpListener = null
    let secondsToWaitForDaprToBeReady = 30.0

    let defaultObjectStorageProvider = ObjectStorageProvider.AzureBlobStorage

    /// Sets multiple values for the application. In functional programming, a global construct like this is used instead of dependency injection.
    let Set =
        task {
            let mutable isReady = false

            // Wait for the Dapr gRPC port to be ready.
            logToConsole
                $"""----------------------------------------------------------------------------------------------
                                Pausing to check for an active gRPC connection with the Dapr sidecar.
                                -----------------------------------------------------------------------------------------------
                                Grace Server should not complete startup and accept requests until we know that we can
                                talk to Dapr, so Grace Server will wait for {secondsToWaitForDaprToBeReady} seconds for Dapr to be ready.
                                If no connection is made, that almost always means that something happened trying
                                to start the Dapr sidecar, and Kubernetes is going to restart it. If that happens,
                                Grace Server will exit to allow Kubernetes to restart it; by the time Grace Server
                                restarts, the Dapr sidecar is usually up and running, and we should connect right away.
                                -----------------------------------------------------------------------------------------------"""

            let mutable gRPCPort: int = 50001 // This is Dapr's default gRPC port.

            let grpcPortString = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DaprGrpcPort)

            if Int32.TryParse(grpcPortString, &gRPCPort) then
                let startTime = getCurrentInstant ()

                while not <| isReady do
                    do! Task.Delay(TimeSpan.FromSeconds(2.0))
                    logToConsole $"Checking for an active TcpListner on gRPC port {gRPCPort}."

                    let tcpListeners =
                        Net.NetworkInformation.IPGlobalProperties
                            .GetIPGlobalProperties()
                            .GetActiveTcpListeners()
                    //if tcpListeners.Length > 0 then logToConsole "Active TCP listeners:"
                    //for t in tcpListeners do
                    //    logToConsole $"{t.Address}:{t.Port} {t.AddressFamily}."
                    if tcpListeners.Any(fun tcpListener -> tcpListener.Port = gRPCPort) then
                        logToConsole $"gRPC port is ready."
                        isReady <- true
                    else if getCurrentInstant().Minus(startTime) > Duration.FromSeconds(secondsToWaitForDaprToBeReady) then
                        logToConsole $"gRPC port is not ready after {secondsToWaitForDaprToBeReady} seconds. Exiting."
                        Environment.Exit(-1)
            else
                logToConsole $"Could not parse gRPC port {grpcPortString} as a port number. Exiting."
                Environment.Exit(-1)

            let storageKey = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.AzureStorageKey)

            sharedKeyCredential <- StorageSharedKeyCredential(DefaultObjectStorageAccount, storageKey)

            let cosmosDbConnectionString = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.AzureCosmosDBConnectionString)

            let cosmosDatabaseName = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.CosmosDatabaseName)

            let cosmosContainerName = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.CosmosContainerName)

            // Get a reference to the CosmosDB database.
            let cosmosClientOptions =
                CosmosClientOptions(
                    ApplicationName = graceServerAppId,
                    EnableContentResponseOnWrite = false,
                    LimitToEndpoint = true,
                    Serializer = new CosmosJsonSerializer(Constants.JsonSerializerOptions)
                )

#if DEBUG
            // The CosmosDB emulator uses a self-signed certificate, and, by default, HttpClient will refuse
            //   to connect over https: if the certificate can't be traced back to a root.
            // These settings allow Grace Server to access the CosmosDB Emulator by bypassing TLS.
            // And none of this matters if Dapr won't bypass TLS as well. 🤷
            //let httpClientFactory = fun () ->
            //    let httpMessageHandler: HttpMessageHandler = new HttpClientHandler(
            //        ServerCertificateCustomValidationCallback = (fun _ _ _ _ -> true))
            //    new HttpClient(httpMessageHandler)
            //cosmosClientOptions.HttpClientFactory <- httpClientFactory
            //cosmosClientOptions.ConnectionMode <- ConnectionMode.Direct
#endif
            cosmosClient <- new CosmosClient(cosmosDbConnectionString, cosmosClientOptions)
            let! databaseResponse = cosmosClient.CreateDatabaseIfNotExistsAsync(cosmosDatabaseName)
            let database = databaseResponse.Database

            // Get a reference to the CosmosDB container.
            let containerProperties = ContainerProperties(Id = cosmosContainerName, PartitionKeyPath = "/PartitionKey", DefaultTimeToLive = 3600)

            let! containerResponse = database.CreateContainerIfNotExistsAsync(containerProperties)
            cosmosContainer <- containerResponse.Container

            logToConsole $"CosmosDB database '{cosmosDatabaseName}' and container '{cosmosContainer.Id}' are ready."

            // Create a MemoryCache instance.
            let memoryCacheOptions =
                MemoryCacheOptions(TrackStatistics = false, TrackLinkedCacheEntries = false, ExpirationScanFrequency = TimeSpan.FromSeconds(30.0))
            //memoryCacheOptions.SizeLimit <- 100L * 1024L * 1024L
            //memoryCache <- new MemoryCache(memoryCacheOptions, loggerFactory)

            // Inject things into Grace.Shared.
            //Utilities.memoryCache <- memoryCache

            // Inject things into Actor Services.
            setCosmosClient cosmosClient
            setCosmosContainer cosmosContainer
            //setMemoryCache memoryCache
            setTimings timings

            logToConsole "Grace Server is ready."
        }
        :> Task
