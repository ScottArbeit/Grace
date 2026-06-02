namespace Grace.Server

open Giraffe
open Grace.Server.Security
open Grace.Shared.Authorization
open Grace.Shared.Parameters.Approval
open Grace.Shared.Utilities
open Grace.Types
open Grace.Types.Authorization
open Grace.Types.Webhooks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open System
open System.Collections.Generic
open System.IO
open System.Threading.Tasks

module ApprovalRequest =

    open ApprovalCommon

    let private requestIdFromContext<'T when 'T :> ApprovalRequestParameters> (context: HttpContext) =
        task {
            context.Request.EnableBuffering()
            let! parameters = context.BindJsonAsync<'T>()

            context.Request.Body.Seek(0L, IO.SeekOrigin.Begin)
            |> ignore

            return
                tryParseGuid parameters.ApprovalRequestId
                |> Option.bind ApprovalStore.tryGetRequest
        }

    let resolveStoredRequestForRead<'T when 'T :> ApprovalRequestParameters> (context: HttpContext) =
        task {
            let! request = requestIdFromContext<'T> context

            return
                match request with
                | Some approvalRequest -> Ok(Operation.ApprovalRequestRead, resourceFromApprovalScope approvalRequest.Scope)
                | None -> Error(error context "Approval request was not found.")
        }

    let resolveStoredRequestForRespond<'T when 'T :> ApprovalRequestParameters> (context: HttpContext) =
        task {
            let! request = requestIdFromContext<'T> context

            return
                match request with
                | Some approvalRequest -> Ok(Operation.ApprovalRequestRespond, resourceFromApprovalScope approvalRequest.Scope)
                | None -> Error(error context "Approval request was not found.")
        }

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
        elif selector.Equals("role:ApprovalResponder", StringComparison.OrdinalIgnoreCase) then
            let evaluator = context.RequestServices.GetRequiredService<IGracePermissionEvaluator>()

            let decision =
                evaluator
                    .CheckAsync(principals, claims, Operation.ApprovalRequestRespond, resourceFromApprovalScope request.Scope)
                    .GetAwaiter()
                    .GetResult()

            match decision with
            | Allowed _ -> true
            | Denied _ -> false
        else
            false

    let private respond decision approvalRequestId reason clientDecisionId : HttpHandler =
        fun _ context ->
            task {
                match tryParseGuid approvalRequestId
                      |> Option.bind ApprovalStore.tryGetRequest
                    with
                | None -> return! Services.result404NotFound context
                | Some request when request.Status.IsTerminal ->
                    return!
                        context
                        |> Services.result400BadRequest (error context $"Approval request is already {request.Status}.")
                | Some request when not (selectorMatches context request) ->
                    return!
                        context
                        |> Services.result400BadRequest (error context "Caller does not match the stored approval responder selector.")
                | Some request ->
                    let status =
                        match decision with
                        | ApprovalDecision.Approve -> ApprovalRequestStatus.Approved
                        | ApprovalDecision.Reject -> ApprovalRequestStatus.Rejected

                    let updated: Grace.Types.Webhooks.ApprovalRequest =
                        { request with
                            Status = status
                            Decision =
                                Some
                                    { ApprovalRequestDecision.Default with
                                        Decision = decision
                                        DecidedBy = currentUserId context
                                        DecidedAt = getCurrentInstant ()
                                        Reason = if String.IsNullOrWhiteSpace reason then None else Some reason
                                        ClientDecisionId = clientDecisionId
                                    }
                            UpdatedAt = Some(getCurrentInstant ())
                        }

                    return!
                        context
                        |> Services.result200Ok (ApprovalStore.updateRequest updated)
            }

    let List: HttpHandler =
        fun _ context ->
            task {
                let! parameters = Services.parse<ListApprovalRequestsParameters> context

                return!
                    context
                    |> Services.result200Ok (ApprovalStore.listRequests parameters.IncludeTerminal)
            }

    let Show: HttpHandler =
        fun _ context ->
            task {
                let! parameters = Services.parse<ShowApprovalRequestParameters> context

                match tryParseGuid parameters.ApprovalRequestId
                      |> Option.bind ApprovalStore.tryGetRequest
                    with
                | Some request -> return! context |> Services.result200Ok request
                | None -> return! Services.result404NotFound context
            }

    let Approve: HttpHandler =
        fun next context ->
            task {
                let! parameters = Services.parse<ApproveApprovalRequestParameters> context
                return! respond ApprovalDecision.Approve parameters.ApprovalRequestId parameters.Reason parameters.ClientDecisionId next context
            }

    let Reject: HttpHandler =
        fun next context ->
            task {
                let! parameters = Services.parse<RejectApprovalRequestParameters> context
                return! respond ApprovalDecision.Reject parameters.ApprovalRequestId parameters.Reason parameters.ClientDecisionId next context
            }

    let History: HttpHandler =
        fun _ context ->
            task {
                let! parameters = Services.parse<ApprovalRequestHistoryParameters> context

                match tryParseGuid parameters.ApprovalRequestId with
                | Some requestId ->
                    return!
                        context
                        |> Services.result200Ok (ApprovalStore.requestHistory requestId)
                | None ->
                    return!
                        context
                        |> Services.result400BadRequest (error context "ApprovalRequestId is required.")
            }
