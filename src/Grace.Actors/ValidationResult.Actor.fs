namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Actors.Types
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types.Events
open Grace.Types.Common
open Grace.Types.Validation
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

/// Groups Orleans actor helpers for validation result keys, proxies, state, or workflow transitions.
module ValidationResult =
    /// Checks whether the request correlation id already appears in persisted events.
    let internal hasDuplicateCorrelationId (events: seq<ValidationResultEvent>) (metadata: EventMetadata) =
        events
        |> Seq.exists (fun ev -> ev.Metadata.CorrelationId = metadata.CorrelationId)

    /// Implements the Orleans grain for validation result actor.
    type ValidationResultActor
        (
            [<PersistentState(StateName.ValidationResult, Constants.GraceActorStorage)>] state: IPersistentState<List<ValidationResultEvent>>
        ) =
        inherit Grain()

        static let actorName = ActorName.ValidationResult
        let log = loggerFactory.CreateLogger("ValidationResult.Actor")

        let mutable currentCommand = String.Empty
        let mutable validationResult = ValidationResultDto.Default

        /// Stores the correlation id used by this actor while reporting timings and errors.
        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            validationResult <-
                state.State
                |> Seq.fold (fun dto event -> ValidationResultDto.UpdateDto event dto) validationResult

            Task.CompletedTask

        /// Applies one persisted ValidationResult event to this activation's in-memory state.
        member private this.ApplyEvent(validationResultEvent: ValidationResultEvent) =
            task {
                let correlationId = validationResultEvent.Metadata.CorrelationId

                try
                    state.State.Add(validationResultEvent)
                    do! state.WriteStateAsync()

                    validationResult <-
                        validationResult
                        |> ValidationResultDto.UpdateDto validationResultEvent

                    let graceEvent = GraceEvent.ValidationResultEvent validationResultEvent
                    do! publishGraceEvent graceEvent validationResultEvent.Metadata

                    let graceReturnValue: GraceReturnValue<string> =
                        (GraceReturnValue.Create "Validation result command succeeded." correlationId)
                            .enhance(nameof RepositoryId, validationResult.RepositoryId)
                            .enhance (nameof ValidationResultId, validationResult.ValidationResultId)

                    return Ok graceReturnValue
                with
                | ex ->
                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Failed to apply event for ValidationResultId: {ValidationResultId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId,
                        validationResult.ValidationResultId
                    )

                    return
                        Error(
                            (GraceError.CreateWithException ex "Failed while applying ValidationResult event." correlationId)
                                .enhance(nameof RepositoryId, validationResult.RepositoryId)
                                .enhance (nameof ValidationResultId, validationResult.ValidationResultId)
                        )
            }

        interface IHasRepositoryId with
            /// Returns the repository id recorded in this ValidationResult actor state.
            member this.GetRepositoryId correlationId = validationResult.RepositoryId |> returnTask

        interface IValidationResultActor with
            /// Reports whether this ValidationResult actor has persisted state.
            member this.Exists correlationId =
                this.correlationId <- correlationId

                not
                <| validationResult.ValidationResultId.Equals(ValidationResultId.Empty)
                |> returnTask

            /// Returns the current ValidationResult actor state snapshot.
            member this.Get correlationId =
                this.correlationId <- correlationId

                if validationResult.ValidationResultId = ValidationResultId.Empty then
                    Option.None
                else
                    Some validationResult
                |> returnTask

            /// Returns the persisted ValidationResult event stream for replay or audit.
            member this.GetEvents correlationId =
                this.correlationId <- correlationId

                state.State :> IReadOnlyList<ValidationResultEvent>
                |> returnTask

            /// Routes a public actor command to the domain operation that validates and persists it.
            member this.Handle command metadata =
                /// Checks whether command validation succeeded before emitting the domain event.
                let isValid (validationResultCommand: ValidationResultCommand) (eventMetadata: EventMetadata) =
                    task {
                        if hasDuplicateCorrelationId state.State eventMetadata then
                            return Error(GraceError.Create "Duplicate correlation ID for ValidationResult command." eventMetadata.CorrelationId)
                        else
                            match validationResultCommand with
                            | ValidationResultCommand.Record _ -> return Ok validationResultCommand
                    }

                /// Runs ValidationResult command decisions, applies emitted events, and persists the result.
                let processCommand (validationResultCommand: ValidationResultCommand) (eventMetadata: EventMetadata) =
                    task {
                        let eventType =
                            match validationResultCommand with
                            | ValidationResultCommand.Record validationResultDto -> ValidationResultEventType.Recorded validationResultDto

                        let validationResultEvent: ValidationResultEvent = { Event = eventType; Metadata = eventMetadata }
                        return! this.ApplyEvent validationResultEvent
                    }

                task {
                    currentCommand <- getDiscriminatedUnionCaseName command
                    this.correlationId <- metadata.CorrelationId

                    match! isValid command metadata with
                    | Ok validCommand -> return! processCommand validCommand metadata
                    | Error validationError -> return Error validationError
                }
