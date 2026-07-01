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
open Grace.Types.Common
open Microsoft.Extensions.Logging
open NodaTime
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

/// Groups Orleans actor helpers for policy keys, proxies, state, or workflow transitions.
module Policy =

    /// Implements the Orleans grain for policy actor.
    type PolicyActor([<PersistentState(StateName.Policy, Constants.GraceActorStorage)>] state: IPersistentState<List<PolicyEvent>>) =
        inherit Grain()

        static let actorName = ActorName.Policy

        let log = loggerFactory.CreateLogger("Policy.Actor")

        let mutable currentCommand = String.Empty

        let mutable snapshots: PolicySnapshot list = []

        let mutable acknowledgements: PolicyAcknowledgement list = []

        let mutable repositoryId: RepositoryId = RepositoryId.Empty

        /// Stores the correlation id used by this actor while reporting timings and errors.
        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            /// Applies to state changes to the Policy actor state.
            let applyToState (policyEvent: PolicyEvent) =
                match policyEvent.Event with
                | SnapshotCreated snapshot ->
                    snapshots <-
                        snapshots
                        |> List.filter (fun s -> s.PolicySnapshotId <> snapshot.PolicySnapshotId)
                        |> fun list -> list @ [ snapshot ]

                    repositoryId <- snapshot.RepositoryId
                | Acknowledged (policySnapshotId, acknowledgedBy, note) ->
                    let acknowledgement =
                        { PolicySnapshotId = policySnapshotId; AcknowledgedBy = acknowledgedBy; AcknowledgedAt = policyEvent.Metadata.Timestamp; Note = note }

                    acknowledgements <- acknowledgements @ [ acknowledgement ]

            state.State |> Seq.iter applyToState

            Task.CompletedTask

        /// Returns current snapshot data from the Policy actor state or related storage.
        member private this.GetCurrentSnapshot() =
            snapshots
            |> List.sortBy (fun snapshot -> snapshot.CreatedAt)
            |> List.tryLast

        /// Applies one persisted Policy event to this activation's in-memory state.
        member private this.ApplyEvent(policyEvent: PolicyEvent) =
            task {
                let correlationId = policyEvent.Metadata.CorrelationId

                try
                    state.State.Add(policyEvent)
                    do! state.WriteStateAsync()

                    match policyEvent.Event with
                    | SnapshotCreated snapshot ->
                        snapshots <-
                            snapshots
                            |> List.filter (fun s -> s.PolicySnapshotId <> snapshot.PolicySnapshotId)
                            |> fun list -> list @ [ snapshot ]

                        repositoryId <- snapshot.RepositoryId
                    | Acknowledged (policySnapshotId, acknowledgedBy, note) ->
                        let acknowledgement =
                            {
                                PolicySnapshotId = policySnapshotId
                                AcknowledgedBy = acknowledgedBy
                                AcknowledgedAt = policyEvent.Metadata.Timestamp
                                Note = note
                            }

                        acknowledgements <- acknowledgements @ [ acknowledgement ]

                    let graceEvent = GraceEvent.PolicyEvent policyEvent
                    do! publishGraceEvent graceEvent policyEvent.Metadata

                    let policySnapshotId =
                        match policyEvent.Event with
                        | SnapshotCreated snapshot -> snapshot.PolicySnapshotId
                        | Acknowledged (policySnapshotId, _, _) -> policySnapshotId

                    let returnValue =
                        (GraceReturnValue.Create "Policy command succeeded." correlationId)
                            .enhance(nameof RepositoryId, repositoryId)
                            .enhance(nameof PolicySnapshotId, policySnapshotId)
                            .enhance (nameof PolicyEventType, getDiscriminatedUnionFullName policyEvent.Event)

                    return Ok returnValue
                with
                | ex ->
                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {hostName}; CorrelationId: {correlationId}; Failed to apply event {eventType} for policy.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId,
                        getDiscriminatedUnionCaseName policyEvent.Event
                    )

                    let graceError =
                        (GraceError.CreateWithException ex (PolicyError.getErrorMessage PolicyError.FailedWhileApplyingEvent) correlationId)
                            .enhance (nameof RepositoryId, repositoryId)

                    return Error graceError
            }

        interface IHasRepositoryId with
            /// Returns the repository id recorded in this Policy actor state.
            member this.GetRepositoryId correlationId = repositoryId |> returnTask

        interface IPolicyActor with
            /// Returns current data from the Policy actor state or related storage.
            member this.GetCurrent correlationId =
                this.correlationId <- correlationId
                this.GetCurrentSnapshot() |> returnTask

            /// Returns snapshots data from the Policy actor state or related storage.
            member this.GetSnapshots correlationId =
                this.correlationId <- correlationId

                (snapshots :> IReadOnlyList<PolicySnapshot>)
                |> returnTask

            /// Returns acknowledgements data from the Policy actor state or related storage.
            member this.GetAcknowledgements correlationId =
                this.correlationId <- correlationId

                (acknowledgements :> IReadOnlyList<PolicyAcknowledgement>)
                |> returnTask

            /// Routes a public actor command to the domain operation that validates and persists it.
            member this.Handle command metadata =
                /// Checks whether command validation succeeded before emitting the domain event.
                let isValid (command: PolicyCommand) (metadata: EventMetadata) =
                    task {
                        if state.State.Exists(fun ev -> ev.Metadata.CorrelationId = metadata.CorrelationId) then
                            return Error(GraceError.Create (PolicyError.getErrorMessage PolicyError.DuplicateCorrelationId) metadata.CorrelationId)
                        else
                            match command with
                            | CreateSnapshot snapshot ->
                                let exists =
                                    snapshots
                                    |> List.exists (fun existing -> existing.PolicySnapshotId = snapshot.PolicySnapshotId)

                                if exists then
                                    return Error(GraceError.Create (PolicyError.getErrorMessage PolicyError.PolicySnapshotAlreadyExists) metadata.CorrelationId)
                                else
                                    return Ok command
                            | Acknowledge (policySnapshotId, _, _) ->
                                let exists =
                                    snapshots
                                    |> List.exists (fun existing -> existing.PolicySnapshotId = policySnapshotId)

                                if exists then
                                    return Ok command
                                else
                                    return Error(GraceError.Create (PolicyError.getErrorMessage PolicyError.PolicySnapshotDoesNotExist) metadata.CorrelationId)
                    }

                /// Runs Policy command decisions, applies emitted events, and persists the result.
                let processCommand (command: PolicyCommand) (metadata: EventMetadata) =
                    task {
                        let! policyEventType =
                            task {
                                match command with
                                | CreateSnapshot snapshot -> return SnapshotCreated snapshot
                                | Acknowledge (policySnapshotId, acknowledgedBy, note) -> return Acknowledged(policySnapshotId, acknowledgedBy, note)
                            }

                        let policyEvent = { Event = policyEventType; Metadata = metadata }
                        return! this.ApplyEvent policyEvent
                    }

                task {
                    currentCommand <- getDiscriminatedUnionCaseName command
                    this.correlationId <- metadata.CorrelationId
                    RequestContext.Set(Constants.CurrentCommandProperty, getDiscriminatedUnionCaseName command)

                    match! isValid command metadata with
                    | Ok command -> return! processCommand command metadata
                    | Error error -> return Error error
                }
