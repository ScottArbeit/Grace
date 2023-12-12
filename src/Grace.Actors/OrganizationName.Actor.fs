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

module OrganizationName =

    let mutable actorStartTime = Instant.MinValue
    let mutable logScope: IDisposable = null

    let GetActorId (organizationName: string) = ActorId(organizationName)

    type OrganizationNameActor(host: ActorHost) =
        inherit Actor(host)

        let actorName = ActorName.OrganizationName
        let log = loggerFactory.CreateLogger("OrganizationName.Actor")
    
        let mutable cachedOrganizationId: string option = None

        override this.OnPreActorMethodAsync(context) =
            actorStartTime <- getCurrentInstant()
            logScope <- log.BeginScope("Actor {actorName}", actorName)
            log.LogTrace("{CurrentInstant}: Started {ActorName}.{MethodName} Id: {Id}.", getCurrentInstantExtended(), actorName, context.MethodName, this.Id)
            Task.CompletedTask

        override this.OnPostActorMethodAsync(context) =
            let duration_ms = (getCurrentInstant().Minus(actorStartTime).TotalMilliseconds).ToString("F3")
            log.LogInformation("{CurrentInstant}: Finished {ActorName}.{MethodName}; OrganizationName: {OrganizationName}; OrganizationId: {OrganizationId}; Duration: {duration_ms}ms.", 
                getCurrentInstantExtended(), actorName, context.MethodName, this.Id, (if Option.isSome cachedOrganizationId then cachedOrganizationId.Value else "None"), duration_ms)
            logScope.Dispose()
            Task.CompletedTask

        interface IOrganizationNameActor with
            member this.GetOrganizationId() = cachedOrganizationId |> returnTask

            member this.SetOrganizationId(organizationId: string) =
                let mutable guid = Guid.Empty
                if Guid.TryParse(organizationId, &guid) && guid <> Guid.Empty then
                    cachedOrganizationId <- Some organizationId
                Task.CompletedTask
