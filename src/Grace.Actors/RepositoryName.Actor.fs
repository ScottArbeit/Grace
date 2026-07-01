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

/// Groups Orleans actor helpers for repository name keys, proxies, state, or workflow transitions.
module RepositoryName =

    /// Implements the Orleans grain for repository name actor.
    type RepositoryNameActor() =
        inherit Grain()

        static let actorName = ActorName.RepositoryName

        let log = loggerFactory.CreateLogger("RepositoryName.Actor")

        let mutable cachedRepositoryId: RepositoryId option = None

        /// Stores the correlation id used by this actor while reporting timings and errors.
        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            logActorActivation log this.IdentityString activateStartTime "In-memory only"

            Task.CompletedTask

        interface IRepositoryNameActor with
            /// Returns the repository id recorded in this RepositoryName actor state.
            member this.GetRepositoryId correlationId =
                this.correlationId <- correlationId
                cachedRepositoryId |> returnTask

            /// Stores set repository id data in the RepositoryName actor state.
            member this.SetRepositoryId (repositoryId: RepositoryId) correlationId =
                this.correlationId <- correlationId
                cachedRepositoryId <- Some repositoryId

                Task.CompletedTask
