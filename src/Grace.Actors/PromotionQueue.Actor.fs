namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Extensions.MemoryCache
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Actors.Types
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Grace.Types.Events
open Grace.Types.Policy
open Grace.Types.Queue
open Grace.Types.Common
open Microsoft.Extensions.Logging
open NodaTime
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

/// Groups Orleans actor helpers for promotion queue keys, proxies, state, or workflow transitions.
module PromotionQueue =

    /// Implements the Orleans grain for promotion queue actor.
    type PromotionQueueActor([<PersistentState(StateName.PromotionQueue, Constants.GraceActorStorage)>] state: IPersistentState<List<PromotionQueueEvent>>) =
        inherit Grain()

        static let actorName = ActorName.PromotionQueue

        let log = loggerFactory.CreateLogger("PromotionQueue.Actor")

        let mutable currentCommand = String.Empty

        let mutable promotionQueue = PromotionQueue.Default

        /// Stores the correlation id used by this actor while reporting timings and errors.
        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            promotionQueue <-
                state.State
                |> Seq.fold (fun dto ev -> PromotionQueueDto.UpdateDto ev dto) promotionQueue

            Task.CompletedTask

        /// Applies one persisted PromotionQueue event to this activation's in-memory state.
        member private this.ApplyEvent(queueEvent: PromotionQueueEvent) =
            task {
                let correlationId = queueEvent.Metadata.CorrelationId

                try
                    state.State.Add(queueEvent)
                    do! state.WriteStateAsync()

                    promotionQueue <-
                        promotionQueue
                        |> PromotionQueueDto.UpdateDto queueEvent

                    let graceEvent = GraceEvent.QueueEvent queueEvent
                    do! publishGraceEvent graceEvent queueEvent.Metadata

                    let returnValue =
                        (GraceReturnValue.Create "Promotion queue command succeeded." correlationId)
                            .enhance(nameof BranchId, promotionQueue.TargetBranchId)
                            .enhance (nameof PromotionQueueEventType, getDiscriminatedUnionFullName queueEvent.Event)

                    return Ok returnValue
                with
                | ex ->
                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {hostName}; CorrelationId: {correlationId}; Failed to apply event {eventType} for promotion queue {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId,
                        getDiscriminatedUnionCaseName queueEvent.Event,
                        promotionQueue.TargetBranchId
                    )

                    let graceError =
                        (GraceError.CreateWithException ex (QueueError.getErrorMessage QueueError.FailedWhileApplyingEvent) correlationId)
                            .enhance (nameof BranchId, promotionQueue.TargetBranchId)

                    return Error graceError
            }

        interface IHasRepositoryId with
            /// Returns the repository id recorded in this PromotionQueue actor state.
            member this.GetRepositoryId correlationId = RepositoryId.Empty |> returnTask

        interface IPromotionQueueActor with
            /// Reports whether this PromotionQueue actor has persisted state.
            member this.Exists correlationId =
                this.correlationId <- correlationId

                (promotionQueue.TargetBranchId <> BranchId.Empty)
                |> returnTask

            /// Returns the current PromotionQueue actor state snapshot.
            member this.Get correlationId =
                this.correlationId <- correlationId
                promotionQueue |> returnTask

            /// Returns for route data from the PromotionQueue actor state or related storage.
            member this.GetForRoute correlationId =
                this.correlationId <- correlationId
                serialize promotionQueue |> returnTask

            /// Returns the persisted PromotionQueue event stream for replay or audit.
            member this.GetEvents correlationId =
                this.correlationId <- correlationId

                state.State :> IReadOnlyList<PromotionQueueEvent>
                |> returnTask

            /// Coordinates initialize for route logic for the PromotionQueue actor.
            member this.InitializeForRoute targetBranchId policySnapshotId metadata =
                (this :> IPromotionQueueActor).Handle (PromotionQueueCommand.Initialize(targetBranchId, PolicySnapshotId policySnapshotId)) metadata

            /// Adds enqueue for route data to the PromotionQueue actor workflow or state.
            member this.EnqueueForRoute promotionSetId metadata = (this :> IPromotionQueueActor).Handle (PromotionQueueCommand.Enqueue promotionSetId) metadata

            /// Removes or invalidates dequeue for route data from the PromotionQueue actor state.
            member this.DequeueForRoute promotionSetId metadata = (this :> IPromotionQueueActor).Handle (PromotionQueueCommand.Dequeue promotionSetId) metadata

            /// Coordinates pause for route logic for the PromotionQueue actor.
            member this.PauseForRoute metadata = (this :> IPromotionQueueActor).Handle PromotionQueueCommand.Pause metadata

            /// Coordinates resume for route logic for the PromotionQueue actor.
            member this.ResumeForRoute metadata = (this :> IPromotionQueueActor).Handle PromotionQueueCommand.Resume metadata

            /// Routes a public actor command to the domain operation that validates and persists it.
            member this.Handle command metadata =
                /// Checks whether command validation succeeded before emitting the domain event.
                let isValid (command: PromotionQueueCommand) (metadata: EventMetadata) =
                    task {
                        if state.State.Exists(fun ev -> ev.Metadata.CorrelationId = metadata.CorrelationId) then
                            return Error(GraceError.Create (QueueError.getErrorMessage QueueError.DuplicateCorrelationId) metadata.CorrelationId)
                        else
                            let result =
                                match command with
                                | Initialize _ ->
                                    if promotionQueue.TargetBranchId <> BranchId.Empty then
                                        Error(GraceError.Create (QueueError.getErrorMessage QueueError.QueueAlreadyInitialized) metadata.CorrelationId)
                                    else
                                        Ok command
                                | _ ->
                                    if promotionQueue.TargetBranchId = BranchId.Empty then
                                        Error(GraceError.Create (QueueError.getErrorMessage QueueError.QueueNotInitialized) metadata.CorrelationId)
                                    else
                                        match command with
                                        | Dequeue promotionSetId ->
                                            let exists =
                                                promotionQueue.PromotionSetIds
                                                |> List.exists (fun existing -> existing = promotionSetId)

                                            if exists then
                                                Ok command
                                            else
                                                Error(GraceError.Create (QueueError.getErrorMessage QueueError.PromotionSetNotInQueue) metadata.CorrelationId)
                                        | SetRunning (Some promotionSetId) ->
                                            let exists =
                                                promotionQueue.PromotionSetIds
                                                |> List.exists (fun existing -> existing = promotionSetId)

                                            if exists then
                                                Ok command
                                            else
                                                Error(GraceError.Create (QueueError.getErrorMessage QueueError.PromotionSetNotInQueue) metadata.CorrelationId)
                                        | _ -> Ok command

                            return result
                    }

                /// Runs PromotionQueue command decisions, applies emitted events, and persists the result.
                let processCommand (command: PromotionQueueCommand) (metadata: EventMetadata) =
                    task {
                        let! (queueEventType: PromotionQueueEventType) =
                            task {
                                match command with
                                | Initialize (targetBranchId, policySnapshotId) -> return Initialized(targetBranchId, policySnapshotId)
                                | Enqueue promotionSetId -> return PromotionSetEnqueued promotionSetId
                                | Dequeue promotionSetId -> return PromotionSetDequeued promotionSetId
                                | SetRunning promotionSetId -> return RunningPromotionSetSet promotionSetId
                                | Pause -> return Paused
                                | Resume -> return Resumed
                                | SetDegraded -> return Degraded
                                | UpdatePolicySnapshot policySnapshotId -> return PolicySnapshotUpdated policySnapshotId
                            }

                        let queueEvent: PromotionQueueEvent = { Event = queueEventType; Metadata = metadata }
                        return! this.ApplyEvent queueEvent
                    }

                task {
                    currentCommand <- getDiscriminatedUnionCaseName command
                    this.correlationId <- metadata.CorrelationId
                    RequestContext.Set(Constants.CurrentCommandProperty, getDiscriminatedUnionCaseName command)

                    match! isValid command metadata with
                    | Ok command -> return! processCommand command metadata
                    | Error error -> return Error error
                }
