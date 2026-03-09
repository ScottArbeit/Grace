namespace Grace.Server

open Giraffe
open Grace.Actors.Extensions.ActorProxy
open Grace.Server.Services
open Grace.Shared
open Grace.Shared.Extensions
open Grace.Shared.Parameters.PromotionSet
open Grace.Shared.Validation.Common
open Grace.Shared.Validation.Errors
open Grace.Shared.Validation.Utilities
open Grace.Types.PromotionSet
open Grace.Types.Types
open Grace.Shared.Utilities
open Microsoft.AspNetCore.Http
open System
open System.Diagnostics
open System.Threading.Tasks

module PromotionSet =
    type Validations<'T when 'T :> PromotionSetParameters> = 'T -> ValueTask<Result<unit, QueueError>> array

    let activitySource = new ActivitySource("PromotionSet")

    let private parsePromotionSetIdOrNew (rawPromotionSetId: string) =
        if String.IsNullOrWhiteSpace(rawPromotionSetId) then
            Ok(Guid.NewGuid())
        else
            let mutable parsed = Guid.Empty

            if
                Guid.TryParse(rawPromotionSetId, &parsed)
                && parsed <> Guid.Empty
            then
                Ok parsed
            else
                Error QueueError.InvalidPromotionSetId

    let private validatePromotionPointers (promotionPointers: PromotionPointer list) =
        if promotionPointers.IsEmpty then
            Some "At least one promotion pointer is required."
        else
            promotionPointers
            |> List.tryPick (fun pointer ->
                if pointer.BranchId = BranchId.Empty then
                    Some "PromotionPointer.BranchId must be a non-empty Guid."
                elif pointer.ReferenceId = ReferenceId.Empty then
                    Some "PromotionPointer.ReferenceId must be a non-empty Guid."
                elif pointer.DirectoryVersionId = DirectoryVersionId.Empty then
                    Some "PromotionPointer.DirectoryVersionId must be a non-empty Guid."
                else
                    Option.None)

    let private processCommand<'T when 'T :> PromotionSetParameters>
        (context: HttpContext)
        (parameters: 'T)
        (validations: Validations<'T>)
        (promotionSetId: PromotionSetId)
        (command: PromotionSetCommand)
        =
        task {
            use activity = activitySource.StartActivity("processCommand", ActivityKind.Server)
            let graceIds = getGraceIds context
            let correlationId = getCorrelationId context
            let metadata = createMetadata context
            metadata.Properties[ nameof PromotionSetId ] <- $"{promotionSetId}"
            metadata.Properties[ "ActorId" ] <- $"{promotionSetId}"
            let parameterDictionary = getParametersAsDictionary parameters
            let validationResults = validations parameters
            let! validationsPassed = validationResults |> allPass

            if validationsPassed then
                let actorProxy = PromotionSet.CreateActorProxy promotionSetId graceIds.RepositoryId correlationId

                match! actorProxy.Handle command metadata with
                | Ok graceReturnValue ->
                    graceReturnValue
                        .enhance(parameterDictionary)
                        .enhance(nameof OwnerId, graceIds.OwnerId)
                        .enhance(nameof OrganizationId, graceIds.OrganizationId)
                        .enhance(nameof RepositoryId, graceIds.RepositoryId)
                        .enhance(nameof PromotionSetId, promotionSetId)
                        .enhance ("Path", context.Request.Path.Value)
                    |> ignore

                    return! context |> result200Ok graceReturnValue
                | Error graceError ->
                    graceError
                        .enhance(parameterDictionary)
                        .enhance(nameof OwnerId, graceIds.OwnerId)
                        .enhance(nameof OrganizationId, graceIds.OrganizationId)
                        .enhance(nameof RepositoryId, graceIds.RepositoryId)
                        .enhance(nameof PromotionSetId, promotionSetId)
                        .enhance ("Path", context.Request.Path.Value)
                    |> ignore

                    return! context |> result400BadRequest graceError
            else
                let! validationError = validationResults |> getFirstError
                let graceError = GraceError.Create (QueueError.getErrorMessage validationError) correlationId
                return! context |> result400BadRequest graceError
        }

    let private processGet (context: HttpContext) (parameters: GetPromotionSetParameters) (promotionSetId: PromotionSetId) =
        task {
            let graceIds = getGraceIds context
            let correlationId = getCorrelationId context
            let actorProxy = PromotionSet.CreateActorProxy promotionSetId graceIds.RepositoryId correlationId
            let! promotionSet = actorProxy.Get correlationId

            let graceReturnValue =
                (GraceReturnValue.Create promotionSet correlationId)
                    .enhance(getParametersAsDictionary parameters)
                    .enhance(nameof OwnerId, graceIds.OwnerId)
                    .enhance(nameof OrganizationId, graceIds.OrganizationId)
                    .enhance(nameof RepositoryId, graceIds.RepositoryId)
                    .enhance(nameof PromotionSetId, promotionSetId)
                    .enhance ("Path", context.Request.Path.Value)

            return! context |> result200Ok graceReturnValue
        }

    let private processGetEvents (context: HttpContext) (parameters: GetPromotionSetEventsParameters) (promotionSetId: PromotionSetId) =
        task {
            let graceIds = getGraceIds context
            let correlationId = getCorrelationId context
            let actorProxy = PromotionSet.CreateActorProxy promotionSetId graceIds.RepositoryId correlationId
            let! events = actorProxy.GetEvents correlationId

            let graceReturnValue =
                (GraceReturnValue.Create events correlationId)
                    .enhance(getParametersAsDictionary parameters)
                    .enhance(nameof OwnerId, graceIds.OwnerId)
                    .enhance(nameof OrganizationId, graceIds.OrganizationId)
                    .enhance(nameof RepositoryId, graceIds.RepositoryId)
                    .enhance(nameof PromotionSetId, promotionSetId)
                    .enhance ("Path", context.Request.Path.Value)

            return! context |> result200Ok graceReturnValue
        }

    /// Creates a promotion set.
    let Create: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let! parameters = context |> parse<CreatePromotionSetParameters>
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                match parsePromotionSetIdOrNew parameters.PromotionSetId with
                | Error validationError ->
                    return!
                        context
                        |> result400BadRequest (GraceError.Create (QueueError.getErrorMessage validationError) (getCorrelationId context))
                | Ok promotionSetId ->
                    let validations (_: CreatePromotionSetParameters) =
                        [|
                            Guid.isValidAndNotEmptyGuid parameters.TargetBranchId QueueError.InvalidTargetBranchId
                        |]

                    let command =
                        PromotionSetCommand.CreatePromotionSet(
                            promotionSetId,
                            graceIds.OwnerId,
                            graceIds.OrganizationId,
                            graceIds.RepositoryId,
                            Guid.Parse(parameters.TargetBranchId)
                        )

                    return! processCommand context parameters validations promotionSetId command
            }

    /// Gets a promotion set.
    let Get: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context
                let! parameters = context |> parse<GetPromotionSetParameters>
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                let validations (_: GetPromotionSetParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.PromotionSetId QueueError.InvalidPromotionSetId
                    |]

                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    let promotionSetId = Guid.Parse(parameters.PromotionSetId)
                    return! processGet context parameters promotionSetId
                else
                    let! validationError = validationResults |> getFirstError

                    return!
                        context
                        |> result400BadRequest (GraceError.Create (QueueError.getErrorMessage validationError) correlationId)
            }

    /// Gets all promotion set events.
    let GetEvents: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context
                let! parameters = context |> parse<GetPromotionSetEventsParameters>
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                let validations (_: GetPromotionSetEventsParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.PromotionSetId QueueError.InvalidPromotionSetId
                    |]

                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    let promotionSetId = Guid.Parse(parameters.PromotionSetId)
                    return! processGetEvents context parameters promotionSetId
                else
                    let! validationError = validationResults |> getFirstError

                    return!
                        context
                        |> result400BadRequest (GraceError.Create (QueueError.getErrorMessage validationError) correlationId)
            }

    /// Updates the input promotion pointers for a promotion set.
    let UpdateInputPromotions: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context

                let! parameters =
                    context
                    |> parse<UpdatePromotionSetInputPromotionsParameters>

                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                let validations (_: UpdatePromotionSetInputPromotionsParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.PromotionSetId QueueError.InvalidPromotionSetId
                    |]

                match validatePromotionPointers parameters.PromotionPointers with
                | Option.Some errorMessage ->
                    return!
                        context
                        |> result400BadRequest (GraceError.Create errorMessage correlationId)
                | Option.None ->
                    let promotionSetId = Guid.Parse(parameters.PromotionSetId)
                    let command = PromotionSetCommand.UpdateInputPromotions parameters.PromotionPointers
                    return! processCommand context parameters validations promotionSetId command
            }

    /// Requests server-side recomputation for a promotion set.
    let Recompute: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let! parameters = context |> parse<RecomputePromotionSetParameters>
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                let validations (_: RecomputePromotionSetParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.PromotionSetId QueueError.InvalidPromotionSetId
                    |]

                let promotionSetId = Guid.Parse(parameters.PromotionSetId)

                let reason =
                    if String.IsNullOrWhiteSpace(parameters.Reason) then
                        Option.None
                    else
                        Option.Some parameters.Reason

                let command = PromotionSetCommand.RecomputeStepsIfStale reason
                return! processCommand context parameters validations promotionSetId command
            }

    /// Applies a promotion set.
    let Apply: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let! parameters = context |> parse<ApplyPromotionSetParameters>
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                let validations (_: ApplyPromotionSetParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.PromotionSetId QueueError.InvalidPromotionSetId
                    |]

                let promotionSetId = Guid.Parse(parameters.PromotionSetId)
                let command = PromotionSetCommand.Apply
                return! processCommand context parameters validations promotionSetId command
            }

    /// Resolves blocked conflicts for a promotion set.
    let ResolveConflicts (routePromotionSetId: PromotionSetId) : HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context

                let! parameters =
                    context
                    |> parse<ResolvePromotionSetConflictsParameters>

                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString
                parameters.PromotionSetId <- $"{routePromotionSetId}"

                let validations (_: ResolvePromotionSetConflictsParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.PromotionSetId QueueError.InvalidPromotionSetId
                    |]

                if parameters.StepsComputationAttempt <= 0 then
                    return!
                        context
                        |> result400BadRequest (
                            GraceError.Create (ValidationResultError.getErrorMessage ValidationResultError.InvalidStepsComputationAttempt) correlationId
                        )
                elif parameters.Decisions.IsEmpty then
                    return!
                        context
                        |> result400BadRequest (GraceError.Create "At least one conflict resolution decision is required." correlationId)
                else
                    let mutable stepId = Guid.Empty

                    if
                        not
                            (
                                Guid.TryParse(parameters.StepId, &stepId)
                                && stepId <> Guid.Empty
                            )
                    then
                        return!
                            context
                            |> result400BadRequest (
                                GraceError.Create (ValidationResultError.getErrorMessage ValidationResultError.InvalidPromotionSetStepId) correlationId
                            )
                    else
                        let promotionSetId = routePromotionSetId
                        let actorProxy = PromotionSet.CreateActorProxy promotionSetId graceIds.RepositoryId correlationId
                        let! currentPromotionSet = actorProxy.Get correlationId

                        if currentPromotionSet.Status
                           <> PromotionSetStatus.Blocked then
                            return!
                                context
                                |> result400BadRequest (GraceError.Create "PromotionSet is not blocked for conflict resolution." correlationId)
                        elif currentPromotionSet.StepsComputationAttempt
                             <> parameters.StepsComputationAttempt then
                            return!
                                context
                                |> result400BadRequest (GraceError.Create "StepsComputationAttempt does not match current PromotionSet state." correlationId)
                        else
                            let command = PromotionSetCommand.ResolveConflicts(stepId, parameters.Decisions)
                            return! processCommand context parameters validations promotionSetId command
            }

    /// Logically deletes a promotion set.
    let Delete: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let! parameters = context |> parse<DeletePromotionSetParameters>
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                let validations (_: DeletePromotionSetParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.PromotionSetId QueueError.InvalidPromotionSetId
                    |]

                let promotionSetId = Guid.Parse(parameters.PromotionSetId)
                let command = PromotionSetCommand.DeleteLogical(parameters.Force, DeleteReason parameters.DeleteReason)
                return! processCommand context parameters validations promotionSetId command
            }
