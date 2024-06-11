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

    let actorName = ActorName.OrganizationName
    let mutable actorStartTime = Instant.MinValue
    let mutable logScope: IDisposable = null

    let GetActorId (organizationName: string) = ActorId(organizationName)

    type OrganizationNameActor(host: ActorHost) =
        inherit Actor(host)

        let idSections = host.Id.GetId().Split('|')
        let organizationName = idSections[0]
        let ownerId = idSections[1]

        let log = loggerFactory.CreateLogger("OrganizationName.Actor")

        let mutable cachedOrganizationId: OrganizationId option = None

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync() =
            log.LogInformation(
                "{CurrentInstant}: Duration:   0.100ms; Activated {ActorType} {ActorId}.",
                getCurrentInstantExtended (),
                this.GetType().Name,
                host.Id
            )

            Task.CompletedTask

        override this.OnPreActorMethodAsync(context) =
            this.correlationId <- String.Empty
            actorStartTime <- getCurrentInstant ()
            logScope <- log.BeginScope("Actor {actorName}", actorName)

            log.LogTrace("{CurrentInstant}: Started {ActorName}.{MethodName} Id: {Id}.", getCurrentInstantExtended (), actorName, context.MethodName, this.Id)

            Task.CompletedTask

        override this.OnPostActorMethodAsync(context) =
            let duration_ms = getPaddedDuration_ms actorStartTime

            log.LogInformation(
                "{currentInstant}: Node: {hostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {ActorName}.{MethodName}; OwnerId: {OwnerId}; OrganizationName: {OrganizationName}; OrganizationId: {OrganizationId}.",
                getCurrentInstantExtended (),
                Environment.MachineName,
                duration_ms,
                this.correlationId,
                actorName,
                context.MethodName,
                ownerId,
                organizationName,
                (if Option.isSome cachedOrganizationId then
                     $"{cachedOrganizationId.Value}"
                 else
                     "None")
            )

            logScope.Dispose()
            Task.CompletedTask

        interface IOrganizationNameActor with
            member this.GetOrganizationId correlationId =
                this.correlationId <- correlationId
                cachedOrganizationId |> returnTask

            member this.SetOrganizationId (organizationId: string) correlationId =
                this.correlationId <- correlationId
                let mutable guid = Guid.Empty

                if Guid.TryParse(organizationId, &guid) && guid <> Guid.Empty then
                    cachedOrganizationId <- Some guid

                Task.CompletedTask
