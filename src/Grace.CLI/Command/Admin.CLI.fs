namespace Grace.CLI.Command

open FSharpPlus
open Grace.CLI.Common
open Grace.CLI.Common.Validations
open Grace.CLI.Services
open Grace.CLI.Text
open Grace.SDK.Admin
open Grace.Shared
open Grace.Shared.Parameters
open Grace.Shared.Parameters.Reminder
open Grace.Types.Reminder
open Grace.Types.Types
open Grace.Shared.Utilities
open Grace.Shared.Client.Configuration
open Grace.Shared.Validation.Errors
open NodaTime
open Spectre.Console
open Spectre.Console.Json
open System
open System.Collections.Generic
open System.CommandLine
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.Linq
open System.Threading
open System.Threading.Tasks

module Admin =

    module Reminder =

        module private Options =
            let ownerId =
                new Option<OwnerId>(
                    OptionName.OwnerId,
                    Required = false,
                    Description = "The repository's owner ID <Guid>.",
                    Arity = ArgumentArity.ZeroOrOne,
                    DefaultValueFactory = (fun _ -> OwnerId.Empty)
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
                    Arity = ArgumentArity.ZeroOrOne,
                    DefaultValueFactory = (fun _ -> OrganizationId.Empty)
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
                    DefaultValueFactory = (fun _ -> RepositoryId.Empty)
                )

            let repositoryName =
                new Option<String>(
                    OptionName.RepositoryName,
                    [| "-n" |],
                    Required = false,
                    Description = "The name of the repository. [default: current repository]",
                    Arity = ArgumentArity.ExactlyOne
                )

            let reminderId =
                new Option<String>("--reminder-id", Required = true, Description = "The ID of the reminder <Guid>.", Arity = ArgumentArity.ExactlyOne)

            let maxCount =
                new Option<int>(
                    "--max-count",
                    Required = false,
                    Description = $"Maximum number of reminders to return. [default: {Constants.DefaultReminderMaxCount}]",
                    Arity = ArgumentArity.ExactlyOne,
                    DefaultValueFactory = (fun _ -> Constants.DefaultReminderMaxCount)
                )

            let reminderType =
                (new Option<String>(
                    "--reminder-type",
                    Required = false,
                    Description = "Filter by reminder type (Maintenance, PhysicalDeletion, DeleteCachedState, DeleteZipFile).",
                    Arity = ArgumentArity.ZeroOrOne
                ))
                    .AcceptOnlyFromAmong(listCases<ReminderTypes> ())

            let actorName =
                new Option<String>(
                    "--actor-name",
                    Required = false,
                    Description = "Filter by target actor name (e.g., Branch, Repository, Owner).",
                    Arity = ArgumentArity.ZeroOrOne
                )

            let actorId = new Option<String>("--actor-id", Required = true, Description = "The target actor ID.", Arity = ArgumentArity.ExactlyOne)

            let dueAfter =
                new Option<String>(
                    "--due-after",
                    Required = false,
                    Description = "Filter by reminders due after this time (ISO8601).",
                    Arity = ArgumentArity.ZeroOrOne
                )

            let dueBefore =
                new Option<String>(
                    "--due-before",
                    Required = false,
                    Description = "Filter by reminders due before this time (ISO8601).",
                    Arity = ArgumentArity.ZeroOrOne
                )

            let fireAt =
                new Option<String>(
                    "--fire-at",
                    Required = true,
                    Description = "When the reminder should fire (ISO8601 format).",
                    Arity = ArgumentArity.ExactlyOne
                )

            let afterDuration =
                new Option<String>(
                    "--after",
                    Required = true,
                    Description = "Duration to add relative to now (e.g., +15m, +1h, +1d).",
                    Arity = ArgumentArity.ExactlyOne
                )

            let stateJson =
                new Option<String>(
                    "--state-json",
                    Required = false,
                    Description = "Optional JSON payload for the reminder state.",
                    Arity = ArgumentArity.ZeroOrOne
                )

        // List subcommand
        let private listRemindersImpl (parseResult: ParseResult) =
            task {
                if parseResult |> verbose then printParseResult parseResult

                let validateIncomingParameters = parseResult |> Grace.CLI.Common.Validations.CommonValidations

                match validateIncomingParameters with
                | Ok _ ->
                    let graceIds = parseResult |> getNormalizedIdsAndNames

                    let parameters =
                        ListRemindersParameters(
                            OwnerId = graceIds.OwnerIdString,
                            OwnerName = graceIds.OwnerName,
                            OrganizationId = graceIds.OrganizationIdString,
                            OrganizationName = graceIds.OrganizationName,
                            RepositoryId = graceIds.RepositoryIdString,
                            RepositoryName = graceIds.RepositoryName,
                            MaxCount = parseResult.GetValue(Options.maxCount),
                            ReminderType =
                                (parseResult.GetValue(Options.reminderType)
                                 |> Option.ofObj
                                 |> Option.defaultValue ""),
                            ActorName =
                                (parseResult.GetValue(Options.actorName)
                                 |> Option.ofObj
                                 |> Option.defaultValue ""),
                            DueAfter = (parseResult.GetValue(Options.dueAfter) |> Option.ofObj |> Option.defaultValue ""),
                            DueBefore =
                                (parseResult.GetValue(Options.dueBefore)
                                 |> Option.ofObj
                                 |> Option.defaultValue ""),
                            CorrelationId = getCorrelationId parseResult
                        )

                    let! result =
                        if parseResult |> hasOutput then
                            progress
                                .Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                    task {
                                        let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                        let! response = Reminder.List(parameters)
                                        t0.Increment(100.0)
                                        return response
                                    })
                        else
                            Reminder.List(parameters)

                    match result with
                    | Ok graceReturnValue ->
                        if parseResult |> hasOutput then
                            let reminders = graceReturnValue.ReturnValue

                            if Seq.isEmpty reminders then
                                logToAnsiConsole Colors.Highlighted "No reminders found."
                            else
                                let table = Table(Border = TableBorder.DoubleEdge)

                                table.AddColumns(
                                    [| TableColumn($"[{Colors.Important}]Reminder ID[/]")
                                       TableColumn($"[{Colors.Important}]Type[/]")
                                       TableColumn($"[{Colors.Important}]Actor[/]")
                                       TableColumn($"[{Colors.Important}]Fire Time[/]")
                                       TableColumn($"[{Colors.Important}]Created At[/]") |]
                                )
                                |> ignore

                                reminders
                                |> Seq.iter (fun reminder ->
                                    let actorIdDisplay =
                                        if String.IsNullOrEmpty(reminder.ActorId) then "(empty)"
                                        elif reminder.ActorId.Length > 8 then reminder.ActorId.Substring(0, 8) + "..."
                                        else reminder.ActorId

                                    table.AddRow(
                                        $"[{Colors.Deemphasized}]{reminder.ReminderId}[/]",
                                        $"{reminder.ReminderType}",
                                        $"{reminder.ActorName}:{actorIdDisplay}",
                                        instantToLocalTime reminder.ReminderTime,
                                        instantToLocalTime reminder.CreatedAt
                                    )
                                    |> ignore)

                                AnsiConsole.Write(table)

                        return result |> renderOutput parseResult
                    | Error graceError ->
                        logToAnsiConsole Colors.Error (Markup.Escape($"{graceError}"))
                        return result |> renderOutput parseResult
                | Error error -> return Error error |> renderOutput parseResult
            }

        type List() =
            inherit AsynchronousCommandLineAction()

            override this.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
                task {
                    try
                        return! listRemindersImpl parseResult
                    with ex ->
                        return
                            renderOutput
                                parseResult
                                (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
                }

        // Get subcommand
        type Get() =
            inherit AsynchronousCommandLineAction()

            override this.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
                task {
                    try
                        if parseResult |> verbose then printParseResult parseResult

                        let validateIncomingParameters = parseResult |> Grace.CLI.Common.Validations.CommonValidations

                        match validateIncomingParameters with
                        | Ok _ ->
                            let graceIds = parseResult |> getNormalizedIdsAndNames

                            let parameters =
                                GetReminderParameters(
                                    OwnerId = graceIds.OwnerIdString,
                                    OwnerName = graceIds.OwnerName,
                                    OrganizationId = graceIds.OrganizationIdString,
                                    OrganizationName = graceIds.OrganizationName,
                                    RepositoryId = graceIds.RepositoryIdString,
                                    RepositoryName = graceIds.RepositoryName,
                                    ReminderId = parseResult.GetValue(Options.reminderId),
                                    CorrelationId = getCorrelationId parseResult
                                )

                            let! result =
                                if parseResult |> hasOutput then
                                    progress
                                        .Columns(progressColumns)
                                        .StartAsync(fun progressContext ->
                                            task {
                                                let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                                let! response = Reminder.Get(parameters)
                                                t0.Increment(100.0)
                                                return response
                                            })
                                else
                                    Reminder.Get(parameters)

                            match result with
                            | Ok graceReturnValue ->
                                let jsonText = JsonText(serialize graceReturnValue.ReturnValue)
                                AnsiConsole.Write(jsonText)
                                AnsiConsole.WriteLine()
                                return Ok graceReturnValue |> renderOutput parseResult
                            | Error graceError ->
                                logToAnsiConsole Colors.Error (Markup.Escape($"{graceError}"))
                                return result |> renderOutput parseResult
                        | Error error -> return Error error |> renderOutput parseResult

                    with ex ->
                        return
                            renderOutput
                                parseResult
                                (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
                }

        // Delete subcommand
        type Delete() =
            inherit AsynchronousCommandLineAction()

            override this.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
                task {
                    try
                        if parseResult |> verbose then printParseResult parseResult

                        let validateIncomingParameters = parseResult |> Grace.CLI.Common.Validations.CommonValidations

                        match validateIncomingParameters with
                        | Ok _ ->
                            let graceIds = parseResult |> getNormalizedIdsAndNames

                            let parameters =
                                DeleteReminderParameters(
                                    OwnerId = graceIds.OwnerIdString,
                                    OwnerName = graceIds.OwnerName,
                                    OrganizationId = graceIds.OrganizationIdString,
                                    OrganizationName = graceIds.OrganizationName,
                                    RepositoryId = graceIds.RepositoryIdString,
                                    RepositoryName = graceIds.RepositoryName,
                                    ReminderId = parseResult.GetValue(Options.reminderId),
                                    CorrelationId = getCorrelationId parseResult
                                )

                            let! result =
                                if parseResult |> hasOutput then
                                    progress
                                        .Columns(progressColumns)
                                        .StartAsync(fun progressContext ->
                                            task {
                                                let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                                let! response = Reminder.Delete(parameters)
                                                t0.Increment(100.0)
                                                return response
                                            })
                                else
                                    Reminder.Delete(parameters)

                            return result |> renderOutput parseResult
                        | Error error -> return Error error |> renderOutput parseResult

                    with ex ->
                        return
                            renderOutput
                                parseResult
                                (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
                }

        // UpdateTime subcommand
        type UpdateTime() =
            inherit AsynchronousCommandLineAction()

            override this.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
                task {
                    try
                        if parseResult |> verbose then printParseResult parseResult

                        let validateIncomingParameters = parseResult |> Grace.CLI.Common.Validations.CommonValidations

                        match validateIncomingParameters with
                        | Ok _ ->
                            let graceIds = parseResult |> getNormalizedIdsAndNames

                            let parameters =
                                UpdateReminderTimeParameters(
                                    OwnerId = graceIds.OwnerIdString,
                                    OwnerName = graceIds.OwnerName,
                                    OrganizationId = graceIds.OrganizationIdString,
                                    OrganizationName = graceIds.OrganizationName,
                                    RepositoryId = graceIds.RepositoryIdString,
                                    RepositoryName = graceIds.RepositoryName,
                                    ReminderId = parseResult.GetValue(Options.reminderId),
                                    FireAt = parseResult.GetValue(Options.fireAt),
                                    CorrelationId = getCorrelationId parseResult
                                )

                            let! result =
                                if parseResult |> hasOutput then
                                    progress
                                        .Columns(progressColumns)
                                        .StartAsync(fun progressContext ->
                                            task {
                                                let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                                let! response = Reminder.UpdateTime(parameters)
                                                t0.Increment(100.0)
                                                return response
                                            })
                                else
                                    Reminder.UpdateTime(parameters)

                            return result |> renderOutput parseResult
                        | Error error -> return Error error |> renderOutput parseResult

                    with ex ->
                        return
                            renderOutput
                                parseResult
                                (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
                }

        // Reschedule subcommand
        type Reschedule() =
            inherit AsynchronousCommandLineAction()

            override this.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
                task {
                    try
                        if parseResult |> verbose then printParseResult parseResult

                        let validateIncomingParameters = parseResult |> Grace.CLI.Common.Validations.CommonValidations

                        match validateIncomingParameters with
                        | Ok _ ->
                            let graceIds = parseResult |> getNormalizedIdsAndNames

                            let parameters =
                                RescheduleReminderParameters(
                                    OwnerId = graceIds.OwnerIdString,
                                    OwnerName = graceIds.OwnerName,
                                    OrganizationId = graceIds.OrganizationIdString,
                                    OrganizationName = graceIds.OrganizationName,
                                    RepositoryId = graceIds.RepositoryIdString,
                                    RepositoryName = graceIds.RepositoryName,
                                    ReminderId = parseResult.GetValue(Options.reminderId),
                                    After = parseResult.GetValue(Options.afterDuration),
                                    CorrelationId = getCorrelationId parseResult
                                )

                            let! result =
                                if parseResult |> hasOutput then
                                    progress
                                        .Columns(progressColumns)
                                        .StartAsync(fun progressContext ->
                                            task {
                                                let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                                let! response = Reminder.Reschedule(parameters)
                                                t0.Increment(100.0)
                                                return response
                                            })
                                else
                                    Reminder.Reschedule(parameters)

                            return result |> renderOutput parseResult
                        | Error error -> return Error error |> renderOutput parseResult

                    with ex ->
                        return
                            renderOutput
                                parseResult
                                (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
                }

        // Create subcommand
        type Create() =
            inherit AsynchronousCommandLineAction()

            override this.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
                task {
                    try
                        if parseResult |> verbose then printParseResult parseResult

                        let validateIncomingParameters = parseResult |> Grace.CLI.Common.Validations.CommonValidations

                        match validateIncomingParameters with
                        | Ok _ ->
                            let graceIds = parseResult |> getNormalizedIdsAndNames

                            let parameters =
                                CreateReminderParameters(
                                    OwnerId = graceIds.OwnerIdString,
                                    OwnerName = graceIds.OwnerName,
                                    OrganizationId = graceIds.OrganizationIdString,
                                    OrganizationName = graceIds.OrganizationName,
                                    RepositoryId = graceIds.RepositoryIdString,
                                    RepositoryName = graceIds.RepositoryName,
                                    ActorName = parseResult.GetValue(Options.actorName),
                                    ActorId = parseResult.GetValue(Options.actorId),
                                    ReminderType = parseResult.GetValue(Options.reminderType),
                                    FireAt = parseResult.GetValue(Options.fireAt),
                                    StateJson =
                                        (parseResult.GetValue(Options.stateJson)
                                         |> Option.ofObj
                                         |> Option.defaultValue ""),
                                    CorrelationId = getCorrelationId parseResult
                                )

                            let! result =
                                if parseResult |> hasOutput then
                                    progress
                                        .Columns(progressColumns)
                                        .StartAsync(fun progressContext ->
                                            task {
                                                let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                                let! response = Reminder.Create(parameters)
                                                t0.Increment(100.0)
                                                return response
                                            })
                                else
                                    Reminder.Create(parameters)

                            return result |> renderOutput parseResult
                        | Error error -> return Error error |> renderOutput parseResult

                    with ex ->
                        return
                            renderOutput
                                parseResult
                                (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
                }

        /// Builds the Reminder subcommand.
        let Build =
            let addCommonOptions (command: Command) =
                command
                |> addOption Options.ownerName
                |> addOption Options.ownerId
                |> addOption Options.organizationName
                |> addOption Options.organizationId
                |> addOption Options.repositoryId
                |> addOption Options.repositoryName

            // Create main command and aliases
            let reminderCommand = new Command("reminder", Description = "Administrative commands for managing Grace reminders.")

            // List subcommand
            let listCommand =
                new Command("list", Description = "List reminders for the repository.")
                |> addCommonOptions
                |> addOption Options.maxCount
                |> addOption Options.reminderType
                |> addOption Options.actorName
                |> addOption Options.dueAfter
                |> addOption Options.dueBefore

            listCommand.Aliases.Add("ls")
            listCommand.Action <- new List()
            reminderCommand.Subcommands.Add(listCommand)

            // Get subcommand
            let getCommand =
                new Command("get", Description = "Get details of a specific reminder.")
                |> addCommonOptions
                |> addOption Options.reminderId

            getCommand.Action <- new Get()
            reminderCommand.Subcommands.Add(getCommand)

            // Delete subcommand
            let deleteCommand =
                new Command("delete", Description = "Delete a reminder.")
                |> addCommonOptions
                |> addOption Options.reminderId

            deleteCommand.Action <- new Delete()
            reminderCommand.Subcommands.Add(deleteCommand)

            // UpdateTime subcommand
            let updateTimeCommand =
                new Command("update-time", Description = "Update the fire time for a reminder.")
                |> addCommonOptions
                |> addOption Options.reminderId
                |> addOption Options.fireAt

            updateTimeCommand.Action <- new UpdateTime()
            reminderCommand.Subcommands.Add(updateTimeCommand)

            // Reschedule subcommand
            let rescheduleCommand =
                new Command("reschedule", Description = "Reschedule a reminder relative to now.")
                |> addCommonOptions
                |> addOption Options.reminderId
                |> addOption Options.afterDuration

            rescheduleCommand.Action <- new Reschedule()
            reminderCommand.Subcommands.Add(rescheduleCommand)

            // Create subcommand
            let createActorNameOption =
                new Option<String>(
                    "--actor-name",
                    Required = true,
                    Description = "The target actor name (e.g., Branch, Repository, Owner).",
                    Arity = ArgumentArity.ExactlyOne
                )

            let createReminderTypeOption =
                (new Option<String>(
                    "--reminder-type",
                    Required = true,
                    Description = "The type of reminder (Maintenance, PhysicalDeletion, DeleteCachedState, DeleteZipFile).",
                    Arity = ArgumentArity.ExactlyOne
                ))
                    .AcceptOnlyFromAmong(listCases<ReminderTypes> ())

            let createCommand =
                new Command("create", Description = "Create a new manual reminder.")
                |> addCommonOptions
                |> addOption createActorNameOption
                |> addOption Options.actorId
                |> addOption createReminderTypeOption
                |> addOption Options.fireAt
                |> addOption Options.stateJson

            createCommand.Action <- new Create()
            reminderCommand.Subcommands.Add(createCommand)

            reminderCommand

    /// Builds the Admin subcommand.
    let Build =
        // Create main command and aliases
        let adminCommand = new Command("admin", Description = "Administrative commands for managing Grace.")
        adminCommand.Add(Reminder.Build) |> ignore
        adminCommand
