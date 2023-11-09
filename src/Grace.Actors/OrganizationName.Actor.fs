namespace Grace.Actors

open Dapr.Actors
open Dapr.Actors.Runtime
open Grace.Actors.Constants
open Grace.Actors.Interfaces
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
        let log = host.LoggerFactory.CreateLogger(actorName)
    
        let mutable cachedOrganizationId: string option = None

        override this.OnPreActorMethodAsync(context) =
            actorStartTime <- getCurrentInstant()
            logScope <- log.BeginScope("Actor {actorName}", actorName)
            log.LogTrace("{CurrentInstant}: Started {ActorName}.{MethodName} Id: {Id}.", getCurrentInstantExtended(), actorName, context.MethodName, this.Id)
            Task.CompletedTask

        override this.OnPostActorMethodAsync(context) =
            let durationμs = (getCurrentInstant().Minus(actorStartTime).TotalMilliseconds * 1000.0).ToString("F0")
            log.LogInformation("{CurrentInstant}: Finished {ActorName}.{MethodName}; Id: {Id}; Duration: {duration}μs.", getCurrentInstantExtended(), actorName, context.MethodName, this.Id, durationμs)
            logScope.Dispose()
            Task.CompletedTask

        interface IOrganizationNameActor with
            member this.GetOrganizationId() = Task.FromResult(cachedOrganizationId)

            member this.SetOrganizationId(organizationId: string) =
                let mutable guid = Guid.Empty
                if Guid.TryParse(organizationId, &guid) && guid <> Guid.Empty then
                    cachedOrganizationId <- Some organizationId
                Task.CompletedTask
