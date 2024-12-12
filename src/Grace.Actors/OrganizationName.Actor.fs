namespace Grace.Actors

open Dapr.Actors
open Dapr.Actors.Runtime
open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Extensions.MemoryCache
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared.Types
open Grace.Shared.Utilities
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Threading.Tasks

module OrganizationName =

    let GetActorId (organizationName: string) = ActorId(organizationName)
    let log = loggerFactory.CreateLogger("OrganizationName.Actor")

    type OrganizationNameActor(host: ActorHost) =
        inherit Actor(host)

        static let actorName = ActorName.OrganizationName

        let mutable actorStartTime = Instant.MinValue
        let mutable logScope: IDisposable = null

        let idSections = host.Id.GetId().Split('|')
        let organizationName = idSections[0]
        let ownerId = idSections[1]

        let mutable cachedOrganizationId: OrganizationId option = None

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync() =
            let activateStartTime = getCurrentInstant ()

            let correlationId =
                match memoryCache.GetCorrelationIdEntry this.Id with
                | Some correlationId -> correlationId
                | None -> String.Empty

            logActorActivation log activateStartTime correlationId actorName this.Id "In-memory only"

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
                "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {ActorName}.{MethodName}; OwnerId: {OwnerId}; OrganizationName: {OrganizationName}; OrganizationId: {OrganizationId}.",
                getCurrentInstantExtended (),
                getMachineName,
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
