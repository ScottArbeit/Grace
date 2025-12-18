namespace Grace.Server

open Azure.Core
open Azure.Data.Tables
open Azure.Identity
open Azure.Storage.Queues
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
open System.Threading.Tasks
open Grace.Actors
open System.Runtime.CompilerServices
open Microsoft.AspNetCore.Builder
open System.Diagnostics

module OrleansFsharpFix =
    // Grace.Orleans.CodeGen is the name of the C# codegen project.
    [<assembly: Orleans.ApplicationPartAttribute("Grace.Orleans.CodeGen")>]

    // other assemblies matching NuGet packages
    [<assembly: Orleans.ApplicationPartAttribute("Orleans.Core.Abstractions")>]
    [<assembly: Orleans.ApplicationPartAttribute("Orleans.Serialization")>]
    [<assembly: Orleans.ApplicationPartAttribute("Orleans.Core")>]
    [<assembly: Orleans.ApplicationPartAttribute("Orleans.Persistence.Memory")>]
    [<assembly: Orleans.ApplicationPartAttribute("Orleans.Runtime")>]
    [<assembly: Orleans.ApplicationPartAttribute("OrleansDashboard.Core")>]
    [<assembly: Orleans.ApplicationPartAttribute("OrleansDashboard")>]
    [<assembly: Orleans.ApplicationPartAttribute("Orleans.Serialization.Abstractions")>]
    [<assembly: Orleans.ApplicationPartAttribute("Orleans.Serialization")>]
    do ()

module Program =

    [<InternalsVisibleTo("Host")>]
    do ()

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
        [| Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".env") // during debug
           Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".env") |] // during debug

    for envPath in envPaths do
        let path = Path.GetFullPath(envPath)
        logToConsole $"Checking for .env file at {path}."

        if File.Exists(path) then
            logToConsole $"Loading environment variables from {path}."
            DotEnv.Load(DotEnvOptions(envFilePaths = [| path |], ignoreExceptions = true))

    let createHostBuilder (args: string[]) : IHostBuilder =
        let builder = Host.CreateDefaultBuilder(args)
        let storageEndpoints = AzureEnvironment.storageEndpoints

        let azureStorageConnectionString =
            match storageEndpoints.ConnectionString with
            | Some value -> value
            | None -> Environment.GetEnvironmentVariable EnvironmentVariables.AzureStorageConnectionString

        let azureCosmosDBConnectionString =
            match AzureEnvironment.cosmosConnectionString with
            | Some value -> value
            | None -> Environment.GetEnvironmentVariable EnvironmentVariables.AzureCosmosDBConnectionString

        let useManagedIdentity = AzureEnvironment.useManagedIdentity

        builder
            .UseContentRoot(Directory.GetCurrentDirectory())
            .UseOrleans(fun siloBuilder ->
                siloBuilder
                    .Configure<ClusterOptions>(fun (options: ClusterOptions) ->
                        options.ClusterId <- Environment.GetEnvironmentVariable EnvironmentVariables.OrleansClusterId
                        options.ServiceId <- Environment.GetEnvironmentVariable EnvironmentVariables.OrleansServiceId)
                    .Configure<SiloOptions>(fun (options: SiloOptions) ->
                        options.SiloName <- $"Silo-{Environment.GetEnvironmentVariable EnvironmentVariables.OrleansServiceId}")
                    .Configure<MessagingOptions>(fun (options: MessagingOptions) -> options.ResponseTimeout <- TimeSpan.FromSeconds(60.0))
                    .Configure<GrainCollectionOptions>(fun (options: GrainCollectionOptions) ->
                        options.CollectionAge <- TimeSpan.FromMinutes(15.0)
                        options.ClassSpecificCollectionAge[$"{(typeof<GrainRepository.GrainRepositoryActor>).FullName}"] <- TimeSpan.FromMinutes(5.0))
                    .UseAzureStorageClustering(fun (options: AzureStorageClusteringOptions) ->
                        let tableServiceClient =
                            if useManagedIdentity then
                                TableServiceClient(storageEndpoints.TableEndpoint, defaultAzureCredential.Value)
                            else if String.IsNullOrWhiteSpace azureStorageConnectionString then
                                invalidOp "Azure Storage connection string must be configured for clustering when managed identity is disabled."
                            else
                                TableServiceClient(azureStorageConnectionString)

                        options.TableServiceClient <- tableServiceClient)
                    .AddCosmosGrainStorage(
                        GraceActorStorage,
                        (fun (options: CosmosGrainStorageOptions) ->
                            //options.ConfigureCosmosClient(azureCosmosDBConnectionString)
                            options.ContainerName <- Environment.GetEnvironmentVariable EnvironmentVariables.AzureCosmosDBContainerName
                            options.DatabaseName <- Environment.GetEnvironmentVariable EnvironmentVariables.AzureCosmosDBDatabaseName
                            options.IsResourceCreationEnabled <- false

                            options.ConfigureCosmosClient(fun (serviceProvider: IServiceProvider) ->
                                let cosmosClientOptions = CosmosClientOptions()
                                cosmosClientOptions.ApplicationName <- "Grace.Server"
                                cosmosClientOptions.LimitToEndpoint <- false
                                cosmosClientOptions.UseSystemTextJsonSerializerWithOptions <- Grace.Shared.Constants.JsonSerializerOptions
#if DEBUG
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
#endif
                                // When debugging, the Cosmos DB emulator takes a while to start up.
                                // We're going to use a loop to wait until it's ready.
                                let cosmosClient =
                                    if useManagedIdentity then
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
                            options.ContainerName <- Environment.GetEnvironmentVariable EnvironmentVariables.DiffContainerName
                            options.GrainStorageSerializer <- SystemTextJsonGrainStorageSerializer(Grace.Shared.Constants.JsonSerializerOptions))
                    )
                    .AddActivityPropagation()

                |> ignore

                siloBuilder.AddMemoryGrainStorage(GraceInMemoryStorage) |> ignore

                siloBuilder.Services.AddSerializer(fun serializerBuilder ->
                    serializerBuilder.AddJsonSerializer(
                        isSupported =
                            (fun t ->
                                not <| String.IsNullOrEmpty(t.Namespace)
                                && t.Namespace.StartsWith("Grace", StringComparison.InvariantCulture)),
                        jsonSerializerOptions = Grace.Shared.Constants.JsonSerializerOptions
                    )
                    |> ignore)
                |> ignore)
            .ConfigureLogging(fun logConfig ->
                logConfig
                    .SetMinimumLevel(LogLevel.Debug)
                    .AddFilter("Orleans", LogLevel.Information)
                    .AddFilter("Orleans.Streams", LogLevel.Debug)
                    .AddFilter("Orleans.Providers", LogLevel.Debug)
                |> ignore

                logConfig.AddOpenTelemetry(fun openTelemetryOptions -> openTelemetryOptions.IncludeScopes <- true)
                |> ignore)
            .ConfigureWebHostDefaults(fun webBuilder ->
                webBuilder
                    .UseStartup<Application.Startup>()
                    .UseKestrel(fun kestrelServerOptions ->
                        kestrelServerOptions.ConfigureEndpointDefaults(fun listenOptions -> listenOptions.Protocols <- HttpProtocols.Http1AndHttp2)

                        kestrelServerOptions.ConfigureHttpsDefaults(fun options ->
                            options.SslProtocols <- SslProtocols.Tls12 ||| SslProtocols.Tls13
#if DEBUG
                            options.AllowAnyClientCertificate()
#endif
                        ))
                |> ignore)

    [<EntryPoint>]
    let main args =
        try
            logToConsole "----------------------------- Starting Grace Server ------------------------------"

            // Build the configuration
            let environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")

            let configuration =
                ConfigurationBuilder()
                    .AddJsonFile("appsettings.json", true, true) // Load appsettings.json
                    .AddJsonFile($"appsettings.{environment}.json", false, true) // Load environment-specific settings
                    .AddEnvironmentVariables()
                    .AddUserSecrets() // Use `dotnet user-secrets` to store sensitive settings during development
                    .Build()

            use configurationEntry = memoryCache.CreateEntry(MemoryCache.GraceConfiguration)
            configurationEntry.Value <- configuration
            configurationEntry.Priority <- CacheItemPriority.NeverRemove
            configurationEntry.Dispose()

            logToConsole "Configuration settings saved in memory cache."

            let host = createHostBuilder(args).Build()

            // Placing some much-used services into ApplicationContext where they're easy to find.
            Grace.Actors.Context.setHostServiceProvider host.Services

            let grainFactory = host.Services.GetService(typeof<IGrainFactory>) :?> IGrainFactory
            ApplicationContext.setGrainFactory grainFactory

            let loggerFactory = host.Services.GetService(typeof<ILoggerFactory>) :?> ILoggerFactory
            ApplicationContext.setLoggerFactory (loggerFactory)

            // Dump out configuration settings at startup for debugging purposes.
            //logToConsole "Configuration settings:"
            //for pair in config.AsEnumerable() do
            //    logToConsole $"  {pair.Key} = {pair.Value}"

            host.Run()

            0 // Return an integer exit code
        with ex ->
            logToConsole $"Fatal error starting Grace Server.{Environment.NewLine}{ExceptionResponse.Create ex}"
            -1
