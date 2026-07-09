namespace Grace.Server

open Giraffe
open Grace.Actors.Constants
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Server.ApplicationContext
open Grace.Server.Services
open Grace.Shared
open Grace.Shared.Extensions
open Grace.Shared.Parameters.Queue
open Grace.Shared.Validation.Common
open Grace.Shared.Validation.Errors
open Grace.Shared.Validation.Utilities
open Grace.Types.RequiredAction
open Grace.Types.Policy
open Grace.Types.Queue
open Grace.Types.Common
open Grace.Shared.Utilities
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Collections.Generic
open System.Diagnostics
open System.Threading.Tasks

/// Contains Grace Server queue behavior and supporting helpers.
module Queue =
    /// Represents validations used by Grace Server APIs and background services.
    type Validations<'T when 'T :> QueueParameters> = 'T -> ValueTask<Result<unit, QueueError>> array

    let log = ApplicationContext.loggerFactory.CreateLogger("Queue.Server")

    let activitySource = new ActivitySource("Queue")

    /// Loads a PromotionSet only when the current caller may observe its private workflow state.
    let private tryGetObservablePromotionSet (context: HttpContext) repositoryId correlationId promotionSetId =
        task {
            let promotionSetActorProxy = PromotionSet.CreateActorProxy promotionSetId repositoryId correlationId
            let! promotionSet = promotionSetActorProxy.Get correlationId

            if promotionSet.PromotionSetId = PromotionSetId.Empty then
                return None
            else
                let! canObserve = Grace.Server.PromotionSet.canObservePromotionSet context promotionSet

                if canObserve then return Some promotionSet else return None
        }

    /// Projects queue status from the caller-visible PromotionSet set so hidden private work cannot affect public shape.
    let internal projectObservableQueueStatus
        (queue: PromotionQueue)
        (observablePromotionSetIds: PromotionSetId list)
        (observableRunningPromotionSetId: PromotionSetId option)
        =
        let allAffectedWorkVisible =
            let queuedIds = if isNull (box queue.PromotionSetIds) then [] else queue.PromotionSetIds

            let queuedWorkVisible =
                queuedIds.Length = observablePromotionSetIds.Length
                && queuedIds
                   |> List.forall (fun promotionSetId ->
                       observablePromotionSetIds
                       |> List.contains promotionSetId)

            let runningWorkVisible =
                match queue.RunningPromotionSetId with
                | Some promotionSetId -> observableRunningPromotionSetId = Some promotionSetId
                | None -> true

            queuedWorkVisible && runningWorkVisible

        let visibleState =
            match observableRunningPromotionSetId with
            | Some _ -> QueueState.Running
            | None when observablePromotionSetIds.IsEmpty -> QueueState.Idle
            | None when allAffectedWorkVisible ->
                match queue.State with
                | QueueState.Paused -> QueueState.Paused
                | QueueState.Degraded -> QueueState.Degraded
                | _ -> QueueState.Idle
            | None -> QueueState.Idle

        let visibleUpdatedAt =
            match visibleState with
            | QueueState.Idle when observableRunningPromotionSetId.IsNone -> None
            | QueueState.Paused
            | QueueState.Degraded when allAffectedWorkVisible -> if isNull (box queue.UpdatedAt) then None else queue.UpdatedAt
            | QueueState.Running -> if isNull (box queue.UpdatedAt) then None else queue.UpdatedAt
            | _ -> None

        if observablePromotionSetIds.IsEmpty
           && observableRunningPromotionSetId.IsNone then
            PromotionQueue.Default
        else
            { queue with
                PromotionSetIds = observablePromotionSetIds
                RunningPromotionSetId = observableRunningPromotionSetId
                State = visibleState
                UpdatedAt = visibleUpdatedAt
            }

    /// Decides whether a queue route may mutate state from caller-visible work.
    let internal hasCallerVisibleQueueWork (observablePromotionSetIds: PromotionSetId list) (observableRunningPromotionSetId: PromotionSetId option) =
        not observablePromotionSetIds.IsEmpty
        || observableRunningPromotionSetId.IsSome

    /// Requires every queued or running PromotionSet to be observable before a queue-wide mutation can run.
    let internal allAffectedQueueWorkVisible
        (queue: PromotionQueue)
        (observablePromotionSetIds: PromotionSetId list)
        (observableRunningPromotionSetId: PromotionSetId option)
        =
        let queuedIds = if isNull (box queue.PromotionSetIds) then [] else queue.PromotionSetIds

        let queuedWorkVisible =
            queuedIds.Length = observablePromotionSetIds.Length
            && queuedIds
               |> List.forall (fun promotionSetId ->
                   observablePromotionSetIds
                   |> List.contains promotionSetId)

        let runningWorkVisible =
            match queue.RunningPromotionSetId with
            | Some promotionSetId -> observableRunningPromotionSetId = Some promotionSetId
            | None -> true

        queuedWorkVisible && runningWorkVisible

    /// Decides whether pause or resume can mutate the whole queue without touching hidden work.
    let internal canMutateCallerVisibleQueueWork
        (queue: PromotionQueue)
        (observablePromotionSetIds: PromotionSetId list)
        (observableRunningPromotionSetId: PromotionSetId option)
        =
        hasCallerVisibleQueueWork observablePromotionSetIds observableRunningPromotionSetId
        && allAffectedQueueWorkVisible queue observablePromotionSetIds observableRunningPromotionSetId

    /// Creates the stable no-op result for pause and resume when the caller has no visible queue work.
    let internal noObservableQueueWorkReturnValue commandName correlationId =
        GraceReturnValue.Create $"{commandName} skipped because no caller-visible queue work exists." correlationId

    /// Builds caller-visible queue evidence before a pause or resume route mutates branch queue state.
    let private getObservableQueueWork context repositoryId correlationId (queue: PromotionQueue) =
        task {
            let promotionSetIds = if isNull (box queue.PromotionSetIds) then [] else queue.PromotionSetIds
            let observablePromotionSetIds = ResizeArray<PromotionSetId>()
            let promotionSetIdArray = promotionSetIds |> List.toArray
            let mutable index = 0

            while index < promotionSetIdArray.Length do
                let promotionSetId = promotionSetIdArray[index]
                let! promotionSet = tryGetObservablePromotionSet context repositoryId correlationId promotionSetId

                if promotionSet.IsSome then observablePromotionSetIds.Add promotionSetId

                index <- index + 1

            let! observableRunningPromotionSetId =
                if isNull (box queue.RunningPromotionSetId) then
                    Task.FromResult None
                else
                    match queue.RunningPromotionSetId with
                    | Some promotionSetId ->
                        task {
                            let! promotionSet = tryGetObservablePromotionSet context repositoryId correlationId promotionSetId

                            return
                                promotionSet
                                |> Option.map (fun _ -> promotionSetId)
                        }
                    | None -> Task.FromResult None

            return observablePromotionSetIds |> Seq.toList, observableRunningPromotionSetId
        }

    /// Copies private PromotionSet visibility facts onto queue automation metadata.
    let private inheritPromotionSetVisibilityMetadata (metadata: EventMetadata) (promotionSet: Grace.Types.PromotionSet.PromotionSetDto) =
        metadata.Properties[ "InheritedVisibility" ] <- $"{promotionSet.Visibility}"
        metadata.Properties[ "InheritedOwnership" ] <- $"{promotionSet.Ownership}"

        promotionSet.CreatorUserId
        |> Option.iter (fun creatorUserId -> metadata.Properties[ "InheritedCreatorUserId" ] <- $"{creatorUserId}")

        metadata

    /// Implements requires policy snapshot for initialization for the server request pipeline.
    let internal requiresPolicySnapshotForInitialization (queueExists: bool) (policySnapshotId: string) =
        not queueExists
        && String.IsNullOrEmpty(policySnapshotId)

    /// Coordinates process command processing for Grace Server.
    let processCommand<'T when 'T :> QueueParameters> (context: HttpContext) (validations: Validations<'T>) (command: 'T -> ValueTask<PromotionQueueCommand>) =
        task {
            let commandName = context.Items["Command"] :?> string
            let graceIds = getGraceIds context
            let correlationId = getCorrelationId context
            let parameterDictionary = Dictionary<string, obj>()

            try
                use activity = activitySource.StartActivity("processCommand", ActivityKind.Server)
                let! parameters = context |> parse<'T>
                parameterDictionary.AddRange(getParametersAsDictionary parameters)

                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                /// Coordinates handle command processing for Grace Server.
                let handleCommand targetBranchId cmd =
                    task {
                        let actorProxy = PromotionQueue.CreateActorProxy targetBranchId graceIds.RepositoryId correlationId
                        let metadata = createMetadata context

                        match! actorProxy.Handle cmd metadata with
                        | Ok graceReturnValue ->
                            graceReturnValue
                                .enhance(parameterDictionary)
                                .enhance(nameof OwnerId, graceIds.OwnerId)
                                .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                .enhance(nameof BranchId, targetBranchId)
                                .enhance("Command", commandName)
                                .enhance ("Path", context.Request.Path.Value)
                            |> ignore

                            return! context |> result200Ok graceReturnValue
                        | Error graceError ->
                            graceError
                                .enhance(parameterDictionary)
                                .enhance(nameof OwnerId, graceIds.OwnerId)
                                .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                .enhance(nameof BranchId, targetBranchId)
                                .enhance("Command", commandName)
                                .enhance ("Path", context.Request.Path.Value)
                            |> ignore

                            return! context |> result400BadRequest graceError
                    }

                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    let! cmd = command parameters
                    let targetBranchId = Guid.Parse(parameters.TargetBranchId)
                    return! handleCommand targetBranchId cmd
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = QueueError.getErrorMessage error

                    let graceError =
                        (GraceError.Create errorMessage correlationId)
                            .enhance(parameterDictionary)
                            .enhance(nameof OwnerId, graceIds.OwnerId)
                            .enhance(nameof OrganizationId, graceIds.OrganizationId)
                            .enhance(nameof RepositoryId, graceIds.RepositoryId)
                            .enhance("Command", commandName)
                            .enhance ("Path", context.Request.Path.Value)

                    return! context |> result400BadRequest graceError
            with
            | ex ->
                log.LogError(
                    ex,
                    "{CurrentInstant}: Exception in Queue.Server.processCommand. CorrelationId: {correlationId}.",
                    getCurrentInstantExtended (),
                    correlationId
                )

                let graceError =
                    (GraceError.CreateWithException ex String.Empty correlationId)
                        .enhance(parameterDictionary)
                        .enhance(nameof OwnerId, graceIds.OwnerId)
                        .enhance(nameof OrganizationId, graceIds.OrganizationId)
                        .enhance(nameof RepositoryId, graceIds.RepositoryId)
                        .enhance ("Path", context.Request.Path.Value)

                return! context |> result500ServerError graceError
        }

    /// Coordinates process command with parameters processing for Grace Server.
    let processCommandWithParameters<'T when 'T :> QueueParameters>
        (context: HttpContext)
        (parameters: 'T)
        (validations: Validations<'T>)
        (command: 'T -> ValueTask<PromotionQueueCommand>)
        =
        task {
            let commandName = context.Items["Command"] :?> string
            let graceIds = getGraceIds context
            let correlationId = getCorrelationId context
            let parameterDictionary = Dictionary<string, obj>()

            try
                use activity = activitySource.StartActivity("processCommandWithParameters", ActivityKind.Server)
                parameterDictionary.AddRange(getParametersAsDictionary parameters)

                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                /// Coordinates handle command processing for Grace Server.
                let handleCommand targetBranchId cmd =
                    task {
                        let actorProxy = PromotionQueue.CreateActorProxy targetBranchId graceIds.RepositoryId correlationId
                        let metadata = createMetadata context

                        match! actorProxy.Handle cmd metadata with
                        | Ok graceReturnValue ->
                            graceReturnValue
                                .enhance(parameterDictionary)
                                .enhance(nameof OwnerId, graceIds.OwnerId)
                                .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                .enhance(nameof BranchId, targetBranchId)
                                .enhance("Command", commandName)
                                .enhance ("Path", context.Request.Path.Value)
                            |> ignore

                            return! context |> result200Ok graceReturnValue
                        | Error graceError ->
                            graceError
                                .enhance(parameterDictionary)
                                .enhance(nameof OwnerId, graceIds.OwnerId)
                                .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                .enhance(nameof BranchId, targetBranchId)
                                .enhance("Command", commandName)
                                .enhance ("Path", context.Request.Path.Value)
                            |> ignore

                            return! context |> result400BadRequest graceError
                    }

                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    let! cmd = command parameters
                    let targetBranchId = Guid.Parse(parameters.TargetBranchId)
                    return! handleCommand targetBranchId cmd
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = QueueError.getErrorMessage error

                    let graceError =
                        (GraceError.Create errorMessage correlationId)
                            .enhance(parameterDictionary)
                            .enhance(nameof OwnerId, graceIds.OwnerId)
                            .enhance(nameof OrganizationId, graceIds.OrganizationId)
                            .enhance(nameof RepositoryId, graceIds.RepositoryId)
                            .enhance("Command", commandName)
                            .enhance ("Path", context.Request.Path.Value)

                    return! context |> result400BadRequest graceError
            with
            | ex ->
                log.LogError(
                    ex,
                    "{CurrentInstant}: Exception in Queue.Server.processCommandWithParameters. CorrelationId: {correlationId}.",
                    getCurrentInstantExtended (),
                    correlationId
                )

                let graceError =
                    (GraceError.CreateWithException ex String.Empty correlationId)
                        .enhance(parameterDictionary)
                        .enhance(nameof OwnerId, graceIds.OwnerId)
                        .enhance(nameof OrganizationId, graceIds.OrganizationId)
                        .enhance(nameof RepositoryId, graceIds.RepositoryId)
                        .enhance ("Path", context.Request.Path.Value)

                return! context |> result500ServerError graceError
        }

    /// Coordinates process query processing for Grace Server.
    let processQuery<'T, 'U when 'T :> QueueParameters>
        (context: HttpContext)
        (parameters: 'T)
        (validations: Validations<'T>)
        (query: QueryResult<IPromotionQueueActor, 'U>)
        =
        task {
            use activity = activitySource.StartActivity("processQuery", ActivityKind.Server)
            let graceIds = getGraceIds context
            let correlationId = getCorrelationId context
            let parameterDictionary = getParametersAsDictionary parameters

            try
                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    let targetBranchId = Guid.Parse(parameters.TargetBranchId)
                    let actorProxy = PromotionQueue.CreateActorProxy targetBranchId graceIds.RepositoryId correlationId
                    let! queryResult = query context 0 actorProxy

                    let graceReturnValue =
                        (GraceReturnValue.Create queryResult correlationId)
                            .enhance(parameterDictionary)
                            .enhance(nameof OwnerId, graceIds.OwnerId)
                            .enhance(nameof OrganizationId, graceIds.OrganizationId)
                            .enhance(nameof RepositoryId, graceIds.RepositoryId)
                            .enhance(nameof BranchId, targetBranchId)
                            .enhance ("Path", context.Request.Path.Value)

                    return! context |> result200Ok graceReturnValue
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = QueueError.getErrorMessage error

                    let graceError =
                        (GraceError.Create errorMessage correlationId)
                            .enhance(parameterDictionary)
                            .enhance(nameof OwnerId, graceIds.OwnerId)
                            .enhance(nameof OrganizationId, graceIds.OrganizationId)
                            .enhance(nameof RepositoryId, graceIds.RepositoryId)
                            .enhance ("Path", context.Request.Path.Value)

                    return! context |> result400BadRequest graceError
            with
            | ex ->
                let graceError =
                    (GraceError.CreateWithException ex String.Empty correlationId)
                        .enhance(parameterDictionary)
                        .enhance(nameof OwnerId, graceIds.OwnerId)
                        .enhance(nameof OrganizationId, graceIds.OrganizationId)
                        .enhance(nameof RepositoryId, graceIds.RepositoryId)
                        .enhance ("Path", context.Request.Path.Value)

                return! context |> result500ServerError graceError
        }

    /// Returns queue status for a target branch.
    let Status: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context

                /// Implements validations for the server request pipeline.
                let validations (parameters: QueueStatusParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.TargetBranchId QueueError.InvalidTargetBranchId
                    |]

                let! parameters = context |> parse<QueueStatusParameters>
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                /// Implements query for the server request pipeline.
                let query (context: HttpContext) _ (actorProxy: IPromotionQueueActor) =
                    task {
                        let! queueJson = actorProxy.GetForRoute(getCorrelationId context)
                        let queue = deserialize<PromotionQueue> queueJson
                        let promotionSetIds = if isNull (box queue.PromotionSetIds) then [] else queue.PromotionSetIds
                        let observablePromotionSetIds = ResizeArray<PromotionSetId>()

                        for promotionSetId in promotionSetIds do
                            let! promotionSet = tryGetObservablePromotionSet context graceIds.RepositoryId (getCorrelationId context) promotionSetId

                            if promotionSet.IsSome then observablePromotionSetIds.Add promotionSetId

                        let! observableRunningPromotionSetId =
                            if isNull (box queue.RunningPromotionSetId) then
                                Task.FromResult None
                            else
                                match queue.RunningPromotionSetId with
                                | Some promotionSetId ->
                                    task {
                                        let! promotionSet = tryGetObservablePromotionSet context graceIds.RepositoryId (getCorrelationId context) promotionSetId

                                        return
                                            promotionSet
                                            |> Option.map (fun _ -> promotionSetId)
                                    }
                                | None -> Task.FromResult None

                        return projectObservableQueueStatus queue (observablePromotionSetIds |> Seq.toList) observableRunningPromotionSetId
                    }

                context.Items[ "Command" ] <- "Status"
                return! processQuery context parameters validations query
            }

    /// Pauses a promotion queue.
    let Pause: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context

                /// Implements validations for the server request pipeline.
                let validations (parameters: QueueActionParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.TargetBranchId QueueError.InvalidTargetBranchId
                    |]

                let! parameters = context |> parse<QueueActionParameters>
                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass
                context.Items[ "Command" ] <- nameof Pause

                if validationsPassed then
                    let targetBranchId = Guid.Parse(parameters.TargetBranchId)
                    let actorProxy = PromotionQueue.CreateActorProxy targetBranchId graceIds.RepositoryId correlationId
                    let! queueJson = actorProxy.GetForRoute correlationId
                    let queue = deserialize<PromotionQueue> queueJson
                    let! observablePromotionSetIds, observableRunningPromotionSetId = getObservableQueueWork context graceIds.RepositoryId correlationId queue

                    if canMutateCallerVisibleQueueWork queue observablePromotionSetIds observableRunningPromotionSetId then
                        match! actorProxy.PauseForRoute(createMetadata context) with
                        | Ok graceReturnValue -> return! context |> result200Ok graceReturnValue
                        | Error graceError -> return! context |> result400BadRequest graceError
                    else
                        return!
                            context
                            |> result200Ok (noObservableQueueWorkReturnValue (nameof Pause) correlationId)
                else
                    let! error = validationResults |> getFirstError

                    return!
                        context
                        |> result400BadRequest (GraceError.Create (QueueError.getErrorMessage error) correlationId)
            }

    /// Resumes a promotion queue.
    let Resume: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context

                /// Implements validations for the server request pipeline.
                let validations (parameters: QueueActionParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.TargetBranchId QueueError.InvalidTargetBranchId
                    |]

                let! parameters = context |> parse<QueueActionParameters>
                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass
                context.Items[ "Command" ] <- nameof Resume

                if validationsPassed then
                    let targetBranchId = Guid.Parse(parameters.TargetBranchId)
                    let actorProxy = PromotionQueue.CreateActorProxy targetBranchId graceIds.RepositoryId correlationId
                    let! queueJson = actorProxy.GetForRoute correlationId
                    let queue = deserialize<PromotionQueue> queueJson
                    let! observablePromotionSetIds, observableRunningPromotionSetId = getObservableQueueWork context graceIds.RepositoryId correlationId queue

                    if canMutateCallerVisibleQueueWork queue observablePromotionSetIds observableRunningPromotionSetId then
                        match! actorProxy.ResumeForRoute(createMetadata context) with
                        | Ok graceReturnValue -> return! context |> result200Ok graceReturnValue
                        | Error graceError -> return! context |> result400BadRequest graceError
                    else
                        return!
                            context
                            |> result200Ok (noObservableQueueWorkReturnValue (nameof Resume) correlationId)
                else
                    let! error = validationResults |> getFirstError

                    return!
                        context
                        |> result400BadRequest (GraceError.Create (QueueError.getErrorMessage error) correlationId)
            }

    /// Enqueues a promotion set in the promotion queue.
    let Enqueue: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context

                /// Implements validations for the server request pipeline.
                let validations (parameters: EnqueueParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.TargetBranchId QueueError.InvalidTargetBranchId
                        Guid.isValidAndNotEmptyGuid parameters.PromotionSetId QueueError.InvalidPromotionSetId
                        (if String.IsNullOrEmpty(parameters.PolicySnapshotId) then
                             Ok() |> returnValueTask
                         else
                             String.isNotEmpty parameters.PolicySnapshotId QueueError.InvalidPolicySnapshotId)
                    |]

                let! parameters = context |> parse<EnqueueParameters>
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    let targetBranchId = Guid.Parse(parameters.TargetBranchId)
                    let promotionSetId = Guid.Parse(parameters.PromotionSetId)
                    let actorProxy = PromotionQueue.CreateActorProxy targetBranchId graceIds.RepositoryId correlationId
                    let policyActorProxy = Policy.CreateActorProxy targetBranchId graceIds.RepositoryId correlationId
                    let metadata = createMetadata context

                    /// Implements run enqueue for the server request pipeline.
                    let runEnqueue () =
                        task {
                            context.Items[ "Command" ] <- nameof Enqueue

                            match! actorProxy.EnqueueForRoute promotionSetId metadata with
                            | Ok graceReturnValue -> return! context |> result200Ok graceReturnValue
                            | Error graceError -> return! context |> result400BadRequest graceError
                        }

                    /// Implements continue enqueue for the server request pipeline.
                    let continueEnqueue () =
                        task {
                            let! exists = actorProxy.Exists correlationId

                            if not exists then
                                if requiresPolicySnapshotForInitialization exists parameters.PolicySnapshotId then
                                    let graceError = GraceError.Create "PolicySnapshotId is required to initialize the queue." correlationId

                                    return! context |> result400BadRequest graceError
                                else
                                    let! actorCurrentPolicy = policyActorProxy.GetCurrent correlationId

                                    let currentPolicy =
                                        match actorCurrentPolicy with
                                        | Some snapshot when Grace.Server.Policy.isUsableSnapshot snapshot -> actorCurrentPolicy
                                        | _ -> Grace.Server.Policy.tryGetSeededSnapshot targetBranchId

                                    match currentPolicy with
                                    | None ->
                                        let graceError = GraceError.Create "Queue initialization requires a current policy snapshot." correlationId
                                        return! context |> result400BadRequest graceError
                                    | Some snapshot when
                                        snapshot.PolicySnapshotId
                                        <> PolicySnapshotId parameters.PolicySnapshotId
                                        ->
                                        let graceError = GraceError.Create "PolicySnapshotId does not match the current policy snapshot." correlationId

                                        graceError.enhance ("RequestedPolicySnapshotId", parameters.PolicySnapshotId)
                                        |> ignore

                                        graceError.enhance ("CurrentPolicySnapshotId", snapshot.PolicySnapshotId)
                                        |> ignore

                                        graceError.enhance ("CurrentPolicySnapshotIdText", $"{snapshot.PolicySnapshotId}")
                                        |> ignore

                                        return! context |> result400BadRequest graceError
                                    | Some _ ->
                                        let initializeMetadata = { metadata with CorrelationId = $"{correlationId}:initialize" }

                                        match! actorProxy.InitializeForRoute targetBranchId parameters.PolicySnapshotId initializeMetadata with
                                        | Error error -> return! context |> result400BadRequest error
                                        | Ok _ -> return! runEnqueue ()
                            else
                                return! runEnqueue ()
                        }

                    let! promotionSet = tryGetObservablePromotionSet context graceIds.RepositoryId correlationId promotionSetId

                    match promotionSet with
                    | None ->
                        let graceError = GraceError.Create "The specified promotion set does not exist." correlationId
                        return! context |> result400BadRequest graceError
                    | Some observablePromotionSet ->
                        inheritPromotionSetVisibilityMetadata metadata observablePromotionSet
                        |> ignore

                        return! continueEnqueue ()
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = QueueError.getErrorMessage error
                    let graceError = GraceError.Create errorMessage correlationId
                    return! context |> result400BadRequest graceError
            }

    /// Dequeues a promotion set from the promotion queue.
    let Dequeue: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context

                /// Implements validations for the server request pipeline.
                let validations (parameters: PromotionSetActionParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.TargetBranchId QueueError.InvalidTargetBranchId
                        Guid.isValidAndNotEmptyGuid parameters.PromotionSetId QueueError.InvalidPromotionSetId
                    |]

                let! parameters = context |> parse<PromotionSetActionParameters>
                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass
                context.Items[ "Command" ] <- nameof Dequeue

                if validationsPassed then
                    let targetBranchId = Guid.Parse(parameters.TargetBranchId)
                    let promotionSetId = Guid.Parse(parameters.PromotionSetId)
                    let actorProxy = PromotionQueue.CreateActorProxy targetBranchId graceIds.RepositoryId correlationId
                    let! promotionSet = tryGetObservablePromotionSet context graceIds.RepositoryId correlationId promotionSetId

                    match promotionSet with
                    | None ->
                        return!
                            context
                            |> result400BadRequest (GraceError.Create "The specified promotion set does not exist." correlationId)
                    | Some observablePromotionSet ->
                        let metadata = inheritPromotionSetVisibilityMetadata (createMetadata context) observablePromotionSet

                        match! actorProxy.DequeueForRoute promotionSetId metadata with
                        | Ok graceReturnValue -> return! context |> result200Ok graceReturnValue
                        | Error graceError -> return! context |> result400BadRequest graceError
                else
                    let! error = validationResults |> getFirstError

                    return!
                        context
                        |> result400BadRequest (GraceError.Create (QueueError.getErrorMessage error) correlationId)
            }
