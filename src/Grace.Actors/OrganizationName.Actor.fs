namespace Grace.Actors

open Dapr.Actors
open Dapr.Actors.Runtime
open Grace.Actors.Constants
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared.Types
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

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync() =
            log.LogInformation("{CurrentInstant} Activated {ActorType} {ActorId}.", getCurrentInstantExtended(), this.GetType().Name, host.Id)
            Task.CompletedTask

        override this.OnPreActorMethodAsync(context) =
            this.correlationId <- String.Empty
            actorStartTime <- getCurrentInstant()
            logScope <- log.BeginScope("Actor {actorName}", actorName)
            log.LogTrace("{CurrentInstant}: Started {ActorName}.{MethodName} Id: {Id}.", getCurrentInstantExtended(), actorName, context.MethodName, this.Id)
            Task.CompletedTask

        override this.OnPostActorMethodAsync(context) =
            let duration_ms = (getCurrentInstant().Minus(actorStartTime).TotalMilliseconds).ToString("F3")
            log.LogInformation("{CurrentInstant}: CorrelationId: {correlationId}; Finished {ActorName}.{MethodName}; OrganizationName: {OrganizationName}; OrganizationId: {OrganizationId}; Duration: {duration_ms}ms.", 
                getCurrentInstantExtended(), this.correlationId, actorName, context.MethodName, this.Id, (if Option.isSome cachedOrganizationId then cachedOrganizationId.Value else "None"), duration_ms)
            logScope.Dispose()
            Task.CompletedTask

        interface IOrganizationNameActor with
            member this.GetOrganizationId correlationId = 
                this.correlationId <- correlationId
                cachedOrganizationId |> returnTask

            member this.SetOrganizationId(organizationId: string) correlationId =
                this.correlationId <- correlationId
                let mutable guid = Guid.Empty
                if Guid.TryParse(organizationId, &guid) && guid <> Guid.Empty then
                    cachedOrganizationId <- Some organizationId
                Task.CompletedTask
