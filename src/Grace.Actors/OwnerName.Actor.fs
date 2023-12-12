namespace Grace.Actors

open Dapr.Actors
open Dapr.Actors.Runtime
open Grace.Actors.Constants
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared.Utilities
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Threading.Tasks

module OwnerName =

    let actorName = Constants.ActorName.OwnerName
    let mutable actorStartTime = Instant.MinValue
    let mutable logScope: IDisposable = null
    
    let GetActorId (ownerName: string) = ActorId(ownerName)

    type OwnerNameActor(host: ActorHost) =
        inherit Actor(host)

        let actorName = ActorName.OwnerName
        let log = loggerFactory.CreateLogger("OwnerName.Actor")

        let mutable cachedOwnerId: string option = None

        override this.OnPreActorMethodAsync(context) =
            actorStartTime <- getCurrentInstant()
            logScope <- log.BeginScope("Actor {actorName}", actorName)
            log.LogTrace("{CurrentInstant}: Started {ActorName}.{MethodName} Id: {Id}.", getCurrentInstantExtended(), actorName, context.MethodName, this.Id)
            Task.CompletedTask

        override this.OnPostActorMethodAsync(context) =
            let duration_ms = (getCurrentInstant().Minus(actorStartTime).TotalMilliseconds).ToString("F3")
            log.LogInformation("{CurrentInstant}: Finished {ActorName}.{MethodName}; OwnerName: {OwnerName}; OwnerId: {ownerId}; Duration: {duration_ms}ms.", 
                getCurrentInstantExtended(), actorName, context.MethodName, this.Id, (if Option.isSome cachedOwnerId then cachedOwnerId.Value else "None"), duration_ms)
            logScope.Dispose()
            Task.CompletedTask

        interface IOwnerNameActor with
            member this.GetOwnerId() = cachedOwnerId |> returnTask

            member this.SetOwnerId(ownerId: string) =
                let mutable guid = Guid.Empty
                if Guid.TryParse(ownerId, &guid) && guid <> Guid.Empty then
                    cachedOwnerId <- Some ownerId
                Task.CompletedTask
