namespace Grace.CLI.Command

open FSharpPlus
open Grace.CLI.Common
open Grace.CLI.Services
open Grace.CLI.Text
open Grace.SDK
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Utilities
open Grace.Types.Owner
open Grace.Types.Branch
open Grace.Types.Organization
open Grace.Types.Reference
open Grace.Types.Repository
open Grace.Types.DirectoryVersion
open Grace.Types.Common
open Grace.Shared.Validation.Common
open Grace.Shared.Validation.Errors
open System
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.IO
open System.Threading.Tasks
open System.Threading
open System.CommandLine
open Spectre.Console
open Azure.Storage.Blobs
open Grace.CLI

/// Groups the connect command parser, handlers, and output helpers.
module Connect =

    /// Marks a configuration write failure so Connect can preserve its existing error result shape without stack output.
    exception private ConfigurationWriteFailure of exn

    /// Writes configuration data while retaining the original exception for command-level projection.
    let private saveConfigurationFileForCommand path configuration =
        try
            saveConfigFile path configuration
        with
        | ex -> raise (ConfigurationWriteFailure ex)

    /// Updates configuration data while retaining the original exception for command-level projection.
    let private updateConfigurationForCommand configuration =
        try
            updateConfiguration configuration
        with
        | ex -> raise (ConfigurationWriteFailure ex)

    /// Executes the common parameters command by binding ParseResult values to the SDK request and CLI output contract.
    type CommonParameters() =
        inherit ParameterBase()
        /// Stores a parsed command value for handler execution.
        member val public RepositoryId: string = String.Empty with get, set
        /// Stores a parsed command value for handler execution.
        member val public RepositoryName: string = String.Empty with get, set
        /// Stores a parsed command value for handler execution.
        member val public OwnerId: string = String.Empty with get, set
        /// Stores a parsed command value for handler execution.
        member val public OwnerName: string = String.Empty with get, set
        /// Stores a parsed command value for handler execution.
        member val public OrganizationId: string = String.Empty with get, set
        /// Stores a parsed command value for handler execution.
        member val public OrganizationName: string = String.Empty with get, set
        /// Stores a parsed command value for handler execution.
        member val public RetrieveDefaultBranch: bool = true with get, set

    /// Defines the options parsed by the connect command handlers.
    module private Options =
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
                Description = "The name of the repository.",
                Arity = ArgumentArity.ExactlyOne
            )

        let ownerId =
            new Option<OwnerId>(
                OptionName.OwnerId,
                Required = false,
                Description = "The repository's owner ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> OwnerId.Empty)
            )

        let ownerName =
            new Option<String>(OptionName.OwnerName, Required = false, Description = "The repository's owner name.", Arity = ArgumentArity.ExactlyOne)

        let organizationId =
            new Option<OrganizationId>(
                OptionName.OrganizationId,
                Required = false,
                Description = "The repository's organization ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> OrganizationId.Empty)
            )

        let organizationName =
            new Option<String>(
                OptionName.OrganizationName,
                Required = false,
                Description = "The repository's organization name.",
                Arity = ArgumentArity.ZeroOrOne
            )

        let correlationId =
            new Option<String>(
                OptionName.CorrelationId,
                [| "-c" |],
                Required = false,
                Description = "CorrelationId to track this command throughout Grace. [default: new Guid]",
                Arity = ArgumentArity.ExactlyOne
            )

        let serverAddress =
            new Option<String>(
                OptionName.ServerAddress,
                [| "-s" |],
                Required = false,
                Description = "Address of the Grace server to connect to.",
                Arity = ArgumentArity.ExactlyOne
            )

        let branchId =
            new Option<BranchId>(
                OptionName.BranchId,
                [| "-i" |],
                Required = false,
                Description = "The branch ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> BranchId.Empty)
            )

        let branchName =
            new Option<String>(OptionName.BranchName, [| "-b" |], Required = false, Description = "The name of the branch.", Arity = ArgumentArity.ExactlyOne)

        let referenceType =
            (new Option<String>(OptionName.ReferenceType, Required = false, Description = "The type of reference.", Arity = ArgumentArity.ExactlyOne))
                .AcceptOnlyFromAmong(listCases<ReferenceType> ())

        let referenceId =
            new Option<ReferenceId>(OptionName.ReferenceId, [||], Required = false, Description = "The reference ID <Guid>.", Arity = ArgumentArity.ExactlyOne)

        let directoryVersionId =
            new Option<DirectoryVersionId>(
                OptionName.DirectoryVersionId,
                [| "-t" |],
                Required = false,
                Description = "The directory version ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne
            )

        let force =
            new Option<bool>(
                OptionName.Force,
                [| "-f"; "--force" |],
                Required = false,
                Description = "Overwrite conflicting files when connecting.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> false)
            )

        let retrieveDefaultBranch =
            new Option<bool>(
                OptionName.RetrieveDefaultBranch,
                [||],
                Required = false,
                Description = "True to retrieve the default branch after connecting; false to connect but not download any files.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> true)
            )

    /// Groups the connect command parser, handlers, and output helpers.
    module private Arguments =
        let repositoryShortcut =
            new Argument<string>("repository", Description = "Repository shortcut in the form owner/organization/repository.", Arity = ArgumentArity.ZeroOrOne)

    /// Models directory version selection values passed between the parser and connect handlers.
    type DirectoryVersionSelection =
        | UseDirectoryVersionId of DirectoryVersionId
        | UseReferenceId of ReferenceId
        | UseReferenceType of ReferenceType
        | UseDefault

    /// Tries to map get explicit value and returns a GraceError instead of throwing on unsupported input.
    let private tryGetExplicitValue<'T> (parseResult: ParseResult) (option: Option<'T>) =
        let result = parseResult.GetResult(option)

        if isNull result || result.Implicit then
            None
        else
            Some(parseResult.GetValue(option))

    /// Tries to map get explicit non empty string and returns a GraceError instead of throwing on unsupported input.
    let private tryGetExplicitNonEmptyString (parseResult: ParseResult) (option: Option<string>) =
        match tryGetExplicitValue parseResult option with
        | Some value when not <| String.IsNullOrWhiteSpace(value) -> Some value
        | _ -> None

    /// Defines structured data exchanged by CLI helpers.
    type private RepositoryShortcut = { OwnerName: OwnerName; OrganizationName: OrganizationName; RepositoryName: RepositoryName }

    /// Validates grace name from parsed options and returns a correlated GraceError when input is invalid.
    let private validateGraceName (name: string) (error: IErrorDiscriminatedUnion) (parseResult: ParseResult) =
        if Constants.GraceNameRegex.IsMatch(name) then
            Ok name
        else
            Error(GraceError.Create (getErrorMessage error) (getCorrelationId parseResult))

    /// Tries to map get repository shortcut and returns a GraceError instead of throwing on unsupported input.
    let private tryGetRepositoryShortcut (parseResult: ParseResult) =
        let result = parseResult.GetResult(Arguments.repositoryShortcut)

        if isNull result || result.Implicit then
            Ok None
        else
            let value = parseResult.GetValue(Arguments.repositoryShortcut)

            if String.IsNullOrWhiteSpace(value) then
                Error(GraceError.Create "Repository shortcut must be in the form owner/organization/repository." (getCorrelationId parseResult))
            else
                let parts =
                    value
                        .Trim()
                        .Split('/', StringSplitOptions.RemoveEmptyEntries)

                if parts.Length <> 3 then
                    Error(GraceError.Create "Repository shortcut must be in the form owner/organization/repository." (getCorrelationId parseResult))
                else
                    let ownerName = parts[ 0 ].Trim()
                    let organizationName = parts[ 1 ].Trim()
                    let repositoryName = parts[ 2 ].Trim()

                    match validateGraceName ownerName OwnerError.InvalidOwnerName parseResult with
                    | Error error -> Error error
                    | Ok ownerName ->
                        match validateGraceName organizationName OrganizationError.InvalidOrganizationName parseResult with
                        | Error error -> Error error
                        | Ok organizationName ->
                            match validateGraceName repositoryName RepositoryError.InvalidRepositoryName parseResult with
                            | Error error -> Error error
                            | Ok repositoryName -> Ok(Some { OwnerName = ownerName; OrganizationName = organizationName; RepositoryName = repositoryName })

    /// Evaluates has explicit owner against parsed options and command state.
    let private hasExplicitOwner (parseResult: ParseResult) =
        tryGetExplicitValue parseResult Options.ownerId
        |> Option.exists (fun ownerId -> ownerId <> Guid.Empty)
        || (tryGetExplicitNonEmptyString parseResult Options.ownerName
            |> Option.isSome)

    /// Evaluates has explicit organization against parsed options and command state.
    let private hasExplicitOrganization (parseResult: ParseResult) =
        tryGetExplicitValue parseResult Options.organizationId
        |> Option.exists (fun organizationId -> organizationId <> Guid.Empty)
        || (tryGetExplicitNonEmptyString parseResult Options.organizationName
            |> Option.isSome)

    /// Evaluates has explicit repository against parsed options and command state.
    let private hasExplicitRepository (parseResult: ParseResult) =
        tryGetExplicitValue parseResult Options.repositoryId
        |> Option.exists (fun repositoryId -> repositoryId <> Guid.Empty)
        || (tryGetExplicitNonEmptyString parseResult Options.repositoryName
            |> Option.isSome)

    /// Updates CLI authentication state for apply repository shortcut while keeping token handling centralized.
    let internal applyRepositoryShortcut (parseResult: ParseResult) (graceIds: GraceIds) =
        match tryGetRepositoryShortcut parseResult with
        | Error error -> Error error
        | Ok None -> Ok graceIds
        | Ok (Some shortcut) ->
            if hasExplicitOwner parseResult
               || hasExplicitOrganization parseResult
               || hasExplicitRepository parseResult then
                Error(
                    GraceError.Create
                        "Provide either the repository shortcut or the owner/organization/repository options, not both."
                        (getCorrelationId parseResult)
                )
            else
                Ok
                    { graceIds with
                        OwnerId = Guid.Empty
                        OwnerIdString = String.Empty
                        OwnerName = shortcut.OwnerName
                        OrganizationId = Guid.Empty
                        OrganizationIdString = String.Empty
                        OrganizationName = shortcut.OrganizationName
                        RepositoryId = Guid.Empty
                        RepositoryIdString = String.Empty
                        RepositoryName = shortcut.RepositoryName
                        HasOwner = true
                        HasOrganization = true
                        HasRepository = true
                    }

    /// Reads directory version selection from ParseResult, local configuration, or Grace ids.
    let internal getDirectoryVersionSelection (parseResult: ParseResult) =
        match tryGetExplicitValue parseResult Options.directoryVersionId with
        | Some directoryVersionId when directoryVersionId <> Guid.Empty -> UseDirectoryVersionId directoryVersionId
        | _ ->
            match tryGetExplicitValue parseResult Options.referenceId with
            | Some referenceId when referenceId <> Guid.Empty -> UseReferenceId referenceId
            | _ ->
                match tryGetExplicitNonEmptyString parseResult Options.referenceType with
                | Some referenceTypeRaw ->
                    let referenceType =
                        discriminatedUnionFromString<ReferenceType>(
                            referenceTypeRaw
                        )
                            .Value

                    UseReferenceType referenceType
                | None -> UseDefault

    /// Tries to map get directory id from branch and returns a GraceError instead of throwing on unsupported input.
    let internal tryGetDirectoryIdFromBranch (referenceType: ReferenceType) (branchDto: BranchDto) =
        match referenceType with
        | ReferenceType.Promotion when
            branchDto.LatestPromotion.ReferenceId
            <> ReferenceId.Empty
            ->
            Some branchDto.LatestPromotion.DirectoryId
        | ReferenceType.Commit when
            branchDto.LatestCommit.ReferenceId
            <> ReferenceId.Empty
            ->
            Some branchDto.LatestCommit.DirectoryId
        | ReferenceType.Checkpoint when
            branchDto.LatestCheckpoint.ReferenceId
            <> ReferenceId.Empty
            ->
            Some branchDto.LatestCheckpoint.DirectoryId
        | ReferenceType.Save when
            branchDto.LatestSave.ReferenceId
            <> ReferenceId.Empty
            ->
            Some branchDto.LatestSave.DirectoryId
        | _ -> None

    /// Resolves default directory version id from command options, configuration, or local state.
    let internal resolveDefaultDirectoryVersionId (branchDto: BranchDto) =
        if branchDto.LatestPromotion.DirectoryId
           <> Guid.Empty then
            Some branchDto.LatestPromotion.DirectoryId
        elif branchDto.BasedOn.DirectoryId <> Guid.Empty then
            Some branchDto.BasedOn.DirectoryId
        else
            None

    /// Coordinates select latest reference behavior for this CLI command path.
    let private selectLatestReference (references: ReferenceDto seq) =
        references
        |> Seq.sortByDescending (fun reference ->
            reference.UpdatedAt
            |> Option.defaultValue reference.CreatedAt)
        |> Seq.tryHead

    /// Resolves directory version id from reference type from command options, configuration, or local state.
    let private resolveDirectoryVersionIdFromReferenceType
        (graceIds: GraceIds)
        (ownerDto: OwnerDto)
        (organizationDto: OrganizationDto)
        (repositoryDto: RepositoryDto)
        (branchDto: BranchDto)
        (referenceType: ReferenceType)
        =
        task {
            match tryGetDirectoryIdFromBranch referenceType branchDto with
            | Some directoryId -> return Ok directoryId
            | None ->
                let getReferencesParameters =
                    Parameters.Branch.GetReferencesParameters(
                        OwnerId = $"{ownerDto.OwnerId}",
                        OwnerName = ownerDto.OwnerName,
                        OrganizationId = $"{organizationDto.OrganizationId}",
                        OrganizationName = organizationDto.OrganizationName,
                        RepositoryId = $"{repositoryDto.RepositoryId}",
                        RepositoryName = repositoryDto.RepositoryName,
                        BranchId = $"{branchDto.BranchId}",
                        BranchName = branchDto.BranchName,
                        MaxCount = 50,
                        CorrelationId = graceIds.CorrelationId
                    )

                let referencesTask =
                    match referenceType with
                    | ReferenceType.Tag -> Branch.GetTags(getReferencesParameters)
                    | ReferenceType.External -> Branch.GetExternals(getReferencesParameters)
                    | ReferenceType.Rebase -> Branch.GetRebases(getReferencesParameters)
                    | _ -> Task.FromResult(Ok(GraceReturnValue.Create [||] graceIds.CorrelationId))

                let! referencesResult = referencesTask

                match referencesResult with
                | Ok returnValue ->
                    match selectLatestReference returnValue.ReturnValue with
                    | Some reference -> return Ok reference.DirectoryId
                    | None ->
                        return Error(GraceError.Create $"No {referenceType} references were found for branch {branchDto.BranchName}." graceIds.CorrelationId)
                | Error error -> return Error error
        }

    /// Resolves target directory version id from command options, configuration, or local state.
    let private resolveTargetDirectoryVersionId
        (parseResult: ParseResult)
        (graceIds: GraceIds)
        (ownerDto: OwnerDto)
        (organizationDto: OrganizationDto)
        (repositoryDto: RepositoryDto)
        (branchDto: BranchDto)
        =
        task {
            match getDirectoryVersionSelection parseResult with
            | UseDirectoryVersionId directoryVersionId -> return Ok directoryVersionId
            | UseReferenceId referenceId ->
                let getReferenceParameters =
                    Parameters.Branch.GetReferenceParameters(
                        OwnerId = $"{ownerDto.OwnerId}",
                        OwnerName = ownerDto.OwnerName,
                        OrganizationId = $"{organizationDto.OrganizationId}",
                        OrganizationName = organizationDto.OrganizationName,
                        RepositoryId = $"{repositoryDto.RepositoryId}",
                        RepositoryName = repositoryDto.RepositoryName,
                        BranchId = $"{branchDto.BranchId}",
                        BranchName = branchDto.BranchName,
                        ReferenceId = $"{referenceId}",
                        CorrelationId = graceIds.CorrelationId
                    )

                let! referenceResult = Branch.GetReference(getReferenceParameters)

                return
                    match referenceResult with
                    | Ok returnValue -> Ok returnValue.ReturnValue.DirectoryId
                    | Error error -> Error error
            | UseReferenceType referenceType ->
                return! resolveDirectoryVersionIdFromReferenceType graceIds ownerDto organizationDto repositoryDto branchDto referenceType
            | UseDefault ->
                match resolveDefaultDirectoryVersionId branchDto with
                | Some directoryVersionId -> return Ok directoryVersionId
                | None -> return Error(GraceError.Create "No downloadable version found for this branch." graceIds.CorrelationId)
        }

    /// Resolves one server-ordered boundary together with the exact root selected for materialization.
    let private resolveTargetMaterializationBoundary
        (parseResult: ParseResult)
        (graceIds: GraceIds)
        (ownerDto: OwnerDto)
        (organizationDto: OrganizationDto)
        (repositoryDto: RepositoryDto)
        (branchDto: BranchDto)
        =
        task {
            let parameters =
                Parameters.Branch.GetReferenceMaterializationBoundaryParameters(
                    OwnerId = $"{ownerDto.OwnerId}",
                    OwnerName = ownerDto.OwnerName,
                    OrganizationId = $"{organizationDto.OrganizationId}",
                    OrganizationName = organizationDto.OrganizationName,
                    RepositoryId = $"{repositoryDto.RepositoryId}",
                    RepositoryName = repositoryDto.RepositoryName,
                    BranchId = $"{branchDto.BranchId}",
                    BranchName = branchDto.BranchName,
                    CorrelationId = graceIds.CorrelationId
                )

            match getDirectoryVersionSelection parseResult with
            | UseDirectoryVersionId directoryVersionId -> parameters.DirectoryVersionId <- directoryVersionId
            | UseReferenceId referenceId -> parameters.ReferenceId <- $"{referenceId}"
            | UseReferenceType referenceType -> parameters.ReferenceType <- getDiscriminatedUnionCaseName referenceType
            | UseDefault -> ()

            match! Branch.GetReferenceMaterializationBoundary parameters with
            | Error error -> return Error error
            | Ok returnValue ->
                let boundary = returnValue.ReturnValue

                if boundary.RepositoryId
                   <> repositoryDto.RepositoryId
                   || boundary.BranchId <> branchDto.BranchId
                   || boundary.DirectoryId = DirectoryVersionId.Empty
                   || String.IsNullOrWhiteSpace(string boundary.Sha256Hash)
                   || String.IsNullOrWhiteSpace(string boundary.Blake3Hash)
                   || String.IsNullOrWhiteSpace boundary.EventCursor then
                    return Error(GraceError.Create "The server returned an invalid materialization boundary." graceIds.CorrelationId)
                else
                    return Ok boundary
        }

    /// Ensures required command context is present.
    let private ensureConfigurationFileExists () =
        if not <| configurationFileExists () then
            let graceDirPath = Path.Combine(Environment.CurrentDirectory, Constants.GraceConfigDirectory)
            let graceConfigPath = Path.Combine(graceDirPath, Constants.GraceConfigFileName)
            Directory.CreateDirectory(graceDirPath) |> ignore

            if not <| File.Exists(graceConfigPath) then
                GraceConfiguration()
                |> saveConfigurationFileForCommand graceConfigPath

    /// Reads reload configuration data needed by the command workflow without changing remote state.
    let private reloadConfiguration () =
        resetConfiguration ()
        Current() |> ignore

    /// Updates CLI authentication state for apply server address override while keeping token handling centralized.
    let private applyServerAddressOverride (parseResult: ParseResult) =
        match tryGetExplicitNonEmptyString parseResult Options.serverAddress with
        | Some serverAddress ->
            let newConfig = Current()
            newConfig.ServerUri <- serverAddress
            updateConfigurationForCommand newConfig
            reloadConfiguration ()
        | None -> ()

    /// Validates required ids from parsed options and returns a correlated GraceError when input is invalid.
    let private validateRequiredIds (parseResult: ParseResult) (graceIds: GraceIds) =
        let correlationId = getCorrelationId parseResult

        let ownerValid =
            graceIds.OwnerId <> Guid.Empty
            || not
               <| String.IsNullOrWhiteSpace(graceIds.OwnerName)

        let organizationValid =
            graceIds.OrganizationId <> Guid.Empty
            || not
               <| String.IsNullOrWhiteSpace(graceIds.OrganizationName)

        let repositoryValid =
            graceIds.RepositoryId <> Guid.Empty
            || not
               <| String.IsNullOrWhiteSpace(graceIds.RepositoryName)

        if not ownerValid then
            Error(GraceError.Create (getErrorMessage OwnerError.EitherOwnerIdOrOwnerNameRequired) correlationId)
        elif not organizationValid then
            Error(GraceError.Create (getErrorMessage OrganizationError.EitherOrganizationIdOrOrganizationNameRequired) correlationId)
        elif not repositoryValid then
            Error(GraceError.Create (getErrorMessage RepositoryError.EitherRepositoryIdOrRepositoryNameRequired) correlationId)
        else
            Ok()

    /// Reads owner organization repository from ParseResult, local configuration, or Grace ids.
    let private getOwnerOrganizationRepository (graceIds: GraceIds) =
        task {
            let ownerParameters =
                Parameters.Owner.GetOwnerParameters(OwnerId = graceIds.OwnerIdString, OwnerName = graceIds.OwnerName, CorrelationId = graceIds.CorrelationId)

            let! ownerResult = Grace.SDK.Owner.Get(ownerParameters)

            let organizationParameters =
                Parameters.Organization.GetOrganizationParameters(
                    OwnerId = graceIds.OwnerIdString,
                    OwnerName = graceIds.OwnerName,
                    OrganizationId = graceIds.OrganizationIdString,
                    OrganizationName = graceIds.OrganizationName,
                    CorrelationId = graceIds.CorrelationId
                )

            let! organizationResult = Organization.Get(organizationParameters)

            let repositoryParameters =
                Parameters.Repository.GetRepositoryParameters(
                    OwnerId = graceIds.OwnerIdString,
                    OwnerName = graceIds.OwnerName,
                    OrganizationId = graceIds.OrganizationIdString,
                    OrganizationName = graceIds.OrganizationName,
                    RepositoryId = graceIds.RepositoryIdString,
                    RepositoryName = graceIds.RepositoryName,
                    CorrelationId = graceIds.CorrelationId
                )

            let! repositoryResult = Repository.Get(repositoryParameters)

            match (ownerResult, organizationResult, repositoryResult) with
            | (Ok owner, Ok organization, Ok repository) -> return Ok(owner.ReturnValue, organization.ReturnValue, repository.ReturnValue)
            | (Error error, _, _) -> return Error error
            | (_, Error error, _) -> return Error error
            | (_, _, Error error) -> return Error error
        }

    /// Reads branch for connect from ParseResult, local configuration, or Grace ids.
    let private getBranchForConnect
        (parseResult: ParseResult)
        (graceIds: GraceIds)
        (ownerDto: OwnerDto)
        (organizationDto: OrganizationDto)
        (repositoryDto: RepositoryDto)
        =
        task {
            let branchId =
                tryGetExplicitValue parseResult Options.branchId
                |> Option.filter (fun value -> value <> Guid.Empty)

            let branchName = tryGetExplicitNonEmptyString parseResult Options.branchName

            let branchParameters =
                match branchId, branchName with
                | Some id, _ ->
                    Parameters.Branch.GetBranchParameters(
                        OwnerId = $"{ownerDto.OwnerId}",
                        OrganizationId = $"{organizationDto.OrganizationId}",
                        RepositoryId = $"{repositoryDto.RepositoryId}",
                        BranchId = $"{id}",
                        CorrelationId = graceIds.CorrelationId
                    )
                | None, Some name ->
                    Parameters.Branch.GetBranchParameters(
                        OwnerId = $"{ownerDto.OwnerId}",
                        OrganizationId = $"{organizationDto.OrganizationId}",
                        RepositoryId = $"{repositoryDto.RepositoryId}",
                        BranchName = name,
                        CorrelationId = graceIds.CorrelationId
                    )
                | None, None ->
                    Parameters.Branch.GetBranchParameters(
                        OwnerId = $"{ownerDto.OwnerId}",
                        OrganizationId = $"{organizationDto.OrganizationId}",
                        RepositoryId = $"{repositoryDto.RepositoryId}",
                        BranchName = $"{repositoryDto.DefaultBranchName}",
                        CorrelationId = graceIds.CorrelationId
                    )

            let! branchResult = Branch.Get(branchParameters)

            return
                match branchResult with
                | Ok graceReturnValue -> Ok graceReturnValue.ReturnValue
                | Error error -> Error error
        }

    /// Writes human line data through the CLI output contract.
    let private writeHumanLine (parseResult: ParseResult) text =
        if
            not (parseResult |> json)
            && not (parseResult |> silent)
        then
            AnsiConsole.MarkupLine text

    /// Converts command data into the required shape.
    let private toConnectDto
        (ownerDto: OwnerDto)
        (organizationDto: OrganizationDto)
        (repositoryDto: RepositoryDto)
        (branchDto: BranchDto)
        (retrievedDefaultBranch: bool)
        : LocalOutputDto.ConnectDto
        =
        {
            OwnerId = ownerDto.OwnerId
            OwnerName = ownerDto.OwnerName
            OrganizationId = organizationDto.OrganizationId
            OrganizationName = organizationDto.OrganizationName
            RepositoryId = repositoryDto.RepositoryId
            RepositoryName = repositoryDto.RepositoryName
            BranchId = branchDto.BranchId
            BranchName = branchDto.BranchName
            DefaultBranchName = repositoryDto.DefaultBranchName
            RetrievedDefaultBranch = retrievedDefaultBranch
        }

    /// Names the Connect error property that reports the already-persisted configuration outcome.
    let internal configurationOutcomeProperty = "Connect.ConfigurationOutcome"

    /// Names the Connect error property that reports the optional working-directory update outcome.
    let internal updateOutcomeProperty = "Connect.UpdateOutcome"

    /// Renders a failed optional update while preserving the successful configuration result in human and JSON output.
    let internal renderConfiguredUpdateFailure (parseResult: ParseResult) (configuration: LocalOutputDto.ConnectDto) updateOutcome (error: GraceError) =
        writeHumanLine parseResult $"[{Colors.Important}]Grace repository configuration remains saved.[/]"

        error.enhance (configurationOutcomeProperty, "Configured")
        |> ignore

        error.enhance ("Connect.RepositoryId", configuration.RepositoryId)
        |> ignore

        error.enhance ("Connect.BranchId", configuration.BranchId)
        |> ignore

        error.enhance (updateOutcomeProperty, updateOutcome)
        |> ignore

        Error error |> renderOutput parseResult

    /// Renders a successful optional update with configuration and update outcomes as separate envelope facts.
    let internal renderConfiguredUpdateSuccess (parseResult: ParseResult) (configuration: LocalOutputDto.ConnectDto) updateOutcome correlationId =
        let result = GraceReturnValue.Create { configuration with RetrievedDefaultBranch = true } correlationId

        result.enhance (configurationOutcomeProperty, "Configured")
        |> ignore

        result.enhance ("Connect.RepositoryId", configuration.RepositoryId)
        |> ignore

        result.enhance ("Connect.BranchId", configuration.BranchId)
        |> ignore

        result.enhance (updateOutcomeProperty, updateOutcome)
        |> ignore

        Ok result |> renderOutput parseResult

    /// Builds the exact target status selected by the server without retaining predecessor descendants.
    let private createTargetStatus
        (previousStatus: GraceStatus)
        (boundary: ReferenceMaterializationBoundaryDto)
        (directoryVersionDtos: DirectoryVersionDto seq)
        =
        let targetIndex = GraceIndex()
        let mutable duplicateDirectoryId = None

        for directoryVersionDto in directoryVersionDtos do
            let directory = directoryVersionDto.DirectoryVersion.ToLocalDirectoryVersion DateTime.UtcNow

            if not (targetIndex.TryAdd(directory.DirectoryVersionId, directory)) then
                duplicateDirectoryId <- Some directory.DirectoryVersionId

        match duplicateDirectoryId with
        | Some directoryVersionId -> Error $"The server-selected DirectoryVersion graph repeats '{directoryVersionId}'."
        | None ->
            match targetIndex.TryGetValue(boundary.DirectoryId) with
            | false, _ -> Error "The server-selected DirectoryVersion graph does not contain its selected root."
            | true, rootDirectory when
                rootDirectory.RelativePath
                <> Constants.RootDirectoryPath
                || rootDirectory.RepositoryId
                   <> boundary.RepositoryId
                || rootDirectory.Sha256Hash <> boundary.Sha256Hash
                || rootDirectory.Blake3Hash <> boundary.Blake3Hash
                ->
                Error "The server-selected DirectoryVersion root does not match its materialization boundary."
            | true, rootDirectory ->
                if targetIndex.Values
                   |> Seq.exists (fun directory -> directory.RepositoryId <> boundary.RepositoryId) then
                    Error "The server-selected DirectoryVersion graph contains a directory from another repository."
                else
                    let targetStatus =
                        {
                            Index = targetIndex
                            RootDirectoryId = rootDirectory.DirectoryVersionId
                            RootDirectorySha256Hash = rootDirectory.Sha256Hash
                            RootDirectoryBlake3Hash = rootDirectory.Blake3Hash
                            LastSuccessfulDirectoryVersionUpload = previousStatus.LastSuccessfulDirectoryVersionUpload
                            LastSuccessfulFileUpload = previousStatus.LastSuccessfulFileUpload
                        }

                    match LocalStateDb.validateCompleteStatusTree targetStatus with
                    | Error error -> Error $"The server-selected DirectoryVersion graph is incomplete: {error}"
                    | Ok () -> Ok targetStatus

    /// Creates the immutable prepared-content declaration for one exact Connect target status.
    let private createPreparedManifest (targetStatus: GraceStatus) =
        targetStatus.Index.Values
        |> Seq.collect (fun directory ->
            seq {
                if directory.DirectoryVersionId
                   <> targetStatus.RootDirectoryId then
                    yield WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory directory.RelativePath

                for file in directory.Files do
                    yield WorkingDirectoryUpdateContracts.PreparedManifestEntry.File(file.RelativePath, file.Sha256Hash, file.Blake3Hash)
            })
        |> WorkingDirectoryUpdateContracts.PreparedManifest.create

    /// Projects the shared update outcome without mixing it with Connect configuration reporting.
    let private renderWorkingDirectoryOutcome parseResult configuration correlationId outcome =
        match outcome with
        | WorkingDirectoryUpdateContracts.Outcome.Updated _ ->
            writeHumanLine parseResult $"[{Colors.Important}]Updated the working directory to the selected root.[/]"
            renderConfiguredUpdateSuccess parseResult configuration "Updated" correlationId
        | WorkingDirectoryUpdateContracts.Outcome.Unchanged _ ->
            writeHumanLine parseResult $"[{Colors.Deemphasized}]The working directory already matches the selected root.[/]"
            renderConfiguredUpdateSuccess parseResult configuration "Unchanged" correlationId
        | WorkingDirectoryUpdateContracts.Outcome.Rejected failure ->
            GraceError.Create (WorkingDirectoryUpdateContracts.Failure.reason failure) correlationId
            |> renderConfiguredUpdateFailure parseResult configuration "Rejected"
        | WorkingDirectoryUpdateContracts.Outcome.UpdateIncomplete failure ->
            GraceError.Create (WorkingDirectoryUpdateContracts.Failure.reason failure) correlationId
            |> renderConfiguredUpdateFailure parseResult configuration "UpdateIncomplete"
        | WorkingDirectoryUpdateContracts.Outcome.FinalizationIncomplete (_, failure) ->
            GraceError.Create (WorkingDirectoryUpdateContracts.Failure.reason failure) correlationId
            |> renderConfiguredUpdateFailure parseResult configuration "FinalizationIncomplete"

    /// Identifies the one selected ZIP stream source before shared staging and WDU execution.
    type internal SelectedZipSource =
        | DirectZip of UriWithSharedAccessSignature
        | CacheZip of Uri

    /// Selects the Cache source without evaluating the unchanged Direct ZIP lookup.
    let internal selectZipSourceWith getDirectZip retrieval =
        match retrieval with
        | ConnectCache.Direct ->
            task {
                let! result = getDirectZip ()
                return result |> Result.map DirectZip
            }
        | ConnectCache.Required cacheUri -> Task.FromResult(Ok(CacheZip cacheUri))

    /// Supplies the stream, staging, and prepared-content application seams for one selected Connect ZIP source.
    type internal ZipApplicationDependencies =
        {
            OpenDirectZip: UriWithSharedAccessSignature -> CancellationToken -> Task<Stream>
            UseCacheZip: Uri
                -> string
                -> string
                -> string
                -> CancellationToken
                -> (Stream -> Task<Result<WorkingDirectoryUpdateContracts.Outcome, GraceError>>)
                -> Task<Result<Result<WorkingDirectoryUpdateContracts.Outcome, GraceError>, GraceError>>
            StageZip: Stream -> CancellationToken -> Task<Result<WorkingDirectoryUpdateContracts.PreparedContent, string>>
            ApplyPreparedContent: WorkingDirectoryUpdateContracts.PreparedContent
                -> string
                -> bool
                -> string
                -> CancellationToken
                -> Task<Result<WorkingDirectoryUpdateContracts.Outcome, GraceError>>
        }

    /// Routes one selected ZIP source through exact staging and one prepared-content application.
    let internal applySelectedZipWith
        (dependencies: ZipApplicationDependencies)
        zipSource
        repositoryId
        directoryVersionId
        eventCursor
        force
        correlationId
        cancellationToken
        =
        task {
            let stageAndApply zipFile =
                task {
                    match! dependencies.StageZip zipFile cancellationToken with
                    | Error error -> return Error(GraceError.Create error correlationId)
                    | Ok preparedContent -> return! dependencies.ApplyPreparedContent preparedContent eventCursor force correlationId cancellationToken
                }

            match zipSource with
            | DirectZip sourceUri ->
                let! zipFile = dependencies.OpenDirectZip sourceUri cancellationToken
                return! stageAndApply zipFile
            | CacheZip cacheUri ->
                let! result = dependencies.UseCacheZip cacheUri repositoryId directoryVersionId correlationId cancellationToken stageAndApply

                match result with
                | Error error -> return Error error
                | Ok applicationResult -> return applicationResult
        }

    /// Coordinates retrieve default branch and write behavior for this CLI command path.
    let private retrieveDefaultBranchAndWrite
        (parseResult: ParseResult)
        (retrieval: ConnectCache.Retrieval)
        (graceIds: GraceIds)
        (ownerDto: OwnerDto)
        (organizationDto: OrganizationDto)
        (repositoryDto: RepositoryDto)
        (branchDto: BranchDto)
        (configurationOutput: LocalOutputDto.ConnectDto)
        (cancellationToken: CancellationToken)
        =
        task {
            cancellationToken.ThrowIfCancellationRequested()
            let! boundaryResult = resolveTargetMaterializationBoundary parseResult graceIds ownerDto organizationDto repositoryDto branchDto

            match boundaryResult with
            | Error error -> return renderConfiguredUpdateFailure parseResult configurationOutput "RetrievalFailed" error
            | Ok boundary ->
                let directoryVersionId = boundary.DirectoryId

                let getDirectoryContentsParameters =
                    Parameters.DirectoryVersion.GetParameters(
                        OwnerId = $"{ownerDto.OwnerId}",
                        OrganizationId = $"{organizationDto.OrganizationId}",
                        RepositoryId = $"{repositoryDto.RepositoryId}",
                        DirectoryVersionId = $"{directoryVersionId}",
                        CorrelationId = graceIds.CorrelationId
                    )

                writeHumanLine parseResult $"[{Colors.Important}]Retrieving all DirectoryVersions.[/]"

                let! directoryVersionsResult = DirectoryVersion.GetDirectoryVersionsRecursive(getDirectoryContentsParameters)

                let! zipSourceResult =
                    selectZipSourceWith
                        (fun () ->
                            task {
                                let getZipFileParameters =
                                    Parameters.DirectoryVersion.GetZipFileParameters(
                                        OwnerId = $"{ownerDto.OwnerId}",
                                        OrganizationId = $"{organizationDto.OrganizationId}",
                                        RepositoryId = $"{repositoryDto.RepositoryId}",
                                        DirectoryVersionId = $"{directoryVersionId}",
                                        CorrelationId = graceIds.CorrelationId
                                    )

                                writeHumanLine parseResult $"[{Colors.Important}]Retrieving zip file download uri.[/]"
                                let! getZipFileResult = DirectoryVersion.GetZipFile(getZipFileParameters)
                                writeHumanLine parseResult $"[{Colors.Important}]Finished getting zip file download uri.[/]"

                                return
                                    getZipFileResult
                                    |> Result.map (fun returnValue -> returnValue.ReturnValue)
                            })
                        retrieval

                match (directoryVersionsResult, zipSourceResult) with
                | (Ok directoryVerionsReturnValue, Ok zipSource) ->
                    writeHumanLine parseResult $"[{Colors.Important}]Retrieved all DirectoryVersions.[/]"

                    let directoryVersionDtos = directoryVerionsReturnValue.ReturnValue
                    let! currentStatus = readGraceStatusFile ()

                    match createTargetStatus currentStatus boundary directoryVersionDtos with
                    | Error error ->
                        return
                            GraceError.Create error graceIds.CorrelationId
                            |> renderConfiguredUpdateFailure parseResult configurationOutput "RetrievalFailed"
                    | Ok targetStatus ->
                        match createPreparedManifest targetStatus with
                        | Error error ->
                            return
                                GraceError.Create error graceIds.CorrelationId
                                |> renderConfiguredUpdateFailure parseResult configurationOutput "RetrievalFailed"
                        | Ok manifest ->
                            writeHumanLine parseResult $"[{Colors.Important}]Downloading and validating the selected zip file.[/]"

                            let dependencies: ZipApplicationDependencies =
                                {
                                    OpenDirectZip =
                                        fun sourceUri cancellationToken ->
                                            let blobClient = BlobClient(sourceUri)
                                            blobClient.OpenReadAsync(bufferSize = 64 * 1024, cancellationToken = cancellationToken)
                                    UseCacheZip = ConnectCache.useVerifiedZip
                                    StageZip = fun zipFile cancellationToken -> ConnectZipStaging.prepare manifest zipFile cancellationToken
                                    ApplyPreparedContent =
                                        fun preparedContent eventCursor force correlationId cancellationToken ->
                                            task {
                                                match
                                                    WorkingDirectoryUpdateContracts.Target.create
                                                        boundary.RepositoryId
                                                        boundary.BranchId
                                                        boundary.DirectoryId
                                                        boundary.Sha256Hash
                                                        boundary.Blake3Hash
                                                    with
                                                | Error error ->
                                                    WorkingDirectoryUpdateContracts.PreparedContent.dispose preparedContent
                                                    return Error(GraceError.Create error correlationId)
                                                | Ok target ->
                                                    let! outcome =
                                                        WorkingDirectoryUpdate.Connect.run
                                                            target
                                                            currentStatus
                                                            targetStatus
                                                            preparedContent
                                                            eventCursor
                                                            force
                                                            correlationId
                                                            cancellationToken
                                                            WorkingDirectoryUpdate.Connect.none

                                                    return Ok outcome
                                            }
                                }

                            let! outcomeResult =
                                applySelectedZipWith
                                    dependencies
                                    zipSource
                                    (string boundary.RepositoryId)
                                    (string boundary.DirectoryId)
                                    boundary.EventCursor
                                    (parseResult.GetValue(Options.force))
                                    graceIds.CorrelationId
                                    cancellationToken

                            match outcomeResult with
                            | Error error -> return renderConfiguredUpdateFailure parseResult configurationOutput "RetrievalFailed" error
                            | Ok outcome -> return renderWorkingDirectoryOutcome parseResult configurationOutput graceIds.CorrelationId outcome
                | (Error error, _) -> return renderConfiguredUpdateFailure parseResult configurationOutput "RetrievalFailed" error
                | (_, Error error) -> return renderConfiguredUpdateFailure parseResult configurationOutput "RetrievalFailed" error
        }

    /// Runs the materialization branch only when Connect explicitly requested retrieval.
    let internal retrieveWhenRequested shouldRetrieve retrieve =
        task {
            if shouldRetrieve then
                let! exitCode = retrieve ()
                return Some exitCode
            else
                return None
        }

    /// Keeps post-configuration retrieval exceptions inside the separated Connect update result.
    let private retrieveAfterConfiguration parseResult configurationOutput correlationId retrieve =
        task {
            try
                return! retrieve ()
            with
            | :? OperationCanceledException ->
                return
                    GraceError.Create "Connect retrieval was cancelled." correlationId
                    |> renderConfiguredUpdateFailure parseResult configurationOutput "RetrievalFailed"
            | ex ->
                return
                    GraceError.Create $"{ExceptionResponse.Create ex}" correlationId
                    |> renderConfiguredUpdateFailure parseResult configurationOutput "RetrievalFailed"
        }

    /// Routes one selected retrieval mode through validation, configuration, and the existing Connect workflow.
    let private connectSelectedImpl retrieval (parseResult: ParseResult) (cancellationToken: CancellationToken) : Task<int> =
        task {
            if parseResult |> verbose then printParseResult parseResult
            ensureConfigurationFileExists ()
            reloadConfiguration ()
            applyServerAddressOverride parseResult
            let validateIncomingParameters = Validations.CommonValidations parseResult

            match validateIncomingParameters with
            | Error error -> return (Error error |> renderOutput parseResult)
            | Ok _ ->
                let graceIds = getNormalizedIdsAndNames parseResult

                match applyRepositoryShortcut parseResult graceIds with
                | Error error -> return (Error error |> renderOutput parseResult)
                | Ok graceIds ->
                    match validateRequiredIds parseResult graceIds with
                    | Error error -> return (Error error |> renderOutput parseResult)
                    | Ok () ->
                        do! Auth.ensureAccessToken parseResult

                        let! ownerOrgRepoResult = getOwnerOrganizationRepository graceIds

                        match ownerOrgRepoResult with
                        | Ok (ownerDto, organizationDto, repositoryDto) ->
                            writeHumanLine parseResult $"[{Colors.Important}]Found owner, organization, and repository.[/]"

                            let! branchResult = getBranchForConnect parseResult graceIds ownerDto organizationDto repositoryDto

                            match branchResult with
                            | Ok branchDto ->
                                writeHumanLine parseResult $"[{Colors.Important}]Retrieved branch {branchDto.BranchName}.[/]"
                                // Write the new configuration to the config file.
                                let newConfig = Current()
                                newConfig.OwnerId <- ownerDto.OwnerId
                                newConfig.OwnerName <- ownerDto.OwnerName
                                newConfig.OrganizationId <- organizationDto.OrganizationId
                                newConfig.OrganizationName <- organizationDto.OrganizationName
                                newConfig.RepositoryId <- repositoryDto.RepositoryId
                                newConfig.RepositoryName <- repositoryDto.RepositoryName
                                newConfig.BranchId <- branchDto.BranchId
                                newConfig.BranchName <- branchDto.BranchName
                                newConfig.DefaultBranchName <- repositoryDto.DefaultBranchName
                                newConfig.ObjectStorageProvider <- repositoryDto.ObjectStorageProvider
                                updateConfigurationForCommand newConfig
                                reloadConfiguration ()
                                writeHumanLine parseResult $"[{Colors.Important}]Wrote new Grace configuration file.[/]"

                                let retrieveDefaultBranch = parseResult.GetValue(Options.retrieveDefaultBranch)
                                let configurationOutput = toConnectDto ownerDto organizationDto repositoryDto branchDto false

                                let! retrieveExitCode =
                                    retrieveWhenRequested retrieveDefaultBranch (fun () ->
                                        retrieveAfterConfiguration parseResult configurationOutput graceIds.CorrelationId (fun () ->
                                            retrieveDefaultBranchAndWrite
                                                parseResult
                                                retrieval
                                                graceIds
                                                ownerDto
                                                organizationDto
                                                repositoryDto
                                                branchDto
                                                configurationOutput
                                                cancellationToken))

                                match retrieveExitCode with
                                | Some exitCode -> return exitCode
                                | None ->
                                    return
                                        GraceReturnValue.Create configurationOutput (getCorrelationId parseResult)
                                        |> Ok
                                        |> renderOutput parseResult
                            | Error error -> return (Error error |> renderOutput parseResult)
                        | Error error -> return (Error error |> renderOutput parseResult)
        }

    /// Rejects invalid Cache selection before Connect creates or changes repository-local configuration.
    let private connectImpl (parseResult: ParseResult) (cancellationToken: CancellationToken) : Task<int> =
        task {
            match ConnectCache.selectRetrieval parseResult (fun name -> Environment.GetEnvironmentVariable(name)) with
            | Error error ->
                return
                    Error(GraceError.Create error (getCorrelationId parseResult))
                    |> renderOutput parseResult
            | Ok retrieval -> return! connectSelectedImpl retrieval parseResult cancellationToken
        }

    /// Executes the connect command by binding ParseResult values to the SDK request and CLI output contract.
    type Connect() =
        inherit AsynchronousCommandLineAction()

        /// Runs the asynchronous connect action when System.CommandLine dispatches the parsed command.
        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: Threading.CancellationToken) : Task<int> =
            task {
                try
                    return! connectImpl parseResult cancellationToken
                with
                | ConfigurationWriteFailure ex ->
                    return
                        Error(GraceError.Create ex.Message (getCorrelationId parseResult))
                        |> renderOutput parseResult
                | :? OperationCanceledException -> return -1
                | ex ->
                    let error = GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult)
                    return (Error error |> renderOutput parseResult)
            }

    let Build =
        // Create main command and aliases, if any.
        let connectCommand = new Command("connect", Description = "Connect to a Grace repository.")

        connectCommand.Arguments.Add(Arguments.repositoryShortcut)
        connectCommand.Options.Add(Options.repositoryId)
        connectCommand.Options.Add(Options.repositoryName)
        connectCommand.Options.Add(Options.ownerId)
        connectCommand.Options.Add(Options.ownerName)
        connectCommand.Options.Add(Options.organizationId)
        connectCommand.Options.Add(Options.organizationName)
        connectCommand.Options.Add(Options.branchId)
        connectCommand.Options.Add(Options.branchName)
        connectCommand.Options.Add(Options.referenceType)
        connectCommand.Options.Add(Options.referenceId)
        connectCommand.Options.Add(Options.directoryVersionId)
        connectCommand.Options.Add(Options.correlationId)
        connectCommand.Options.Add(Options.serverAddress)
        connectCommand.Options.Add(Options.retrieveDefaultBranch)
        connectCommand.Options.Add(Options.force)
        connectCommand.Options.Add(ConnectCache.Options.cacheRequired)
        connectCommand.Options.Add(ConnectCache.Options.cacheUri)

        connectCommand.Action <- Connect()
        connectCommand
