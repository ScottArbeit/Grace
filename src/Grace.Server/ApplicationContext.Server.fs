namespace Grace.Server

open Azure.Core
open Azure.Identity
open Azure.Storage
open Azure.Storage.Blobs
open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Types
open Grace.Shared
open Grace.Shared.AzureEnvironment
open Grace.Shared.Constants
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
open System.Diagnostics
open Microsoft.AspNetCore.Hosting
open Orleans.Serialization
open System.Net.Security
open Microsoft.AspNetCore.SignalR

module ApplicationContext =

    let mutable private configuration: IConfiguration = null
    let Configuration () : IConfiguration = configuration
    let mutable private log: ILogger = null

    /// Global dictionary of timing information for each request.
    let timings = ConcurrentDictionary<CorrelationId, List<Timing>>()

    /// Orleans client instance for the application.
    let mutable grainFactory: IGrainFactory = null
    //let orleansClient = ServiceCollection().FirstOrDefault(fun service -> service.ServiceType = typeof<IGrainFactory>).ImplementationInstance :?> IGrainFactory

    let mutable serviceProvider: IServiceProvider = null

    /// Actor state storage provider instance
    let mutable actorStateStorageProvider: ActorStateStorageProvider = ActorStateStorageProvider.Unknown

    /// Logger factory instance
    let mutable loggerFactory: ILoggerFactory = null

    /// Grace Server's universal .NET memory cache
    let mutable memoryCache: IMemoryCache = null

    /// CosmosDB client instance
    //let mutable cosmosClient: CosmosClient = null

    /// CosmosDB container instance (set during startup).
    let mutable cosmosContainer: Container = null

    /// Pub-sub settings for the application.
    let mutable pubSubSettings: GracePubSubSettings = GracePubSubSettings.Empty

    let private defaultAzureCredential = lazy (DefaultAzureCredential())

    /// Sets the Application global configuration.
    let setConfiguration (config: IConfiguration) =
        let assembly = Assembly.GetExecutingAssembly()
        let version = assembly.GetName().Version
        let fileInfo = FileInfo(assembly.Location)

        logToConsole $"Grace Server version: {version}; build time (UTC): {fileInfo.LastWriteTimeUtc}."

        configuration <- config
    //configuration.AsEnumerable() |> Seq.iter (fun kvp -> logToConsole $"{kvp.Key}: {kvp.Value}")

    /// Sets the Orleans client (IGrainFactory instance) for the application.
    let setOrleansClient client =
        grainFactory <- client
        setOrleansClient grainFactory

    /// Sets the ActorStateStorageProvider for the application.
    let setActorStateStorageProvider actorStateStorage =
        actorStateStorageProvider <- actorStateStorage
        logToConsole $"In ApplicationContext.Server.setActorStateStorageProvider: Setting actor state storage provider to {actorStateStorageProvider}."
        setActorStateStorageProvider actorStateStorageProvider

    /// Sets the ILoggerFactory for the application.
    let setLoggerFactory logFactory =
        loggerFactory <- logFactory
        log <- loggerFactory.CreateLogger("ApplicationContext.Server")
        setLoggerFactory loggerFactory

    let setPubSubSettings settings =
        pubSubSettings <- settings
        setPubSubSettings settings

    /// Holds information about each Azure Storage Account used by the application.
    type StorageAccount = { StorageAccountName: string; StorageAccountConnectionString: string }

    let mutable sharedKeyCredential: StorageSharedKeyCredential = null
    let mutable grpcPortListener: TcpListener = null
    let secondsToDelayReminderProcessing = 30.0

    let useManagedIdentity = AzureEnvironment.useManagedIdentity

    if not useManagedIdentity then
        let storageKey = Environment.GetEnvironmentVariable(EnvironmentVariables.AzureStorageKey)
        sharedKeyCredential <- StorageSharedKeyCredential(DefaultObjectStorageAccount, storageKey)

    let defaultObjectStorageProvider = ObjectStorageProvider.AzureBlobStorage

    let cosmosClientOptions =
        CosmosClientOptions(ApplicationName = "Grace.Server", LimitToEndpoint = false, UseSystemTextJsonSerializerWithOptions = Constants.JsonSerializerOptions)

    let private tryGetConfigValue (config: IConfiguration) (name: string) =
        if isNull config then
            None
        else
            let value = config[getConfigKey name]
            if String.IsNullOrWhiteSpace value then None else Some(value.Trim())

    let private tryGetCosmosConnectionString (config: IConfiguration) =
        tryGetConfigValue config EnvironmentVariables.AzureCosmosDBConnectionString

    let isGraceTesting =
        match Environment.GetEnvironmentVariable("GRACE_TESTING") with
        | null -> false
        | value ->
            value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)

    let isLocalDebugEnvironment =
        match Environment.GetEnvironmentVariable EnvironmentVariables.DebugEnvironment with
        | null -> false
        | value -> value.Equals("Local", StringComparison.OrdinalIgnoreCase)

    let private isLocalEndpoint (config: IConfiguration) =
        match tryGetCosmosConnectionString config with
        | Some value ->
            value.Contains("localhost", StringComparison.OrdinalIgnoreCase)
            || value.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase)
        | None -> false

    let private applyLocalEmulatorSettings (config: IConfiguration) =
        let useLocalEmulatorSettings =
            not useManagedIdentity
            && (isGraceTesting || isLocalDebugEnvironment || isLocalEndpoint config)

        if useLocalEmulatorSettings then
            // The CosmosDB emulator uses a self-signed certificate, and, by default, HttpClient will refuse
            //   to connect over https: if the certificate can't be traced back to a root.
            // These settings allow Grace Server to access the CosmosDB Emulator by bypassing TLS.
            let handler =
                new SocketsHttpHandler(
                    SslOptions =
                        new SslClientAuthenticationOptions(
                            TargetHost = "localhost", // SNI host_name must be DNS per RFC 6066
                            RemoteCertificateValidationCallback = (fun _ __ ___ ____ -> true) // emulator only
                        )
                )

            cosmosClientOptions.HttpClientFactory <- (fun () -> new HttpClient(handler, disposeHandler = true))

            // During debugging, we might want to see the responses.
            cosmosClientOptions.EnableContentResponseOnWrite <- true

            // When using the CosmosDB Emulator, these settings help with connectivity.
            cosmosClientOptions.LimitToEndpoint <- true
            cosmosClientOptions.ConnectionMode <- ConnectionMode.Gateway

    let private resolveSetting (config: IConfiguration) (name: string) =
        match tryGetConfigValue config name with
        | Some value -> value
        | None -> invalidOp $"Configuration value '{getConfigKey name}' must be set."

    /// Sets multiple values for the application. In functional programming, a global construct like this is used instead of dependency injection.
    let Set () =
        task {
            try
                if isNull configuration then
                    invalidOp "Configuration must be set before initializing ApplicationContext."

                applyLocalEmulatorSettings configuration

                let cosmosDatabaseName = resolveSetting configuration EnvironmentVariables.AzureCosmosDBDatabaseName
                let cosmosContainerName = resolveSetting configuration EnvironmentVariables.AzureCosmosDBContainerName

                let cosmosClient =
                    if useManagedIdentity then
                        let endpoint =
                            AzureEnvironment.tryGetCosmosEndpointUri ()
                            |> Option.defaultWith (fun () ->
                                invalidOp "Azure Cosmos DB endpoint must be configured when using a managed identity.")

                        new CosmosClient(endpoint.AbsoluteUri, defaultAzureCredential.Value, cosmosClientOptions)
                    else
                        let cosmosDbConnectionString = resolveSetting configuration EnvironmentVariables.AzureCosmosDBConnectionString
                        new CosmosClient(cosmosDbConnectionString, cosmosClientOptions)

                let database = cosmosClient.GetDatabase(cosmosDatabaseName)
                cosmosContainer <- database.GetContainer(cosmosContainerName)

                logToConsole $"Using CosmosDB database '{cosmosDatabaseName}' and container '{cosmosContainer.Id}'."

                // Inject things into Actor Services.
                setCosmosClient cosmosClient
                setCosmosContainer cosmosContainer
                setTimings timings

                let configurePubSubSettings () =
                    let rawSystem = Environment.GetEnvironmentVariable EnvironmentVariables.GracePubSubSystem

                    let system =
                        match rawSystem with
                        | value when value.Equals("AzureEventHubs", StringComparison.OrdinalIgnoreCase) -> GracePubSubSystem.AzureEventHubs
                        | value when value.Equals("AzureServiceBus", StringComparison.OrdinalIgnoreCase) -> GracePubSubSystem.AzureServiceBus
                        | value when value.Equals("AWS_SQS", StringComparison.OrdinalIgnoreCase) -> GracePubSubSystem.AwsSqs
                        | value when value.Equals("AWS-SQS", StringComparison.OrdinalIgnoreCase) -> GracePubSubSystem.AwsSqs
                        | value when value.Equals("AWS", StringComparison.OrdinalIgnoreCase) -> GracePubSubSystem.AwsSqs
                        | value when value.Equals("AWS SQS", StringComparison.OrdinalIgnoreCase) -> GracePubSubSystem.AwsSqs
                        | value when value.Equals("GCP", StringComparison.OrdinalIgnoreCase) -> GracePubSubSystem.GoogleCloudPubSub
                        | value when value.Equals("GOOGLE_CLOUD_PUBSUB", StringComparison.OrdinalIgnoreCase) -> GracePubSubSystem.GoogleCloudPubSub
                        | value when value.Equals("GOOGLECLOUDPUBSUB", StringComparison.OrdinalIgnoreCase) -> GracePubSubSystem.GoogleCloudPubSub
                        | _ -> GracePubSubSystem.UnknownPubSubProvider

                    let azureServiceBusSettings =
                        if system = GracePubSubSystem.AzureServiceBus then
                            let serviceBusConnectionString = Environment.GetEnvironmentVariable EnvironmentVariables.AzureServiceBusConnectionString

                            if not <| AzureEnvironment.useManagedIdentity then
                                if String.IsNullOrWhiteSpace(serviceBusConnectionString) then
                                    invalidOp
                                        $"Environment variable '{EnvironmentVariables.AzureServiceBusConnectionString}' must be set when {EnvironmentVariables.GracePubSubSystem} is {GracePubSubSystem.AzureServiceBus} and you're not using a managed identity."

                            let sb_namespace = Environment.GetEnvironmentVariable EnvironmentVariables.AzureServiceBusNamespace

                            if String.IsNullOrWhiteSpace(sb_namespace) then
                                invalidOp
                                    $"Environment variable '{EnvironmentVariables.AzureServiceBusNamespace}' must be set when {EnvironmentVariables.GracePubSubSystem} is {GracePubSubSystem.AzureServiceBus}."

                            let topic = Environment.GetEnvironmentVariable EnvironmentVariables.AzureServiceBusTopic

                            if String.IsNullOrWhiteSpace(topic) then
                                invalidOp
                                    $"Environment variable '{EnvironmentVariables.AzureServiceBusTopic}' must be set when {EnvironmentVariables.GracePubSubSystem} is {GracePubSubSystem.AzureServiceBus}."

                            let subscription = Environment.GetEnvironmentVariable EnvironmentVariables.AzureServiceBusSubscription

                            if String.IsNullOrWhiteSpace(subscription) then
                                invalidOp
                                    $"Environment variable '{EnvironmentVariables.AzureServiceBusSubscription}' must be set when {EnvironmentVariables.GracePubSubSystem} is {GracePubSubSystem.AzureServiceBus}."

                            let fullyQualifiedNamespace =
                                AzureEnvironment.tryGetServiceBusFullyQualifiedNamespace ()
                                |> Option.defaultWith (fun () ->
                                    invalidOp
                                        $"Environment variable '{EnvironmentVariables.AzureServiceBusNamespace}' must be set when {EnvironmentVariables.GracePubSubSystem} is {GracePubSubSystem.AzureServiceBus}.")

                            let useManagedIdentity = AzureEnvironment.useManagedIdentity

                            Some
                                { ConnectionString =
                                    if String.IsNullOrEmpty(serviceBusConnectionString) then
                                        String.Empty
                                    else
                                        serviceBusConnectionString
                                  FullyQualifiedNamespace = fullyQualifiedNamespace
                                  TopicName = topic
                                  SubscriptionName = subscription
                                  UseManagedIdentity = useManagedIdentity }
                        else
                            None

                    { System = system; AzureServiceBus = azureServiceBusSettings }

                configurePubSubSettings () |> setPubSubSettings

                logToConsole $"Grace pub-sub configured as:{Environment.NewLine}{serialize pubSubSettings}"

                logToConsole "Grace Server is ready."
            with ex ->
                logToConsole ($"{ex.ToStringDemystified()}")
        }
        :> Task
