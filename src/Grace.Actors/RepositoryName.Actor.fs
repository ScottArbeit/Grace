namespace Grace.Actors

open Dapr.Actors
open Dapr.Actors.Runtime
open System
open System.Threading.Tasks

module RepositoryName =

    let GetActorId (repositoryName: string) = ActorId(repositoryName)

    type IRepositoryNameActor =
        inherit IActor
        /// <summary>
        /// Sets the RepositoryId that matches the RepositoryName.
        /// </summary>
        abstract member SetRepositoryId: repositoryName: string -> Task

        /// <summary>
        /// Returns the RepositoryId for the given RepositoryName.
        /// </summary>
        abstract member GetRepositoryId: unit -> Task<String option>

    type RepositoryNameActor(host: ActorHost) =
        inherit Actor(host)

        let mutable cachedRepositoryId: string option = None

        interface IRepositoryNameActor with
            member this.GetRepositoryId() = Task.FromResult(cachedRepositoryId)

            member this.SetRepositoryId(repositoryId: string) =
                let mutable guid = Guid.Empty
                if Guid.TryParse(repositoryId, &guid) && guid <> Guid.Empty then
                    cachedRepositoryId <- Some repositoryId
                Task.CompletedTask
