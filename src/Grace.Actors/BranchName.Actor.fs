namespace Grace.Actors

open Dapr.Actors
open Dapr.Actors.Runtime
open Grace.Actors.Constants
open Grace.Shared.Utilities
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Threading.Tasks

module BranchName =

    let actorName = Constants.ActorName.BranchName
    let mutable actorStartTime = Instant.MinValue
    let mutable logScope: IDisposable = null

    let GetActorId repositoryId branchName = ActorId($"{repositoryId}-{branchName}")

    type IBranchNameActor =
        inherit IActor
        /// <summary>
        /// Sets the BranchId that matches the BranchName.
        /// </summary>
        abstract member SetBranchId: branchName: string -> Task

        /// <summary>
        /// Returns the BranchId for the given BranchName.
        /// </summary>
        abstract member GetBranchId: unit -> Task<String option>

    type BranchNameActor(host: ActorHost) =
        inherit Actor(host)

        let log = host.LoggerFactory.CreateLogger(actorName)

        let mutable cachedBranchId: string option = None

        override this.OnPreActorMethodAsync(context) =
            actorStartTime <- getCurrentInstant()
            logScope <- log.BeginScope("Actor {actorName}", actorName)
            //log.LogInformation("{CurrentInstant}: Started {ActorName}.{MethodName} Id: {Id}.", getCurrentInstantExtended(), actorName, context.MethodName, this.Id.GetId())
            Task.CompletedTask

        override this.OnPostActorMethodAsync(context) =
            let duration = getCurrentInstant().Minus(actorStartTime)
            log.LogInformation("{CurrentInstant}: Finished {ActorName}.{MethodName} Id: {Id}; Duration: {duration}ms.", getCurrentInstantExtended(), actorName, context.MethodName, this.Id.GetId(), duration.TotalMilliseconds.ToString("F3"))
            logScope.Dispose()
            Task.CompletedTask

        interface IBranchNameActor with
            member this.GetBranchId() = Task.FromResult(cachedBranchId)

            member this.SetBranchId(branchId: string) =
                let mutable guid = Guid.Empty
                if Guid.TryParse(branchId, &guid) && guid <> Guid.Empty then
                    cachedBranchId <- Some branchId
                Task.CompletedTask
