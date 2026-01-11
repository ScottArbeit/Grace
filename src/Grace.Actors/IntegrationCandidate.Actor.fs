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

module IntegrationCandidate =

    type IntegrationCandidateActor
        (
            [<PersistentState(StateName.IntegrationCandidate, Constants.GraceActorStorage)>] state: IPersistentState<List<CandidateEvent>>
        ) =
        inherit Grain()

        static let actorName = ActorName.IntegrationCandidate

        let log = loggerFactory.CreateLogger("IntegrationCandidate.Actor")

        let mutable currentCommand = String.Empty

        let mutable candidate = IntegrationCandidate.Default

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            candidate <-
                state.State
                |> Seq.fold (fun dto ev -> IntegrationCandidateDto.UpdateDto ev dto) candidate

            Task.CompletedTask

        member private this.ApplyEvent(candidateEvent: CandidateEvent) =
            task {
                let correlationId = candidateEvent.Metadata.CorrelationId

                try
                    state.State.Add(candidateEvent)
                    do! state.WriteStateAsync()

                    candidate <-
                        candidate
                        |> IntegrationCandidateDto.UpdateDto candidateEvent

                    let graceEvent = GraceEvent.CandidateEvent candidateEvent
                    do! publishGraceEvent graceEvent candidateEvent.Metadata

                    let returnValue =
                        (GraceReturnValue.Create "Integration candidate command succeeded." correlationId)
                            .enhance(nameof CandidateId, candidate.CandidateId)
                            .enhance (nameof CandidateEventType, getDiscriminatedUnionFullName candidateEvent.Event)

                    return Ok returnValue
                with
                | ex ->
                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {hostName}; CorrelationId: {correlationId}; Failed to apply event {eventType} for candidate {candidateId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId,
                        getDiscriminatedUnionCaseName candidateEvent.Event,
                        candidate.CandidateId
                    )

                    let graceError =
                        (GraceError.CreateWithException ex (CandidateError.getErrorMessage CandidateError.FailedWhileApplyingEvent) correlationId)
                            .enhance (nameof CandidateId, candidate.CandidateId)

                    return Error graceError
            }

        interface IHasRepositoryId with
            member this.GetRepositoryId correlationId = candidate.RepositoryId |> returnTask

        interface IIntegrationCandidateActor with
            member this.Exists correlationId =
                this.correlationId <- correlationId

                (candidate.CandidateId <> CandidateId.Empty)
                |> returnTask

            member this.Get correlationId =
                this.correlationId <- correlationId

                if candidate.CandidateId = CandidateId.Empty then
                    None |> returnTask
                else
                    Some candidate |> returnTask

            member this.GetEvents correlationId =
                this.correlationId <- correlationId

                state.State :> IReadOnlyList<CandidateEvent>
                |> returnTask

            member this.Handle command metadata =
                let isValid (command: CandidateCommand) (metadata: EventMetadata) =
                    task {
                        if state.State.Exists(fun ev -> ev.Metadata.CorrelationId = metadata.CorrelationId) then
                            return Error(GraceError.Create (CandidateError.getErrorMessage CandidateError.DuplicateCorrelationId) metadata.CorrelationId)
                        else
                            match command with
                            | CandidateCommand.Initialize _ ->
                                if candidate.CandidateId <> CandidateId.Empty then
                                    return
                                        Error(GraceError.Create (CandidateError.getErrorMessage CandidateError.CandidateAlreadyExists) metadata.CorrelationId)
                                else
                                    return Ok command
                            | _ ->
                                if candidate.CandidateId = CandidateId.Empty then
                                    return Error(GraceError.Create (CandidateError.getErrorMessage CandidateError.CandidateDoesNotExist) metadata.CorrelationId)
                                else
                                    return Ok command
                    }

                let processCommand (command: CandidateCommand) (metadata: EventMetadata) =
                    task {
                        let! (candidateEventType: CandidateEventType) =
                            task {
                                match command with
                                | CandidateCommand.Initialize candidate -> return CandidateEventType.Initialized candidate
                                | CandidateCommand.SetStatus status -> return CandidateEventType.StatusSet status
                                | CandidateCommand.SetRequiredActions actions -> return CandidateEventType.RequiredActionsSet actions
                                | CandidateCommand.AddRequiredAction action -> return CandidateEventType.RequiredActionAdded action
                                | CandidateCommand.ClearRequiredActions -> return CandidateEventType.RequiredActionsCleared
                                | CandidateCommand.AddGateAttestation gateAttestationId -> return CandidateEventType.GateAttestationAdded gateAttestationId
                                | CandidateCommand.AddConflict conflict -> return CandidateEventType.ConflictAdded conflict
                                | CandidateCommand.AddConflictReceipt conflictReceiptId -> return CandidateEventType.ConflictReceiptAdded conflictReceiptId
                            }

                        let candidateEvent: CandidateEvent = { Event = candidateEventType; Metadata = metadata }
                        return! this.ApplyEvent candidateEvent
                    }

                task {
                    currentCommand <- getDiscriminatedUnionCaseName command
                    this.correlationId <- metadata.CorrelationId
                    RequestContext.Set(Constants.CurrentCommandProperty, getDiscriminatedUnionCaseName command)

                    match! isValid command metadata with
                    | Ok command -> return! processCommand command metadata
                    | Error error -> return Error error
                }
