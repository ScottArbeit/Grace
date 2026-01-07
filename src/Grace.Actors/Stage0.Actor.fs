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
open Grace.Types.Review
open Grace.Types.Types
open Microsoft.Extensions.Logging
open NodaTime
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

module Stage0 =

    let internal hasDuplicateCorrelationId (events: seq<Stage0Event>) (metadata: EventMetadata) =
        events
        |> Seq.exists (fun ev -> ev.Metadata.CorrelationId = metadata.CorrelationId)


    type Stage0Actor([<PersistentState(StateName.Stage0, Constants.GraceActorStorage)>] state: IPersistentState<List<Stage0Event>>) =
        inherit Grain()

        static let actorName = ActorName.Stage0

        let log = loggerFactory.CreateLogger("Stage0.Actor")

        let mutable currentCommand = String.Empty

        let mutable stage0Analysis = Stage0Analysis.Default

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            stage0Analysis <-
                state.State
                |> Seq.fold (fun dto ev -> Stage0AnalysisDto.UpdateDto ev dto) stage0Analysis

            Task.CompletedTask

        member private this.ApplyEvent(stage0Event: Stage0Event) =
            task {
                let correlationId = stage0Event.Metadata.CorrelationId

                try
                    state.State.Add(stage0Event)
                    do! state.WriteStateAsync()

                    stage0Analysis <- stage0Analysis |> Stage0AnalysisDto.UpdateDto stage0Event

                    let graceEvent = GraceEvent.Stage0Event stage0Event
                    do! publishGraceEvent graceEvent stage0Event.Metadata

                    let returnValue =
                        (GraceReturnValue.Create "Stage 0 analysis recorded." correlationId)
                            .enhance(nameof ReferenceId, stage0Analysis.ReferenceId)
                            .enhance(nameof Stage0AnalysisId, stage0Analysis.Stage0AnalysisId)
                            .enhance (nameof Stage0EventType, getDiscriminatedUnionFullName stage0Event.Event)

                    return Ok returnValue
                with ex ->
                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {hostName}; CorrelationId: {correlationId}; Failed to apply event {eventType} for Stage 0 analysis {stage0AnalysisId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId,
                        getDiscriminatedUnionCaseName stage0Event.Event,
                        stage0Analysis.Stage0AnalysisId
                    )

                    let graceError =
                        (GraceError.CreateWithException ex (Stage0Error.getErrorMessage Stage0Error.FailedWhileApplyingEvent) correlationId)
                            .enhance (nameof Stage0AnalysisId, stage0Analysis.Stage0AnalysisId)

                    return Error graceError
            }

        interface IHasRepositoryId with
            member this.GetRepositoryId correlationId = stage0Analysis.RepositoryId |> returnTask

        interface IStage0Actor with
            member this.Exists correlationId =
                this.correlationId <- correlationId
                (stage0Analysis.ReferenceId <> ReferenceId.Empty) |> returnTask

            member this.Get correlationId =
                this.correlationId <- correlationId

                if stage0Analysis.ReferenceId = ReferenceId.Empty then
                    None
                else
                    Some stage0Analysis
                |> returnTask

            member this.GetEvents correlationId =
                this.correlationId <- correlationId
                state.State :> IReadOnlyList<Stage0Event> |> returnTask

            member this.Handle command metadata =
                let isValid (command: Stage0Command) (metadata: EventMetadata) =
                    task {
                        if hasDuplicateCorrelationId state.State metadata then
                            return Error(GraceError.Create (Stage0Error.getErrorMessage Stage0Error.DuplicateCorrelationId) metadata.CorrelationId)
                        else
                            return Ok command
                    }

                let processCommand (command: Stage0Command) (metadata: EventMetadata) =
                    task {
                        let! (stage0EventType: Stage0EventType) =
                            task {
                                match command with
                                | Record stage0Analysis -> return Recorded stage0Analysis
                            }

                        let stage0Event: Stage0Event = { Event = stage0EventType; Metadata = metadata }
                        return! this.ApplyEvent stage0Event
                    }

                task {
                    currentCommand <- getDiscriminatedUnionCaseName command
                    this.correlationId <- metadata.CorrelationId
                    RequestContext.Set(Constants.CurrentCommandProperty, getDiscriminatedUnionCaseName command)

                    match! isValid command metadata with
                    | Ok command -> return! processCommand command metadata
                    | Error error -> return Error error
                }
