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
open Grace.Types.Types
open Grace.Types.Validation
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

module ValidationSet =

    type ValidationSetActor([<PersistentState(StateName.ValidationSet, Constants.GraceActorStorage)>] state: IPersistentState<List<ValidationSetEvent>>) =
        inherit Grain()

        static let actorName = ActorName.ValidationSet
        let log = loggerFactory.CreateLogger("ValidationSet.Actor")

        let mutable currentCommand = String.Empty
        let mutable validationSet = ValidationSetDto.Default

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            validationSet <-
                state.State
                |> Seq.fold (fun dto event -> ValidationSetDto.UpdateDto event dto) validationSet

            Task.CompletedTask

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
            member this.GetRepositoryId correlationId = validationSet.RepositoryId |> returnTask

        interface IValidationSetActor with
            member this.Exists correlationId =
                this.correlationId <- correlationId

                not
                <| validationSet.ValidationSetId.Equals(ValidationSetId.Empty)
                |> returnTask

            member this.IsDeleted correlationId =
                this.correlationId <- correlationId
                validationSet.DeletedAt.IsSome |> returnTask

            member this.Get correlationId =
                this.correlationId <- correlationId

                if validationSet.ValidationSetId = ValidationSetId.Empty then
                    Option.None
                else
                    Some validationSet
                |> returnTask

            member this.GetEvents correlationId =
                this.correlationId <- correlationId

                state.State :> IReadOnlyList<ValidationSetEvent>
                |> returnTask

            member this.Handle command metadata =
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
