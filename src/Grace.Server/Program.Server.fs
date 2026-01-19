namespace Grace.Server

open Azure.Core
open Azure.Data.Tables
open Azure.Identity
open Azure.Storage.Blobs
open dotenv.net
open Grace.Actors.Interfaces
open Grace.Server.ApplicationContext
open Grace.Shared
open Grace.Shared.AzureEnvironment
open Grace.Shared.Constants
open Grace.Types.Types
open Grace.Shared.Utilities
open MessagePack
open Microsoft.AspNetCore
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Server.Kestrel.Core
open Microsoft.Azure.Cosmos
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Caching.Memory
open Orleans
open Orleans.Clustering.AzureStorage
open Orleans.Hosting
open Orleans.Configuration
open Orleans.Persistence
open Orleans.Persistence.Cosmos
open Orleans.Runtime
open Orleans.Hosting
open Orleans.Serialization
open Orleans.Storage
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.IO
open System.Linq
open System.Net.Http
open System.Net.Security
open System.Security.Authentication
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Grace.Actors
open System.Runtime.CompilerServices
open Microsoft.AspNetCore.Builder
open System.Diagnostics
open System.Globalization
open Azure.Storage

module OrleansFsharpFix =
    // Grace.Orleans.CodeGen is the name of the C# codegen project.
    [<assembly: Orleans.ApplicationPartAttribute("Grace.Orleans.CodeGen")>]
    do ()

module Program =

    [<InternalsVisibleTo("Host")>]
    do ()

    type FileLogger(name: string, minLevel: LogLevel, writer: StreamWriter, scopeProvider: unit -> IExternalScopeProvider) =
        let emptyScope =
            { new IDisposable with
                member _.Dispose() = ()
            }

        interface ILogger with
            member _.IsEnabled level = level >= minLevel

            member _.BeginScope<'TState>(state: 'TState) : IDisposable =
                match scopeProvider () with
                | null -> emptyScope
                | provider -> provider.Push(state)

            member _.Log<'TState>(level: LogLevel, eventId: EventId, state: 'TState, ex: exn, formatter: Func<'TState, exn, string>) =
                if level >= minLevel && not (isNull formatter) then
                    let message = formatter.Invoke(state, ex)

                    let scopes =
                        match scopeProvider () with
                        | null -> String.Empty
                        | provider ->
                            let items = ResizeArray<string>()
                            provider.ForEachScope((fun scope _ -> items.Add(scope.ToString())), ())

                            if items.Count = 0 then
                                String.Empty
                            else
                                let connector = " => "
                                $" [Scope: {String.Join(connector, items)}]"

                    let exceptionText = if isNull ex then String.Empty else Environment.NewLine + ex.ToString()

                    lock writer (fun () ->
                        writer.WriteLine($"{DateTime.UtcNow:O} [{level}] {name}: {message}{scopes}{exceptionText}")
                        writer.Flush())

    type FileLoggerProvider(filePath: string, minLevel: LogLevel) =
        let fileStream =
            try
                new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)
            with
            | :? IOException -> raise (InvalidOperationException($"Log file '{filePath}' already exists; refusing to overwrite."))

        let writer = new StreamWriter(fileStream)
        let mutable scopeProvider: IExternalScopeProvider = new LoggerExternalScopeProvider()

        do writer.AutoFlush <- true

        interface ILoggerProvider with
            member _.CreateLogger(categoryName) = new FileLogger(categoryName, minLevel, writer, (fun () -> scopeProvider)) :> ILogger
            member _.Dispose() = writer.Dispose()

        interface ISupportExternalScope with
            member _.SetScopeProvider(provider) =
                scopeProvider <-
                    match provider with
                    | null -> new LoggerExternalScopeProvider() :> IExternalScopeProvider
                    | _ -> provider

    type SystemTextJsonGrainStorageSerializer(options: JsonSerializerOptions) =
        interface IGrainStorageSerializer with
            member _.Serialize(obj) =
                let t = obj.GetType()
                let bytes = JsonSerializer.SerializeToUtf8Bytes(obj, t, options)
                BinaryData(bytes)

            member _.Deserialize<'T>(data: BinaryData) =
                use stream = data.ToStream()
                JsonSerializer.Deserialize<'T>(stream, options)

    let private defaultAzureCredential = lazy (DefaultAzureCredential())

    // Load environment variables from .env file, if it exists.
    let envPaths =
        [|
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".env") // during debug
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".env")
        |] // during debug

    for envPath in envPaths do
        let path = Path.GetFullPath(envPath)
        logToConsole $"Checking for .env file at {path}."

        if File.Exists(path) then
            logToConsole $"Loading environment variables from {path}."
            DotEnv.Load(DotEnvOptions(envFilePaths = [| path |], ignoreExceptions = true))

    /// Configures and builds the generic host for the Grace server application.
    let createHostBuilder (args: string []) (configuration: IConfiguration) =
        let storageEndpoints = AzureEnvironment.storageEndpoints

        let azureStorageConnectionString =
            match storageEndpoints.ConnectionString with
            | Some value -> value
            | None -> Environment.GetEnvironmentVariable EnvironmentVariables.AzureStorageConnectionString

        let azureCosmosDBConnectionString =
            match AzureEnvironment.cosmosConnectionString with
            | Some value -> value
            | None -> Environment.GetEnvironmentVariable EnvironmentVariables.AzureCosmosDBConnectionString

        let hasAzureStorageConnectionString =
            not
            <| String.IsNullOrWhiteSpace azureStorageConnectionString

        let debugEnvironment = configuration[getConfigKey EnvironmentVariables.DebugEnvironment]
        let isLocalDebug = String.Equals(debugEnvironment, "Local", StringComparison.OrdinalIgnoreCase)
        let isAzureDebug = String.Equals(debugEnvironment, "Azure", StringComparison.OrdinalIgnoreCase)

        let orleansClusterId = configuration[getConfigKey EnvironmentVariables.OrleansClusterId]
        let orleansServiceId = configuration[getConfigKey EnvironmentVariables.OrleansServiceId]

        let createTableClientOptions () =
            let options = TableClientOptions()

            if isLocalDebug || isAzureDebug then
                options.Retry.Mode <- RetryMode.Fixed
                options.Retry.Delay <- TimeSpan.FromMilliseconds(200.0)
                options.Retry.MaxDelay <- TimeSpan.FromSeconds(1.0)
                options.Retry.MaxRetries <- 1
                options.Retry.NetworkTimeout <- TimeSpan.FromSeconds(5.0)

            options

        let waitForAzureTableReady (client: TableServiceClient) =
            match AzureEnvironment.debugEnvironment with
            | Some value when value.Equals("Local", StringComparison.OrdinalIgnoreCase) ->
                let sw = Stopwatch.StartNew()
                let timeout = TimeSpan.FromSeconds(60.0)
                let delay = TimeSpan.FromSeconds(1.0)

                let rec poll attempt =
                    try
                        client.GetProperties() |> ignore
                        logToConsole $"Azure Table endpoint ready after {attempt} attempt(s)."
                    with
                    | ex ->
                        if sw.Elapsed >= timeout then
                            logToConsole $"Azure Table endpoint was not ready after {timeout.TotalSeconds} seconds: {ex.Message}"
                            reraise ()
                        else
                            Thread.Sleep(delay)
                            poll (attempt + 1)

                poll 0
            | _ -> ()

        let logDirectory =
            let directoryValue = Environment.GetEnvironmentVariable EnvironmentVariables.GraceLogDirectory

            if String.IsNullOrWhiteSpace directoryValue then
                invalidOp $"Environment variable '{EnvironmentVariables.GraceLogDirectory}' must be set to a writable directory for log files."

            let fullPath = Path.GetFullPath(directoryValue)
            Directory.CreateDirectory(fullPath) |> ignore
            fullPath

        let logFileName =
            getCurrentInstant()
                .ToString("yyyy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture)
            + ".log"

        let logFilePath = Path.Combine(logDirectory, logFileName)
        let fileLoggerProvider = new FileLoggerProvider(logFilePath, LogLevel.Debug)

        logToConsole $"Grace Server logs will be written to {logFilePath}"

        let hostBuilder = Host.CreateDefaultBuilder(args)

        hostBuilder
            .UseContentRoot(Directory.GetCurrentDirectory())
            .UseOrleans(fun siloBuilder ->
                siloBuilder
                    .Configure<ClusterMembershipOptions>(fun (options: ClusterMembershipOptions) ->
                        options.DefunctSiloExpiration <- TimeSpan.FromMinutes(5.0)
                        options.DefunctSiloCleanupPeriod <- TimeSpan.FromMinutes(1.0)

                        if isLocalDebug || isAzureDebug then
                            options.IAmAliveTablePublishTimeout <- TimeSpan.FromSeconds(5.0)
                            options.NumMissedTableIAmAliveLimit <- 1
                            options.TableRefreshTimeout <- TimeSpan.FromSeconds(5.0)
                            options.DeathVoteExpirationTimeout <- TimeSpan.FromSeconds(15.0)
                            options.DefunctSiloExpiration <- TimeSpan.FromSeconds(15.0)
                            options.DefunctSiloCleanupPeriod <- TimeSpan.FromSeconds(5.0))
                    .Configure<ClusterOptions>(fun (options: ClusterOptions) ->
                        options.ClusterId <- orleansClusterId
                        options.ServiceId <- orleansServiceId)
                    .Configure<SiloOptions>(fun (options: SiloOptions) -> options.SiloName <- $"Silo-{orleansServiceId}")
                    .Configure<SiloMessagingOptions>(fun (options: SiloMessagingOptions) -> options.ResponseTimeout <- TimeSpan.FromSeconds(60.0))
                    .Configure<ClientMessagingOptions>(fun (options: ClientMessagingOptions) -> options.ResponseTimeout <- TimeSpan.FromSeconds(60.0))
                    .Configure<GrainCollectionOptions>(fun (options: GrainCollectionOptions) ->
                        options.CollectionAge <- TimeSpan.FromMinutes(15.0)

                        options.ClassSpecificCollectionAge[
                            $"{(typeof<GrainRepository.GrainRepositoryActor>)
                                   .FullName}"
                        ] <- TimeSpan.FromMinutes(5.0))
                    .UseAzureStorageClustering(fun (options: AzureStorageClusteringOptions) ->
                        logToConsole
                            $"Orleans clustering using Azure Tables at {storageEndpoints.TableEndpoint}; account {storageEndpoints.AccountName}; debug env {debugEnvironment}; storage connection string present {hasAzureStorageConnectionString}; managed identity {AzureEnvironment.useManagedIdentity}; managed identity for storage {AzureEnvironment.useManagedIdentityForStorage}."

                        let tableClientOptions = createTableClientOptions ()

                        let tableServiceClient =
                            if AzureEnvironment.useManagedIdentityForStorage then
                                TableServiceClient(storageEndpoints.TableEndpoint, defaultAzureCredential.Value, tableClientOptions)
                            else if String.IsNullOrWhiteSpace azureStorageConnectionString then
                                invalidOp "Azure Storage connection string must be configured for clustering when managed identity is disabled."
                            else
                                TableServiceClient(azureStorageConnectionString, tableClientOptions)

                        waitForAzureTableReady tableServiceClient

                        options.TableServiceClient <- tableServiceClient)
                    .AddCosmosGrainStorage(
                        GraceActorStorage,
                        (fun (options: CosmosGrainStorageOptions) ->
                            options.ContainerName <- configuration[getConfigKey EnvironmentVariables.AzureCosmosDBContainerName]
                            options.DatabaseName <- configuration[getConfigKey EnvironmentVariables.AzureCosmosDBDatabaseName]

                            logToConsole $"Configuring Cosmos DB grain storage with database '{options.DatabaseName}' and container '{options.ContainerName}'."

                            // All Cosmos DB resources should be created prior to starting Grace.
                            options.IsResourceCreationEnabled <- false

                            options.ConfigureCosmosClient (fun (serviceProvider: IServiceProvider) ->
                                let cosmosClientOptions = CosmosClientOptions()
                                cosmosClientOptions.ApplicationName <- "Grace.Server"
                                cosmosClientOptions.LimitToEndpoint <- false
                                cosmosClientOptions.UseSystemTextJsonSerializerWithOptions <- Grace.Shared.Constants.JsonSerializerOptions

                                // If we're doing local debugging, and not using managed identity, we assume we're using the Cosmos DB emulator.
                                // The emulator uses a self-signed certificate, so we need to bypass certificate validation.

                                if isLocalDebug
                                   && not
                                      <| AzureEnvironment.useManagedIdentityForCosmos then
                                    cosmosClientOptions.LimitToEndpoint <- true
                                    cosmosClientOptions.ConnectionMode <- ConnectionMode.Gateway
                                    cosmosClientOptions.EnableContentResponseOnWrite <- true

                                    cosmosClientOptions.HttpClientFactory <-
                                        fun () ->
                                            logToConsole "Creating custom HttpClient for Cosmos DB."

                                            let handler =
                                                new SocketsHttpHandler(
                                                    SslOptions =
                                                        new SslClientAuthenticationOptions(
                                                            TargetHost = "localhost",
                                                            RemoteCertificateValidationCallback = (fun _ _ _ _ -> true)
                                                        )
                                                )

                                            new HttpClient(handler, disposeHandler = true)

                                let cosmosClient =
                                    if AzureEnvironment.useManagedIdentity then
                                        let endpoint =
                                            AzureEnvironment.tryGetCosmosEndpointUri ()
                                            |> Option.defaultWith (fun () ->
                                                invalidOp "Azure Cosmos DB endpoint must be configured when using a managed identity.")

                                        new CosmosClient(endpoint.AbsoluteUri, defaultAzureCredential.Value, cosmosClientOptions)
                                    else
                                        if String.IsNullOrWhiteSpace azureCosmosDBConnectionString then
                                            invalidOp "Cosmos DB connection string must be configured when managed identity is disabled."

                                        new CosmosClient(azureCosmosDBConnectionString, cosmosClientOptions)

                                ValueTask.FromResult(cosmosClient))),
                        typeof<GracePartitionKeyProvider>
                    )
                    .AddAzureBlobGrainStorage(
                        GraceDiffStorage,
                        (fun (options: AzureBlobStorageOptions) ->
                            options.BlobServiceClient <- Context.blobServiceClient
                            options.ContainerName <- configuration[getConfigKey EnvironmentVariables.DiffContainerName]
                            options.GrainStorageSerializer <- SystemTextJsonGrainStorageSerializer(Constants.JsonSerializerOptions))
                    )
                    .AddActivityPropagation()

                |> ignore

                siloBuilder.AddMemoryGrainStorage(GraceInMemoryStorage)
                |> ignore

                siloBuilder.Services.AddSerializer (fun serializerBuilder ->
                    serializerBuilder.AddJsonSerializer(
                        isSupported =
                            (fun _type ->
                                not <| String.IsNullOrEmpty(_type.Namespace)
                                && _type.Namespace.StartsWith("Grace", StringComparison.InvariantCulture)),
                        jsonSerializerOptions = Constants.JsonSerializerOptions
                    )
                    |> ignore)
                |> ignore)
            .ConfigureLogging(fun logConfig ->
                logConfig
                    .SetMinimumLevel(LogLevel.Debug)
                    .AddFilter("Orleans", LogLevel.Information)
                    .AddFilter("Orleans.Providers", LogLevel.Debug)
                |> ignore

                logConfig.AddProvider(fileLoggerProvider)
                |> ignore

                logConfig.AddOpenTelemetry(fun openTelemetryOptions -> openTelemetryOptions.IncludeScopes <- true)
                |> ignore)
            .ConfigureWebHostDefaults(fun webBuilder ->
                webBuilder
                    .UseStartup<Application.Startup>()
                    .UseKestrel(fun kestrelServerOptions ->
                        kestrelServerOptions.ConfigureEndpointDefaults(fun listenOptions -> listenOptions.Protocols <- HttpProtocols.Http1AndHttp2)

                        kestrelServerOptions.ConfigureHttpsDefaults (fun options ->
                            options.SslProtocols <- SslProtocols.Tls12 ||| SslProtocols.Tls13
#if DEBUG
                            options.AllowAnyClientCertificate()
#endif
                        ))
                |> ignore)
            .Build()

    [<EntryPoint>]
    let main args =
        (task {
            try
                logToConsole "----------------------------- Starting Grace Server ------------------------------"

                // Build the configuration
                let environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")

                let configurationBuilder =
                    ConfigurationBuilder()
                        .AddJsonFile("appsettings.json", true, true) // Load appsettings.json

                if not <| String.IsNullOrWhiteSpace(environment) then
                    configurationBuilder.AddJsonFile($"appsettings.{environment}.json", true, true) // Load environment-specific settings
                    |> ignore

                let configuration =
                    configurationBuilder
                        .AddEnvironmentVariables()
                        .AddUserSecrets() // Use `dotnet user-secrets` to store sensitive settings during development
                        .Build()

                // Store the configuration in memory cache for easy access throughout the application.
                use configurationEntry = memoryCache.CreateEntry(MemoryCache.GraceConfiguration)
                configurationEntry.Value <- configuration
                configurationEntry.Priority <- CacheItemPriority.NeverRemove
                configurationEntry.Dispose()

                logToConsole "Configuration settings saved in memory cache."

                use host = createHostBuilder args configuration

                // Placing some much-used services into ApplicationContext where they're easy to find.
                Context.setHostServiceProvider host.Services

                let orleansClient = host.Services.GetService(typeof<IGrainFactory>) :?> IGrainFactory
                ApplicationContext.setOrleansClient orleansClient

                let loggerFactory = host.Services.GetService(typeof<ILoggerFactory>) :?> ILoggerFactory
                ApplicationContext.setLoggerFactory loggerFactory

                // Dump out configuration settings at startup for debugging purposes.
                //logToConsole "Configuration settings:"
                //for pair in config.AsEnumerable() do
                //    logToConsole $"  {pair.Key} = {pair.Value}"

                do! host.RunAsync()

                return 0 // Return an integer exit code
            with
            | ex ->
                logToConsole $"Fatal error starting Grace Server.{Environment.NewLine}{ex.ToStringDemystified()}"
                return -1
        })
            .Result
