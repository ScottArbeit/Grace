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
open Grace.Types.Common
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
open System.Security.Cryptography.X509Certificates
open System.Text
open System.Threading
open System.Threading.Tasks
open System.Diagnostics
open Microsoft.AspNetCore.Hosting
open Orleans.Serialization
open System.Net.Security
open Microsoft.AspNetCore.SignalR

/// Contains Grace Server application context behavior and supporting helpers.
module ApplicationContext =

    /// Environment variable that acknowledges the durable operational facts processor subscription exists.
    [<Literal>]
    let private AzureServiceBusOperationalFactsProcessorSubscription = "grace__azure_service_bus__operational_facts_processor_subscription"

    /// Fixed Service Bus subscription that consumes immutable operational usage facts.
    [<Literal>]
    let private OperationalFactsProcessorSubscriptionName = "operational-facts-processor"

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

    /// CosmosDB container instance (set during startup).
    let mutable cosmosContainer: Container = null

    /// Pub-sub settings for the application.
    let mutable pubSubSettings: GracePubSubSettings = GracePubSubSettings.Empty

    let private defaultAzureCredential = lazy (DefaultAzureCredential())

    /// Sets the Application global configuration.
    let setConfiguration (config: IConfiguration) =
        let buildInfo = BuildInfo.fromAssembly (Assembly.GetExecutingAssembly())

        logToConsole $"Grace Server build: {buildInfo.InformationalVersion}."

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

    /// Writes pub sub settings onto the current response or server state.
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

    /// Gets try get config value data needed by the server flow.
    let private tryGetConfigValue (config: IConfiguration) (name: string) =
        if isNull config then
            None
        else
            let value = config[getConfigKey name]
            if String.IsNullOrWhiteSpace value then None else Some(value.Trim())

    /// Gets try get cosmos connection string data needed by the server flow.
    let private tryGetCosmosConnectionString (config: IConfiguration) = tryGetConfigValue config EnvironmentVariables.AzureCosmosDBConnectionString

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

    /// Determines whether local endpoint.
    let private isLocalEndpoint (config: IConfiguration) =
        match tryGetCosmosConnectionString config with
        | Some value ->
            value.Contains("localhost", StringComparison.OrdinalIgnoreCase)
            || value.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase)
        | None -> false

    /// Implements apply local emulator settings for the server request pipeline.
    let private applyLocalEmulatorSettings (config: IConfiguration) =
        let useLocalEmulatorSettings =
            not useManagedIdentity
            && (isGraceTesting
                || isLocalDebugEnvironment
                || isLocalEndpoint config)

        if useLocalEmulatorSettings then
            // The CosmosDB emulator uses a self-signed certificate, and, by default, HttpClient will refuse
            //   to connect over https: if the certificate can't be traced back to a root.
            // These settings allow Grace Server to access the CosmosDB Emulator by bypassing TLS.
            cosmosClientOptions.HttpClientFactory <-
                fun () ->
                    let handler = new HttpClientHandler()
                    handler.ServerCertificateCustomValidationCallback <- HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                    new HttpClient(handler, disposeHandler = true)

            cosmosClientOptions.ServerCertificateCustomValidationCallback <- Func<X509Certificate2, X509Chain, SslPolicyErrors, bool>(fun _ _ _ -> true)

            // During debugging, we might want to see the responses.
            cosmosClientOptions.EnableContentResponseOnWrite <- true

            // When using the CosmosDB Emulator, these settings help with connectivity.
            cosmosClientOptions.LimitToEndpoint <- true
            cosmosClientOptions.ConnectionMode <- ConnectionMode.Gateway

    /// Resolves resolve setting data from request or repository state.
    let private resolveSetting (config: IConfiguration) (name: string) =
        match tryGetConfigValue config name with
        | Some value -> value
        | None -> invalidOp $"Configuration value '{getConfigKey name}' must be set."

    /// Extracts a named segment from an Azure-style connection string.
    let private tryGetConnectionStringSegment segmentName (connectionString: string) =
        if String.IsNullOrWhiteSpace(connectionString) then
            None
        else
            connectionString.Split([| ';' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.tryPick (fun segment ->
                let idx = segment.IndexOf('=')

                if idx <= 0 then
                    None
                else
                    let key = segment.Substring(0, idx).Trim()
                    let value = segment.Substring(idx + 1).Trim()

                    if
                        key.Equals(segmentName, StringComparison.OrdinalIgnoreCase)
                        && not (String.IsNullOrWhiteSpace(value))
                    then
                        Some value
                    else
                        None)

    /// Normalizes a Service Bus namespace value for managed-identity clients and diagnostics.
    let private normalizeServiceBusNamespace (value: string) =
        let trimmed = value.Trim()

        let withoutScheme =
            if trimmed.StartsWith("sb://", StringComparison.OrdinalIgnoreCase) then
                trimmed.Substring(5)
            else
                trimmed

        let normalizedNamespace = withoutScheme.Trim().TrimEnd('/')

        if normalizedNamespace.Contains(".") then
            normalizedNamespace
        else
            $"{normalizedNamespace}.servicebus.windows.net"

    /// Resolves the Service Bus namespace from the explicit namespace setting or connection string endpoint.
    let private resolveServiceBusFullyQualifiedNamespace (serviceBusConnectionString: string) =
        match Environment.GetEnvironmentVariable EnvironmentVariables.AzureServiceBusNamespace with
        | value when not (String.IsNullOrWhiteSpace(value)) -> Some(normalizeServiceBusNamespace value)
        | _ ->
            serviceBusConnectionString
            |> tryGetConnectionStringSegment "Endpoint"
            |> Option.map normalizeServiceBusNamespace

    /// Sets multiple values for the application. In functional programming, a global construct like this is used instead of dependency injection.
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

                let topic = Environment.GetEnvironmentVariable EnvironmentVariables.AzureServiceBusTopic

                if String.IsNullOrWhiteSpace(topic) then
                    invalidOp
                        $"Environment variable '{EnvironmentVariables.AzureServiceBusTopic}' must be set when {EnvironmentVariables.GracePubSubSystem} is {GracePubSubSystem.AzureServiceBus}."

                let operationalFactsTopic = Environment.GetEnvironmentVariable EnvironmentVariables.AzureServiceBusOperationalFactsTopic

                if String.IsNullOrWhiteSpace(operationalFactsTopic) then
                    invalidOp
                        $"Environment variable '{EnvironmentVariables.AzureServiceBusOperationalFactsTopic}' must be set when {EnvironmentVariables.GracePubSubSystem} is {GracePubSubSystem.AzureServiceBus}."

                if String.Equals(topic.Trim(), operationalFactsTopic.Trim(), StringComparison.OrdinalIgnoreCase) then
                    invalidOp
                        $"Environment variable '{EnvironmentVariables.AzureServiceBusOperationalFactsTopic}' must differ from '{EnvironmentVariables.AzureServiceBusTopic}' so usage facts cannot enter the GraceEvent topic/subscriber path."

                let operationalFactsProcessorSubscription =
                    match Environment.GetEnvironmentVariable AzureServiceBusOperationalFactsProcessorSubscription with
                    | null -> String.Empty
                    | value -> value.Trim()

                if not (String.Equals(operationalFactsProcessorSubscription, OperationalFactsProcessorSubscriptionName, StringComparison.Ordinal)) then
                    invalidOp
                        $"Environment variable '{AzureServiceBusOperationalFactsProcessorSubscription}' must be set to '{OperationalFactsProcessorSubscriptionName}' when {EnvironmentVariables.GracePubSubSystem} is {GracePubSubSystem.AzureServiceBus}, after confirming topic '{EnvironmentVariables.AzureServiceBusOperationalFactsTopic}' has that durable subscription."

                let subscription = Environment.GetEnvironmentVariable EnvironmentVariables.AzureServiceBusSubscription

                if String.IsNullOrWhiteSpace(subscription) then
                    invalidOp
                        $"Environment variable '{EnvironmentVariables.AzureServiceBusSubscription}' must be set when {EnvironmentVariables.GracePubSubSystem} is {GracePubSubSystem.AzureServiceBus}."

                let useManagedIdentity = AzureEnvironment.useManagedIdentityForServiceBus

                if
                    not useManagedIdentity
                    && String.IsNullOrWhiteSpace(serviceBusConnectionString)
                then
                    invalidOp
                        $"Environment variable '{EnvironmentVariables.AzureServiceBusConnectionString}' must be set when {EnvironmentVariables.GracePubSubSystem} is {GracePubSubSystem.AzureServiceBus} and you're not using a managed identity."

                let fullyQualifiedNamespace =
                    resolveServiceBusFullyQualifiedNamespace serviceBusConnectionString
                    |> Option.defaultWith (fun () ->
                        invalidOp
                            $"Environment variable '{EnvironmentVariables.AzureServiceBusNamespace}' or an Endpoint segment in '{EnvironmentVariables.AzureServiceBusConnectionString}' must be set when {EnvironmentVariables.GracePubSubSystem} is {GracePubSubSystem.AzureServiceBus}.")

                Some
                    {
                        ConnectionString =
                            if String.IsNullOrEmpty(serviceBusConnectionString) then
                                String.Empty
                            else
                                serviceBusConnectionString
                        FullyQualifiedNamespace = fullyQualifiedNamespace
                        TopicName = topic
                        SubscriptionName = subscription
                        UseManagedIdentity = useManagedIdentity
                    }
            else
                None

        { System = system; AzureServiceBus = azureServiceBusSettings }

    /// Handles the Grace Server set request.
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
                            |> Option.defaultWith (fun () -> invalidOp "Azure Cosmos DB endpoint must be configured when using a managed identity.")

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

                configurePubSubSettings () |> setPubSubSettings

                logToConsole $"Grace pub-sub configured as:{Environment.NewLine}{serialize pubSubSettings}"

                logToConsole "Grace Server is ready."
            with
            | ex -> logToConsole ($"{ex.ToStringDemystified()}")
        }
        :> Task
