namespace Grace.Actors

open Dapr.Actors
open Dapr.Actors.Runtime
open Grace.Actors.Constants
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared.Utilities
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Threading.Tasks

module RepositoryName =

    let mutable actorStartTime = Instant.MinValue
    let mutable logScope: IDisposable = null

    let GetActorId (repositoryName: string) = ActorId(repositoryName)

    type RepositoryNameActor(host: ActorHost) =
        inherit Actor(host)

        let actorName = Constants.ActorName.RepositoryName
    
        let log = loggerFactory.CreateLogger(actorName)

        let mutable cachedRepositoryId: string option = None

        override this.OnPreActorMethodAsync(context) =
            actorStartTime <- getCurrentInstant()
            logScope <- log.BeginScope("Actor {actorName}", actorName)
            log.LogTrace("{CurrentInstant}: Started {ActorName}.{MethodName} Id: {Id}.", getCurrentInstantExtended(), actorName, context.MethodName, this.Id)
            Task.CompletedTask

        override this.OnPostActorMethodAsync(context) =
            let duration_ms = (getCurrentInstant().Minus(actorStartTime).TotalMilliseconds).ToString("F3")
            log.LogInformation("{CurrentInstant}: Finished {ActorName}.{MethodName}; RepositoryName: {RepositoryName}; RepositoryId: {RepositoryId}; Duration: {duration}ms.", 
                getCurrentInstantExtended(), actorName, context.MethodName, this.Id, (if Option.isSome cachedRepositoryId then cachedRepositoryId.Value else "None"), duration_ms)
            logScope.Dispose()
            Task.CompletedTask

        interface IRepositoryNameActor with
            member this.GetRepositoryId() = cachedRepositoryId |> returnTask

            member this.SetRepositoryId(repositoryId: string) =
                let mutable guid = Guid.Empty
                if Guid.TryParse(repositoryId, &guid) && guid <> Guid.Empty then
                    cachedRepositoryId <- Some repositoryId
                Task.CompletedTask
