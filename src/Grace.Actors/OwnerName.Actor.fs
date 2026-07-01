namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Extensions.MemoryCache
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Types.Common
open Grace.Shared.Utilities
open Microsoft.Extensions.Logging
open NodaTime
open Orleans
open Orleans.Runtime
open System
open System.Threading.Tasks

/// Groups Orleans actor helpers for owner name keys, proxies, state, or workflow transitions.
module OwnerName =

    /// Implements the Orleans grain for owner name actor.
    type OwnerNameActor(log: ILogger<OwnerNameActor>) =
        inherit Grain()

        static let actorName = ActorName.OwnerName

        let log = loggerFactory.CreateLogger("OwnerName.Actor")

        let mutable cachedOwnerId: OwnerId option = None

        /// Stores the correlation id used by this actor while reporting timings and errors.
        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            logActorActivation log this.IdentityString activateStartTime "In-memory only"

            Task.CompletedTask

        interface IOwnerNameActor with
            /// Removes or invalidates clear owner id data from the OwnerName actor state.
            member this.ClearOwnerId correlationId =
                this.correlationId <- correlationId
                cachedOwnerId <- None

                Task.CompletedTask

            /// Returns owner id data from the OwnerName actor state or related storage.
            member this.GetOwnerId(correlationId) =
                this.correlationId <- correlationId
                cachedOwnerId |> returnTask

            /// Stores set owner id data in the OwnerName actor state.
            member this.SetOwnerId (ownerId: OwnerId) correlationId =
                this.correlationId <- correlationId
                cachedOwnerId <- Some ownerId

                Task.CompletedTask
