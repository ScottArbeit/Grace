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

    let internal requiresPolicySnapshotForInitialization (queueExists: bool) (policySnapshotId: string) =
        not queueExists
        && String.IsNullOrEmpty(policySnapshotId)

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

                let validations (parameters: QueueStatusParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.TargetBranchId QueueError.InvalidTargetBranchId
                    |]

                let query (context: HttpContext) _ (actorProxy: IPromotionQueueActor) = actorProxy.Get(getCorrelationId context)

                let! parameters = context |> parse<QueueStatusParameters>
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString
                context.Items[ "Command" ] <- "Status"
                return! processQuery context parameters validations query
            }

    /// Pauses a promotion queue.
    let Pause: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: QueueActionParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.TargetBranchId QueueError.InvalidTargetBranchId
                    |]

                let command (_: QueueActionParameters) = PromotionQueueCommand.Pause |> returnValueTask
                context.Items[ "Command" ] <- nameof Pause
                return! processCommand context validations command
            }

    /// Resumes a promotion queue.
    let Resume: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: QueueActionParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.TargetBranchId QueueError.InvalidTargetBranchId
                    |]

                let command (_: QueueActionParameters) = PromotionQueueCommand.Resume |> returnValueTask
                context.Items[ "Command" ] <- nameof Resume
                return! processCommand context validations command
            }

    /// Enqueues a promotion set in the promotion queue.
    let Enqueue: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context

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
                    let promotionSetActorProxy = PromotionSet.CreateActorProxy promotionSetId graceIds.RepositoryId correlationId
                    let metadata = createMetadata context

                    let runEnqueue () =
                        task {
                            context.Items[ "Command" ] <- nameof Enqueue

                            let command (_: EnqueueParameters) =
                                PromotionQueueCommand.Enqueue promotionSetId
                                |> returnValueTask

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

                    let! promotionSetExists = promotionSetActorProxy.Exists correlationId

                    if not promotionSetExists then
                        let graceError = GraceError.Create "The specified promotion set does not exist." correlationId
                        return! context |> result400BadRequest graceError
                    else
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
                let validations (parameters: PromotionSetActionParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.TargetBranchId QueueError.InvalidTargetBranchId
                        Guid.isValidAndNotEmptyGuid parameters.PromotionSetId QueueError.InvalidPromotionSetId
                    |]

                let command (parameters: PromotionSetActionParameters) =
                    PromotionQueueCommand.Dequeue(Guid.Parse(parameters.PromotionSetId))
                    |> returnValueTask

                context.Items[ "Command" ] <- nameof Dequeue
                return! processCommand context validations command
            }
