namespace Grace.CLI.Command

open DiffPlex
open DiffPlex.DiffBuilder.Model
open FSharpPlus
open Grace.CLI.Common
open Grace.CLI.Services
open Grace.SDK
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Parameters.Branch
open Grace.Shared.Parameters.Diff
open Grace.Shared.Parameters.DirectoryVersion
open Grace.Shared.Utilities
open Grace.Types.Branch
open Grace.Types.Diff
open Grace.Types.Reference
open Grace.Types.Types
open Grace.Shared.Validation.Errors.Diff
open Spectre.Console
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.CommandLine
open System.CommandLine.NamingConventionBinder
open System.CommandLine.Parsing
open System.Linq
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Spectre.Console
open Spectre.Console
open Spectre.Console.Rendering
open Grace.Shared.Parameters.Storage

module Diff =

    type CommonParameters() =
        inherit ParameterBase()
        member val public OwnerId: string = String.Empty with get, set
        member val public OwnerName: string = String.Empty with get, set
        member val public OrganizationId: string = String.Empty with get, set
        member val public OrganizationName: string = String.Empty with get, set
        member val public RepositoryId: string = String.Empty with get, set
        member val public RepositoryName: string = String.Empty with get, set
        member val public DirectoryVersionId1: string = String.Empty with get, set
        member val public DirectoryVersionId2: string = String.Empty with get, set

    module private Options =
        let ownerId =
            new Option<string>(
                "--ownerId",
                Required = false,
                Description = "The repository's owner ID <Guid>.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> $"{Current().OwnerId}")
            )

        let ownerName =
            new Option<string>(
                "--ownerName",
                Required = false,
                Description = "The repository's owner name. [default: current owner]",
                Arity = ArgumentArity.ExactlyOne
            )

        let organizationId =
            new Option<string>(
                "--organizationId",
                Required = false,
                Description = "The repository's organization ID <Guid>.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> $"{Current().OrganizationId}")
            )

        let organizationName =
            new Option<string>(
                "--organizationName",
                Required = false,
                Description = "The repository's organization name. [default: current organization]",
                Arity = ArgumentArity.ZeroOrOne
            )

        let repositoryId =
            new Option<string>(
                "--repositoryId",
                [| "-r" |],
                Required = false,
                Description = "The repository's Id <Guid>.",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> $"{Current().RepositoryId}")
            )

        let repositoryName =
            new Option<string>(
                "--repositoryName",
                [| "-n" |],
                Required = false,
                Description = "The name of the repository. [default: current repository]",
                Arity = ArgumentArity.ExactlyOne
            )

        let branchId =
            new Option<string>(
                "--branchId",
                [| "-i" |],
                Required = false,
                Description = "The branch's ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> $"{Current().BranchId}")
            )

        let branchName =
            new Option<string>(
                "--branchName",
                [| "-b" |],
                Required = false,
                Description = "The name of the branch. [default: current branch]",
                Arity = ArgumentArity.ExactlyOne
            )

        let directoryVersionId1 =
            new Option<string>(
                "--directoryVersionId1",
                [| "--d1" |],
                Required = true,
                Description = "The first DirectoryId to compare in the diff.",
                Arity = ArgumentArity.ExactlyOne
            )

        let directoryVersionId2 =
            new Option<string>(
                "--directoryVersionId2",
                [| "--d2" |],
                Required = false,
                Description = "The second DirectoryId to compare in the diff.",
                Arity = ArgumentArity.ExactlyOne
            )

        let sha256Hash1 =
            new Option<Sha256Hash>(
                "--sha256Hash1",
                [| "--s1" |],
                Required = true,
                Description = "The first partial or full SHA-256 hash to compare in the diff.",
                Arity = ArgumentArity.ExactlyOne
            )

        let sha256Hash2 =
            new Option<Sha256Hash>(
                "--sha256Hash2",
                [| "--s2" |],
                Required = false,
                Description = "The second partial or full SHA-256 hash to compare in the diff.",
                Arity = ArgumentArity.ExactlyOne
            )

        let tag = new Option<string>("--tag", Required = true, Description = "The tag to compare the current version to.", Arity = ArgumentArity.ExactlyOne)

    let mustBeAValidGuid (parseResult: ParseResult) (parameters: CommonParameters) (option: Option) (value: string) (error: DiffError) =
        let mutable guid = Guid.Empty

        if
            parseResult.GetResult(option) <> null
            && not <| String.IsNullOrEmpty(value)
            && (Guid.TryParse(value, &guid) = false || guid = Guid.Empty)
        then
            Error(GraceError.Create (DiffError.getErrorMessage error) (parameters.CorrelationId))
        else
            Ok(parseResult, parameters)

    let mustBeAValidGraceName (parseResult: ParseResult) (parameters: CommonParameters) (option: Option) (value: string) (error: DiffError) =
        if
            parseResult.GetResult(option) <> null
            && not <| Constants.GraceNameRegex.IsMatch(value)
        then
            Error(GraceError.Create (DiffError.getErrorMessage error) (parameters.CorrelationId))
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

        let ``RepositoryId must be a Guid`` (parseResult: ParseResult, parameters: CommonParameters) =
            mustBeAValidGuid parseResult parameters Options.repositoryId parameters.RepositoryId InvalidRepositoryId

        let ``RepositoryName must be a valid Grace name`` (parseResult: ParseResult, parameters: CommonParameters) =
            mustBeAValidGraceName parseResult parameters Options.repositoryName parameters.RepositoryName InvalidRepositoryName

        (parseResult, parameters)
        |> ``OwnerId must be a Guid``
        >>= ``OwnerName must be a valid Grace name``
        >>= ``OrganizationId must be a Guid``
        >>= ``OrganizationName must be a valid Grace name``
        >>= ``RepositoryId must be a Guid``
        >>= ``RepositoryName must be a valid Grace name``

    let private DirectoryIdValidations (parseResult, parameters) =
        let ``DirectoryVersionId1 must be a Guid`` (parseResult: ParseResult, parameters: CommonParameters) =
            mustBeAValidGuid parseResult parameters Options.directoryVersionId1 parameters.DirectoryVersionId1 InvalidDirectoryId

        let ``DirectoryVersionId2 must be a Guid`` (parseResult: ParseResult, parameters: CommonParameters) =
            mustBeAValidGuid parseResult parameters Options.directoryVersionId2 parameters.DirectoryVersionId2 InvalidDirectoryId

        (parseResult, parameters)
        |> ``DirectoryVersionId1 must be a Guid``
        >>= ``DirectoryVersionId2 must be a Guid``

    let private sha256Validations (parseResult, parameters) =
        let ``Sha256Hash1 must be a valid SHA-256 hash value`` (parseResult: ParseResult, (parameters: GetDiffBySha256HashParameters)) =
            if
                parseResult.GetResult(Options.sha256Hash1) <> null
                && not <| Constants.Sha256Regex.IsMatch(parameters.Sha256Hash1)
            then
                let properties = Dictionary<string, string>()
                properties.Add("repositoryId", $"{parameters.RepositoryId}")
                properties.Add("sha256Hash1", parameters.Sha256Hash1)

                Error(GraceError.CreateWithMetadata (DiffError.getErrorMessage InvalidSha256Hash) (parameters.CorrelationId) properties)
            else
                Ok(parseResult, parameters)

        let ``Sha256Hash2 must be a valid SHA-256 hash value`` (parseResult: ParseResult, (parameters: GetDiffBySha256HashParameters)) =
            if
                parseResult.GetResult(Options.sha256Hash2) <> null
                && not <| Constants.Sha256Regex.IsMatch(parameters.Sha256Hash2)
            then
                Error(GraceError.Create (DiffError.getErrorMessage InvalidSha256Hash) (parameters.CorrelationId))
            else
                Ok(parseResult, parameters)

        (parseResult, parameters)
        |> ``Sha256Hash1 must be a valid SHA-256 hash value``
        >>= ``Sha256Hash2 must be a valid SHA-256 hash value``

    let private renderLine (diffLine: DiffPiece) =
        if not <| diffLine.Position.HasValue then
            $"        {diffLine.Text.EscapeMarkup()}"
        else
            $"{diffLine.Position, 6:D}: {diffLine.Text.EscapeMarkup()}"

    let private getMarkup (diffLine: DiffPiece) =
        match diffLine.Type with
        | ChangeType.Deleted -> Markup($"[{Colors.Deleted}]-{renderLine diffLine}[/]")
        | ChangeType.Inserted -> Markup($"[{Colors.Added}]+{renderLine diffLine}[/]")
        | ChangeType.Modified -> Markup($"[{Colors.Changed}]~{renderLine diffLine}[/]")
        | ChangeType.Imaginary -> Markup($"[{Colors.Deemphasized}] {renderLine diffLine}[/]")
        | ChangeType.Unchanged -> Markup($"[{Colors.Important}] {renderLine diffLine}[/]")
        | _ -> Markup($"[{Colors.Important}] {diffLine.Text}[/]")

    let markupList = List<IRenderable>()
    let addToOutput (markup: IRenderable) = markupList.Add markup

    let renderInlineDiff (inlineDiff: List<DiffPiece[]>) =
        for i = 0 to inlineDiff.Count - 1 do
            for diffLine in inlineDiff[i] do
                addToOutput (getMarkup diffLine)

            if not <| (i = inlineDiff.Count - 1) then
                addToOutput (Markup($"[{Colors.Deemphasized}]-------[/]"))
            else
                addToOutput (Markup(String.Empty))

    let printDiffResults (diffDto: DiffDto) =
        if diffDto.HasDifferences then
            addToOutput (Markup($"[{Colors.Important}]Differences found.[/]"))

            for diff in diffDto.Differences do
                match diff.FileSystemEntryType with
                | FileSystemEntryType.File ->
                    addToOutput (
                        Markup($"[{Colors.Important}]{getDiscriminatedUnionCaseName diff.DifferenceType}[/] [{Colors.Highlighted}]{diff.RelativePath}[/]")
                    )
                | FileSystemEntryType.Directory ->
                    if diff.DifferenceType <> DifferenceType.Change then
                        addToOutput (
                            Markup($"[{Colors.Important}]{getDiscriminatedUnionCaseName diff.DifferenceType}[/] [{Colors.Highlighted}]{diff.RelativePath}[/]")
                        )

            for fileDiff in diffDto.FileDiffs.OrderBy(fun fileDiff -> fileDiff.RelativePath) do
                //addToOutput ((new Rule($"[{Colors.Important}]{fileDiff.RelativePath}[/]")).LeftAligned())
                if fileDiff.CreatedAt1 > fileDiff.CreatedAt2 then
                    addToOutput (
                        (new Rule(
                            $"[{Colors.Important}]{fileDiff.RelativePath} | {getShortSha256Hash fileDiff.FileSha1} - {fileDiff.CreatedAt1 |> ago} | {getShortSha256Hash fileDiff.FileSha2} - {fileDiff.CreatedAt2 |> ago}[/]"
                        ))
                            .LeftJustified()
                    )
                else
                    addToOutput (
                        (new Rule(
                            $"[{Colors.Important}]{fileDiff.RelativePath} | {getShortSha256Hash fileDiff.FileSha2} - {fileDiff.CreatedAt2 |> ago} | {getShortSha256Hash fileDiff.FileSha1} - {fileDiff.CreatedAt1 |> ago}[/]"
                        ))
                            .LeftJustified()
                    )

                if fileDiff.IsBinary then
                    addToOutput (Markup($"[{Colors.Important}]Binary file.[/]"))
                else
                    renderInlineDiff fileDiff.InlineDiff
        else
            logToAnsiConsole Colors.Highlighted $"No differences found."

    /// Creates the text output for a diff to the most recent specific ReferenceType.
    type GetDiffByReferenceTypeParameters() =
        inherit CommonParameters()
        member val public BranchId = String.Empty with get, set
        member val public BranchName = BranchName String.Empty with get, set

    let private diffToReferenceType (parseResult: ParseResult) (parameters: GetDiffByReferenceTypeParameters) (referenceType: ReferenceType) =
        task {
            if parseResult |> verbose then printParseResult parseResult

            let validateIncomingParameters = (parseResult, parameters) |> CommonValidations

            match validateIncomingParameters with
            | Ok _ ->
                if parseResult |> hasOutput then
                    do!
                        progress
                            .Columns(progressColumns)
                            .StartAsync(fun progressContext ->
                                task {
                                    let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Reading Grace index file.[/]")

                                    let t1 = progressContext.AddTask($"[{Color.DodgerBlue1}]Scanning working directory for changes.[/]", autoStart = false)

                                    let t2 = progressContext.AddTask($"[{Color.DodgerBlue1}]Creating new directory verions.[/]", autoStart = false)

                                    let t3 = progressContext.AddTask($"[{Color.DodgerBlue1}]Uploading changed files to object storage.[/]", autoStart = false)

                                    let t4 = progressContext.AddTask($"[{Color.DodgerBlue1}]Uploading new directory versions.[/]", autoStart = false)

                                    let t5 = progressContext.AddTask($"[{Color.DodgerBlue1}]Creating a save reference.[/]", autoStart = false)

                                    let t6 =
                                        progressContext.AddTask(
                                            $"[{Color.DodgerBlue1}]Getting {(getDiscriminatedUnionCaseName referenceType).ToLowerInvariant()}.[/]",
                                            autoStart = false
                                        )

                                    let t7 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending diff request to server.[/]", autoStart = false)

                                    let mutable rootDirectoryId = DirectoryVersionId.Empty
                                    let mutable rootDirectorySha256Hash = Sha256Hash String.Empty
                                    let mutable previousDirectoryIds: HashSet<DirectoryVersionId> = null
                                    let repositoryId = RepositoryId.Parse(parameters.RepositoryId)

                                    // Check for latest commit and latest root directory version from grace watch. If it's running, we know GraceStatus is up-to-date.
                                    match! getGraceWatchStatus () with
                                    | Some graceWatchStatus ->
                                        t0.Value <- 100.0
                                        t1.Value <- 100.0
                                        t2.Value <- 100.0
                                        t3.Value <- 100.0
                                        t4.Value <- 100.0
                                        t5.Value <- 100.0
                                        rootDirectoryId <- graceWatchStatus.RootDirectoryId
                                        rootDirectorySha256Hash <- graceWatchStatus.RootDirectorySha256Hash
                                        previousDirectoryIds <- graceWatchStatus.DirectoryIds
                                    | None ->
                                        let! previousGraceStatus = readGraceStatusFile ()
                                        let mutable graceStatus = previousGraceStatus
                                        t0.Value <- 100.0
                                        t1.StartTask()
                                        let! differences = scanForDifferences previousGraceStatus
                                        let! newFileVersions = copyUpdatedFilesToObjectCache t1 differences
                                        t1.Value <- 100.0

                                        t2.StartTask()

                                        let! (updatedGraceStatus, newDirectoryVersions) = getNewGraceStatusAndDirectoryVersions previousGraceStatus differences

                                        do! writeGraceStatusFile updatedGraceStatus
                                        rootDirectoryId <- updatedGraceStatus.RootDirectoryId
                                        rootDirectorySha256Hash <- updatedGraceStatus.RootDirectorySha256Hash
                                        previousDirectoryIds <- updatedGraceStatus.Index.Keys.ToHashSet()
                                        t2.Value <- 100.0

                                        t3.StartTask()

                                        let getUploadMetadataForFilesParameters =
                                            GetUploadMetadataForFilesParameters(
                                                OwnerId = parameters.OwnerId,
                                                OwnerName = parameters.OwnerName,
                                                OrganizationId = parameters.OrganizationId,
                                                OrganizationName = parameters.OrganizationName,
                                                RepositoryId = parameters.RepositoryId,
                                                RepositoryName = parameters.RepositoryName,
                                                CorrelationId = getCorrelationId parseResult,
                                                FileVersions =
                                                    (newFileVersions
                                                     |> Seq.map (fun localFileVersion -> localFileVersion.ToFileVersion)
                                                     |> Seq.toArray)
                                            )

                                        match! uploadFilesToObjectStorage getUploadMetadataForFilesParameters with
                                        | Ok returnValue -> ()
                                        | Error error -> logToAnsiConsole Colors.Error $"Failed to upload changed files to object storage. {error}"

                                        t3.Value <- 100.0

                                        t4.StartTask()

                                        if (newDirectoryVersions.Count > 0) then
                                            (task {
                                                let saveParameters = SaveDirectoryVersionsParameters()
                                                saveParameters.OwnerId <- parameters.OwnerId
                                                saveParameters.OwnerName <- parameters.OwnerName
                                                saveParameters.OrganizationId <- parameters.OrganizationId
                                                saveParameters.OrganizationName <- parameters.OrganizationName
                                                saveParameters.RepositoryId <- parameters.RepositoryId
                                                saveParameters.RepositoryName <- parameters.RepositoryName
                                                saveParameters.CorrelationId <- getCorrelationId parseResult

                                                saveParameters.DirectoryVersions <- newDirectoryVersions.Select(fun dv -> dv.ToDirectoryVersion).ToList()

                                                match! DirectoryVersion.SaveDirectoryVersions saveParameters with
                                                | Ok returnValue -> ()
                                                | Error error -> logToAnsiConsole Colors.Error $"Failed to upload new directory versions. {error}"
                                            })
                                                .Wait()

                                        t4.Value <- 100.0

                                        t5.StartTask()

                                        if newDirectoryVersions.Count > 0 then
                                            (task {
                                                match!
                                                    createSaveReference
                                                        (getRootDirectoryVersion updatedGraceStatus)
                                                        $"Created during `grace diff {(getDiscriminatedUnionCaseName referenceType).ToLowerInvariant()}` operation."
                                                        (getCorrelationId parseResult)
                                                with
                                                | Ok saveReference -> ()
                                                | Error error -> logToAnsiConsole Colors.Error $"Failed to create a save reference. {error}"
                                            })
                                                .Wait()

                                        t5.Value <- 100.0

                                    // Check for latest reference of the given type from the server.
                                    t6.StartTask()

                                    let getReferencesParameters =
                                        GetReferencesParameters(
                                            OwnerId = parameters.OwnerId,
                                            OwnerName = parameters.OwnerName,
                                            OrganizationId = parameters.OrganizationId,
                                            OrganizationName = parameters.OrganizationName,
                                            RepositoryId = parameters.RepositoryId,
                                            RepositoryName = parameters.RepositoryName,
                                            BranchId = parameters.BranchId,
                                            BranchName = parameters.BranchName,
                                            MaxCount = 1,
                                            CorrelationId = parameters.CorrelationId
                                        )

                                    let! getAReferenceResult =
                                        task {
                                            match referenceType with
                                            | Commit -> return! Branch.GetCommits getReferencesParameters
                                            | Checkpoint -> return! Branch.GetCheckpoints getReferencesParameters
                                            | Save -> return! Branch.GetSaves getReferencesParameters
                                            | Tag -> return! Branch.GetTags getReferencesParameters
                                            | External -> return! Branch.GetExternals getReferencesParameters
                                            | Rebase -> return! Branch.GetRebases getReferencesParameters

                                            // Promotions are different, because we actually want the promotion from the parent branch that this branch is based on.
                                            | Promotion ->
                                                let promotions = List<ReferenceDto>()

                                                let branchParameters =
                                                    Parameters.Branch.GetBranchParameters(
                                                        OwnerId = parameters.OwnerId,
                                                        OwnerName = parameters.OwnerName,
                                                        OrganizationId = parameters.OrganizationId,
                                                        OrganizationName = parameters.OrganizationName,
                                                        RepositoryId = parameters.RepositoryId,
                                                        RepositoryName = parameters.RepositoryName,
                                                        BranchId = parameters.BranchId,
                                                        BranchName = parameters.BranchName,
                                                        CorrelationId = parameters.CorrelationId
                                                    )

                                                match! Branch.Get(branchParameters) with
                                                | Ok returnValue ->
                                                    let branchDto = returnValue.ReturnValue
                                                    promotions.Add(branchDto.BasedOn)
                                                | Error error ->
                                                    logToAnsiConsole Colors.Error (Markup.Escape($"Error in Branch.Get: {error}"))

                                                    if parseResult |> json || parseResult |> verbose then
                                                        logToAnsiConsole Colors.Verbose (serialize error)

                                                return Ok(GraceReturnValue.Create (promotions.ToArray()) parameters.CorrelationId)
                                        }

                                    let latestReference =
                                        match getAReferenceResult with
                                        | Ok returnValue ->
                                            // There should only be one reference, because we're using MaxCount = 1.
                                            let references = returnValue.ReturnValue

                                            if references.Count() > 0 then
                                                //logToAnsiConsole Colors.Verbose $"Got latest reference: {references.First().ReferenceText}; {references.First().CreatedAt}; {getShortenedSha256Hash (references.First().Sha256Hash)}; {references.First().DirectoryId}."
                                                references.First()
                                            else
                                                logToAnsiConsole Colors.Error $"Error getting latest reference. No matching references were found."

                                                ReferenceDto.Default
                                        | Error error ->
                                            logToAnsiConsole Colors.Error $"Error getting latest reference: {Markup.Escape(error.Error)}."

                                            ReferenceDto.Default

                                    t6.Value <- 100.0

                                    // Sending diff request to server.
                                    t7.StartTask()
                                    //logToAnsiConsole Colors.Verbose $"latestReference.DirectoryId: {latestReference.DirectoryId}; rootDirectoryId: {rootDirectoryId}."
                                    let getDiffParameters =
                                        GetDiffParameters(
                                            OwnerId = parameters.OwnerId,
                                            OwnerName = parameters.OwnerName,
                                            OrganizationId = parameters.OrganizationId,
                                            OrganizationName = parameters.OrganizationName,
                                            RepositoryId = parameters.RepositoryId,
                                            RepositoryName = parameters.RepositoryName,
                                            DirectoryVersionId1 = latestReference.DirectoryId,
                                            DirectoryVersionId2 = rootDirectoryId,
                                            CorrelationId = parameters.CorrelationId
                                        )

                                    let! getDiffResult = Diff.GetDiff(getDiffParameters)

                                    match getDiffResult with
                                    | Ok returnValue ->
                                        let diffDto = returnValue.ReturnValue
                                        printDiffResults diffDto
                                    | Error error ->
                                        let s = StringExtensions.EscapeMarkup($"{error.Error}")
                                        logToAnsiConsole Colors.Error $"Error submitting diff: {s}"

                                        if parseResult |> json || parseResult |> verbose then
                                            logToAnsiConsole Colors.Verbose (serialize error)

                                    t7.Increment(100.0)
                                //AnsiConsole.MarkupLine($"[{Colors.Important}]Differences: {differences.Count}.[/]")
                                //AnsiConsole.MarkupLine($"[{Colors.Error}]{error.Error.EscapeMarkup()}[/]")
                                })

                    for markup in markupList do
                        writeMarkup markup

                    return 0
                else
                    // Do the thing here
                    return 0
            | Error error -> return (Error error) |> renderOutput parseResult
        }

    let private promotionHandler =
        CommandHandler.Create(fun (parseResult: ParseResult) (parameters: GetDiffByReferenceTypeParameters) ->
            task { return! diffToReferenceType parseResult parameters ReferenceType.Promotion } :> Task)

    let private commitHandler =
        CommandHandler.Create(fun (parseResult: ParseResult) (parameters: GetDiffByReferenceTypeParameters) ->
            task { return! diffToReferenceType parseResult parameters ReferenceType.Commit } :> Task)

    let private checkpointHandler =
        CommandHandler.Create(fun (parseResult: ParseResult) (parameters: GetDiffByReferenceTypeParameters) ->
            task { return! diffToReferenceType parseResult parameters ReferenceType.Checkpoint } :> Task)

    let private saveHandler =
        CommandHandler.Create(fun (parseResult: ParseResult) (parameters: GetDiffByReferenceTypeParameters) ->
            task { return! diffToReferenceType parseResult parameters ReferenceType.Save } :> Task)

    let private tagHandler =
        CommandHandler.Create(fun (parseResult: ParseResult) (parameters: GetDiffByReferenceTypeParameters) ->
            task { return! diffToReferenceType parseResult parameters ReferenceType.Tag } :> Task)

    type DirectoryIdParameters() =
        inherit CommonParameters()
        member val public DirectoryVersionId1 = DirectoryVersionId.Empty with get, set
        member val public DirectoryVersionId2 = DirectoryVersionId.Empty with get, set

    let private DirectoryIdCommand =
        CommandHandler.Create(fun (parseResult: ParseResult) (parameters: DirectoryIdParameters) ->
            task {
                if parseResult |> verbose then printParseResult parseResult

                let validateIncomingParameters = (parseResult, parameters) |> CommonValidations >>= DirectoryIdValidations

                match validateIncomingParameters with
                | Ok _ -> return 0
                | Error error -> return (Error error) |> renderOutput parseResult
            }
            :> Task)

    type ShaParameters() =
        inherit CommonParameters()
        member val public Sha256Hash1 = Sha256Hash String.Empty with get, set
        member val public Sha256Hash2 = Sha256Hash String.Empty with get, set

    let private shaHandler =
        CommandHandler.Create(fun (parseResult: ParseResult) (parameters: ShaParameters) ->
            task {
                if parseResult |> verbose then printParseResult parseResult

                let validateIncomingParameters = (parseResult, parameters) |> CommonValidations

                match validateIncomingParameters with
                | Ok _ ->
                    if parseResult |> hasOutput then
                        do!
                            progress
                                .Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                    task {
                                        let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Reading Grace index file.[/]")

                                        let t1 =
                                            progressContext.AddTask($"[{Color.DodgerBlue1}]Scanning working directory for changes.[/]", autoStart = false)

                                        let t2 = progressContext.AddTask($"[{Color.DodgerBlue1}]Creating new directory verions.[/]", autoStart = false)

                                        let t3 =
                                            progressContext.AddTask($"[{Color.DodgerBlue1}]Uploading changed files to object storage.[/]", autoStart = false)

                                        let t4 = progressContext.AddTask($"[{Color.DodgerBlue1}]Uploading new directory versions.[/]", autoStart = false)

                                        let t5 = progressContext.AddTask($"[{Color.DodgerBlue1}]Creating a save reference.[/]", autoStart = false)

                                        let t6 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending diff request to server.[/]", autoStart = false)

                                        let mutable rootDirectoryId = DirectoryVersionId.Empty
                                        let mutable rootDirectorySha256Hash = Sha256Hash String.Empty
                                        let mutable previousDirectoryIds: HashSet<DirectoryVersionId> = null
                                        let repositoryId = RepositoryId.Parse(parameters.RepositoryId)

                                        // Check for latest commit and latest root directory version from grace watch. If it's running, we know GraceStatus is up-to-date.
                                        match! getGraceWatchStatus () with
                                        | Some graceWatchStatus ->
                                            t0.Value <- 100.0
                                            t1.Value <- 100.0
                                            t2.Value <- 100.0
                                            t3.Value <- 100.0
                                            t4.Value <- 100.0
                                            t5.Value <- 100.0
                                            rootDirectoryId <- graceWatchStatus.RootDirectoryId
                                            rootDirectorySha256Hash <- graceWatchStatus.RootDirectorySha256Hash
                                            previousDirectoryIds <- graceWatchStatus.DirectoryIds
                                        | None ->
                                            let! previousGraceStatus = readGraceStatusFile ()
                                            let mutable graceStatus = previousGraceStatus
                                            t0.Value <- 100.0
                                            t1.StartTask()
                                            let! differences = scanForDifferences previousGraceStatus
                                            let! newFileVersions = copyUpdatedFilesToObjectCache t1 differences
                                            t1.Value <- 100.0

                                            t2.StartTask()

                                            let! (updatedGraceStatus, newDirectoryVersions) =
                                                getNewGraceStatusAndDirectoryVersions previousGraceStatus differences

                                            do! writeGraceStatusFile updatedGraceStatus
                                            rootDirectoryId <- updatedGraceStatus.RootDirectoryId
                                            rootDirectorySha256Hash <- updatedGraceStatus.RootDirectorySha256Hash
                                            previousDirectoryIds <- updatedGraceStatus.Index.Keys.ToHashSet()
                                            t2.Value <- 100.0

                                            t3.StartTask()

                                            let getUploadMetadataForFilesParameters =
                                                GetUploadMetadataForFilesParameters(
                                                    OwnerId = parameters.OwnerId,
                                                    OwnerName = parameters.OwnerName,
                                                    OrganizationId = parameters.OrganizationId,
                                                    OrganizationName = parameters.OrganizationName,
                                                    RepositoryId = parameters.RepositoryId,
                                                    RepositoryName = parameters.RepositoryName,
                                                    CorrelationId = getCorrelationId parseResult,
                                                    FileVersions =
                                                        (newFileVersions
                                                         |> Seq.map (fun localFileVersion -> localFileVersion.ToFileVersion)
                                                         |> Seq.toArray)
                                                )

                                            match! uploadFilesToObjectStorage getUploadMetadataForFilesParameters with
                                            | Ok returnValue -> ()
                                            | Error error -> logToAnsiConsole Colors.Error $"Failed to upload changed files to object storage. {error}"

                                            t3.Value <- 100.0

                                            t4.StartTask()

                                            if (newDirectoryVersions.Count > 0) then
                                                (task {
                                                    let saveParameters = SaveDirectoryVersionsParameters()
                                                    saveParameters.OwnerId <- parameters.OwnerId
                                                    saveParameters.OwnerName <- parameters.OwnerName
                                                    saveParameters.OrganizationId <- parameters.OrganizationId
                                                    saveParameters.OrganizationName <- parameters.OrganizationName
                                                    saveParameters.RepositoryId <- parameters.RepositoryId
                                                    saveParameters.RepositoryName <- parameters.RepositoryName
                                                    saveParameters.CorrelationId <- getCorrelationId parseResult
                                                    saveParameters.DirectoryVersions <- newDirectoryVersions.Select(fun dv -> dv.ToDirectoryVersion).ToList()

                                                    match! DirectoryVersion.SaveDirectoryVersions saveParameters with
                                                    | Ok returnValue -> ()
                                                    | Error error -> logToAnsiConsole Colors.Error $"Failed to upload new directory versions. {error}"
                                                })
                                                    .Wait()

                                            t4.Value <- 100.0

                                            t5.StartTask()

                                            if newDirectoryVersions.Count > 0 then
                                                (task {
                                                    match!
                                                        createSaveReference
                                                            (getRootDirectoryVersion updatedGraceStatus)
                                                            $"Created during `grace diff sha` operation."
                                                            (getCorrelationId parseResult)
                                                    with
                                                    | Ok saveReference -> ()
                                                    | Error error -> logToAnsiConsole Colors.Error $"Failed to create a save reference. {error}"
                                                })
                                                    .Wait()

                                            t5.Value <- 100.0

                                        // Check for latest reference of the given type from the server.
                                        t6.StartTask()

                                        let getDiffBySha256HashParameters =
                                            GetDiffBySha256HashParameters(
                                                OwnerId = $"{Current().OwnerId}",
                                                OrganizationId = $"{Current().OrganizationId}",
                                                RepositoryId = $"{Current().RepositoryId}",
                                                Sha256Hash1 = parameters.Sha256Hash1,
                                                Sha256Hash2 = parameters.Sha256Hash2,
                                                CorrelationId = parameters.CorrelationId
                                            )

                                        match! Diff.GetDiffBySha256Hash(getDiffBySha256HashParameters) with
                                        | Ok returnValue ->
                                            let diffDto = returnValue.ReturnValue
                                            printDiffResults diffDto
                                            t6.Value <- 100.0
                                        | Error error -> logToAnsiConsole Colors.Error $"Failed to get diff by sha256 hash. {error}"

                                        t6.Value <- 100.0
                                    })

                        for markup in markupList do
                            writeMarkup markup

                        return 0
                    else
                        // Do the thing here
                        return 0
                | Error error -> return (Error error) |> renderOutput parseResult
            }
            :> Task)

    let Build =
        let addCommonOptions (command: Command) =
            command
            |> addOption Options.ownerName
            |> addOption Options.ownerId
            |> addOption Options.organizationName
            |> addOption Options.organizationId
            |> addOption Options.repositoryName
            |> addOption Options.repositoryId

        let addBranchOptions (command: Command) = command |> addOption Options.branchName |> addOption Options.branchId

        let diffCommand = new Command("diff", Description = "Displays the difference between two versions of your repository.")

        let promotionCommand =
            new Command("promotion", Description = "Displays the difference between the promotion that this branch is based on and your current version.")
            |> addCommonOptions
            |> addBranchOptions

        promotionCommand.Action <- promotionHandler
        diffCommand.Subcommands.Add(promotionCommand)

        let commitCommand =
            new Command("commit", Description = "Displays the difference between the most recent commit and your current version.")
            |> addCommonOptions
            |> addBranchOptions

        commitCommand.Action <- commitHandler
        diffCommand.Subcommands.Add(commitCommand)

        let checkpointCommand =
            new Command("checkpoint", Description = "Displays the difference between the most recent checkpoint and your current version.")
            |> addCommonOptions
            |> addBranchOptions

        checkpointCommand.Action <- checkpointHandler
        diffCommand.Subcommands.Add(checkpointCommand)

        let saveCommand =
            new Command("save", Description = "Displays the difference between the most recent save and your current version.")
            |> addCommonOptions
            |> addBranchOptions

        saveCommand.Action <- saveHandler
        diffCommand.Subcommands.Add(saveCommand)

        let tagCommand =
            new Command("tag", Description = "Displays the difference between the specified tag and your current version.")
            |> addCommonOptions
            |> addBranchOptions
            |> addOption Options.tag

        tagCommand.Action <- tagHandler
        diffCommand.Subcommands.Add(tagCommand)

        let directoryIdCommand =
            new Command(
                "directoryid",
                Description =
                    "Displays the difference between two versions, specified by DirectoryId. If a second DirectoryId is not supplied, the current branch's root DirectoryId will be used."
            )
            |> addCommonOptions
            |> addOption Options.directoryVersionId1
            |> addOption Options.directoryVersionId2

        directoryIdCommand.Action <- DirectoryIdCommand
        diffCommand.Subcommands.Add(directoryIdCommand)

        let shaCommand =
            new Command(
                "sha",
                Description =
                    "Displays the difference between two versions, specified by partial or full SHA-256 hash. If a second SHA-256 value is not supplied, the current branch's root SHA-256 hash will be used."
            )
            |> addCommonOptions
            |> addOption Options.sha256Hash1
            |> addOption Options.sha256Hash2

        shaCommand.Action <- shaHandler
        diffCommand.Subcommands.Add(shaCommand)

        diffCommand
