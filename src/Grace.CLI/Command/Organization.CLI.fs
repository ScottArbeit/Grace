namespace Grace.CLI.Command

open FSharpPlus
open Grace.CLI.Common
open Grace.SDK
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Types.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors.Organization
open NodaTime
open Spectre.Console
open Spectre.Console.Json
open System
open System.Collections.Generic
open System.CommandLine
open System.CommandLine.NamingConventionBinder
open System.CommandLine.Parsing
open System.Linq
open System.Threading

module Organization =

    type CommonParameters() =
        inherit ParameterBase()
        member val public OwnerId: string = String.Empty with get, set
        member val public OwnerName: string = String.Empty with get, set
        member val public OrganizationId: string = String.Empty with get, set
        member val public OrganizationName: string = String.Empty with get, set

    module private Options =
        let ownerId =
            new Option<String>(
                "--ownerId",
                Required = false,
                Description = "The organization's owner ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory =
                    (fun _ ->
                        if Current().OwnerId = Guid.Empty then
                            $"{Guid.NewGuid()}"
                        else
                            $"{Current().OwnerId}")
            )

        let ownerName =
            new Option<String>(
                "--ownerName",
                Required = false,
                Description = "The organization's owner name. [default: current owner]",
                Arity = ArgumentArity.ExactlyOne
            )

        let organizationId =
            new Option<String>(
                "--organizationId",
                Required = false,
                Description = "The organization ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory =
                    (fun _ ->
                        if Current().OrganizationId = Guid.Empty then
                            $"{Guid.NewGuid()}"
                        else
                            $"{Current().OrganizationId}")
            )

        let organizationName =
            new Option<String>(
                "--organizationName",
                Required = false,
                Description = "The name of the organization. [default: current organization]",
                Arity = ArgumentArity.ExactlyOne
            )

        let organizationNameRequired =
            new Option<String>("--organizationName", Required = true, Description = "The name of the organization.", Arity = ArgumentArity.ExactlyOne)

        let organizationType =
            (new Option<String>(
                "--organizationType",
                Required = true,
                Description = "The type of the organization. [default: Public]",
                Arity = ArgumentArity.ExactlyOne
            ))

        organizationType.AcceptOnlyFromAmong(listCases<OrganizationType> ())

        let searchVisibility =
            (new Option<String>(
                "--searchVisibility",
                Required = true,
                Description = "Enables or disables the organization appearing in searches. [default: Visible]",
                Arity = ArgumentArity.ExactlyOne
            ))

        searchVisibility.AcceptOnlyFromAmong(listCases<SearchVisibility> ())

        let description = new Option<String>("--description", Required = true, Description = "Description of the owner.", Arity = ArgumentArity.ExactlyOne)

        let newName = new Option<String>("--newName", Required = true, Description = "The new name of the organization.", Arity = ArgumentArity.ExactlyOne)

        let force = new Option<bool>("--force", Required = false, Description = "Delete even if there is data under this organization. [default: false]")

        let includeDeleted =
            new Option<bool>("--include-deleted", [| "-d" |], Required = false, Description = "Include deleted organizations in the result. [default: false]")

        let deleteReason =
            new Option<String>("--deleteReason", Required = true, Description = "The reason for deleting the organization.", Arity = ArgumentArity.ExactlyOne)

        let doNotSwitch =
            new Option<bool>(
                "--doNotSwitch",
                Required = false,
                Description = "Do not switch to the new organization as the current organization.",
                Arity = ArgumentArity.ZeroOrOne
            )

    let mustBeAValidGuid (parseResult: ParseResult) (parameters: CommonParameters) (option: Option<'T>) (value: string) (error: OrganizationError) =
        let mutable guid = Guid.Empty

        if
            parseResult.CommandResult.GetValue<'T>(option) <> null
            && not <| String.IsNullOrEmpty(value)
            && (Guid.TryParse(value, &guid) = false || guid = Guid.Empty)
        then
            Error(GraceError.Create (OrganizationError.getErrorMessage error) (parameters.CorrelationId))
        else
            Ok(parseResult, parameters)

    let mustBeAValidGraceName (parseResult: ParseResult) (parameters: CommonParameters) (option: Option<'T>) (value: string) (error: OrganizationError) =
        if
            parseResult.CommandResult.GetValue<'T>(option) <> null
            && not <| Constants.GraceNameRegex.IsMatch(value)
        then
            Error(GraceError.Create (OrganizationError.getErrorMessage error) (parameters.CorrelationId))
        else
            Ok(parseResult, parameters)

    let private CommonValidations (parseResult, parameters) =
        let ``OwnerId must be a Guid`` (parseResult: ParseResult, parameters: CommonParameters) =
            mustBeAValidGuid parseResult parameters Options.ownerId parameters.OwnerId InvalidOwnerId

        let ``OwnerName must be a valid Grace name`` (parseResult: ParseResult, parameters: CommonParameters) =
            mustBeAValidGraceName parseResult parameters Options.ownerName parameters.OwnerName InvalidOwnerName

        let ``OrganizationId must be a Guid`` (parseResult: ParseResult, parameters: CommonParameters) =
            mustBeAValidGuid parseResult parameters Options.organizationId parameters.OrganizationId InvalidOrganizationId

        let ``OrganizationName must be a valid Grace name`` (parseResult: ParseResult, parameters: CommonParameters) =
            mustBeAValidGraceName parseResult parameters Options.organizationName parameters.OrganizationName InvalidOrganizationName

        (parseResult, parameters)
        |> ``OwnerId must be a Guid``
        >>= ``OwnerName must be a valid Grace name``
        >>= ``OrganizationId must be a Guid``
        >>= ``OrganizationName must be a valid Grace name``

    let ``OrganizationName must not be empty`` (parseResult: ParseResult, parameters: CommonParameters) =
        if
            (parseResult.CommandResult.Command.Options.Contains(Options.organizationNameRequired)
             || parseResult.CommandResult.Command.Options.Contains(Options.organizationName))
            && not <| String.IsNullOrEmpty(parameters.OrganizationName)
        then
            Ok(parseResult, parameters)
        else
            Error(GraceError.Create (OrganizationError.getErrorMessage OrganizationNameIsRequired) (parameters.CorrelationId))

    let ``Either OwnerId or OwnerName must be provided`` (parseResult: ParseResult, parameters: CommonParameters) =
        if
            (parseResult.CommandResult.Command.Options.Contains(Options.ownerId)
             || parseResult.CommandResult.Command.Options.Contains(Options.ownerName))
        then
            Ok(parseResult, parameters)
        else
            Error(GraceError.Create (OrganizationError.getErrorMessage EitherOwnerIdOrOwnerNameRequired) (parameters.CorrelationId))

    /// Adjusts parameters to account for whether Id's or Name's were specified by the user, or should be taken from default values.
    let normalizeIdsAndNames<'T when 'T :> CommonParameters> (parseResult: ParseResult) (parameters: 'T) =
        // If the name was specified on the command line, but the id wasn't, then we should only send the name, and we set the id to String.Empty.
        if
            parseResult.GetResult(Options.ownerId).Implicit
            && not <| isNull (parseResult.GetResult(Options.ownerName))
            && not <| parseResult.GetResult(Options.ownerName).Implicit
        then
            parameters.OwnerId <- String.Empty

        if
            parseResult.GetResult(Options.organizationId).Implicit
            && not <| isNull (parseResult.GetResult(Options.organizationName))
            && not <| parseResult.GetResult(Options.organizationName).Implicit
        then
            parameters.OrganizationId <- String.Empty

        parameters

    // Create subcommand.
    type CreateParameters() =
        inherit CommonParameters()

    let private createHandler (parseResult: ParseResult) (createParameters: CreateParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult

                let validateIncomingParameters = CommonValidations(parseResult, createParameters)

                match validateIncomingParameters with
                | Ok _ ->
                    let organizationId =
                        if parseResult.GetResult(Options.organizationId).Implicit then
                            Guid.NewGuid().ToString()
                        else
                            createParameters.OrganizationId

                    let parameters =
                        Parameters.Organization.CreateOrganizationParameters(
                            OwnerId = createParameters.OwnerId,
                            OwnerName = createParameters.OwnerName,
                            OrganizationId = organizationId,
                            OrganizationName = createParameters.OrganizationName,
                            CorrelationId = createParameters.CorrelationId
                        )

                    if parseResult |> hasOutput then
                        return!
                            progress
                                .Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                    task {
                                        let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                        let! result = Organization.Create(parameters)
                                        t0.Increment(100.0)
                                        return result
                                    })
                    else
                        return! Organization.Create(parameters)
                | Error error -> return Error error
            with ex ->
                return Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    let private Create =
        CommandHandler.Create(fun (parseResult: ParseResult) (createParameters: CreateParameters) ->
            task {
                let! result = createHandler parseResult createParameters

                match result with
                | Ok returnValue ->
                    // Update the Grace configuration file with the newly-created organization.
                    if not <| parseResult.CommandResult.Command.Options.Contains(Options.doNotSwitch) then
                        let newConfig = Current()
                        newConfig.OrganizationId <- Guid.Parse(returnValue.Properties[nameof (OrganizationId)])
                        newConfig.OrganizationName <- returnValue.Properties[nameof (OrganizationName)]
                        updateConfiguration newConfig
                | Error _ -> ()

                return result |> renderOutput parseResult
            })

    // Get subcommand
    type GetParameters() =
        inherit CommonParameters()
        member val public IncludeDeleted: bool = false with get, set

    let private getHandler (parseResult: ParseResult) (getParameters: GetParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult

                let validateIncomingParameters = CommonValidations(parseResult, getParameters)

                match validateIncomingParameters with
                | Ok _ ->
                    let parameters =
                        Parameters.Organization.GetOrganizationParameters(
                            OwnerId = getParameters.OwnerId,
                            OwnerName = getParameters.OwnerName,
                            OrganizationId = getParameters.OrganizationId,
                            OrganizationName = getParameters.OrganizationName,
                            IncludeDeleted = getParameters.IncludeDeleted,
                            CorrelationId = getParameters.CorrelationId
                        )

                    if parseResult |> hasOutput then
                        return!
                            progress
                                .Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                    task {
                                        let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                        let! result = Organization.Get(parameters)
                                        t0.Increment(100.0)
                                        return result
                                    })
                    else
                        return! Organization.Get(parameters)
                | Error error -> return Error error
            with ex ->
                return Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    let private Get =
        CommandHandler.Create(fun (parseResult: ParseResult) (getParameters: GetParameters) ->
            task {
                let! result = getHandler parseResult (getParameters |> normalizeIdsAndNames parseResult)
                //return result |> renderOutput parseResult
                match result with
                | Ok graceReturnValue ->
                    let jsonText = JsonText(serialize graceReturnValue.ReturnValue)
                    AnsiConsole.Write(jsonText)
                    AnsiConsole.WriteLine()
                    return Ok graceReturnValue |> renderOutput parseResult
                | Error graceError -> return Error graceError |> renderOutput parseResult
            })

    // SetName subcommand
    type NameParameters() =
        inherit CommonParameters()
        member val public NewName: string = String.Empty with get, set

    let private setNameHandler (parseResult: ParseResult) (setNameParameters: NameParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult

                let validateIncomingParameters = CommonValidations(parseResult, setNameParameters)

                match validateIncomingParameters with
                | Ok _ ->
                    let parameters =
                        Parameters.Organization.SetOrganizationNameParameters(
                            OwnerId = setNameParameters.OwnerId,
                            OwnerName = setNameParameters.OwnerName,
                            OrganizationId = setNameParameters.OrganizationId,
                            OrganizationName = setNameParameters.OrganizationName,
                            NewName = setNameParameters.NewName,
                            CorrelationId = setNameParameters.CorrelationId
                        )

                    if parseResult |> hasOutput then
                        return!
                            progress
                                .Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                    task {
                                        let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                        let! result = Organization.SetName(parameters)
                                        t0.Increment(100.0)
                                        return result
                                    })
                    else
                        return! Organization.SetName(parameters)
                | Error error -> return Error error
            with ex ->
                return Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    let private SetName =
        CommandHandler.Create(fun (parseResult: ParseResult) (setNameParameters: NameParameters) ->
            task {
                let! result = setNameHandler parseResult (setNameParameters |> normalizeIdsAndNames parseResult)
                return result |> renderOutput parseResult
            })

    // SetType subcommand
    type TypeParameters() =
        inherit CommonParameters()
        member val public OrganizationType: string = String.Empty with get, set

    let private setTypeHandler (parseResult: ParseResult) (setTypeParameters: TypeParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult

                let validateIncomingParameters = CommonValidations(parseResult, setTypeParameters)

                match validateIncomingParameters with
                | Ok _ ->
                    let parameters =
                        Parameters.Organization.SetOrganizationTypeParameters(
                            OwnerId = setTypeParameters.OwnerId,
                            OwnerName = setTypeParameters.OwnerName,
                            OrganizationId = setTypeParameters.OrganizationId,
                            OrganizationName = setTypeParameters.OrganizationName,
                            OrganizationType = setTypeParameters.OrganizationType,
                            CorrelationId = setTypeParameters.CorrelationId
                        )

                    if parseResult |> hasOutput then
                        return!
                            progress
                                .Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                    task {
                                        let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                        let! result = Organization.SetType(parameters)
                                        t0.Increment(100.0)
                                        return result
                                    })
                    else
                        return! Organization.SetType(parameters)
                | Error error -> return Error error
            with ex ->
                return Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    let private SetType =
        CommandHandler.Create(fun (parseResult: ParseResult) (setTypeParameters: TypeParameters) ->
            task {
                let! result = setTypeHandler parseResult (setTypeParameters |> normalizeIdsAndNames parseResult)
                return result |> renderOutput parseResult
            })

    // SetSearchVisibility subcommand
    type SearchVisibilityParameters() =
        inherit CommonParameters()
        member val public SearchVisibility: string = String.Empty with get, set

    let private setSearchVisibilityHandler (parseResult: ParseResult) (setSearchVisibilityParameters: SearchVisibilityParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult

                let validateIncomingParameters = CommonValidations(parseResult, setSearchVisibilityParameters)

                match validateIncomingParameters with
                | Ok _ ->
                    let organizationId =
                        if not <| String.IsNullOrEmpty(setSearchVisibilityParameters.OrganizationId) then
                            setSearchVisibilityParameters.OrganizationId
                        else
                            $"{Current().OrganizationId}"

                    let parameters =
                        Parameters.Organization.SetOrganizationSearchVisibilityParameters(
                            OwnerId = setSearchVisibilityParameters.OwnerId,
                            OwnerName = setSearchVisibilityParameters.OwnerName,
                            OrganizationId = organizationId,
                            OrganizationName = setSearchVisibilityParameters.OrganizationName,
                            SearchVisibility = setSearchVisibilityParameters.SearchVisibility,
                            CorrelationId = setSearchVisibilityParameters.CorrelationId
                        )

                    if parseResult |> hasOutput then
                        return!
                            progress
                                .Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                    task {
                                        let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                        let! result = Organization.SetSearchVisibility(parameters)
                                        t0.Increment(100.0)
                                        return result
                                    })
                    else
                        return! Organization.SetSearchVisibility(parameters)
                | Error error -> return Error error
            with ex ->
                return Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    let private SetSearchVisibility =
        CommandHandler.Create(fun (parseResult: ParseResult) (setSearchVisibilityParameters: SearchVisibilityParameters) ->
            task {
                let! result = setSearchVisibilityHandler parseResult (setSearchVisibilityParameters |> normalizeIdsAndNames parseResult)

                return result |> renderOutput parseResult
            })

    // SetDescription subcommand
    type DescriptionParameters() =
        inherit CommonParameters()
        member val public Description: string = String.Empty with get, set

    let private setDescriptionHandler (parseResult: ParseResult) (descriptionParameters: DescriptionParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult

                let validateIncomingParameters = CommonValidations(parseResult, descriptionParameters)

                match validateIncomingParameters with
                | Ok _ ->
                    let parameters =
                        Parameters.Organization.SetOrganizationDescriptionParameters(
                            OwnerId = descriptionParameters.OwnerId,
                            OwnerName = descriptionParameters.OwnerName,
                            OrganizationId = descriptionParameters.OrganizationId,
                            OrganizationName = descriptionParameters.OrganizationName,
                            Description = descriptionParameters.Description,
                            CorrelationId = descriptionParameters.CorrelationId
                        )

                    if parseResult |> hasOutput then
                        return!
                            progress
                                .Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                    task {
                                        let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                        let! result = Organization.SetDescription(parameters)
                                        t0.Increment(100.0)
                                        return result
                                    })
                    else
                        return! Organization.SetDescription(parameters)
                | Error error -> return Error error
            with ex ->
                return Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    let private SetDescription =
        CommandHandler.Create(fun (parseResult: ParseResult) (descriptionParameters: DescriptionParameters) ->
            task {
                let! result = setDescriptionHandler parseResult (descriptionParameters |> normalizeIdsAndNames parseResult)

                return result |> renderOutput parseResult
            })

    // Delete subcommand
    type DeleteParameters() =
        inherit CommonParameters()
        member val public Force: bool = false with get, set
        member val public DeleteReason: DeleteReason = String.Empty with get, set

    let private deleteHandler (parseResult: ParseResult) (deleteParameters: DeleteParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult

                let validateIncomingParameters = CommonValidations(parseResult, deleteParameters)

                match validateIncomingParameters with
                | Ok _ ->
                    let parameters =
                        Parameters.Organization.DeleteOrganizationParameters(
                            OwnerId = deleteParameters.OwnerId,
                            OwnerName = deleteParameters.OwnerName,
                            OrganizationId = deleteParameters.OrganizationId,
                            OrganizationName = deleteParameters.OrganizationName,
                            Force = deleteParameters.Force,
                            DeleteReason = deleteParameters.DeleteReason,
                            CorrelationId = deleteParameters.CorrelationId
                        )

                    if parseResult |> hasOutput then
                        return!
                            progress
                                .Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                    task {
                                        let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                        let! result = Organization.Delete(parameters)
                                        t0.Increment(100.0)
                                        return result
                                    })
                    else
                        return! Organization.Delete(parameters)
                | Error error -> return Error error
            with ex ->
                return Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    let private Delete =
        CommandHandler.Create(fun (parseResult: ParseResult) (deleteParameters: DeleteParameters) ->
            task {
                let! result = deleteHandler parseResult (deleteParameters |> normalizeIdsAndNames parseResult)
                return result |> renderOutput parseResult
            })

    // Undelete subcommand
    type UndeleteParameters() =
        inherit CommonParameters()

    let private undeleteHandler (parseResult: ParseResult) (undeleteParameters: UndeleteParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult

                let validateIncomingParameters = CommonValidations(parseResult, undeleteParameters)

                match validateIncomingParameters with
                | Ok _ ->
                    let parameters =
                        Parameters.Organization.DeleteOrganizationParameters(
                            OwnerId = undeleteParameters.OwnerId,
                            OwnerName = undeleteParameters.OwnerName,
                            OrganizationId = undeleteParameters.OrganizationId,
                            OrganizationName = undeleteParameters.OrganizationName,
                            CorrelationId = undeleteParameters.CorrelationId
                        )

                    if parseResult |> hasOutput then
                        return!
                            progress
                                .Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                    task {
                                        let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                        let! result = Organization.Delete(parameters)
                                        t0.Increment(100.0)
                                        return result
                                    })
                    else
                        return! Organization.Delete(parameters)
                | Error error -> return Error error
            with ex ->
                return Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    let private Undelete =
        CommandHandler.Create(fun (parseResult: ParseResult) (undeleteParameters: UndeleteParameters) ->
            task {
                let! result = undeleteHandler parseResult (undeleteParameters |> normalizeIdsAndNames parseResult)
                return result |> renderOutput parseResult
            })

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

        organizationCreateCommand.Action <- Create
        organizationCommand.Subcommands.Add(organizationCreateCommand)

        let getCommand =
            new Command("get", Description = "Gets details for the organization.")
            |> addOption Options.includeDeleted
            |> addCommonOptions

        getCommand.Action <- Get
        organizationCommand.Subcommands.Add(getCommand)

        let setNameCommand =
            new Command("set-name", Description = "Change the name of the organization.")
            |> addOption Options.newName
            |> addCommonOptions

        setNameCommand.Action <- SetName
        organizationCommand.Subcommands.Add(setNameCommand)

        let setTypeCommand =
            new Command("set-type", Description = "Change the type of the organization.")
            |> addOption Options.organizationType
            |> addCommonOptions

        setTypeCommand.Action <- SetType
        organizationCommand.Subcommands.Add(setTypeCommand)

        let setSearchVisibilityCommand =
            new Command("set-search-visibility", Description = "Change the search visibility of the organization.")
            |> addOption Options.searchVisibility
            |> addCommonOptions

        setSearchVisibilityCommand.Action <- SetSearchVisibility
        organizationCommand.Subcommands.Add(setSearchVisibilityCommand)

        let setDescriptionCommand =
            new Command("set-description", Description = "Change the description of the organization.")
            |> addOption Options.description
            |> addCommonOptions

        setDescriptionCommand.Action <- SetDescription
        organizationCommand.Subcommands.Add(setDescriptionCommand)

        let deleteCommand =
            new Command("delete", Description = "Delete the organization.")
            |> addOption Options.force
            |> addOption Options.deleteReason
            |> addCommonOptions

        deleteCommand.Action <- Delete
        organizationCommand.Subcommands.Add(deleteCommand)

        let undeleteCommand =
            new Command("undelete", Description = "Undeletes the organization.")
            |> addCommonOptions

        undeleteCommand.Action <- Undelete
        organizationCommand.Subcommands.Add(undeleteCommand)

        organizationCommand
