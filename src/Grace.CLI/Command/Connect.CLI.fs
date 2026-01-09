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
open Grace.Types.Reference
open Grace.Types.Types
open Grace.Shared.Validation.Common
open Grace.Shared.Validation.Errors
open System
open System.Collections.Generic
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.IO
open System.Threading.Tasks
open System.CommandLine
open Spectre.Console
open Azure.Storage.Blobs
open Azure.Storage.Blobs.Models
open System.IO.Compression
open Grace.CLI

module Connect =

    type CommonParameters() =
        inherit ParameterBase()
        member val public RepositoryId: string = String.Empty with get, set
        member val public RepositoryName: string = String.Empty with get, set
        member val public OwnerId: string = String.Empty with get, set
        member val public OwnerName: string = String.Empty with get, set
        member val public OrganizationId: string = String.Empty with get, set
        member val public OrganizationName: string = String.Empty with get, set
        member val public RetrieveDefaultBranch: bool = true with get, set

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

    module private Arguments =
        let repositoryShortcut =
            new Argument<string>("repository", Description = "Repository shortcut in the form owner/organization/repository.", Arity = ArgumentArity.ZeroOrOne)

    type DirectoryVersionSelection =
        | UseDirectoryVersionId of DirectoryVersionId
        | UseReferenceId of ReferenceId
        | UseReferenceType of ReferenceType
        | UseDefault

    let private tryGetExplicitValue<'T> (parseResult: ParseResult) (option: Option<'T>) =
        let result = parseResult.GetResult(option)

        if isNull result || result.Implicit then
            None
        else
            Some(parseResult.GetValue(option))

    let private tryGetExplicitNonEmptyString (parseResult: ParseResult) (option: Option<string>) =
        match tryGetExplicitValue parseResult option with
        | Some value when not <| String.IsNullOrWhiteSpace(value) -> Some value
        | _ -> None

    type private RepositoryShortcut = { OwnerName: OwnerName; OrganizationName: OrganizationName; RepositoryName: RepositoryName }

    let private validateGraceName (name: string) (error: IErrorDiscriminatedUnion) (parseResult: ParseResult) =
        if Constants.GraceNameRegex.IsMatch(name) then
            Ok name
        else
            Error(GraceError.Create (getErrorMessage error) (getCorrelationId parseResult))

    let private tryGetRepositoryShortcut (parseResult: ParseResult) =
        let result = parseResult.GetResult(Arguments.repositoryShortcut)

        if isNull result || result.Implicit then
            Ok None
        else
            let value = parseResult.GetValue(Arguments.repositoryShortcut)

            if String.IsNullOrWhiteSpace(value) then
                Error(GraceError.Create "Repository shortcut must be in the form owner/organization/repository." (getCorrelationId parseResult))
            else
                let parts = value.Trim().Split('/', StringSplitOptions.RemoveEmptyEntries)

                if parts.Length <> 3 then
                    Error(GraceError.Create "Repository shortcut must be in the form owner/organization/repository." (getCorrelationId parseResult))
                else
                    let ownerName = parts[0].Trim()
                    let organizationName = parts[1].Trim()
                    let repositoryName = parts[2].Trim()

                    match validateGraceName ownerName OwnerError.InvalidOwnerName parseResult with
                    | Error error -> Error error
                    | Ok ownerName ->
                        match validateGraceName organizationName OrganizationError.InvalidOrganizationName parseResult with
                        | Error error -> Error error
                        | Ok organizationName ->
                            match validateGraceName repositoryName RepositoryError.InvalidRepositoryName parseResult with
                            | Error error -> Error error
                            | Ok repositoryName -> Ok(Some { OwnerName = ownerName; OrganizationName = organizationName; RepositoryName = repositoryName })

    let private hasExplicitOwner (parseResult: ParseResult) =
        tryGetExplicitValue parseResult Options.ownerId
        |> Option.exists (fun ownerId -> ownerId <> Guid.Empty)
        || (tryGetExplicitNonEmptyString parseResult Options.ownerName |> Option.isSome)

    let private hasExplicitOrganization (parseResult: ParseResult) =
        tryGetExplicitValue parseResult Options.organizationId
        |> Option.exists (fun organizationId -> organizationId <> Guid.Empty)
        || (tryGetExplicitNonEmptyString parseResult Options.organizationName
            |> Option.isSome)

    let private hasExplicitRepository (parseResult: ParseResult) =
        tryGetExplicitValue parseResult Options.repositoryId
        |> Option.exists (fun repositoryId -> repositoryId <> Guid.Empty)
        || (tryGetExplicitNonEmptyString parseResult Options.repositoryName |> Option.isSome)

    let internal applyRepositoryShortcut (parseResult: ParseResult) (graceIds: GraceIds) =
        match tryGetRepositoryShortcut parseResult with
        | Error error -> Error error
        | Ok None -> Ok graceIds
        | Ok(Some shortcut) ->
            if
                hasExplicitOwner parseResult
                || hasExplicitOrganization parseResult
                || hasExplicitRepository parseResult
            then
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
                        HasRepository = true }

    let internal getDirectoryVersionSelection (parseResult: ParseResult) =
        match tryGetExplicitValue parseResult Options.directoryVersionId with
        | Some directoryVersionId when directoryVersionId <> Guid.Empty -> UseDirectoryVersionId directoryVersionId
        | _ ->
            match tryGetExplicitValue parseResult Options.referenceId with
            | Some referenceId when referenceId <> Guid.Empty -> UseReferenceId referenceId
            | _ ->
                match tryGetExplicitNonEmptyString parseResult Options.referenceType with
                | Some referenceTypeRaw ->
                    let referenceType = discriminatedUnionFromString<ReferenceType>(referenceTypeRaw).Value
                    UseReferenceType referenceType
                | None -> UseDefault

    let internal tryGetDirectoryIdFromBranch (referenceType: ReferenceType) (branchDto: BranchDto) =
        match referenceType with
        | ReferenceType.Promotion when branchDto.LatestPromotion.DirectoryId <> Guid.Empty -> Some branchDto.LatestPromotion.DirectoryId
        | ReferenceType.Commit when branchDto.LatestCommit.DirectoryId <> Guid.Empty -> Some branchDto.LatestCommit.DirectoryId
        | ReferenceType.Checkpoint when branchDto.LatestCheckpoint.DirectoryId <> Guid.Empty -> Some branchDto.LatestCheckpoint.DirectoryId
        | ReferenceType.Save when branchDto.LatestSave.DirectoryId <> Guid.Empty -> Some branchDto.LatestSave.DirectoryId
        | _ -> None

    let internal resolveDefaultDirectoryVersionId (branchDto: BranchDto) =
        if branchDto.LatestPromotion.DirectoryId <> Guid.Empty then
            Some branchDto.LatestPromotion.DirectoryId
        elif branchDto.BasedOn.DirectoryId <> Guid.Empty then
            Some branchDto.BasedOn.DirectoryId
        else
            None

    let private ensureConfigurationFileExists () =
        if not <| configurationFileExists () then
            let graceDirPath = Path.Combine(Environment.CurrentDirectory, Constants.GraceConfigDirectory)
            let graceConfigPath = Path.Combine(graceDirPath, Constants.GraceConfigFileName)
            Directory.CreateDirectory(graceDirPath) |> ignore

            if not <| File.Exists(graceConfigPath) then
                GraceConfiguration() |> saveConfigFile graceConfigPath

    let private reloadConfiguration () =
        resetConfiguration ()
        Current() |> ignore

    let private applyServerAddressOverride (parseResult: ParseResult) =
        match tryGetExplicitNonEmptyString parseResult Options.serverAddress with
        | Some serverAddress ->
            let newConfig = Current()
            newConfig.ServerUri <- serverAddress
            updateConfiguration newConfig
            reloadConfiguration ()
        | None -> ()

    let private validateRequiredIds (parseResult: ParseResult) (graceIds: GraceIds) =
        let correlationId = getCorrelationId parseResult

        let ownerValid =
            graceIds.OwnerId <> Guid.Empty
            || not <| String.IsNullOrWhiteSpace(graceIds.OwnerName)

        let organizationValid =
            graceIds.OrganizationId <> Guid.Empty
            || not <| String.IsNullOrWhiteSpace(graceIds.OrganizationName)

        let repositoryValid =
            graceIds.RepositoryId <> Guid.Empty
            || not <| String.IsNullOrWhiteSpace(graceIds.RepositoryName)

        if not ownerValid then
            Error(GraceError.Create (getErrorMessage OwnerError.EitherOwnerIdOrOwnerNameRequired) correlationId)
        elif not organizationValid then
            Error(GraceError.Create (getErrorMessage OrganizationError.EitherOrganizationIdOrOrganizationNameRequired) correlationId)
        elif not repositoryValid then
            Error(GraceError.Create (getErrorMessage RepositoryError.EitherRepositoryIdOrRepositoryNameRequired) correlationId)
        else
            Ok()

    type Connect() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: Threading.CancellationToken) : Task<int> =
            task {
                try
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
                            | Ok() ->
                                do! Auth.ensureAccessToken parseResult

                                let ownerParameters =
                                    Parameters.Owner.GetOwnerParameters(
                                        OwnerId = graceIds.OwnerIdString,
                                        OwnerName = graceIds.OwnerName,
                                        CorrelationId = graceIds.CorrelationId
                                    )

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
                                | (Ok owner, Ok organization, Ok repository) ->
                                    let ownerDto = owner.ReturnValue
                                    let organizationDto = organization.ReturnValue
                                    let repositoryDto = repository.ReturnValue
                                    AnsiConsole.MarkupLine $"[{Colors.Important}]Found owner, organization, and repository.[/]"

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

                                    match! Branch.Get(branchParameters) with
                                    | Ok graceReturnValue ->
                                        let branchDto = graceReturnValue.ReturnValue
                                        AnsiConsole.MarkupLine $"[{Colors.Important}]Retrieved branch {branchDto.BranchName}.[/]"
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
                                        updateConfiguration newConfig
                                        reloadConfiguration ()
                                        AnsiConsole.MarkupLine $"[{Colors.Important}]Wrote new Grace configuration file.[/]"
                                        let retrieveDefaultBranch = parseResult.GetValue(Options.retrieveDefaultBranch)

                                        if retrieveDefaultBranch then
                                            let selectLatestReference (references: ReferenceDto seq) =
                                                references
                                                |> Seq.sortByDescending (fun reference -> reference.UpdatedAt |> Option.defaultValue reference.CreatedAt)
                                                |> Seq.tryHead

                                            let resolveDirectoryVersionIdFromReferenceType (referenceType: ReferenceType) =
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

                                                        let! referencesResult =
                                                            match referenceType with
                                                            | ReferenceType.Tag -> Branch.GetTags(getReferencesParameters)
                                                            | ReferenceType.External -> Branch.GetExternals(getReferencesParameters)
                                                            | ReferenceType.Rebase -> Branch.GetRebases(getReferencesParameters)
                                                            | _ -> task { return Ok(GraceReturnValue.Create [||] graceIds.CorrelationId) }

                                                        match referencesResult with
                                                        | Ok returnValue ->
                                                            match selectLatestReference returnValue.ReturnValue with
                                                            | Some reference -> return Ok reference.DirectoryId
                                                            | None ->
                                                                return
                                                                    Error(
                                                                        GraceError.Create
                                                                            $"No {referenceType} references were found for branch {branchDto.BranchName}."
                                                                            graceIds.CorrelationId
                                                                    )
                                                        | Error error -> return Error error
                                                }

                                            let resolveTargetDirectoryVersionId () =
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

                                                        match! Branch.GetReference(getReferenceParameters) with
                                                        | Ok returnValue -> return Ok returnValue.ReturnValue.DirectoryId
                                                        | Error error -> return Error error
                                                    | UseReferenceType referenceType -> return! resolveDirectoryVersionIdFromReferenceType referenceType
                                                    | UseDefault ->
                                                        match resolveDefaultDirectoryVersionId branchDto with
                                                        | Some directoryVersionId -> return Ok directoryVersionId
                                                        | None ->
                                                            return
                                                                Error(GraceError.Create "No downloadable version found for this branch." graceIds.CorrelationId)
                                                }

                                            match! resolveTargetDirectoryVersionId () with
                                            | Error error -> return (Error error |> renderOutput parseResult)
                                            | Ok directoryVersionId ->
                                                let getDirectoryContentsParameters =
                                                    Parameters.DirectoryVersion.GetParameters(
                                                        OwnerId = $"{ownerDto.OwnerId}",
                                                        OrganizationId = $"{organizationDto.OrganizationId}",
                                                        RepositoryId = $"{repositoryDto.RepositoryId}",
                                                        DirectoryVersionId = $"{directoryVersionId}",
                                                        CorrelationId = graceIds.CorrelationId
                                                    )

                                                AnsiConsole.MarkupLine $"[{Colors.Important}]Retrieving all DirectoryVersions.[/]"

                                                let! directoryVersionsResult = DirectoryVersion.GetDirectoryVersionsRecursive(getDirectoryContentsParameters)

                                                let getZipFileParameters =
                                                    Parameters.DirectoryVersion.GetZipFileParameters(
                                                        OwnerId = $"{ownerDto.OwnerId}",
                                                        OrganizationId = $"{organizationDto.OrganizationId}",
                                                        RepositoryId = $"{repositoryDto.RepositoryId}",
                                                        DirectoryVersionId = $"{directoryVersionId}",
                                                        CorrelationId = graceIds.CorrelationId
                                                    )

                                                AnsiConsole.MarkupLine $"[{Colors.Important}]Retrieving zip file download uri.[/]"
                                                let! getZipFileResult = DirectoryVersion.GetZipFile(getZipFileParameters)
                                                AnsiConsole.MarkupLine $"[{Colors.Important}]Finished getting zip file download uri.[/]"

                                                match (directoryVersionsResult, getZipFileResult) with
                                                | (Ok directoryVerionsReturnValue, Ok getZipFileReturnValue) ->
                                                    AnsiConsole.MarkupLine $"[{Colors.Important}]Retrieved all DirectoryVersions.[/]"

                                                    let directoryVersionDtos = directoryVerionsReturnValue.ReturnValue

                                                    let fileVersions =
                                                        directoryVersionDtos
                                                        |> Seq.map (fun directoryVersionDto -> directoryVersionDto.DirectoryVersion)
                                                        |> Seq.collect (fun dv -> dv.Files)
                                                        |> Seq.toArray

                                                    let force = parseResult.GetValue(Options.force)

                                                    let! conflicts, filesToSkip =
                                                        task {
                                                            let conflicts = ResizeArray<string>()
                                                            let filesToSkip = HashSet<RelativePath>()

                                                            for fileVersion in fileVersions do
                                                                let filePath = Path.Combine(Current().RootDirectory, fileVersion.RelativePath)

                                                                if File.Exists(filePath) then
                                                                    try
                                                                        use stream = File.OpenRead(filePath)

                                                                        let! localHash =
                                                                            Grace.Shared.Services.computeSha256ForFile stream fileVersion.RelativePath

                                                                        if localHash = fileVersion.Sha256Hash then
                                                                            filesToSkip.Add(fileVersion.RelativePath) |> ignore
                                                                        elif not force then
                                                                            conflicts.Add(fileVersion.RelativePath)
                                                                    with _ ->
                                                                        if not force then conflicts.Add(fileVersion.RelativePath)

                                                            return conflicts, filesToSkip
                                                        }

                                                    if conflicts.Count > 0 then
                                                        AnsiConsole.MarkupLine
                                                            $"[{Colors.Error}]Found {conflicts.Count} conflicting file(s). Use --force to overwrite.[/]"

                                                        if parseResult |> verbose then
                                                            conflicts
                                                            |> Seq.sort
                                                            |> Seq.iter (fun conflict -> AnsiConsole.MarkupLine $"[{Colors.Error}]{conflict}[/]")

                                                        return
                                                            (Error(GraceError.Create "Conflicting files exist in the working directory." graceIds.CorrelationId)
                                                             |> renderOutput parseResult)

                                                    else
                                                        let fileVersionsByRelativePath =
                                                            let lookup =
                                                                Dictionary<RelativePath, FileVersion>(fileVersions.Length, StringComparer.OrdinalIgnoreCase)

                                                            fileVersions
                                                            |> Seq.iter (fun fileVersion -> lookup[normalizeFilePath fileVersion.RelativePath] <- fileVersion)

                                                            lookup

                                                        let uriWithSharedAccessSignature = getZipFileReturnValue.ReturnValue

                                                        // Download the .zip file to temp directory.
                                                        let blobClient = BlobClient(uriWithSharedAccessSignature)

                                                        // Loop through the ZipArchiveEntry list, identify if each file version is binary, and extract
                                                        //   each one accordingly.
                                                        use! zipFile = blobClient.OpenReadAsync(bufferSize = 64 * 1024)
                                                        use zipArchive = new ZipArchive(zipFile, ZipArchiveMode.Read)

                                                        AnsiConsole.MarkupLine $"[{Colors.Important}]Streaming contents from .zip file.[/]"
                                                        AnsiConsole.MarkupLine $"[{Colors.Important}]Starting to write files to disk.[/]"

                                                        let additionalEntries = ResizeArray<string>()

                                                        for entry in zipArchive.Entries do
                                                            if not <| String.IsNullOrEmpty(entry.Name) then
                                                                let entryRelativePath = normalizeFilePath entry.FullName

                                                                match fileVersionsByRelativePath.TryGetValue(entryRelativePath) with
                                                                | true, fileVersion ->
                                                                    let objectFileName =
                                                                        if String.IsNullOrWhiteSpace(entry.Comment) then
                                                                            fileVersion.GetObjectFileName
                                                                        else
                                                                            entry.Comment

                                                                    let fileInfo = FileInfo(Path.Combine(Current().RootDirectory, fileVersion.RelativePath))

                                                                    let objectFileInfo =
                                                                        FileInfo(
                                                                            Path.Combine(Current().ObjectDirectory, fileVersion.RelativePath, objectFileName)
                                                                        )

                                                                    Directory.CreateDirectory(fileInfo.DirectoryName) |> ignore
                                                                    Directory.CreateDirectory(objectFileInfo.DirectoryName) |> ignore

                                                                    let writeWorkingFile = not <| filesToSkip.Contains(fileVersion.RelativePath)

                                                                    let writeObjectFile = not objectFileInfo.Exists

                                                                    if fileVersion.IsBinary then
                                                                        if writeWorkingFile then entry.ExtractToFile(fileInfo.FullName, true)

                                                                        if writeObjectFile then entry.ExtractToFile(objectFileInfo.FullName, true)
                                                                    else
                                                                        let uncompressAndWriteToFile (zipEntry: ZipArchiveEntry) (fileInfo: FileInfo) =
                                                                            use entryStream = zipEntry.Open()
                                                                            use fileStream = fileInfo.Create()
                                                                            use gzipStream = new GZipStream(entryStream, CompressionMode.Decompress)
                                                                            gzipStream.CopyTo(fileStream)

                                                                        if writeWorkingFile then uncompressAndWriteToFile entry fileInfo

                                                                        if writeObjectFile then uncompressAndWriteToFile entry objectFileInfo

                                                                    if parseResult |> verbose then
                                                                        AnsiConsole.MarkupLine $"[{Colors.Important}]Wrote {fileVersion.RelativePath}.[/]"
                                                                | false, _ -> additionalEntries.Add(entry.FullName)

                                                        if additionalEntries.Count > 0 && (parseResult |> verbose) then
                                                            AnsiConsole.MarkupLine
                                                                $"[{Colors.Deemphasized}]Zip contained {additionalEntries.Count} additional entry(ies). Ignored.[/]"

                                                        AnsiConsole.MarkupLine $"[{Colors.Important}]Finished writing files to disk.[/]"

                                                        AnsiConsole.MarkupLine $"[{Colors.Important}]Creating Grace Index file.[/]"
                                                        let! previousGraceStatus = readGraceStatusFile ()
                                                        let! graceStatus = createNewGraceStatusFile previousGraceStatus parseResult
                                                        do! writeGraceStatusFile graceStatus

                                                        AnsiConsole.MarkupLine $"[{Colors.Important}]Creating Grace Object Cache Index file.[/]"
                                                        let! objectCache = readGraceObjectCacheFile ()

                                                        let _ =
                                                            Parallel.ForEach(
                                                                graceStatus.Index.Values,
                                                                Constants.ParallelOptions,
                                                                (fun localDirectoryVersion ->
                                                                    if not <| objectCache.Index.ContainsKey(localDirectoryVersion.DirectoryVersionId) then
                                                                        objectCache.Index.AddOrUpdate(
                                                                            localDirectoryVersion.DirectoryVersionId,
                                                                            (fun _ -> localDirectoryVersion),
                                                                            (fun _ _ -> localDirectoryVersion)
                                                                        )
                                                                        |> ignore)
                                                            )

                                                        do! writeGraceObjectCacheFile objectCache
                                                        return 0
                                                | (Error error, _) -> return (Error error |> renderOutput parseResult)
                                                | (_, Error error) -> return (Error error |> renderOutput parseResult)

                                        else
                                            return 0
                                    | Error error -> return (Error error |> renderOutput parseResult)
                                | (Error error, _, _) -> return (Error error |> renderOutput parseResult)
                                | (_, Error error, _) -> return (Error error |> renderOutput parseResult)
                                | (_, _, Error error) -> return (Error error |> renderOutput parseResult)
                with
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

        connectCommand.Action <- Connect()
        connectCommand
