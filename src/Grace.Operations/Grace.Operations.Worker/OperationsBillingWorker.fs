namespace Grace.Operations.Worker

open Grace.Operations.Data
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open System
open System.Threading
open System.Threading.Tasks

/// Runs owner-scoped billing materialization, lifecycle transitions, close retries, and correction retries on a durable cadence.
type OperationsBillingWorkerService(schema: IOperationsUsageSchemaInitializer, service: IBillingPeriodService, logger: ILogger<OperationsBillingWorkerService>) =
    inherit BackgroundService()

    /// Performs one immediate schema-gated pass then repeats every thirty minutes; persisted state remains the only lifecycle source.
    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            do! schema.EnsureCreatedAsync(stoppingToken)
            use timer = new PeriodicTimer(TimeSpan.FromMinutes(30.0))
            let mutable keepRunning = true

            while keepRunning
                  && not stoppingToken.IsCancellationRequested do
                try
                    do! service.RunAsync(DateTime.UtcNow, stoppingToken)
                with
                | :? OperationCanceledException when stoppingToken.IsCancellationRequested -> keepRunning <- false
                | ex -> logger.LogError(ex, "Operations owner billing pass failed; persisted work will retry on the next 30-minute cadence.")

                if keepRunning then
                    try
                        let! tick = timer.WaitForNextTickAsync(stoppingToken)
                        keepRunning <- tick
                    with
                    | :? OperationCanceledException -> keepRunning <- false
        }
        :> Task
