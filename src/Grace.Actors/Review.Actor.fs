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

module Review =

    type ReviewActor([<PersistentState(StateName.Review, Constants.GraceActorStorage)>] state: IPersistentState<List<ReviewEvent>>) =
        inherit Grain()

        static let actorName = ActorName.Review

        let log = loggerFactory.CreateLogger("Review.Actor")

        let mutable currentCommand = String.Empty

        let mutable reviewNotes: ReviewNotes option = None

        let mutable checkpoints: ReviewCheckpoint list = []

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            let applyToState (reviewEvent: ReviewEvent) =
                match reviewEvent.Event with
                | NotesUpserted notes ->
                    let createdAt =
                        if notes.CreatedAt = Constants.DefaultTimestamp then
                            reviewEvent.Metadata.Timestamp
                        else
                            notes.CreatedAt

                    let updatedNotes = { notes with CreatedAt = createdAt; UpdatedAt = Some reviewEvent.Metadata.Timestamp }

                    reviewNotes <- Some updatedNotes
                | FindingResolved (findingId, resolutionState, resolvedBy, note) ->
                    reviewNotes <-
                        reviewNotes
                        |> Option.map (fun notes ->
                            let updatedFindings =
                                notes.Findings
                                |> List.map (fun finding ->
                                    if finding.FindingId = findingId then
                                        { finding with
                                            ResolutionState = resolutionState
                                            ResolvedBy = Some resolvedBy
                                            ResolvedAt = Some reviewEvent.Metadata.Timestamp
                                            ResolutionNote = note
                                        }
                                    else
                                        finding)

                            { notes with Findings = updatedFindings; UpdatedAt = Some reviewEvent.Metadata.Timestamp })
                | CheckpointAdded checkpoint -> checkpoints <- checkpoints @ [ checkpoint ]

            state.State |> Seq.iter applyToState

            Task.CompletedTask

        member private this.ApplyEvent(reviewEvent: ReviewEvent) =
            task {
                let correlationId = reviewEvent.Metadata.CorrelationId

                try
                    state.State.Add(reviewEvent)
                    do! state.WriteStateAsync()

                    match reviewEvent.Event with
                    | NotesUpserted notes ->
                        let createdAt =
                            if notes.CreatedAt = Constants.DefaultTimestamp then
                                reviewEvent.Metadata.Timestamp
                            else
                                notes.CreatedAt

                        let updatedNotes = { notes with CreatedAt = createdAt; UpdatedAt = Some reviewEvent.Metadata.Timestamp }

                        reviewNotes <- Some updatedNotes
                    | FindingResolved (findingId, resolutionState, resolvedBy, note) ->
                        reviewNotes <-
                            reviewNotes
                            |> Option.map (fun notes ->
                                let updatedFindings =
                                    notes.Findings
                                    |> List.map (fun finding ->
                                        if finding.FindingId = findingId then
                                            { finding with
                                                ResolutionState = resolutionState
                                                ResolvedBy = Some resolvedBy
                                                ResolvedAt = Some reviewEvent.Metadata.Timestamp
                                                ResolutionNote = note
                                            }
                                        else
                                            finding)

                                { notes with Findings = updatedFindings; UpdatedAt = Some reviewEvent.Metadata.Timestamp })
                    | CheckpointAdded checkpoint -> checkpoints <- checkpoints @ [ checkpoint ]

                    let graceEvent = GraceEvent.ReviewEvent reviewEvent
                    do! publishGraceEvent graceEvent reviewEvent.Metadata

                    let returnValue =
                        (GraceReturnValue.Create "Review command succeeded." correlationId)
                            .enhance(
                                nameof ReviewNotesId,
                                reviewNotes
                                |> Option.map (fun notes -> notes.ReviewNotesId)
                                |> Option.defaultValue (ReviewNotesId.Empty)
                            )
                            .enhance (nameof ReviewEventType, getDiscriminatedUnionFullName reviewEvent.Event)

                    return Ok returnValue
                with
                | ex ->
                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {hostName}; CorrelationId: {correlationId}; Failed to apply event {eventType} for review.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId,
                        getDiscriminatedUnionCaseName reviewEvent.Event
                    )

                    let graceError =
                        (GraceError.CreateWithException ex (ReviewError.getErrorMessage ReviewError.FailedWhileApplyingEvent) correlationId)
                            .enhance (
                                nameof ReviewNotesId,
                                reviewNotes
                                |> Option.map (fun notes -> notes.ReviewNotesId)
                                |> Option.defaultValue (ReviewNotesId.Empty)
                            )

                    return Error graceError
            }

        interface IHasRepositoryId with
            member this.GetRepositoryId correlationId =
                let repositoryId =
                    reviewNotes
                    |> Option.map (fun notes -> notes.RepositoryId)
                    |> Option.defaultValue RepositoryId.Empty

                repositoryId |> returnTask

        interface IReviewActor with
            member this.GetNotes correlationId =
                this.correlationId <- correlationId
                reviewNotes |> returnTask

            member this.GetCheckpoints correlationId =
                this.correlationId <- correlationId

                (checkpoints :> IReadOnlyList<ReviewCheckpoint>)
                |> returnTask

            member this.GetEvents correlationId =
                this.correlationId <- correlationId

                state.State :> IReadOnlyList<ReviewEvent>
                |> returnTask

            member this.Handle command metadata =
                let isValid (command: ReviewCommand) (metadata: EventMetadata) =
                    task {
                        if state.State.Exists(fun ev -> ev.Metadata.CorrelationId = metadata.CorrelationId) then
                            return Error(GraceError.Create (ReviewError.getErrorMessage ReviewError.DuplicateCorrelationId) metadata.CorrelationId)
                        else
                            match command with
                            | UpsertNotes _ -> return Ok command
                            | AddCheckpoint _ -> return Ok command
                            | ResolveFinding (findingId, _, _, _) ->
                                match reviewNotes with
                                | None ->
                                    return Error(GraceError.Create (ReviewError.getErrorMessage ReviewError.ReviewNotesDoesNotExist) metadata.CorrelationId)
                                | Some notes ->
                                    let exists =
                                        notes.Findings
                                        |> List.exists (fun finding -> finding.FindingId = findingId)

                                    if exists then
                                        return Ok command
                                    else
                                        return Error(GraceError.Create (ReviewError.getErrorMessage ReviewError.FindingDoesNotExist) metadata.CorrelationId)
                    }

                let processCommand (command: ReviewCommand) (metadata: EventMetadata) =
                    task {
                        let! reviewEventType =
                            task {
                                match command with
                                | UpsertNotes notes -> return NotesUpserted notes
                                | ResolveFinding (findingId, resolutionState, resolvedBy, note) ->
                                    return FindingResolved(findingId, resolutionState, resolvedBy, note)
                                | AddCheckpoint checkpoint -> return CheckpointAdded checkpoint
                            }

                        let reviewEvent = { Event = reviewEventType; Metadata = metadata }
                        return! this.ApplyEvent reviewEvent
                    }

                task {
                    currentCommand <- getDiscriminatedUnionCaseName command
                    this.correlationId <- metadata.CorrelationId
                    RequestContext.Set(Constants.CurrentCommandProperty, getDiscriminatedUnionCaseName command)

                    match! isValid command metadata with
                    | Ok command -> return! processCommand command metadata
                    | Error error -> return Error error
                }
