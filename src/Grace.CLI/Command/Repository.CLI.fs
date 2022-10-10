namespace Grace.Cli.Command

open FSharpPlus
open Grace.Cli.Common
open Grace.Cli.Services
open Grace.SDK
open Grace.Shared
open Grace.Shared.Dto
open Grace.Shared.Parameters
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared.Client.Configuration
open Grace.Shared.Parameters.Repository
open Grace.Shared.Validation.Errors.Repository
open NodaTime
open Spectre.Console
open System
open System.Collections.Generic
open System.CommandLine
open System.CommandLine.NamingConventionBinder
open System.CommandLine.Parsing
open System.Linq
open System.Threading.Tasks

module Repository =

    type CommonParameters() = 
        inherit ParameterBase()
        member val public Name: string = String.Empty with get, set
        member val public OwnerId: string = String.Empty with get, set
        member val public OwnerName: string = String.Empty with get, set
        member val public OrganizationId: string = String.Empty with get, set
        member val public OrganizationName: string = String.Empty with get, set
        member val public RepositoryId: string = String.Empty with get, set
        member val public RepositoryName: string = String.Empty with get, set

    module private Options =
        let ownerId = new Option<String>("--ownerId", IsRequired = false, Description = "The repository's owner ID <Guid>.", Arity = ArgumentArity.ZeroOrOne)
        ownerId.SetDefaultValue($"{Current().OwnerId}")
        let ownerName = new Option<String>("--ownerName", IsRequired = false, Description = "The repository's owner name. [default: current owner]", Arity = ArgumentArity.ExactlyOne)
        let organizationId = new Option<String>("--organizationId", IsRequired = false, Description = "The repository's organization ID <Guid>.", Arity = ArgumentArity.ZeroOrOne)
        organizationId.SetDefaultValue($"{Current().OrganizationId}")
        let organizationName = new Option<String>("--organizationName", IsRequired = false, Description = "The repository's organization name. [default: current organization]", Arity = ArgumentArity.ZeroOrOne)
        let repositoryId = new Option<String>([|"--repositoryId"; "-r"|], IsRequired = false, Description = "The repository's ID <Guid>.", Arity = ArgumentArity.ExactlyOne)
        repositoryId.SetDefaultValue($"{Current().RepositoryId}")
        let repositoryName = new Option<String>([|"--repositoryName"; "-n"|], IsRequired = false, Description = "The name of the repository. [default: current repository]", Arity = ArgumentArity.ExactlyOne)
        let requiredRepositoryName = new Option<String>([|"--repositoryName"; "-n"|], IsRequired = true, Description = "The name of the repository.", Arity = ArgumentArity.ExactlyOne)
        let visibility = (new Option<RepositoryVisibility>("--visibility", IsRequired = true, Description = "The visibility of the repository.", Arity = ArgumentArity.ExactlyOne))
                            .FromAmong (Utilities.listCases(typeof<RepositoryVisibility>))
        let status = (new Option<String>("--status", IsRequired = true, Description = "The status of the repository.", Arity = ArgumentArity.ExactlyOne))
                            .FromAmong(Utilities.listCases(typeof<RepositoryStatus>))
        let recordSaves = new Option<bool>("--recordSaves", IsRequired = true, Description = "True to record all saves; false to turn it off.", Arity = ArgumentArity.ExactlyOne)
        let defaultServerApiVersion = (new Option<String>("--defaultServerApiVersion", IsRequired = true, Description = "The default version of the server API that clients should use when accessing this repository.", Arity = ArgumentArity.ExactlyOne))
                                            .FromAmong(Utilities.listCases(typeof<Constants.ServerApiVersions>))
        let saveDays = new Option<RepositoryVisibility>("--saveDays", IsRequired = true, Description = "How many days to keep saves. [default: 7.0]", Arity = ArgumentArity.ExactlyOne)
        let checkpointDays = new Option<RepositoryVisibility>("--checkpointDays", IsRequired = true, Description = "How many days to keep checkpoints. [default: Double.MaxValue]", Arity = ArgumentArity.ExactlyOne)
        let newName = new Option<String>("--newName", IsRequired = true, Description = "The new name for the repository.", Arity = ArgumentArity.ExactlyOne)
        let deleteReason = new Option<String>("--deleteReason", IsRequired = true, Description = "The reason for deleting the repository.", Arity = ArgumentArity.ExactlyOne)
        let graceConfig = new Option<String>("--graceConfig", IsRequired = false, Description = "The path of a Grace config file that you'd like to use instead of the default graceconfig.json.", Arity = ArgumentArity.ExactlyOne)
        let force = new Option<bool>("--force", IsRequired = false, Description = "Deletes repository even if there are links to other repositories.", Arity = ArgumentArity.ExactlyOne)
        let doNotSwitch = new Option<bool>("--doNotSwitch", IsRequired = false, Description = "Do not switch to the new repository as the current repository.", Arity = ArgumentArity.ExactlyOne)
        let enabled = new Option<bool>("--enabled", IsRequired = true, Description = "True to enable the merge type; false to disable it.", Arity = ArgumentArity.ExactlyOne)
        //enabled.SetDefaultValue(false)
        let includeDeleted = new Option<bool>("--includeDeleted", IsRequired = false, Description = "True to include deleted branches; false to exclude them.", Arity = ArgumentArity.ZeroOrOne)
        //includeDeleted.SetDefaultValue(false)

    let private CommonValidations parseResult commonParameters =

        let ``OwnerId must be a Guid`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            let mutable ownerId: Guid = Guid.Empty
            if parseResult.HasOption(Options.ownerId) && Guid.TryParse(commonParameters.OwnerId, &ownerId) = false then 
                Error (GraceError.Create (RepositoryError.getErrorMessage InvalidOwnerId) (commonParameters.CorrelationId))
            else
                Ok (parseResult, commonParameters)

        let ``OwnerName must be a valid Grace name`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            if not (parseResult.HasOption(Options.ownerName)) || (parseResult.HasOption(Options.ownerName) && Constants.GraceNameRegex.IsMatch(commonParameters.OwnerName)) then 
                Ok (parseResult, commonParameters)
            else
                Error (GraceError.Create (RepositoryError.getErrorMessage InvalidOwnerName) (commonParameters.CorrelationId))

        let ``OrganizationId must be a Guid`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            let mutable organizationId: Guid = Guid.Empty
            if parseResult.HasOption(Options.organizationId) && Guid.TryParse(commonParameters.OrganizationId, &organizationId) = false then 
                Error (GraceError.Create (RepositoryError.getErrorMessage InvalidOrganizationId) (commonParameters.CorrelationId))
            else
                Ok (parseResult, commonParameters)

        let ``OrganizationName must be a valid Grace name`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            if not (parseResult.HasOption(Options.organizationName)) || (parseResult.HasOption(Options.organizationName) && Constants.GraceNameRegex.IsMatch(commonParameters.OrganizationName)) then 
                Ok (parseResult, commonParameters)
            else
                Error (GraceError.Create (RepositoryError.getErrorMessage InvalidOrganizationName) (commonParameters.CorrelationId))

        let ``RepositoryId must be a Guid`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            let mutable repositoryId: Guid = Guid.Empty
            if parseResult.HasOption(Options.repositoryId) && Guid.TryParse(commonParameters.RepositoryId, &repositoryId) = false then 
                Error (GraceError.Create (RepositoryError.getErrorMessage InvalidRepositoryId) (commonParameters.CorrelationId))
            else
                Ok (parseResult, commonParameters)

        (parseResult, commonParameters)
            |>  ``OwnerId must be a Guid``
            >>= ``OwnerName must be a valid Grace name``
            >>= ``OrganizationId must be a Guid``
            >>= ``OrganizationName must be a valid Grace name``
            >>= ``RepositoryId must be a Guid``

    let ``Either RepositoryId or RepositoryName must be specified`` (parseResult: ParseResult, commonParameters: CommonParameters) =
        if parseResult.HasOption(Options.repositoryId) || parseResult.HasOption(Options.repositoryName) || parseResult.HasOption(Options.requiredRepositoryName) then
            Ok (parseResult, commonParameters)
        else
            Error (GraceError.Create (RepositoryError.getErrorMessage EitherRepositoryIdOrRepositoryNameRequired) (commonParameters.CorrelationId))

    /// Adjusts parameters to account for whether Id's or Name's were specified by the user.
    let normalizeIdsAndNames<'T when 'T :> CommonParameters> (parseResult: ParseResult) (parameters: 'T) =
        // If the name was specified on the command line, but the id wasn't, drop the default assignment of the id.
        if parseResult.CommandResult.FindResultFor(Options.ownerId).IsImplicit && not <| isNull(parseResult.CommandResult.FindResultFor(Options.ownerName)) && not <| parseResult.CommandResult.FindResultFor(Options.ownerName).IsImplicit then
            parameters.OwnerId <- String.Empty
        if parseResult.CommandResult.FindResultFor(Options.organizationId).IsImplicit && not <| isNull(parseResult.CommandResult.FindResultFor(Options.organizationName)) && not <| parseResult.CommandResult.FindResultFor(Options.organizationName).IsImplicit then
            parameters.OrganizationId <- String.Empty
        if parseResult.CommandResult.FindResultFor(Options.repositoryId).IsImplicit && not <| isNull(parseResult.CommandResult.FindResultFor(Options.repositoryName)) && not <| parseResult.CommandResult.FindResultFor(Options.repositoryName).IsImplicit then
            parameters.RepositoryId <- String.Empty
        parameters

    /// Populates OwnerId, OrganizationId, and RepositoryId based on the command parameters and the contents of graceconfig.json.
    let getIds<'T when 'T :> CommonParameters> (parameters: 'T) = 
        let ownerId = if not <| String.IsNullOrEmpty(parameters.OwnerId) || not <| String.IsNullOrEmpty(parameters.OwnerName)
                       then parameters.OwnerId 
                       else $"{Current().OwnerId}"
        let organizationId = if not <| String.IsNullOrEmpty(parameters.OrganizationId) || not <| String.IsNullOrEmpty(parameters.OrganizationName)
                             then parameters.OrganizationId
                             else $"{Current().OrganizationId}"
        let repositoryId = if not <| String.IsNullOrEmpty(parameters.RepositoryId) || not <| String.IsNullOrEmpty(parameters.RepositoryName)
                           then parameters.RepositoryId 
                           else $"{Current().RepositoryId}"
        (ownerId, organizationId, repositoryId)

    // Create subcommand.
    type CreateParameters() = 
        inherit CommonParameters()
    let private createHandler (parseResult: ParseResult) (createParameters: CreateParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let validateIncomingParameters = CommonValidations parseResult createParameters >>= ``Either RepositoryId or RepositoryName must be specified``
                match validateIncomingParameters with
                | Ok _ -> 
                    let optionResult = parseResult.FindResultFor(Options.repositoryId)
                    let repositoryId = if ((not <| isNull(optionResult)) && optionResult.IsImplicit) || isNull(optionResult) then Guid.NewGuid().ToString() else createParameters.RepositoryId
                    let ownerId = if not <| String.IsNullOrEmpty(createParameters.OwnerId) || not <| String.IsNullOrEmpty(createParameters.OwnerName)
                                  then createParameters.OwnerId
                                  else $"{Current().OwnerId}"
                    let organizationId = if not <| String.IsNullOrEmpty(createParameters.OrganizationId) || not <| String.IsNullOrEmpty(createParameters.OrganizationName)
                                         then createParameters.OrganizationId
                                         else $"{Current().OrganizationId}"
                    let enhancedParameters = Repository.CreateParameters(RepositoryId = repositoryId, 
                        RepositoryName = createParameters.RepositoryName, 
                        OwnerId = ownerId, 
                        OwnerName = createParameters.OwnerName,
                        OrganizationId = organizationId,
                        OrganizationName = createParameters.OrganizationName,
                        CorrelationId = createParameters.CorrelationId)

                    if parseResult |> showOutput then
                        return! progress.Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                task {
                                    let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                    let! result = Repository.Create(enhancedParameters)
                                    t0.Increment(100.0)
                                    return result
                                })
                    else
                        return! Repository.Create(enhancedParameters)
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
                        newConfig.RepositoryId <- Guid.Parse(returnValue.Properties[nameof(RepositoryId)])
                        newConfig.RepositoryName <- RepositoryName (returnValue.Properties[nameof(RepositoryName)])
                        newConfig.BranchId <- Guid.Parse(returnValue.Properties[nameof(BranchId)])
                        newConfig.BranchName <- BranchName (returnValue.Properties[nameof(BranchName)])
                        updateConfiguration newConfig
                | Error error ->
                    logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                return result |> renderOutput parseResult
            })

    // Init subcommand
    type InitParameters() =
        inherit CommonParameters()
        member val public GraceConfig = String.Empty with get, set

    let private initHandler (parseResult: ParseResult) (parameters: InitParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let validateIncomingParameters = CommonValidations parseResult parameters
                match validateIncomingParameters with
                | Ok _ -> 
                    let (ownerId, organizationId, repositoryId) = getIds parameters
                    let enhancedParameters = Repository.InitParameters(
                        OwnerId = ownerId,
                        OwnerName = parameters.OwnerName,
                        OrganizationId = organizationId,
                        OrganizationName = parameters.OrganizationName,
                        RepositoryId = repositoryId, 
                        RepositoryName = parameters.RepositoryName, 
                        CorrelationId = parameters.CorrelationId)

                    return Ok (GraceReturnValue.Create "Not yet implemented" (getCorrelationId parseResult))
                | Error error -> return Error error
            with ex -> return Error (GraceError.Create $"{Utilities.createExceptionResponse ex}" (parseResult |> getCorrelationId))
        }
    let private Init =
        CommandHandler.Create(fun (parseResult: ParseResult) (initParameters: InitParameters) ->
            task {                
                let! result = initHandler parseResult initParameters
                return result |> renderOutput parseResult
            })

    // Get-Branches subcommand
    type GetBranchesParameters() = 
        inherit CommonParameters()
        member val public IncludeDeleted = false with get, set
    let private getBranchesHandler (parseResult: ParseResult) (parameters: GetBranchesParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let validateIncomingParameters = CommonValidations parseResult parameters
                match validateIncomingParameters with
                | Ok _ -> 
                    let normalizedParameters = parameters |> normalizeIdsAndNames parseResult
                    let getBranchesParameters = Repository.GetBranchesParameters(
                        OwnerId = normalizedParameters.OwnerId,
                        OwnerName = normalizedParameters.OwnerName,
                        OrganizationId = normalizedParameters.OrganizationId,
                        OrganizationName = normalizedParameters.OrganizationName,
                        RepositoryId = normalizedParameters.RepositoryId, 
                        RepositoryName = normalizedParameters.RepositoryName,
                        IncludeDeleted = normalizedParameters.IncludeDeleted,
                        CorrelationId = normalizedParameters.CorrelationId)

                    if parseResult |> showOutput then
                        return! progress.Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                task {
                                    let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                    let! result = Repository.GetBranches(getBranchesParameters)
                                    t0.Increment(100.0)
                                    return result
                                })
                    else
                        return! Repository.GetBranches(getBranchesParameters)
                | Error error -> return Error error
            with
                | ex -> return Error (GraceError.Create $"{Utilities.createExceptionResponse ex}" (parseResult |> getCorrelationId))
        }
    let private GetBranches =
        CommandHandler.Create(fun (parseResult: ParseResult) (getBranchesParameters: GetBranchesParameters) ->
            task {                
                let! result = getBranchesHandler parseResult getBranchesParameters
                match result with
                | Ok returnValue ->
                    let table = Table(Border = TableBorder.DoubleEdge)
                    table.AddColumn(TableColumn(Markup($"[{Colors.Important}]Branch[/]")))
                         .AddColumn(TableColumn(Markup($"[{Colors.Important}]Updated at[/]")))
                         .AddColumn(TableColumn(Markup($"[{Colors.Important}]Branch Id[/]")))
                         .AddColumn(TableColumn(Markup($"[{Colors.Important}]Parent branch[/]"))) |> ignore
                    let allBranches = returnValue.ReturnValue
                    let parents = allBranches.Select(fun branch -> {|
                        branchId = branch.BranchId
                        branchName = if branch.ParentBranchId = Constants.DefaultParentBranchId then "root"
                                     else allBranches.Where(fun br -> br.BranchId = branch.ParentBranchId).Select(fun br -> br.BranchName).First() |})

                    let branchesWithParentNames = allBranches.Join(parents, (fun branch -> branch.BranchId), (fun parent -> parent.branchId), 
                        (fun branch parent -> {| branchId = branch.BranchId; branchName = branch.BranchName; updatedAt = branch.UpdatedAt; parentBranchName = parent.branchName |})).OrderBy(fun branch -> branch.parentBranchName, branch.branchName)

                    for br in branchesWithParentNames do
                        let updatedAt = match br.updatedAt with | Some t -> instantToLocalTime(t) | None -> String.Empty
                        table.AddRow(br.branchName, updatedAt, $"[{Colors.Deemphasized}]{br.branchId}[/]", br.parentBranchName) |> ignore
                    AnsiConsole.Write(table)
                    return 0
                | Error error ->
                    logToAnsiConsole Colors.Error $"{error.ToString()}"
                    return -1
            })

    // Set-Visibility subcommand
    type SetVisibilityParameters() = 
        inherit CommonParameters()
        member val public Visibility: string = String.Empty with get, set
    let private setVisibilityHandler (parseResult: ParseResult) (parameters: SetVisibilityParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let validateIncomingParameters = CommonValidations parseResult parameters
                match validateIncomingParameters with
                | Ok _ -> 
                    let normalizedParameters = parameters |> normalizeIdsAndNames parseResult
                    let visibilityParameters = Repository.VisibilityParameters(
                        OwnerId = normalizedParameters.OwnerId,
                        OwnerName = normalizedParameters.OwnerName,
                        OrganizationId = normalizedParameters.OrganizationId,
                        OrganizationName = normalizedParameters.OrganizationName,
                        RepositoryId = normalizedParameters.RepositoryId, 
                        RepositoryName = normalizedParameters.RepositoryName, 
                        Visibility = normalizedParameters.Visibility,
                        CorrelationId = normalizedParameters.CorrelationId)

                    if parseResult |> showOutput then
                        return! progress.Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                task {
                                    let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                    let! result = Repository.SetVisibility(visibilityParameters)
                                    t0.Increment(100.0)
                                    return result
                                })
                    else
                        return! Repository.SetVisibility(visibilityParameters)
                | Error error -> return Error error
            with
                | ex -> return Error (GraceError.Create $"{Utilities.createExceptionResponse ex}" (parseResult |> getCorrelationId))
        }
    let private SetVisibility =
        CommandHandler.Create(fun (parseResult: ParseResult) (setNameParameters: SetVisibilityParameters) ->
            task {                
                let! result = setVisibilityHandler parseResult setNameParameters
                return result |> renderOutput parseResult
            })

    // Set-Status subcommand
    type StatusParameters() = 
        inherit CommonParameters()
        member val public Status: string = String.Empty with get, set
    let private setStatusHandler (parseResult: ParseResult) (parameters: StatusParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let validateIncomingParameters = CommonValidations parseResult parameters
                match validateIncomingParameters with
                | Ok _ -> 
                    let normalizedParameters = parameters |> normalizeIdsAndNames parseResult
                    let statusParameters = Repository.StatusParameters(
                        OwnerId = normalizedParameters.OwnerId,
                        OwnerName = normalizedParameters.OwnerName,
                        OrganizationId = normalizedParameters.OrganizationId,
                        OrganizationName = normalizedParameters.OrganizationName,
                        RepositoryId = normalizedParameters.RepositoryId, 
                        RepositoryName = normalizedParameters.RepositoryName, 
                        Status = normalizedParameters.Status,
                        CorrelationId = normalizedParameters.CorrelationId)

                    if parseResult |> showOutput then
                        return! progress.Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                task {
                                    let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                    let! result = Repository.SetStatus(statusParameters)
                                    t0.Increment(100.0)
                                    return result
                                })
                    else
                        return! Repository.SetStatus(statusParameters)
                | Error error -> return Error error
            with
                | ex -> return Error (GraceError.Create $"{Utilities.createExceptionResponse ex}" (parseResult |> getCorrelationId))
        }
    let private SetStatus =
        CommandHandler.Create(fun (parseResult: ParseResult) (setNameParameters: StatusParameters) ->
                task {                
                    let! result = setStatusHandler parseResult setNameParameters
                    return result |> renderOutput parseResult
                })

    // Set-RecordSaves subcommand
    type RecordSavesParameters() = 
        inherit CommonParameters()
        member val public RecordSaves: bool = false with get, set
    let private setRecordSavesHandler (parseResult: ParseResult) (parameters: RecordSavesParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let validateIncomingParameters = CommonValidations parseResult parameters
                match validateIncomingParameters with
                | Ok _ -> 
                    let normalizedParameters = parameters |> normalizeIdsAndNames parseResult
                    let recordSavesParameters = Repository.RecordSavesParameters(
                        OwnerId = normalizedParameters.OwnerId,
                        OwnerName = normalizedParameters.OwnerName,
                        OrganizationId = normalizedParameters.OrganizationId,
                        OrganizationName = normalizedParameters.OrganizationName,
                        RepositoryId = normalizedParameters.RepositoryId, 
                        RepositoryName = normalizedParameters.RepositoryName, 
                        RecordSaves = normalizedParameters.RecordSaves,
                        CorrelationId = normalizedParameters.CorrelationId)

                    if parseResult |> showOutput then
                        return! progress.Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                task {
                                    let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                    let! result = Repository.SetRecordSaves(recordSavesParameters)
                                    t0.Increment(100.0)
                                    return result
                                })
                    else
                        return! Repository.SetRecordSaves(recordSavesParameters)
                | Error error -> return Error error
            with
                | ex -> return Error (GraceError.Create $"{Utilities.createExceptionResponse ex}" (parseResult |> getCorrelationId))
        }
    let private SetRecordSaves =
        CommandHandler.Create(fun (parseResult: ParseResult) (recordSavesParameters: RecordSavesParameters) ->
                    task {                
                        let! result = setRecordSavesHandler parseResult recordSavesParameters
                        return result |> renderOutput parseResult
                    })

    // Set-SaveDays subcommand
    type SaveDaysParameters() = 
        inherit CommonParameters()
        member val public SaveDays: float = Double.MinValue with get, set
    let private setSaveDaysHandler (parseResult: ParseResult) (parameters: SaveDaysParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let validateIncomingParameters = CommonValidations parseResult parameters
                match validateIncomingParameters with
                | Ok _ -> 
                    let normalizedParameters = parameters |> normalizeIdsAndNames parseResult
                    let setSaveDaysParameters = Repository.SaveDaysParameters(
                        OwnerId = normalizedParameters.OwnerId,
                        OwnerName = normalizedParameters.OwnerName,
                        OrganizationId = normalizedParameters.OrganizationId,
                        OrganizationName = normalizedParameters.OrganizationName,
                        RepositoryId = normalizedParameters.RepositoryId, 
                        RepositoryName = normalizedParameters.RepositoryName,
                        CorrelationId = normalizedParameters.CorrelationId,
                        SaveDays = normalizedParameters.SaveDays)

                    if parseResult |> showOutput then
                        return! progress.Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                task {
                                    let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                    let! result = Repository.SetSaveDays(setSaveDaysParameters)
                                    t0.Increment(100.0)
                                    return result
                                })
                    else
                        return! Repository.SetSaveDays(setSaveDaysParameters)
                | Error error -> return Error error
            with
                | ex -> return Error (GraceError.Create $"{Utilities.createExceptionResponse ex}" (parseResult |> getCorrelationId))
        }
    let private SetSaveDays =
        CommandHandler.Create(fun (parseResult: ParseResult) (setNameParameters: SaveDaysParameters) ->
                    task {                
                        let! result = setSaveDaysHandler parseResult setNameParameters
                        return result |> renderOutput parseResult
                    })

    // Set-CheckpointDays subcommand
    type CheckpointDaysParameters() = 
        inherit CommonParameters()
        member val public CheckpointDays: float = Double.MinValue with get, set
    let private setCheckpointDaysHandler (parseResult: ParseResult) (parameters: CheckpointDaysParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let validateIncomingParameters = CommonValidations parseResult parameters
                match validateIncomingParameters with
                | Ok _ -> 
                    let normalizedParameters = parameters |> normalizeIdsAndNames parseResult
                    let checkpointDaysParameters = Repository.CheckpointDaysParameters(
                        OwnerId = normalizedParameters.OwnerId,
                        OwnerName = normalizedParameters.OwnerName,
                        OrganizationId = normalizedParameters.OrganizationId,
                        OrganizationName = normalizedParameters.OrganizationName,
                        RepositoryId = normalizedParameters.RepositoryId, 
                        RepositoryName = normalizedParameters.RepositoryName,
                        CorrelationId = normalizedParameters.CorrelationId,
                        CheckpointDays = normalizedParameters.CheckpointDays)

                    if parseResult |> showOutput then
                        return! progress.Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                task {
                                    let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                    let! result = Repository.SetCheckpointDays(checkpointDaysParameters)
                                    t0.Increment(100.0)
                                    return result
                                })
                    else
                        return! Repository.SetCheckpointDays(checkpointDaysParameters)
                | Error error -> return Error error
            with ex ->
                return Error (GraceError.Create $"{Utilities.createExceptionResponse ex}" (parseResult |> getCorrelationId))
        }
    let private SetCheckpointDays =
        CommandHandler.Create(fun (parseResult: ParseResult) (setNameParameters: CheckpointDaysParameters) ->
                    task {                
                        let! result = setCheckpointDaysHandler parseResult setNameParameters
                        return result |> renderOutput parseResult
                    })
                    
    // Enable merge type subcommands
    type EnableMergeTypeCommand = EnableMergeTypeParameters -> Task<GraceResult<string>>
    type EnableMergeParameters() =
        inherit CommonParameters()
        member val public Enabled = false with get, set
    let private enableMergeTypeHandler (parseResult: ParseResult) (parameters: EnableMergeParameters) (command: EnableMergeTypeCommand) (mergeType: MergeType) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let validateIncomingParameters = CommonValidations parseResult parameters
                match validateIncomingParameters with
                | Ok _ -> 
                    let normalizedParameters = parameters |> normalizeIdsAndNames parseResult
                    let enableMergeTypeParameters = EnableMergeTypeParameters(
                        OwnerId = normalizedParameters.OwnerId,
                        OwnerName = normalizedParameters.OwnerName,
                        OrganizationId = normalizedParameters.OrganizationId,
                        OrganizationName = normalizedParameters.OrganizationName,
                        RepositoryId = normalizedParameters.RepositoryId, 
                        RepositoryName = normalizedParameters.RepositoryName,
                        CorrelationId = normalizedParameters.CorrelationId,
                        Enabled = normalizedParameters.Enabled)

                    if parseResult |> showOutput then
                        return! progress.Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                task {
                                    let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                    let! result = command enableMergeTypeParameters
                                    //let! result = 
                                    //    task {
                                    //        match mergeType with
                                    //        | SingleStep -> return! Repository.EnableSingleStepMerge(enhancedParameters)
                                    //        | Complex -> return! Repository.EnableComplexMerge(enhancedParameters)
                                    //    }
                                    t0.Increment(100.0)
                                    return result
                                })
                    else
                        return! command enableMergeTypeParameters
                | Error error -> return Error error
            with ex ->
                return Error (GraceError.Create $"{Utilities.createExceptionResponse ex}" (parseResult |> getCorrelationId))
        }

    let private EnableSingleStepMerge =
        CommandHandler.Create(fun (parseResult: ParseResult) (enableMergeParameters: EnableMergeParameters) ->
            task {
                let command (parameters: EnableMergeTypeParameters) =
                    task {
                        return! Repository.EnableSingleStepMerge(parameters)
                    }

                let! result = enableMergeTypeHandler parseResult enableMergeParameters command MergeType.SingleStep
                return result |> renderOutput parseResult
            })

    let private EnableComplexMerge =
        CommandHandler.Create(fun (parseResult: ParseResult) (enableMergeParameters: EnableMergeParameters) ->
            task {
                let command (parameters: EnableMergeTypeParameters) =
                    task {
                        return! Repository.EnableComplexMerge(parameters)
                    }

                let! result = enableMergeTypeHandler parseResult enableMergeParameters command MergeType.Complex
                return result |> renderOutput parseResult
            })

    // Set-DefaultServerApiVersion subcommand
    type DefaultServerApiVersionParameters() = 
        inherit CommonParameters()
        member val public DefaultServerApiVersion = String.Empty with get, set
    let private setDefaultServerApiVersionHandler (parseResult: ParseResult) (parameters: DefaultServerApiVersionParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let validateIncomingParameters = CommonValidations parseResult parameters
                match validateIncomingParameters with
                | Ok _ -> 
                    let normalizedParameters = parameters |> normalizeIdsAndNames parseResult
                    let defaultServerApiVersionParameters = Repository.DefaultServerApiVersionParameters(
                        OwnerId = normalizedParameters.OwnerId,
                        OwnerName = normalizedParameters.OwnerName,
                        OrganizationId = normalizedParameters.OrganizationId,
                        OrganizationName = normalizedParameters.OrganizationName,
                        RepositoryId = normalizedParameters.RepositoryId, 
                        RepositoryName = normalizedParameters.RepositoryName,
                        CorrelationId = normalizedParameters.CorrelationId,
                        DefaultServerApiVersion = normalizedParameters.DefaultServerApiVersion)

                    if parseResult |> showOutput then
                        return! progress.Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                task {
                                    let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                    let! result = Repository.SetDefaultServerApiVersion(defaultServerApiVersionParameters)
                                    t0.Increment(100.0)
                                    return result
                                })
                    else
                        return! Repository.SetDefaultServerApiVersion(defaultServerApiVersionParameters)
                | Error error -> return Error error
            with ex -> 
                return Error (GraceError.Create $"{Utilities.createExceptionResponse ex}" (parseResult |> getCorrelationId))
        }
    let private SetDefaultServerApiVersion =
        CommandHandler.Create(fun (parseResult: ParseResult) (defaultServerApiVersionParameters: DefaultServerApiVersionParameters) ->
                    task {                
                        let! result = setDefaultServerApiVersionHandler parseResult defaultServerApiVersionParameters
                        return result |> renderOutput parseResult
                    })

    // Rename subcommand    
    type SetNameParameters() = 
        inherit CommonParameters()
        member val public NewName: string = String.Empty with get, set
    let private setNameHandler (parseResult: ParseResult) (parameters: SetNameParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let validateIncomingParameters = CommonValidations parseResult parameters
                match validateIncomingParameters with
                | Ok _ -> 
                    let normalizedParameters = parameters |> normalizeIdsAndNames parseResult
                    let setNameParameters = Repository.SetNameParameters(
                        OwnerId = normalizedParameters.OwnerId,
                        OwnerName = normalizedParameters.OwnerName,
                        OrganizationId = normalizedParameters.OrganizationId,
                        OrganizationName = normalizedParameters.OrganizationName,
                        RepositoryId = normalizedParameters.RepositoryId, 
                        RepositoryName = normalizedParameters.RepositoryName,
                        CorrelationId = normalizedParameters.CorrelationId,
                        NewName = normalizedParameters.NewName)

                    if parseResult |> showOutput then
                        return! progress.Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                task {
                                    let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                    let! result = Repository.SetName(setNameParameters)
                                    t0.Increment(100.0)
                                    return result
                                })
                    else
                        return! Repository.SetName(setNameParameters)
                | Error error -> return Error error
            with
                | ex -> return Error (GraceError.Create $"{Utilities.createExceptionResponse ex}" (parseResult |> getCorrelationId))
        }
    let private SetName =
        CommandHandler.Create(fun (parseResult: ParseResult) (setNameParameters: SetNameParameters) ->
                task {                
                    let! result = setNameHandler parseResult setNameParameters
                    return result |> renderOutput parseResult
                })

    // Delete subcommand
    type DeleteParameters() =
        inherit CommonParameters()
        member val public Force: Boolean = false with get, set
        member val public DeleteReason: string = String.Empty with get, set
    let private deleteHandler (parseResult: ParseResult) (parameters: DeleteParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let validateIncomingParameters = CommonValidations parseResult parameters
                match validateIncomingParameters with
                | Ok _ -> 
                    let normalizedParameters = parameters |> normalizeIdsAndNames parseResult
                    let enhancedParameters = Repository.DeleteParameters(
                        OwnerId = normalizedParameters.OwnerId,
                        OwnerName = normalizedParameters.OwnerName,
                        OrganizationId = normalizedParameters.OrganizationId,
                        OrganizationName = normalizedParameters.OrganizationName,
                        RepositoryId = normalizedParameters.RepositoryId, 
                        RepositoryName = normalizedParameters.RepositoryName,
                        CorrelationId = normalizedParameters.CorrelationId,
                        Force = normalizedParameters.Force,
                        DeleteReason = normalizedParameters.DeleteReason)

                    if parseResult |> showOutput then
                        return! progress.Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                task {
                                    let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                    let! result = Repository.Delete(enhancedParameters)
                                    t0.Increment(100.0)
                                    return result
                                })
                    else
                        return! Repository.Delete(enhancedParameters)
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

    // Delete subcommand
    type UndeleteParameters() =
        inherit CommonParameters()
    let private undeleteHandler (parseResult: ParseResult) (parameters: UndeleteParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let validateIncomingParameters = CommonValidations parseResult parameters
                match validateIncomingParameters with
                | Ok _ -> 
                    let normalizedParameters = parameters |> normalizeIdsAndNames parseResult
                    let undeleteParameters = Repository.UndeleteParameters(
                        OwnerId = normalizedParameters.OwnerId,
                        OwnerName = normalizedParameters.OwnerName,
                        OrganizationId = normalizedParameters.OrganizationId,
                        OrganizationName = normalizedParameters.OrganizationName,
                        RepositoryId = normalizedParameters.RepositoryId, 
                        RepositoryName = normalizedParameters.RepositoryName,
                        CorrelationId = normalizedParameters.CorrelationId)

                    if parseResult |> showOutput then
                        return! progress.Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                task {
                                    let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                    let! result = Repository.Undelete(undeleteParameters)
                                    t0.Increment(100.0)
                                    return result
                                })
                    else
                        return! Repository.Undelete(undeleteParameters)
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

    /// Builds the Repository subcommand.
    let Build = 
        let addCommonOptionsExceptForRepositoryInfo (command: Command) =
            command |> addOption Options.ownerName
                    |> addOption Options.ownerId
                    |> addOption Options.organizationName
                    |> addOption Options.organizationId

        let addCommonOptions (command: Command) =
            command |> addOption Options.repositoryId |> addOption Options.repositoryName |> addCommonOptionsExceptForRepositoryInfo

        // Create main command and aliases, if any.
        let repositoryCommand = new Command("repository", Description = "Create, change, or delete repository-level information.")
        repositoryCommand.AddAlias("repo")

        // Add subcommands.
        let repositoryCreateCommand = new Command("create", Description = "Create a new repository.") |> addOption Options.requiredRepositoryName |> addCommonOptionsExceptForRepositoryInfo
        repositoryCreateCommand.Handler <- Create
        repositoryCommand.AddCommand(repositoryCreateCommand)

        let repositoryInitCommand = new Command("init", Description = "Initializes a new repository with the contents of a directory.") |> addOption Options.graceConfig |> addCommonOptions
        repositoryInitCommand.Handler <- Init
        repositoryCommand.AddCommand(repositoryInitCommand)

        let getBranchesCommand = new Command("get-branches", Description = "Gets a list of branches in the repository.") |> addOption Options.includeDeleted |> addCommonOptions
        getBranchesCommand.Handler <- GetBranches
        repositoryCommand.AddCommand(getBranchesCommand)

        let setVisibilityCommand = new Command("set-visibility", Description = "Set the visibility of the repository.") |> addOption Options.visibility |> addCommonOptions
        setVisibilityCommand.Handler <- SetVisibility
        repositoryCommand.AddCommand(setVisibilityCommand)

        let setStatusCommand = new Command("set-status", Description = "Set the status of the repository.") |> addOption Options.status |> addCommonOptions
        setStatusCommand.Handler <- SetStatus
        repositoryCommand.AddCommand(setStatusCommand)

        let setRecordSavesCommand = new Command("set-recordsaves", Description = "Set whether the repository defaults to recording every save.") |> addOption Options.recordSaves |> addCommonOptions
        setRecordSavesCommand.Handler <- SetRecordSaves
        repositoryCommand.AddCommand(setRecordSavesCommand)

        let setDefaultServerApiVersionCommand = new Command("set-defaultserverapiversion", Description = "Sets the default server API version for clients to use when accessing this repository.") |> addOption Options.defaultServerApiVersion |> addCommonOptions
        setDefaultServerApiVersionCommand.Handler <- SetDefaultServerApiVersion
        repositoryCommand.AddCommand(setDefaultServerApiVersionCommand)

        let setSaveDaysCommand = new Command("set-savedays", Description = "Set how long to keep saves in the repository.") |> addOption Options.saveDays |> addCommonOptions
        setSaveDaysCommand.Handler <- SetSaveDays
        repositoryCommand.AddCommand(setSaveDaysCommand)
        
        let setCheckpointDaysCommand = new Command("set-checkpointdays", Description = "Set how long to keep checkpoints in the repository.") |> addOption Options.checkpointDays |> addCommonOptions
        setCheckpointDaysCommand.Handler <- SetCheckpointDays
        repositoryCommand.AddCommand(setCheckpointDaysCommand)

        let enableSingleStepMergeCommand = new Command("enable-singlestepmerge", Description = "Enables or disables single-step merges in the repository.") |> addOption Options.enabled |> addCommonOptions
        enableSingleStepMergeCommand.Handler <- EnableSingleStepMerge
        repositoryCommand.AddCommand(enableSingleStepMergeCommand)

        let enableComplexMergeCommand = new Command("enable-complexmerge", Description = "Enables or disables complex merges in the repository.") |> addOption Options.enabled |> addCommonOptions
        enableComplexMergeCommand.Handler <- EnableComplexMerge
        repositoryCommand.AddCommand(enableComplexMergeCommand)

        let setNameCommand = new Command("set-name", Description = "Set the name of the repository.") |> addOption Options.newName |> addCommonOptions
        setNameCommand.Handler <- SetName
        repositoryCommand.AddCommand(setNameCommand)

        let deleteCommand = new Command("delete", Description = "Delete a repository.") |> addOption Options.deleteReason |> addOption Options.force |> addCommonOptions
        deleteCommand.Handler <- Delete
        repositoryCommand.AddCommand(deleteCommand)

        let undeleteCommand = new Command("undelete", Description = "Undeletes the repository.") |> addCommonOptions
        undeleteCommand.Handler <- Undelete
        repositoryCommand.AddCommand(undeleteCommand)

        repositoryCommand
