namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types.Reminder
open Grace.Types.Types
open Grace.Types.UploadSession
open Microsoft.Extensions.Logging
open NodaTime
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

module UploadSession =

    let private actorName = ActorName.UploadSession

    let commandName command =
        match command with
        | UploadSessionCommand.Start _ -> "Start"
        | UploadSessionCommand.IssueDedupeDiscovery _ -> "IssueDedupeDiscovery"
        | UploadSessionCommand.RegisterBlockUploadIntent _ -> "RegisterBlockUploadIntent"
        | UploadSessionCommand.ConfirmBlockUploaded _ -> "ConfirmBlockUploaded"
        | UploadSessionCommand.ClaimReuseRanges _ -> "ClaimReuseRanges"
        | UploadSessionCommand.FinalizeManifest _ -> "FinalizeManifest"
        | UploadSessionCommand.Abandon _ -> "Abandon"
        | UploadSessionCommand.Expire _ -> "Expire"
        | UploadSessionCommand.DeletePhysicalState _ -> "DeletePhysicalState"

    let operationId command =
        match command with
        | UploadSessionCommand.Start start -> start.OperationId
        | UploadSessionCommand.IssueDedupeDiscovery operationId -> operationId
        | UploadSessionCommand.RegisterBlockUploadIntent (operationId, _) -> operationId
        | UploadSessionCommand.ConfirmBlockUploaded (operationId, _) -> operationId
        | UploadSessionCommand.ClaimReuseRanges operationId -> operationId
        | UploadSessionCommand.FinalizeManifest (operationId, _) -> operationId
        | UploadSessionCommand.Abandon operationId -> operationId
        | UploadSessionCommand.Expire operationId -> operationId
        | UploadSessionCommand.DeletePhysicalState operationId -> operationId

    let createCleanupOperationId operationId = $"{operationId}:cleanup"

    let createCleanupReminderState uploadSessionId repositoryId operationId correlationId =
        {
            UploadSessionId = uploadSessionId
            RepositoryId = repositoryId
            OperationId = createCleanupOperationId operationId
            DeleteReason = "UploadSession coordination state retention window elapsed."
            CorrelationId = correlationId
        }

    let private eventOperationId uploadSessionEvent =
        match uploadSessionEvent.Event with
        | UploadSessionEventType.Started start -> start.OperationId
        | UploadSessionEventType.Abandoned operationId -> operationId
        | UploadSessionEventType.Expired operationId -> operationId
        | UploadSessionEventType.Finalized (operationId, _) -> operationId
        | UploadSessionEventType.CleanupReminderScheduled (operationId, _) -> operationId
        | UploadSessionEventType.PhysicalStateDeleted operationId -> operationId

    let private hasAppliedOperationId (events: seq<UploadSessionEvent>) operationId =
        events
        |> Seq.exists (fun uploadSessionEvent -> eventOperationId uploadSessionEvent = operationId)

    let private applyEvents (events: UploadSessionEvent list) (session: UploadSessionDto) =
        events
        |> List.fold (fun current event -> UploadSessionDto.UpdateDto event current) session

    let private okDecision (session: UploadSessionDto) operationId (events: UploadSessionEvent list) wasReplay message =
        Ok { Session = applyEvents events session; OperationId = operationId; Events = events; WasIdempotentReplay = wasReplay; Message = message }

    let private graceError correlationId message = GraceError.Create message correlationId

    let private cleanupEvents (session: UploadSessionDto) operationId (metadata: EventMetadata) =
        let cleanupOperationId = createCleanupOperationId operationId
        let reminderTime = metadata.Timestamp.Plus(DefaultPhysicalDeletionReminderDuration)

        [
            { Event = UploadSessionEventType.CleanupReminderScheduled(cleanupOperationId, reminderTime); Metadata = metadata }
        ]

    let private terminalMutationError (session: UploadSessionDto) command correlationId =
        match session.LifecycleState with
        | UploadSessionLifecycleState.Finalized -> Some(graceError correlationId $"UploadSession is finalized and cannot be changed by {commandName command}.")
        | UploadSessionLifecycleState.RetentionPending ->
            Some(graceError correlationId $"UploadSession is waiting for cleanup and cannot be changed by {commandName command}.")
        | UploadSessionLifecycleState.StateDeleted ->
            Some(graceError correlationId $"UploadSession physical state has been deleted and cannot be changed by {commandName command}.")
        | _ -> None

    let decideCommand (events: seq<UploadSessionEvent>) (session: UploadSessionDto) (command: UploadSessionCommand) (metadata: EventMetadata) =
        let operationId = operationId command

        if String.IsNullOrWhiteSpace operationId then
            Error(graceError metadata.CorrelationId "UploadSession command requires a non-empty operation id.")
        elif hasAppliedOperationId events operationId then
            okDecision session operationId [] true "Upload session command replayed."
        else
            match command with
            | UploadSessionCommand.Start start ->
                if session.LifecycleState
                   <> UploadSessionLifecycleState.NotStarted then
                    Error(graceError metadata.CorrelationId "UploadSession has already been started.")
                elif start.UploadSessionId = UploadSessionId.Empty then
                    Error(graceError metadata.CorrelationId "UploadSessionId must be a non-empty Guid.")
                elif start.RepositoryId = RepositoryId.Empty then
                    Error(graceError metadata.CorrelationId "RepositoryId must be a non-empty Guid.")
                elif start.ExpectedSize <= 0L then
                    Error(graceError metadata.CorrelationId "ExpectedSize must be greater than zero.")
                else
                    let events =
                        [
                            { Event = UploadSessionEventType.Started start; Metadata = metadata }
                        ]

                    okDecision session operationId events false "Upload session started."
            | UploadSessionCommand.Abandon operationId ->
                match terminalMutationError session command metadata.CorrelationId with
                | Some error -> Error error
                | None ->
                    match session.LifecycleState with
                    | UploadSessionLifecycleState.Started
                    | UploadSessionLifecycleState.Discovering
                    | UploadSessionLifecycleState.UploadingBlocks
                    | UploadSessionLifecycleState.ClaimingRanges ->
                        let events =
                            [
                                { Event = UploadSessionEventType.Abandoned operationId; Metadata = metadata }
                            ]
                            @ cleanupEvents session operationId metadata

                        okDecision session operationId events false "Upload session abandoned."
                    | UploadSessionLifecycleState.NotStarted -> Error(graceError metadata.CorrelationId "UploadSession must be started before Abandon.")
                    | _ -> Error(graceError metadata.CorrelationId $"UploadSession cannot Abandon from {session.LifecycleState}.")
            | UploadSessionCommand.Expire operationId ->
                match terminalMutationError session command metadata.CorrelationId with
                | Some error -> Error error
                | None ->
                    match session.LifecycleState with
                    | UploadSessionLifecycleState.Started
                    | UploadSessionLifecycleState.Discovering
                    | UploadSessionLifecycleState.UploadingBlocks
                    | UploadSessionLifecycleState.ClaimingRanges ->
                        let events =
                            [
                                { Event = UploadSessionEventType.Expired operationId; Metadata = metadata }
                            ]
                            @ cleanupEvents session operationId metadata

                        okDecision session operationId events false "Upload session expired."
                    | UploadSessionLifecycleState.NotStarted -> Error(graceError metadata.CorrelationId "UploadSession must be started before Expire.")
                    | _ -> Error(graceError metadata.CorrelationId $"UploadSession cannot Expire from {session.LifecycleState}.")
            | UploadSessionCommand.DeletePhysicalState operationId ->
                match session.LifecycleState with
                | UploadSessionLifecycleState.NotStarted
                | UploadSessionLifecycleState.StateDeleted -> okDecision session operationId [] true "Upload session physical state was already deleted."
                | UploadSessionLifecycleState.RetentionPending ->
                    let events =
                        [
                            { Event = UploadSessionEventType.PhysicalStateDeleted operationId; Metadata = metadata }
                        ]

                    okDecision session operationId events false "Upload session physical state deleted."
                | _ -> Error(graceError metadata.CorrelationId $"UploadSession cannot DeletePhysicalState from {session.LifecycleState}.")
            | UploadSessionCommand.IssueDedupeDiscovery _
            | UploadSessionCommand.RegisterBlockUploadIntent _
            | UploadSessionCommand.ConfirmBlockUploaded _
            | UploadSessionCommand.ClaimReuseRanges _
            | UploadSessionCommand.FinalizeManifest _ ->
                match terminalMutationError session command metadata.CorrelationId with
                | Some error -> Error error
                | None -> Error(graceError metadata.CorrelationId $"UploadSession command {commandName command} is not implemented in this skeleton.")

    type UploadSessionActor([<PersistentState(StateName.UploadSession, Constants.GraceActorStorage)>] state: IPersistentState<List<UploadSessionEvent>>) =
        inherit Grain()

        let log = loggerFactory.CreateLogger("UploadSession.Actor")
        let mutable uploadSessionDto = UploadSessionDto.Default
        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            uploadSessionDto <-
                state.State
                |> Seq.fold (fun dto event -> UploadSessionDto.UpdateDto event dto) UploadSessionDto.Default

            Task.CompletedTask

        member private this.ApplyEvents(events: UploadSessionEvent list) =
            task {
                for uploadSessionEvent in events do
                    state.State.Add(uploadSessionEvent)

                do! state.WriteStateAsync()

                uploadSessionDto <- applyEvents events uploadSessionDto
            }

        interface IGraceReminderWithGuidKey with
            member this.ScheduleReminderAsync reminderType delay reminderState correlationId =
                task {
                    let reminder =
                        ReminderDto.Create
                            actorName
                            $"{this.IdentityString}"
                            uploadSessionDto.OwnerId
                            uploadSessionDto.OrganizationId
                            uploadSessionDto.RepositoryId
                            reminderType
                            (getFutureInstant delay)
                            reminderState
                            correlationId

                    do! createReminder reminder
                }
                :> Task

            member this.ReceiveReminderAsync(reminder: ReminderDto) : Task<Result<unit, GraceError>> =
                task {
                    this.correlationId <- reminder.CorrelationId

                    match reminder.ReminderType, reminder.State with
                    | ReminderTypes.PhysicalDeletion, ReminderState.UploadSessionPhysicalDeletion reminderState ->
                        this.correlationId <- reminderState.CorrelationId

                        let metadata = EventMetadata.New reminderState.CorrelationId "system"
                        let command = UploadSessionCommand.DeletePhysicalState reminderState.OperationId

                        match decideCommand state.State uploadSessionDto command metadata with
                        | Ok decision ->
                            if not decision.Events.IsEmpty then do! this.ApplyEvents decision.Events

                            do! state.ClearStateAsync()
                            this.DeactivateOnIdle()
                            return Ok()
                        | Error error -> return Error error
                    | reminderType, reminderState ->
                        return
                            Error(
                                GraceError.Create
                                    $"{actorName} does not process reminder type {getDiscriminatedUnionCaseName reminderType} with state {getDiscriminatedUnionCaseName reminderState}."
                                    this.correlationId
                            )
                }

        interface IUploadSessionActor with
            member this.Exists correlationId =
                this.correlationId <- correlationId

                (uploadSessionDto.UploadSessionId
                 <> UploadSessionId.Empty)
                |> returnTask

            member this.Get correlationId =
                this.correlationId <- correlationId
                uploadSessionDto |> returnTask

            member this.GetEvents correlationId =
                this.correlationId <- correlationId

                (state.State :> IReadOnlyList<UploadSessionEvent>)
                |> returnTask

            member this.Handle command metadata =
                task {
                    this.correlationId <- metadata.CorrelationId
                    RequestContext.Set(Constants.CurrentCommandProperty, commandName command)

                    match decideCommand state.State uploadSessionDto command metadata with
                    | Ok decision ->
                        if not decision.Events.IsEmpty then do! this.ApplyEvents decision.Events

                        match command with
                        | UploadSessionCommand.Abandon operationId when not decision.WasIdempotentReplay ->
                            let reminderState =
                                createCleanupReminderState decision.Session.UploadSessionId decision.Session.RepositoryId operationId metadata.CorrelationId

                            do!
                                (this :> IGraceReminderWithGuidKey)
                                    .ScheduleReminderAsync
                                    ReminderTypes.PhysicalDeletion
                                    DefaultPhysicalDeletionReminderDuration
                                    (ReminderState.UploadSessionPhysicalDeletion reminderState)
                                    metadata.CorrelationId
                        | UploadSessionCommand.Expire operationId when not decision.WasIdempotentReplay ->
                            let reminderState =
                                createCleanupReminderState decision.Session.UploadSessionId decision.Session.RepositoryId operationId metadata.CorrelationId

                            do!
                                (this :> IGraceReminderWithGuidKey)
                                    .ScheduleReminderAsync
                                    ReminderTypes.PhysicalDeletion
                                    DefaultPhysicalDeletionReminderDuration
                                    (ReminderState.UploadSessionPhysicalDeletion reminderState)
                                    metadata.CorrelationId
                        | UploadSessionCommand.DeletePhysicalState _ when not decision.WasIdempotentReplay ->
                            do! state.ClearStateAsync()
                            this.DeactivateOnIdle()
                        | _ -> ()

                        let returnValue =
                            (GraceReturnValue.Create decision metadata.CorrelationId)
                                .enhance(nameof RepositoryId, decision.Session.RepositoryId)
                                .enhance(nameof UploadSessionId, decision.Session.UploadSessionId)
                                .enhance(nameof UploadSessionOperationId, decision.OperationId)
                                .enhance (nameof UploadSessionLifecycleState, decision.Session.LifecycleState)

                        return Ok returnValue
                    | Error error ->
                        log.LogWarning(
                            "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Rejected UploadSession command {Command}. Error: {Error}",
                            getCurrentInstantExtended (),
                            getMachineName,
                            metadata.CorrelationId,
                            commandName command,
                            error.Error
                        )

                        return Error error
                }
