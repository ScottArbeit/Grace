namespace Grace.CLI.Command

open FSharpPlus
open Grace.CLI.Common
open Grace.CLI.Common.Validations
open Grace.CLI.Services
open Grace.CLI.Text
open Grace.SDK
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Types.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open NodaTime
open Spectre.Console
open Spectre.Console.Json
open System
open System.Collections.Generic
open System.CommandLine
open System.CommandLine.Invocation
open System.Linq
open System.Threading
open System.Threading.Tasks
open Grace.CLI

module Organization =

    type CommonParameters() =
        inherit ParameterBase()
        member val public OwnerId: string = String.Empty with get, set
        member val public OwnerName: string = String.Empty with get, set
        member val public OrganizationId: string = String.Empty with get, set
        member val public OrganizationName: string = String.Empty with get, set

    module private Options =
        let ownerId =
            new Option<OwnerId>(
                OptionName.OwnerId,
                Required = false,
                Description = "The organization's owner ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> if Current().OwnerId = Guid.Empty then Guid.NewGuid() else Current().OwnerId)
            )

        let ownerName =
            new Option<String>(
                OptionName.OwnerName,
                Required = false,
                Description = "The organization's owner name. [default: current owner]",
                Arity = ArgumentArity.ExactlyOne
            )

        let organizationId =
            new Option<OrganizationId>(
                OptionName.OrganizationId,
                Required = false,
                Description = "The organization ID <Guid>.",
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
                Description = "The name of the organization. [default: current organization]",
                Arity = ArgumentArity.ExactlyOne
            )

        let organizationNameRequired =
            new Option<String>(OptionName.OrganizationName, Required = true, Description = "The name of the organization.", Arity = ArgumentArity.ExactlyOne)

        let organizationType =
            (new Option<String>(
                "--organizationType",
                Required = true,
                Description = "The type of the organization. [default: Public]",
                Arity = ArgumentArity.ExactlyOne
            ))
                .AcceptOnlyFromAmong(listCases<OrganizationType> ())

        let searchVisibility =
            (new Option<String>(
                "--searchVisibility",
                Required = true,
                Description = "Enables or disables the organization appearing in searches. [default: Visible]",
                Arity = ArgumentArity.ExactlyOne
            ))
                .AcceptOnlyFromAmong(listCases<SearchVisibility> ())

        let description =
            new Option<String>(OptionName.Description, Required = true, Description = "Description of the organization.", Arity = ArgumentArity.ExactlyOne)

        let newName =
            new Option<String>(OptionName.NewName, Required = true, Description = "The new name of the organization.", Arity = ArgumentArity.ExactlyOne)

        let force = new Option<bool>("--force", Required = false, Description = "Delete even if there is data under this organization. [default: false]")

        let includeDeleted =
            new Option<bool>("--include-deleted", [| "-d" |], Required = false, Description = "Include deleted organizations in the result. [default: false]")

        let deleteReason =
            new Option<String>("--deleteReason", Required = true, Description = "The reason for deleting the organization.", Arity = ArgumentArity.ExactlyOne)

        let doNotSwitch =
            new Option<bool>(
                OptionName.DoNotSwitch,
                Required = false,
                Description =
                    "Do not switch your current organization to the new organization after it is created. By default, the new organization becomes the current organization.",
                Arity = ArgumentArity.ZeroOrOne
            )

    // Create subcommand.
    type Create() =
        inherit AsynchronousCommandLineAction()

        override this.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> CommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let organizationId =
                            if parseResult.GetResult(Options.organizationId).Implicit then
                                Guid.NewGuid().ToString()
                            else
                                graceIds.OrganizationIdString

                        let parameters =
                            Parameters.Organization.CreateOrganizationParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = organizationId,
                                OrganizationName = graceIds.OrganizationName,
                                CorrelationId = getCorrelationId parseResult
                            )

                        if parseResult |> hasOutput then
                            let! result =
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! result = Organization.Create(parameters)
                                            t0.Increment(100.0)
                                            return result
                                        })

                            match result with
                            | Ok returnValue ->
                                if not <| parseResult.GetValue(Options.doNotSwitch) then
                                    let newConfig = Current()
                                    newConfig.OrganizationId <- Guid.Parse($"{returnValue.Properties[nameof OrganizationId]}")
                                    newConfig.OrganizationName <- $"{returnValue.Properties[nameof OrganizationName]}"
                                    updateConfiguration newConfig
                                return result |> renderOutput parseResult
                            | Error _ -> return result |> renderOutput parseResult
                        else
                            let! result = Organization.Create(parameters)
                            match result with
                            | Ok returnValue ->
                                if not <| parseResult.GetValue(Options.doNotSwitch) then
                                    let newConfig = Current()
                                    newConfig.OrganizationId <- Guid.Parse($"{returnValue.Properties[nameof OrganizationId]}")
                                    newConfig.OrganizationName <- $"{returnValue.Properties[nameof OrganizationName]}"
                                    updateConfiguration newConfig
                                return result |> renderOutput parseResult
                            | Error _ -> return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult

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
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> CommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let parameters =
                            Parameters.Organization.GetOrganizationParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                IncludeDeleted = parseResult.GetValue(Options.includeDeleted),
                                CorrelationId = getCorrelationId parseResult
                            )

                        if parseResult |> hasOutput then
                            let! result =
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! result = Organization.Get(parameters)
                                            t0.Increment(100.0)
                                            return result
                                        })

                            match result with
                            | Ok graceReturnValue ->
                                let jsonText = JsonText(serialize graceReturnValue.ReturnValue)
                                AnsiConsole.Write(jsonText)
                                AnsiConsole.WriteLine()
                                return Ok graceReturnValue |> renderOutput parseResult
                            | Error graceError -> return Error graceError |> renderOutput parseResult
                        else
                            let! result = Organization.Get(parameters)
                            match result with
                            | Ok graceReturnValue ->
                                let jsonText = JsonText(serialize graceReturnValue.ReturnValue)
                                AnsiConsole.Write(jsonText)
                                AnsiConsole.WriteLine()
                                return Ok graceReturnValue |> renderOutput parseResult
                            | Error graceError -> return Error graceError |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult

                with ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    // SetName subcommand
    type SetName() =
        inherit AsynchronousCommandLineAction()

        override this.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> CommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let parameters =
                            Parameters.Organization.SetOrganizationNameParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                NewName = parseResult.GetValue(Options.newName),
                                CorrelationId = getCorrelationId parseResult
                            )

                        if parseResult |> hasOutput then
                            let! result =
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! result = Organization.SetName(parameters)
                                            t0.Increment(100.0)
                                            return result
                                        })

                            return result |> renderOutput parseResult
                        else
                            let! result = Organization.SetName(parameters)
                            return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult

                with ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    /// Organization.SetType subcommand definition
    type SetType() =
        inherit AsynchronousCommandLineAction()

        override this.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> CommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let parameters =
                            Parameters.Organization.SetOrganizationTypeParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                OrganizationType = parseResult.GetValue(Options.organizationType),
                                CorrelationId = getCorrelationId parseResult
                            )

                        if parseResult |> hasOutput then
                            let! result =
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! result = Organization.SetType(parameters)
                                            t0.Increment(100.0)
                                            return result
                                        })

                            return result |> renderOutput parseResult
                        else
                            let! result = Organization.SetType(parameters)
                            return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult

                with ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    // SetSearchVisibility subcommand
    type SetSearchVisibility() =
        inherit AsynchronousCommandLineAction()

        override this.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> CommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let parameters =
                            Parameters.Organization.SetOrganizationSearchVisibilityParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                SearchVisibility = parseResult.GetValue(Options.searchVisibility),
                                CorrelationId = getCorrelationId parseResult
                            )

                        if parseResult |> hasOutput then
                            let! result =
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! result = Organization.SetSearchVisibility(parameters)
                                            t0.Increment(100.0)
                                            return result
                                        })

                            return result |> renderOutput parseResult
                        else
                            let! result = Organization.SetSearchVisibility(parameters)
                            return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult

                with ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    // SetDescription subcommand
    type SetDescription() =
        inherit AsynchronousCommandLineAction()

        override this.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> CommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let parameters =
                            Parameters.Organization.SetOrganizationDescriptionParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                Description = parseResult.GetValue(Options.description),
                                CorrelationId = getCorrelationId parseResult
                            )

                        if parseResult |> hasOutput then
                            let! result =
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! result = Organization.SetDescription(parameters)
                                            t0.Increment(100.0)
                                            return result
                                        })

                            return result |> renderOutput parseResult
                        else
                            let! result = Organization.SetDescription(parameters)
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
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> CommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let parameters =
                            Parameters.Organization.DeleteOrganizationParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                Force = parseResult.GetValue(Options.force),
                                DeleteReason = parseResult.GetValue(Options.deleteReason),
                                CorrelationId = getCorrelationId parseResult
                            )

                        if parseResult |> hasOutput then
                            let! result =
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! result = Organization.Delete(parameters)
                                            t0.Increment(100.0)
                                            return result
                                        })

                            return result |> renderOutput parseResult
                        else
                            let! result = Organization.Delete(parameters)
                            return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult

                with ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    // Undelete subcommand
    type Undelete() =
        inherit AsynchronousCommandLineAction()

        override this.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> CommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let parameters =
                            Parameters.Organization.DeleteOrganizationParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                CorrelationId = getCorrelationId parseResult
                            )

                        if parseResult |> hasOutput then
                            let! result =
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! result = Organization.Delete(parameters)
                                            t0.Increment(100.0)
                                            return result
                                        })

                            return result |> renderOutput parseResult
                        else
                            let! result = Organization.Delete(parameters)
                            return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult

                with ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    let Build =
        let addCommonOptionsWithoutOrganizationName (command: Command) =
            command
            |> addOption Options.organizationId
            |> addOption Options.ownerName
            |> addOption Options.ownerId

        let addCommonOptions (command: Command) =
            command
            |> addOption Options.organizationName
            |> addCommonOptionsWithoutOrganizationName

        // Create main command and aliases, if any.
        let organizationCommand = new Command("organization", Description = "Create, change, or delete organization-level information.")

        organizationCommand.Aliases.Add("org")

        // Add subcommands.
        let organizationCreateCommand =
            new Command("create", Description = "Create a new organization.")
            |> addOption Options.organizationNameRequired
            |> addCommonOptionsWithoutOrganizationName
            |> addOption Options.doNotSwitch

        organizationCreateCommand.Action <- new Create()
        organizationCommand.Subcommands.Add(organizationCreateCommand)

        let getCommand =
            new Command("get", Description = "Gets details for the organization.")
            |> addOption Options.includeDeleted
            |> addCommonOptions

        getCommand.Action <- new Get()
        organizationCommand.Subcommands.Add(getCommand)

        let setNameCommand =
            new Command("set-name", Description = "Change the name of the organization.")
            |> addOption Options.newName
            |> addCommonOptions

        setNameCommand.Action <- new SetName()
        organizationCommand.Subcommands.Add(setNameCommand)

        let setTypeCommand =
            new Command("set-type", Description = "Change the type of the organization.")
            |> addOption Options.organizationType
            |> addCommonOptions

        setTypeCommand.Action <- new SetType()
        organizationCommand.Subcommands.Add(setTypeCommand)

        let setSearchVisibilityCommand =
            new Command("set-search-visibility", Description = "Change the search visibility of the organization.")
            |> addOption Options.searchVisibility
            |> addCommonOptions

        setSearchVisibilityCommand.Action <- new SetSearchVisibility()
        organizationCommand.Subcommands.Add(setSearchVisibilityCommand)

        let setDescriptionCommand =
            new Command("set-description", Description = "Change the description of the organization.")
            |> addOption Options.description
            |> addCommonOptions

        setDescriptionCommand.Action <- new SetDescription()
        organizationCommand.Subcommands.Add(setDescriptionCommand)

        let deleteCommand =
            new Command("delete", Description = "Delete the organization.")
            |> addOption Options.force
            |> addOption Options.deleteReason
            |> addCommonOptions

        deleteCommand.Action <- new Delete()
        organizationCommand.Subcommands.Add(deleteCommand)

        let undeleteCommand =
            new Command("undelete", Description = "Undeletes the organization.")
            |> addCommonOptions

        undeleteCommand.Action <- new Undelete()
        organizationCommand.Subcommands.Add(undeleteCommand)

        organizationCommand
