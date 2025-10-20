namespace Grace.CLI.Command

open DiffPlex
open DiffPlex.DiffBuilder.Model
open FSharpPlus
open Grace.CLI.Common
open Grace.CLI.Services
open Grace.CLI.Text
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
open Grace.Shared.Validation.Errors
open Spectre.Console
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.CommandLine
open System.CommandLine.Invocation
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

    module private Options =
        let ownerId =
            new Option<Guid>(
                OptionName.OwnerId,
                Required = false,
                Description = "The repository's owner ID <Guid>.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> Current().OwnerId)
            )

        let ownerName =
            new Option<string>(
                OptionName.OwnerName,
                Required = false,
                Description = "The repository's owner name. [default: current owner]",
                Arity = ArgumentArity.ExactlyOne
            )

        let organizationId =
            new Option<Guid>(
                OptionName.OrganizationId,
                Required = false,
                Description = "The repository's organization ID <Guid>.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> Current().OrganizationId)
            )

        let organizationName =
            new Option<string>(
                OptionName.OrganizationName,
                Required = false,
                Description = "The repository's organization name. [default: current organization]",
                Arity = ArgumentArity.ZeroOrOne
            )

        let repositoryId =
            new Option<Guid>(
                OptionName.RepositoryId,
                [| "-r" |],
                Required = false,
                Description = "The repository's Id <Guid>.",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> Current().RepositoryId)
            )

        let repositoryName =
            new Option<string>(
                OptionName.RepositoryName,
                [| "-n" |],
                Required = false,
                Description = "The name of the repository. [default: current repository]",
                Arity = ArgumentArity.ExactlyOne
            )

        let branchId =
            new Option<Guid>(
                OptionName.BranchId,
                [| "-i" |],
                Required = false,
                Description = "The branch's ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> Current().BranchId)
            )

        let branchName =
            new Option<string>(
                OptionName.BranchName,
                [| "-b" |],
                Required = false,
                Description = "The name of the branch. [default: current branch]",
                Arity = ArgumentArity.ExactlyOne
            )

        let directoryVersionId1 =
            new Option<Guid>(
                OptionName.DirectoryVersionId1,
                [| OptionName.D1 |],
                Required = true,
                Description = "The first DirectoryId to compare in the diff.",
                Arity = ArgumentArity.ExactlyOne
            )

        let directoryVersionId2 =
            new Option<Guid>(
                OptionName.DirectoryVersionId2,
                [| OptionName.D2 |],
                Required = false,
                Description = "The second DirectoryId to compare in the diff.",
                Arity = ArgumentArity.ExactlyOne
            )

        let sha256Hash1 =
            new Option<Sha256Hash>(
                OptionName.Sha256Hash1,
                [| OptionName.S1 |],
                Required = true,
                Description = "The first partial or full SHA-256 hash to compare in the diff.",
                Arity = ArgumentArity.ExactlyOne
            )

        let sha256Hash2 =
            new Option<Sha256Hash>(
                OptionName.Sha256Hash2,
                [| OptionName.S2 |],
                Required = false,
                Description = "The second partial or full SHA-256 hash to compare in the diff.",
                Arity = ArgumentArity.ExactlyOne
            )

        let tag = new Option<string>(OptionName.Tag, Required = true, Description = "The tag to compare the current version to.", Arity = ArgumentArity.ExactlyOne)

    let private sha256Validations parseResult =
        let graceIds = getNormalizedIdsAndNames parseResult

        let ``Sha256Hash1 must be a valid SHA-256 hash value`` (parseResult: ParseResult) =
            if
                parseResult.GetResult(Options.sha256Hash1) <> null
                && not <| Constants.Sha256Regex.IsMatch(parseResult.GetValue(Options.sha256Hash1))
            then
                let properties = Dictionary<string, obj>()
                properties.Add("repositoryId", graceIds.RepositoryId)
                properties.Add("sha256Hash1", parseResult.GetValue(Options.sha256Hash1))
                properties.Add("sha256Hash2", parseResult.GetValue(Options.sha256Hash2))

                Error(GraceError.CreateWithMetadata null (getErrorMessage DiffError.InvalidSha256Hash) (graceIds.CorrelationId) properties)
            else
                Ok parseResult

        let ``Sha256Hash2 must be a valid SHA-256 hash value`` (parseResult: ParseResult) =
            if
                parseResult.GetResult(Options.sha256Hash2) <> null
                && not <| Constants.Sha256Regex.IsMatch(parseResult.GetValue(Options.sha256Hash2))
            then
                let properties = Dictionary<string, obj>()
                properties.Add("repositoryId", graceIds.RepositoryId)
                properties.Add("sha256Hash1", parseResult.GetValue(Options.sha256Hash1))
                properties.Add("sha256Hash2", parseResult.GetValue(Options.sha256Hash2))

                Error(GraceError.Create (getErrorMessage DiffError.InvalidSha256Hash) (graceIds.CorrelationId))
            else
                Ok parseResult

        parseResult
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
        member val public BranchId = String.Empty with get, set
        member val public BranchName = BranchName String.Empty with get, set

    let private diffToReferenceType (parseResult: ParseResult) (referenceType: ReferenceType) =
        task {
            if parseResult |> verbose then printParseResult parseResult

            let validateIncomingParameters = parseResult |> Validations.CommonValidations

            match validateIncomingParameters with
            | Ok _ ->
                let graceIds = getNormalizedIdsAndNames parseResult

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
                                    let repositoryId = graceIds.RepositoryId

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
                                                OwnerId = graceIds.OwnerIdString,
                                                OwnerName = graceIds.OwnerName,
                                                OrganizationId = graceIds.OrganizationIdString,
                                                OrganizationName = graceIds.OrganizationName,
                                                RepositoryId = graceIds.RepositoryIdString,
                                                RepositoryName = graceIds.RepositoryName,
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
                                                saveParameters.OwnerId <- graceIds.OwnerIdString
                                                saveParameters.OwnerName <- graceIds.OwnerName
                                                saveParameters.OrganizationId <- graceIds.OrganizationIdString
                                                saveParameters.OrganizationName <- graceIds.OrganizationName
                                                saveParameters.RepositoryId <- graceIds.RepositoryIdString
                                                saveParameters.RepositoryName <- graceIds.RepositoryName
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
                                            OwnerId = graceIds.OwnerIdString,
                                            OwnerName = graceIds.OwnerName,
                                            OrganizationId = graceIds.OrganizationIdString,
                                            OrganizationName = graceIds.OrganizationName,
                                            RepositoryId = graceIds.RepositoryIdString,
                                            RepositoryName = graceIds.RepositoryName,
                                            BranchId = graceIds.BranchIdString,
                                            BranchName = graceIds.BranchName,
                                            MaxCount = 1,
                                            CorrelationId = graceIds.CorrelationId
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
                                                        OwnerId = graceIds.OwnerIdString,
                                                        OwnerName = graceIds.OwnerName,
                                                        OrganizationId = graceIds.OrganizationIdString,
                                                        OrganizationName = graceIds.OrganizationName,
                                                        RepositoryId = graceIds.RepositoryIdString,
                                                        RepositoryName = graceIds.RepositoryName,
                                                        BranchId = graceIds.BranchIdString,
                                                        BranchName = graceIds.BranchName,
                                                        CorrelationId = graceIds.CorrelationId
                                                    )

                                                match! Branch.Get(branchParameters) with
                                                | Ok returnValue ->
                                                    let branchDto = returnValue.ReturnValue
                                                    promotions.Add(branchDto.BasedOn)
                                                | Error error ->
                                                    logToAnsiConsole Colors.Error (Markup.Escape($"Error in Branch.Get: {error}"))

                                                    if parseResult |> json || parseResult |> verbose then
                                                        logToAnsiConsole Colors.Verbose (serialize error)

                                                return Ok(GraceReturnValue.Create (promotions.ToArray()) graceIds.CorrelationId)
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
                                            OwnerId = graceIds.OwnerIdString,
                                            OwnerName = graceIds.OwnerName,
                                            OrganizationId = graceIds.OrganizationIdString,
                                            OrganizationName = graceIds.OrganizationName,
                                            RepositoryId = graceIds.RepositoryIdString,
                                            RepositoryName = graceIds.RepositoryName,
                                            DirectoryVersionId1 = latestReference.DirectoryId,
                                            DirectoryVersionId2 = rootDirectoryId,
                                            CorrelationId = graceIds.CorrelationId
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

    type PromotionHandler() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task { return! diffToReferenceType parseResult ReferenceType.Promotion }

    type CommitHandler() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task { return! diffToReferenceType parseResult ReferenceType.Commit }

    type CheckpointHandler() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task { return! diffToReferenceType parseResult ReferenceType.Checkpoint }


    type SaveHandler() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task { return! diffToReferenceType parseResult ReferenceType.Save }

    type TagHandler() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task { return! diffToReferenceType parseResult ReferenceType.Tag }

    type DirectoryIdHandler() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                if parseResult |> verbose then printParseResult parseResult

                let validateIncomingParameters = parseResult |> Validations.CommonValidations

                match validateIncomingParameters with
                | Ok _ -> return 0
                | Error error -> return (Error error) |> renderOutput parseResult
            }

    type ShaHandler() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                if parseResult |> verbose then printParseResult parseResult

                let validateIncomingParameters = parseResult |> Validations.CommonValidations

                match validateIncomingParameters with
                | Ok _ ->
                    let graceIds = getNormalizedIdsAndNames parseResult

                    if parseResult |> hasOutput then
                        do!
                            progress
                                .Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                    task {
                                        let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Reading Grace index file.[/]")

                                        let t1 = progressContext.AddTask($"[{Color.DodgerBlue1}]Scanning working directory for changes.[/]", autoStart = false)

                                        let t2 = progressContext.AddTask($"[{Color.DodgerBlue1}]Creating new directory verions.[/]", autoStart = false)

                                        let t3 =
                                            progressContext.AddTask($"[{Color.DodgerBlue1}]Uploading changed files to object storage.[/]", autoStart = false)

                                        let t4 = progressContext.AddTask($"[{Color.DodgerBlue1}]Uploading new directory versions.[/]", autoStart = false)

                                        let t5 = progressContext.AddTask($"[{Color.DodgerBlue1}]Creating a save reference.[/]", autoStart = false)

                                        let t6 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending diff request to server.[/]", autoStart = false)

                                        let mutable rootDirectoryId = DirectoryVersionId.Empty
                                        let mutable rootDirectorySha256Hash = Sha256Hash String.Empty
                                        let mutable previousDirectoryIds: HashSet<DirectoryVersionId> = null

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
                                                    OwnerId = graceIds.OwnerIdString,
                                                    OwnerName = graceIds.OwnerName,
                                                    OrganizationId = graceIds.OrganizationIdString,
                                                    OrganizationName = graceIds.OrganizationName,
                                                    RepositoryId = graceIds.RepositoryIdString,
                                                    RepositoryName = graceIds.RepositoryName,
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
                                                    saveParameters.OwnerId <- graceIds.OwnerIdString
                                                    saveParameters.OwnerName <- graceIds.OwnerName
                                                    saveParameters.OrganizationId <- graceIds.OrganizationIdString
                                                    saveParameters.OrganizationName <- graceIds.OrganizationName
                                                    saveParameters.RepositoryId <- graceIds.RepositoryIdString
                                                    saveParameters.RepositoryName <- graceIds.RepositoryName
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

                                        let sha256Hash1 = parseResult.GetValue(Options.sha256Hash1)
                                        let sha256Hash2 = parseResult.GetValue(Options.sha256Hash2)

                                        let getDiffBySha256HashParameters =
                                            GetDiffBySha256HashParameters(
                                                OwnerId = graceIds.OwnerIdString,
                                                OwnerName = graceIds.OwnerName,
                                                OrganizationId = graceIds.OrganizationIdString,
                                                OrganizationName = graceIds.OrganizationName,
                                                RepositoryId = graceIds.RepositoryIdString,
                                                RepositoryName = graceIds.RepositoryName,
                                                Sha256Hash1 = sha256Hash1,
                                                Sha256Hash2 = sha256Hash2,
                                                CorrelationId = graceIds.CorrelationId
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

        promotionCommand.Action <- PromotionHandler()
        diffCommand.Subcommands.Add(promotionCommand)

        let commitCommand =
            new Command("commit", Description = "Displays the difference between the most recent commit and your current version.")
            |> addCommonOptions
            |> addBranchOptions

        commitCommand.Action <- CommitHandler()
        diffCommand.Subcommands.Add(commitCommand)

        let checkpointCommand =
            new Command("checkpoint", Description = "Displays the difference between the most recent checkpoint and your current version.")
            |> addCommonOptions
            |> addBranchOptions

        checkpointCommand.Action <- CheckpointHandler()
        diffCommand.Subcommands.Add(checkpointCommand)

        let saveCommand =
            new Command("save", Description = "Displays the difference between the most recent save and your current version.")
            |> addCommonOptions
            |> addBranchOptions

        saveCommand.Action <- SaveHandler()
        diffCommand.Subcommands.Add(saveCommand)

        let tagCommand =
            new Command("tag", Description = "Displays the difference between the specified tag and your current version.")
            |> addCommonOptions
            |> addBranchOptions
            |> addOption Options.tag

        tagCommand.Action <- TagHandler()
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

        directoryIdCommand.Action <- DirectoryIdHandler()
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

        shaCommand.Action <- ShaHandler()
        diffCommand.Subcommands.Add(shaCommand)

        diffCommand
