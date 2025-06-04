namespace Grace.CLI.Command

open FSharpPlus
open Grace.CLI.Common
open Grace.SDK
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Types.Types
open Grace.Shared.Validation.Common
open Grace.Shared.Validation.Errors.Connect
open System
open System.Collections.Generic
open System.CommandLine.NamingConventionBinder
open System.CommandLine.Parsing
open System.IO
open System.Threading.Tasks
open System.CommandLine
open Spectre.Console
open Azure.Storage.Blobs
open Azure.Storage.Blobs.Models
open System.IO.Compression
open Grace.CLI.Services

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
            new Option<String>("--repositoryId", [| "-r" |], Required = false, Description = "The repository's ID <Guid>.", Arity = ArgumentArity.ExactlyOne)

        let repositoryName =
            new Option<String>("--repositoryName", [| "-n" |], Required = false, Description = "The name of the repository.", Arity = ArgumentArity.ExactlyOne)

        let ownerId = new Option<String>("--ownerId", Required = false, Description = "The repository's owner ID <Guid>.", Arity = ArgumentArity.ExactlyOne)

        let ownerName = new Option<String>("--ownerName", Required = false, Description = "The repository's owner name.", Arity = ArgumentArity.ExactlyOne)

        let organizationId =
            new Option<String>("--organizationId", Required = false, Description = "The repository's organization ID <Guid>.", Arity = ArgumentArity.ExactlyOne)

        let organizationName =
            new Option<String>("--organizationName", Required = false, Description = "The repository's organization name.", Arity = ArgumentArity.ZeroOrOne)

        let correlationId =
            new Option<String>(
                "--correlationId",
                [| "-c" |],
                Required = false,
                Description = "CorrelationId to track this command throughout Grace. [default: new Guid]",
                Arity = ArgumentArity.ExactlyOne
            )

        let serverAddress =
            new Option<String>(
                "--serverAddress",
                [| "-s" |],
                Required = false,
                Description = "Address of the Grace server to connect to.",
                Arity = ArgumentArity.ExactlyOne
            )

        let retrieveDefaultBranch =
            new Option<bool>(
                "--retrieveDefaultBranch",
                [||],
                Required = false,
                Description = "True to retrieve the default branch after connecting; false to connect but not download any files.",
                Arity = ArgumentArity.ExactlyOne
            )

    let private ValidateIncomingParameters (parseResult: ParseResult) commonParameters =

        let ``RepositoryId must be a non-empty Guid`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            let mutable repositoryId: Guid = Guid.Empty

            if parseResult.CommandResult.Command.Options.Contains(Options.repositoryId) then
                match
                    (Guid.isValidAndNotEmptyGuid commonParameters.RepositoryId InvalidRepositoryId)
                        .Result
                with
                | Ok result -> Result.Ok(parseResult, commonParameters)
                | Error error -> Result.Error error
            else
                Result.Ok(parseResult, commonParameters)

        let ``RepositoryName must be valid`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            if parseResult.CommandResult.Command.Options.Contains(Options.repositoryName) then
                match
                    (String.isValidGraceName commonParameters.RepositoryName InvalidRepositoryName)
                        .Result
                with
                | Ok result -> Result.Ok(parseResult, commonParameters)
                | Error error -> Result.Error error
            else
                Result.Ok(parseResult, commonParameters)

        let ``OwnerId must be a non-empty Guid`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            let mutable ownerId: Guid = Guid.Empty

            if parseResult.CommandResult.Command.Options.Contains(Options.ownerId) then
                match (Guid.isValidAndNotEmptyGuid commonParameters.OwnerId InvalidOwnerId).Result with
                | Ok result -> Result.Ok(parseResult, commonParameters)
                | Error error -> Result.Error error
            else
                Result.Ok(parseResult, commonParameters)

        let ``OwnerName must be valid`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            if parseResult.CommandResult.Command.Options.Contains(Options.ownerName) then
                match (String.isValidGraceName commonParameters.OwnerName InvalidOwnerName).Result with
                | Ok result -> Result.Ok(parseResult, commonParameters)
                | Error error -> Result.Error error
            else
                Result.Ok(parseResult, commonParameters)

        let ``OrganizationId must be a non-empty Guid`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            let mutable organizationId: Guid = Guid.Empty

            if parseResult.CommandResult.Command.Options.Contains(Options.organizationId) then
                match
                    (Guid.isValidAndNotEmptyGuid commonParameters.OrganizationId InvalidOrganizationId)
                        .Result
                with
                | Ok result -> Result.Ok(parseResult, commonParameters)
                | Error error -> Result.Error error
            else
                Result.Ok(parseResult, commonParameters)

        let ``OrganizationName must be valid`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            if parseResult.CommandResult.Command.Options.Contains(Options.organizationName) then
                match
                    (String.isValidGraceName commonParameters.OrganizationName InvalidOrganizationName)
                        .Result
                with
                | Ok result -> Result.Ok(parseResult, commonParameters)
                | Error error -> Result.Error error
            else
                Result.Ok(parseResult, commonParameters)

        (parseResult, commonParameters)
        |> ``RepositoryId must be a non-empty Guid``
        >>= ``RepositoryName must be valid``
        >>= ``OwnerId must be a non-empty Guid``
        >>= ``OwnerName must be valid``
        >>= ``OrganizationId must be a non-empty Guid``
        >>= ``OrganizationName must be valid``

    let private Connect =
        CommandHandler.Create(fun (parseResult: ParseResult) (parameters: CommonParameters) ->
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult

                    let validateIncomingParameters = ValidateIncomingParameters parseResult parameters

                    match validateIncomingParameters with
                    | Ok _ ->
                        let ownerParameters =
                            Parameters.Owner.GetOwnerParameters(
                                OwnerId = parameters.OwnerId,
                                OwnerName = parameters.OwnerName,
                                CorrelationId = parameters.CorrelationId
                            )

                        let! ownerResult = Owner.Get(ownerParameters)

                        let organizationParameters =
                            Parameters.Organization.GetOrganizationParameters(
                                OwnerId = parameters.OwnerId,
                                OwnerName = parameters.OwnerName,
                                OrganizationId = parameters.OrganizationId,
                                OrganizationName = parameters.OrganizationName,
                                CorrelationId = parameters.CorrelationId
                            )

                        let! organizationResult = Organization.Get(organizationParameters)

                        let repositoryParameters =
                            Parameters.Repository.GetRepositoryParameters(
                                OwnerId = parameters.OwnerId,
                                OwnerName = parameters.OwnerName,
                                OrganizationId = parameters.OrganizationId,
                                OrganizationName = parameters.OrganizationName,
                                RepositoryId = parameters.RepositoryId,
                                RepositoryName = parameters.RepositoryName,
                                CorrelationId = parameters.CorrelationId
                            )

                        let! repositoryResult = Repository.Get(repositoryParameters)

                        match (ownerResult, organizationResult, repositoryResult) with
                        | (Ok owner, Ok organization, Ok repository) ->
                            let ownerDto = owner.ReturnValue
                            let organizationDto = organization.ReturnValue
                            let repositoryDto = repository.ReturnValue

                            AnsiConsole.MarkupLine $"[{Colors.Important}]Found owner, organization, and repository.[/]"

                            let branchParameters =
                                Parameters.Branch.GetBranchParameters(
                                    OwnerId = $"{ownerDto.OwnerId}",
                                    OrganizationId = $"{organizationDto.OrganizationId}",
                                    RepositoryId = $"{repositoryDto.RepositoryId}",
                                    BranchName = $"{repositoryDto.DefaultBranchName}",
                                    CorrelationId = parameters.CorrelationId
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
                                AnsiConsole.MarkupLine $"[{Colors.Important}]Wrote new Grace configuration file.[/]"

                                let getDirectoryContentsParameters =
                                    Parameters.DirectoryVersion.GetParameters(
                                        OwnerId = $"{ownerDto.OwnerId}",
                                        OrganizationId = $"{organizationDto.OrganizationId}",
                                        RepositoryId = $"{repositoryDto.RepositoryId}",
                                        DirectoryVersionId = $"{branchDto.LatestPromotion.DirectoryId}",
                                        CorrelationId = parameters.CorrelationId
                                    )

                                AnsiConsole.MarkupLine $"[{Colors.Important}]Retrieving all DirectoryVersions.[/]"
                                let! directoryVersionsResult = DirectoryVersion.GetDirectoryVersionsRecursive(getDirectoryContentsParameters)

                                let getZipFileParameters =
                                    Parameters.DirectoryVersion.GetZipFileParameters(
                                        OwnerId = $"{ownerDto.OwnerId}",
                                        OrganizationId = $"{organizationDto.OrganizationId}",
                                        RepositoryId = $"{repositoryDto.RepositoryId}",
                                        DirectoryVersionId = $"{branchDto.LatestPromotion.DirectoryId}",
                                        CorrelationId = parameters.CorrelationId
                                    )

                                AnsiConsole.MarkupLine $"[{Colors.Important}]Retrieving zip file download uri.[/]"
                                let! getZipFileResult = DirectoryVersion.GetZipFile(getZipFileParameters)
                                AnsiConsole.MarkupLine $"[{Colors.Important}]Finished getting zip file download uri.[/]"

                                match (directoryVersionsResult, getZipFileResult) with
                                | (Ok directoryVerionsReturnValue, Ok getZipFileReturnValue) ->
                                    AnsiConsole.MarkupLine $"[{Colors.Important}]Retrieved all DirectoryVersions.[/]"
                                    let directoryVersions = directoryVerionsReturnValue.ReturnValue
                                    let fileVersions = directoryVersions |> Seq.collect (fun dv -> dv.Files)
                                    let fileVersionLookup = Dictionary<RelativePath, bool>(fileVersions |> Seq.length)

                                    fileVersions
                                    |> Seq.iter (fun fileVersion -> fileVersionLookup.Add(fileVersion.RelativePath, fileVersion.IsBinary))

                                    let uriWithSharedAccessSignature = getZipFileReturnValue.ReturnValue

                                    // Download the .zip file to temp directory.
                                    let blobClient = BlobClient(uriWithSharedAccessSignature)
                                    //let zipFilePath = Path.Combine(Current().GraceDirectory, $"{branchDto.LatestPromotion.DirectoryId}.zip")
                                    //let! downloadResponse = blobClient.DownloadToAsync(zipFilePath)

                                    //if downloadResponse.Status = 200 then
                                    //    AnsiConsole.MarkupLine $"[{Colors.Important}]Successfully downloaded zip file to {zipFilePath}.[/]"

                                    // Loop through the ZipArchiveEntry list, identify if each file version is binary, and extract
                                    //   each one accordingly.
                                    use! zipFile = blobClient.OpenReadAsync(bufferSize = 64 * 1024)
                                    use zipArchive = new ZipArchive(zipFile, ZipArchiveMode.Read)

                                    AnsiConsole.MarkupLine $"[{Colors.Important}]Streaming contents from .zip file.[/]"
                                    AnsiConsole.MarkupLine $"[{Colors.Important}]Starting to write files to disk.[/]"

                                    zipArchive.Entries
                                    |> Seq.iteri (fun i entry ->
                                        let fileVersion =
                                            fileVersions
                                            |> Seq.tryFind (fun fv -> fv.RelativePath = RelativePath(entry.FullName))

                                        match fileVersion with
                                        | Some fileVersion ->
                                            let fileInfo = FileInfo(Path.Combine(Current().RootDirectory, fileVersion.RelativePath))

                                            let objectFileInfo = FileInfo(Path.Combine(Current().ObjectDirectory, fileVersion.RelativePath, entry.Comment))

                                            // Make sure the entire paths exist before writing files to them.
                                            Directory.CreateDirectory(fileInfo.DirectoryName) |> ignore
                                            Directory.CreateDirectory(objectFileInfo.DirectoryName) |> ignore

                                            if fileVersion.IsBinary then
                                                // Binary files are not GZipped, so write it to directly to disk.
                                                if not fileInfo.Exists then entry.ExtractToFile(fileInfo.FullName, false)

                                                if not objectFileInfo.Exists then
                                                    entry.ExtractToFile(objectFileInfo.FullName, false)
                                            else
                                                // It's already GZipped, so let's uncompress it and write it to disk.
                                                let uncompressAndWriteToFile (entry: ZipArchiveEntry) (fileInfo: FileInfo) =
                                                    use entryStream = entry.Open()
                                                    use fileStream = fileInfo.Create()
                                                    use gzipStream = new GZipStream(entryStream, CompressionMode.Decompress)
                                                    gzipStream.CopyTo(fileStream)

                                                uncompressAndWriteToFile entry fileInfo
                                                uncompressAndWriteToFile entry objectFileInfo

                                            if parseResult |> verbose then
                                                AnsiConsole.MarkupLine $"[{Colors.Important}]Wrote {fileVersion.RelativePath}.[/]"
                                        | None ->
                                            // The .zip file has a file in it that isn't in the directory version.
                                            AnsiConsole.MarkupLine
                                                $"[{Colors.Error}]Zip file contains additional file {entry.FullName}. Ignoring this file.[/]")

                                    AnsiConsole.MarkupLine $"[{Colors.Important}]Finished writing files to disk.[/]"

                                    AnsiConsole.MarkupLine $"[{Colors.Important}]Creating Grace Index file.[/]"
                                    let! previousGraceStatus = readGraceStatusFile ()
                                    let! graceStatus = createNewGraceStatusFile previousGraceStatus parseResult
                                    do! writeGraceStatusFile graceStatus

                                    AnsiConsole.MarkupLine $"[{Colors.Important}]Creating Grace Object Cache Index file.[/]"
                                    let! objectCache = readGraceObjectCacheFile ()

                                    let plr =
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
                                                    |> ignore

                                            )
                                        )

                                    do! writeGraceObjectCacheFile objectCache

                                | _ -> AnsiConsole.MarkupLine $"[{Colors.Error}]Failed to retrieve zip file.[/]"
                            | Error error -> AnsiConsole.MarkupLine $"[{Colors.Error}]Failed to retrieve branch.[/]"
                        | (Error error, _, _) -> AnsiConsole.MarkupLine $"[{Colors.Error}]Failed to retrieve owner.[/]"
                        | (_, Error error, _) -> AnsiConsole.MarkupLine $"[{Colors.Error}]Failed to retrieve organization.[/]"
                        | (_, _, Error error) -> AnsiConsole.MarkupLine $"[{Colors.Error}]Failed to retrieve repository.[/]"
                    | (Error error) -> printfn ($"error: {error}")

                    return 0
                with :? OperationCanceledException as ex ->
                    return -1
            })

    let Build =
        // Create main command and aliases, if any.
        let connectCommand = new Command("connect", Description = "Connect to a Grace repository.")

        connectCommand.Options.Add(Options.repositoryId)
        connectCommand.Options.Add(Options.repositoryName)
        connectCommand.Options.Add(Options.ownerId)
        connectCommand.Options.Add(Options.ownerName)
        connectCommand.Options.Add(Options.organizationId)
        connectCommand.Options.Add(Options.organizationName)
        connectCommand.Options.Add(Options.correlationId)
        connectCommand.Options.Add(Options.serverAddress)
        connectCommand.Options.Add(Options.retrieveDefaultBranch)

        connectCommand.Action <- Connect
        connectCommand
