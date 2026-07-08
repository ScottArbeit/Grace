namespace Grace.Operations.Worker

open Grace.Operations.Data
open Azure.Core
open Azure.Identity
open Azure.Storage.Blobs
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Diagnostics.HealthChecks
open Microsoft.Extensions.Hosting
open NodaTime
open System

/// Starts the Grace operations worker host.
module Program =

    /// Configures and runs the operational usage ingestion worker process.
    [<EntryPoint>]
    let main args =
        try
            Host
                .CreateDefaultBuilder(args)
                .ConfigureServices(fun context services ->
                    let settings =
                        match OperationsWorkerSettings.fromConfiguration context.Configuration with
                        | Ok value -> value
                        | Error errors -> invalidOp (String.Join("; ", errors))

                    let archiveSettings =
                        match OperationsUsageArchiveSettings.tryFromConfiguration context.Configuration with
                        | Ok value -> value
                        | Error errors -> invalidOp (String.Join("; ", errors))

                    services.AddSingleton(settings) |> ignore

                    services.AddSingleton(OperationsUsageSchema(settings.SqlConnectionString, settings.SchemaBootstrapMode))
                    |> ignore

                    services.AddSingleton<IOperationsUsageSchemaInitializer> (fun serviceProvider ->
                        OperationsUsageSchemaInitializationBarrier(serviceProvider.GetRequiredService<OperationsUsageSchema>())
                        :> IOperationsUsageSchemaInitializer)
                    |> ignore

                    services.AddSingleton<OperationsUsageReadinessState>()
                    |> ignore

                    services.AddSingleton<IOperationsUsageReadinessProbe> (fun serviceProvider ->
                        serviceProvider.GetRequiredService<OperationsUsageReadinessState>() :> IOperationsUsageReadinessProbe)
                    |> ignore

                    services.AddSingleton<IOperationsUsageReadinessRecorder> (fun serviceProvider ->
                        serviceProvider.GetRequiredService<OperationsUsageReadinessState>() :> IOperationsUsageReadinessRecorder)
                    |> ignore

                    services
                        .AddHealthChecks()
                        .AddCheck<OperationsUsageReadinessHealthCheck>("operations-usage-ingestion")
                    |> ignore

                    services.Configure<HealthCheckPublisherOptions> (fun (options: HealthCheckPublisherOptions) ->
                        options.Delay <- TimeSpan.Zero
                        options.Period <- TimeSpan.FromSeconds(30.0))
                    |> ignore

                    services.AddSingleton<IHealthCheckPublisher, OperationsUsageReadinessHealthCheckPublisher>()
                    |> ignore

                    services.AddSingleton<IOperationsUsageFactStore> (fun _ ->
                        let transactionScope = SqlOperationsUsageTransactionScope(settings.SqlConnectionString)
                        let store = OperationsUsageStore transactionScope
                        OperationsUsageFactStoreAdapter(store) :> IOperationsUsageFactStore)
                    |> ignore

                    services.AddSingleton<OperationsUsageIngestionProcessor>()
                    |> ignore

                    services.AddHostedService<OperationsUsageWorkerService>()
                    |> ignore

                    match archiveSettings with
                    | Some archiveSettings ->
                        services.AddSingleton(archiveSettings) |> ignore

                        services.AddSingleton<IOperationsUsageArchiveStore> (fun _ ->
                            SqlOperationsUsageArchiveStore(settings.SqlConnectionString) :> IOperationsUsageArchiveStore)
                        |> ignore

                        services.AddSingleton<IOperationsUsageArchiveBlobStore> (fun _ ->
                            let containerClient =
                                match archiveSettings.BlobConnectionString, archiveSettings.BlobServiceUri with
                                | Some connectionString, _ -> BlobContainerClient(connectionString, archiveSettings.BlobContainerName)
                                | None, Some serviceUri ->
                                    BlobServiceClient(Uri serviceUri, DefaultAzureCredential() :> TokenCredential)
                                        .GetBlobContainerClient(archiveSettings.BlobContainerName)
                                | None, None -> invalidOp "Operations archive Blob storage must be configured."

                            AzureOperationsUsageArchiveBlobStore(containerClient) :> IOperationsUsageArchiveBlobStore)
                        |> ignore

                        services.AddSingleton<OperationsUsageArchiveProcessor>()
                        |> ignore

                        services.AddSingleton<OperationsUsageTemporaryHotCleanupProcessor> (fun serviceProvider ->
                            OperationsUsageTemporaryHotCleanupProcessor(
                                serviceProvider.GetRequiredService<IOperationsUsageArchiveStore>(),
                                SystemClock.Instance,
                                serviceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<OperationsUsageTemporaryHotCleanupProcessor>>()
                            ))
                        |> ignore

                        services.AddHostedService<OperationsUsageArchiveWorkerService>()
                        |> ignore

                        services.AddHostedService<OperationsUsageTemporaryHotCleanupWorkerService>()
                        |> ignore
                    | None -> ())
                .Build()
                .Run()

            0
        with
        | ex ->
            Console.Error.WriteLine($"Grace operations worker failed to start: {ex.Message}")
            1
