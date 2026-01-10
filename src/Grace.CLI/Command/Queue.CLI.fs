namespace Grace.CLI.Command

open Grace.CLI.Common
open Grace.CLI.Services
open Grace.CLI.Text
open Grace.SDK
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Grace.Types.Queue
open Grace.Types.Types
open Spectre.Console
open System
open System.CommandLine
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.Threading
open System.Threading.Tasks

module QueueCommand =
    module private Options =
        let candidateId =
            new Option<string>(
                "--candidate",
                [| "--candidate-id" |],
                Required = true,
                Description = "The candidate ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne
            )

        let candidateIdOptional =
            new Option<string>(
                "--candidate",
                [| "--candidate-id" |],
                Required = false,
                Description = "The candidate ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne
            )

        let promotionGroupId =
            new Option<string>(
                "--promotion-group",
                [| "--promotion-group-id" |],
                Required = false,
                Description = "The promotion group ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne
            )

        let workItemId =
            new Option<string>(
                "--work",
                [| "--work-item-id"; "-w" |],
                Required = false,
                Description = "The work item ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne
            )

        let policySnapshotId =
            new Option<string>(
                "--policy-snapshot-id",
                Required = false,
                Description = "Policy snapshot ID to initialize the queue.",
                Arity = ArgumentArity.ExactlyOne
            )

        let branch = new Option<string>("--branch", [| "-b" |], Required = false, Description = "Target branch ID or name.", Arity = ArgumentArity.ExactlyOne)

        let branchId =
            new Option<BranchId>(
                OptionName.BranchId,
                Required = false,
                Description = "Target branch ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> BranchId.Empty)
            )

        let branchName =
            new Option<string>(
                OptionName.BranchName,
                Required = false,
                Description = "Target branch name. [default: current branch]",
                Arity = ArgumentArity.ExactlyOne
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

    let private tryParseGuid (value: string) (error: QueueError) (parseResult: ParseResult) =
        let mutable parsed = Guid.Empty

        if
            String.IsNullOrWhiteSpace(value)
            || Guid.TryParse(value, &parsed) = false
            || parsed = Guid.Empty
        then
            Error(GraceError.Create (QueueError.getErrorMessage error) (getCorrelationId parseResult))
        else
            Ok parsed

    let private tryParseWorkItemId (value: string) (parseResult: ParseResult) =
        let mutable parsed = Guid.Empty

        if String.IsNullOrWhiteSpace(value) then
            Ok String.Empty
        elif Guid.TryParse(value, &parsed) = false || parsed = Guid.Empty then
            Error(GraceError.Create (WorkItemError.getErrorMessage WorkItemError.InvalidWorkItemId) (getCorrelationId parseResult))
        else
            Ok(parsed.ToString())

    let private resolveBranchByName (parseResult: ParseResult) (graceIds: GraceIds) (branchName: string) =
        task {
            let parameters =
                Parameters.Branch.GetBranchParameters(
                    BranchName = branchName,
                    OwnerId = graceIds.OwnerIdString,
                    OwnerName = graceIds.OwnerName,
                    OrganizationId = graceIds.OrganizationIdString,
                    OrganizationName = graceIds.OrganizationName,
                    RepositoryId = graceIds.RepositoryIdString,
                    RepositoryName = graceIds.RepositoryName,
                    CorrelationId = graceIds.CorrelationId
                )

            match! Grace.SDK.Branch.Get(parameters) with
            | Error error -> return Error error
            | Ok returnValue -> return Ok returnValue.ReturnValue.BranchId
        }

    let private resolveTargetBranchId (parseResult: ParseResult) (graceIds: GraceIds) =
        task {
            let branchRaw =
                parseResult.GetValue(Options.branch)
                |> Option.ofObj
                |> Option.defaultValue String.Empty

            if not (String.IsNullOrWhiteSpace branchRaw) then
                let mutable parsed = Guid.Empty

                if Guid.TryParse(branchRaw, &parsed) && parsed <> Guid.Empty then
                    return Ok parsed
                else
                    return! resolveBranchByName parseResult graceIds branchRaw
            elif graceIds.BranchId <> Guid.Empty then
                return Ok graceIds.BranchId
            elif not (String.IsNullOrWhiteSpace graceIds.BranchName) then
                return! resolveBranchByName parseResult graceIds graceIds.BranchName
            else
                return Error(GraceError.Create (QueueError.getErrorMessage QueueError.InvalidTargetBranchId) (getCorrelationId parseResult))
        }

    let private resolveTargetBranchIdFromPromotionGroup (parseResult: ParseResult) (graceIds: GraceIds) (promotionGroupIdRaw: string) =
        task {
            let mutable parsed = Guid.Empty

            if Guid.TryParse(promotionGroupIdRaw, &parsed) = false || parsed = Guid.Empty then
                return Error(GraceError.Create (PromotionGroupError.getErrorMessage PromotionGroupError.InvalidPromotionGroupId) (getCorrelationId parseResult))
            else
                let parameters =
                    Parameters.PromotionGroup.GetPromotionGroupParameters(
                        PromotionGroupId = parsed.ToString(),
                        OwnerId = graceIds.OwnerIdString,
                        OwnerName = graceIds.OwnerName,
                        OrganizationId = graceIds.OrganizationIdString,
                        OrganizationName = graceIds.OrganizationName,
                        RepositoryId = graceIds.RepositoryIdString,
                        RepositoryName = graceIds.RepositoryName,
                        CorrelationId = graceIds.CorrelationId
                    )

                match! PromotionGroup.Get(parameters) with
                | Error error -> return Error error
                | Ok returnValue -> return Ok returnValue.ReturnValue.TargetBranchId
        }

    let private resolvePolicySnapshotId (parseResult: ParseResult) (graceIds: GraceIds) (targetBranchId: Guid) =
        task {
            let rawPolicySnapshotId =
                parseResult.GetValue(Options.policySnapshotId)
                |> Option.ofObj
                |> Option.defaultValue String.Empty

            if not (String.IsNullOrWhiteSpace rawPolicySnapshotId) then
                return Ok rawPolicySnapshotId
            else
                let parameters =
                    Parameters.Policy.GetPolicyParameters(
                        TargetBranchId = targetBranchId.ToString(),
                        OwnerId = graceIds.OwnerIdString,
                        OwnerName = graceIds.OwnerName,
                        OrganizationId = graceIds.OrganizationIdString,
                        OrganizationName = graceIds.OrganizationName,
                        RepositoryId = graceIds.RepositoryIdString,
                        RepositoryName = graceIds.RepositoryName,
                        CorrelationId = graceIds.CorrelationId
                    )

                match! Policy.GetCurrent(parameters) with
                | Error error -> return Error error
                | Ok returnValue ->
                    match returnValue.ReturnValue with
                    | Some snapshot when not (String.IsNullOrWhiteSpace snapshot.PolicySnapshotId) -> return Ok snapshot.PolicySnapshotId
                    | _ -> return Ok String.Empty
        }

    let private writeQueueStatus (parseResult: ParseResult) (queue: PromotionQueue) =
        if not (parseResult |> json) && not (parseResult |> silent) then
            let table = Table(Border = TableBorder.Rounded)
            table.AddColumn(TableColumn("[bold]Field[/]").LeftAligned()) |> ignore
            table.AddColumn(TableColumn("[bold]Value[/]").LeftAligned()) |> ignore

            table.AddRow("Target branch", Markup.Escape(queue.TargetBranchId.ToString()))
            |> ignore

            table.AddRow("State", Markup.Escape(getDiscriminatedUnionCaseName queue.State))
            |> ignore

            table.AddRow("Candidate count", queue.CandidateIds.Length.ToString()) |> ignore

            match queue.RunningCandidateId with
            | Some candidateId ->
                table.AddRow("Running candidate", Markup.Escape(candidateId.ToString()))
                |> ignore
            | None -> table.AddRow("Running candidate", "-") |> ignore

            AnsiConsole.Write(table)

    let private statusHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames

                let! targetBranchIdResult = resolveTargetBranchId parseResult graceIds

                match targetBranchIdResult with
                | Error error -> return Error error
                | Ok targetBranchId ->
                    let parameters =
                        Parameters.Queue.QueueStatusParameters(
                            TargetBranchId = targetBranchId.ToString(),
                            OwnerId = graceIds.OwnerIdString,
                            OwnerName = graceIds.OwnerName,
                            OrganizationId = graceIds.OrganizationIdString,
                            OrganizationName = graceIds.OrganizationName,
                            RepositoryId = graceIds.RepositoryIdString,
                            RepositoryName = graceIds.RepositoryName,
                            CorrelationId = graceIds.CorrelationId
                        )

                    let! result = Queue.Status(parameters)

                    match result with
                    | Ok returnValue ->
                        writeQueueStatus parseResult returnValue.ReturnValue
                        return Ok returnValue
                    | Error error -> return Error error
            with ex ->
                return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    type Status() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) : Task<int> =
            task {
                let! result = statusHandler parseResult
                return result |> renderOutput parseResult
            }

    let private pauseHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames

                let! targetBranchIdResult = resolveTargetBranchId parseResult graceIds

                match targetBranchIdResult with
                | Error error -> return Error error
                | Ok targetBranchId ->
                    let parameters =
                        Parameters.Queue.QueueActionParameters(
                            TargetBranchId = targetBranchId.ToString(),
                            OwnerId = graceIds.OwnerIdString,
                            OwnerName = graceIds.OwnerName,
                            OrganizationId = graceIds.OrganizationIdString,
                            OrganizationName = graceIds.OrganizationName,
                            RepositoryId = graceIds.RepositoryIdString,
                            RepositoryName = graceIds.RepositoryName,
                            CorrelationId = graceIds.CorrelationId
                        )

                    let! result = Queue.Pause(parameters)

                    if not (parseResult |> json) && not (parseResult |> silent) then
                        AnsiConsole.MarkupLine("[green]Queue paused.[/]")

                    return result
            with ex ->
                return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    type Pause() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) : Task<int> =
            task {
                let! result = pauseHandler parseResult
                return result |> renderOutput parseResult
            }

    let private resumeHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames

                let! targetBranchIdResult = resolveTargetBranchId parseResult graceIds

                match targetBranchIdResult with
                | Error error -> return Error error
                | Ok targetBranchId ->
                    let parameters =
                        Parameters.Queue.QueueActionParameters(
                            TargetBranchId = targetBranchId.ToString(),
                            OwnerId = graceIds.OwnerIdString,
                            OwnerName = graceIds.OwnerName,
                            OrganizationId = graceIds.OrganizationIdString,
                            OrganizationName = graceIds.OrganizationName,
                            RepositoryId = graceIds.RepositoryIdString,
                            RepositoryName = graceIds.RepositoryName,
                            CorrelationId = graceIds.CorrelationId
                        )

                    let! result = Queue.Resume(parameters)

                    if not (parseResult |> json) && not (parseResult |> silent) then
                        AnsiConsole.MarkupLine("[green]Queue resumed.[/]")

                    return result
            with ex ->
                return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    type Resume() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) : Task<int> =
            task {
                let! result = resumeHandler parseResult
                return result |> renderOutput parseResult
            }

    let private buildEnqueueParameters
        (graceIds: GraceIds)
        (targetBranchId: Guid)
        (candidateId: Guid)
        (promotionGroupIdRaw: string)
        (workItemId: string)
        (policySnapshotId: string)
        =
        Parameters.Queue.EnqueueParameters(
            TargetBranchId = targetBranchId.ToString(),
            CandidateId = candidateId.ToString(),
            PromotionGroupId = promotionGroupIdRaw,
            WorkItemId = workItemId,
            PolicySnapshotId = policySnapshotId,
            OwnerId = graceIds.OwnerIdString,
            OwnerName = graceIds.OwnerName,
            OrganizationId = graceIds.OrganizationIdString,
            OrganizationName = graceIds.OrganizationName,
            RepositoryId = graceIds.RepositoryIdString,
            RepositoryName = graceIds.RepositoryName,
            CorrelationId = graceIds.CorrelationId
        )

    let private enqueueHandlerImpl (parseResult: ParseResult) =
        task {
            if parseResult |> verbose then printParseResult parseResult
            let graceIds = parseResult |> getNormalizedIdsAndNames

            let candidateIdRaw =
                parseResult.GetValue(Options.candidateIdOptional)
                |> Option.ofObj
                |> Option.defaultValue (Guid.NewGuid().ToString())

            match tryParseGuid candidateIdRaw QueueError.InvalidCandidateId parseResult with
            | Error error -> return Error error
            | Ok candidateId ->
                let promotionGroupIdRaw =
                    parseResult.GetValue(Options.promotionGroupId)
                    |> Option.ofObj
                    |> Option.defaultValue String.Empty

                let! targetBranchIdResult =
                    if String.IsNullOrWhiteSpace promotionGroupIdRaw then
                        resolveTargetBranchId parseResult graceIds
                    else
                        resolveTargetBranchIdFromPromotionGroup parseResult graceIds promotionGroupIdRaw

                match targetBranchIdResult with
                | Error error -> return Error error
                | Ok targetBranchId ->
                    let! policySnapshotIdResult = resolvePolicySnapshotId parseResult graceIds targetBranchId

                    match policySnapshotIdResult with
                    | Error error -> return Error error
                    | Ok policySnapshotId ->
                        let workItemIdRaw =
                            parseResult.GetValue(Options.workItemId)
                            |> Option.ofObj
                            |> Option.defaultValue String.Empty

                        match tryParseWorkItemId workItemIdRaw parseResult with
                        | Error error -> return Error error
                        | Ok workItemId ->
                            let parameters =
                                buildEnqueueParameters graceIds targetBranchId candidateId promotionGroupIdRaw workItemId policySnapshotId

                            let! result = Queue.Enqueue(parameters)

                            if not (parseResult |> json) && not (parseResult |> silent) then
                                AnsiConsole.MarkupLine($"[green]Enqueued candidate[/] {Markup.Escape(candidateId.ToString())}")

                            return result
        }

    let private enqueueHandler (parseResult: ParseResult) =
        task {
            try
                return! enqueueHandlerImpl parseResult
            with ex ->
                return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    type Enqueue() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) : Task<int> =
            task {
                let! result = enqueueHandler parseResult
                return result |> renderOutput parseResult
            }

    let private dequeueHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames

                let candidateIdRaw = parseResult.GetValue(Options.candidateId)

                match tryParseGuid candidateIdRaw QueueError.InvalidCandidateId parseResult with
                | Error error -> return Error error
                | Ok candidateId ->
                    let! targetBranchIdResult = resolveTargetBranchId parseResult graceIds

                    match targetBranchIdResult with
                    | Error error -> return Error error
                    | Ok targetBranchId ->
                        let parameters =
                            Parameters.Queue.CandidateActionParameters(
                                TargetBranchId = targetBranchId.ToString(),
                                CandidateId = candidateId.ToString(),
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                CorrelationId = graceIds.CorrelationId
                            )

                        let! result = Queue.Dequeue(parameters)

                        if not (parseResult |> json) && not (parseResult |> silent) then
                            AnsiConsole.MarkupLine($"[green]Dequeued candidate[/] {Markup.Escape(candidateId.ToString())}")

                        return result
            with ex ->
                return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    type Dequeue() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) : Task<int> =
            task {
                let! result = dequeueHandler parseResult
                return result |> renderOutput parseResult
            }

    let private retryHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames

                let candidateIdRaw = parseResult.GetValue(Options.candidateId)

                match tryParseGuid candidateIdRaw QueueError.InvalidCandidateId parseResult with
                | Error error -> return Error error
                | Ok candidateId ->
                    let! targetBranchIdResult = resolveTargetBranchId parseResult graceIds

                    match targetBranchIdResult with
                    | Error error -> return Error error
                    | Ok targetBranchId ->
                        let parameters =
                            Parameters.Queue.CandidateActionParameters(
                                TargetBranchId = targetBranchId.ToString(),
                                CandidateId = candidateId.ToString(),
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                CorrelationId = graceIds.CorrelationId
                            )

                        let! result = Candidate.Retry(parameters)

                        if not (parseResult |> json) && not (parseResult |> silent) then
                            AnsiConsole.MarkupLine($"[green]Retry requested for candidate[/] {Markup.Escape(candidateId.ToString())}")

                        return result
            with ex ->
                return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    type Retry() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) : Task<int> =
            task {
                let! result = retryHandler parseResult
                return result |> renderOutput parseResult
            }

    let Build =
        let addCommonOptions (command: Command) =
            command
            |> addOption Options.ownerName
            |> addOption Options.ownerId
            |> addOption Options.organizationName
            |> addOption Options.organizationId
            |> addOption Options.repositoryName
            |> addOption Options.repositoryId

        let addBranchOptions (command: Command) =
            command
            |> addOption Options.branch
            |> addOption Options.branchId
            |> addOption Options.branchName

        let queueCommand = new Command("queue", Description = "Manage promotion queues.")

        let statusCommand =
            new Command("status", Description = "Get the status of a promotion queue.")
            |> addBranchOptions
            |> addCommonOptions

        statusCommand.Action <- new Status()
        queueCommand.Subcommands.Add(statusCommand)

        let enqueueCommand =
            new Command("enqueue", Description = "Enqueue a candidate in a promotion queue.")
            |> addOption Options.candidateIdOptional
            |> addOption Options.promotionGroupId
            |> addOption Options.workItemId
            |> addOption Options.policySnapshotId
            |> addBranchOptions
            |> addCommonOptions

        enqueueCommand.Action <- new Enqueue()
        queueCommand.Subcommands.Add(enqueueCommand)

        let pauseCommand =
            new Command("pause", Description = "Pause a promotion queue.")
            |> addBranchOptions
            |> addCommonOptions

        pauseCommand.Action <- new Pause()
        queueCommand.Subcommands.Add(pauseCommand)

        let resumeCommand =
            new Command("resume", Description = "Resume a promotion queue.")
            |> addBranchOptions
            |> addCommonOptions

        resumeCommand.Action <- new Resume()
        queueCommand.Subcommands.Add(resumeCommand)

        let dequeueCommand =
            new Command("dequeue", Description = "Dequeue a candidate (admin).")
            |> addOption Options.candidateId
            |> addBranchOptions
            |> addCommonOptions

        dequeueCommand.Action <- new Dequeue()
        queueCommand.Subcommands.Add(dequeueCommand)

        let retryCommand =
            new Command("retry", Description = "Retry a candidate.")
            |> addOption Options.candidateId
            |> addBranchOptions
            |> addCommonOptions

        retryCommand.Action <- new Retry()
        queueCommand.Subcommands.Add(retryCommand)

        queueCommand
