namespace Grace.Cli.Command

open DiffPlex
open DiffPlex.DiffBuilder.Model
open Grace.Cli.Common
open Grace.Cli.Services
open Grace.SDK
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Dto.Branch
open Grace.Shared.Dto.Reference
open Grace.Shared.Parameters.Branch
open Grace.Shared.Parameters.Diff
open Grace.Shared.Parameters.Directory
open Grace.Shared.Types
open Grace.Shared.Utilities
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

module Diff =

    type CommonParameters() =
        inherit ParameterBase()
        member val public DirectoryId1: DirectoryId = Guid.Empty with get, set
        member val public DirectoryId2: DirectoryId = Guid.Empty with get, set
        member val public Sha256Hash1: Sha256Hash = String.Empty with get, set
        member val public Sha256Hash2: Sha256Hash = String.Empty with get, set

    module Options = 
        let directoryId1 = new Option<Guid>([|"--directoryId1"; "--d1"|], IsRequired = true, Description = "The first DirectoryId to compare in the diff.", Arity = ArgumentArity.ExactlyOne)
        let directoryId2 = new Option<Guid>([|"--directoryId2"; "--d2"|], IsRequired = false, Description = "The second DirectoryId to compare in the diff.", Arity = ArgumentArity.ExactlyOne)
        let sha256Hash1 = new Option<Sha256Hash>([|"--sha256Hash1"; "--s1"|], IsRequired = true, Description = "The first partial or full SHA-256 hash to compare in the diff.", Arity = ArgumentArity.ExactlyOne)
        let sha256Hash2 = new Option<Sha256Hash>([|"--sha256Hash2"; "--s2"|], IsRequired = false, Description = "The second partial or full SHA-256 hash to compare in the diff.", Arity = ArgumentArity.ExactlyOne)
        let tag = new Option<string>("--tag", IsRequired = true, Description = "The tag to compare the current version to.", Arity = ArgumentArity.ExactlyOne)

    let private CommonValidations parseResult parameters =
        let ``Sha256Hash1 must be a valid SHA-256 hash value`` (parseResult: ParseResult, parameters: CommonParameters) =
            if parseResult.CommandResult.FindResultFor(Options.sha256Hash1) <> null && not <| Constants.Sha256Regex.IsMatch(parameters.Sha256Hash1) then
                Error (GraceError.Create (DiffError.getErrorMessage InvalidSha256Hash) (parameters.CorrelationId))
            else
                Ok (parseResult, parameters)

        let ``Sha256Hash2 must be a valid SHA-256 hash value`` (parseResult: ParseResult, parameters: CommonParameters) =
            if parseResult.CommandResult.FindResultFor(Options.sha256Hash2) <> null && not <| Constants.Sha256Regex.IsMatch(parameters.Sha256Hash2) then
                Error (GraceError.Create (DiffError.getErrorMessage InvalidSha256Hash) (parameters.CorrelationId))
            else
                Ok (parseResult, parameters)
    
        let ``DirectoryId1 must not be Guid.Empty`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            if parseResult.CommandResult.FindResultFor(Options.directoryId1) <> null && commonParameters.DirectoryId1 = DirectoryId.Empty then 
                Error (GraceError.Create (DiffError.getErrorMessage InvalidDirectoryId) (commonParameters.CorrelationId))
            else
                Ok (parseResult, commonParameters)

        let ``DirectoryId2 must not be Guid.Empty`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            if parseResult.CommandResult.FindResultFor(Options.directoryId2) <> null && commonParameters.DirectoryId2 = DirectoryId.Empty then 
                Error (GraceError.Create (DiffError.getErrorMessage InvalidDirectoryId) (commonParameters.CorrelationId))
            else
                Ok (parseResult, commonParameters)

        // What the hell is happening here? Bind isn't working, but I believe this is a correctly-formed monad.
        (parseResult, parameters)
            |>  ``Sha256Hash1 must be a valid SHA-256 hash value``
            //>>= ``Sha256Hash2 must be a valid SHA-256 hash value``
            //>>= ``DirectoryId1 must not be Guid.Empty``
            //>>= ``DirectoryId2 must not be Guid.Empty``
    
    let private renderLine (diffLine: DiffPiece) = 
        if not <| diffLine.Position.HasValue then $"        {diffLine.Text.EscapeMarkup()}"
        else $"{diffLine.Position,6:D}: {diffLine.Text.EscapeMarkup()}"

    let private getMarkup (diffLine: DiffPiece) = 
        if diffLine.Type = ChangeType.Deleted then Markup($"[{Colors.Deleted}]-{renderLine diffLine}[/]")
        elif diffLine.Type = ChangeType.Inserted then Markup($"[{Colors.Added}]+{renderLine diffLine}[/]")
        elif diffLine.Type = ChangeType.Modified then Markup($"[{Colors.Changed}]~{renderLine diffLine}[/]")
        elif diffLine.Type = ChangeType.Imaginary then Markup($"[{Colors.Deemphasized}] {renderLine diffLine}[/]")
        elif diffLine.Type = ChangeType.Unchanged then Markup($"[{Colors.Important}] {renderLine diffLine}[/]")
        else Markup($"[{Colors.Important}] {diffLine.Text}[/]")
    
    /// Creates the text output for a diff to the most recent specific ReferenceType.
    let private diffToReference (parseResult: ParseResult) (parameters: CommonParameters) (referenceType: ReferenceType) = 
        task {
            if parseResult |> verbose then printParseResult parseResult
            let validateIncomingParameters = CommonValidations parseResult parameters
            match validateIncomingParameters with
            | Ok _ ->
                if parseResult |> showOutput then
                    let markupList = List<IRenderable>()
                    do! progress.Columns(progressColumns).StartAsync(fun progressContext ->
                        task {
                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Reading Grace index file.[/]")
                            let t1 = progressContext.AddTask($"[{Color.DodgerBlue1}]Scanning working directory for changes.[/]", autoStart = false)
                            let t2 = progressContext.AddTask($"[{Color.DodgerBlue1}]Creating new directory verions.[/]", autoStart = false)
                            let t3 = progressContext.AddTask($"[{Color.DodgerBlue1}]Uploading changed files to object storage.[/]", autoStart = false)
                            let t4 = progressContext.AddTask($"[{Color.DodgerBlue1}]Uploading new directory versions.[/]", autoStart = false)
                            let t5 = progressContext.AddTask($"[{Color.DodgerBlue1}]Getting {(discriminatedUnionCaseNameToString referenceType).ToLowerInvariant()}.[/]", autoStart = false)
                            let t6 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending diff request to server.[/]", autoStart = false)

                            let mutable rootDirectoryId = DirectoryId.Empty
                            let mutable rootDirectorySha256Hash = Sha256Hash String.Empty
                            let mutable previousDirectoryIds: HashSet<DirectoryId> = null
                            
                            // Check for latest commit and latest root directory version from grace watch. If it's running, we know GraceStatus is up-to-date.
                            match! getGraceWatchStatus() with
                            | Some graceWatchStatus ->
                                t0.Value <- 100.0
                                t1.Value <- 100.0
                                t2.Value <- 100.0
                                t3.Value <- 100.0
                                t4.Value <- 100.0
                                rootDirectoryId <- graceWatchStatus.RootDirectoryId
                                rootDirectorySha256Hash <- graceWatchStatus.RootDirectorySha256Hash
                                previousDirectoryIds <- graceWatchStatus.DirectoryIds 
                            | None -> 
                                let! previousGraceStatus = readGraceStatusFile()
                                let mutable graceStatus = previousGraceStatus
                                t0.Value <- 100.0
                                t1.StartTask()
                                let! differences = scanForDifferences previousGraceStatus
                                t1.Value <- 100.0

                                t2.StartTask()
                                let! (updatedGraceStatus, newDirectoryVersions) = getNewGraceStatusAndDirectoryVersions previousGraceStatus differences
                                rootDirectoryId <- updatedGraceStatus.RootDirectoryId
                                rootDirectorySha256Hash <- updatedGraceStatus.RootDirectorySha256Hash
                                previousDirectoryIds <- updatedGraceStatus.Index.Keys.ToHashSet()
                                t2.Value <- 100.0

                                t3.StartTask()
                                let updatedRelativePaths = 
                                    differences.Select(fun difference ->
                                        match difference.DifferenceType with
                                        | Add -> match difference.FileSystemEntryType with | FileSystemEntryType.File -> Some difference.RelativePath | FileSystemEntryType.Directory -> None
                                        | Change -> match difference.FileSystemEntryType with | FileSystemEntryType.File -> Some difference.RelativePath | FileSystemEntryType.Directory -> None
                                        | Delete -> None)
                                        .Where(fun relativePathOption -> relativePathOption.IsSome)
                                        .Select(fun relativePath -> relativePath.Value)

                                let newFileVersions = updatedRelativePaths.Select(fun relativePath -> 
                                    newDirectoryVersions.First(fun dv -> dv.Files.Exists(fun file -> file.RelativePath = relativePath)).Files.First(fun file -> file.RelativePath = relativePath))

                                let! uploadResult = uploadFilesToObjectStorage newFileVersions (getCorrelationId parseResult)
                                t3.Value <- 100.0

                                t4.StartTask()
                                let saveParameters = SaveDirectoryVersionsParameters()
                                saveParameters.DirectoryVersions <- newDirectoryVersions.Select(fun dv -> dv.ToDirectoryVersion).ToList()
                                let! uploadDirectoryVersions = Directory.SaveDirectoryVersions saveParameters
                                t4.Value <- 100.0

                            // Check for latest reference of the given type from the server.
                            t5.StartTask()
                            let getReferencesParameters = GetReferencesParameters(BranchId = $"{Current().BranchId}", OwnerId = $"{Current().OwnerId}", 
                                                    OrganizationId = $"{Current().OrganizationId}", RepositoryId = $"{Current().RepositoryId}", 
                                                    MaxCount = 1, CorrelationId = parameters.CorrelationId)

                            let! getAReferenceResult = 
                                task {
                                    match referenceType with
                                    | Merge -> 
                                        let merges = List<ReferenceDto>()
                                        // Merges are different, because we actually want the merge from the parent branch that this branch is based on.
                                        let branchParameters = Parameters.Branch.GetParameters(BranchId = $"{Current().BranchId}", OwnerId = $"{Current().OwnerId}", 
                                            OrganizationId = $"{Current().OrganizationId}", RepositoryId = $"{Current().RepositoryId}")
                                        match! Branch.Get(branchParameters) with
                                        | Ok returnValue ->
                                            let branchDto = returnValue.ReturnValue
                                            let getReferencesByIdParameters = Parameters.Repository.GetReferencesByReferenceIdParameters(OwnerId = $"{Current().OwnerId}", OrganizationId = $"{Current().OrganizationId}", 
                                                RepositoryId = $"{Current().RepositoryId}", ReferenceIds = [| branchDto.BasedOn |], CorrelationId = parameters.CorrelationId)
                                            match! Repository.GetReferencesByReferenceId(getReferencesByIdParameters) with
                                            | Ok returnValue ->
                                                if returnValue.ReturnValue.Count() > 0 then
                                                    // We're only taking the first one because we've only asked for one in the parameters.
                                                    let referenceDto = returnValue.ReturnValue.First()
                                                    merges.Add(referenceDto)
                                                    //logToAnsiConsole Colors.Verbose $"In diffToReference / Merge: Got merge reference from branchDto.BasedOn. ReferenceId: {referenceDto.ReferenceId}; Sha256Hash: {referenceDto.Sha256Hash}."
                                                else
                                                    logToAnsiConsole Colors.Verbose $"In diffToReference / Merge: Did not find the basedOn reference."
                                                    ()
                                            | Error error ->
                                                logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                                        | Error error ->
                                            logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                                        
                                        return Ok (GraceReturnValue.Create (merges :> IEnumerable<ReferenceDto>) parameters.CorrelationId) 
                                    | Commit -> return! Branch.GetCommits getReferencesParameters
                                    | Checkpoint -> return! Branch.GetCheckpoints getReferencesParameters
                                    | Save -> return! Branch.GetSaves getReferencesParameters
                                    | Tag -> return! Branch.GetTags getReferencesParameters
                                }

                            let latestReference = 
                                match getAReferenceResult with
                                | Ok returnValue ->
                                    let references = returnValue.ReturnValue
                                    if references.Count() > 0 then
                                        //logToAnsiConsole Colors.Verbose $"Got latest reference: {references.First().ReferenceText}; {references.First().CreatedAt}; {references.First().Sha256Hash}."
                                        references.First()
                                    else
                                        logToAnsiConsole Colors.Error $"Error getting latest reference. No matching references were found."
                                        ReferenceDto.Default
                                | Error error -> 
                                    logToAnsiConsole Colors.Error $"Error getting latest reference: {Markup.Escape(error.Error)}."
                                    ReferenceDto.Default
                            t5.Value <- 100.0

                            let addToOutput (markup: IRenderable) =
                                markupList.Add markup

                            let renderInlineDiff (inlineDiff: List<DiffPiece[]>) =
                                for i = 0 to inlineDiff.Count - 1 do
                                    for diffLine in inlineDiff[i] do
                                        addToOutput(getMarkup diffLine)
                                    if not <| (i = inlineDiff.Count - 1) then
                                        addToOutput(Markup($"[{Colors.Deemphasized}]-------[/]"))
                                    else
                                        addToOutput(Markup(String.Empty))

                            // Sending diff request to server.
                            t6.StartTask()
                            //logToAnsiConsole Colors.Verbose $"latestReference.DirectoryId: {latestReference.DirectoryId}; rootDirectoryId: {rootDirectoryId}."
                            let getDiffParameters = GetDiffParameters(DirectoryId1 = latestReference.DirectoryId, DirectoryId2 = rootDirectoryId)
                            let! getDiffResult = Diff.GetDiff(getDiffParameters)
                            match getDiffResult with
                            | Ok returnValue ->
                                let diffDto = returnValue.ReturnValue
                                if diffDto.HasDifferences then
                                    addToOutput (Markup($"[{Colors.Important}]Differences found.[/]"))
                                    for diff in diffDto.Differences do
                                        match diff.FileSystemEntryType with
                                        | FileSystemEntryType.File ->
                                            addToOutput(Markup($"[{Colors.Important}]{discriminatedUnionCaseNameToString diff.DifferenceType}[/] [{Colors.Highlighted}]{diff.RelativePath}[/]"))
                                        | FileSystemEntryType.Directory ->
                                            if diff.DifferenceType <> DifferenceType.Change then
                                                addToOutput(Markup($"[{Colors.Important}]{discriminatedUnionCaseNameToString diff.DifferenceType}[/] [{Colors.Highlighted}]{diff.RelativePath}[/]"))
                                    for fileDiff in diffDto.FileDiffs.OrderBy(fun fileDiff -> fileDiff.RelativePath) do
                                        //addToOutput ((new Rule($"[{Colors.Important}]{fileDiff.RelativePath}[/]")).LeftAligned())
                                        if fileDiff.CreatedAt1 > fileDiff.CreatedAt2 then
                                            //addToOutput ((new Rule($"[{Colors.Important}]{fileDiff.RelativePath} | {fileDiff.CreatedAt1 |> instantToLocalTime} ({fileDiff.CreatedAt1 |> ago}) {fileDiff.FileSha1.Substring(0, 8)} | {fileDiff.CreatedAt2 |> instantToLocalTime} ({fileDiff.CreatedAt2 |> ago}) {fileDiff.FileSha2.Substring(0, 8)}[/]")).LeftAligned())
                                            addToOutput ((new Rule($"[{Colors.Important}]{fileDiff.RelativePath} | {fileDiff.FileSha1.Substring(0, 8)} - {fileDiff.CreatedAt1 |> ago} | {fileDiff.FileSha2.Substring(0, 8)} - {fileDiff.CreatedAt2 |> ago}[/]")).LeftJustified())
                                            //addToOutput (Markup($"[{Colors.Important}]   {fileDiff.CreatedAt1 |> instantToLocalTime} ({fileDiff.CreatedAt1 |> ago}) {fileDiff.FileSha1.Substring(0, 8)} vs. {fileDiff.CreatedAt2 |> instantToLocalTime} ({fileDiff.CreatedAt2 |> ago}) {fileDiff.FileSha2.Substring(0, 8)}[/]"))
                                        else
                                            //addToOutput ((new Rule($"[{Colors.Important}]{fileDiff.RelativePath} | {fileDiff.CreatedAt1 |> instantToLocalTime} ({fileDiff.CreatedAt1 |> ago}) {fileDiff.FileSha1.Substring(0, 8)} | {fileDiff.CreatedAt2 |> instantToLocalTime} ({fileDiff.CreatedAt2 |> ago}) {fileDiff.FileSha2.Substring(0, 8)}[/]")).LeftAligned())
                                            addToOutput ((new Rule($"[{Colors.Important}]{fileDiff.RelativePath} | {fileDiff.FileSha2.Substring(0, 8)} - {fileDiff.CreatedAt2 |> ago} | {fileDiff.FileSha1.Substring(0, 8)} - {fileDiff.CreatedAt1 |> ago}[/]")).LeftJustified())
                                            //addToOutput (Markup($"[{Colors.Important}]   {fileDiff.CreatedAt2 |> instantToLocalTime} ({fileDiff.CreatedAt2 |> ago}) {fileDiff.FileSha2.Substring(0, 8)} vs. {fileDiff.CreatedAt1 |> instantToLocalTime} ({fileDiff.CreatedAt1 |> ago}) {fileDiff.FileSha1.Substring(0, 8)}[/]"))

                                        if fileDiff.IsBinary then
                                            addToOutput (Markup($"[{Colors.Important}]Binary file.[/]"))
                                        else
                                            renderInlineDiff fileDiff.InlineDiff
                                else
                                    logToAnsiConsole Colors.Highlighted $"No differences found."
                            | Error error ->
                                let s = StringExtensions.EscapeMarkup($"{error.Error}")
                                logToAnsiConsole Colors.Error $"Error submitting diff: {s}"
                            t6.Increment(100.0)
                            //AnsiConsole.MarkupLine($"[{Colors.Important}]Differences: {differences.Count}.[/]")
                            //AnsiConsole.MarkupLine($"[{Colors.Error}]{error.Error.EscapeMarkup()}[/]")
                        })

                    for markup in markupList do
                        writeMarkup markup

                    return 0
                else
                    // Do the thing here
                    return 0
            | Error error ->
                return (Error error) |> renderOutput parseResult
        }

    let private mergeHandler = 
        CommandHandler.Create(fun (parseResult: ParseResult) (parameters: CommonParameters) ->
            task {
                return! diffToReference parseResult parameters ReferenceType.Merge
            } :> Task)


    let private commitHandler = 
        CommandHandler.Create(fun (parseResult: ParseResult) (parameters: CommonParameters) ->
            task {
                return! diffToReference parseResult parameters ReferenceType.Commit
            } :> Task)

    let private checkpointHandler = 
        CommandHandler.Create(fun (parseResult: ParseResult) (parameters: CommonParameters) ->
            task {
                return! diffToReference parseResult parameters ReferenceType.Checkpoint
            } :> Task)

    let private saveHandler = 
        CommandHandler.Create(fun (parseResult: ParseResult) (parameters: CommonParameters) ->
            task {
                return! diffToReference parseResult parameters ReferenceType.Save
            } :> Task)

    let private tagHandler = 
        CommandHandler.Create(fun (parseResult: ParseResult) (parameters: CommonParameters) ->
            task {
                return! diffToReference parseResult parameters ReferenceType.Tag
            } :> Task)

    type DirectoryIdParameters() =
        inherit CommonParameters()
        member val public DirectoryId1 = DirectoryId.Empty with get, set
        member val public DirectoryId2 = DirectoryId.Empty with get, set
    let private DirectoryIdCommand =
        CommandHandler.Create(fun (parseResult: ParseResult) (parameters: DirectoryIdParameters) ->
            task {
                return 0
            } :> Task)

    type ShaParameters() =
        inherit CommonParameters()
        member val public Sha256Hash1 = Sha256Hash String.Empty with get, set
        member val public Sha256Hash2 = Sha256Hash String.Empty with get, set
    let private ShaCommand =
        CommandHandler.Create(fun (parseResult: ParseResult) (parameters: ShaParameters) ->
            task {
                return 0
            } :> Task)

    let Build = 
        let diffCommand = new Command("diff", Description = "Displays the difference between two versions of your repository.") 

        let mergeCommand = new Command("merge", Description = "Displays the difference between the merge that this branch is based on and your current version.") 
        mergeCommand.Handler <- mergeHandler
        diffCommand.AddCommand(mergeCommand)

        let commitCommand = new Command("commit", Description = "Displays the difference between the most recent commit and your current version.") 
        commitCommand.Handler <- commitHandler
        diffCommand.AddCommand(commitCommand)

        let checkpointCommand = new Command("checkpoint", Description = "Displays the difference between the most recent checkpoint and your current version.") 
        checkpointCommand.Handler <- checkpointHandler
        diffCommand.AddCommand(checkpointCommand)

        let saveCommand = new Command("save", Description = "Displays the difference between the most recent save and your current version.") 
        saveCommand.Handler <- saveHandler
        diffCommand.AddCommand(saveCommand)
        
        let tagCommand = new Command("tag", Description = "Displays the difference between the specified tag and your current version.") |> addOption Options.tag
        tagCommand.Handler <- tagHandler
        diffCommand.AddCommand(tagCommand)
        
        let directoryIdCommand = new Command("directoryid", Description = "Displays the difference between two versions specified by DirectoryId. If a second DirectoryId is not supplied, the repository's current root DirectoryId will be used.") |> addOption Options.directoryId1 |> addOption Options.directoryId2
        directoryIdCommand.Handler <- DirectoryIdCommand
        diffCommand.AddCommand(directoryIdCommand)
        
        let shaCommand = new Command("sha", Description = "Displays the difference between two versions specified by partial or full SHA-256 hash. If a second SHA-256 value is not supplied, the repository's current root SHA-256 hash will be used.") |> addOption Options.sha256Hash1 |> addOption Options.sha256Hash2
        shaCommand.Handler <- ShaCommand
        diffCommand.AddCommand(shaCommand)
        
        diffCommand
