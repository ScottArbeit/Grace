namespace Grace.Actors

open Dapr.Actors
open Dapr.Actors.Runtime
open Grace.Actors.Constants
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared.Types
open Grace.Shared.Utilities
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Threading.Tasks

module BranchName =

    let mutable actorStartTime = Instant.MinValue
    let mutable logScope: IDisposable = null

    type BranchNameActor(host: ActorHost) =
        inherit Actor(host)

        let actorName = Constants.ActorName.BranchName
    
        let log = loggerFactory.CreateLogger("BranchName.Actor")

        let mutable cachedBranchId: Guid option = None

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnPreActorMethodAsync(context) =
            this.correlationId <- String.Empty
            actorStartTime <- getCurrentInstant()
            logScope <- log.BeginScope("Actor {actorName}", actorName)
            log.LogTrace("{CurrentInstant}: Started {ActorName}.{MethodName} Id: {Id}.", getCurrentInstantExtended(), actorName, context.MethodName, this.Id)
            Task.CompletedTask

        override this.OnPostActorMethodAsync(context) =
            let duration_ms = (getCurrentInstant().Minus(actorStartTime).TotalMilliseconds).ToString("F3")
            log.LogInformation("{CurrentInstant}: Finished {ActorName}.{MethodName}; Id: {Id}; CorrelationId: {correlationId}; Duration: {duration_ms}ms.", getCurrentInstantExtended(), actorName, context.MethodName, this.Id, this.correlationId, duration_ms)
            logScope.Dispose()
            Task.CompletedTask

        interface IBranchNameActor with
            member this.GetBranchId correlationId = 
                this.correlationId <- correlationId
                Task.FromResult(cachedBranchId)

            member this.SetBranchId branchId correlationId =
                this.correlationId <- correlationId
                cachedBranchId <- Some branchId
                Task.CompletedTask
