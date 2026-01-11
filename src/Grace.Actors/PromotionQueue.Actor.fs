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
open Grace.Types.Queue
open Grace.Types.Types
open Microsoft.Extensions.Logging
open NodaTime
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

module PromotionQueue =

    type PromotionQueueActor([<PersistentState(StateName.PromotionQueue, Constants.GraceActorStorage)>] state: IPersistentState<List<PromotionQueueEvent>>) =
        inherit Grain()

        static let actorName = ActorName.PromotionQueue

        let log = loggerFactory.CreateLogger("PromotionQueue.Actor")

        let mutable currentCommand = String.Empty

        let mutable promotionQueue = PromotionQueue.Default

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            promotionQueue <-
                state.State
                |> Seq.fold (fun dto ev -> PromotionQueueDto.UpdateDto ev dto) promotionQueue

            Task.CompletedTask

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
            member this.GetRepositoryId correlationId = RepositoryId.Empty |> returnTask

        interface IPromotionQueueActor with
            member this.Exists correlationId =
                this.correlationId <- correlationId

                (promotionQueue.TargetBranchId <> BranchId.Empty)
                |> returnTask

            member this.Get correlationId =
                this.correlationId <- correlationId
                promotionQueue |> returnTask

            member this.GetEvents correlationId =
                this.correlationId <- correlationId

                state.State :> IReadOnlyList<PromotionQueueEvent>
                |> returnTask

            member this.Handle command metadata =
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
                                        | Dequeue candidateId ->
                                            let exists =
                                                promotionQueue.CandidateIds
                                                |> List.exists (fun existing -> existing = candidateId)

                                            if exists then
                                                Ok command
                                            else
                                                Error(GraceError.Create (QueueError.getErrorMessage QueueError.CandidateNotInQueue) metadata.CorrelationId)
                                        | SetRunning (Some candidateId) ->
                                            let exists =
                                                promotionQueue.CandidateIds
                                                |> List.exists (fun existing -> existing = candidateId)

                                            if exists then
                                                Ok command
                                            else
                                                Error(GraceError.Create (QueueError.getErrorMessage QueueError.CandidateNotInQueue) metadata.CorrelationId)
                                        | _ -> Ok command

                            return result
                    }

                let processCommand (command: PromotionQueueCommand) (metadata: EventMetadata) =
                    task {
                        let! (queueEventType: PromotionQueueEventType) =
                            task {
                                match command with
                                | Initialize (targetBranchId, policySnapshotId) -> return Initialized(targetBranchId, policySnapshotId)
                                | Enqueue candidateId -> return CandidateEnqueued candidateId
                                | Dequeue candidateId -> return CandidateDequeued candidateId
                                | SetRunning candidateId -> return RunningCandidateSet candidateId
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
