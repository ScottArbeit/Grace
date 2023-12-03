namespace Grace.Actors

open Dapr.Actors
open Dapr.Actors.Runtime
open Grace.Actors.Constants
open Grace.Actors.Interfaces
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
    
        let log = host.LoggerFactory.CreateLogger("BranchName.Actor")

        let mutable cachedBranchId: string option = None

        override this.OnPreActorMethodAsync(context) =
            actorStartTime <- getCurrentInstant()
            logScope <- log.BeginScope("Actor {actorName}", actorName)
            log.LogTrace("{CurrentInstant}: Started {ActorName}.{MethodName} Id: {Id}.", getCurrentInstantExtended(), actorName, context.MethodName, this.Id)
            Task.CompletedTask

        override this.OnPostActorMethodAsync(context) =
            let duration_ms = (getCurrentInstant().Minus(actorStartTime).TotalMilliseconds * 1000.0).ToString("F0")
            log.LogInformation("{CurrentInstant}: Finished {ActorName}.{MethodName}; Id: {Id}; Duration: {duration}ms.", getCurrentInstantExtended(), actorName, context.MethodName, this.Id, duration_ms)
            logScope.Dispose()
            Task.CompletedTask

        interface IBranchNameActor with
            member this.GetBranchId() = Task.FromResult(cachedBranchId)

            member this.SetBranchId(branchId: string) =
                let mutable guid = Guid.Empty
                if Guid.TryParse(branchId, &guid) && guid <> Guid.Empty then
                    cachedBranchId <- Some branchId
                Task.CompletedTask
