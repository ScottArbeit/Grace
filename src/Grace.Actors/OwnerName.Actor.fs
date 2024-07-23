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
            log.LogInformation(
                "{CurrentInstant}: Duration:   0.100ms; Activated {ActorType} {ActorId}.",
                getCurrentInstantExtended (),
                this.GetType().Name,
                host.Id
            )

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
                "{currentInstant}: Node: {hostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {ActorName}.{MethodName}; OwnerName: {OwnerName}; OwnerId: {ownerId}.",
                getCurrentInstantExtended (),
                getMachineName,
                duration_ms,
                this.correlationId,
                actorName,
                context.MethodName,
                this.Id,
                (if Option.isSome cachedOwnerId then $"{cachedOwnerId.Value}" else "None")
            )

            logScope.Dispose()
            Task.CompletedTask

        interface IOwnerNameActor with
            member this.ClearOwnerId correlationId =
                this.correlationId <- correlationId
                cachedOwnerId <- None

                Task.CompletedTask

            member this.GetOwnerId(correlationId) =
                this.correlationId <- correlationId
                cachedOwnerId |> returnTask

            member this.SetOwnerId (ownerId: OwnerId) correlationId =
                this.correlationId <- correlationId
                cachedOwnerId <- Some ownerId

                Task.CompletedTask
