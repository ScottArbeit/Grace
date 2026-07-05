namespace Grace.Operations.Worker

open Grace.Operations.Data
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
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

                    services.AddSingleton(settings) |> ignore

                    services.AddSingleton(OperationsUsageSchema(settings.SqlConnectionString, settings.SchemaBootstrapMode))
                    |> ignore

                    services.AddSingleton<IOperationsUsageFactStore> (fun _ ->
                        let transactionScope = SqlOperationsUsageTransactionScope(settings.SqlConnectionString)
                        let store = OperationsUsageStore transactionScope
                        OperationsUsageFactStoreAdapter(store) :> IOperationsUsageFactStore)
                    |> ignore

                    services.AddSingleton<OperationsUsageIngestionProcessor>()
                    |> ignore

                    services.AddHostedService<OperationsUsageWorkerService>()
                    |> ignore)
                .Build()
                .Run()

            0
        with
        | ex ->
            Console.Error.WriteLine($"Grace operations worker failed to start: {ex.Message}")
            1
