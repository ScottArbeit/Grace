namespace Grace.Server

open Grace.Actors.Constants
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Services
open Grace.Actors.Types
open Grace.Server.ApplicationContext
open Grace.Shared
open Grace.Types.Reminder
open Grace.Types.Types
open Grace.Shared.Utilities
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Collections.Generic
open System.Linq
open System.Threading
open System.Threading.Tasks
open System.Text
open Microsoft.Azure.Cosmos
open Grace.Shared.Constants
open System.Net

module ReminderService =

    type ReminderValue() =
        member val Id = String.Empty with get, set
        member val PartitionKey = String.Empty with get, set
        member val ReminderId = ReminderId.Empty with get, set
        member val CorrelationId: CorrelationId = String.Empty with get, set
        override this.ToString() = serialize this

    type ReminderService() =
        inherit BackgroundService()

        let defaultReminderBatchSize = 5000
        let timer = TimeSpan.FromSeconds(60.0)
        let log = loggerFactory.CreateLogger("ReminderService.Server")

        let reminderBatchSize =
            let mutable reminderBatchSize = 0
            let enviromentValue = Configuration().Item(EnvironmentVariables.GraceReminderBatchSize)

            if Int32.TryParse(enviromentValue, &reminderBatchSize) then
                reminderBatchSize
            else
                defaultReminderBatchSize

        /// Retrieves reminders from storage.
        let retrieveReminders (cancellationToken: CancellationToken) =
            task {
                let reminders = List<ReminderValue>(reminderBatchSize)

                match actorStateStorageProvider with
                | Unknown -> ()
                | AzureCosmosDb ->
                    let queryDefinition =
                        QueryDefinition(
                            """
                            SELECT TOP @maxCount c.id as Id, c.State.Reminder.ReminderId AS ReminderId, c.State.Reminder.CorrelationId AS CorrelationId
                            FROM c
                            WHERE c.GrainType = @grainType
                                AND c.PartitionKey = @partitionKey
                                AND c.State.Reminder.ReminderTime < GetCurrentDateTime()
                                ORDER BY c._ts ASC
                            """
                        )
                            .WithParameter("@maxCount", reminderBatchSize)
                            .WithParameter("@grainType", StateName.Reminder)
                            .WithParameter("@partitionKey", StateName.Reminder)

                    //let queryText =
                    //    """SELECT TOP @maxCount c.id as Id, c.partitionKey as PartitionKey, c["value"].ReminderId, c["value"].CorrelationId FROM c
                    //        WHERE c["value"].Class = @class
                    //        AND c["value"].ReminderTime < GetCurrentDateTime()
                    //        ORDER BY c["value"].ReminderTime ASC"""

                    //let queryDefinition =
                    //    QueryDefinition(queryText)
                    //        .WithParameter("@maxCount", reminderBatchSize)
                    //        .WithParameter("@class", nameof ReminderDto)

                    use iterator = ApplicationContext.cosmosContainer.GetItemQueryIterator<ReminderValue>(queryDefinition)

                    //log.LogInformation(
                    //    "{CurrentInstant}: Node: {HostName}; In ReminderService.retrieveReminders. Created iterator.",
                    //    getCurrentInstantExtended (),
                    //    getMachineName
                    //)

                    while iterator.HasMoreResults do
                        let! results = iterator.ReadNextAsync(cancellationToken)
                        //logToConsole $"*******Reminders retrieved: {results.Resource.Count()}."

                        for reminder in results do
                            reminders.Add(reminder)

                //log.LogInformation(
                //    "{CurrentInstant}: Node: {HostName}; In ReminderService.retrieveReminders. After calling Cosmos DB.",
                //    getCurrentInstantExtended (),
                //    getMachineName
                //)

                | MongoDB -> ()

                return reminders :> IReadOnlyList<ReminderValue>
            }

        /// Processes reminders by:
        ///   1. retrieving them,
        ///   2. calling .Remind() on each one to send the reminder to the source actor, and
        ///   3. deleting the reminder from storage.
        let processReminders (cancellationToken: CancellationToken) =
            task {
                let start = getCurrentInstant ()

                try
                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; In ReminderService.ProcessReminders. Retrieving reminders.",
                        getCurrentInstantExtended (),
                        getMachineName
                    )

                    let! reminders = retrieveReminders cancellationToken

                    if reminders.Count >= 0 then
                        log.LogInformation(
                            "{CurrentInstant}: Node: {HostName}; In ReminderService.ProcessReminders. Processing {reminderCount} reminder(s).",
                            getCurrentInstantExtended (),
                            getMachineName,
                            reminders.Count
                        )

                    do!
                        Parallel.ForEachAsync(
                            reminders,
                            ParallelOptions,
                            (fun reminder ct ->
                                ValueTask(
                                    task {
                                        try
                                            let reminderActorProxy = Reminder.CreateActorProxy reminder.ReminderId reminder.CorrelationId

                                            match! reminderActorProxy.Remind reminder.CorrelationId with
                                            | Ok() ->
                                                let itemRequestOptions =
                                                    ItemRequestOptions(
                                                        AddRequestHeaders =
                                                            fun headers -> headers.Add(Constants.CorrelationIdHeaderKey, reminder.CorrelationId)
                                                    )

                                                // Delete the reminder from storage to avoid reprocessing.
                                                let! deleteReminderResponse =
                                                    cosmosContainer.DeleteItemAsync(reminder.Id, PartitionKey(StateName.Reminder), itemRequestOptions)

                                                if deleteReminderResponse.StatusCode <> HttpStatusCode.NoContent then
                                                    log.LogError(
                                                        "{CurrentInstant}: Node: {HostName}; Error deleting reminder: {reminder.id}. Status code: {deleteResponse.StatusCode}.",
                                                        getCurrentInstantExtended (),
                                                        getMachineName,
                                                        reminder.Id,
                                                        deleteReminderResponse.StatusCode
                                                    )
                                            | Error error ->
                                                log.LogError(
                                                    "{CurrentInstant}: Node: {HostName}; Error processing reminder: {reminder.id}. {error}.",
                                                    getCurrentInstantExtended (),
                                                    getMachineName,
                                                    reminder.Id,
                                                    error
                                                )
                                        with ex ->
                                            log.LogError(
                                                "{CurrentInstant}: Node: {HostName}; Error processing reminder. Reminder: {Reminder}. Error: {error}.",
                                                getCurrentInstantExtended (),
                                                getMachineName,
                                                reminder,
                                                (ExceptionResponse.Create ex)
                                            )
                                    }
                                    :> Task
                                ))
                        )
                with ex ->
                    log.LogError(
                        "{CurrentInstant}: Node: {HostName}; Error processing reminder. Error: {error}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        (ExceptionResponse.Create ex)
                    )

            }
            :> Task

        override this.StartAsync(cancellationToken: CancellationToken) =
            log.LogInformation("{CurrentInstant}: Node: {HostName}; ReminderService is starting.", getCurrentInstantExtended (), getMachineName)

            log.LogInformation(
                "{CurrentInstant}: Node: {HostName}; Reminder batch size set to {reminderBatchSize}.",
                getCurrentInstantExtended (),
                getMachineName,
                reminderBatchSize
            )

            base.StartAsync(cancellationToken)

        override this.ExecuteAsync(stoppingToken: CancellationToken) =
            task {
                try
                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; In ReminderService.ExecuteAsync. Pausing for {DelaySeconds} seconds before processing reminders.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        secondsToWaitForDaprToBeReady
                    )

                    // Initial delay before processing reminders; I'm using the same delay time we use to wait for Dapr to be ready, because
                    //   Grace.Server will shut down if Dapr isn't ready before this delay finishes.
                    do! Task.Delay(TimeSpan.FromSeconds(secondsToWaitForDaprToBeReady), stoppingToken)

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; In ReminderService.ExecuteAsync. Starting reminder timer; checking for reminders every {ReminderTimer} seconds.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        timer.TotalSeconds
                    )

                    use periodicTimer = new PeriodicTimer(timer)
                    let mutable ticked = true
                    let globalLockActorProxy = GlobalLock.CreateActorProxy LockName.ReminderLock (generateCorrelationId ())

                    while ticked && not (stoppingToken.IsCancellationRequested) do
                        let! locked = globalLockActorProxy.AcquireLock(getMachineName)

                        if locked then
                            do! processReminders (stoppingToken)

                            match! globalLockActorProxy.ReleaseLock(getMachineName) with
                            | Ok() -> ()
                            | Error error ->
                                log.LogError(
                                    "{CurrentInstant}: Node: {HostName}; Error releasing reminder lock: {error}.",
                                    getCurrentInstantExtended (),
                                    getMachineName,
                                    error
                                )

                            let! tick = periodicTimer.WaitForNextTickAsync(stoppingToken)
                            ticked <- tick
                        else
                            do! Task.Delay(TimeSpan.FromSeconds(1.0), stoppingToken)
                with ex ->
                    log.LogError(
                        "{CurrentInstant}: Node: {HostName}; Error in ReminderService.ExecuteAsync. Error: {error}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        (ExceptionResponse.Create ex)
                    )
            }
            :> Task

        override this.StopAsync(cancellationToken: CancellationToken) =
            log.LogInformation("{CurrentInstant}: Node: {HostName}; ReminderService is stopping.", getCurrentInstantExtended (), getMachineName)
            base.StopAsync(cancellationToken)
