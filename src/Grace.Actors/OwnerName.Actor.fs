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

module OwnerName =

    let actorName = ActorName.OwnerName
    let mutable actorStartTime = Instant.MinValue
    let mutable logScope: IDisposable = null

    let GetActorId (ownerName: string) = ActorId(ownerName)

    type OwnerNameActor(host: ActorHost) =
        inherit Actor(host)

        let log = loggerFactory.CreateLogger("OwnerName.Actor")

        let mutable cachedOwnerId: OwnerId option = None

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync() =
            log.LogInformation("{CurrentInstant}: Activated {ActorType} {ActorId}.", getCurrentInstantExtended (), this.GetType().Name, host.Id)
            Task.CompletedTask

        override this.OnPreActorMethodAsync(context) =
            actorStartTime <- getCurrentInstant ()
            this.correlationId <- String.Empty
            logScope <- log.BeginScope("Actor {actorName}", actorName)

            log.LogTrace("{CurrentInstant}: Started {ActorName}.{MethodName} Id: {Id}.", getCurrentInstantExtended (), actorName, context.MethodName, this.Id)

            Task.CompletedTask

        override this.OnPostActorMethodAsync(context) =
            let duration_ms = getPaddedDuration_ms actorStartTime

            log.LogInformation(
                "{CurrentInstant}: CorrelationID: {correlationID}; Duration: {duration_ms}ms; Finished {ActorName}.{MethodName}; OwnerName: {OwnerName}; OwnerId: {ownerId}.",
                getCurrentInstantExtended (),
                this.correlationId,
                duration_ms,
                actorName,
                context.MethodName,
                this.Id,
                (if Option.isSome cachedOwnerId then $"{cachedOwnerId.Value}" else "None")
            )

            logScope.Dispose()
            Task.CompletedTask

        interface IOwnerNameActor with
            member this.GetOwnerId(correlationId) =
                this.correlationId <- correlationId
                cachedOwnerId |> returnTask

            member this.SetOwnerId (ownerId: string) correlationId =
                this.correlationId <- correlationId
                let mutable guid = Guid.Empty

                if Guid.TryParse(ownerId, &guid) && guid <> Guid.Empty then
                    cachedOwnerId <- Some guid

                Task.CompletedTask
