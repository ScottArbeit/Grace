namespace Grace.CLI.Command

open Grace.CLI.Common
open Grace.CLI.Services
open Grace.CLI.Text
open Grace.SDK
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Grace.Types.WorkItem
open Grace.Types.Types
open Spectre.Console
open Spectre.Console.Json
open System
open System.CommandLine
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.Threading
open System.Threading.Tasks

module WorkItemCommand =

    module private Options =
        let workItemId =
            new Option<string>("--work-item-id", [| "-w" |], Required = false, Description = "The work item ID <Guid>.", Arity = ArgumentArity.ExactlyOne)

        let workItemIdRequired =
            new Option<string>("--work-item-id", [| "-w" |], Required = true, Description = "The work item ID <Guid>.", Arity = ArgumentArity.ExactlyOne)

        let title = new Option<string>("--title", Required = true, Description = "Title for the work item.", Arity = ArgumentArity.ExactlyOne)

        let description =
            new Option<string>(
                OptionName.Description,
                [| "-d" |],
                Required = false,
                Description = "Description for the work item.",
                Arity = ArgumentArity.ExactlyOne
            )

        let statusSet =
            (new Option<string>("--set", Required = true, Description = "Set the work item status.", Arity = ArgumentArity.ExactlyOne))
                .AcceptOnlyFromAmong(listCases<WorkItemStatus> ())

        let referenceId =
            new Option<string>(OptionName.ReferenceId, Required = true, Description = "The reference ID <Guid>.", Arity = ArgumentArity.ExactlyOne)

        let promotionGroupId =
            new Option<string>(
                "--promotion-group-id",
                [| "-g" |],
                Required = true,
                Description = "The promotion group ID <Guid>.",
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

    module private Arguments =
        let workItemId = new Argument<string>("work-item-id", Description = "Work item ID <Guid>.")

        let referenceId = new Argument<string>("reference-id", Description = "Reference ID <Guid>.")

        let promotionGroupId = new Argument<string>("promotion-group-id", Description = "Promotion group ID <Guid>.")

    let private tryParseGuid (value: string) (error: WorkItemError) (parseResult: ParseResult) =
        let mutable parsed = Guid.Empty

        if
            String.IsNullOrWhiteSpace(value)
            || Guid.TryParse(value, &parsed) = false
            || parsed = Guid.Empty
        then
            Error(GraceError.Create (WorkItemError.getErrorMessage error) (getCorrelationId parseResult))
        else
            Ok parsed

    let private createHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames

                let title = parseResult.GetValue(Options.title)

                if String.IsNullOrWhiteSpace title then
                    return Error(GraceError.Create "Title is required." (getCorrelationId parseResult))
                else
                    let description =
                        parseResult.GetValue(Options.description)
                        |> Option.ofObj
                        |> Option.defaultValue String.Empty

                    let workItemId =
                        parseResult.GetValue(Options.workItemId)
                        |> Option.ofObj
                        |> Option.defaultValue (Guid.NewGuid().ToString())

                    let parameters =
                        Parameters.WorkItem.CreateWorkItemParameters(
                            WorkItemId = workItemId,
                            Title = title,
                            Description = description,
                            OwnerId = graceIds.OwnerIdString,
                            OwnerName = graceIds.OwnerName,
                            OrganizationId = graceIds.OrganizationIdString,
                            OrganizationName = graceIds.OrganizationName,
                            RepositoryId = graceIds.RepositoryIdString,
                            RepositoryName = graceIds.RepositoryName,
                            CorrelationId = graceIds.CorrelationId
                        )

                    if parseResult |> hasOutput then
                        return!
                            progress
                                .Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                    task {
                                        let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                        let! result = WorkItem.Create(parameters)
                                        t0.Increment(100.0)
                                        return result
                                    })
                    else
                        return! WorkItem.Create(parameters)
            with ex ->
                return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    type Create() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                let! result = createHandler parseResult
                return result |> renderOutput parseResult
            }

    let private showHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames
                let workItemIdRaw = parseResult.GetValue(Arguments.workItemId)

                match tryParseGuid workItemIdRaw WorkItemError.InvalidWorkItemId parseResult with
                | Error error -> return Error error
                | Ok workItemId ->
                    let parameters =
                        Parameters.WorkItem.GetWorkItemParameters(
                            WorkItemId = workItemId.ToString(),
                            OwnerId = graceIds.OwnerIdString,
                            OwnerName = graceIds.OwnerName,
                            OrganizationId = graceIds.OrganizationIdString,
                            OrganizationName = graceIds.OrganizationName,
                            RepositoryId = graceIds.RepositoryIdString,
                            RepositoryName = graceIds.RepositoryName,
                            CorrelationId = graceIds.CorrelationId
                        )

                    let! result = WorkItem.Get(parameters)

                    match result with
                    | Ok graceReturnValue ->
                        if parseResult |> hasOutput then
                            let jsonText = JsonText(serialize graceReturnValue.ReturnValue)
                            AnsiConsole.Write(jsonText)
                            AnsiConsole.WriteLine()

                        return Ok graceReturnValue
                    | Error error -> return Error error
            with ex ->
                return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    type Show() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                let! result = showHandler parseResult
                return result |> renderOutput parseResult
            }

    let private statusHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames
                let workItemIdRaw = parseResult.GetValue(Arguments.workItemId)

                match tryParseGuid workItemIdRaw WorkItemError.InvalidWorkItemId parseResult with
                | Error error -> return Error error
                | Ok workItemId ->
                    let statusValue = parseResult.GetValue(Options.statusSet)

                    match discriminatedUnionFromString<WorkItemStatus> statusValue with
                    | None -> return Error(GraceError.Create (WorkItemError.getErrorMessage WorkItemError.InvalidStatus) (getCorrelationId parseResult))
                    | Some status ->
                        let parameters =
                            Parameters.WorkItem.UpdateWorkItemParameters(
                                WorkItemId = workItemId.ToString(),
                                Status = status.ToString(),
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                CorrelationId = graceIds.CorrelationId
                            )

                        return! WorkItem.Update(parameters)
            with ex ->
                return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    type Status() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                let! result = statusHandler parseResult
                return result |> renderOutput parseResult
            }

    let private linkReferenceHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames
                let workItemIdRaw = parseResult.GetValue(Arguments.workItemId)
                let referenceIdRaw = parseResult.GetValue(Arguments.referenceId)

                match tryParseGuid workItemIdRaw WorkItemError.InvalidWorkItemId parseResult with
                | Error error -> return Error error
                | Ok workItemId ->
                    match tryParseGuid referenceIdRaw WorkItemError.InvalidReferenceId parseResult with
                    | Error error -> return Error error
                    | Ok referenceId ->
                        let parameters =
                            Parameters.WorkItem.LinkReferenceParameters(
                                WorkItemId = workItemId.ToString(),
                                ReferenceId = referenceId.ToString(),
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                CorrelationId = graceIds.CorrelationId
                            )

                        return! WorkItem.LinkReference(parameters)
            with ex ->
                return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    type LinkReference() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                let! result = linkReferenceHandler parseResult
                return result |> renderOutput parseResult
            }

    let private linkPromotionGroupHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames
                let workItemIdRaw = parseResult.GetValue(Arguments.workItemId)
                let promotionGroupIdRaw = parseResult.GetValue(Arguments.promotionGroupId)

                match tryParseGuid workItemIdRaw WorkItemError.InvalidWorkItemId parseResult with
                | Error error -> return Error error
                | Ok workItemId ->
                    match tryParseGuid promotionGroupIdRaw WorkItemError.InvalidPromotionGroupId parseResult with
                    | Error error -> return Error error
                    | Ok promotionGroupId ->
                        let parameters =
                            Parameters.WorkItem.LinkPromotionGroupParameters(
                                WorkItemId = workItemId.ToString(),
                                PromotionGroupId = promotionGroupId.ToString(),
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                CorrelationId = graceIds.CorrelationId
                            )

                        return! WorkItem.LinkPromotionGroup(parameters)
            with ex ->
                return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    type LinkPromotionGroup() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                let! result = linkPromotionGroupHandler parseResult
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

        let workCommand = new Command("work", Description = "Create and manage work items.")
        workCommand.Aliases.Add("work-item")
        workCommand.Aliases.Add("wi")

        let createCommand =
            new Command("create", Description = "Create a new work item.")
            |> addOption Options.workItemId
            |> addOption Options.title
            |> addOption Options.description
            |> addCommonOptions

        createCommand.Action <- new Create()
        workCommand.Subcommands.Add(createCommand)

        let showCommand = new Command("show", Description = "Show a work item.") |> addCommonOptions

        showCommand.Arguments.Add(Arguments.workItemId)
        showCommand.Action <- new Show()
        workCommand.Subcommands.Add(showCommand)

        let statusCommand =
            new Command("status", Description = "Update the status of a work item.")
            |> addOption Options.statusSet
            |> addCommonOptions

        statusCommand.Arguments.Add(Arguments.workItemId)
        statusCommand.Action <- new Status()
        workCommand.Subcommands.Add(statusCommand)

        let linkCommand = new Command("link", Description = "Link artifacts to a work item.")

        let linkRefCommand =
            new Command("ref", Description = "Link a reference to a work item.")
            |> addCommonOptions

        linkRefCommand.Arguments.Add(Arguments.workItemId)
        linkRefCommand.Arguments.Add(Arguments.referenceId)
        linkRefCommand.Action <- new LinkReference()
        linkCommand.Subcommands.Add(linkRefCommand)

        let linkGroupCommand =
            new Command("group", Description = "Link a promotion group to a work item.")
            |> addCommonOptions

        linkGroupCommand.Arguments.Add(Arguments.workItemId)
        linkGroupCommand.Arguments.Add(Arguments.promotionGroupId)
        linkGroupCommand.Action <- new LinkPromotionGroup()
        linkCommand.Subcommands.Add(linkGroupCommand)

        workCommand.Subcommands.Add(linkCommand)
        workCommand
