namespace Grace.Actors

open Dapr.Actors
open Dapr.Actors.Runtime
open Grace.Actors.Constants
open System
open System.Threading.Tasks

module OwnerName =

    let GetActorId (ownerName: string) = ActorId(ownerName)

    type IOwnerNameActor =
        inherit IActor
        /// <summary>
        /// Sets the OwnerId for a given OwnerName.
        /// </summary>
        abstract member SetOwnerId: ownerName: string -> Task

        /// <summary>
        /// Returns the OwnerId for the given OwnerName.
        /// </summary>
        abstract member GetOwnerId: unit -> Task<String option>

    type OwnerNameActor(host: ActorHost) =
        inherit Actor(host)

        let log = host.LoggerFactory.CreateLogger(ActorName.OwnerName)

        let mutable cachedOwnerId: string option = None

        interface IOwnerNameActor with
            member this.GetOwnerId() = Task.FromResult(cachedOwnerId)

            member this.SetOwnerId(ownerId: string) =
                let mutable guid = Guid.Empty
                if Guid.TryParse(ownerId, &guid) && guid <> Guid.Empty then
                    cachedOwnerId <- Some ownerId
                Task.CompletedTask
