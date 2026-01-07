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
open Grace.Types.Types
open Grace.Shared.Utilities
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Collections.Generic
open System.Diagnostics
open System.Threading.Tasks

module Queue =
    type Validations<'T when 'T :> QueueParameters> = 'T -> ValueTask<Result<unit, QueueError>> array

    let log = ApplicationContext.loggerFactory.CreateLogger("Queue.Server")

    let activitySource = new ActivitySource("Queue")

    let buildRequiredAction actionType reason targetId =
        { RequiredActionType = actionType
          TargetId = targetId
          Reason = reason
          Parameters = Dictionary<string, string>()
          SuggestedCliCommand = None
          SuggestedApiCall = None }

    let computeRequiredActions (candidate: IntegrationCandidate) =
        let actions = ResizeArray<RequiredActionDto>()
        actions.AddRange(candidate.RequiredActions)

        if candidate.PolicySnapshotId = PolicySnapshotId String.Empty then
            actions.Add(buildRequiredAction RequiredActionType.AcknowledgePolicyChange "Policy snapshot not set." None)

        if not candidate.Conflicts.IsEmpty then
            actions.Add(buildRequiredAction RequiredActionType.ResolveConflict "Candidate has unresolved conflicts." None)

        match candidate.Status with
        | CandidateStatus.Blocked -> actions.Add(buildRequiredAction RequiredActionType.RunGate "Candidate is blocked; gate re-run may be required." None)
        | CandidateStatus.Failed -> actions.Add(buildRequiredAction RequiredActionType.FixGateFailure "Candidate failed; gate fixes required." None)
        | _ -> ()

        actions
        |> Seq.distinctBy (fun action -> action.RequiredActionType, action.TargetId, action.Reason)
        |> Seq.toList

    let internal requiresPolicySnapshotForInitialization (queueExists: bool) (policySnapshotId: string) =
        not queueExists && String.IsNullOrEmpty(policySnapshotId)

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
            with ex ->
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
            with ex ->
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
            with ex ->
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

                let validations (parameters: QueueStatusParameters) =
                    [| Guid.isValidAndNotEmptyGuid parameters.TargetBranchId QueueError.InvalidTargetBranchId |]

                let query (context: HttpContext) _ (actorProxy: IPromotionQueueActor) = actorProxy.Get(getCorrelationId context)

                let! parameters = context |> parse<QueueStatusParameters>
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString
                context.Items["Command"] <- "Status"
                return! processQuery context parameters validations query
            }

    /// Pauses a promotion queue.
    let Pause: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: QueueActionParameters) =
                    [| Guid.isValidAndNotEmptyGuid parameters.TargetBranchId QueueError.InvalidTargetBranchId |]

                let command (_: QueueActionParameters) = PromotionQueueCommand.Pause |> returnValueTask
                context.Items["Command"] <- nameof Pause
                return! processCommand context validations command
            }

    /// Resumes a promotion queue.
    let Resume: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: QueueActionParameters) =
                    [| Guid.isValidAndNotEmptyGuid parameters.TargetBranchId QueueError.InvalidTargetBranchId |]

                let command (_: QueueActionParameters) = PromotionQueueCommand.Resume |> returnValueTask
                context.Items["Command"] <- nameof Resume
                return! processCommand context validations command
            }

    /// Enqueues a candidate in the promotion queue.
    let Enqueue: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context

                let validations (parameters: EnqueueParameters) =
                    [| Guid.isValidAndNotEmptyGuid parameters.TargetBranchId QueueError.InvalidTargetBranchId
                       Guid.isValidAndNotEmptyGuid parameters.CandidateId QueueError.InvalidCandidateId
                       (if String.IsNullOrEmpty(parameters.PolicySnapshotId) then
                            Ok() |> returnValueTask
                        else
                            String.isNotEmpty parameters.PolicySnapshotId QueueError.InvalidPolicySnapshotId) |]

                let! parameters = context |> parse<EnqueueParameters>
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    let targetBranchId = Guid.Parse(parameters.TargetBranchId)
                    let candidateId = Guid.Parse(parameters.CandidateId)
                    let actorProxy = PromotionQueue.CreateActorProxy targetBranchId graceIds.RepositoryId correlationId
                    let metadata = createMetadata context
                    let candidateActorProxy = IntegrationCandidate.CreateActorProxy candidateId graceIds.RepositoryId correlationId

                    let runEnqueue () =
                        task {
                            context.Items["Command"] <- nameof Enqueue
                            let command (_: EnqueueParameters) = PromotionQueueCommand.Enqueue candidateId |> returnValueTask
                            return! processCommandWithParameters context parameters (fun _ -> [||]) command
                        }

                    let continueEnqueue () =
                        task {
                            let! exists = actorProxy.Exists correlationId

                            if not exists then
                                if requiresPolicySnapshotForInitialization exists parameters.PolicySnapshotId then
                                    let graceError = GraceError.Create "PolicySnapshotId is required to initialize the queue." correlationId

                                    return! context |> result400BadRequest graceError
                                else
                                    let initializeCommand = PromotionQueueCommand.Initialize(targetBranchId, PolicySnapshotId parameters.PolicySnapshotId)

                                    match! actorProxy.Handle initializeCommand metadata with
                                    | Error error -> return! context |> result400BadRequest error
                                    | Ok _ -> return! runEnqueue ()
                            else
                                return! runEnqueue ()
                        }

                    let! candidateExists = candidateActorProxy.Exists correlationId

                    if not candidateExists then
                        let workItemId =
                            if String.IsNullOrEmpty(parameters.WorkItemId) then
                                WorkItemId.Empty
                            else
                                Guid.Parse(parameters.WorkItemId)

                        let promotionGroupId =
                            if String.IsNullOrEmpty(parameters.PromotionGroupId) then
                                None
                            else
                                Some(Guid.Parse(parameters.PromotionGroupId))

                        let policySnapshotId =
                            if String.IsNullOrEmpty(parameters.PolicySnapshotId) then
                                PolicySnapshotId String.Empty
                            else
                                PolicySnapshotId parameters.PolicySnapshotId

                        let candidate =
                            { IntegrationCandidate.Default with
                                CandidateId = candidateId
                                OwnerId = graceIds.OwnerId
                                OrganizationId = graceIds.OrganizationId
                                RepositoryId = graceIds.RepositoryId
                                WorkItemId = workItemId
                                PromotionGroupId = promotionGroupId
                                TargetBranchId = targetBranchId
                                PolicySnapshotId = policySnapshotId
                                Status = CandidateStatus.Pending
                                CreatedAt = getCurrentInstant () }

                        match! candidateActorProxy.Handle (CandidateCommand.Initialize candidate) metadata with
                        | Error error -> return! context |> result400BadRequest error
                        | Ok _ -> return! continueEnqueue ()
                    else
                        return! continueEnqueue ()
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = QueueError.getErrorMessage error
                    let graceError = GraceError.Create errorMessage correlationId
                    return! context |> result400BadRequest graceError
            }

    /// Dequeues a candidate from the promotion queue.
    let Dequeue: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: CandidateActionParameters) =
                    [| Guid.isValidAndNotEmptyGuid parameters.TargetBranchId QueueError.InvalidTargetBranchId
                       Guid.isValidAndNotEmptyGuid parameters.CandidateId QueueError.InvalidCandidateId |]

                let command (parameters: CandidateActionParameters) =
                    PromotionQueueCommand.Dequeue(Guid.Parse(parameters.CandidateId))
                    |> returnValueTask

                context.Items["Command"] <- nameof Dequeue
                return! processCommand context validations command
            }

    /// Gets a candidate (placeholder).
    let GetCandidate: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context

                let validations (parameters: CandidateParameters) = [| Guid.isValidAndNotEmptyGuid parameters.CandidateId QueueError.InvalidCandidateId |]

                let! parameters = context |> parse<CandidateParameters>
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    let candidateId = Guid.Parse(parameters.CandidateId)
                    let actorProxy = IntegrationCandidate.CreateActorProxy candidateId graceIds.RepositoryId correlationId

                    match! actorProxy.Get correlationId with
                    | Some candidate ->
                        let graceReturnValue =
                            (GraceReturnValue.Create candidate correlationId)
                                .enhance(nameof OwnerId, graceIds.OwnerId)
                                .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                .enhance(nameof CandidateId, candidateId)
                                .enhance ("Path", context.Request.Path.Value)

                        return! context |> result200Ok graceReturnValue
                    | None ->
                        let graceError = GraceError.Create (CandidateError.getErrorMessage CandidateError.CandidateDoesNotExist) correlationId

                        return! context |> result400BadRequest graceError
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = QueueError.getErrorMessage error
                    let graceError = GraceError.Create errorMessage correlationId
                    return! context |> result400BadRequest graceError
            }

    /// Cancels a candidate in the promotion queue.
    let CancelCandidate: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context

                let validations (parameters: CandidateActionParameters) =
                    [| Guid.isValidAndNotEmptyGuid parameters.TargetBranchId QueueError.InvalidTargetBranchId
                       Guid.isValidAndNotEmptyGuid parameters.CandidateId QueueError.InvalidCandidateId |]

                let! parameters = context |> parse<CandidateActionParameters>
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    let candidateId = Guid.Parse(parameters.CandidateId)
                    let targetBranchId = Guid.Parse(parameters.TargetBranchId)
                    let metadata = createMetadata context

                    let candidateActorProxy = IntegrationCandidate.CreateActorProxy candidateId graceIds.RepositoryId correlationId

                    match! candidateActorProxy.Handle (CandidateCommand.SetStatus CandidateStatus.Canceled) metadata with
                    | Error error -> return! context |> result400BadRequest error
                    | Ok _ ->
                        let queueActorProxy = PromotionQueue.CreateActorProxy targetBranchId graceIds.RepositoryId correlationId

                        match! queueActorProxy.Handle (PromotionQueueCommand.Dequeue candidateId) metadata with
                        | Ok graceReturnValue ->
                            graceReturnValue
                                .enhance(nameof OwnerId, graceIds.OwnerId)
                                .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                .enhance(nameof CandidateId, candidateId)
                                .enhance(nameof BranchId, targetBranchId)
                                .enhance("Command", "CancelCandidate")
                                .enhance ("Path", context.Request.Path.Value)
                            |> ignore

                            return! context |> result200Ok graceReturnValue
                        | Error graceError ->
                            graceError
                                .enhance(nameof OwnerId, graceIds.OwnerId)
                                .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                .enhance(nameof CandidateId, candidateId)
                                .enhance(nameof BranchId, targetBranchId)
                                .enhance("Command", "CancelCandidate")
                                .enhance ("Path", context.Request.Path.Value)
                            |> ignore

                            return! context |> result400BadRequest graceError
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = QueueError.getErrorMessage error
                    let graceError = GraceError.Create errorMessage correlationId
                    return! context |> result400BadRequest graceError
            }

    /// Retries a candidate by re-enqueueing it.
    let RetryCandidate: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context

                let validations (parameters: CandidateActionParameters) =
                    [| Guid.isValidAndNotEmptyGuid parameters.TargetBranchId QueueError.InvalidTargetBranchId
                       Guid.isValidAndNotEmptyGuid parameters.CandidateId QueueError.InvalidCandidateId |]

                let! parameters = context |> parse<CandidateActionParameters>
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    let candidateId = Guid.Parse(parameters.CandidateId)
                    let targetBranchId = Guid.Parse(parameters.TargetBranchId)
                    let metadata = createMetadata context

                    let candidateActorProxy = IntegrationCandidate.CreateActorProxy candidateId graceIds.RepositoryId correlationId

                    match! candidateActorProxy.Handle (CandidateCommand.ClearRequiredActions) metadata with
                    | Error error -> return! context |> result400BadRequest error
                    | Ok _ ->
                        match! candidateActorProxy.Handle (CandidateCommand.SetStatus CandidateStatus.Pending) metadata with
                        | Error error -> return! context |> result400BadRequest error
                        | Ok _ ->
                            let queueActorProxy = PromotionQueue.CreateActorProxy targetBranchId graceIds.RepositoryId correlationId

                            match! queueActorProxy.Handle (PromotionQueueCommand.Enqueue candidateId) metadata with
                            | Ok graceReturnValue ->
                                graceReturnValue
                                    .enhance(nameof OwnerId, graceIds.OwnerId)
                                    .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                    .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                    .enhance(nameof CandidateId, candidateId)
                                    .enhance(nameof BranchId, targetBranchId)
                                    .enhance("Command", "RetryCandidate")
                                    .enhance ("Path", context.Request.Path.Value)
                                |> ignore

                                return! context |> result200Ok graceReturnValue
                            | Error graceError ->
                                graceError
                                    .enhance(nameof OwnerId, graceIds.OwnerId)
                                    .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                    .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                    .enhance(nameof CandidateId, candidateId)
                                    .enhance(nameof BranchId, targetBranchId)
                                    .enhance("Command", "RetryCandidate")
                                    .enhance ("Path", context.Request.Path.Value)
                                |> ignore

                                return! context |> result400BadRequest graceError
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = QueueError.getErrorMessage error
                    let graceError = GraceError.Create errorMessage correlationId
                    return! context |> result400BadRequest graceError
            }

    /// Required actions for a candidate (placeholder).
    let RequiredActions: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context

                let validations (parameters: CandidateParameters) = [| Guid.isValidAndNotEmptyGuid parameters.CandidateId QueueError.InvalidCandidateId |]

                let! parameters = context |> parse<CandidateParameters>
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    let candidateId = Guid.Parse(parameters.CandidateId)
                    let metadata = createMetadata context
                    let actorProxy = IntegrationCandidate.CreateActorProxy candidateId graceIds.RepositoryId correlationId

                    match! actorProxy.Get correlationId with
                    | Some candidate ->
                        let requiredActions = computeRequiredActions candidate

                        let respondWithRequiredActions actions =
                            let graceReturnValue =
                                (GraceReturnValue.Create actions correlationId)
                                    .enhance(nameof OwnerId, graceIds.OwnerId)
                                    .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                    .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                    .enhance(nameof CandidateId, candidateId)
                                    .enhance ("Path", context.Request.Path.Value)

                            context |> result200Ok graceReturnValue

                        if requiredActions <> candidate.RequiredActions then
                            match! actorProxy.Handle (CandidateCommand.SetRequiredActions requiredActions) metadata with
                            | Error error -> return! context |> result400BadRequest error
                            | Ok _ -> return! respondWithRequiredActions requiredActions
                        else
                            return! respondWithRequiredActions requiredActions
                    | None ->
                        let graceError = GraceError.Create (CandidateError.getErrorMessage CandidateError.CandidateDoesNotExist) correlationId

                        return! context |> result400BadRequest graceError
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = QueueError.getErrorMessage error
                    let graceError = GraceError.Create errorMessage correlationId
                    return! context |> result400BadRequest graceError
            }

    /// Gate attestations for a candidate (placeholder).
    let Attestations: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context

                let validations (parameters: CandidateAttestationsParameters) =
                    [| Guid.isValidAndNotEmptyGuid parameters.CandidateId QueueError.InvalidCandidateId |]

                let! parameters = context |> parse<CandidateAttestationsParameters>
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    let candidateId = Guid.Parse(parameters.CandidateId)
                    let candidateActorProxy = IntegrationCandidate.CreateActorProxy candidateId graceIds.RepositoryId correlationId

                    match! candidateActorProxy.Get correlationId with
                    | Some candidate ->
                        let attestationTasks =
                            candidate.GateAttestationIds
                            |> List.map (fun attestationId ->
                                task {
                                    let attestationActorProxy = GateAttestation.CreateActorProxy attestationId graceIds.RepositoryId correlationId

                                    return! attestationActorProxy.Get correlationId
                                })

                        let! attestationResults = Task.WhenAll(attestationTasks)
                        let attestations = attestationResults |> Array.choose id |> Array.toList

                        let graceReturnValue =
                            (GraceReturnValue.Create attestations correlationId)
                                .enhance(nameof OwnerId, graceIds.OwnerId)
                                .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                .enhance(nameof CandidateId, candidateId)
                                .enhance ("Path", context.Request.Path.Value)

                        return! context |> result200Ok graceReturnValue
                    | None ->
                        let graceError = GraceError.Create (CandidateError.getErrorMessage CandidateError.CandidateDoesNotExist) correlationId

                        return! context |> result400BadRequest graceError
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = QueueError.getErrorMessage error
                    let graceError = GraceError.Create errorMessage correlationId
                    return! context |> result400BadRequest graceError
            }

    /// Reruns a gate for a candidate (placeholder).
    let RerunGate: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context

                let validations (parameters: CandidateGateRerunParameters) =
                    [| Guid.isValidAndNotEmptyGuid parameters.CandidateId QueueError.InvalidCandidateId |]

                let! parameters = context |> parse<CandidateGateRerunParameters>
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    if String.IsNullOrWhiteSpace(parameters.GateName) then
                        let graceError = GraceError.Create "GateName is required." correlationId
                        return! context |> result400BadRequest graceError
                    else
                        let candidateId = Guid.Parse(parameters.CandidateId)
                        let metadata = createMetadata context

                        let principal =
                            if
                                isNull context.User
                                || isNull context.User.Identity
                                || String.IsNullOrEmpty(context.User.Identity.Name)
                            then
                                Constants.GraceSystemUser
                            else
                                context.User.Identity.Name

                        let candidateActorProxy = IntegrationCandidate.CreateActorProxy candidateId graceIds.RepositoryId correlationId

                        match! candidateActorProxy.Get correlationId with
                        | Some candidate ->
                            let policyActorProxy = Policy.CreateActorProxy candidate.TargetBranchId graceIds.RepositoryId correlationId

                            let! policySnapshot = policyActorProxy.GetCurrent correlationId

                            let gateContext: Gates.GateContext =
                                { Candidate = candidate; PolicySnapshot = policySnapshot; CorrelationId = correlationId; Principal = UserId principal }

                            let! attestation = Gates.runGate parameters.GateName gateContext

                            let attestationActorProxy = GateAttestation.CreateActorProxy attestation.GateAttestationId graceIds.RepositoryId correlationId

                            match! attestationActorProxy.Handle (GateAttestationCommand.Create attestation) metadata with
                            | Error error -> return! context |> result400BadRequest error
                            | Ok _ ->
                                match! candidateActorProxy.Handle (CandidateCommand.AddGateAttestation attestation.GateAttestationId) metadata with
                                | Error error -> return! context |> result400BadRequest error
                                | Ok _ ->
                                    let graceReturnValue =
                                        (GraceReturnValue.Create attestation correlationId)
                                            .enhance(nameof OwnerId, graceIds.OwnerId)
                                            .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                            .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                            .enhance(nameof CandidateId, candidateId)
                                            .enhance("GateName", parameters.GateName)
                                            .enhance ("Path", context.Request.Path.Value)

                                    return! context |> result200Ok graceReturnValue
                        | None ->
                            let graceError = GraceError.Create (CandidateError.getErrorMessage CandidateError.CandidateDoesNotExist) correlationId

                            return! context |> result400BadRequest graceError
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = QueueError.getErrorMessage error
                    let graceError = GraceError.Create errorMessage correlationId
                    return! context |> result400BadRequest graceError
            }
