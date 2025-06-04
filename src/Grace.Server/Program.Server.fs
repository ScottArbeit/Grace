namespace Grace.Server

open Azure.Data.Tables
open Grace.Actors.Interfaces
open Grace.Server.ApplicationContext
open Grace.Shared.Constants
open Grace.Types.Types
open Grace.Shared.Utilities
open MessagePack
open Microsoft.AspNetCore
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Server.Kestrel.Core
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Clustering.AzureStorage
open Orleans.Hosting
open Orleans.Configuration
open Orleans.Persistence
open Orleans.Persistence.Cosmos
open Orleans.Runtime
open Orleans.Serialization
open Orleans.Storage
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.IO
open System.Linq
open System.Security.Authentication
open System.Threading.Tasks
open System.Net
open Microsoft.Extensions.Logging
open Grace.Actors
open System.Runtime.CompilerServices

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

    let graceAppPort = Environment.GetEnvironmentVariable("DAPR_APP_PORT") |> int

    let createHostBuilder (args: string[]) : IHostBuilder =
        Host
            .CreateDefaultBuilder(args)
            .UseContentRoot(Directory.GetCurrentDirectory())
            .UseOrleans(fun siloBuilder ->
                siloBuilder
                    .Configure<ClusterOptions>(fun (options: ClusterOptions) ->
                        options.ClusterId <- Environment.GetEnvironmentVariable EnvironmentVariables.OrleansClusterId
                        options.ServiceId <- Environment.GetEnvironmentVariable EnvironmentVariables.OrleansServiceId)
                    .Configure<SiloOptions>(fun (options: SiloOptions) ->
                        options.SiloName <- $"Silo-{Environment.GetEnvironmentVariable EnvironmentVariables.OrleansServiceId}")
                    .Configure<GrainCollectionOptions>(fun (options: GrainCollectionOptions) ->
                        options.CollectionAge <- TimeSpan.FromMinutes(15.0)
                        options.ClassSpecificCollectionAge[$"{(typeof<GrainRepository.GrainRepositoryActor>).FullName}"] <- TimeSpan.FromMinutes(5.0))
                    .UseAzureStorageClustering(fun (options: AzureStorageClusteringOptions) ->
                        let tableServiceClient = TableServiceClient(Environment.GetEnvironmentVariable EnvironmentVariables.AzureStorageConnectionString)
                        options.TableServiceClient <- tableServiceClient)
                    .AddCosmosGrainStorage(
                        GraceActorStorage,
                        (fun (options: CosmosGrainStorageOptions) ->
                            options.ConfigureCosmosClient(Environment.GetEnvironmentVariable EnvironmentVariables.AzureCosmosDBConnectionString)
                            options.ContainerName <- Environment.GetEnvironmentVariable EnvironmentVariables.CosmosContainerName
                            options.DatabaseName <- Environment.GetEnvironmentVariable EnvironmentVariables.CosmosDatabaseName
                            options.IsResourceCreationEnabled <- true
                            options.ClientOptions.ApplicationName <- EnvironmentVariables.DaprAppId
                            options.ClientOptions.UseSystemTextJsonSerializerWithOptions <- JsonSerializerOptions)
                    )
                    .AddAzureBlobGrainStorage(
                        GraceObjectStorage,
                        (fun (options: AzureBlobStorageOptions) ->
                            options.BlobServiceClient <- Context.blobServiceClient
                            options.ContainerName <- Environment.GetEnvironmentVariable EnvironmentVariables.DiffContainerName)
                    )
                |> ignore

                siloBuilder.AddMemoryGrainStorage(GraceInMemoryStorage) |> ignore

                siloBuilder.Services.AddSerializer(fun serializerBuilder ->
                    serializerBuilder.AddJsonSerializer(
                        isSupported =
                            (fun t ->
                                not <| String.IsNullOrEmpty(t.Namespace)
                                && t.Namespace.StartsWith("Grace", StringComparison.InvariantCulture)),
                        jsonSerializerOptions = JsonSerializerOptions
                    )
                    |> ignore)
                |> ignore)
            .ConfigureLogging(fun logConfig ->
                logConfig.AddOpenTelemetry(fun openTelemetryOptions -> openTelemetryOptions.IncludeScopes <- true)
                |> ignore)
            .ConfigureWebHostDefaults(fun webBuilder ->
                webBuilder
                    .UseStartup<Application.Startup>()
                    .UseKestrel(fun kestrelServerOptions ->
                        kestrelServerOptions.Listen(IPAddress.Any, graceAppPort)
                        //kestrelServerOptions.Limits.MaxConcurrentConnections <- 1000
                        //kestrelServerOptions.Limits.MaxConcurrentUpgradedConnections <- 1000
                        //kestrelServerOptions.Listen(IPAddress.Any, 5001, (fun listenOptions -> listenOptions.UseHttps("/etc/certificates/gracedevcert.pfx", "GraceDevCert") |> ignore))
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
        //let dir = new DirectoryInfo("/etc/certificates")
        //let files = dir.EnumerateFiles()
        //logToConsole $"In main. Contents of {dir.FullName} ({files.Count()} files):"
        //files |> Seq.iter (fun file -> logToConsole $"{file.Name}: {file.Length} bytes")

        logToConsole "-----------------------------------------------------------"
        let host = createHostBuilder(args).Build()

        // Build the configuration
        let environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")

        let config =
            ConfigurationBuilder()
                .AddJsonFile("appsettings.json", true, true) // Load appsettings.json
                .AddJsonFile($"appsettings.{environment}.json", false, true) // Load environment-specific settings
                .AddEnvironmentVariables() // Include environment variables
                .Build()


        // for kvp in config.AsEnumerable().ToImmutableSortedDictionary() do
        //     Console.WriteLine($"{kvp.Key}: {kvp.Value}");

        // Just placing some much-used services into ApplicationContext where they're easy to find.
        Grace.Actors.Context.setHostServiceProvider host.Services
        let grainFactory = host.Services.GetService(typeof<IGrainFactory>) :?> IGrainFactory
        ApplicationContext.setGrainFactory grainFactory

        let loggerFactory = host.Services.GetService(typeof<ILoggerFactory>) :?> ILoggerFactory
        ApplicationContext.setLoggerFactory (loggerFactory)

        host.Run()

        0 // Return an integer exit code
