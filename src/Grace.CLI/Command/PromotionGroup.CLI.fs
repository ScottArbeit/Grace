namespace Grace.CLI.Command

open FSharpPlus
open Grace.CLI.Common
open Grace.CLI.Services
open Grace.CLI.Text
open Grace.SDK
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Client.Theme
open Grace.Shared.Parameters.PromotionGroup
open Grace.Shared.Services
open Grace.Shared.Resources
open Grace.Shared.Utilities
open Grace.Shared.Validation
open Grace.Shared.Validation.Errors
open Grace.Types.PromotionGroup
open Grace.Types.Types
open NodaTime
open NodaTime.Text
open Spectre.Console
open Spectre.Console.Json
open System
open System.Collections.Generic
open System.CommandLine
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.Threading
open System.Threading.Tasks

module PromotionGroupCommand =

    type CommonParameters() =
        inherit ParameterBase()
        member val public PromotionGroupId: string = String.Empty with get, set
        member val public OwnerId: string = String.Empty with get, set
        member val public OwnerName: string = String.Empty with get, set
        member val public OrganizationId: string = String.Empty with get, set
        member val public OrganizationName: string = String.Empty with get, set
        member val public RepositoryId: string = String.Empty with get, set
        member val public RepositoryName: string = String.Empty with get, set

    module private Options =
        let promotionGroupId =
            new Option<String>(
                "--promotion-group-id",
                [| "-g" |],
                Required = false,
                Description = "The promotion group ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne
            )

        let promotionGroupIdRequired =
            new Option<String>(
                "--promotion-group-id",
                [| "-g" |],
                Required = true,
                Description = "The promotion group ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne
            )

        let targetBranchId =
            new Option<String>("--target-branch-id", Required = false, Description = "The target branch ID <Guid>.", Arity = ArgumentArity.ExactlyOne)

        let targetBranchName =
            new Option<String>("--target-branch-name", [| "-t" |], Required = false, Description = "The target branch name.", Arity = ArgumentArity.ExactlyOne)

        let description =
            new Option<String>(
                "--description",
                [| "-d" |],
                Required = false,
                Description = "Description of the promotion group.",
                Arity = ArgumentArity.ExactlyOne
            )

        let scheduledAt =
            new Option<String>(
                "--scheduled-at",
                Required = false,
                Description = "Scheduled execution time (ISO 8601 format).",
                Arity = ArgumentArity.ExactlyOne
            )

        let promotionId =
            new Option<String>("--promotion-id", [| "-p" |], Required = true, Description = "The promotion ID <Guid>.", Arity = ArgumentArity.ExactlyOne)

        let promotionIds =
            new Option<String[]>(
                "--promotion-ids",
                Required = true,
                Description = "List of promotion IDs in the desired order.",
                Arity = ArgumentArity.OneOrMore
            )

        let reason =
            new Option<String>("--reason", [| "-r" |], Required = true, Description = "Reason for blocking the group.", Arity = ArgumentArity.ExactlyOne)

        let success =
            new Option<Boolean>("--success", Required = true, Description = "Whether the group completed successfully.", Arity = ArgumentArity.ExactlyOne)

        let force =
            new Option<Boolean>(
                "--force",
                [| "-f" |],
                Required = false,
                Description = "Force delete even if not in deletable state.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> false)
            )

        let deleteReason =
            new Option<String>("--delete-reason", Required = false, Description = "Reason for deleting the group.", Arity = ArgumentArity.ExactlyOne)

        let status =
            (new Option<String>(
                "--status",
                [| "-s" |],
                Required = false,
                Description = "Filter by status: Draft, Ready, Running, Succeeded, Failed, or Blocked.",
                Arity = ArgumentArity.ExactlyOne
            ))
                .AcceptOnlyFromAmong(listCases<PromotionGroupStatus> ())

        let maxCount =
            new Option<int>(
                "--max-count",
                Required = false,
                Description = "Maximum number of promotion groups to return.",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> 30)
            )

        let ownerId = new Option<String>("--owner-id", Required = false, Description = "The repository's owner ID <Guid>.", Arity = ArgumentArity.ZeroOrOne)

        let ownerName =
            new Option<String>(
                "--owner-name",
                Required = false,
                Description = "The repository's owner name. [default: current owner]",
                Arity = ArgumentArity.ExactlyOne
            )

        let organizationId =
            new Option<String>("--organization-id", Required = false, Description = "The organization's ID <Guid>.", Arity = ArgumentArity.ZeroOrOne)

        let organizationName =
            new Option<String>(
                "--organization-name",
                Required = false,
                Description = "The organization's name. [default: current organization]",
                Arity = ArgumentArity.ExactlyOne
            )

        let repositoryId = new Option<String>("--repository-id", Required = false, Description = "The repository's ID <Guid>.", Arity = ArgumentArity.ZeroOrOne)

        let repositoryName =
            new Option<String>(
                "--repository-name",
                Required = false,
                Description = "The repository's name. [default: current repository]",
                Arity = ArgumentArity.ExactlyOne
            )

    let private createHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames

                let targetBranchId =
                    parseResult.GetValue(Options.targetBranchId)
                    |> Option.ofObj
                    |> Option.defaultValue String.Empty

                let targetBranchName =
                    parseResult.GetValue(Options.targetBranchName)
                    |> Option.ofObj
                    |> Option.defaultValue String.Empty

                let description =
                    parseResult.GetValue(Options.description)
                    |> Option.ofObj
                    |> Option.defaultValue String.Empty

                let scheduledAt =
                    parseResult.GetValue(Options.scheduledAt)
                    |> Option.ofObj
                    |> Option.defaultValue String.Empty

                let promotionGroupId =
                    parseResult.GetValue(Options.promotionGroupId)
                    |> Option.ofObj
                    |> Option.defaultValue (Guid.NewGuid().ToString())

                let parameters =
                    CreatePromotionGroupParameters(
                        PromotionGroupId = promotionGroupId,
                        OwnerId = graceIds.OwnerIdString,
                        OwnerName = graceIds.OwnerName,
                        OrganizationId = graceIds.OrganizationIdString,
                        OrganizationName = graceIds.OrganizationName,
                        RepositoryId = graceIds.RepositoryIdString,
                        RepositoryName = graceIds.RepositoryName,
                        TargetBranchId = targetBranchId,
                        TargetBranchName = targetBranchName,
                        Description = description,
                        ScheduledAt = scheduledAt,
                        CorrelationId = graceIds.CorrelationId
                    )

                if parseResult |> hasOutput then
                    return!
                        progress
                            .Columns(progressColumns)
                            .StartAsync(fun progressContext ->
                                task {
                                    let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                    let! result = PromotionGroup.Create(parameters)
                                    t0.Increment(100.0)
                                    return result
                                })
                else
                    return! PromotionGroup.Create(parameters)
            with ex ->
                return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    type Create() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                let! result = createHandler parseResult
                return result |> renderOutput parseResult
            }

    let private getHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames
                let promotionGroupId = parseResult.GetValue(Options.promotionGroupIdRequired)

                let parameters =
                    GetPromotionGroupParameters(
                        PromotionGroupId = promotionGroupId,
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
                                    let! result = PromotionGroup.Get(parameters)
                                    t0.Increment(100.0)
                                    return result
                                })
                else
                    return! PromotionGroup.Get(parameters)
            with ex ->
                return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    type Get() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                let! result = getHandler parseResult
                return result |> renderOutput parseResult
            }

    let private addPromotionHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames
                let promotionGroupId = parseResult.GetValue(Options.promotionGroupIdRequired)
                let promotionId = parseResult.GetValue(Options.promotionId)

                let parameters =
                    AddPromotionParameters(
                        PromotionGroupId = promotionGroupId,
                        PromotionId = promotionId,
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
                                    let! result = PromotionGroup.AddPromotion(parameters)
                                    t0.Increment(100.0)
                                    return result
                                })
                else
                    return! PromotionGroup.AddPromotion(parameters)
            with ex ->
                return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    type AddPromotion() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                let! result = addPromotionHandler parseResult
                return result |> renderOutput parseResult
            }

    let private removePromotionHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames
                let promotionGroupId = parseResult.GetValue(Options.promotionGroupIdRequired)
                let promotionId = parseResult.GetValue(Options.promotionId)

                let parameters =
                    RemovePromotionParameters(
                        PromotionGroupId = promotionGroupId,
                        PromotionId = promotionId,
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
                                    let! result = PromotionGroup.RemovePromotion(parameters)
                                    t0.Increment(100.0)
                                    return result
                                })
                else
                    return! PromotionGroup.RemovePromotion(parameters)
            with ex ->
                return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    type RemovePromotion() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                let! result = removePromotionHandler parseResult
                return result |> renderOutput parseResult
            }

    let private reorderHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames
                let promotionGroupId = parseResult.GetValue(Options.promotionGroupIdRequired)
                let promotionIds = parseResult.GetValue(Options.promotionIds)

                let parameters =
                    ReorderPromotionsParameters(
                        PromotionGroupId = promotionGroupId,
                        PromotionIds = promotionIds,
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
                                    let! result = PromotionGroup.ReorderPromotions(parameters)
                                    t0.Increment(100.0)
                                    return result
                                })
                else
                    return! PromotionGroup.ReorderPromotions(parameters)
            with ex ->
                return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    type Reorder() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                let! result = reorderHandler parseResult
                return result |> renderOutput parseResult
            }

    let private scheduleHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames
                let promotionGroupId = parseResult.GetValue(Options.promotionGroupIdRequired)

                let scheduledAt =
                    parseResult.GetValue(Options.scheduledAt)
                    |> Option.ofObj
                    |> Option.defaultValue String.Empty

                let parameters =
                    ScheduleParameters(
                        PromotionGroupId = promotionGroupId,
                        ScheduledAt = scheduledAt,
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
                                    let! result = PromotionGroup.Schedule(parameters)
                                    t0.Increment(100.0)
                                    return result
                                })
                else
                    return! PromotionGroup.Schedule(parameters)
            with ex ->
                return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    type Schedule() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                let! result = scheduleHandler parseResult
                return result |> renderOutput parseResult
            }

    let private markReadyHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames
                let promotionGroupId = parseResult.GetValue(Options.promotionGroupIdRequired)

                let parameters =
                    MarkReadyParameters(
                        PromotionGroupId = promotionGroupId,
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
                                    let! result = PromotionGroup.MarkReady(parameters)
                                    t0.Increment(100.0)
                                    return result
                                })
                else
                    return! PromotionGroup.MarkReady(parameters)
            with ex ->
                return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    type MarkReady() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                let! result = markReadyHandler parseResult
                return result |> renderOutput parseResult
            }

    let private startHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames
                let promotionGroupId = parseResult.GetValue(Options.promotionGroupIdRequired)

                let parameters =
                    StartParameters(
                        PromotionGroupId = promotionGroupId,
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
                                    let! result = PromotionGroup.Start(parameters)
                                    t0.Increment(100.0)
                                    return result
                                })
                else
                    return! PromotionGroup.Start(parameters)
            with ex ->
                return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    type Start() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                let! result = startHandler parseResult
                return result |> renderOutput parseResult
            }

    let private blockHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames
                let promotionGroupId = parseResult.GetValue(Options.promotionGroupIdRequired)
                let reason = parseResult.GetValue(Options.reason)

                let parameters =
                    BlockParameters(
                        PromotionGroupId = promotionGroupId,
                        Reason = reason,
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
                                    let! result = PromotionGroup.Block(parameters)
                                    t0.Increment(100.0)
                                    return result
                                })
                else
                    return! PromotionGroup.Block(parameters)
            with ex ->
                return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    type Block() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                let! result = blockHandler parseResult
                return result |> renderOutput parseResult
            }

    let private deleteHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames
                let promotionGroupId = parseResult.GetValue(Options.promotionGroupIdRequired)
                let force = parseResult.GetValue(Options.force)

                let deleteReason =
                    parseResult.GetValue(Options.deleteReason)
                    |> Option.ofObj
                    |> Option.defaultValue String.Empty

                let parameters =
                    DeletePromotionGroupParameters(
                        PromotionGroupId = promotionGroupId,
                        Force = force,
                        DeleteReason = deleteReason,
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
                                    let! result = PromotionGroup.Delete(parameters)
                                    t0.Increment(100.0)
                                    return result
                                })
                else
                    return! PromotionGroup.Delete(parameters)
            with ex ->
                return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    type Delete() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                let! result = deleteHandler parseResult
                return result |> renderOutput parseResult
            }

    let private listHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames

                let targetBranchId =
                    parseResult.GetValue(Options.targetBranchId)
                    |> Option.ofObj
                    |> Option.defaultValue String.Empty

                let targetBranchName =
                    parseResult.GetValue(Options.targetBranchName)
                    |> Option.ofObj
                    |> Option.defaultValue String.Empty

                let status =
                    parseResult.GetValue(Options.status)
                    |> Option.ofObj
                    |> Option.defaultValue String.Empty

                let maxCount = parseResult.GetValue(Options.maxCount)

                let parameters =
                    ListPromotionGroupsParameters(
                        TargetBranchId = targetBranchId,
                        TargetBranchName = targetBranchName,
                        Status = status,
                        MaxCount = maxCount,
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
                                    let! result = PromotionGroup.List(parameters)
                                    t0.Increment(100.0)
                                    return result
                                })
                else
                    return! PromotionGroup.List(parameters)
            with ex ->
                return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    type List() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                let! result = listHandler parseResult
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

        // Create main command and aliases
        let promotionGroupCommand = new Command("promotion-group", Description = "Create, manage, or delete promotion groups.")
        promotionGroupCommand.Aliases.Add("pg")

        // Add subcommands
        let createCommand =
            new Command("create", Description = "Create a new promotion group.")
            |> addOption Options.promotionGroupId
            |> addOption Options.targetBranchId
            |> addOption Options.targetBranchName
            |> addOption Options.description
            |> addOption Options.scheduledAt
            |> addCommonOptions

        createCommand.Action <- new Create()
        promotionGroupCommand.Subcommands.Add(createCommand)

        let getCommand =
            new Command("get", Description = "Get details of a promotion group.")
            |> addOption Options.promotionGroupIdRequired
            |> addCommonOptions

        getCommand.Action <- new Get()
        promotionGroupCommand.Subcommands.Add(getCommand)

        let addPromotionCommand =
            new Command("add", Description = "Add a promotion to the group.")
            |> addOption Options.promotionGroupIdRequired
            |> addOption Options.promotionId
            |> addCommonOptions

        addPromotionCommand.Action <- new AddPromotion()
        promotionGroupCommand.Subcommands.Add(addPromotionCommand)

        let removePromotionCommand =
            new Command("remove", Description = "Remove a promotion from the group.")
            |> addOption Options.promotionGroupIdRequired
            |> addOption Options.promotionId
            |> addCommonOptions

        removePromotionCommand.Action <- new RemovePromotion()
        promotionGroupCommand.Subcommands.Add(removePromotionCommand)

        let reorderCommand =
            new Command("reorder", Description = "Reorder promotions in the group.")
            |> addOption Options.promotionGroupIdRequired
            |> addOption Options.promotionIds
            |> addCommonOptions

        reorderCommand.Action <- new Reorder()
        promotionGroupCommand.Subcommands.Add(reorderCommand)

        let scheduleCommand =
            new Command("schedule", Description = "Schedule the promotion group.")
            |> addOption Options.promotionGroupIdRequired
            |> addOption Options.scheduledAt
            |> addCommonOptions

        scheduleCommand.Action <- new Schedule()
        promotionGroupCommand.Subcommands.Add(scheduleCommand)

        let markReadyCommand =
            new Command("mark-ready", Description = "Mark the promotion group as ready.")
            |> addOption Options.promotionGroupIdRequired
            |> addCommonOptions

        markReadyCommand.Action <- new MarkReady()
        promotionGroupCommand.Subcommands.Add(markReadyCommand)

        let startCommand =
            new Command("start", Description = "Start execution of the promotion group.")
            |> addOption Options.promotionGroupIdRequired
            |> addCommonOptions

        startCommand.Aliases.Add("start-now")
        startCommand.Action <- new Start()
        promotionGroupCommand.Subcommands.Add(startCommand)

        let blockCommand =
            new Command("block", Description = "Block the promotion group.")
            |> addOption Options.promotionGroupIdRequired
            |> addOption Options.reason
            |> addCommonOptions

        blockCommand.Action <- new Block()
        promotionGroupCommand.Subcommands.Add(blockCommand)

        let deleteCommand =
            new Command("delete", Description = "Delete the promotion group.")
            |> addOption Options.promotionGroupIdRequired
            |> addOption Options.force
            |> addOption Options.deleteReason
            |> addCommonOptions

        deleteCommand.Action <- new Delete()
        promotionGroupCommand.Subcommands.Add(deleteCommand)

        let listCommand =
            new Command("list", Description = "List promotion groups for a target branch.")
            |> addOption Options.targetBranchId
            |> addOption Options.targetBranchName
            |> addOption Options.status
            |> addOption Options.maxCount
            |> addCommonOptions

        listCommand.Action <- new List()
        promotionGroupCommand.Subcommands.Add(listCommand)

        promotionGroupCommand
