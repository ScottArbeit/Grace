namespace Grace.Server

open Giraffe
open Grace.Actors
open Grace.Actors.Constants
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Server.Services
open Grace.Server.Validations
open Grace.Shared
open Grace.Shared.Extensions
open Grace.Shared.Parameters.PromotionGroup
open Grace.Types
open Grace.Types.PromotionGroup
open Grace.Types.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Common
open Grace.Shared.Validation.Errors
open Grace.Shared.Validation.Utilities
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open NodaTime
open NodaTime.Text
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.Linq
open System.Text.Json
open System.Threading.Tasks

module PromotionGroup =
    type Validations<'T when 'T :> PromotionGroupParameters> = 'T -> ValueTask<Result<unit, PromotionGroupError>> array

    let activitySource = new ActivitySource("PromotionGroup")

    let log = ApplicationContext.loggerFactory.CreateLogger("PromotionGroup.Server")

    let processCommand<'T when 'T :> PromotionGroupParameters>
        (context: HttpContext)
        (validations: Validations<'T>)
        (command: 'T -> ValueTask<PromotionGroupCommand>)
        =
        task {
            let startTime = getCurrentInstant ()
            let graceIds = getGraceIds context
            let correlationId = getCorrelationId context
            let parameterDictionary = Dictionary<string, obj>()

            try
                let commandName = context.Items["Command"] :?> string
                use activity = activitySource.StartActivity("processCommand", ActivityKind.Server)
                let! parameters = context |> parse<'T>
                parameterDictionary.AddRange(getParametersAsDictionary parameters)

                // We know these Id's from ValidateIds.Middleware, so let's set them so we never have to resolve them again.
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                let handleCommand promotionGroupId repositoryId cmd =
                    task {
                        let actorProxy = PromotionGroup.CreateActorProxy promotionGroupId repositoryId correlationId

                        match! actorProxy.Handle cmd (createMetadata context) with
                        | Ok graceReturnValue ->
                            graceReturnValue
                                .enhance(parameterDictionary)
                                .enhance(nameof OwnerId, graceIds.OwnerId)
                                .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                .enhance(nameof PromotionGroupId, promotionGroupId)
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
                                .enhance(nameof PromotionGroupId, promotionGroupId)
                                .enhance("Command", commandName)
                                .enhance ("Path", context.Request.Path.Value)
                            |> ignore

                            return! context |> result400BadRequest graceError
                    }

                let validationResults = validations parameters

                let! validationsPassed = validationResults |> allPass

                log.LogDebug(
                    "{CurrentInstant}: In PromotionGroup.Server.processCommand: validationsPassed: {validationsPassed}.",
                    getCurrentInstantExtended (),
                    validationsPassed
                )

                if validationsPassed then
                    let! cmd = command parameters
                    let promotionGroupId = Guid.Parse(parameters.PromotionGroupId)
                    let! result = handleCommand promotionGroupId graceIds.RepositoryId cmd
                    let duration = getDurationRightAligned_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {hostName}; Duration: {duration}; CorrelationId: {correlationId}; Finished {path}; Status code: {statusCode}; OwnerId: {ownerId}; OrganizationId: {organizationId}; RepositoryId: {repositoryId}; PromotionGroupId: {promotionGroupId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration,
                        correlationId,
                        context.Request.Path,
                        context.Response.StatusCode,
                        graceIds.OwnerIdString,
                        graceIds.OrganizationIdString,
                        graceIds.RepositoryIdString,
                        parameters.PromotionGroupId
                    )

                    return result
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = PromotionGroupError.getErrorMessage error
                    log.LogDebug("{CurrentInstant}: error: {error}", getCurrentInstantExtended (), errorMessage)

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
                log.LogError(
                    ex,
                    "{CurrentInstant}: Exception in PromotionGroup.Server.processCommand. CorrelationId: {correlationId}.",
                    getCurrentInstantExtended (),
                    (getCorrelationId context)
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

    let processQuery<'T, 'U when 'T :> PromotionGroupParameters>
        (context: HttpContext)
        (parameters: 'T)
        (validations: Validations<'T>)
        (query: QueryResult<IPromotionGroupActor, 'U>)
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
                    // Get the actor proxy for the promotion group.
                    let promotionGroupId = Guid.Parse(parameters.PromotionGroupId)
                    let actorProxy = PromotionGroup.CreateActorProxy promotionGroupId graceIds.RepositoryId correlationId

                    // Execute the query.
                    let! queryResult = query context 0 actorProxy

                    // Wrap the query result in a GraceReturnValue.
                    let graceReturnValue =
                        (GraceReturnValue.Create queryResult correlationId)
                            .enhance(parameterDictionary)
                            .enhance(nameof OwnerId, graceIds.OwnerId)
                            .enhance(nameof OrganizationId, graceIds.OrganizationId)
                            .enhance(nameof RepositoryId, graceIds.RepositoryId)
                            .enhance(nameof PromotionGroupId, promotionGroupId)
                            .enhance ("Path", context.Request.Path.Value)

                    return! context |> result200Ok graceReturnValue
                else
                    let! error = validationResults |> getFirstError

                    let graceError =
                        (GraceError.Create (PromotionGroupError.getErrorMessage error) correlationId)
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

    /// Creates a new promotion group.
    let Create: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context

                let validations (parameters: CreatePromotionGroupParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.PromotionGroupId PromotionGroupError.InvalidPromotionGroupId
                        Guid.isValidAndNotEmptyGuid parameters.TargetBranchId PromotionGroupError.InvalidTargetBranchId
                    |]

                let command (parameters: CreatePromotionGroupParameters) =
                    let promotionGroupId = Guid.Parse(parameters.PromotionGroupId)
                    let targetBranchId = Guid.Parse(parameters.TargetBranchId)

                    let scheduledAt =
                        if String.IsNullOrWhiteSpace(parameters.ScheduledAt) then
                            None
                        else
                            match InstantPattern.ExtendedIso.Parse(parameters.ScheduledAt) with
                            | result when result.Success -> Some result.Value
                            | _ -> None

                    PromotionGroupCommand.Create(
                        promotionGroupId,
                        graceIds.OwnerId,
                        graceIds.OrganizationId,
                        graceIds.RepositoryId,
                        targetBranchId,
                        parameters.Description,
                        scheduledAt
                    )
                    |> returnValueTask

                context.Items[ "Command" ] <- nameof (Create)
                return! processCommand context validations command
            }

    /// Gets a promotion group.
    let Get: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context

                let validations (parameters: GetPromotionGroupParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.PromotionGroupId PromotionGroupError.InvalidPromotionGroupId
                    |]

                let query (context: HttpContext) maxCount (actorProxy: IPromotionGroupActor) = task { return! actorProxy.Get(getCorrelationId context) }

                let! parameters = context |> parse<GetPromotionGroupParameters>
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString
                context.Items[ "Command" ] <- "Get"
                return! processQuery context parameters validations query
            }

    /// Gets events for a promotion group.
    let GetEvents: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context

                let validations (parameters: GetPromotionGroupParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.PromotionGroupId PromotionGroupError.InvalidPromotionGroupId
                    |]

                let query (context: HttpContext) maxCount (actorProxy: IPromotionGroupActor) = task { return! actorProxy.GetEvents(getCorrelationId context) }

                let! parameters = context |> parse<GetPromotionGroupParameters>
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString
                context.Items[ "Command" ] <- "GetEvents"
                return! processQuery context parameters validations query
            }

    /// Adds a promotion to the group.
    let AddPromotion: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context

                let validations (parameters: AddPromotionParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.PromotionGroupId PromotionGroupError.InvalidPromotionGroupId
                        Guid.isValidAndNotEmptyGuid parameters.PromotionId PromotionGroupError.InvalidPromotionId
                    |]

                let command (parameters: AddPromotionParameters) =
                    let promotionId = Guid.Parse(parameters.PromotionId)

                    PromotionGroupCommand.AddPromotion promotionId
                    |> returnValueTask

                context.Items[ "Command" ] <- nameof (AddPromotion)
                return! processCommand context validations command
            }

    /// Removes a promotion from the group.
    let RemovePromotion: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context

                let validations (parameters: RemovePromotionParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.PromotionGroupId PromotionGroupError.InvalidPromotionGroupId
                        Guid.isValidAndNotEmptyGuid parameters.PromotionId PromotionGroupError.InvalidPromotionId
                    |]

                let command (parameters: RemovePromotionParameters) =
                    let promotionId = Guid.Parse(parameters.PromotionId)

                    PromotionGroupCommand.RemovePromotion promotionId
                    |> returnValueTask

                context.Items[ "Command" ] <- nameof (RemovePromotion)
                return! processCommand context validations command
            }

    /// Reorders the promotions in a group.
    let ReorderPromotions: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context

                let validatePromotionIds (promotionIds: System.Collections.Generic.IEnumerable<string>) (error: PromotionGroupError) =
                    let allValid =
                        promotionIds
                        |> Seq.forall (fun id ->
                            match Guid.TryParse(id) with
                            | (true, guid) -> guid <> Guid.Empty
                            | _ -> false)

                    if allValid then Ok() |> returnValueTask else Error error |> returnValueTask

                let validations (parameters: ReorderPromotionsParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.PromotionGroupId PromotionGroupError.InvalidPromotionGroupId
                        validatePromotionIds parameters.PromotionIds PromotionGroupError.InvalidPromotionId
                    |]

                let command (parameters: ReorderPromotionsParameters) =
                    let promotionIds =
                        parameters.PromotionIds
                        |> Seq.map (fun id -> Guid.Parse(id))
                        |> Seq.toList

                    PromotionGroupCommand.ReorderPromotions promotionIds
                    |> returnValueTask

                context.Items[ "Command" ] <- nameof (ReorderPromotions)
                return! processCommand context validations command
            }

    /// Schedules the promotion group.
    let Schedule: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context

                let validations (parameters: ScheduleParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.PromotionGroupId PromotionGroupError.InvalidPromotionGroupId
                    |]

                let command (parameters: ScheduleParameters) =
                    let scheduledAt =
                        if String.IsNullOrWhiteSpace(parameters.ScheduledAt) then
                            None
                        else
                            match InstantPattern.ExtendedIso.Parse(parameters.ScheduledAt) with
                            | result when result.Success -> Some result.Value
                            | _ -> None

                    PromotionGroupCommand.Schedule scheduledAt
                    |> returnValueTask

                context.Items[ "Command" ] <- nameof (Schedule)
                return! processCommand context validations command
            }

    /// Marks the promotion group as ready.
    let MarkReady: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context

                let validations (parameters: MarkReadyParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.PromotionGroupId PromotionGroupError.InvalidPromotionGroupId
                    |]

                let command (parameters: MarkReadyParameters) = PromotionGroupCommand.MarkReady |> returnValueTask

                context.Items[ "Command" ] <- nameof (MarkReady)
                return! processCommand context validations command
            }

    /// Starts the promotion group execution.
    let Start: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context

                let validations (parameters: StartParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.PromotionGroupId PromotionGroupError.InvalidPromotionGroupId
                    |]

                let command (parameters: StartParameters) = PromotionGroupCommand.Start |> returnValueTask

                context.Items[ "Command" ] <- nameof (Start)
                return! processCommand context validations command
            }

    /// Completes the promotion group execution.
    let Complete: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context

                let validations (parameters: CompleteParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.PromotionGroupId PromotionGroupError.InvalidPromotionGroupId
                    |]

                let command (parameters: CompleteParameters) =
                    PromotionGroupCommand.Complete parameters.Success
                    |> returnValueTask

                context.Items[ "Command" ] <- nameof (Complete)
                return! processCommand context validations command
            }

    /// Blocks the promotion group.
    let Block: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context

                let validations (parameters: BlockParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.PromotionGroupId PromotionGroupError.InvalidPromotionGroupId
                    |]

                let command (parameters: BlockParameters) =
                    PromotionGroupCommand.Block parameters.Reason
                    |> returnValueTask

                context.Items[ "Command" ] <- nameof (Block)
                return! processCommand context validations command
            }

    /// Deletes the promotion group.
    let Delete: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context

                let validations (parameters: DeletePromotionGroupParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.PromotionGroupId PromotionGroupError.InvalidPromotionGroupId
                    |]

                let command (parameters: DeletePromotionGroupParameters) =
                    PromotionGroupCommand.DeleteLogical(parameters.Force, parameters.DeleteReason)
                    |> returnValueTask

                context.Items[ "Command" ] <- nameof (Delete)
                return! processCommand context validations command
            }
