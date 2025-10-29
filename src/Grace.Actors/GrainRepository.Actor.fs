namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Extensions.MemoryCache
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Types.Types
open Grace.Shared.Utilities
open Microsoft.Extensions.Logging
open NodaTime
open Orleans
open Orleans.Runtime
open System
open System.Threading.Tasks

module GrainRepository =

    type GrainRepositoryActor() =
        inherit Grain()

        static let actorName = ActorName.RepositoryName

        let log = loggerFactory.CreateLogger("GrainRepository.Actor")

        let mutable cachedRepositoryId: RepositoryId option = None

        member val private correlationId: CorrelationId = String.Empty with get, set

        //override this.OnActivateAsync(ct) =
        //    logActorActivation log this.IdentityString "In-memory only"
        //    Task.CompletedTask

        interface IGrainRepositoryActor with
            member this.GetRepositoryId correlationId =
                this.correlationId <- correlationId
                cachedRepositoryId |> returnTask

            member this.SetRepositoryId (repositoryId: RepositoryId) correlationId =
                this.correlationId <- correlationId
                cachedRepositoryId <- Some repositoryId
                Task.CompletedTask
