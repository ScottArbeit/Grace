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

module GateAttestation =

    type GateAttestationActor([<PersistentState(StateName.GateAttestation, Constants.GraceActorStorage)>] state: IPersistentState<List<GateAttestationEvent>>) =
        inherit Grain()

        static let actorName = ActorName.GateAttestation

        let log = loggerFactory.CreateLogger("GateAttestation.Actor")

        let mutable currentCommand = String.Empty

        let mutable attestation = GateAttestation.Default

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            attestation <-
                state.State
                |> Seq.fold (fun dto ev -> GateAttestationDto.UpdateDto ev dto) attestation

            Task.CompletedTask

        member private this.ApplyEvent(attestationEvent: GateAttestationEvent) =
            task {
                let correlationId = attestationEvent.Metadata.CorrelationId

                try
                    state.State.Add(attestationEvent)
                    do! state.WriteStateAsync()

                    attestation <- attestation |> GateAttestationDto.UpdateDto attestationEvent

                    let graceEvent = GraceEvent.GateAttestationEvent attestationEvent
                    do! publishGraceEvent graceEvent attestationEvent.Metadata

                    let returnValue =
                        (GraceReturnValue.Create "Gate attestation recorded." correlationId)
                            .enhance(nameof GateAttestationId, attestation.GateAttestationId)
                            .enhance(nameof CandidateId, attestation.CandidateId)
                            .enhance (nameof GateAttestationEventType, getDiscriminatedUnionFullName attestationEvent.Event)

                    return Ok returnValue
                with ex ->
                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {hostName}; CorrelationId: {correlationId}; Failed to apply event {eventType} for gate attestation {attestationId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId,
                        getDiscriminatedUnionCaseName attestationEvent.Event,
                        attestation.GateAttestationId
                    )

                    let graceError =
                        (GraceError.CreateWithException ex (GateAttestationError.getErrorMessage GateAttestationError.FailedWhileApplyingEvent) correlationId)
                            .enhance (nameof GateAttestationId, attestation.GateAttestationId)

                    return Error graceError
            }

        interface IHasRepositoryId with
            member this.GetRepositoryId correlationId = attestation.RepositoryId |> returnTask

        interface IGateAttestationActor with
            member this.Exists correlationId =
                this.correlationId <- correlationId
                (attestation.GateAttestationId <> GateAttestationId.Empty) |> returnTask

            member this.Get correlationId =
                this.correlationId <- correlationId

                if attestation.GateAttestationId = GateAttestationId.Empty then
                    None |> returnTask
                else
                    Some attestation |> returnTask

            member this.GetEvents correlationId =
                this.correlationId <- correlationId
                state.State :> IReadOnlyList<GateAttestationEvent> |> returnTask

            member this.Handle command metadata =
                let isValid (command: GateAttestationCommand) (metadata: EventMetadata) =
                    task {
                        if state.State.Exists(fun ev -> ev.Metadata.CorrelationId = metadata.CorrelationId) then
                            return
                                Error(
                                    GraceError.Create (GateAttestationError.getErrorMessage GateAttestationError.DuplicateCorrelationId) metadata.CorrelationId
                                )
                        else
                            match command with
                            | GateAttestationCommand.Create _ ->
                                if attestation.GateAttestationId <> GateAttestationId.Empty then
                                    return
                                        Error(
                                            GraceError.Create
                                                (GateAttestationError.getErrorMessage GateAttestationError.GateAttestationAlreadyExists)
                                                metadata.CorrelationId
                                        )
                                else
                                    return Ok command
                    }

                let processCommand (command: GateAttestationCommand) (metadata: EventMetadata) =
                    task {
                        let! (attestationEventType: GateAttestationEventType) =
                            task {
                                match command with
                                | GateAttestationCommand.Create attestation -> return GateAttestationEventType.Created attestation
                            }

                        let attestationEvent: GateAttestationEvent = { Event = attestationEventType; Metadata = metadata }
                        return! this.ApplyEvent attestationEvent
                    }

                task {
                    currentCommand <- getDiscriminatedUnionCaseName command
                    this.correlationId <- metadata.CorrelationId
                    RequestContext.Set(Constants.CurrentCommandProperty, getDiscriminatedUnionCaseName command)

                    match! isValid command metadata with
                    | Ok command -> return! processCommand command metadata
                    | Error error -> return Error error
                }
