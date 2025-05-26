namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Extensions.MemoryCache
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared.Types
open Grace.Shared.Utilities
open Microsoft.Extensions.Logging
open NodaTime
open Orleans
open Orleans.Runtime
open System
open System.Threading.Tasks

module OwnerName =

    type OwnerNameActor(log: ILogger<OwnerNameActor>) =
        inherit Grain()

        static let actorName = ActorName.OwnerName

        let mutable cachedOwnerId: OwnerId option = None

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            logActorActivation log this.IdentityString "In-memory only"

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
