namespace Grace.CLI.Command

open Azure.Storage.Blobs
open Grace.CLI.Common
open Grace.CLI.Services
open Grace.CLI.Text
open Grace.SDK
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Grace.Types.Artifact
open Grace.Types.Types
open Spectre.Console
open System
open System.CommandLine
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.IO
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks

module AgentCommand =
    module private Options =
        let workItemId =
            new Option<string>(
                "--work-item-id",
                [| "--workItemId"; "-w" |],
                Required = true,
                Description = "The work item ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne
            )

        let summaryFile =
            new Option<string>(
                "--summary-file",
                [| "--summaryFile" |],
                Required = true,
                Description = "Path to the summary file to upload.",
                Arity = ArgumentArity.ExactlyOne
            )

        let promptFile =
            new Option<string>(
                "--prompt-file",
                [| "--promptFile" |],
                Required = false,
                Description = "Optional path to the prompt file to upload.",
                Arity = ArgumentArity.ExactlyOne
            )

        let ownerId =
            new Option<OwnerId>(
                OptionName.OwnerId,
                Required = false,
                Description = "The repository's owner ID <Guid>.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> OwnerId.Empty)
            )

        let ownerName =
            new Option<string>(
                OptionName.OwnerName,
                Required = false,
                Description = "The repository's owner name. [default: current owner]",
                Arity = ArgumentArity.ExactlyOne
            )

        let organizationId =
            new Option<OrganizationId>(
                OptionName.OrganizationId,
                Required = false,
                Description = "The organization's ID <Guid>.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> OrganizationId.Empty)
            )

        let organizationName =
            new Option<string>(
                OptionName.OrganizationName,
                Required = false,
                Description = "The organization's name. [default: current organization]",
                Arity = ArgumentArity.ExactlyOne
            )

        let repositoryId =
            new Option<RepositoryId>(
                OptionName.RepositoryId,
                Required = false,
                Description = "The repository's ID <Guid>.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> RepositoryId.Empty)
            )

        let repositoryName =
            new Option<string>(
                OptionName.RepositoryName,
                Required = false,
                Description = "The repository's name. [default: current repository]",
                Arity = ArgumentArity.ExactlyOne
            )

    type private AddSummaryResult = { WorkItemId: WorkItemId; SummaryArtifactId: ArtifactId; PromptArtifactId: ArtifactId option }

    let private tryParseGuid (value: string) (errorMessage: string) (parseResult: ParseResult) =
        let mutable parsed = Guid.Empty

        if String.IsNullOrWhiteSpace(value)
           || Guid.TryParse(value, &parsed) = false
           || parsed = Guid.Empty then
            Error(GraceError.Create errorMessage (getCorrelationId parseResult))
        else
            Ok parsed

    let private inferMimeType (filePath: string) =
        match Path.GetExtension(filePath).ToLowerInvariant() with
        | ".md" -> "text/markdown"
        | ".txt" -> "text/plain"
        | ".json" -> "application/json"
        | _ -> "application/octet-stream"

    let private computeSha256 (contentBytes: byte array) =
        use hasher = SHA256.Create()
        let hash = hasher.ComputeHash(contentBytes)
        Convert.ToHexString(hash).ToLowerInvariant()

    let private uploadArtifactContent (uploadUri: UriWithSharedAccessSignature) (contentBytes: byte array) =
        task {
            use stream = new MemoryStream(contentBytes)
            let blobClient = BlobClient(uploadUri)
            let! _ = blobClient.UploadAsync(stream, overwrite = true)
            return ()
        }

    let private createAndUploadArtifact (graceIds: GraceIds) (artifactType: ArtifactType) (filePath: string) =
        task {
            let correlationId = graceIds.CorrelationId
            let fileBytes = File.ReadAllBytes(filePath)

            let createParameters =
                Parameters.Artifact.CreateArtifactParameters(
                    ArtifactType = getDiscriminatedUnionCaseName artifactType,
                    MimeType = inferMimeType filePath,
                    Size = int64 fileBytes.LongLength,
                    Sha256 = computeSha256 fileBytes,
                    OwnerId = graceIds.OwnerIdString,
                    OwnerName = graceIds.OwnerName,
                    OrganizationId = graceIds.OrganizationIdString,
                    OrganizationName = graceIds.OrganizationName,
                    RepositoryId = graceIds.RepositoryIdString,
                    RepositoryName = graceIds.RepositoryName,
                    CorrelationId = correlationId
                )

            match! Artifact.Create(createParameters) with
            | Error error -> return Error error
            | Ok createResult ->
                let createdArtifact = createResult.ReturnValue

                try
                    do! uploadArtifactContent createdArtifact.UploadUri fileBytes
                    return Ok createdArtifact.ArtifactId
                with
                | ex ->
                    return
                        Error(GraceError.Create ($"Failed to upload {getDiscriminatedUnionCaseName artifactType} artifact content: {ex.Message}") correlationId)
        }

    let private linkArtifactToWorkItem (graceIds: GraceIds) (workItemId: WorkItemId) (artifactId: ArtifactId) =
        let parameters =
            Parameters.WorkItem.LinkArtifactParameters(
                WorkItemId = workItemId.ToString(),
                ArtifactId = artifactId.ToString(),
                OwnerId = graceIds.OwnerIdString,
                OwnerName = graceIds.OwnerName,
                OrganizationId = graceIds.OrganizationIdString,
                OrganizationName = graceIds.OrganizationName,
                RepositoryId = graceIds.RepositoryIdString,
                RepositoryName = graceIds.RepositoryName,
                CorrelationId = graceIds.CorrelationId
            )

        WorkItem.LinkArtifact(parameters)

    let private addSummaryHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames
                let workItemIdRaw = parseResult.GetValue(Options.workItemId)
                let summaryFilePath = parseResult.GetValue(Options.summaryFile)

                let promptFilePath =
                    parseResult.GetValue(Options.promptFile)
                    |> Option.ofObj
                    |> Option.defaultValue String.Empty

                match tryParseGuid workItemIdRaw (WorkItemError.getErrorMessage WorkItemError.InvalidWorkItemId) parseResult with
                | Error error -> return Error error
                | Ok workItemId ->
                    if not <| File.Exists(summaryFilePath) then
                        return Error(GraceError.Create $"Summary file does not exist: {summaryFilePath}" (getCorrelationId parseResult))
                    elif
                        not (String.IsNullOrWhiteSpace(promptFilePath))
                        && not <| File.Exists(promptFilePath)
                    then
                        return Error(GraceError.Create $"Prompt file does not exist: {promptFilePath}" (getCorrelationId parseResult))
                    else
                        match! createAndUploadArtifact graceIds ArtifactType.AgentSummary summaryFilePath with
                        | Error error -> return Error error
                        | Ok summaryArtifactId ->
                            match! linkArtifactToWorkItem graceIds workItemId summaryArtifactId with
                            | Error error -> return Error error
                            | Ok _ ->
                                let! promptArtifactResult =
                                    task {
                                        if String.IsNullOrWhiteSpace(promptFilePath) then
                                            return Ok Option.None
                                        else
                                            match! createAndUploadArtifact graceIds ArtifactType.Prompt promptFilePath with
                                            | Error error -> return Error error
                                            | Ok promptArtifactId ->
                                                match! linkArtifactToWorkItem graceIds workItemId promptArtifactId with
                                                | Error error -> return Error error
                                                | Ok _ -> return Ok(Option.Some promptArtifactId)
                                    }

                                match promptArtifactResult with
                                | Error error -> return Error error
                                | Ok promptArtifactId ->
                                    let result = { WorkItemId = workItemId; SummaryArtifactId = summaryArtifactId; PromptArtifactId = promptArtifactId }

                                    if
                                        not (parseResult |> json)
                                        && not (parseResult |> silent)
                                    then
                                        AnsiConsole.MarkupLine(
                                            $"[green]Linked AgentSummary artifact[/] {Markup.Escape(summaryArtifactId.ToString())} [green]to work item[/] {Markup.Escape(workItemId.ToString())}"
                                        )

                                        match promptArtifactId with
                                        | Some artifactId ->
                                            AnsiConsole.MarkupLine(
                                                $"[green]Linked Prompt artifact[/] {Markup.Escape(artifactId.ToString())} [green]to work item[/] {Markup.Escape(workItemId.ToString())}"
                                            )
                                        | None -> ()

                                    return Ok(GraceReturnValue.Create result graceIds.CorrelationId)
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    type AddSummary() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                let! result = addSummaryHandler parseResult
                return result |> renderOutput parseResult
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

        let agentCommand = new Command("agent", Description = "Agent workflow commands.")

        let addSummaryCommand =
            new Command("add-summary", Description = "Upload agent summary/prompt artifacts and link them to a work item.")
            |> addOption Options.workItemId
            |> addOption Options.summaryFile
            |> addOption Options.promptFile
            |> addCommonOptions

        addSummaryCommand.Action <- new AddSummary()
        agentCommand.Subcommands.Add(addSummaryCommand)
        agentCommand
