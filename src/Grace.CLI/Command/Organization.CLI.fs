namespace Grace.Cli.Command

open FSharpPlus
open Grace.Cli.Common
open Grace.SDK
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Types
open Grace.Shared.Validation.Errors.Organization
open NodaTime
open Spectre.Console
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
        let ownerId = new Option<String>("--ownerId", IsRequired = false, Description = "The organization's owner ID <Guid>.", Arity = ArgumentArity.ExactlyOne)
        ownerId.SetDefaultValue($"{Current().OwnerId}")
        let ownerName = new Option<String>("--ownerName", IsRequired = false, Description = "The organization's owner name. [default: current owner]", Arity = ArgumentArity.ExactlyOne)
        let organizationId = new Option<String>("--organizationId", IsRequired = false, Description = "The organization ID <Guid>.", Arity = ArgumentArity.ExactlyOne)
        organizationId.SetDefaultValue($"{Current().OrganizationId}")
        let organizationName = new Option<String>("--organizationName", IsRequired = false, Description = "The name of the organization. [default: current organization]", Arity = ArgumentArity.ExactlyOne)
        let organizationNameRequired = new Option<String>("--organizationName", IsRequired = true, Description = "The name of the organization.", Arity = ArgumentArity.ExactlyOne)
        let organizationType = (new Option<String>("--organizationType", IsRequired = true, Description = "The type of the organization. [default: Public]", Arity = ArgumentArity.ExactlyOne))
                                            .FromAmong(Utilities.listCases(typeof<OrganizationType>))
        let searchVisibility = (new Option<String>("--searchVisibility", IsRequired = true, Description = "Enables or disables the organization appearing in searches. [default: Visible]", Arity = ArgumentArity.ExactlyOne))
                                            .FromAmong(Utilities.listCases(typeof<SearchVisibility>))
        let description = new Option<String>("--description", IsRequired = true, Description = "Description of the owner.", Arity = ArgumentArity.ExactlyOne)
        let newName = new Option<String>("--newName", IsRequired = true, Description = "The new name of the organization.", Arity = ArgumentArity.ExactlyOne)
        let force = new Option<bool>("--force", IsRequired = false, Description = "Delete even if there is data under this organization. [default: false]")
        let deleteReason = new Option<String>("--deleteReason", IsRequired = true, Description = "The reason for deleting the organization.", Arity = ArgumentArity.ExactlyOne)
        let doNotSwitch = new Option<bool>("--doNotSwitch", IsRequired = false, Description = "Do not switch to the new organization as the current organization.", Arity = ArgumentArity.ExactlyOne)

    let CommonValidations (parseResult: ParseResult) (commonParameters: CommonParameters) =
        let ``OwnerId must be a Guid`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            let mutable ownerId: Guid = Guid.Empty
            if parseResult.CommandResult.FindResultFor(Options.ownerId) <> null && Guid.TryParse(commonParameters.OwnerId, &ownerId) = false then 
                Error (GraceError.Create (OrganizationError.getErrorMessage InvalidOwnerId) (commonParameters.CorrelationId))
            else
                Ok (parseResult, commonParameters)

        let ``OwnerName must be a valid Grace name`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            if parseResult.CommandResult.FindResultFor(Options.ownerName) <> null && not <| Constants.GraceNameRegex.IsMatch(commonParameters.OwnerName) then 
                Error (GraceError.Create (OrganizationError.getErrorMessage InvalidOwnerName) (commonParameters.CorrelationId))
            else
                Ok (parseResult, commonParameters)

        let ``OrganizationId must be a Guid`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            let mutable organizationId: Guid = Guid.Empty
            if parseResult.CommandResult.FindResultFor(Options.organizationId) <> null && Guid.TryParse(commonParameters.OrganizationId, &organizationId) = false then 
                Error (GraceError.Create (OrganizationError.getErrorMessage InvalidOrganizationId) (commonParameters.CorrelationId))
            else
                Ok (parseResult, commonParameters)

        let ``OrganizationName must be a valid Grace name`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            let hasOption = (parseResult.CommandResult.FindResultFor(Options.organizationNameRequired) <> null) || (parseResult.CommandResult.FindResultFor(Options.organizationName) <> null)
            if hasOption && not <| Constants.GraceNameRegex.IsMatch(commonParameters.OrganizationName) then 
                Error (GraceError.Create (OrganizationError.getErrorMessage InvalidOrganizationName) (commonParameters.CorrelationId))
            else
                Ok (parseResult, commonParameters)

        (parseResult, commonParameters)
            |>  ``OwnerId must be a Guid``
            >>= ``OwnerName must be a valid Grace name``
            >>= ``OrganizationId must be a Guid``
            >>= ``OrganizationName must be a valid Grace name``

    let ``OrganizationName must not be empty`` (parseResult: ParseResult, commonParameters: CommonParameters) =
        if (parseResult.HasOption(Options.organizationNameRequired) || parseResult.HasOption(Options.organizationName))
                && not <| String.IsNullOrEmpty(commonParameters.OrganizationName) then 
            Ok (parseResult, commonParameters)
        else
            Error (GraceError.Create (OrganizationError.getErrorMessage OrganizationNameIsRequired) (commonParameters.CorrelationId))

    let ``Either OwnerId or OwnerName must be provided`` (parseResult: ParseResult, commonParameters: CommonParameters) =
        if (parseResult.HasOption(Options.ownerId) || parseResult.HasOption(Options.ownerName)) then 
            Ok (parseResult, commonParameters)
        else
            Error (GraceError.Create (OrganizationError.getErrorMessage EitherOwnerIdOrOwnerNameRequired) (commonParameters.CorrelationId))
            
    // Create subcommand.
    type CreateParameters() = 
        inherit CommonParameters()
    let private createHandler (parseResult: ParseResult) (createParameters: CreateParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let validateIncomingParameters = CommonValidations parseResult createParameters
                match validateIncomingParameters with
                | Ok _ -> 
                    let organizationId = if parseResult.FindResultFor(Options.organizationId).IsImplicit then Guid.NewGuid().ToString() else createParameters.OrganizationId
                    let parameters = Parameters.Organization.CreateParameters(OwnerId = createParameters.OwnerId, OwnerName = createParameters.OwnerName, OrganizationId = organizationId, OrganizationName = createParameters.OrganizationName, CorrelationId = createParameters.CorrelationId)
                    if parseResult |> showOutput then
                        return! progress.Columns(progressColumns)
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
            with
                | ex -> return Error (GraceError.Create $"{Utilities.createExceptionResponse ex}" (parseResult |> getCorrelationId))
        }
    let private Create =
        CommandHandler.Create(fun (parseResult: ParseResult) (createParameters: CreateParameters) ->
            task {                
                let! result = createHandler parseResult createParameters
                match result with
                | Ok returnValue ->
                    // Update the Grace configuration file with the newly-created repository.
                    if not <| parseResult.HasOption(Options.doNotSwitch) then
                        let newConfig = Current()
                        newConfig.OrganizationId <- Guid.Parse(returnValue.Properties[nameof(OrganizationId)])
                        newConfig.OrganizationName <- returnValue.Properties[nameof(OrganizationName)]
                        updateConfiguration newConfig
                | Error _ -> ()
                return result |> renderOutput parseResult
            })

    // SetName subcommand
    type NameParameters() =
        inherit CommonParameters()
        member val public NewName: string = String.Empty with get, set
    let private setNameHandler (parseResult: ParseResult) (setNameParameters: NameParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let validateIncomingParameters = CommonValidations parseResult setNameParameters
                match validateIncomingParameters with
                | Ok _ -> 
                    let parameters = Parameters.Organization.NameParameters(OrganizationId = setNameParameters.OrganizationId, OrganizationName = setNameParameters.OrganizationName, NewName = setNameParameters.NewName, CorrelationId = setNameParameters.CorrelationId)
                    if parseResult |> showOutput then
                        return! progress.Columns(progressColumns)
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
            with
                | ex -> return Error (GraceError.Create $"{Utilities.createExceptionResponse ex}" (parseResult |> getCorrelationId))
        }
    let private SetName =
        CommandHandler.Create(fun (parseResult: ParseResult) (setNameParameters: NameParameters) ->
            task {                
                let! result = setNameHandler parseResult setNameParameters
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
                let validateIncomingParameters = CommonValidations parseResult setTypeParameters
                match validateIncomingParameters with
                | Ok _ -> 
                    let parameters = Parameters.Organization.TypeParameters(
                                        OwnerId = setTypeParameters.OwnerId, 
                                        OwnerName = setTypeParameters.OwnerName, 
                                        OrganizationId = setTypeParameters.OrganizationId, 
                                        OrganizationName = setTypeParameters.OrganizationName, 
                                        OrganizationType = setTypeParameters.OrganizationType,
                                        CorrelationId = setTypeParameters.CorrelationId)
                    if parseResult |> showOutput then
                        return! progress.Columns(progressColumns)
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
            with
                | ex -> return Error (GraceError.Create $"{Utilities.createExceptionResponse ex}" (parseResult |> getCorrelationId))
        }
    let private SetType =
        CommandHandler.Create(fun (parseResult: ParseResult) (setTypeParameters: TypeParameters) ->
            task {                
                let! result = setTypeHandler parseResult setTypeParameters
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
                let validateIncomingParameters = CommonValidations parseResult setSearchVisibilityParameters
                match validateIncomingParameters with
                | Ok _ ->
                    let organizationId = if not <| String.IsNullOrEmpty(setSearchVisibilityParameters.OrganizationId) then setSearchVisibilityParameters.OrganizationId else $"{Current().OrganizationId}"
                    let parameters = Parameters.Organization.SearchVisibilityParameters(
                                        OwnerId = setSearchVisibilityParameters.OwnerId, 
                                        OwnerName = setSearchVisibilityParameters.OwnerName, 
                                        OrganizationId = organizationId, 
                                        OrganizationName = setSearchVisibilityParameters.OrganizationName, 
                                        SearchVisibility = setSearchVisibilityParameters.SearchVisibility, 
                                        CorrelationId = setSearchVisibilityParameters.CorrelationId)
                    if parseResult |> showOutput then
                        return! progress.Columns(progressColumns)
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
            with
                | ex -> return Error (GraceError.Create $"{Utilities.createExceptionResponse ex}" (parseResult |> getCorrelationId))
        }
    let private SetSearchVisibility =
        CommandHandler.Create(fun (parseResult: ParseResult) (setSearchVisibilityParameters: SearchVisibilityParameters) ->
            task {
                let! result = setSearchVisibilityHandler parseResult setSearchVisibilityParameters
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
                let validateIncomingParameters = CommonValidations parseResult descriptionParameters
                match validateIncomingParameters with
                | Ok _ -> 
                    let parameters = Parameters.Organization.DescriptionParameters(
                                        OwnerId = descriptionParameters.OwnerId,
                                        OwnerName = descriptionParameters.OwnerName,
                                        OrganizationId = descriptionParameters.OrganizationId, 
                                        OrganizationName = descriptionParameters.OrganizationName, 
                                        Description = descriptionParameters.Description, 
                                        CorrelationId = descriptionParameters.CorrelationId)
                    if parseResult |> showOutput then
                        return! progress.Columns(progressColumns)
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
            with
                | ex -> return Error (GraceError.Create $"{Utilities.createExceptionResponse ex}" (parseResult |> getCorrelationId))
        }
    let private SetDescription =
        CommandHandler.Create(fun (parseResult: ParseResult) (descriptionParameters: DescriptionParameters) ->
            task {                
                let! result = setDescriptionHandler parseResult descriptionParameters
                return result |> renderOutput parseResult
            })
        
    // Delete subcommand
    type DeleteParameters() = 
        inherit CommonParameters()
        member val public Force: bool = false with get, set
        member val public DeleteReason: string = String.Empty with get, set
    let private deleteHandler (parseResult: ParseResult) (deleteParameters: DeleteParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let validateIncomingParameters = CommonValidations parseResult deleteParameters
                match validateIncomingParameters with
                | Ok _ -> 
                    let parameters = Parameters.Organization.DeleteParameters(
                                        OwnerId = deleteParameters.OwnerId,
                                        OwnerName = deleteParameters.OwnerName,
                                        OrganizationId = deleteParameters.OrganizationId, 
                                        OrganizationName = deleteParameters.OrganizationName, 
                                        Force = deleteParameters.Force, 
                                        DeleteReason = deleteParameters.DeleteReason, 
                                        CorrelationId = deleteParameters.CorrelationId)
                    if parseResult |> showOutput then
                        return! progress.Columns(progressColumns)
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
            with
                | ex -> return Error (GraceError.Create $"{Utilities.createExceptionResponse ex}" (parseResult |> getCorrelationId))
        }
    let private Delete =
        CommandHandler.Create(fun (parseResult: ParseResult) (deleteParameters: DeleteParameters) ->
            task {                
                let! result = deleteHandler parseResult deleteParameters
                return result |> renderOutput parseResult
            })

    // Undelete subcommand
    type UndeleteParameters() = 
        inherit CommonParameters()
    let private undeleteHandler (parseResult: ParseResult) (undeleteParameters: UndeleteParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let validateIncomingParameters = CommonValidations parseResult undeleteParameters
                match validateIncomingParameters with
                | Ok _ -> 
                    let parameters = Parameters.Organization.DeleteParameters(
                                        OwnerId = undeleteParameters.OwnerId,
                                        OwnerName = undeleteParameters.OwnerName,
                                        OrganizationId = undeleteParameters.OrganizationId, 
                                        OrganizationName = undeleteParameters.OrganizationName, 
                                        CorrelationId = undeleteParameters.CorrelationId)
                    if parseResult |> showOutput then
                        return! progress.Columns(progressColumns)
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
            with
                | ex -> return Error (GraceError.Create $"{Utilities.createExceptionResponse ex}" (parseResult |> getCorrelationId))
        }
    let private Undelete =
        CommandHandler.Create(fun (parseResult: ParseResult) (undeleteParameters: UndeleteParameters) ->
            task {                
                let! result = undeleteHandler parseResult undeleteParameters
                return result |> renderOutput parseResult
            })

    let Build = 
        let addCommonOptionsWithoutOrganizationName (command: Command) =
            command |> addOption Options.organizationId
                    |> addOption Options.ownerName
                    |> addOption Options.ownerId

        let addCommonOptions (command: Command) =
            command |> addOption Options.organizationName |> addCommonOptionsWithoutOrganizationName

        // Create main command and aliases, if any.`
        let organizationCommand = new Command("organization", Description = "Create, change, or delete organization-level information.")
        organizationCommand.AddAlias("org")

        // Add subcommands.
        let organizationCreateCommand = new Command("create", Description = "Create a new organization.") |> addOption Options.organizationNameRequired |> addCommonOptionsWithoutOrganizationName
        organizationCreateCommand.Handler <- Create
        organizationCommand.AddCommand(organizationCreateCommand)

        let setNameCommand = new Command("set-name", Description = "Change the name of the organization.") |> addOption Options.newName |> addCommonOptions
        setNameCommand.Handler <- SetName
        organizationCommand.AddCommand(setNameCommand)
        
        let setTypeCommand = new Command("set-type", Description = "Change the type of the organization.") |> addOption Options.organizationType |> addCommonOptions
        setTypeCommand.Handler <- SetType
        organizationCommand.AddCommand(setTypeCommand)
        
        let setSearchVisibilityCommand = new Command("set-search-visibility", Description = "Change the search visibility of the organization.") |> addOption Options.searchVisibility |> addCommonOptions
        setSearchVisibilityCommand.Handler <- SetSearchVisibility
        organizationCommand.AddCommand(setSearchVisibilityCommand)
        
        let setDescriptionCommand = new Command("set-description", Description = "Change the description of the organization.") |> addOption Options.description |> addCommonOptions
        setDescriptionCommand.Handler <- SetDescription
        organizationCommand.AddCommand(setDescriptionCommand)
        
        let deleteCommand = new Command("delete", Description = "Delete the organization.") |> addOption Options.force |> addOption Options.deleteReason |> addCommonOptions
        deleteCommand.Handler <- Delete
        organizationCommand.AddCommand(deleteCommand)

        let undeleteCommand = new Command("undelete", Description = "Undeletes the organization.") |> addCommonOptions
        undeleteCommand.Handler <- Undelete
        organizationCommand.AddCommand(undeleteCommand)

        organizationCommand
