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
open OrganizationName

module BranchName =

    let log = loggerFactory.CreateLogger("BranchName.Actor")

    type BranchNameActor(host: ActorHost) =
        inherit Actor(host)

        static let actorName = ActorName.BranchName

        let mutable actorStartTime = Instant.MinValue
        let mutable logScope: IDisposable = null

        let idSections = host.Id.GetId().Split('|')
        let branchName = idSections[0]
        let repositoryId = idSections[1]

        let mutable cachedBranchId: Guid option = None

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
            actorStartTime <- getCurrentInstant ()
            this.correlationId <- String.Empty
            logScope <- log.BeginScope("Actor {actorName}", actorName)

            log.LogTrace("{CurrentInstant}: Started {ActorName}.{MethodName} Id: {Id}.", getCurrentInstantExtended (), actorName, context.MethodName, this.Id)

            Task.CompletedTask

        override this.OnPostActorMethodAsync(context) =
            let duration_ms = getPaddedDuration_ms actorStartTime

            log.LogInformation(
                "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {ActorName}.{MethodName}; RepositoryId: {RepositoryId}; BranchName: {BranchName}; BranchId: {BranchId}.",
                getCurrentInstantExtended (),
                getMachineName,
                duration_ms,
                this.correlationId,
                actorName,
                context.MethodName,
                repositoryId,
                branchName,
                (if Option.isSome cachedBranchId then $"{cachedBranchId.Value}" else "None")
            )

            logScope.Dispose()
            Task.CompletedTask

        interface IBranchNameActor with
            member this.GetBranchId correlationId =
                this.correlationId <- correlationId
                Task.FromResult(cachedBranchId)

            member this.SetBranchId branchId correlationId =
                this.correlationId <- correlationId
                cachedBranchId <- Some branchId
                Task.CompletedTask
