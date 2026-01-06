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

        let mutable reviewPacket: ReviewPacket option = None

        let mutable checkpoints: ReviewCheckpoint list = []

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            let applyToState (reviewEvent: ReviewEvent) =
                match reviewEvent.Event with
                | PacketUpserted packet ->
                    let createdAt =
                        if packet.CreatedAt = Constants.DefaultTimestamp then
                            reviewEvent.Metadata.Timestamp
                        else
                            packet.CreatedAt

                    let updatedPacket = { packet with CreatedAt = createdAt; UpdatedAt = Some reviewEvent.Metadata.Timestamp }

                    reviewPacket <- Some updatedPacket
                | FindingResolved(findingId, resolutionState, resolvedBy, note) ->
                    reviewPacket <-
                        reviewPacket
                        |> Option.map (fun packet ->
                            let updatedFindings =
                                packet.Findings
                                |> List.map (fun finding ->
                                    if finding.FindingId = findingId then
                                        { finding with
                                            ResolutionState = resolutionState
                                            ResolvedBy = Some resolvedBy
                                            ResolvedAt = Some reviewEvent.Metadata.Timestamp
                                            ResolutionNote = note }
                                    else
                                        finding)

                            { packet with Findings = updatedFindings; UpdatedAt = Some reviewEvent.Metadata.Timestamp })
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
                    | PacketUpserted packet ->
                        let createdAt =
                            if packet.CreatedAt = Constants.DefaultTimestamp then
                                reviewEvent.Metadata.Timestamp
                            else
                                packet.CreatedAt

                        let updatedPacket = { packet with CreatedAt = createdAt; UpdatedAt = Some reviewEvent.Metadata.Timestamp }

                        reviewPacket <- Some updatedPacket
                    | FindingResolved(findingId, resolutionState, resolvedBy, note) ->
                        reviewPacket <-
                            reviewPacket
                            |> Option.map (fun packet ->
                                let updatedFindings =
                                    packet.Findings
                                    |> List.map (fun finding ->
                                        if finding.FindingId = findingId then
                                            { finding with
                                                ResolutionState = resolutionState
                                                ResolvedBy = Some resolvedBy
                                                ResolvedAt = Some reviewEvent.Metadata.Timestamp
                                                ResolutionNote = note }
                                        else
                                            finding)

                                { packet with Findings = updatedFindings; UpdatedAt = Some reviewEvent.Metadata.Timestamp })
                    | CheckpointAdded checkpoint -> checkpoints <- checkpoints @ [ checkpoint ]

                    let graceEvent = GraceEvent.ReviewEvent reviewEvent
                    do! publishGraceEvent graceEvent reviewEvent.Metadata

                    let returnValue =
                        (GraceReturnValue.Create "Review command succeeded." correlationId)
                            .enhance(
                                nameof ReviewPacketId,
                                reviewPacket
                                |> Option.map (fun packet -> packet.ReviewPacketId)
                                |> Option.defaultValue (ReviewPacketId.Empty)
                            )
                            .enhance (nameof ReviewEventType, getDiscriminatedUnionFullName reviewEvent.Event)

                    return Ok returnValue
                with ex ->
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
                                nameof ReviewPacketId,
                                reviewPacket
                                |> Option.map (fun packet -> packet.ReviewPacketId)
                                |> Option.defaultValue (ReviewPacketId.Empty)
                            )

                    return Error graceError
            }

        interface IHasRepositoryId with
            member this.GetRepositoryId correlationId =
                let repositoryId =
                    reviewPacket
                    |> Option.map (fun packet -> packet.RepositoryId)
                    |> Option.defaultValue RepositoryId.Empty

                repositoryId |> returnTask

        interface IReviewActor with
            member this.GetPacket correlationId =
                this.correlationId <- correlationId
                reviewPacket |> returnTask

            member this.GetCheckpoints correlationId =
                this.correlationId <- correlationId
                (checkpoints :> IReadOnlyList<ReviewCheckpoint>) |> returnTask

            member this.Handle command metadata =
                let isValid (command: ReviewCommand) (metadata: EventMetadata) =
                    task {
                        if state.State.Exists(fun ev -> ev.Metadata.CorrelationId = metadata.CorrelationId) then
                            return Error(GraceError.Create (ReviewError.getErrorMessage ReviewError.DuplicateCorrelationId) metadata.CorrelationId)
                        else
                            match command with
                            | UpsertPacket _ -> return Ok command
                            | AddCheckpoint _ -> return Ok command
                            | ResolveFinding(findingId, _, _, _) ->
                                match reviewPacket with
                                | None ->
                                    return Error(GraceError.Create (ReviewError.getErrorMessage ReviewError.ReviewPacketDoesNotExist) metadata.CorrelationId)
                                | Some packet ->
                                    let exists = packet.Findings |> List.exists (fun finding -> finding.FindingId = findingId)

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
                                | UpsertPacket packet -> return PacketUpserted packet
                                | ResolveFinding(findingId, resolutionState, resolvedBy, note) ->
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
