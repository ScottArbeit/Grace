namespace Grace.Server

open Giraffe
open Grace.Actors
open Grace.Actors.Extensions.ActorProxy
open Grace.Server.Security
open Grace.Server.Services
open Grace.Shared
open Grace.Shared.Extensions
open Grace.Shared.Parameters.PromotionSet
open Grace.Shared.Parameters.Visibility
open Grace.Shared.Validation.Common
open Grace.Shared.Validation.Errors
open Grace.Shared.Validation.Utilities
open Grace.Types.Authorization
open Grace.Types.PromotionSet
open Grace.Types.Common
open Grace.Types.Visibility
open Grace.Types.Webhooks
open Grace.Shared.Utilities
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open System
open System.Collections.Generic
open System.Diagnostics
open System.Threading.Tasks

/// Contains Grace Server promotion set behavior and supporting helpers.
module PromotionSet =
    /// Represents validations used by Grace Server APIs and background services.
    type Validations<'T when 'T :> PromotionSetParameters> = 'T -> ValueTask<Result<unit, QueueError>> array

    let activitySource = new ActivitySource("PromotionSet")

    /// Treats `new` as an empty promotion-set id and otherwise requires a valid GUID.
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

    /// Validates validate promotion pointers inputs before server processing continues.
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

    let private approvalSubject = "promotion"

    /// Checks exact Grace RBAC authority needed by visibility decisions after route authentication has completed.
    let private canPerformOperation (context: HttpContext) operation resource =
        task {
            let principals = PrincipalMapper.getPrincipals context.User
            let claims = PrincipalMapper.getEffectiveClaims context.User
            let evaluator = context.RequestServices.GetRequiredService<IGracePermissionEvaluator>()

            match! evaluator.CheckAsync(principals, claims, operation, resource) with
            | Allowed _ -> return true
            | Denied _ -> return false
        }

    /// Converts the authenticated request into caller facts for PromotionSet visibility checks.
    let private getVisibilityCallerAudience (context: HttpContext) (promotionSet: PromotionSetDto) =
        task {
            let callerUserId =
                PrincipalMapper.tryGetUserId context.User
                |> Option.map UserId

            let resource = Resource.Repository(promotionSet.OwnerId, promotionSet.OrganizationId, promotionSet.RepositoryId)
            let! hasRepositoryAdministration = canPerformOperation context Operation.RepositoryAdmin resource

            return { VisibilityCallerAudience.Anonymous with UserId = callerUserId; HasRepositoryAdministration = hasRepositoryAdministration }
        }

    /// Projects durable PromotionSet visibility facts into the shared hidden-as-missing helper shape.
    let private promotionSetVisibilityResource (promotionSet: PromotionSetDto) =
        {
            AuthorityKey = promotionSet.PromotionSetId
            Visibility = promotionSet.Visibility
            Ownership = promotionSet.Ownership
            CreatorUserId = promotionSet.CreatorUserId
        }

    /// Checks whether the current caller may observe a loaded PromotionSet.
    let internal canObservePromotionSet (context: HttpContext) (promotionSet: PromotionSetDto) =
        task {
            let! caller = getVisibilityCallerAudience context promotionSet

            return
                VisibilityAuthorization.canObservePromotionSet caller (fun _ -> VisibilityResourceAudience.None) (promotionSetVisibilityResource promotionSet)
        }

    /// Builds the route-equivalent missing response for an unobservable PromotionSet.
    let private hiddenPromotionSetReturnValue promotionSetId correlationId =
        GraceReturnValue.Create { PromotionSetDto.Default with PromotionSetId = promotionSetId } correlationId

    /// Builds the same successful create envelope for hidden duplicate PromotionSet ids as an unused caller-supplied id.
    let internal hiddenPromotionSetCreateReturnValue (context: HttpContext) (parameters: CreatePromotionSetParameters) promotionSetId =
        let graceIds = getGraceIds context
        let correlationId = getCorrelationId context

        let graceReturnValue = GraceReturnValue.Create "Promotion set command succeeded." correlationId

        graceReturnValue
            .enhance(getParametersAsDictionary parameters)
            .enhance(nameof OwnerId, graceIds.OwnerId)
            .enhance(nameof OrganizationId, graceIds.OrganizationId)
            .enhance(nameof RepositoryId, graceIds.RepositoryId)
            .enhance(nameof PromotionSetId, promotionSetId)
            .enhance ("Path", context.Request.Path.Value)
        |> ignore

        graceReturnValue

    /// Resolves create-time visibility into implemented public/private PromotionSet semantics.
    let internal resolvePromotionSetVisibility (parameters: CreatePromotionSetParameters) =
        let visibility =
            if String.IsNullOrWhiteSpace parameters.Visibility then
                Ok Option.None
            else
                match tryParseVisibilityInput parameters.Visibility with
                | Some parsed -> Ok(Some parsed)
                | Microsoft.FSharp.Core.Option.None -> Error "PromotionSet visibility must be Public or Private."

        let ownership =
            if String.IsNullOrWhiteSpace parameters.Ownership then
                Ok Option.None
            else
                match tryParseOwnershipInput parameters.Ownership with
                | Some parsed -> Ok(Some parsed)
                | Microsoft.FSharp.Core.Option.None -> Error "PromotionSet ownership must be RepositoryOwned or ContributorOwned."

        match visibility, ownership with
        | Error message, _
        | _, Error message -> Error message
        | Ok (Some ResourceVisibility.Private), Ok (Some ResourceOwnership.RepositoryOwned) ->
            Error "Private PromotionSet visibility requires ContributorOwned ownership."
        | Ok (Some ResourceVisibility.Public), Ok (Some ResourceOwnership.ContributorOwned) ->
            Error "ContributorOwned PromotionSet ownership requires Private visibility."
        | Ok (Some ResourceVisibility.Private), _ -> Ok(ResourceVisibility.Private, ResourceOwnership.ContributorOwned)
        | _, Ok (Some ResourceOwnership.ContributorOwned) -> Ok(ResourceVisibility.Private, ResourceOwnership.ContributorOwned)
        | Ok (Some ResourceVisibility.Public), _
        | _, Ok (Some ResourceOwnership.RepositoryOwned)
        | Ok Option.None, Ok Option.None -> Ok(ResourceVisibility.Public, ResourceOwnership.RepositoryOwned)

    /// Implements approval scope from promotion set for the server request pipeline.
    let private approvalScopeFromPromotionSet (promotionSet: PromotionSetDto) (policy: PromotionSetApprovalPolicySnapshot) =
        { ApprovalScope.Default with
            OwnerId = promotionSet.OwnerId
            OrganizationId = promotionSet.OrganizationId
            RepositoryId = promotionSet.RepositoryId
            TargetBranchId = promotionSet.TargetBranchId
            PromotionSetId = Some promotionSet.PromotionSetId
            StepsComputationAttempt = Some promotionSet.StepsComputationAttempt
            ApprovalPolicyId = Some policy.ApprovalPolicyId
            ApprovalPolicyVersion = Some policy.Version
        }

    /// Implements policy snapshot for the server request pipeline.
    let private policySnapshot (policy: ApprovalPolicy) : PromotionSetApprovalPolicySnapshot =
        { PromotionSetApprovalPolicySnapshot.Default with
            ApprovalPolicyId = policy.ApprovalPolicyId
            Version = policy.Version
            Subject = policy.Subject
            OwnerId = policy.Scope.OwnerId
            OrganizationId = policy.Scope.OrganizationId
            RepositoryId = policy.Scope.RepositoryId
            TargetBranchId = policy.Scope.TargetBranchId
            RequiredResponder = policy.RequiredResponder
            TimeoutSeconds = policy.TimeoutSeconds
        }

    /// Implements matching approval policies for the server request pipeline.
    let private matchingApprovalPolicies ownerId organizationId repositoryId targetBranchId : PromotionSetApprovalPolicySnapshot list =
        let scope =
            { ApprovalScope.Default with OwnerId = ownerId; OrganizationId = organizationId; RepositoryId = repositoryId; TargetBranchId = targetBranchId }

        ApprovalStore.listPolicies scope false
        |> Seq.filter (fun (policy: ApprovalPolicy) -> policy.Status = ApprovalPolicyStatus.Enabled)
        |> Seq.filter (fun (policy: ApprovalPolicy) -> String.Equals(policy.Subject, approvalSubject, StringComparison.OrdinalIgnoreCase))
        |> Seq.map policySnapshot
        |> Seq.sortBy (fun policy -> policy.ApprovalPolicyId, policy.Version)
        |> Seq.toList

    /// Represents approval policy snapshot resolver used by Grace Server APIs and background services.
    type ApprovalPolicySnapshotResolver() =
        interface Grace.Actors.IApprovalPolicySnapshotResolver with
            /// Loads approval policies that apply to the promotion set before a promotion apply command is issued.
            member _.GetCurrentApprovalPoliciesForPromotionApply(ownerId, organizationId, repositoryId, targetBranchId, _correlationId) =
                matchingApprovalPolicies ownerId organizationId repositoryId targetBranchId
                |> Task.FromResult

    /// Implements approval summary from request for the server request pipeline.
    let internal approvalSummaryFromRequest
        (promotionSet: PromotionSetDto)
        (policy: PromotionSetApprovalPolicySnapshot)
        (request: ApprovalRequest option)
        : PromotionSetApprovalSummary
        =
        let baseSummary = PromotionSetApprovalSummary.NotRequired promotionSet.PromotionSetId promotionSet.TargetBranchId promotionSet.StepsComputationAttempt

        /// Implements policy summary for the server request pipeline.
        let policySummary state reason =
            { baseSummary with
                State = state
                ApprovalPolicyId = Some policy.ApprovalPolicyId
                ApprovalRequestId =
                    request
                    |> Option.map (fun approvalRequest -> approvalRequest.ApprovalRequestId)
                RequiredResponder = Some policy.RequiredResponder
                LastDecisionAt =
                    request
                    |> Option.bind (fun approvalRequest ->
                        approvalRequest.Decision
                        |> Option.map (fun decision -> decision.DecidedAt))
                ExpiresAt =
                    request
                    |> Option.bind (fun approvalRequest -> approvalRequest.ExpiresAt)
                Reason = reason
            }

        let now = getCurrentInstant ()

        match request with
        | Option.None -> policySummary PromotionSetApprovalState.Pending (Option.Some "Approval is required before apply can continue.")
        | Option.Some approvalRequest ->
            match approvalRequest.Status with
            | ApprovalRequestStatus.Pending
            | ApprovalRequestStatus.Approved when
                approvalRequest.ExpiresAt
                |> Option.exists (fun expiresAt -> expiresAt <= now)
                ->
                policySummary PromotionSetApprovalState.Stale (Some "Approval request is expired.")
            | ApprovalRequestStatus.Pending -> policySummary PromotionSetApprovalState.Pending (Some "Approval is required before apply can continue.")
            | ApprovalRequestStatus.Approved -> policySummary PromotionSetApprovalState.Approved Option.None
            | ApprovalRequestStatus.Rejected -> policySummary PromotionSetApprovalState.Rejected (Some "Approval request was rejected.")
            | ApprovalRequestStatus.Expired -> policySummary PromotionSetApprovalState.Expired (Some "Approval request is expired.")
            | ApprovalRequestStatus.Cancelled -> policySummary PromotionSetApprovalState.Stale (Some "Approval request was cancelled.")
            | ApprovalRequestStatus.Superseded -> policySummary PromotionSetApprovalState.Stale (Some "Approval request was superseded.")

    /// Implements multiple policies approval summary for the server request pipeline.
    let private multiplePoliciesApprovalSummary (promotionSet: PromotionSetDto) =
        { (PromotionSetApprovalSummary.NotRequired promotionSet.PromotionSetId promotionSet.TargetBranchId promotionSet.StepsComputationAttempt) with
            State = PromotionSetApprovalState.Stale
            Reason = Some "Multiple enabled approval policies match promotion apply scope; apply requires exactly one."
        }

    /// Implements invalid policy approval summary for the server request pipeline.
    let private invalidPolicyApprovalSummary (promotionSet: PromotionSetDto) (policy: PromotionSetApprovalPolicySnapshot) =
        { (PromotionSetApprovalSummary.NotRequired promotionSet.PromotionSetId promotionSet.TargetBranchId promotionSet.StepsComputationAttempt) with
            State = PromotionSetApprovalState.Stale
            ApprovalPolicyId = Some policy.ApprovalPolicyId
            RequiredResponder = Some policy.RequiredResponder
            Reason = Some "Approval policy is invalid for apply because RequiredResponder is blank or policy identity is invalid."
        }

    /// Implements derive approval summary for the server request pipeline.
    let internal deriveApprovalSummary (promotionSet: PromotionSetDto) correlationId =
        task {
            let matchingPolicies =
                matchingApprovalPolicies promotionSet.OwnerId promotionSet.OrganizationId promotionSet.RepositoryId promotionSet.TargetBranchId

            match Grace.Actors.PromotionSet.selectApprovalPolicyOrInvalid promotionSet matchingPolicies with
            | Error (Grace.Actors.PromotionSet.InvalidMatchingApprovalPolicy policy) -> return invalidPolicyApprovalSummary promotionSet policy
            | Error (Grace.Actors.PromotionSet.MultipleMatchingApprovalPolicies _) -> return multiplePoliciesApprovalSummary promotionSet
            | Ok Option.None ->
                return PromotionSetApprovalSummary.NotRequired promotionSet.PromotionSetId promotionSet.TargetBranchId promotionSet.StepsComputationAttempt
            | Ok (Option.Some policy) ->
                let scope = approvalScopeFromPromotionSet promotionSet policy

                let requestTemplate =
                    { ApprovalRequest.Default with
                        ApprovalPolicyId = policy.ApprovalPolicyId
                        ApprovalPolicyVersion = policy.Version
                        Subject = approvalSubject
                        Scope = scope
                        RequiredResponder = policy.RequiredResponder
                    }

                let approvalRequestId = ApprovalStore.buildGeneratedApprovalRequestId requestTemplate
                let! request = ApprovalStore.tryGetRequestAsync approvalRequestId scope correlationId

                match request with
                | Option.Some approvalRequest when
                    approvalRequest.ApprovalPolicyId = policy.ApprovalPolicyId
                    && approvalRequest.ApprovalPolicyVersion = policy.Version
                    && approvalRequest.Subject = approvalSubject
                    && approvalRequest.RequiredResponder = policy.RequiredResponder
                    && approvalRequest.Scope = scope
                    ->
                    return approvalSummaryFromRequest promotionSet policy (Option.Some approvalRequest)
                | Option.Some approvalRequest ->
                    return
                        { approvalSummaryFromRequest promotionSet policy (Option.Some approvalRequest) with
                            State = PromotionSetApprovalState.Stale
                            Reason = Option.Some "Approval request is not valid for the current apply attempt."
                        }
                | Option.None -> return approvalSummaryFromRequest promotionSet policy Option.None
        }

    /// Coordinates process command processing for Grace Server.
    let private processCommand<'T when 'T :> PromotionSetParameters>
        (context: HttpContext)
        (parameters: 'T)
        (validations: Validations<'T>)
        (promotionSetId: PromotionSetId)
        (requireObservable: bool)
        (command: PromotionSetCommand)
        =
        task {
            use activity = activitySource.StartActivity("processCommand", ActivityKind.Server)
            let graceIds = getGraceIds context
            let correlationId = getCorrelationId context
            let metadata = createMetadata context
            let parameterDictionary = getParametersAsDictionary parameters
            let validationResults = validations parameters
            let! validationsPassed = validationResults |> allPass

            if validationsPassed then
                let actorProxy = PromotionSet.CreateActorProxy promotionSetId graceIds.RepositoryId correlationId

                let! canHandle =
                    if requireObservable then
                        task {
                            let! promotionSet = actorProxy.Get correlationId

                            if promotionSet.PromotionSetId = PromotionSetId.Empty then
                                return false
                            else
                                return! canObservePromotionSet context promotionSet
                        }
                    else
                        Task.FromResult true

                if not canHandle then
                    let graceError =
                        (GraceError.Create "PromotionSet does not exist." correlationId)
                            .enhance(parameterDictionary)
                            .enhance(nameof OwnerId, graceIds.OwnerId)
                            .enhance(nameof OrganizationId, graceIds.OrganizationId)
                            .enhance(nameof RepositoryId, graceIds.RepositoryId)
                            .enhance(nameof PromotionSetId, promotionSetId)
                            .enhance ("Path", context.Request.Path.Value)

                    return! context |> result400BadRequest graceError
                else
                    match command with
                    | PromotionSetCommand.CreatePromotionSet (createdPromotionSetId,
                                                              ownerId,
                                                              organizationId,
                                                              repositoryId,
                                                              targetBranchId,
                                                              visibility,
                                                              ownership,
                                                              creatorUserId) ->
                        metadata.Properties[ nameof PromotionSetId ] <- $"{createdPromotionSetId}"
                        metadata.Properties[ nameof OwnerId ] <- $"{ownerId}"
                        metadata.Properties[ nameof OrganizationId ] <- $"{organizationId}"
                        metadata.Properties[ nameof RepositoryId ] <- $"{repositoryId}"
                        metadata.Properties[ nameof BranchId ] <- $"{targetBranchId}"
                        metadata.Properties[ "TargetBranchId" ] <- $"{targetBranchId}"
                        metadata.Properties[ "Visibility" ] <- $"{visibility}"
                        metadata.Properties[ "Ownership" ] <- $"{ownership}"

                        creatorUserId
                        |> Option.iter (fun creator -> metadata.Properties[ "CreatorUserId" ] <- $"{creator}")
                    | _ -> ()

                    let! commandResult =
                        match command with
                        | PromotionSetCommand.CreatePromotionSet (createdPromotionSetId,
                                                                  ownerId,
                                                                  organizationId,
                                                                  repositoryId,
                                                                  targetBranchId,
                                                                  visibility,
                                                                  ownership,
                                                                  creatorUserId) ->
                            actorProxy.CreatePromotionSet(
                                createdPromotionSetId,
                                ownerId,
                                organizationId,
                                repositoryId,
                                targetBranchId,
                                visibility,
                                ownership,
                                creatorUserId,
                                metadata
                            )
                        | _ -> actorProxy.Handle command metadata

                    match commandResult with
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

    /// Coordinates process get processing for Grace Server.
    let private processGet (context: HttpContext) (parameters: GetPromotionSetParameters) (promotionSetId: PromotionSetId) =
        task {
            let graceIds = getGraceIds context
            let correlationId = getCorrelationId context
            let actorProxy = PromotionSet.CreateActorProxy promotionSetId graceIds.RepositoryId correlationId
            let! promotionSet = actorProxy.Get correlationId

            if promotionSet.PromotionSetId = PromotionSetId.Empty then
                return!
                    context
                    |> result200Ok (hiddenPromotionSetReturnValue promotionSetId correlationId)
            else
                let! isObservable = canObservePromotionSet context promotionSet

                if not isObservable then
                    return!
                        context
                        |> result200Ok (hiddenPromotionSetReturnValue promotionSetId correlationId)
                else
                    let! approvalSummary = deriveApprovalSummary promotionSet correlationId

                    let graceReturnValue =
                        (GraceReturnValue.Create promotionSet correlationId)
                            .enhance(getParametersAsDictionary parameters)
                            .enhance(nameof OwnerId, graceIds.OwnerId)
                            .enhance(nameof OrganizationId, graceIds.OrganizationId)
                            .enhance(nameof RepositoryId, graceIds.RepositoryId)
                            .enhance(nameof PromotionSetId, promotionSetId)
                            .enhance("ApprovalSummary", approvalSummary)
                            .enhance ("Path", context.Request.Path.Value)

                    return! context |> result200Ok graceReturnValue
        }

    /// Coordinates process get events processing for Grace Server.
    let private processGetEvents (context: HttpContext) (parameters: GetPromotionSetEventsParameters) (promotionSetId: PromotionSetId) =
        task {
            let graceIds = getGraceIds context
            let correlationId = getCorrelationId context
            let actorProxy = PromotionSet.CreateActorProxy promotionSetId graceIds.RepositoryId correlationId
            let! promotionSet = actorProxy.Get correlationId

            let! isObservable =
                if promotionSet.PromotionSetId = PromotionSetId.Empty then
                    Task.FromResult false
                else
                    canObservePromotionSet context promotionSet

            let! events =
                if isObservable then
                    actorProxy.GetEvents correlationId
                else
                    Task.FromResult(ResizeArray<PromotionSetEvent>() :> IReadOnlyList<PromotionSetEvent>)

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
                    /// Implements validations for the server request pipeline.
                    let validations (_: CreatePromotionSetParameters) =
                        [|
                            Guid.isValidAndNotEmptyGuid parameters.TargetBranchId QueueError.InvalidTargetBranchId
                        |]

                    match resolvePromotionSetVisibility parameters with
                    | Error errorMessage ->
                        return!
                            context
                            |> result400BadRequest (GraceError.Create errorMessage (getCorrelationId context))
                    | Ok (visibility, ownership) ->
                        let creatorUserId =
                            PrincipalMapper.tryGetUserId context.User
                            |> Option.map UserId

                        let command =
                            PromotionSetCommand.CreatePromotionSet(
                                promotionSetId,
                                graceIds.OwnerId,
                                graceIds.OrganizationId,
                                graceIds.RepositoryId,
                                Guid.Parse(parameters.TargetBranchId),
                                visibility,
                                ownership,
                                creatorUserId
                            )

                        let correlationId = getCorrelationId context
                        let actorProxy = PromotionSet.CreateActorProxy promotionSetId graceIds.RepositoryId correlationId
                        let! existingPromotionSet = actorProxy.Get correlationId

                        if existingPromotionSet.PromotionSetId = PromotionSetId.Empty then
                            return! processCommand context parameters validations promotionSetId false command
                        else
                            let! isObservable = canObservePromotionSet context existingPromotionSet

                            if isObservable then
                                return! processCommand context parameters validations promotionSetId false command
                            else
                                return!
                                    context
                                    |> result200Ok (hiddenPromotionSetCreateReturnValue context parameters promotionSetId)
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

                /// Implements validations for the server request pipeline.
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

                /// Implements validations for the server request pipeline.
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

                /// Implements validations for the server request pipeline.
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
                    return! processCommand context parameters validations promotionSetId true command
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

                /// Implements validations for the server request pipeline.
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
                return! processCommand context parameters validations promotionSetId true command
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

                /// Implements validations for the server request pipeline.
                let validations (_: ApplyPromotionSetParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.PromotionSetId QueueError.InvalidPromotionSetId
                    |]

                let promotionSetId = Guid.Parse(parameters.PromotionSetId)
                let correlationId = getCorrelationId context
                let command = PromotionSetCommand.Apply []
                return! processCommand context parameters validations promotionSetId true command
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

                /// Implements validations for the server request pipeline.
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
                            return! processCommand context parameters validations promotionSetId true command
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

                /// Implements validations for the server request pipeline.
                let validations (_: DeletePromotionSetParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.PromotionSetId QueueError.InvalidPromotionSetId
                    |]

                let promotionSetId = Guid.Parse(parameters.PromotionSetId)
                let command = PromotionSetCommand.DeleteLogical(parameters.Force, DeleteReason parameters.DeleteReason)
                return! processCommand context parameters validations promotionSetId true command
            }
