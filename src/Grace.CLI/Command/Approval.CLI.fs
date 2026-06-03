namespace Grace.CLI.Command

open Grace.CLI.Common
open Grace.CLI.Services
open Grace.CLI.Text
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Grace.Types.Types
open Grace.Types.Webhooks
open Spectre.Console
open System
open System.CommandLine
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.Threading
open System.Threading.Tasks

module ApprovalCommand =
    module private Options =
        let policyId =
            new Option<string>(
                "--policy",
                [|
                    "--policy-id"
                    "--approval-policy"
                    "--approval-policy-id"
                |],
                Required = true,
                Description = "The approval policy ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne
            )

        let requestId =
            new Option<string>(
                "--request",
                [|
                    "--request-id"
                    "--approval-request"
                    "--approval-request-id"
                |],
                Required = true,
                Description = "The approval request ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne
            )

        let name = new Option<string>("--name", Required = true, Description = "The approval policy name.", Arity = ArgumentArity.ExactlyOne)
        let nameOptional = new Option<string>("--name", Required = false, Description = "The approval policy name.", Arity = ArgumentArity.ExactlyOne)

        let subject =
            new Option<string>(
                "--subject",
                [| "--promotion-set" |],
                Required = true,
                Description = "The approval subject, such as a promotion set ID.",
                Arity = ArgumentArity.ExactlyOne
            )

        let requiredResponder =
            new Option<string>(
                "--required-responder",
                [| "--responder"; "--approver" |],
                Required = true,
                Description = "Required approval responder selector.",
                Arity = ArgumentArity.ExactlyOne
            )

        let notificationUrl =
            new Option<string>("--notification-url", Required = false, Description = "Optional approval notification URL.", Arity = ArgumentArity.ExactlyOne)

        let allowUnsafeLocal =
            new Option<bool>(
                "--allow-unsafe-local-development",
                [|
                    "--acknowledge-unsafe-local-development"
                |],
                Required = false,
                Description = "Allow unsafe local-development notification URLs.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> false)
            )

        let timeoutSeconds =
            new Option<int>(
                "--timeout-seconds",
                Required = false,
                Description = "Optional approval timeout in seconds.",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> 0)
            )

        let onTimeout =
            new Option<ApprovalTimeoutAction>(
                "--on-timeout",
                Required = false,
                Description = "Action when approval times out.",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> ApprovalTimeoutAction.Reject)
            )

        let includeDeleted =
            new Option<bool>(
                "--include-deleted",
                Required = false,
                Description = "Include deleted approval policies.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> false)
            )

        let includeTerminal =
            new Option<bool>(
                "--include-terminal",
                Required = false,
                Description = "Include terminal approval requests.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> true)
            )

        let reason = new Option<string>("--reason", Required = false, Description = "Decision reason.", Arity = ArgumentArity.ExactlyOne)

        let clientDecisionId =
            new Option<string>(
                "--client-decision-id",
                Required = false,
                Description = "Idempotency key for the approval decision.",
                Arity = ArgumentArity.ExactlyOne
            )

        let targetBranchId =
            new Option<BranchId>(
                OptionName.BranchId,
                Required = false,
                Description = "Optional target branch ID <Guid>.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> BranchId.Empty)
            )

        let waitTimeoutSeconds =
            new Option<int>(
                "--wait-timeout-seconds",
                Required = false,
                Description = "Maximum time to wait for a terminal request status.",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> 300)
            )

        let pollSeconds =
            new Option<int>(
                "--poll-seconds",
                Required = false,
                Description = "Polling interval while waiting.",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> 5)
            )

        let ownerId =
            new Option<OwnerId>(
                OptionName.OwnerId,
                Required = false,
                Description = "The repository's owner ID <Guid>.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> OwnerId.Empty)
            )

        let ownerName =
            new Option<string>(
                OptionName.OwnerName,
                Required = false,
                Description = "The repository's owner name. [default: current owner]",
                Arity = ArgumentArity.ExactlyOne
            )

        let organizationId =
            new Option<OrganizationId>(
                OptionName.OrganizationId,
                Required = false,
                Description = "The organization's ID <Guid>.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> OrganizationId.Empty)
            )

        let organizationName =
            new Option<string>(
                OptionName.OrganizationName,
                Required = false,
                Description = "The organization's name. [default: current organization]",
                Arity = ArgumentArity.ExactlyOne
            )

        let repositoryId =
            new Option<RepositoryId>(
                OptionName.RepositoryId,
                Required = false,
                Description = "The repository's ID <Guid>.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> RepositoryId.Empty)
            )

        let repositoryName =
            new Option<string>(
                OptionName.RepositoryName,
                Required = false,
                Description = "The repository's name. [default: current repository]",
                Arity = ArgumentArity.ExactlyOne
            )

    let private targetBranch (parseResult: ParseResult) =
        let value = parseResult.GetValue(Options.targetBranchId)
        if value = BranchId.Empty then String.Empty else value.ToString()

    let private urlSafety allowUnsafe =
        if allowUnsafe then
            OutboundUrlSafety.LocalUnsafeDevOnly
        else
            OutboundUrlSafety.PublicHttps

    let private nullableTimeout seconds = if seconds <= 0 then Nullable<int>() else Nullable<int>(seconds)

    let private addPolicyScope (parameters: #Grace.Shared.Parameters.Approval.ApprovalPolicyParameters) (graceIds: GraceIds) (parseResult: ParseResult) =
        parameters.OwnerId <- graceIds.OwnerIdString
        parameters.OwnerName <- graceIds.OwnerName
        parameters.OrganizationId <- graceIds.OrganizationIdString
        parameters.OrganizationName <- graceIds.OrganizationName
        parameters.RepositoryId <- graceIds.RepositoryIdString
        parameters.RepositoryName <- graceIds.RepositoryName
        parameters.TargetBranchId <- targetBranch parseResult
        parameters.CorrelationId <- graceIds.CorrelationId
        parameters

    let private addRequestScope (parameters: #Grace.Shared.Parameters.Approval.ApprovalRequestParameters) (graceIds: GraceIds) (parseResult: ParseResult) =
        parameters.OwnerId <- graceIds.OwnerIdString
        parameters.OwnerName <- graceIds.OwnerName
        parameters.OrganizationId <- graceIds.OrganizationIdString
        parameters.OrganizationName <- graceIds.OrganizationName
        parameters.RepositoryId <- graceIds.RepositoryIdString
        parameters.RepositoryName <- graceIds.RepositoryName
        parameters.TargetBranchId <- targetBranch parseResult
        parameters.CorrelationId <- graceIds.CorrelationId
        parameters

    let internal approvalCommandText (requestId: ApprovalRequestId) = $"grace approval request approve --request {requestId}"

    let internal renderApprovalSummary (parseResult: ParseResult) (summary: PromotionSetApprovalSummary) =
        if
            not (parseResult |> json)
            && not (parseResult |> silent)
        then
            let table = Table(Border = TableBorder.Rounded)
            table.AddColumn("Field") |> ignore
            table.AddColumn("Value") |> ignore

            table.AddRow("ApprovalState", Markup.Escape(getDiscriminatedUnionCaseName summary.State))
            |> ignore

            table.AddRow(
                "ApprovalRequestId",
                summary.ApprovalRequestId
                |> Option.map string
                |> Option.defaultValue "-"
                |> Markup.Escape
            )
            |> ignore

            table.AddRow(
                "RequiredPolicy",
                summary.ApprovalPolicyId
                |> Option.map string
                |> Option.defaultValue "-"
                |> Markup.Escape
            )
            |> ignore

            table.AddRow(
                "RequiredResponder",
                summary.RequiredResponder
                |> Option.defaultValue "-"
                |> Markup.Escape
            )
            |> ignore

            table.AddRow(
                "LastDecisionAt",
                summary.LastDecisionAt
                |> Option.map string
                |> Option.defaultValue "-"
                |> Markup.Escape
            )
            |> ignore

            table.AddRow(
                "Reason",
                summary.Reason
                |> Option.defaultValue "-"
                |> Markup.Escape
            )
            |> ignore

            match summary.ApprovalRequestId with
            | Some requestId when summary.State = PromotionSetApprovalState.Pending ->
                table.AddRow("NextCommand", Markup.Escape(approvalCommandText requestId))
                |> ignore
            | _ -> ()

            AnsiConsole.Write(table)

    let internal renderPendingApproval (parseResult: ParseResult) (summary: PromotionSetApprovalSummary) =
        if
            not (parseResult |> json)
            && not (parseResult |> silent)
        then
            AnsiConsole.MarkupLine("[yellow]Promotion set apply is waiting for approval.[/]")
            renderApprovalSummary parseResult summary

    let internal renderApprovalPolicy (parseResult: ParseResult) (policy: ApprovalPolicy) =
        if
            not (parseResult |> json)
            && not (parseResult |> silent)
        then
            let table = Table(Border = TableBorder.Rounded)
            table.AddColumn("Field") |> ignore
            table.AddColumn("Value") |> ignore

            table.AddRow("ApprovalPolicyId", Markup.Escape(policy.ApprovalPolicyId.ToString()))
            |> ignore

            table.AddRow("Name", Markup.Escape(policy.Name))
            |> ignore

            table.AddRow("Subject", Markup.Escape(policy.Subject))
            |> ignore

            table.AddRow("RequiredResponder", Markup.Escape(policy.RequiredResponder))
            |> ignore

            table.AddRow("Status", Markup.Escape(getDiscriminatedUnionCaseName policy.Status))
            |> ignore

            table.AddRow(
                "TimeoutSeconds",
                policy.TimeoutSeconds
                |> Option.map string
                |> Option.defaultValue "-"
            )
            |> ignore

            AnsiConsole.Write(table)

    let internal renderApprovalPolicies (parseResult: ParseResult) (policies: ApprovalPolicy seq) =
        if
            not (parseResult |> json)
            && not (parseResult |> silent)
        then
            let table = Table(Border = TableBorder.Rounded)
            table.AddColumn("ApprovalPolicyId") |> ignore
            table.AddColumn("Name") |> ignore
            table.AddColumn("Subject") |> ignore
            table.AddColumn("Responder") |> ignore
            table.AddColumn("Status") |> ignore

            policies
            |> Seq.iter (fun policy ->
                table.AddRow(
                    Markup.Escape(policy.ApprovalPolicyId.ToString()),
                    Markup.Escape(policy.Name),
                    Markup.Escape(policy.Subject),
                    Markup.Escape(policy.RequiredResponder),
                    Markup.Escape(getDiscriminatedUnionCaseName policy.Status)
                )
                |> ignore)

            AnsiConsole.Write(table)

    let internal renderApprovalRequest (parseResult: ParseResult) (request: ApprovalRequest) =
        if
            not (parseResult |> json)
            && not (parseResult |> silent)
        then
            let table = Table(Border = TableBorder.Rounded)
            table.AddColumn("Field") |> ignore
            table.AddColumn("Value") |> ignore

            table.AddRow("ApprovalRequestId", Markup.Escape(request.ApprovalRequestId.ToString()))
            |> ignore

            table.AddRow("ApprovalPolicyId", Markup.Escape(request.ApprovalPolicyId.ToString()))
            |> ignore

            table.AddRow("Subject", Markup.Escape(request.Subject))
            |> ignore

            table.AddRow("RequiredResponder", Markup.Escape(request.RequiredResponder))
            |> ignore

            table.AddRow("Status", Markup.Escape(getDiscriminatedUnionCaseName request.Status))
            |> ignore

            table.AddRow(
                "ExpiresAt",
                request.ExpiresAt
                |> Option.map string
                |> Option.defaultValue "-"
                |> Markup.Escape
            )
            |> ignore

            AnsiConsole.Write(table)

    let internal renderApprovalRequests (parseResult: ParseResult) (requests: ApprovalRequest seq) =
        if
            not (parseResult |> json)
            && not (parseResult |> silent)
        then
            let table = Table(Border = TableBorder.Rounded)
            table.AddColumn("ApprovalRequestId") |> ignore
            table.AddColumn("Policy") |> ignore
            table.AddColumn("Subject") |> ignore
            table.AddColumn("Status") |> ignore
            table.AddColumn("ExpiresAt") |> ignore

            requests
            |> Seq.iter (fun request ->
                table.AddRow(
                    Markup.Escape(request.ApprovalRequestId.ToString()),
                    Markup.Escape(request.ApprovalPolicyId.ToString()),
                    Markup.Escape(request.Subject),
                    Markup.Escape(getDiscriminatedUnionCaseName request.Status),
                    request.ExpiresAt
                    |> Option.map string
                    |> Option.defaultValue "-"
                    |> Markup.Escape
                )
                |> ignore)

            AnsiConsole.Write(table)

    let private execute action render =
        task {
            try
                let! result = action ()

                match result with
                | Ok returnValue ->
                    render returnValue.ReturnValue
                    return Ok returnValue
                | Error error -> return Error error
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" String.Empty)
        }

    let private createPolicyParameters (parseResult: ParseResult) =
        let allowUnsafe = parseResult.GetValue(Options.allowUnsafeLocal)

        let parameters = Grace.Shared.Parameters.Approval.CreateApprovalPolicyParameters()
        parameters.ApprovalPolicyId <- Guid.NewGuid().ToString()
        parameters.Name <- parseResult.GetValue(Options.name)
        parameters.Subject <- parseResult.GetValue(Options.subject)
        parameters.RequiredResponder <- parseResult.GetValue(Options.requiredResponder)

        parameters.NotificationUrl <-
            parseResult.GetValue(Options.notificationUrl)
            |> Option.ofObj
            |> Option.defaultValue String.Empty

        parameters.NotificationUrlSafety <- urlSafety allowUnsafe
        parameters.AcknowledgeUnsafeLocalDevelopment <- allowUnsafe
        parameters.TimeoutSeconds <- nullableTimeout (parseResult.GetValue(Options.timeoutSeconds))
        parameters.OnTimeout <- parseResult.GetValue(Options.onTimeout)
        parameters

    let private updatePolicyParameters (parseResult: ParseResult) =
        let allowUnsafe = parseResult.GetValue(Options.allowUnsafeLocal)

        let parameters = Grace.Shared.Parameters.Approval.UpdateApprovalPolicyParameters()
        parameters.ApprovalPolicyId <- parseResult.GetValue(Options.policyId)
        parameters.Name <- parseResult.GetValue(Options.name)
        parameters.Subject <- parseResult.GetValue(Options.subject)
        parameters.RequiredResponder <- parseResult.GetValue(Options.requiredResponder)

        parameters.NotificationUrl <-
            parseResult.GetValue(Options.notificationUrl)
            |> Option.ofObj
            |> Option.defaultValue String.Empty

        parameters.NotificationUrlSafety <- urlSafety allowUnsafe
        parameters.AcknowledgeUnsafeLocalDevelopment <- allowUnsafe
        parameters.TimeoutSeconds <- nullableTimeout (parseResult.GetValue(Options.timeoutSeconds))
        parameters.OnTimeout <- parseResult.GetValue(Options.onTimeout)
        parameters

    let private createPolicyHandler parseResult =
        let graceIds = parseResult |> getNormalizedIdsAndNames
        let parameters = createPolicyParameters parseResult

        addPolicyScope parameters graceIds parseResult
        |> ignore

        execute (fun () -> Grace.SDK.ApprovalPolicy.Create parameters) (renderApprovalPolicy parseResult)

    let private listPolicyHandler parseResult =
        let graceIds = parseResult |> getNormalizedIdsAndNames

        let parameters = Grace.Shared.Parameters.Approval.ListApprovalPoliciesParameters(IncludeDeleted = parseResult.GetValue(Options.includeDeleted))

        addPolicyScope parameters graceIds parseResult
        |> ignore

        execute (fun () -> Grace.SDK.ApprovalPolicy.List parameters) (renderApprovalPolicies parseResult)

    let private showPolicyHandler parseResult =
        let graceIds = parseResult |> getNormalizedIdsAndNames
        let parameters = Grace.Shared.Parameters.Approval.ShowApprovalPolicyParameters(ApprovalPolicyId = parseResult.GetValue(Options.policyId))

        addPolicyScope parameters graceIds parseResult
        |> ignore

        execute (fun () -> Grace.SDK.ApprovalPolicy.Show parameters) (renderApprovalPolicy parseResult)

    let private updatePolicyHandler parseResult =
        let graceIds = parseResult |> getNormalizedIdsAndNames
        let parameters = updatePolicyParameters parseResult

        addPolicyScope parameters graceIds parseResult
        |> ignore

        execute (fun () -> Grace.SDK.ApprovalPolicy.Update parameters) (renderApprovalPolicy parseResult)

    let private simplePolicyHandler makeParameters sdkCall parseResult =
        let graceIds = parseResult |> getNormalizedIdsAndNames
        let parameters = makeParameters (parseResult.GetValue(Options.policyId))

        addPolicyScope parameters graceIds parseResult
        |> ignore

        execute (fun () -> sdkCall parameters) (renderApprovalPolicy parseResult)

    let private evaluatePolicyHandler parseResult =
        let graceIds = parseResult |> getNormalizedIdsAndNames

        let parameters = Grace.Shared.Parameters.Approval.EvaluateApprovalPolicyParameters(Subject = parseResult.GetValue(Options.subject))

        addPolicyScope parameters graceIds parseResult
        |> ignore

        execute (fun () -> Grace.SDK.ApprovalPolicy.Evaluate parameters) (renderApprovalPolicies parseResult)

    let private listRequestHandler parseResult =
        let graceIds = parseResult |> getNormalizedIdsAndNames

        let parameters = Grace.Shared.Parameters.Approval.ListApprovalRequestsParameters(IncludeTerminal = parseResult.GetValue(Options.includeTerminal))

        addRequestScope parameters graceIds parseResult
        |> ignore

        execute (fun () -> Grace.SDK.ApprovalRequest.List parameters) (renderApprovalRequests parseResult)

    let private showRequestHandler parseResult =
        let graceIds = parseResult |> getNormalizedIdsAndNames
        let parameters = Grace.Shared.Parameters.Approval.ShowApprovalRequestParameters(ApprovalRequestId = parseResult.GetValue(Options.requestId))

        addRequestScope parameters graceIds parseResult
        |> ignore

        execute (fun () -> Grace.SDK.ApprovalRequest.Show parameters) (renderApprovalRequest parseResult)

    let private approveRequestHandler parseResult =
        let graceIds = parseResult |> getNormalizedIdsAndNames

        let parameters = Grace.Shared.Parameters.Approval.ApproveApprovalRequestParameters()
        parameters.ApprovalRequestId <- parseResult.GetValue(Options.requestId)

        parameters.Reason <-
            parseResult.GetValue(Options.reason)
            |> Option.ofObj
            |> Option.defaultValue String.Empty

        parameters.ClientDecisionId <-
            parseResult.GetValue(Options.clientDecisionId)
            |> Option.ofObj
            |> Option.defaultValue String.Empty

        addRequestScope parameters graceIds parseResult
        |> ignore

        execute (fun () -> Grace.SDK.ApprovalRequest.Approve parameters) (renderApprovalRequest parseResult)

    let private rejectRequestHandler parseResult =
        let graceIds = parseResult |> getNormalizedIdsAndNames

        let parameters = Grace.Shared.Parameters.Approval.RejectApprovalRequestParameters()
        parameters.ApprovalRequestId <- parseResult.GetValue(Options.requestId)

        parameters.Reason <-
            parseResult.GetValue(Options.reason)
            |> Option.ofObj
            |> Option.defaultValue String.Empty

        parameters.ClientDecisionId <-
            parseResult.GetValue(Options.clientDecisionId)
            |> Option.ofObj
            |> Option.defaultValue String.Empty

        addRequestScope parameters graceIds parseResult
        |> ignore

        execute (fun () -> Grace.SDK.ApprovalRequest.Reject parameters) (renderApprovalRequest parseResult)

    let private historyRequestHandler parseResult =
        let graceIds = parseResult |> getNormalizedIdsAndNames
        let parameters = Grace.Shared.Parameters.Approval.ApprovalRequestHistoryParameters(ApprovalRequestId = parseResult.GetValue(Options.requestId))

        addRequestScope parameters graceIds parseResult
        |> ignore

        execute (fun () -> Grace.SDK.ApprovalRequest.History parameters) (renderApprovalRequests parseResult)

    let private waitRequestHandler parseResult =
        task {
            let graceIds = parseResult |> getNormalizedIdsAndNames
            let requestId = parseResult.GetValue(Options.requestId)
            let timeoutSeconds = max 1 (parseResult.GetValue(Options.waitTimeoutSeconds))
            let pollSeconds = max 1 (parseResult.GetValue(Options.pollSeconds))
            let deadline = DateTimeOffset.UtcNow.AddSeconds(float timeoutSeconds)
            let mutable completed = false
            let mutable lastResult: GraceResult<ApprovalRequest> option = None

            while not completed do
                let parameters = Grace.Shared.Parameters.Approval.ShowApprovalRequestParameters(ApprovalRequestId = requestId)

                addRequestScope parameters graceIds parseResult
                |> ignore

                let! result = Grace.SDK.ApprovalRequest.Show parameters
                lastResult <- Some result

                match result with
                | Ok returnValue when returnValue.ReturnValue.Status.IsTerminal ->
                    renderApprovalRequest parseResult returnValue.ReturnValue
                    completed <- true
                | Error _ -> completed <- true
                | _ when DateTimeOffset.UtcNow >= deadline -> completed <- true
                | _ -> do! Task.Delay(TimeSpan.FromSeconds(float pollSeconds))

            match lastResult with
            | Some result -> return result
            | None -> return Error(GraceError.Create "Approval request wait did not fetch a request." graceIds.CorrelationId)
        }

    type Action<'T>(handler: ParseResult -> Task<GraceResult<'T>>) =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) : Task<int> =
            task {
                let! result = handler parseResult
                return result |> renderOutput parseResult
            }

    let private action<'T> (handler: ParseResult -> Task<GraceResult<'T>>) = Action<'T>(handler)

    let Build =
        let addCommonOptions (command: Command) =
            command
            |> addOption Options.ownerName
            |> addOption Options.ownerId
            |> addOption Options.organizationName
            |> addOption Options.organizationId
            |> addOption Options.repositoryName
            |> addOption Options.repositoryId
            |> addOption Options.targetBranchId

        let approvalCommand = new Command("approval", Description = "Manage approval policies and approval requests.")
        let policyCommand = new Command("policy", Description = "Manage approval policies.")

        let createPolicyCommand =
            new Command("create", Description = "Create an approval policy.")
            |> addOption Options.name
            |> addOption Options.subject
            |> addOption Options.requiredResponder
            |> addOption Options.notificationUrl
            |> addOption Options.allowUnsafeLocal
            |> addOption Options.timeoutSeconds
            |> addOption Options.onTimeout
            |> addCommonOptions

        createPolicyCommand.Action <- action createPolicyHandler
        policyCommand.Subcommands.Add(createPolicyCommand)

        let listPolicyCommand =
            new Command("list", Description = "List approval policies.")
            |> addOption Options.includeDeleted
            |> addCommonOptions

        listPolicyCommand.Action <- action listPolicyHandler
        policyCommand.Subcommands.Add(listPolicyCommand)

        let showPolicyCommand =
            new Command("show", Description = "Show an approval policy.")
            |> addOption Options.policyId
            |> addCommonOptions

        showPolicyCommand.Action <- action showPolicyHandler
        policyCommand.Subcommands.Add(showPolicyCommand)

        let updatePolicyCommand =
            new Command("update", Description = "Update an approval policy.")
            |> addOption Options.policyId
            |> addOption Options.name
            |> addOption Options.subject
            |> addOption Options.requiredResponder
            |> addOption Options.notificationUrl
            |> addOption Options.allowUnsafeLocal
            |> addOption Options.timeoutSeconds
            |> addOption Options.onTimeout
            |> addCommonOptions

        updatePolicyCommand.Action <- action updatePolicyHandler
        policyCommand.Subcommands.Add(updatePolicyCommand)

        let enablePolicyCommand =
            new Command("enable", Description = "Enable an approval policy.")
            |> addOption Options.policyId
            |> addCommonOptions

        enablePolicyCommand.Action <-
            action (
                simplePolicyHandler
                    (fun id -> Grace.Shared.Parameters.Approval.EnableApprovalPolicyParameters(ApprovalPolicyId = id))
                    Grace.SDK.ApprovalPolicy.Enable
            )

        policyCommand.Subcommands.Add(enablePolicyCommand)

        let disablePolicyCommand =
            new Command("disable", Description = "Disable an approval policy.")
            |> addOption Options.policyId
            |> addCommonOptions

        disablePolicyCommand.Action <-
            action (
                simplePolicyHandler
                    (fun id -> Grace.Shared.Parameters.Approval.DisableApprovalPolicyParameters(ApprovalPolicyId = id))
                    Grace.SDK.ApprovalPolicy.Disable
            )

        policyCommand.Subcommands.Add(disablePolicyCommand)

        let deletePolicyCommand =
            new Command("delete", Description = "Delete an approval policy.")
            |> addOption Options.policyId
            |> addCommonOptions

        deletePolicyCommand.Action <-
            action (
                simplePolicyHandler
                    (fun id -> Grace.Shared.Parameters.Approval.DeleteApprovalPolicyParameters(ApprovalPolicyId = id))
                    Grace.SDK.ApprovalPolicy.Delete
            )

        policyCommand.Subcommands.Add(deletePolicyCommand)

        let evaluatePolicyCommand =
            new Command("evaluate", Description = "Evaluate approval policies for a subject.")
            |> addOption Options.subject
            |> addCommonOptions

        evaluatePolicyCommand.Action <- action evaluatePolicyHandler
        policyCommand.Subcommands.Add(evaluatePolicyCommand)

        approvalCommand.Subcommands.Add(policyCommand)

        let requestCommand = new Command("request", Description = "Inspect and respond to approval requests.")

        let listRequestCommand =
            new Command("list", Description = "List approval requests.")
            |> addOption Options.includeTerminal
            |> addCommonOptions

        listRequestCommand.Action <- action listRequestHandler
        requestCommand.Subcommands.Add(listRequestCommand)

        let showRequestCommand =
            new Command("show", Description = "Show an approval request.")
            |> addOption Options.requestId
            |> addCommonOptions

        showRequestCommand.Action <- action showRequestHandler
        requestCommand.Subcommands.Add(showRequestCommand)

        let approveRequestCommand =
            new Command("approve", Description = "Approve an approval request.")
            |> addOption Options.requestId
            |> addOption Options.reason
            |> addOption Options.clientDecisionId
            |> addCommonOptions

        approveRequestCommand.Action <- action approveRequestHandler
        requestCommand.Subcommands.Add(approveRequestCommand)

        let rejectRequestCommand =
            new Command("reject", Description = "Reject an approval request.")
            |> addOption Options.requestId
            |> addOption Options.reason
            |> addOption Options.clientDecisionId
            |> addCommonOptions

        rejectRequestCommand.Action <- action rejectRequestHandler
        requestCommand.Subcommands.Add(rejectRequestCommand)

        let waitRequestCommand =
            new Command("wait", Description = "Wait for an approval request to reach a terminal state.")
            |> addOption Options.requestId
            |> addOption Options.waitTimeoutSeconds
            |> addOption Options.pollSeconds
            |> addCommonOptions

        waitRequestCommand.Action <- action waitRequestHandler
        requestCommand.Subcommands.Add(waitRequestCommand)

        let historyRequestCommand =
            new Command("history", Description = "Show approval request history.")
            |> addOption Options.requestId
            |> addCommonOptions

        historyRequestCommand.Action <- action historyRequestHandler
        requestCommand.Subcommands.Add(historyRequestCommand)

        approvalCommand.Subcommands.Add(requestCommand)
        approvalCommand
