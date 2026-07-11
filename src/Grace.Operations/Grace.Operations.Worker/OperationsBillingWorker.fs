namespace Grace.Operations.Worker

open Grace.Operations.Data
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open System
open System.Threading
open System.Threading.Tasks

/// Runs durable billing lifecycle and correction work on the approved 30-minute cadence.
type OperationsBillingWorkerService(service: IBillingPeriodService, logger: ILogger<OperationsBillingWorkerService>) =
    inherit BackgroundService()

    /// Executes immediately after startup and then every 30 minutes without process-local lifecycle truth.
    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            use timer = new PeriodicTimer(TimeSpan.FromMinutes 30.0)
            let mutable continueRunning = true

            while continueRunning
                  && not stoppingToken.IsCancellationRequested do
                try
                    do! service.RunAutomaticPassAsync(DateTime.UtcNow, stoppingToken)
                with
                | :? OperationCanceledException when stoppingToken.IsCancellationRequested -> continueRunning <- false
                | ex -> logger.LogError(ex, "Operations billing lifecycle pass failed and will retry on the next 30-minute cadence.")

                if continueRunning then
                    try
                        let! tick = timer.WaitForNextTickAsync(stoppingToken)
                        continueRunning <- tick
                    with
                    | :? OperationCanceledException -> continueRunning <- false
        }
        :> Task
