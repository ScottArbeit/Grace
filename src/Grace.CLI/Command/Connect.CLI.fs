namespace Grace.CLI.Command

open FSharpPlus
open Grace.CLI.Common
open Grace.CLI.Services
open Grace.CLI.Text
open Grace.SDK
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Types.Owner
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
                Arity = ArgumentArity.ExactlyOne
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
            new Option<OwnerId>(OptionName.OwnerId, Required = false, Description = "The repository's owner ID <Guid>.", Arity = ArgumentArity.ExactlyOne)

        let ownerName =
            new Option<String>(OptionName.OwnerName, Required = false, Description = "The repository's owner name.", Arity = ArgumentArity.ExactlyOne)

        let organizationId =
            new Option<OrganizationId>(
                OptionName.OrganizationId,
                Required = false,
                Description = "The repository's organization ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne
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

        let retrieveDefaultBranch =
            new Option<bool>(
                OptionName.RetrieveDefaultBranch,
                [||],
                Required = false,
                Description = "True to retrieve the default branch after connecting; false to connect but not download any files.",
                Arity = ArgumentArity.ExactlyOne
            )

    type Connect() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: Threading.CancellationToken) : Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult

                    let validateIncomingParameters = Validations.CommonValidations parseResult

                    match validateIncomingParameters with
                    | Ok _ ->
                        let graceIds = getNormalizedIdsAndNames parseResult

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

                            let branchParameters =
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
                                AnsiConsole.MarkupLine $"[{Colors.Important}]Wrote new Grace configuration file.[/]"

                                let getDirectoryContentsParameters =
                                    Parameters.DirectoryVersion.GetParameters(
                                        OwnerId = $"{ownerDto.OwnerId}",
                                        OrganizationId = $"{organizationDto.OrganizationId}",
                                        RepositoryId = $"{repositoryDto.RepositoryId}",
                                        DirectoryVersionId = $"{branchDto.LatestPromotion.DirectoryId}",
                                        CorrelationId = graceIds.CorrelationId
                                    )

                                AnsiConsole.MarkupLine $"[{Colors.Important}]Retrieving all DirectoryVersions.[/]"
                                let! directoryVersionsResult = DirectoryVersion.GetDirectoryVersionsRecursive(getDirectoryContentsParameters)

                                let getZipFileParameters =
                                    Parameters.DirectoryVersion.GetZipFileParameters(
                                        OwnerId = $"{ownerDto.OwnerId}",
                                        OrganizationId = $"{organizationDto.OrganizationId}",
                                        RepositoryId = $"{repositoryDto.RepositoryId}",
                                        DirectoryVersionId = $"{branchDto.LatestPromotion.DirectoryId}",
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

                                    let fileVersionLookup = Dictionary<RelativePath, bool>(fileVersions |> Seq.length)

                                    fileVersions
                                    |> Seq.iter (fun fileVersion -> fileVersionLookup.Add(fileVersion.RelativePath, fileVersion.IsBinary))

                                    let uriWithSharedAccessSignature = getZipFileReturnValue.ReturnValue

                                    // Download the .zip file to temp directory.
                                    let blobClient = BlobClient(uriWithSharedAccessSignature)

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
            }

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

        connectCommand.Action <- Connect()
        connectCommand
