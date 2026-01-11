namespace Grace.CLI.Command

open FSharpPlus
open Grace.CLI.Common
open Grace.CLI.Common.Validations
open Grace.CLI.Services
open Grace.CLI.Text
open Grace.SDK
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Validation.Errors
open Grace.Shared.Parameters.Branch
open Grace.Shared.Parameters.DirectoryVersion
open Grace.Shared.Parameters.Storage
open Grace.Shared.Services
open Grace.Types.Branch
open Grace.Types.Reference
open Grace.Types.Types
open Grace.Shared.Utilities
open NodaTime
open NodaTime.TimeZones
open Spectre.Console
open Spectre.Console.Json
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.CommandLine
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.Globalization
open System.IO
open System.IO.Enumeration
open System.Linq
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open System.Text
open System.Text.Json

module Reference =
    open Grace.Shared.Validation.Common.Input

    module private Options =

        let branchId =
            new Option<Guid>(
                OptionName.BranchId,
                [| "-i" |],
                Required = false,
                Description = "The branch's ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> BranchId.Empty)
            )

        let branchName =
            new Option<String>(
                OptionName.BranchName,
                [| "-b" |],
                Required = false,
                Description = "The name of the branch. [default: current branch]",
                Arity = ArgumentArity.ExactlyOne
            )

        let branchNameRequired =
            new Option<String>(OptionName.BranchName, [| "-b" |], Required = true, Description = "The name of the branch.", Arity = ArgumentArity.ExactlyOne)

        let ownerId =
            new Option<OwnerId>(
                OptionName.OwnerId,
                Required = false,
                Description = "The repository's owner ID <Guid>.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> OwnerId.Empty)
            )

        let ownerName =
            new Option<String>(
                OptionName.OwnerName,
                Required = false,
                Description = "The repository's owner name. [default: current owner]",
                Arity = ArgumentArity.ExactlyOne
            )

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
                Description = "The repository's organization name. [default: current organization]",
                Arity = ArgumentArity.ZeroOrOne
            )

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
                Description = "The name of the repository. [default: current repository]",
                Arity = ArgumentArity.ExactlyOne
            )

        let parentBranchId =
            new Option<BranchId>(
                OptionName.ParentBranchId,
                [||],
                Required = false,
                Description = "The parent branch's ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne
            )

        let parentBranchName =
            new Option<String>(
                OptionName.ParentBranchName,
                [||],
                Required = false,
                Description = "The name of the parent branch. [default: current branch]",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> String.Empty)
            )

        let newName = new Option<String>(OptionName.NewName, Required = true, Description = "The new name of the branch.", Arity = ArgumentArity.ExactlyOne)

        let message =
            new Option<String>(
                OptionName.Message,
                [| "-m" |],
                Required = false,
                Description = "The text to store with this reference.",
                Arity = ArgumentArity.ExactlyOne
            )

        let messageRequired =
            new Option<String>(
                OptionName.Message,
                [| "-m" |],
                Required = true,
                Description = "The text to store with this reference.",
                Arity = ArgumentArity.ExactlyOne
            )

        let referenceType =
            (new Option<String>(OptionName.ReferenceType, Required = false, Description = "The type of reference.", Arity = ArgumentArity.ExactlyOne))
                .AcceptOnlyFromAmong(listCases<ReferenceType> ())

        let fullSha =
            new Option<bool>(OptionName.FullSha, Required = false, Description = "Show the full SHA-256 value in output.", Arity = ArgumentArity.ZeroOrOne)

        let maxCount =
            new Option<int>(
                OptionName.MaxCount,
                Required = false,
                Description = "The maximum number of results to return.",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> 30)
            )

        let referenceId =
            new Option<ReferenceId>(OptionName.ReferenceId, [||], Required = false, Description = "The reference ID <Guid>.", Arity = ArgumentArity.ExactlyOne)

        let sha256Hash =
            new Option<String>(
                OptionName.Sha256Hash,
                [||],
                Required = false,
                Description = "The full or partial SHA-256 hash value of the version.",
                Arity = ArgumentArity.ExactlyOne
            )

        let enabled =
            new Option<bool>(
                OptionName.Enabled,
                Required = false,
                Description = "True to enable the feature; false to disable it.",
                Arity = ArgumentArity.ZeroOrOne
            )

        let includeDeleted =
            new Option<bool>(OptionName.IncludeDeleted, [| "-d" |], Required = false, Description = "Include deleted branches in the result. [default: false]")

        let showEvents =
            new Option<bool>(OptionName.ShowEvents, [| "-e" |], Required = false, Description = "Include actor events in the result. [default: false]")

        let directoryVersionId =
            new Option<DirectoryVersionId>(
                OptionName.DirectoryVersionId,
                [| "-v" |],
                Required = false,
                Description = "The directory version ID to assign to the promotion <Guid>.",
                Arity = ArgumentArity.ExactlyOne
            )

    let private valueOrEmpty (value: string) = if String.IsNullOrWhiteSpace(value) then String.Empty else value

    let private ReferenceValidations (parseResult: ParseResult) =

        let ensureFileExists path error (parseResult: ParseResult) =
            if File.Exists(path) then
                Ok parseResult
            else
                Error(GraceError.Create (getErrorMessage error) (parseResult |> getCorrelationId))

        parseResult
        |> ensureFileExists (Current().GraceStatusFile) ReferenceError.IndexFileNotFound
        >>= ensureFileExists (Current().GraceObjectCacheFile) ReferenceError.ObjectCacheFileNotFound

    let printContents (parseResult: ParseResult) (directoryVersions: IEnumerable<DirectoryVersion>) =
        let longestRelativePath =
            getLongestRelativePath (
                directoryVersions
                |> Seq.map (fun directoryVersion -> directoryVersion.ToLocalDirectoryVersion(DateTime.UtcNow))
            )
        //logToAnsiConsole Colors.Verbose $"In printContents: getLongestRelativePath: {longestRelativePath}"
        let additionalSpaces = String.replicate (longestRelativePath - 2) " "
        let additionalImportantDashes = String.replicate (longestRelativePath + 3) "-"
        let additionalDeemphasizedDashes = String.replicate (38) "-"

        directoryVersions
        |> Seq.iteri (fun i directoryVersion ->
            AnsiConsole.WriteLine()

            if i = 0 then
                AnsiConsole.MarkupLine(
                    $"[{Colors.Important}]Created At                   SHA-256            Size  Path{additionalSpaces}[/][{Colors.Deemphasized}] (DirectoryVersionId)[/]"
                )

                AnsiConsole.MarkupLine(
                    $"[{Colors.Important}]-----------------------------------------------------{additionalImportantDashes}[/][{Colors.Deemphasized}] {additionalDeemphasizedDashes}[/]"
                )
            //logToAnsiConsole Colors.Verbose $"In printContents: directoryVersion.RelativePath: {directoryVersion.RelativePath}"
            let rightAlignedDirectoryVersionId =
                (String.replicate
                    (longestRelativePath
                     - directoryVersion.RelativePath.Length)
                    " ")
                + $"({directoryVersion.DirectoryVersionId})"

            AnsiConsole.MarkupLine(
                $"[{Colors.Highlighted}]{formatInstantAligned directoryVersion.CreatedAt}   {getShortSha256Hash directoryVersion.Sha256Hash}  {directoryVersion.Size, 13:N0}  /{directoryVersion.RelativePath}[/] [{Colors.Deemphasized}] {rightAlignedDirectoryVersionId}[/]"
            )
            //if parseResult.CommandResult.Command.Options.Contains(Options.listFiles) then
            let sortedFiles = directoryVersion.Files.OrderBy(fun f -> f.RelativePath)

            for file in sortedFiles do
                AnsiConsole.MarkupLine(
                    $"[{Colors.Verbose}]{formatInstantAligned file.CreatedAt}   {getShortSha256Hash file.Sha256Hash}  {file.Size, 13:N0}  |- {file.RelativePath.Split('/').LastOrDefault()}[/]"
                ))

    type GetRecursiveSize() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult

                    let validateIncomingParameters =
                        parseResult
                        |> CommonValidations
                        >>= ReferenceValidations

                    let graceIds = parseResult |> getNormalizedIdsAndNames

                    match validateIncomingParameters with
                    | Ok _ ->
                        let referenceId =
                            if
                                not
                                <| isNull (parseResult.GetResult(Options.referenceId))
                            then
                                parseResult
                                    .GetValue(Options.referenceId)
                                    .ToString()
                            else
                                String.Empty

                        let sha256Hash = parseResult.GetValue(Options.sha256Hash)

                        let sdkParameters =
                            Parameters.Branch.ListContentsParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                BranchId = graceIds.BranchIdString,
                                BranchName = graceIds.BranchName,
                                Sha256Hash = sha256Hash,
                                ReferenceId = referenceId,
                                Pattern = String.Empty,
                                ShowDirectories = true,
                                ShowFiles = true,
                                ForceRecompute = false,
                                CorrelationId = graceIds.CorrelationId
                            )

                        let! result =
                            if parseResult |> hasOutput then
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! response = Grace.SDK.Branch.GetRecursiveSize(sdkParameters)
                                            t0.Increment(100.0)
                                            return response
                                        })
                            else
                                Grace.SDK.Branch.GetRecursiveSize(sdkParameters)

                        match result with
                        | Ok returnValue -> AnsiConsole.MarkupLine $"[{Colors.Highlighted}]Total file size: {returnValue.ReturnValue:N0}[/]"
                        | Error error -> AnsiConsole.MarkupLine $"[{Colors.Error}]{error}[/]"

                        return result |> renderOutput parseResult
                    | Error error ->
                        return
                            GraceResult.Error error
                            |> renderOutput parseResult
                with
                | ex ->
                    let graceError = GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)

                    return renderOutput parseResult (GraceResult.Error graceError)
            }

    type ListContents() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult

                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> ReferenceValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let referenceId =
                            if
                                not
                                <| isNull (parseResult.GetResult(Options.referenceId))
                            then
                                parseResult
                                    .GetValue(Options.referenceId)
                                    .ToString()
                            else
                                String.Empty

                        let sha256Hash = parseResult.GetValue(Options.sha256Hash)

                        let sdkParameters =
                            Parameters.Branch.ListContentsParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                BranchId = graceIds.BranchIdString,
                                BranchName = graceIds.BranchName,
                                Sha256Hash = sha256Hash,
                                ReferenceId = referenceId,
                                Pattern = String.Empty,
                                ShowDirectories = true,
                                ShowFiles = true,
                                ForceRecompute = false,
                                CorrelationId = graceIds.CorrelationId
                            )

                        let! result =
                            if parseResult |> hasOutput then
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! response = Grace.SDK.Branch.ListContents(sdkParameters)
                                            t0.Increment(100.0)
                                            return response
                                        })
                            else
                                Grace.SDK.Branch.ListContents(sdkParameters)

                        match result with
                        | Ok returnValue ->
                            let! graceStatus = readGraceStatusFile ()

                            let directoryVersions =
                                returnValue
                                    .ReturnValue
                                    .Select(fun directoryVersionDto -> directoryVersionDto.DirectoryVersion)
                                    .OrderBy(fun dv -> dv.RelativePath)

                            let directoryCount = directoryVersions.Count()

                            let fileCount =
                                directoryVersions
                                    .Select(fun directoryVersion -> directoryVersion.Files.Count)
                                    .Sum()

                            let totalFileSize = directoryVersions.Sum(fun directoryVersion -> directoryVersion.Files.Sum(fun f -> f.Size))

                            let rootDirectoryVersion = directoryVersions.First(fun d -> d.RelativePath = Constants.RootDirectoryPath)

                            AnsiConsole.MarkupLine($"[{Colors.Important}]All values taken from the selected version of this branch from the server.[/]")
                            AnsiConsole.MarkupLine($"[{Colors.Highlighted}]Number of directories: {directoryCount}.[/]")
                            AnsiConsole.MarkupLine($"[{Colors.Highlighted}]Number of files: {fileCount}; total file size: {totalFileSize:N0}.[/]")
                            AnsiConsole.MarkupLine($"[{Colors.Highlighted}]Root SHA-256 hash: {rootDirectoryVersion.Sha256Hash.Substring(0, 8)}[/]")

                            printContents parseResult directoryVersions
                            return result |> renderOutput parseResult
                        | Error _ -> return result |> renderOutput parseResult
                    | Error error ->
                        return
                            GraceResult.Error error
                            |> renderOutput parseResult
                with
                | ex ->
                    let graceError = GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)

                    return renderOutput parseResult (GraceResult.Error graceError)
            }

    type Assign() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult

                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> ReferenceValidations

                    let directoryVersionId =
                        if
                            not
                            <| isNull (parseResult.GetResult(Options.directoryVersionId))
                        then
                            parseResult.GetValue(Options.directoryVersionId)
                        else
                            Guid.Empty

                    let sha256Hash = parseResult.GetValue(Options.sha256Hash)

                    match validateIncomingParameters with
                    | Ok _ ->
                        match (directoryVersionId, sha256Hash) with
                        | (directoryVersionId, sha256Hash) when
                            directoryVersionId = Guid.Empty
                            && sha256Hash = String.Empty
                            ->
                            let error =
                                GraceError.Create
                                    (getErrorMessage ReferenceError.EitherDirectoryVersionIdOrSha256HashRequired)
                                    (parseResult |> getCorrelationId)

                            return Error error |> renderOutput parseResult
                        | _ ->
                            let assignParameters =
                                Parameters.Branch.AssignParameters(
                                    OwnerId = graceIds.OwnerIdString,
                                    OwnerName = graceIds.OwnerName,
                                    OrganizationId = graceIds.OrganizationIdString,
                                    OrganizationName = graceIds.OrganizationName,
                                    RepositoryId = graceIds.RepositoryIdString,
                                    RepositoryName = graceIds.RepositoryName,
                                    BranchId = graceIds.BranchIdString,
                                    BranchName = graceIds.BranchName,
                                    DirectoryVersionId = directoryVersionId,
                                    Sha256Hash = sha256Hash,
                                    CorrelationId = getCorrelationId parseResult
                                )

                            let! result =
                                if parseResult |> hasOutput then
                                    progress
                                        .Columns(progressColumns)
                                        .StartAsync(fun progressContext ->
                                            task {
                                                let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                                let! response = Grace.SDK.Branch.Assign(assignParameters)
                                                t0.Increment(100.0)
                                                return response
                                            })
                                else
                                    Grace.SDK.Branch.Assign(assignParameters)

                            return result |> renderOutput parseResult
                    | Error graceError -> return Error graceError |> renderOutput parseResult
                with
                | ex ->
                    let error = GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)

                    return Error error |> renderOutput parseResult
            }

    type CreateReferenceCommand = CreateReferenceParameters -> Task<GraceResult<string>>

    //type ReferenceCommandContext =
    //    { GraceIds: GraceIds
    //      OwnerId: string
    //      OwnerName: string
    //      OrganizationId: string
    //      OrganizationName: string
    //      RepositoryId: string
    //      RepositoryName: string
    //      BranchId: string
    //      BranchName: string
    //      CorrelationId: string }

    //let buildReferenceContext (parseResult: ParseResult) =
    //    let graceIds = parseResult |> getNormalizedIdsAndNames

    //    let commonParameters = CommonParameters()
    //    commonParameters.OwnerId <- graceIds.OwnerIdString |> valueOrEmpty
    //    commonParameters.OwnerName <- graceIds.OwnerName |> valueOrEmpty
    //    commonParameters.OrganizationId <- graceIds.OrganizationIdString |> valueOrEmpty
    //    commonParameters.OrganizationName <- graceIds.OrganizationName |> valueOrEmpty
    //    commonParameters.RepositoryId <- graceIds.RepositoryIdString |> valueOrEmpty
    //    commonParameters.RepositoryName <- graceIds.RepositoryName |> valueOrEmpty
    //    commonParameters.BranchId <- graceIds.BranchIdString |> valueOrEmpty
    //    commonParameters.BranchName <- graceIds.BranchName |> valueOrEmpty

    //    let (ownerId, organizationId, repositoryId, branchId) = getIds commonParameters

    //    let ownerName =
    //        if String.IsNullOrWhiteSpace(commonParameters.OwnerName) then
    //            $"{Current().OwnerName}"
    //        else
    //            commonParameters.OwnerName

    //    let organizationName =
    //        if String.IsNullOrWhiteSpace(commonParameters.OrganizationName) then
    //            $"{Current().OrganizationName}"
    //        else
    //            commonParameters.OrganizationName

    //    let repositoryName =
    //        if String.IsNullOrWhiteSpace(commonParameters.RepositoryName) then
    //            $"{Current().RepositoryName}"
    //        else
    //            commonParameters.RepositoryName

    //    let branchName =
    //        if String.IsNullOrWhiteSpace(commonParameters.BranchName) then
    //            $"{Current().BranchName}"
    //        else
    //            commonParameters.BranchName

    //    { GraceIds = graceIds
    //      OwnerId = ownerId
    //      OwnerName = ownerName
    //      OrganizationId = organizationId
    //      OrganizationName = organizationName
    //      RepositoryId = repositoryId
    //      RepositoryName = repositoryName
    //      BranchId = branchId
    //      BranchName = branchName
    //      CorrelationId = getCorrelationId parseResult }

    let createReferenceHandler (parseResult: ParseResult) (message: string) (command: CreateReferenceCommand) (commandType: string) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult

                let referenceId =
                    if
                        not
                        <| isNull (parseResult.GetResult(Options.referenceId))
                    then
                        parseResult
                            .GetValue(Options.referenceId)
                            .ToString()
                    else
                        String.Empty

                let graceIds = parseResult |> getNormalizedIdsAndNames

                let validateIncomingParameters =
                    parseResult
                    |> CommonValidations
                    >>= ReferenceValidations

                match validateIncomingParameters with
                | Ok _ ->
                    //let sha256Bytes = SHA256.HashData(Encoding.ASCII.GetBytes(rnd.NextInt64().ToString("x8")))
                    //let sha256Hash = Seq.fold (fun (sb: StringBuilder) currentByte ->
                    //    sb.Append(sprintf $"{currentByte:X2}")) (StringBuilder(sha256Bytes.Length)) sha256Bytes
                    if parseResult |> hasOutput then
                        return!
                            progress
                                .Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                    task {
                                        let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Reading Grace status file.[/]")

                                        let t1 = progressContext.AddTask($"[{Color.DodgerBlue1}]Scanning working directory for changes.[/]", autoStart = false)

                                        let t2 = progressContext.AddTask($"[{Color.DodgerBlue1}]Creating new directory verions.[/]", autoStart = false)

                                        let t3 =
                                            progressContext.AddTask($"[{Color.DodgerBlue1}]Uploading changed files to object storage.[/]", autoStart = false)

                                        let t4 = progressContext.AddTask($"[{Color.DodgerBlue1}]Uploading new directory versions.[/]", autoStart = false)

                                        let t5 = progressContext.AddTask($"[{Color.DodgerBlue1}]Creating new {commandType}.[/]", autoStart = false)

                                        //let mutable rootDirectoryId = DirectoryId.Empty
                                        //let mutable rootDirectorySha256Hash = Sha256Hash String.Empty
                                        let rootDirectoryVersion = ref (DirectoryVersionId.Empty, Sha256Hash String.Empty)

                                        match! getGraceWatchStatus () with
                                        | Some graceWatchStatus ->
                                            t0.Value <- 100.0
                                            t1.Value <- 100.0
                                            t2.Value <- 100.0
                                            t3.Value <- 100.0
                                            t4.Value <- 100.0

                                            rootDirectoryVersion.Value <- (graceWatchStatus.RootDirectoryId, graceWatchStatus.RootDirectorySha256Hash)
                                        | None ->
                                            t0.StartTask() // Read Grace status file.
                                            let! previousGraceStatus = readGraceStatusFile ()
                                            let mutable newGraceStatus = previousGraceStatus
                                            t0.Value <- 100.0

                                            t1.StartTask() // Scan for differences.
                                            let! differences = scanForDifferences previousGraceStatus
                                            //logToAnsiConsole Colors.Verbose $"differences: {serialize differences}"
                                            let! newFileVersions = copyUpdatedFilesToObjectCache t1 differences
                                            //logToAnsiConsole Colors.Verbose $"newFileVersions: {serialize newFileVersions}"
                                            t1.Value <- 100.0

                                            t2.StartTask() // Create new directory versions.

                                            let! (updatedGraceStatus, newDirectoryVersions) =
                                                getNewGraceStatusAndDirectoryVersions previousGraceStatus differences

                                            newGraceStatus <- updatedGraceStatus

                                            rootDirectoryVersion.Value <- (newGraceStatus.RootDirectoryId, newGraceStatus.RootDirectorySha256Hash)

                                            t2.Value <- 100.0

                                            t3.StartTask() // Upload to object storage.

                                            let updatedRelativePaths =
                                                differences
                                                    .Select(fun difference ->
                                                        match difference.DifferenceType with
                                                        | Add ->
                                                            match difference.FileSystemEntryType with
                                                            | FileSystemEntryType.File -> Some difference.RelativePath
                                                            | FileSystemEntryType.Directory -> None
                                                        | Change ->
                                                            match difference.FileSystemEntryType with
                                                            | FileSystemEntryType.File -> Some difference.RelativePath
                                                            | FileSystemEntryType.Directory -> None
                                                        | Delete -> None)
                                                    .Where(fun relativePathOption -> relativePathOption.IsSome)
                                                    .Select(fun relativePath -> relativePath.Value)

                                            // let newFileVersions = updatedRelativePaths.Select(fun relativePath ->
                                            //     newDirectoryVersions.First(fun dv -> dv.Files.Exists(fun file -> file.RelativePath = relativePath)).Files.First(fun file -> file.RelativePath = relativePath))

                                            let mutable lastFileUploadInstant = newGraceStatus.LastSuccessfulFileUpload

                                            if newFileVersions.Count() > 0 then
                                                let getUploadMetadataForFilesParameters =
                                                    GetUploadMetadataForFilesParameters(
                                                        OwnerId = graceIds.OwnerIdString,
                                                        OwnerName = graceIds.OwnerName,
                                                        OrganizationId = graceIds.OrganizationIdString,
                                                        OrganizationName = graceIds.OrganizationName,
                                                        RepositoryId = graceIds.RepositoryIdString,
                                                        RepositoryName = graceIds.RepositoryName,
                                                        CorrelationId = graceIds.CorrelationId,
                                                        FileVersions =
                                                            (newFileVersions
                                                             |> Seq.map (fun localFileVersion -> localFileVersion.ToFileVersion)
                                                             |> Seq.toArray)
                                                    )

                                                match! uploadFilesToObjectStorage getUploadMetadataForFilesParameters with
                                                | Ok returnValue -> () //logToAnsiConsole Colors.Verbose $"Uploaded all files to object storage."
                                                | Error error -> logToAnsiConsole Colors.Error $"Error uploading files to object storage: {error.Error}"

                                                lastFileUploadInstant <- getCurrentInstant ()

                                            t3.Value <- 100.0

                                            t4.StartTask() // Upload directory versions.

                                            let mutable lastDirectoryVersionUpload = newGraceStatus.LastSuccessfulDirectoryVersionUpload

                                            if newDirectoryVersions.Count > 0 then
                                                let saveParameters = SaveDirectoryVersionsParameters()
                                                saveParameters.OwnerId <- graceIds.OwnerIdString
                                                saveParameters.OwnerName <- graceIds.OwnerName
                                                saveParameters.OrganizationId <- graceIds.OrganizationIdString
                                                saveParameters.OrganizationName <- graceIds.OrganizationName
                                                saveParameters.RepositoryId <- graceIds.RepositoryIdString
                                                saveParameters.RepositoryName <- graceIds.RepositoryName
                                                saveParameters.CorrelationId <- graceIds.CorrelationId
                                                saveParameters.DirectoryVersionId <- $"{newGraceStatus.RootDirectoryId}"

                                                saveParameters.DirectoryVersions <-
                                                    newDirectoryVersions
                                                        .Select(fun dv -> dv.ToDirectoryVersion)
                                                        .ToList()

                                                let! uploadDirectoryVersions = DirectoryVersion.SaveDirectoryVersions saveParameters

                                                lastDirectoryVersionUpload <- getCurrentInstant ()

                                            t4.Value <- 100.0

                                            newGraceStatus <-
                                                { newGraceStatus with
                                                    LastSuccessfulFileUpload = lastFileUploadInstant
                                                    LastSuccessfulDirectoryVersionUpload = lastDirectoryVersionUpload
                                                }

                                            do! writeGraceStatusFile newGraceStatus

                                        t5.StartTask() // Create new reference.

                                        let (rootDirectoryId, rootDirectorySha256Hash) = rootDirectoryVersion.Value

                                        let sdkParameters =
                                            Parameters.Branch.CreateReferenceParameters(
                                                BranchId = graceIds.BranchIdString,
                                                BranchName = graceIds.BranchName,
                                                OwnerId = graceIds.OwnerIdString,
                                                OwnerName = graceIds.OwnerName,
                                                OrganizationId = graceIds.OrganizationIdString,
                                                OrganizationName = graceIds.OrganizationName,
                                                RepositoryId = graceIds.RepositoryIdString,
                                                RepositoryName = graceIds.RepositoryName,
                                                DirectoryVersionId = rootDirectoryId,
                                                Sha256Hash = rootDirectorySha256Hash,
                                                Message = message,
                                                CorrelationId = graceIds.CorrelationId
                                            )

                                        let! result = command sdkParameters
                                        t5.Value <- 100.0

                                        return result
                                    })
                    else
                        let! previousGraceStatus = readGraceStatusFile ()
                        let! differences = scanForDifferences previousGraceStatus

                        let! (newGraceIndex, newDirectoryVersions) = getNewGraceStatusAndDirectoryVersions previousGraceStatus differences

                        let updatedRelativePaths =
                            differences
                                .Select(fun difference ->
                                    match difference.DifferenceType with
                                    | Add ->
                                        match difference.FileSystemEntryType with
                                        | FileSystemEntryType.File -> Some difference.RelativePath
                                        | FileSystemEntryType.Directory -> None
                                    | Change ->
                                        match difference.FileSystemEntryType with
                                        | FileSystemEntryType.File -> Some difference.RelativePath
                                        | FileSystemEntryType.Directory -> None
                                    | Delete -> None)
                                .Where(fun relativePathOption -> relativePathOption.IsSome)
                                .Select(fun relativePath -> relativePath.Value)

                        let newFileVersions =
                            updatedRelativePaths.Select (fun relativePath ->
                                newDirectoryVersions
                                    .First(fun dv -> dv.Files.Exists(fun file -> file.RelativePath = relativePath))
                                    .Files.First(fun file -> file.RelativePath = relativePath))

                        let getUploadMetadataForFilesParameters =
                            GetUploadMetadataForFilesParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                CorrelationId = graceIds.CorrelationId,
                                FileVersions =
                                    (newFileVersions
                                     |> Seq.map (fun localFileVersion -> localFileVersion.ToFileVersion)
                                     |> Seq.toArray)
                            )

                        let! uploadResult = uploadFilesToObjectStorage getUploadMetadataForFilesParameters
                        let saveParameters = SaveDirectoryVersionsParameters()
                        saveParameters.OwnerId <- graceIds.OwnerIdString
                        saveParameters.OwnerName <- graceIds.OwnerName
                        saveParameters.OrganizationId <- graceIds.OrganizationIdString
                        saveParameters.OrganizationName <- graceIds.OrganizationName
                        saveParameters.RepositoryId <- graceIds.RepositoryIdString
                        saveParameters.RepositoryName <- graceIds.RepositoryName
                        saveParameters.CorrelationId <- graceIds.CorrelationId

                        saveParameters.DirectoryVersions <-
                            newDirectoryVersions
                                .Select(fun dv -> dv.ToDirectoryVersion)
                                .ToList()

                        let! uploadDirectoryVersions = DirectoryVersion.SaveDirectoryVersions saveParameters
                        let rootDirectoryVersion = getRootDirectoryVersion previousGraceStatus

                        let sdkParameters =
                            Parameters.Branch.CreateReferenceParameters(
                                BranchId = graceIds.BranchIdString,
                                BranchName = graceIds.BranchName,
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                DirectoryVersionId = rootDirectoryVersion.DirectoryVersionId,
                                Sha256Hash = rootDirectoryVersion.Sha256Hash,
                                Message = message,
                                CorrelationId = graceIds.CorrelationId
                            )

                        let! result = command sdkParameters
                        return result
                | Error error -> return Error error
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    let promotionHandler (parseResult: ParseResult) (message: string) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult

                let validateIncomingParameters = parseResult |> ReferenceValidations
                let graceIds = parseResult |> getNormalizedIdsAndNames

                match validateIncomingParameters with
                | Ok _ ->
                    if parseResult |> hasOutput then
                        return!
                            progress
                                .Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                    task {
                                        let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Reading Grace status file.[/]")

                                        let t1 = progressContext.AddTask($"[{Color.DodgerBlue1}]Checking if the promotion is valid.[/]", autoStart = false)

                                        let t2 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]", autoStart = false)

                                        // Read Grace status file.
                                        let! graceStatus = readGraceStatusFile ()
                                        let rootDirectoryId = graceStatus.RootDirectoryId
                                        let rootDirectorySha256Hash = graceStatus.RootDirectorySha256Hash
                                        t0.Value <- 100.0

                                        // Check if the promotion is valid; i.e. it's allowed by the ReferenceTypes enabled in the repository.
                                        t1.StartTask()
                                        // For single-step promotion, the current branch's latest commit will become the parent branch's next promotion.
                                        // If our current state is not the latest commit, print a warning message.

                                        // Get the Dto for the current branch. That will have its latest commit.
                                        let branchGetParameters =
                                            GetBranchParameters(
                                                BranchId = graceIds.BranchIdString,
                                                BranchName = graceIds.BranchName,
                                                OwnerId = graceIds.OwnerIdString,
                                                OwnerName = graceIds.OwnerName,
                                                OrganizationId = graceIds.OrganizationIdString,
                                                OrganizationName = graceIds.OrganizationName,
                                                RepositoryId = graceIds.RepositoryIdString,
                                                RepositoryName = graceIds.RepositoryName,
                                                CorrelationId = graceIds.CorrelationId
                                            )

                                        let! branchResult = Grace.SDK.Branch.Get(branchGetParameters)

                                        match branchResult with
                                        | Ok branchReturnValue ->
                                            // If we succeeded, get the parent branch Dto. That will have its latest promotion.
                                            let! parentBranchResult = Branch.GetParentBranch(branchGetParameters)

                                            match parentBranchResult with
                                            | Ok parentBranchReturnValue ->
                                                // Yay, we have both Dto's.
                                                let branchDto = branchReturnValue.ReturnValue
                                                let parentBranchDto = parentBranchReturnValue.ReturnValue

                                                // Get the references for the latest commit and/or promotion on the current branch.
                                                //let getReferenceParameters =
                                                //    Parameters.Branch.GetReferenceParameters(BranchId = parameters.BranchId, BranchName = parameters.BranchName,
                                                //        OwnerId = parameters.OwnerId, OwnerName = parameters.OwnerName,
                                                //        OrganizationId = parameters.OrganizationId, OrganizationName = parameters.OrganizationName,
                                                //        RepositoryId = parameters.RepositoryId, RepositoryName = parameters.RepositoryName,
                                                //        ReferenceId = $"{branchDto.LatestCommit}", CorrelationId = parameters.CorrelationId)
                                                //let! referenceResult = Branch.GetReference(getReferenceParameters)

                                                let referenceIds = List<ReferenceId>()

                                                if branchDto.LatestCommit <> ReferenceDto.Default then
                                                    referenceIds.Add(branchDto.LatestCommit.ReferenceId)

                                                if branchDto.LatestPromotion <> ReferenceDto.Default then
                                                    referenceIds.Add(branchDto.LatestPromotion.ReferenceId)

                                                if referenceIds.Count > 0 then
                                                    let getReferencesByReferenceIdParameters =
                                                        Parameters.Repository.GetReferencesByReferenceIdParameters(
                                                            OwnerId = graceIds.OwnerIdString,
                                                            OwnerName = graceIds.OwnerName,
                                                            OrganizationId = graceIds.OrganizationIdString,
                                                            OrganizationName = graceIds.OrganizationName,
                                                            RepositoryId = graceIds.RepositoryIdString,
                                                            RepositoryName = graceIds.RepositoryName,
                                                            ReferenceIds = referenceIds,
                                                            CorrelationId = graceIds.CorrelationId
                                                        )

                                                    match! Repository.GetReferencesByReferenceId(getReferencesByReferenceIdParameters) with
                                                    | Ok returnValue ->
                                                        let references = returnValue.ReturnValue

                                                        let latestPromotableReference =
                                                            references
                                                                .OrderByDescending(fun reference -> reference.CreatedAt)
                                                                .First()
                                                        // If the current branch's latest reference is not the latest commit - i.e. they've done more work in the branch
                                                        //   after the commit they're expecting to promote - print a warning.
                                                        //match getReferencesByReferenceIdResult with
                                                        //| Ok returnValue ->
                                                        //    let references = returnValue.ReturnValue
                                                        //    if referenceDto.DirectoryId <> graceStatus.RootDirectoryId then
                                                        //        logToAnsiConsole Colors.Important $"Note: the branch has been updated since the latest commit."
                                                        //| Error error -> () // I don't really care if this call fails, it's just a warning message.
                                                        t1.Value <- 100.0

                                                        // If the current branch is based on the parent's latest promotion, then we can proceed with the promotion.
                                                        if branchDto.BasedOn.ReferenceId = parentBranchDto.LatestPromotion.ReferenceId then
                                                            t2.StartTask()

                                                            let promotionParameters =
                                                                Parameters.Branch.CreateReferenceParameters(
                                                                    BranchId = $"{parentBranchDto.BranchId}",
                                                                    OwnerId = graceIds.OwnerIdString,
                                                                    OwnerName = graceIds.OwnerName,
                                                                    OrganizationId = graceIds.OrganizationIdString,
                                                                    OrganizationName = graceIds.OrganizationName,
                                                                    RepositoryId = graceIds.RepositoryIdString,
                                                                    RepositoryName = graceIds.RepositoryName,
                                                                    DirectoryVersionId = latestPromotableReference.DirectoryId,
                                                                    Sha256Hash = latestPromotableReference.Sha256Hash,
                                                                    Message = message,
                                                                    CorrelationId = graceIds.CorrelationId
                                                                )

                                                            let! promotionResult = Grace.SDK.Branch.Promote(promotionParameters)

                                                            match promotionResult with
                                                            | Ok returnValue ->
                                                                logToAnsiConsole Colors.Verbose $"Succeeded doing promotion."

                                                                let promotionReferenceId = Guid.Parse(returnValue.Properties["ReferenceId"] :?> string)

                                                                let rebaseParameters =
                                                                    Parameters.Branch.RebaseParameters(
                                                                        BranchId = $"{branchDto.BranchId}",
                                                                        RepositoryId = $"{branchDto.RepositoryId}",
                                                                        OwnerId = graceIds.OwnerIdString,
                                                                        OwnerName = graceIds.OwnerName,
                                                                        OrganizationId = graceIds.OrganizationIdString,
                                                                        OrganizationName = graceIds.OrganizationName,
                                                                        BasedOn = promotionReferenceId
                                                                    )

                                                                let! rebaseResult = Grace.SDK.Branch.Rebase(rebaseParameters)
                                                                t2.Value <- 100.0

                                                                match rebaseResult with
                                                                | Ok returnValue ->
                                                                    logToAnsiConsole Colors.Verbose $"Succeeded doing rebase."

                                                                    return promotionResult
                                                                | Error error -> return Error error
                                                            | Error error ->
                                                                t2.Value <- 100.0
                                                                return Error error

                                                        else
                                                            return
                                                                Error(
                                                                    GraceError.Create
                                                                        (getErrorMessage BranchError.BranchIsNotBasedOnLatestPromotion)
                                                                        (parseResult |> getCorrelationId)
                                                                )
                                                    | Error error ->
                                                        t2.Value <- 100.0
                                                        return Error error
                                                else
                                                    return
                                                        Error(
                                                            GraceError.Create
                                                                (getErrorMessage ReferenceError.PromotionNotAvailableBecauseThereAreNoPromotableReferences)
                                                                (parseResult |> getCorrelationId)
                                                        )
                                            | Error error ->
                                                t1.Value <- 100.0
                                                return Error error
                                        | Error error ->
                                            t1.Value <- 100.0
                                            return Error error
                                    })
                    else
                        // Same result, with no output.
                        return Error(GraceError.Create "Need to implement the else clause." (parseResult |> getCorrelationId))
                | Error error -> return Error error
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    type Promote() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                let graceIds = parseResult |> getNormalizedIdsAndNames

                let message =
                    parseResult.GetValue(Options.message)
                    |> valueOrEmpty

                let! result = promotionHandler parseResult message
                return result |> renderOutput parseResult
            }

    type Commit() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                let graceIds = parseResult |> getNormalizedIdsAndNames

                let message =
                    parseResult.GetValue(Options.messageRequired)
                    |> valueOrEmpty

                let command (parameters: CreateReferenceParameters) = task { return! Grace.SDK.Branch.Commit(parameters) }

                let! result = createReferenceHandler parseResult message command (nameof(Commit).ToLowerInvariant())

                return result |> renderOutput parseResult
            }

    type Checkpoint() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                let graceIds = parseResult |> getNormalizedIdsAndNames

                let message =
                    parseResult.GetValue(Options.message)
                    |> valueOrEmpty

                let command (parameters: CreateReferenceParameters) = task { return! Grace.SDK.Branch.Checkpoint(parameters) }

                let! result = createReferenceHandler parseResult message command (nameof(Checkpoint).ToLowerInvariant())

                return result |> renderOutput parseResult
            }

    type Save() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                let graceIds = parseResult |> getNormalizedIdsAndNames

                let message =
                    parseResult.GetValue(Options.message)
                    |> valueOrEmpty

                let command (parameters: CreateReferenceParameters) = task { return! Grace.SDK.Branch.Save(parameters) }

                let! result = createReferenceHandler parseResult message command (nameof(Save).ToLowerInvariant())

                return result |> renderOutput parseResult
            }

    type Tag() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                let graceIds = parseResult |> getNormalizedIdsAndNames

                let message =
                    parseResult.GetValue(Options.messageRequired)
                    |> valueOrEmpty

                let command (parameters: CreateReferenceParameters) = task { return! Grace.SDK.Branch.Tag(parameters) }

                let! result = createReferenceHandler parseResult message command (nameof(Tag).ToLowerInvariant())

                return result |> renderOutput parseResult
            }

    type CreateExternal() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                let graceIds = parseResult |> getNormalizedIdsAndNames

                let message =
                    parseResult.GetValue(Options.messageRequired)
                    |> valueOrEmpty

                let command (parameters: CreateReferenceParameters) = task { return! Grace.SDK.Branch.CreateExternal(parameters) }

                let! result = createReferenceHandler parseResult message command ("External".ToLowerInvariant())

                return result |> renderOutput parseResult
            }

    type Get() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult

                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let includeDeleted = parseResult.GetValue(Options.includeDeleted)
                    let showEvents = parseResult.GetValue(Options.showEvents)
                    let validateIncomingParameters = parseResult |> ReferenceValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let branchParameters =
                            GetBranchParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                BranchId = graceIds.BranchIdString,
                                BranchName = graceIds.BranchName,
                                IncludeDeleted = includeDeleted,
                                CorrelationId = getCorrelationId parseResult
                            )

                        let! result =
                            if parseResult |> hasOutput then
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                            let! response = Grace.SDK.Branch.Get(branchParameters)
                                            t0.Increment(100.0)
                                            return response
                                        })
                            else
                                Grace.SDK.Branch.Get(branchParameters)

                        match result with
                        | Ok graceReturnValue ->
                            let jsonText = JsonText(serialize graceReturnValue.ReturnValue)
                            AnsiConsole.Write(jsonText)
                            AnsiConsole.WriteLine()

                            if showEvents then
                                let eventParameters =
                                    GetBranchVersionParameters(
                                        OwnerId = graceIds.OwnerIdString,
                                        OwnerName = graceIds.OwnerName,
                                        OrganizationId = graceIds.OrganizationIdString,
                                        OrganizationName = graceIds.OrganizationName,
                                        RepositoryId = graceIds.RepositoryIdString,
                                        RepositoryName = graceIds.RepositoryName,
                                        BranchId = graceIds.BranchIdString,
                                        BranchName = graceIds.BranchName,
                                        IncludeDeleted = includeDeleted,
                                        CorrelationId = getCorrelationId parseResult
                                    )

                                let! eventsResult =
                                    if parseResult |> hasOutput then
                                        progress
                                            .Columns(progressColumns)
                                            .StartAsync(fun progressContext ->
                                                task {
                                                    let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                                    let! response = Branch.GetEvents(eventParameters)
                                                    t0.Increment(100.0)
                                                    return response
                                                })
                                    else
                                        Branch.GetEvents(eventParameters)

                                match eventsResult with
                                | Ok eventsValue ->
                                    for line in eventsValue.ReturnValue do
                                        AnsiConsole.MarkupLine $"[{Colors.Verbose}]{Markup.Escape(line)}[/]"

                                    AnsiConsole.WriteLine()
                                    return 0
                                | Error eventError ->
                                    return
                                        GraceResult.Error eventError
                                        |> renderOutput parseResult
                            else
                                return 0
                        | Error graceError ->
                            return
                                GraceResult.Error graceError
                                |> renderOutput parseResult
                    | Error graceError ->
                        return
                            GraceResult.Error graceError
                            |> renderOutput parseResult
                with
                | ex ->
                    let graceError = GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)

                    return renderOutput parseResult (GraceResult.Error graceError)
            }

    type Delete() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult

                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> ReferenceValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let deleteParameters =
                            Parameters.Branch.DeleteBranchParameters(
                                BranchId = graceIds.BranchIdString,
                                BranchName = graceIds.BranchName,
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                CorrelationId = getCorrelationId parseResult
                            )

                        let! result =
                            if parseResult |> hasOutput then
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! response = Grace.SDK.Branch.Delete(deleteParameters)
                                            t0.Increment(100.0)
                                            return response
                                        })
                            else
                                Grace.SDK.Branch.Delete(deleteParameters)

                        return result |> renderOutput parseResult
                    | Error graceError ->
                        return
                            GraceResult.Error graceError
                            |> renderOutput parseResult
                with
                | ex ->
                    let graceError = GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)

                    return renderOutput parseResult (GraceResult.Error graceError)
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
            |> addOption Options.branchName
            |> addOption Options.branchId

        // Create main command and aliases, if any.`
        let referenceCommand = new Command("reference", Description = "Create or delete references.")

        referenceCommand.Aliases.Add("ref")

        let promoteCommand =
            new Command("promote", Description = "Promotes a commit into the parent branch.")
            |> addOption Options.message
            |> addCommonOptions

        promoteCommand.Action <- new Promote()
        referenceCommand.Subcommands.Add(promoteCommand)

        let commitCommand =
            new Command("commit", Description = "Create a commit.")
            |> addOption Options.messageRequired
            |> addCommonOptions

        commitCommand.Action <- new Commit()
        referenceCommand.Subcommands.Add(commitCommand)

        let checkpointCommand =
            new Command("checkpoint", Description = "Create a checkpoint.")
            |> addOption Options.message
            |> addCommonOptions

        checkpointCommand.Action <- new Checkpoint()
        referenceCommand.Subcommands.Add(checkpointCommand)

        let saveCommand =
            new Command("save", Description = "Create a save.")
            |> addOption Options.message
            |> addCommonOptions

        saveCommand.Action <- new Save()
        referenceCommand.Subcommands.Add(saveCommand)

        let tagCommand =
            new Command("tag", Description = "Create a tag.")
            |> addOption Options.messageRequired
            |> addCommonOptions

        tagCommand.Action <- new Tag()
        referenceCommand.Subcommands.Add(tagCommand)

        let createExternalCommand =
            new Command("create-external", Description = "Create an external reference.")
            |> addOption Options.messageRequired
            |> addCommonOptions

        createExternalCommand.Action <- new CreateExternal()
        referenceCommand.Subcommands.Add(createExternalCommand)

        let getCommand =
            new Command("get", Description = "Gets details for the branch.")
            |> addOption Options.includeDeleted
            |> addOption Options.showEvents
            |> addCommonOptions

        getCommand.Action <- new Get()
        referenceCommand.Subcommands.Add(getCommand)

        let deleteCommand =
            new Command("delete", Description = "Delete the branch.")
            |> addCommonOptions

        deleteCommand.Action <- new Delete()
        referenceCommand.Subcommands.Add(deleteCommand)

        let assignCommand =
            new Command("assign", Description = "Assign a promotion to this branch.")
            |> addOption Options.directoryVersionId
            |> addOption Options.sha256Hash
            |> addOption Options.message
            |> addCommonOptions

        assignCommand.Action <- new Assign()
        referenceCommand.Subcommands.Add(assignCommand)

        //let undeleteCommand = new Command("undelete", Description = "Undelete a deleted owner.") |> addCommonOptions
        //undeleteCommand.Action <- Undelete
        //branchCommand.Subcommands.Add(undeleteCommand)

        referenceCommand
