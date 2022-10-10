namespace Grace.Actors

open Dapr.Actors
open Dapr.Actors.Runtime
open System
open System.Threading.Tasks

module BranchName =

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

        let mutable cachedBranchId: string option = None

        interface IBranchNameActor with
            member this.GetBranchId() = Task.FromResult(cachedBranchId)

            member this.SetBranchId(branchId: string) =
                let mutable guid = Guid.Empty
                if Guid.TryParse(branchId, &guid) && guid <> Guid.Empty then
                    cachedBranchId <- Some branchId
                Task.CompletedTask
