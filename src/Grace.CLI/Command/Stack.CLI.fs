namespace Grace.CLI.Command

open Grace.CLI.Common
open Grace.CLI.Services
open Grace.CLI.Text
open Grace.SDK
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Client.Theme
open Grace.Shared.Parameters.Stack
open Grace.Shared.Utilities
open Grace.Types.Operation
open Grace.Types.Types
open Spectre.Console
open System
open System.CommandLine
open System.CommandLine.Parsing
open System.Threading.Tasks

module Stack =

    module private Options =
        let ownerId =
            new Option<OwnerId>(
                OptionName.OwnerId,
                Required = false,
                Description = "The repository's owner ID <Guid>.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> if Current().OwnerId = Guid.Empty then Guid.NewGuid() else Current().OwnerId)
            )

        let ownerName =
            new Option<String>(
                OptionName.OwnerName,
                Required = false,
                Description = "The repository's owner name. [default: current owner]",
                Arity = ArgumentArity.ExactlyOne
            )

        let organizationId =
            new Option<OrganizationId>(
                OptionName.OrganizationId,
                Required = false,
                Description = "The repository's organization ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory =
                    (fun _ ->
                        if Current().OrganizationId = Guid.Empty then
                            Guid.NewGuid()
                        else
                            Current().OrganizationId)
            )

        let organizationName =
            new Option<String>(
                OptionName.OrganizationName,
                Required = false,
                Description = "The repository's organization name. [default: current organization]",
                Arity = ArgumentArity.ZeroOrOne
            )

        let repositoryId =
            new Option<RepositoryId>(
                OptionName.RepositoryId,
                [| "-r" |],
                Required = false,
                Description = "The repository's ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory =
                    (fun _ -> if Current().RepositoryId = Guid.Empty then Guid.NewGuid() else Current().RepositoryId)
            )

        let repositoryName =
            new Option<String>(
                OptionName.RepositoryName,
                [| "-n" |],
                Required = false,
                Description = "The name of the repository. [default: current repository]",
                Arity = ArgumentArity.ExactlyOne
            )

        let stackId =
            new Option<String>(
                "--stack-id",
                [| "-s" |],
                Required = false,
                Description = "The stack ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne
            )

        let baseBranchId =
            new Option<BranchId>(
                "--base-branch-id",
                Required = false,
                Description = "The base branch ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne
            )

        let changeId =
            new Option<ChangeId>(
                "--change-id",
                [| "-c" |],
                Required = true,
                Description = "The change ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne
            )

        let branchId =
            new Option<BranchId>(
                "--branch-id",
                [| "-b" |],
                Required = false,
                Description = "The branch ID <Guid> for this stack layer.",
                Arity = ArgumentArity.ExactlyOne
            )

        let order =
            new Option<int>(
                "--order",
                Required = false,
                Description = "Order for the stack layer.",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> 0)
            )

        let asyncOption =
            new Option<bool>(
                "--async",
                Required = false,
                Description = "Return immediately without waiting for the operation to finish.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> false)
            )

    let private pollOperation (graceIds: GraceIds) (operationId: OperationId) =
        task {
            let parameters =
                Grace.Shared.Parameters.Operation.GetOperationParameters(
                    OwnerId = graceIds.OwnerIdString,
                    OrganizationId = graceIds.OrganizationIdString,
                    RepositoryId = graceIds.RepositoryIdString,
                    OperationId = $"{operationId}",
                    CorrelationId = graceIds.CorrelationId
                )

            let rec poll attempt =
                task {
                    if attempt > 60 then
                        return Error(GraceError.Create "Timed out waiting for operation." graceIds.CorrelationId)
                    else
                        match! Operation.Get(parameters) with
                        | Ok operationReturn ->
                            let operationDto = operationReturn.ReturnValue
                            match operationDto.Status with
                            | OperationStatus.Succeeded -> return Ok operationDto
                            | OperationStatus.Failed ->
                                let errorMessage = operationDto.Error |> Option.defaultValue "Unknown error"
                                return Error(GraceError.Create $"Operation failed: {errorMessage}" graceIds.CorrelationId)
                            | OperationStatus.NeedsUserInput ->
                                return
                                    Error(
                                        GraceError.Create
                                            "Operation needs user input. Use grace operation approve-conflict."
                                            graceIds.CorrelationId
                                    )
                            | _ ->
                                do! Task.Delay(TimeSpan.FromSeconds(1.0))
                                return! poll (attempt + 1)
                        | Error error -> return Error error
                }

            return! poll 0
        }

    let private initHandler (parseResult: ParseResult) =
        task {
            let graceIds = parseResult |> getNormalizedIdsAndNames
            let stackId = parseResult.GetValue(Options.stackId) |> Option.ofObj |> Option.defaultValue (Guid.NewGuid().ToString())
            let baseBranchId =
                let value = parseResult.GetValue(Options.baseBranchId)
                if value = BranchId.Empty then graceIds.BranchId else value

            let parameters =
                CreateStackParameters(
                    OwnerId = graceIds.OwnerIdString,
                    OwnerName = graceIds.OwnerName,
                    OrganizationId = graceIds.OrganizationIdString,
                    OrganizationName = graceIds.OrganizationName,
                    RepositoryId = graceIds.RepositoryIdString,
                    RepositoryName = graceIds.RepositoryName,
                    StackId = stackId,
                    BaseBranchId = baseBranchId,
                    CorrelationId = graceIds.CorrelationId
                )

            match! Stack.Create(parameters) with
            | Ok returnValue ->
                AnsiConsole.MarkupLine($"[{Colors.Important}]Stack created.[/]")
                AnsiConsole.WriteLine(serialize returnValue.ReturnValue)
                return 0
            | Error error ->
                logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                return -1
        }

    let private addHandler (parseResult: ParseResult) =
        task {
            let graceIds = parseResult |> getNormalizedIdsAndNames
            let stackId = parseResult.GetValue(Options.stackId)
            let changeId = parseResult.GetValue(Options.changeId)
            let branchId =
                let value = parseResult.GetValue(Options.branchId)
                if value = BranchId.Empty then graceIds.BranchId else value

            let parameters =
                AddStackLayerParameters(
                    OwnerId = graceIds.OwnerIdString,
                    OwnerName = graceIds.OwnerName,
                    OrganizationId = graceIds.OrganizationIdString,
                    OrganizationName = graceIds.OrganizationName,
                    RepositoryId = graceIds.RepositoryIdString,
                    RepositoryName = graceIds.RepositoryName,
                    StackId = stackId,
                    ChangeId = changeId,
                    BranchId = branchId,
                    Order = parseResult.GetValue(Options.order),
                    CorrelationId = graceIds.CorrelationId
                )

            match! Stack.AddLayer(parameters) with
            | Ok returnValue ->
                AnsiConsole.MarkupLine($"[{Colors.Important}]Stack updated.[/]")
                AnsiConsole.WriteLine(serialize returnValue.ReturnValue)
                return 0
            | Error error ->
                logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                return -1
        }

    let private restackHandler (parseResult: ParseResult) =
        task {
            let graceIds = parseResult |> getNormalizedIdsAndNames
            let stackId = parseResult.GetValue(Options.stackId)
            let asyncMode = parseResult.GetValue(Options.asyncOption)

            let parameters =
                RestackParameters(
                    OwnerId = graceIds.OwnerIdString,
                    OwnerName = graceIds.OwnerName,
                    OrganizationId = graceIds.OrganizationIdString,
                    OrganizationName = graceIds.OrganizationName,
                    RepositoryId = graceIds.RepositoryIdString,
                    RepositoryName = graceIds.RepositoryName,
                    StackId = stackId,
                    CorrelationId = graceIds.CorrelationId
                )

            match! Stack.Restack(parameters) with
            | Ok returnValue ->
                let operationId = returnValue.ReturnValue
                if asyncMode then
                    AnsiConsole.MarkupLine($"[{Colors.Important}]Restack started. OperationId: {operationId}.[/]")
                    return 0
                else
                    match! pollOperation graceIds operationId with
                    | Ok operationDto ->
                        AnsiConsole.MarkupLine($"[{Colors.Important}]Restack completed.[/]")
                        if operationDto.Result.IsSome then
                            AnsiConsole.WriteLine(serialize operationDto.Result.Value)
                        return 0
                    | Error error ->
                        logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                        return -1
            | Error error ->
                logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                return -1
        }

    let private getHandler (parseResult: ParseResult) =
        task {
            let graceIds = parseResult |> getNormalizedIdsAndNames
            let stackId = parseResult.GetValue(Options.stackId)

            let parameters =
                GetStackParameters(
                    OwnerId = graceIds.OwnerIdString,
                    OwnerName = graceIds.OwnerName,
                    OrganizationId = graceIds.OrganizationIdString,
                    OrganizationName = graceIds.OrganizationName,
                    RepositoryId = graceIds.RepositoryIdString,
                    RepositoryName = graceIds.RepositoryName,
                    StackId = stackId,
                    CorrelationId = graceIds.CorrelationId
                )

            match! Stack.Get(parameters) with
            | Ok returnValue ->
                AnsiConsole.WriteLine(serialize returnValue.ReturnValue)
                return 0
            | Error error ->
                logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                return -1
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

        let command = Command("stack", Description = "Manage stacked changes.")

        let initCommand =
            Command("init", Description = "Create a new stack.")
            |> addOption Options.stackId
            |> addOption Options.baseBranchId
            |> addCommonOptions

        initCommand.SetAction(fun parseResult -> initHandler parseResult)

        let addCommand =
            Command("add", Description = "Add a layer to a stack.")
            |> addOption Options.stackId
            |> addOption Options.changeId
            |> addOption Options.branchId
            |> addOption Options.order
            |> addCommonOptions

        addCommand.SetAction(fun parseResult -> addHandler parseResult)

        let restackCommand =
            Command("restack", Description = "Restack a stack on its latest base.")
            |> addOption Options.stackId
            |> addOption Options.asyncOption
            |> addCommonOptions

        restackCommand.SetAction(fun parseResult -> restackHandler parseResult)

        let getCommand =
            Command("get", Description = "Get a stack by id.")
            |> addOption Options.stackId
            |> addCommonOptions

        getCommand.SetAction(fun parseResult -> getHandler parseResult)

        command.Subcommands.Add(initCommand)
        command.Subcommands.Add(addCommand)
        command.Subcommands.Add(restackCommand)
        command.Subcommands.Add(getCommand)

        command
