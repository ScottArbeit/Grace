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

module ValidationResult =

    type ValidationResultActor
        (
            [<PersistentState(StateName.ValidationResult, Constants.GraceActorStorage)>] state: IPersistentState<List<ValidationResultEvent>>
        ) =
        inherit Grain()

        static let actorName = ActorName.ValidationResult
        let log = loggerFactory.CreateLogger("ValidationResult.Actor")

        let mutable currentCommand = String.Empty
        let mutable validationResult = ValidationResultDto.Default

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            validationResult <-
                state.State
                |> Seq.fold (fun dto event -> ValidationResultDto.UpdateDto event dto) validationResult

            Task.CompletedTask

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
            member this.GetRepositoryId correlationId = validationResult.RepositoryId |> returnTask

        interface IValidationResultActor with
            member this.Exists correlationId =
                this.correlationId <- correlationId

                not
                <| validationResult.ValidationResultId.Equals(ValidationResultId.Empty)
                |> returnTask

            member this.Get correlationId =
                this.correlationId <- correlationId

                if validationResult.ValidationResultId = ValidationResultId.Empty then
                    Option.None
                else
                    Some validationResult
                |> returnTask

            member this.GetEvents correlationId =
                this.correlationId <- correlationId

                state.State :> IReadOnlyList<ValidationResultEvent>
                |> returnTask

            member this.Handle command metadata =
                let isValid (validationResultCommand: ValidationResultCommand) (eventMetadata: EventMetadata) =
                    task {
                        if state.State.Exists(fun ev -> ev.Metadata.CorrelationId = eventMetadata.CorrelationId) then
                            return Error(GraceError.Create "Duplicate correlation ID for ValidationResult command." eventMetadata.CorrelationId)
                        else
                            match validationResultCommand with
                            | ValidationResultCommand.Record _ -> return Ok validationResultCommand
                    }

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
