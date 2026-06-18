namespace Grace.Server

open Giraffe
open Grace.Server.Security
open Grace.Shared.Authorization
open Grace.Shared.Parameters.Approval
open Grace.Shared.Utilities
open Grace.Types
open Grace.Types.Authorization
open Grace.Types.Common
open Grace.Types.Webhooks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open System
open System.Collections.Generic
open System.IO
open System.Threading.Tasks

module ApprovalRequest =

    open ApprovalCommon

    let private requestSeedEnabled () =
        let isDevelopment value = String.Equals(value, "Development", StringComparison.OrdinalIgnoreCase)

        Environment.GetEnvironmentVariable("GRACE_TESTING") = "1"
        || isDevelopment (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"))
        || isDevelopment (Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"))

    let private requestIdFromContext<'T when 'T :> ApprovalRequestParameters> (context: HttpContext) =
        task {
            context.Request.EnableBuffering()
            let! parameters = context.BindJsonAsync<'T>()

            context.Request.Body.Seek(0L, IO.SeekOrigin.Begin)
            |> ignore

            return
                tryParseGuid parameters.ApprovalRequestId
                |> Option.map (fun approvalRequestId ->
                    let scope = scopeFromRequestParameters parameters
                    ApprovalStore.tryGetRequestAsync approvalRequestId scope (Services.getCorrelationId context))
        }

    let resolveStoredRequestForRead<'T when 'T :> ApprovalRequestParameters> (context: HttpContext) =
        task {
            let! requestTask = requestIdFromContext<'T> context

            let! request =
                match requestTask with
                | Some getRequest -> getRequest
                | None -> Task.FromResult None

            return
                match request with
                | Some approvalRequest -> Ok(Operation.ApprovalRequestRead, resourceFromApprovalScope approvalRequest.Scope)
                | None -> Error(error context "Approval request was not found.")
        }

    let resolveStoredRequestForRespond<'T when 'T :> ApprovalRequestParameters> (context: HttpContext) =
        task {
            let! requestTask = requestIdFromContext<'T> context

            let! request =
                match requestTask with
                | Some getRequest -> getRequest
                | None -> Task.FromResult None

            return
                match request with
                | Some approvalRequest -> Ok(Operation.ApprovalRequestRespond, resourceFromApprovalScope approvalRequest.Scope)
                | None -> Error(error context "Approval request was not found.")
        }

    type private ApprovalResponderRoleSelector =
        | LegacyApprovalResponder
        | RepositoryApprovalResponder
        | BranchApprovalResponder

    let private tryGetApprovalResponderRoleSelector (selector: string) =
        if selector.StartsWith("role:", StringComparison.OrdinalIgnoreCase) then
            let roleId = selector.Substring("role:".Length)

            if roleId.Equals("ApprovalResponder", StringComparison.OrdinalIgnoreCase) then
                Some LegacyApprovalResponder
            elif roleId.Equals("RepositoryApprovalResponder", StringComparison.OrdinalIgnoreCase) then
                Some RepositoryApprovalResponder
            elif roleId.Equals("BranchApprovalResponder", StringComparison.OrdinalIgnoreCase) then
                Some BranchApprovalResponder
            else
                None
        else
            None

    let private splitResponderRoleAssignment selector (scope: ApprovalScope) =
        match selector with
        | LegacyApprovalResponder -> None
        | RepositoryApprovalResponder -> Some(Scope.Repository(scope.OwnerId, scope.OrganizationId, scope.RepositoryId), "RepositoryApprovalResponder")
        | BranchApprovalResponder ->
            if scope.TargetBranchId = BranchId.Empty then
                None
            else
                Some(Scope.Branch(scope.OwnerId, scope.OrganizationId, scope.RepositoryId, scope.TargetBranchId), "BranchApprovalResponder")

    let private hasSplitResponderRole (principals: Principal list) scope roleId =
        let assignments =
            PermissionEvaluatorDefaults.getAssignmentsForScope (scope, generateCorrelationId ())
            |> fun getAssignments -> getAssignments.GetAwaiter().GetResult()

        assignments
        |> List.exists (fun assignment ->
            principals |> List.contains assignment.Principal
            && assignment.Scope = scope
            && assignment.RoleId.Equals(roleId, StringComparison.OrdinalIgnoreCase))

    let private selectorMatches (context: HttpContext) (request: ApprovalRequest) =
        let selector =
            if isNull request.RequiredResponder then
                String.Empty
            else
                request.RequiredResponder.Trim()

        let principals = PrincipalMapper.getPrincipals context.User
        let claims = PrincipalMapper.getEffectiveClaims context.User

        if selector.StartsWith("user:", StringComparison.OrdinalIgnoreCase) then
            let expected = selector.Substring("user:".Length)

            principals
            |> List.exists (fun principal ->
                principal.PrincipalType = PrincipalType.User
                && principal.PrincipalId.Equals(expected, StringComparison.OrdinalIgnoreCase))
        elif selector.StartsWith("group:", StringComparison.OrdinalIgnoreCase) then
            let expected = selector.Substring("group:".Length)

            principals
            |> List.exists (fun principal ->
                principal.PrincipalType = PrincipalType.Group
                && principal.PrincipalId.Equals(expected, StringComparison.OrdinalIgnoreCase))
        else
            match tryGetApprovalResponderRoleSelector selector with
            | Some LegacyApprovalResponder ->
                let evaluator = context.RequestServices.GetRequiredService<IGracePermissionEvaluator>()

                let decision =
                    evaluator
                        .CheckAsync(principals, claims, Operation.ApprovalRequestRespond, resourceFromApprovalScope request.Scope)
                        .GetAwaiter()
                        .GetResult()

                match decision with
                | Allowed _ -> true
                | Denied _ -> false
            | Some roleSelector ->
                match splitResponderRoleAssignment roleSelector request.Scope with
                | Some (scope, roleId) -> hasSplitResponderRole principals scope roleId
                | None -> false
            | None -> false

    let private respond decision approvalRequestId reason clientDecisionId fallbackScope : HttpHandler =
        fun _ context ->
            task {
                let correlationId = Services.getCorrelationId context

                match tryParseGuid approvalRequestId with
                | None -> return! Services.result404NotFound context
                | Some requestId ->
                    match! ApprovalStore.tryGetRequestAsync requestId fallbackScope correlationId with
                    | None -> return! Services.result404NotFound context
                    | Some request when not (selectorMatches context request) ->
                        return!
                            context
                            |> Services.result400BadRequest (error context "Caller does not match the stored approval responder selector.")
                    | Some request ->
                        let responder = currentUserId context

                        let normalizedClientDecisionId =
                            if String.IsNullOrWhiteSpace clientDecisionId then
                                $"{requestId:N}:{decision}:{responder}"
                            else
                                clientDecisionId

                        let requestDecision =
                            { ApprovalRequestDecision.Default with
                                Decision = decision
                                DecidedBy = responder
                                DecidedAt = getCurrentInstant ()
                                Reason = if String.IsNullOrWhiteSpace reason then None else Some reason
                                ClientDecisionId = normalizedClientDecisionId
                            }

                        match!
                            ApprovalStore.handleRequestCommandAsync
                                requestId
                                request.Scope.RepositoryId
                                (ApprovalRequestCommand.RecordDecision requestDecision)
                                correlationId
                                $"{responder}"
                            with
                        | Ok result ->
                            return!
                                context
                                |> Services.result200Ok result.ReturnValue.Request
                        | Error failure -> return! context |> Services.result400BadRequest failure
            }

    let List: HttpHandler =
        fun _ context ->
            task {
                let! parameters = Services.parse<ListApprovalRequestsParameters> context
                let scope = scopeFromRequestParameters parameters

                let! requests = ApprovalStore.listRequestsAsync scope parameters.IncludeTerminal (Services.getCorrelationId context)
                return! context |> Services.result200Ok requests
            }

    let SeedGenerated: HttpHandler =
        fun _ context ->
            task {
                try
                    if requestSeedEnabled () |> not then
                        return! Services.result404NotFound context
                    else
                        let! parameters = Services.parse<SeedGeneratedApprovalRequestParameters> context

                        let promotionSetId = tryParseGuid parameters.PromotionSetId

                        let attempt =
                            if parameters.StepsComputationAttempt.HasValue then
                                Some parameters.StepsComputationAttempt.Value
                            else
                                None

                        let request =
                            { Grace.Types.Webhooks.ApprovalRequest.Default with
                                ApprovalRequestId =
                                    tryParseGuid parameters.ApprovalRequestId
                                    |> Option.defaultValue ApprovalRequestId.Empty
                                ApprovalPolicyId =
                                    tryParseGuid parameters.ApprovalPolicyId
                                    |> Option.defaultValue ApprovalPolicyId.Empty
                                ApprovalPolicyVersion = parameters.ApprovalPolicyVersion
                                Subject = parameters.Subject
                                Scope =
                                    { scopeFromRequestParameters parameters with
                                        PromotionSetId = promotionSetId
                                        StepsComputationAttempt = attempt
                                        ApprovalPolicyId = tryParseGuid parameters.ApprovalPolicyId
                                        ApprovalPolicyVersion = Some parameters.ApprovalPolicyVersion
                                    }
                                RequiredResponder = parameters.RequiredResponder
                                Status = ApprovalRequestStatus.Pending
                                CreatedBy = Grace.Types.Common.UserId parameters.CreatedBy
                                CreatedAt = getCurrentInstant ()
                            }

                        match! ApprovalStore.seedGeneratedRequestAsync request (Services.getCorrelationId context) with
                        | Ok stored -> return! context |> Services.result200Ok stored
                        | Error failure -> return! context |> Services.result400BadRequest failure
                with
                | ex ->
                    return!
                        context
                        |> Services.result400BadRequest (
                            Grace.Types.Common.GraceError.CreateWithException ex "Approval request seed failed." (Services.getCorrelationId context)
                        )
            }

    let Show: HttpHandler =
        fun _ context ->
            task {
                let! parameters = Services.parse<ShowApprovalRequestParameters> context

                match tryParseGuid parameters.ApprovalRequestId with
                | Some requestId ->
                    let scope = scopeFromRequestParameters parameters

                    match! ApprovalStore.tryGetRequestAsync requestId scope (Services.getCorrelationId context) with
                    | Some request -> return! context |> Services.result200Ok request
                    | None -> return! Services.result404NotFound context
                | None -> return! Services.result404NotFound context
            }

    let Approve: HttpHandler =
        fun next context ->
            task {
                let! parameters = Services.parse<ApproveApprovalRequestParameters> context
                let fallbackScope = scopeFromRequestParameters parameters
                return! respond ApprovalDecision.Approve parameters.ApprovalRequestId parameters.Reason parameters.ClientDecisionId fallbackScope next context
            }

    let Reject: HttpHandler =
        fun next context ->
            task {
                let! parameters = Services.parse<RejectApprovalRequestParameters> context
                let fallbackScope = scopeFromRequestParameters parameters
                return! respond ApprovalDecision.Reject parameters.ApprovalRequestId parameters.Reason parameters.ClientDecisionId fallbackScope next context
            }

    let History: HttpHandler =
        fun _ context ->
            task {
                let! parameters = Services.parse<ApprovalRequestHistoryParameters> context

                match tryParseGuid parameters.ApprovalRequestId with
                | Some requestId ->
                    let scope = scopeFromRequestParameters parameters
                    let! history = ApprovalStore.requestHistoryAsync requestId scope (Services.getCorrelationId context)
                    return! context |> Services.result200Ok history
                | None ->
                    return!
                        context
                        |> Services.result400BadRequest (error context "ApprovalRequestId is required.")
            }
