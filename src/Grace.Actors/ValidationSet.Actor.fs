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

/// Groups Orleans actor helpers for validation set keys, proxies, state, or workflow transitions.
module ValidationSet =

    /// Implements the Orleans grain for validation set actor.
    type ValidationSetActor([<PersistentState(StateName.ValidationSet, Constants.GraceActorStorage)>] state: IPersistentState<List<ValidationSetEvent>>) =
        inherit Grain()

        static let actorName = ActorName.ValidationSet
        let log = loggerFactory.CreateLogger("ValidationSet.Actor")

        let mutable currentCommand = String.Empty
        let mutable validationSet = ValidationSetDto.Default

        /// Stores the correlation id used by this actor while reporting timings and errors.
        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            validationSet <-
                state.State
                |> Seq.fold (fun dto event -> ValidationSetDto.UpdateDto event dto) validationSet

            Task.CompletedTask

        /// Applies one persisted ValidationSet event to this activation's in-memory state.
        member private this.ApplyEvent(validationSetEvent: ValidationSetEvent) =
            task {
                let correlationId = validationSetEvent.Metadata.CorrelationId

                try
                    state.State.Add(validationSetEvent)
                    do! state.WriteStateAsync()

                    validationSet <-
                        validationSet
                        |> ValidationSetDto.UpdateDto validationSetEvent

                    let graceEvent = GraceEvent.ValidationSetEvent validationSetEvent
                    do! publishGraceEvent graceEvent validationSetEvent.Metadata

                    let graceReturnValue: GraceReturnValue<string> =
                        (GraceReturnValue.Create "Validation set command succeeded." correlationId)
                            .enhance(nameof RepositoryId, validationSet.RepositoryId)
                            .enhance (nameof ValidationSetId, validationSet.ValidationSetId)

                    return Ok graceReturnValue
                with
                | ex ->
                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Failed to apply event for ValidationSetId: {ValidationSetId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId,
                        validationSet.ValidationSetId
                    )

                    return
                        Error(
                            (GraceError.CreateWithException ex "Failed while applying ValidationSet event." correlationId)
                                .enhance(nameof RepositoryId, validationSet.RepositoryId)
                                .enhance (nameof ValidationSetId, validationSet.ValidationSetId)
                        )
            }

        interface IHasRepositoryId with
            /// Returns the repository id recorded in this ValidationSet actor state.
            member this.GetRepositoryId correlationId = validationSet.RepositoryId |> returnTask

        interface IValidationSetActor with
            /// Reports whether this ValidationSet actor has persisted state.
            member this.Exists correlationId =
                this.correlationId <- correlationId

                not
                <| validationSet.ValidationSetId.Equals(ValidationSetId.Empty)
                |> returnTask

            /// Reports whether this ValidationSet actor state is marked logically deleted.
            member this.IsDeleted correlationId =
                this.correlationId <- correlationId
                validationSet.DeletedAt.IsSome |> returnTask

            /// Returns the current ValidationSet actor state snapshot.
            member this.Get correlationId =
                this.correlationId <- correlationId

                if validationSet.ValidationSetId = ValidationSetId.Empty then
                    Option.None
                else
                    Some validationSet
                |> returnTask

            /// Returns the persisted ValidationSet event stream for replay or audit.
            member this.GetEvents correlationId =
                this.correlationId <- correlationId

                state.State :> IReadOnlyList<ValidationSetEvent>
                |> returnTask

            /// Routes a public actor command to the domain operation that validates and persists it.
            member this.Handle command metadata =
                /// Checks whether command validation succeeded before emitting the domain event.
                let isValid (validationSetCommand: ValidationSetCommand) (eventMetadata: EventMetadata) =
                    task {
                        if state.State.Exists(fun ev -> ev.Metadata.CorrelationId = eventMetadata.CorrelationId) then
                            return Error(GraceError.Create "Duplicate correlation ID for ValidationSet command." eventMetadata.CorrelationId)
                        else
                            match validationSetCommand with
                            | ValidationSetCommand.Create _ when
                                validationSet.ValidationSetId
                                <> ValidationSetId.Empty
                                ->
                                return Error(GraceError.Create "ValidationSet already exists." eventMetadata.CorrelationId)
                            | _ -> return Ok validationSetCommand
                    }

                /// Runs ValidationSet command decisions, applies emitted events, and persists the result.
                let processCommand (validationSetCommand: ValidationSetCommand) (eventMetadata: EventMetadata) =
                    task {
                        let eventType =
                            match validationSetCommand with
                            | ValidationSetCommand.Create validationSetDto -> ValidationSetEventType.Created validationSetDto
                            | ValidationSetCommand.Update validationSetDto -> ValidationSetEventType.Updated validationSetDto
                            | ValidationSetCommand.DeleteLogical (force, deleteReason) -> ValidationSetEventType.LogicalDeleted(force, deleteReason)

                        let validationSetEvent: ValidationSetEvent = { Event = eventType; Metadata = eventMetadata }
                        return! this.ApplyEvent validationSetEvent
                    }

                task {
                    currentCommand <- getDiscriminatedUnionCaseName command
                    this.correlationId <- metadata.CorrelationId

                    match! isValid command metadata with
                    | Ok validCommand -> return! processCommand validCommand metadata
                    | Error validationError -> return Error validationError
                }
