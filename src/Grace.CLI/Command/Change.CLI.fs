namespace Grace.CLI.Command

open Grace.CLI.Common
open Grace.CLI.Services
open Grace.CLI.Text
open Grace.SDK
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Client.Theme
open Grace.Shared.Parameters.Change
open Grace.Shared.Utilities
open Grace.Shared.Validation
open Grace.Shared.Validation.Errors
open Grace.Types.Change
open Grace.Types.Types
open Spectre.Console
open System
open System.CommandLine
open System.CommandLine.Parsing
open System.Threading.Tasks

module Change =

    type CommonParameters() =
        inherit ParameterBase()
        member val public ChangeId: string = String.Empty with get, set
        member val public OwnerId: string = String.Empty with get, set
        member val public OwnerName: string = String.Empty with get, set
        member val public OrganizationId: string = String.Empty with get, set
        member val public OrganizationName: string = String.Empty with get, set
        member val public RepositoryId: string = String.Empty with get, set
        member val public RepositoryName: string = String.Empty with get, set

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

        let changeId =
            new Option<String>(
                "--change-id",
                [| "-c" |],
                Required = false,
                Description = "The change ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne
            )

        let title =
            new Option<String>(
                "--title",
                [| "-t" |],
                Required = true,
                Description = "Title for the change.",
                Arity = ArgumentArity.ExactlyOne
            )

        let description =
            new Option<String>(
                "--description",
                [| "-d" |],
                Required = false,
                Description = "Description for the change.",
                Arity = ArgumentArity.ExactlyOne
            )

        let maxCount =
            new Option<int>(
                "--max-count",
                Required = false,
                Description = "Maximum number of changes to return.",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> 30)
            )

    let private createHandler (parseResult: ParseResult) =
        task {
            let graceIds = parseResult |> getNormalizedIdsAndNames

            let parameters =
                CreateChangeParameters(
                    OwnerId = graceIds.OwnerIdString,
                    OwnerName = graceIds.OwnerName,
                    OrganizationId = graceIds.OrganizationIdString,
                    OrganizationName = graceIds.OrganizationName,
                    RepositoryId = graceIds.RepositoryIdString,
                    RepositoryName = graceIds.RepositoryName,
                    ChangeId = parseResult.GetValue(Options.changeId),
                    Title = parseResult.GetValue(Options.title),
                    Description = parseResult.GetValue(Options.description),
                    CorrelationId = graceIds.CorrelationId
                )

            match! Change.Create(parameters) with
            | Ok returnValue ->
                AnsiConsole.MarkupLine($"[{Colors.Important}]Change created.[/]")
                AnsiConsole.WriteLine(serialize returnValue.ReturnValue)
                return 0
            | Error error ->
                logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                return -1
        }

    let private statusHandler (parseResult: ParseResult) =
        task {
            let graceIds = parseResult |> getNormalizedIdsAndNames

            let parameters =
                GetChangeParameters(
                    OwnerId = graceIds.OwnerIdString,
                    OwnerName = graceIds.OwnerName,
                    OrganizationId = graceIds.OrganizationIdString,
                    OrganizationName = graceIds.OrganizationName,
                    RepositoryId = graceIds.RepositoryIdString,
                    RepositoryName = graceIds.RepositoryName,
                    ChangeId = parseResult.GetValue(Options.changeId),
                    CorrelationId = graceIds.CorrelationId
                )

            match! Change.Get(parameters) with
            | Ok returnValue ->
                AnsiConsole.WriteLine(serialize returnValue.ReturnValue)
                return 0
            | Error error ->
                logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                return -1
        }

    let private listHandler (parseResult: ParseResult) =
        task {
            let graceIds = parseResult |> getNormalizedIdsAndNames

            let parameters =
                ListChangesParameters(
                    OwnerId = graceIds.OwnerIdString,
                    OwnerName = graceIds.OwnerName,
                    OrganizationId = graceIds.OrganizationIdString,
                    OrganizationName = graceIds.OrganizationName,
                    RepositoryId = graceIds.RepositoryIdString,
                    RepositoryName = graceIds.RepositoryName,
                    MaxCount = parseResult.GetValue(Options.maxCount),
                    CorrelationId = graceIds.CorrelationId
                )

            match! Change.List(parameters) with
            | Ok returnValue ->
                AnsiConsole.WriteLine(serialize returnValue.ReturnValue)
                return 0
            | Error error ->
                logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                return -1
        }

    let Build =
        let command = Command("change", Description = "Manage logical changes (stacked diffs).")

        let createCommand =
            Command("create", Description = "Create a new change.")
            |> addOption Options.ownerId
            |> addOption Options.ownerName
            |> addOption Options.organizationId
            |> addOption Options.organizationName
            |> addOption Options.repositoryId
            |> addOption Options.repositoryName
            |> addOption Options.changeId
            |> addOption Options.title
            |> addOption Options.description

        createCommand.SetAction(fun parseResult -> createHandler parseResult)

        let statusCommand =
            Command("status", Description = "Show a change.")
            |> addOption Options.ownerId
            |> addOption Options.ownerName
            |> addOption Options.organizationId
            |> addOption Options.organizationName
            |> addOption Options.repositoryId
            |> addOption Options.repositoryName
            |> addOption Options.changeId

        statusCommand.SetAction(fun parseResult -> statusHandler parseResult)

        let listCommand =
            Command("list", Description = "List changes.")
            |> addOption Options.ownerId
            |> addOption Options.ownerName
            |> addOption Options.organizationId
            |> addOption Options.organizationName
            |> addOption Options.repositoryId
            |> addOption Options.repositoryName
            |> addOption Options.maxCount

        listCommand.SetAction(fun parseResult -> listHandler parseResult)

        command.Subcommands.Add(createCommand)
        command.Subcommands.Add(statusCommand)
        command.Subcommands.Add(listCommand)

        command
