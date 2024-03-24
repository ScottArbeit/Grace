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

    let actorName = ActorName.BranchName
    let mutable actorStartTime = Instant.MinValue
    let mutable logScope: IDisposable = null

    type BranchNameActor(host: ActorHost) =
        inherit Actor(host)

        let idSections = host.Id.GetId().Split('|')
        let branchName = idSections[0]
        let repositoryId = idSections[1]
    
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
            log.LogInformation("{CurrentInstant}: CorrelationID: {correlationID}; Finished {ActorName}.{MethodName}; RepositoryId: {RepositoryId}; BranchName: {BranchName}; BranchId: {BranchId}; Duration: {duration_ms}ms.", 
                getCurrentInstantExtended(), this.correlationId, actorName, context.MethodName, repositoryId, branchName, (if Option.isSome cachedBranchId then $"{cachedBranchId.Value}" else "None"), duration_ms)
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
