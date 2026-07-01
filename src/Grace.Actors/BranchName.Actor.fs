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
open OrganizationName

/// Groups Orleans actor helpers for branch name keys, proxies, state, or workflow transitions.
module BranchName =

    //let log = loggerFactory.CreateLogger("BranchName.Actor")

    /// Implements the Orleans grain for branch name actor.
    type BranchNameActor() =
        inherit Grain()

        static let actorName = ActorName.BranchName

        let log = loggerFactory.CreateLogger("BranchName.Actor")

        let mutable cachedBranchId: Guid option = None

        /// Stores the correlation id used by this actor while reporting timings and errors.
        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            logActorActivation log this.IdentityString activateStartTime "In-memory only"

            Task.CompletedTask

        interface IBranchNameActor with
            /// Returns the branch id mapped to this repository-local branch name.
            member this.GetBranchId correlationId =
                this.correlationId <- correlationId
                Task.FromResult(cachedBranchId)

            /// Stores the branch id mapped to this repository-local branch name.
            member this.SetBranchId branchId correlationId =
                this.correlationId <- correlationId
                cachedBranchId <- Some branchId
                Task.CompletedTask
