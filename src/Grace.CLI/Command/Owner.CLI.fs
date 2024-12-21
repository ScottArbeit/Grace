namespace Grace.CLI.Command

open FSharpPlus
open Grace.CLI.Common
open Grace.SDK
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Client.Theme
open Grace.Shared.Types
open Grace.Shared.Validation
open Grace.Shared.Validation.Errors.Owner
open NodaTime
open System
open System.Collections.Generic
open System.CommandLine.NamingConventionBinder
open System.CommandLine.Parsing
open System.Linq
open System.Threading.Tasks
open System.CommandLine
open Spectre.Console
open Spectre.Console.Json
open Grace.Shared.Utilities

module Owner =

    type CommonParameters() =
        inherit ParameterBase()
        member val public OwnerId: string = String.Empty with get, set
        member val public OwnerName: string = String.Empty with get, set

    module private Options =
        let ownerId =
            new Option<String>(
                "--ownerId",
                IsRequired = false,
                Description = "The Id of the owner <Guid>.",
                Arity = ArgumentArity.ExactlyOne,
                getDefaultValue = (fun _ -> $"{Current().OwnerId}")
            )

        let ownerName =
            new Option<String>(
                "--ownerName",
                IsRequired = false,
                Description = "The name of the owner. [default: current owner]",
                Arity = ArgumentArity.ExactlyOne
            )

        let ownerNameRequired = new Option<String>("--ownerName", IsRequired = true, Description = "The name of the owner.", Arity = ArgumentArity.ExactlyOne)

        let ownerTypeRequired =
            (new Option<String>("--ownerType", IsRequired = true, Description = "The type of owner. [default: Public]", Arity = ArgumentArity.ExactlyOne))
                .FromAmong(Utilities.listCases<OwnerType> ())

        let searchVisibilityRequired =
            (new Option<String>(
                "--searchVisibility",
                IsRequired = true,
                Description = "Enables or disables the owner appearing in searches. [default: true]",
                Arity = ArgumentArity.ExactlyOne
            ))
                .FromAmong(Utilities.listCases<SearchVisibility> ())

        let descriptionRequired =
            new Option<String>("--description", IsRequired = true, Description = "Description of the owner.", Arity = ArgumentArity.ExactlyOne)

        let newName = new Option<String>("--newName", IsRequired = true, Description = "The new name of the organization.", Arity = ArgumentArity.ExactlyOne)

        let force = new Option<bool>("--force", IsRequired = false, Description = "Delete even if there is data under this owner. [default: false]")

        let includeDeleted =
            new Option<bool>([| "--include-deleted"; "-d" |], IsRequired = false, Description = "Include deleted owners in the result. [default: false]")

        let deleteReason =
            new Option<String>("--deleteReason", IsRequired = true, Description = "The reason for deleting the owner.", Arity = ArgumentArity.ExactlyOne)

        let doNotSwitch =
            new Option<bool>(
                "--doNotSwitch",
                IsRequired = false,
                Description = "Do not switch to the new owner as the current owner.",
                Arity = ArgumentArity.ZeroOrOne
            )

    let mustBeAValidGuid (parseResult: ParseResult) (parameters: CommonParameters) (option: Option) (value: string) (error: OwnerError) =
        let mutable guid = Guid.Empty

        if
            parseResult.CommandResult.FindResultFor(option) <> null
            && not <| String.IsNullOrEmpty(value)
            && (Guid.TryParse(value, &guid) = false || guid = Guid.Empty)
        then
            Error(GraceError.Create (OwnerError.getErrorMessage error) (parameters.CorrelationId))
        else
            Ok(parseResult, parameters)

    let mustBeAValidGraceName (parseResult: ParseResult) (parameters: CommonParameters) (option: Option) (value: string) (error: OwnerError) =
        if
            parseResult.CommandResult.FindResultFor(option) <> null
            && not <| Constants.GraceNameRegex.IsMatch(value)
        then
            Error(GraceError.Create (OwnerError.getErrorMessage error) (parameters.CorrelationId))
        else
            Ok(parseResult, parameters)

    let private CommonValidations (parseResult, commonParameters) =
        let ``OwnerId must be a Guid`` (parseResult: ParseResult, parameters: CommonParameters) =
            mustBeAValidGuid parseResult parameters Options.ownerId parameters.OwnerId InvalidOwnerId

        let ``OwnerName must be a valid Grace name`` (parseResult: ParseResult, parameters: CommonParameters) =
            mustBeAValidGraceName parseResult parameters Options.ownerName parameters.OwnerName InvalidOwnerName

        (parseResult, commonParameters)
        |> ``OwnerId must be a Guid``
        >>= ``OwnerName must be a valid Grace name``

    let private ``OwnerName must not be empty`` (parseResult: ParseResult, commonParameters: CommonParameters) =
        if
            (parseResult.HasOption(Options.ownerNameRequired)
             || parseResult.HasOption(Options.ownerName))
            && not <| String.IsNullOrEmpty(commonParameters.OwnerName)
        then
            Ok(parseResult, commonParameters)
        else
            Error(GraceError.Create (OwnerError.getErrorMessage OwnerNameIsRequired) (commonParameters.CorrelationId))

    /// Adjusts parameters to account for whether Id's or Name's were specified by the user, or should be taken from default values.
    let normalizeIdsAndNames<'T when 'T :> CommonParameters> (parseResult: ParseResult) (parameters: 'T) =
        // If the name was specified on the command line, but the id wasn't, then we should only send the name, and we set the id to String.Empty.
        if
            parseResult.CommandResult.FindResultFor(Options.ownerId).IsImplicit
            && not <| isNull (parseResult.CommandResult.FindResultFor(Options.ownerName))
            && not <| parseResult.CommandResult.FindResultFor(Options.ownerName).IsImplicit
        then
            //parameters.OwnerId <- String.Empty
            ()

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
                    let ownerId =
                        if parseResult.FindResultFor(Options.ownerId).IsImplicit then
                            Guid.NewGuid().ToString()
                        else
                            createParameters.OwnerId

                    let parameters =
                        Parameters.Owner.CreateOwnerParameters(
                            OwnerId = ownerId,
                            OwnerName = createParameters.OwnerName,
                            CorrelationId = createParameters.CorrelationId
                        )

                    if parseResult |> hasOutput then
                        return!
                            progress
                                .Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                    task {
                                        let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                        let! result = Owner.Create(parameters)
                                        t0.Increment(100.0)
                                        return result
                                    })
                    else
                        return! Owner.Create(parameters)
                | Error error -> return Error error
            with ex ->
                return Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    let private Create =
        CommandHandler.Create(fun (parseResult: ParseResult) (createParameters: CreateParameters) ->
            task {
                let! result = createHandler parseResult (createParameters |> normalizeIdsAndNames parseResult)

                match result with
                | Ok returnValue ->
                    // Update the Grace configuration file with the newly-created repository.
                    if not <| parseResult.HasOption(Options.doNotSwitch) then
                        let newConfig = Current()
                        newConfig.OwnerId <- Guid.Parse(returnValue.Properties[nameof (OwnerId)])
                        newConfig.OwnerName <- returnValue.Properties[nameof (OwnerName)]
                        updateConfiguration newConfig
                | Error _ -> ()

                return result |> renderOutput parseResult
            })

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
                        Parameters.Owner.GetOwnerParameters(
                            OwnerId = getParameters.OwnerId,
                            OwnerName = getParameters.OwnerName,
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

                                        let! result = Owner.Get(parameters)
                                        t0.Increment(100.0)
                                        return result
                                    })
                    else
                        return! Owner.Get(parameters)
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
                    return (Ok graceReturnValue) |> renderOutput parseResult
                | Error graceError -> return Error graceError |> renderOutput parseResult
            })

    type SetNameParameters() =
        inherit CommonParameters()
        member val public NewName: string = String.Empty with get, set

    let private setNameHandler (parseResult: ParseResult) (setNameParameters: SetNameParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult

                let validateIncomingParameters = CommonValidations(parseResult, setNameParameters)

                match validateIncomingParameters with
                | Ok _ ->
                    let parameters =
                        Parameters.Owner.SetOwnerNameParameters(
                            OwnerId = setNameParameters.OwnerId,
                            OwnerName = setNameParameters.OwnerName,
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

                                        let! result = Owner.SetName(parameters)

                                        match result with
                                        | Ok returnValue ->
                                            // Update the Grace configuration file with the new Owner name.
                                            let newConfig = Current()
                                            newConfig.OwnerName <- setNameParameters.NewName
                                            updateConfiguration newConfig
                                        | Error _ -> ()

                                        t0.Increment(100.0)
                                        return result
                                    })
                    else
                        let! result = Owner.SetName(parameters)

                        match result with
                        | Ok graceReturnValue ->
                            // Update the Grace configuration file with the new Owner name.
                            let newConfig = Current()
                            newConfig.OwnerName <- graceReturnValue.Properties[setNameParameters.NewName]
                            updateConfiguration newConfig
                        | Error _ -> ()

                        return result
                | Error error -> return Error error
            with ex ->
                return Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    let private SetName =
        CommandHandler.Create(fun (parseResult: ParseResult) (setNameParameters: SetNameParameters) ->
            task {
                let! result = setNameHandler parseResult (setNameParameters |> normalizeIdsAndNames parseResult)
                return result |> renderOutput parseResult
            })

    type SetTypeParameters() =
        inherit CommonParameters()
        member val public OwnerType: string = String.Empty with get, set

    let private setTypeHandler (parseResult: ParseResult) (setTypeParameters: SetTypeParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult

                let validateIncomingParameters = CommonValidations(parseResult, setTypeParameters)

                match validateIncomingParameters with
                | Ok _ ->
                    let parameters =
                        Parameters.Owner.SetOwnerTypeParameters(
                            OwnerId = setTypeParameters.OwnerId,
                            OwnerName = setTypeParameters.OwnerName,
                            OwnerType = setTypeParameters.OwnerType,
                            CorrelationId = setTypeParameters.CorrelationId
                        )

                    if parseResult |> hasOutput then
                        return!
                            progress
                                .Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                    task {
                                        let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                        let! result = Owner.SetType(parameters)
                                        t0.Increment(100.0)
                                        return result
                                    })
                    else
                        return! Owner.SetType(parameters)
                | Error error -> return Error error
            with ex ->
                return Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    let private SetType =
        CommandHandler.Create(fun (parseResult: ParseResult) (setTypeParameters: SetTypeParameters) ->
            task {
                let! result = setTypeHandler parseResult (setTypeParameters |> normalizeIdsAndNames parseResult)
                return result |> renderOutput parseResult
            })

    type SetSearchVisibilityParameters() =
        inherit CommonParameters()
        member val public SearchVisibility: string = String.Empty with get, set

    let private searchVisibilityHandler (parseResult: ParseResult) (setSearchVisibilityParameters: SetSearchVisibilityParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult

                let validateIncomingParameters = CommonValidations(parseResult, setSearchVisibilityParameters)

                match validateIncomingParameters with
                | Ok _ ->
                    let parameters =
                        Parameters.Owner.SetOwnerSearchVisibilityParameters(
                            OwnerId = setSearchVisibilityParameters.OwnerId,
                            OwnerName = setSearchVisibilityParameters.OwnerName,
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

                                        let! result = Owner.SetSearchVisibility(parameters)
                                        t0.Increment(100.0)
                                        return result
                                    })
                    else
                        return! Owner.SetSearchVisibility(parameters)
                | Error error -> return Error error
            with ex ->
                return Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    let private SetSearchVisibility =
        CommandHandler.Create(fun (parseResult: ParseResult) (setSearchVisibilityParameters: SetSearchVisibilityParameters) ->
            task {
                let! result = searchVisibilityHandler parseResult (setSearchVisibilityParameters |> normalizeIdsAndNames parseResult)

                return result |> renderOutput parseResult
            })

    type SetDescriptionParameters() =
        inherit CommonParameters()
        member val public Description: string = String.Empty with get, set

    let private descriptionHandler (parseResult: ParseResult) (setDescriptionParameters: SetDescriptionParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult

                let validateIncomingParameters = CommonValidations(parseResult, setDescriptionParameters)

                match validateIncomingParameters with
                | Ok _ ->
                    let parameters =
                        Parameters.Owner.SetOwnerDescriptionParameters(
                            OwnerId = setDescriptionParameters.OwnerId,
                            OwnerName = setDescriptionParameters.OwnerName,
                            Description = setDescriptionParameters.Description,
                            CorrelationId = setDescriptionParameters.CorrelationId
                        )

                    if parseResult |> hasOutput then
                        return!
                            progress
                                .Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                    task {
                                        let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                        let! result = Owner.SetDescription(parameters)
                                        t0.Increment(100.0)
                                        return result
                                    })
                    else
                        return! Owner.SetDescription(parameters)
                | Error error -> return Error error
            with ex ->
                return Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    let private SetDescription =
        CommandHandler.Create(fun (parseResult: ParseResult) (setDescriptionParameters: SetDescriptionParameters) ->
            task {
                let! result = descriptionHandler parseResult (setDescriptionParameters |> normalizeIdsAndNames parseResult)

                return result |> renderOutput parseResult
            })

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
                        Parameters.Owner.DeleteOwnerParameters(
                            OwnerId = deleteParameters.OwnerId,
                            OwnerName = deleteParameters.OwnerName,
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

                                        let! result = Owner.Delete(parameters)
                                        t0.Increment(100.0)
                                        return result
                                    })
                    else
                        return! Owner.Delete(parameters)
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
                        Parameters.Owner.UndeleteOwnerParameters(
                            OwnerId = undeleteParameters.OwnerId,
                            OwnerName = undeleteParameters.OwnerName,
                            CorrelationId = undeleteParameters.CorrelationId
                        )

                    if parseResult |> hasOutput then
                        return!
                            progress
                                .Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                    task {
                                        let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                        let! result = Owner.Undelete(parameters)
                                        t0.Increment(100.0)
                                        return result
                                    })
                    else
                        return! Owner.Undelete(parameters)
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
        let addCommonOptions (command: Command) = command |> addOption Options.ownerName |> addOption Options.ownerId

        // Create main command and aliases, if any.`
        let ownerCommand = new Command("owner", Description = "Create, change, or delete owner-level information.")

        // Add subcommands.
        let ownerCreateCommand =
            new Command("create", Description = "Create a new owner.")
            |> addOption Options.ownerNameRequired
            |> addOption Options.ownerId
            |> addOption Options.doNotSwitch

        ownerCreateCommand.Handler <- Create
        ownerCommand.AddCommand(ownerCreateCommand)

        let getCommand =
            new Command("get", Description = "Gets details for the owner.")
            |> addOption Options.includeDeleted
            |> addCommonOptions

        getCommand.Handler <- Get
        ownerCommand.AddCommand(getCommand)

        let setNameCommand =
            new Command("set-name", Description = "Change the name of the owner.")
            |> addOption Options.newName
            |> addCommonOptions

        setNameCommand.Handler <- SetName
        ownerCommand.AddCommand(setNameCommand)

        let setTypeCommand =
            new Command("set-type", Description = "Change the type of the owner.")
            |> addOption Options.ownerTypeRequired
            |> addCommonOptions

        setTypeCommand.Handler <- SetType
        ownerCommand.AddCommand(setTypeCommand)

        let setSearchVisibilityCommand =
            new Command("set-search-visibility", Description = "Change the search visibility of the owner.")
            |> addOption Options.searchVisibilityRequired
            |> addCommonOptions

        setSearchVisibilityCommand.Handler <- SetSearchVisibility
        ownerCommand.AddCommand(setSearchVisibilityCommand)

        let setDescriptionCommand =
            new Command("set-description", Description = "Change the description of the owner.")
            |> addOption Options.descriptionRequired
            |> addCommonOptions

        setDescriptionCommand.Handler <- SetDescription
        ownerCommand.AddCommand(setDescriptionCommand)

        let deleteCommand =
            new Command("delete", Description = "Delete the owner.")
            |> addOption Options.force
            |> addOption Options.deleteReason
            |> addCommonOptions

        deleteCommand.Handler <- Delete
        ownerCommand.AddCommand(deleteCommand)

        let undeleteCommand =
            new Command("undelete", Description = "Undelete a deleted owner.")
            |> addCommonOptions

        undeleteCommand.Handler <- Undelete
        ownerCommand.AddCommand(undeleteCommand)

        ownerCommand
