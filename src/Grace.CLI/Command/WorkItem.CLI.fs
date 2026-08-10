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
open Grace.Types.WorkItem
open Grace.Types.Common
open Spectre.Console
open Spectre.Console.Json
open System
open System.CommandLine
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks

/// Groups the work item command parser, handlers, and output helpers.
module WorkItemCommand =

    /// Classifies the supported user-facing work-item attachment kinds at the CLI boundary.
    type internal AttachmentType =
        | Summary
        | Prompt
        | Notes

        /// Returns the canonical lower-case value used by CLI output and existing work-item attachment APIs.
        member this.Label =
            match this with
            | Summary -> "summary"
            | Prompt -> "prompt"
            | Notes -> "notes"

        /// Maps CLI attachment classification to the existing durable artifact type.
        member this.ArtifactType =
            match this with
            | Summary -> ArtifactType.AgentSummary
            | Prompt -> ArtifactType.Prompt
            | Notes -> ArtifactType.ReviewNotes

    /// Defines the options parsed by the work item command handlers.
    module private Options =
        let workItemId =
            new Option<string>(
                "--work-item-id",
                [| "--work-item"; "-w" |],
                Required = false,
                Description = "The work item ID <Guid>. Used only on create to override the generated ID.",
                Arity = ArgumentArity.ExactlyOne
            )

        let title = new Option<string>("--title", Required = true, Description = "Title for the work item.", Arity = ArgumentArity.ExactlyOne)

        let description =
            new Option<string>(
                OptionName.Description,
                [| "-d" |],
                Required = false,
                Description = "Description for the work item.",
                Arity = ArgumentArity.ExactlyOne
            )

        /// Selects the durable status assigned by the set-status command.
        let status =
            (new Option<string>(
                OptionName.Status,
                [| "-s" |],
                Required = true,
                Description = "Status to assign to the work item.",
                Arity = ArgumentArity.ExactlyOne
            ))
                .AcceptOnlyFromAmong(listCases<WorkItemStatus> ())

        let file =
            new Option<string>(
                "--file",
                [| "-f" |],
                Required = false,
                Description = "Read attachment content from this file path.",
                Arity = ArgumentArity.ExactlyOne
            )

        let text =
            new Option<string>("--text", [| "-t" |], Required = false, Description = "Attach inline text content directly.", Arity = ArgumentArity.ExactlyOne)

        let descriptionText =
            new Option<string>(
                "--text",
                [| "-t" |],
                Required = false,
                Description = "Markdown text for the current work-item description.",
                Arity = ArgumentArity.ExactlyOne
            )

        let stdin = new Option<bool>("--stdin", Required = false, Description = "Read attachment content from standard input.", Arity = ArgumentArity.ZeroOrOne)

        let attachmentType =
            let option =
                new Option<AttachmentType>(
                    "--type",
                    Required = true,
                    Description = "Attachment type to target: summary, prompt, or notes.",
                    Arity = ArgumentArity.ExactlyOne
                )

            option.CustomParser <-
                Func<ArgumentResult, AttachmentType> (fun argumentResult ->
                    match argumentResult.Tokens[0].Value with
                    | "summary" -> AttachmentType.Summary
                    | "prompt" -> AttachmentType.Prompt
                    | "notes" -> AttachmentType.Notes
                    | value ->
                        argumentResult.AddError($"Argument '{value}' not recognized. Must be one of: summary, prompt, notes.")
                        Unchecked.defaultof<AttachmentType>)

            option.AcceptOnlyFromAmong([| "summary"; "prompt"; "notes" |])

        let latest =
            new Option<bool>(
                "--latest",
                Required = false,
                Description = "Select the most recently created attachment for the requested type.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> false)
            )

        let artifactId = new Option<string>("--artifact-id", Required = true, Description = "Attachment artifact ID <Guid>.", Arity = ArgumentArity.ExactlyOne)

        let deleteReason =
            new Option<string>("--delete-reason", Required = true, Description = "Reason for deleting the attachment.", Arity = ArgumentArity.ExactlyOne)

        let outputFile =
            new Option<string>(
                "--output-file",
                [| "-f" |],
                Required = true,
                Description = "Write downloaded attachment bytes to this file path.",
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

    /// Groups the work item command parser, handlers, and output helpers.
    module private Arguments =
        let workItemIdentifier = new Argument<string>("work-item", Description = "Work item ID <Guid> or work item number <positive integer>.")

        let referenceId = new Argument<string>("reference-id", Description = "Reference ID <Guid>.")

        let promotionSetId = new Argument<string>("promotion-set-id", Description = "Promotion set ID <Guid>.")

    /// Defines structured data exchanged by CLI helpers.
    type private AttachmentInput = { Bytes: byte array; MimeType: string }

    /// Defines structured data exchanged by CLI helpers.
    type private AttachmentResult = { WorkItem: string; ArtifactId: ArtifactId; ArtifactType: string }

    /// Defines structured data exchanged by CLI helpers.
    type private AttachmentDownloadResult = { WorkItem: string; ArtifactId: ArtifactId; AttachmentType: string; OutputFile: string; Size: int64 }

    /// Tries to map parse guid and returns a GraceError instead of throwing on unsupported input.
    let private tryParseGuid (value: string) (error: WorkItemError) (parseResult: ParseResult) =
        let mutable parsed = Guid.Empty

        if String.IsNullOrWhiteSpace(value)
           || Guid.TryParse(value, &parsed) = false
           || parsed = Guid.Empty then
            Error(GraceError.Create (WorkItemError.getErrorMessage error) (getCorrelationId parseResult))
        else
            Ok parsed

    /// Tries to map normalize work item identifier and returns a GraceError instead of throwing on unsupported input.
    let private tryNormalizeWorkItemIdentifier (value: string) (parseResult: ParseResult) =
        let mutable parsedGuid = Guid.Empty

        if String.IsNullOrWhiteSpace(value) then
            Error(GraceError.Create (WorkItemError.getErrorMessage WorkItemError.InvalidWorkItemId) (getCorrelationId parseResult))
        elif
            Guid.TryParse(value, &parsedGuid)
            && parsedGuid <> Guid.Empty
        then
            Ok(parsedGuid.ToString())
        else
            let mutable parsedNumber = 0L

            if Int64.TryParse(value, &parsedNumber) then
                if parsedNumber > 0L then
                    Ok(parsedNumber.ToString())
                else
                    Error(GraceError.Create (WorkItemError.getErrorMessage WorkItemError.InvalidWorkItemNumber) (getCorrelationId parseResult))
            else
                Error(GraceError.Create (WorkItemError.getErrorMessage WorkItemError.InvalidWorkItemId) (getCorrelationId parseResult))

    /// Submits a work-item creation request while keeping Spectre progress output in sync.
    let private createWorkItemWithProgress (parameters: Parameters.WorkItem.CreateWorkItemParameters) =
        progress
            .Columns(progressColumns)
            .StartAsync(fun progressContext ->
                task {
                    let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                    let! result = WorkItem.Create(parameters)
                    t0.Increment(100.0)
                    return result
                })

    /// Infers command metadata from the supplied input.
    let private inferMimeTypeFromFilePath (filePath: string) =
        match Path.GetExtension(filePath).ToLowerInvariant() with
        | ".md" -> "text/markdown"
        | ".txt" -> "text/plain"
        | ".json" -> "application/json"
        | _ -> "application/octet-stream"

    /// Coordinates compute sha256 behavior for this CLI command path.
    let private computeSha256 (contentBytes: byte array) =
        use hasher = SHA256.Create()
        let hash = hasher.ComputeHash(contentBytes)
        Convert.ToHexString(hash).ToLowerInvariant()

    /// Reads upload artifact content data needed by the command workflow without changing remote state.
    let private uploadArtifactContent (uploadUri: UriWithSharedAccessSignature) (contentBytes: byte array) =
        task {
            use stream = new MemoryStream(contentBytes)
            let blobClient = BlobClient(uploadUri)
            let! _ = blobClient.UploadAsync(stream, overwrite = true)
            return ()
        }

    /// Tries to map get attachment input and returns a GraceError instead of throwing on unsupported input.
    let private tryGetAttachmentInput (parseResult: ParseResult) =
        task {
            let filePath =
                parseResult.GetValue(Options.file)
                |> Option.ofObj
                |> Option.defaultValue String.Empty

            let textInput =
                parseResult.GetValue(Options.text)
                |> Option.ofObj
                |> Option.defaultValue String.Empty

            let readFromStdin = parseResult.GetValue(Options.stdin)

            let selectedCount =
                (if String.IsNullOrWhiteSpace(filePath) then 0 else 1)
                + (if String.IsNullOrWhiteSpace(textInput) then 0 else 1)
                + (if readFromStdin then 1 else 0)

            if selectedCount <> 1 then
                return Error(GraceError.Create "Specify exactly one of --file, --text, or --stdin." (getCorrelationId parseResult))
            elif not <| String.IsNullOrWhiteSpace(filePath) then
                if not <| File.Exists(filePath) then
                    return Error(GraceError.Create $"File does not exist: {filePath}" (getCorrelationId parseResult))
                else
                    let bytes = File.ReadAllBytes(filePath)
                    return Ok { Bytes = bytes; MimeType = inferMimeTypeFromFilePath filePath }
            elif not <| String.IsNullOrWhiteSpace(textInput) then
                return Ok { Bytes = Encoding.UTF8.GetBytes(textInput); MimeType = "text/plain" }
            else
                let! stdinText = Console.In.ReadToEndAsync()
                return Ok { Bytes = Encoding.UTF8.GetBytes(stdinText); MimeType = "text/plain" }
        }

    /// Adds a work-item attachment and uploads the local artifact content for it.
    let private createAndUploadArtifact (graceIds: GraceIds) (workItemId: WorkItemId) (artifactType: ArtifactType) (attachmentInput: AttachmentInput) =
        task {
            let createParameters =
                Parameters.Artifact.CreateArtifactParameters(
                    ArtifactType = getDiscriminatedUnionCaseName artifactType,
                    WorkItemId = workItemId.ToString(),
                    MimeType = attachmentInput.MimeType,
                    Size = int64 attachmentInput.Bytes.LongLength,
                    Sha256 = computeSha256 attachmentInput.Bytes,
                    OwnerId = graceIds.OwnerIdString,
                    OwnerName = graceIds.OwnerName,
                    OrganizationId = graceIds.OrganizationIdString,
                    OrganizationName = graceIds.OrganizationName,
                    RepositoryId = graceIds.RepositoryIdString,
                    RepositoryName = graceIds.RepositoryName,
                    CorrelationId = graceIds.CorrelationId
                )

            match! Artifact.Create(createParameters) with
            | Error error -> return Error error
            | Ok createResult ->
                let createdArtifact = createResult.ReturnValue

                try
                    do! uploadArtifactContent createdArtifact.UploadUri attachmentInput.Bytes
                    return Ok createdArtifact.ArtifactId
                with
                | ex ->
                    return
                        Error(
                            GraceError.Create
                                ($"Failed to upload {getDiscriminatedUnionCaseName artifactType} artifact content: {ex.Message}")
                                graceIds.CorrelationId
                        )
        }

    /// Tries to map resolve output file path and returns a GraceError instead of throwing on unsupported input.
    let private tryResolveOutputFilePath (parseResult: ParseResult) =
        let outputFileRaw =
            parseResult.GetValue(Options.outputFile)
            |> Option.ofObj
            |> Option.defaultValue String.Empty

        if String.IsNullOrWhiteSpace outputFileRaw then
            Error(GraceError.Create "Output file path is required." (getCorrelationId parseResult))
        else
            try
                let outputFilePath = Path.GetFullPath(outputFileRaw)
                let outputFileName = Path.GetFileName(outputFilePath)

                if
                    outputFileName.IndexOfAny(Path.GetInvalidFileNameChars())
                    >= 0
                then
                    Error(GraceError.Create $"Output file path is invalid: {outputFileRaw}" (getCorrelationId parseResult))
                elif Directory.Exists(outputFilePath) then
                    Error(GraceError.Create $"Output file path points to a directory: {outputFilePath}" (getCorrelationId parseResult))
                else
                    Ok outputFilePath

            with
            | ex -> Error(GraceError.Create $"Output file path is invalid: {ex.Message}" (getCorrelationId parseResult))

    /// Reads download attachment bytes data needed by the command workflow without changing remote state.
    let private downloadAttachmentBytes (downloadUri: string) (parseResult: ParseResult) =
        task {
            if String.IsNullOrWhiteSpace(downloadUri) then
                return Error(GraceError.Create "Attachment download URI was empty." (getCorrelationId parseResult))
            else
                try
                    let blobClient = BlobClient(Uri(downloadUri))
                    let! downloadResult = blobClient.DownloadContentAsync()
                    return Ok(downloadResult.Value.Content.ToArray())
                with
                | ex -> return Error(GraceError.Create ($"Failed to download attachment bytes: {ex.Message}") (getCorrelationId parseResult))
        }

    /// Routes the create handler command from parsed options through validation, the SDK call, and result rendering.
    let private createHandlerImpl (parseResult: ParseResult) =
        if parseResult |> verbose then printParseResult parseResult
        let graceIds = parseResult |> getNormalizedIdsAndNames

        let title = parseResult.GetValue(Options.title)

        if String.IsNullOrWhiteSpace title then
            Task.FromResult(Error(GraceError.Create "Title is required." (getCorrelationId parseResult)))
        else
            let description =
                parseResult.GetValue(Options.description)
                |> Option.ofObj
                |> Option.defaultValue String.Empty

            let workItemId =
                parseResult.GetValue(Options.workItemId)
                |> Option.ofObj
                |> Option.defaultValue (Guid.NewGuid().ToString())

            let parameters =
                Parameters.WorkItem.CreateWorkItemParameters(
                    WorkItemId = workItemId,
                    Title = title,
                    Description = description,
                    OwnerId = graceIds.OwnerIdString,
                    OwnerName = graceIds.OwnerName,
                    OrganizationId = graceIds.OrganizationIdString,
                    OrganizationName = graceIds.OrganizationName,
                    RepositoryId = graceIds.RepositoryIdString,
                    RepositoryName = graceIds.RepositoryName,
                    CorrelationId = graceIds.CorrelationId
                )

            if parseResult |> hasOutput then
                createWorkItemWithProgress parameters
            else
                WorkItem.Create(parameters)

    /// Routes the create command from parsed options through validation, the SDK call, and result rendering.
    let private createHandler (parseResult: ParseResult) =
        task {
            try
                return! createHandlerImpl parseResult
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    /// Executes the create command by binding ParseResult values to the SDK request and CLI output contract.
    type Create() =
        inherit AsynchronousCommandLineAction()

        /// Runs the asynchronous create action when System.CommandLine dispatches the parsed command.
        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                let! result = createHandler parseResult
                return result |> renderOutput parseResult
            }

    /// Routes the show command from parsed options through validation, the SDK call, and result rendering.
    let private showHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames
                let workItemRaw = parseResult.GetValue(Arguments.workItemIdentifier)

                match tryNormalizeWorkItemIdentifier workItemRaw parseResult with
                | Error error -> return Error error
                | Ok workItem ->
                    let parameters =
                        Parameters.WorkItem.GetWorkItemParameters(
                            WorkItemId = workItem,
                            OwnerId = graceIds.OwnerIdString,
                            OwnerName = graceIds.OwnerName,
                            OrganizationId = graceIds.OrganizationIdString,
                            OrganizationName = graceIds.OrganizationName,
                            RepositoryId = graceIds.RepositoryIdString,
                            RepositoryName = graceIds.RepositoryName,
                            CorrelationId = graceIds.CorrelationId
                        )

                    let! result = WorkItem.Get(parameters)

                    match result with
                    | Ok graceReturnValue ->
                        if parseResult |> hasOutput then
                            let jsonText = JsonText(serialize graceReturnValue.ReturnValue)
                            AnsiConsole.Write(jsonText)
                            AnsiConsole.WriteLine()

                        return Ok graceReturnValue
                    | Error error -> return Error error
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    /// Executes the show command by binding ParseResult values to the SDK request and CLI output contract.
    type Show() =
        inherit AsynchronousCommandLineAction()

        /// Runs the asynchronous show action when System.CommandLine dispatches the parsed command.
        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                let! result = showHandler parseResult
                return result |> renderOutput parseResult
            }

    /// Routes the set-status command from parsed options through validation and the existing SDK update call.
    let private setStatusHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames
                let workItemRaw = parseResult.GetValue(Arguments.workItemIdentifier)

                match tryNormalizeWorkItemIdentifier workItemRaw parseResult with
                | Error error -> return Error error
                | Ok workItem ->
                    let statusValue = parseResult.GetValue(Options.status)

                    match discriminatedUnionFromString<WorkItemStatus> statusValue with
                    | None -> return Error(GraceError.Create (WorkItemError.getErrorMessage WorkItemError.InvalidStatus) (getCorrelationId parseResult))
                    | Some status ->
                        let parameters =
                            Parameters.WorkItem.UpdateWorkItemParameters(
                                WorkItemId = workItem,
                                Status = status.ToString(),
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                CorrelationId = graceIds.CorrelationId
                            )

                        return! WorkItem.Update(parameters)
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    /// Executes set-status while preserving the existing work item update result envelope.
    type SetStatus() =
        inherit AsynchronousCommandLineAction()

        /// Runs the asynchronous set-status action when System.CommandLine dispatches the parsed command.
        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                let! result = setStatusHandler parseResult
                return result |> renderOutput parseResult
            }

    /// Routes description replacement from parsed Markdown through the dedicated SDK operation.
    let private setDescriptionHandler (parseResult: ParseResult) =
        task {
            try
                let graceIds = parseResult |> getNormalizedIdsAndNames
                let workItemRaw = parseResult.GetValue(Arguments.workItemIdentifier)
                let text = parseResult.GetValue(Options.descriptionText)

                match tryNormalizeWorkItemIdentifier workItemRaw parseResult with
                | Error error -> return Error error
                | Ok _ when String.IsNullOrWhiteSpace(text) -> return Error(GraceError.Create "Description text is required." (getCorrelationId parseResult))
                | Ok workItem ->
                    let parameters =
                        Parameters.WorkItem.SetWorkItemDescriptionParameters(
                            WorkItemId = workItem,
                            Text = text,
                            OwnerId = graceIds.OwnerIdString,
                            OwnerName = graceIds.OwnerName,
                            OrganizationId = graceIds.OrganizationIdString,
                            OrganizationName = graceIds.OrganizationName,
                            RepositoryId = graceIds.RepositoryIdString,
                            RepositoryName = graceIds.RepositoryName,
                            CorrelationId = graceIds.CorrelationId
                        )

                    return! WorkItem.SetDescription(parameters)
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    /// Executes the dedicated description set command through the normal CLI output contract.
    type SetDescription() =
        inherit AsynchronousCommandLineAction()

        /// Runs description replacement after System.CommandLine binds the work-item identifier and Markdown text.
        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                let! result = setDescriptionHandler parseResult
                return result |> renderOutput parseResult
            }

    /// Routes the link reference command from parsed options through validation, the SDK call, and result rendering.
    let private linkReferenceHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames
                let workItemRaw = parseResult.GetValue(Arguments.workItemIdentifier)
                let referenceIdRaw = parseResult.GetValue(Arguments.referenceId)

                match tryNormalizeWorkItemIdentifier workItemRaw parseResult with
                | Error error -> return Error error
                | Ok workItem ->
                    match tryParseGuid referenceIdRaw WorkItemError.InvalidReferenceId parseResult with
                    | Error error -> return Error error
                    | Ok referenceId ->
                        let parameters =
                            Parameters.WorkItem.LinkReferenceParameters(
                                WorkItemId = workItem,
                                ReferenceId = referenceId.ToString(),
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                CorrelationId = graceIds.CorrelationId
                            )

                        return! WorkItem.LinkReference(parameters)
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    /// Executes the link reference command by binding ParseResult values to the SDK request and CLI output contract.
    type LinkReference() =
        inherit AsynchronousCommandLineAction()

        /// Runs the asynchronous link reference action when System.CommandLine dispatches the parsed command.
        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                let! result = linkReferenceHandler parseResult
                return result |> renderOutput parseResult
            }

    /// Routes the link promotion set command from parsed options through validation, the SDK call, and result rendering.
    let private linkPromotionSetHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames
                let workItemRaw = parseResult.GetValue(Arguments.workItemIdentifier)
                let promotionSetIdRaw = parseResult.GetValue(Arguments.promotionSetId)

                match tryNormalizeWorkItemIdentifier workItemRaw parseResult with
                | Error error -> return Error error
                | Ok workItem ->
                    match tryParseGuid promotionSetIdRaw WorkItemError.InvalidPromotionSetId parseResult with
                    | Error error -> return Error error
                    | Ok promotionSetId ->
                        let parameters =
                            Parameters.WorkItem.LinkPromotionSetParameters(
                                WorkItemId = workItem,
                                PromotionSetId = promotionSetId.ToString(),
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                CorrelationId = graceIds.CorrelationId
                            )

                        return! WorkItem.LinkPromotionSet(parameters)
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    /// Executes the link promotion set command by binding ParseResult values to the SDK request and CLI output contract.
    type LinkPromotionSet() =
        inherit AsynchronousCommandLineAction()

        /// Runs the asynchronous link promotion set action when System.CommandLine dispatches the parsed command.
        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                let! result = linkPromotionSetHandler parseResult
                return result |> renderOutput parseResult
            }

    /// Routes canonical attachment creation through input validation, upload, linking, and result rendering once.
    let private attachmentsAddHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames
                let workItemRaw = parseResult.GetValue(Arguments.workItemIdentifier)
                let attachmentType = parseResult.GetValue(Options.attachmentType)

                match tryNormalizeWorkItemIdentifier workItemRaw parseResult with
                | Error error -> return Error error
                | Ok workItem ->
                    let getParameters =
                        Parameters.WorkItem.GetWorkItemParameters(
                            WorkItemId = workItem,
                            OwnerId = graceIds.OwnerIdString,
                            OrganizationId = graceIds.OrganizationIdString,
                            RepositoryId = graceIds.RepositoryIdString,
                            CorrelationId = graceIds.CorrelationId
                        )

                    match! WorkItem.Get(getParameters) with
                    | Error error -> return Error error
                    | Ok workItemResult ->
                        match! tryGetAttachmentInput parseResult with
                        | Error error -> return Error error
                        | Ok attachmentInput ->
                            match! createAndUploadArtifact graceIds workItemResult.ReturnValue.WorkItemId attachmentType.ArtifactType attachmentInput with
                            | Error error -> return Error error
                            | Ok artifactId ->
                                let linkParameters =
                                    Parameters.WorkItem.LinkArtifactParameters(
                                        WorkItemId = workItem,
                                        ArtifactId = artifactId.ToString(),
                                        OwnerId = graceIds.OwnerIdString,
                                        OwnerName = graceIds.OwnerName,
                                        OrganizationId = graceIds.OrganizationIdString,
                                        OrganizationName = graceIds.OrganizationName,
                                        RepositoryId = graceIds.RepositoryIdString,
                                        RepositoryName = graceIds.RepositoryName,
                                        CorrelationId = graceIds.CorrelationId
                                    )

                                match! WorkItem.LinkArtifact(linkParameters) with
                                | Error error -> return Error error
                                | Ok _ ->
                                    let result = { WorkItem = workItem; ArtifactId = artifactId; ArtifactType = attachmentType.Label }

                                    if
                                        not (parseResult |> json)
                                        && not (parseResult |> silent)
                                    then
                                        AnsiConsole.MarkupLine(
                                            $"[green]Attached {Markup.Escape(attachmentType.Label)} content[/] [grey](artifact {Markup.Escape(artifactId.ToString())})[/] [green]to work item[/] {Markup.Escape(workItem)}"
                                        )

                                    return Ok(GraceReturnValue.Create result graceIds.CorrelationId)
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    /// Executes canonical attachment creation by binding one typed command to the generic workflow.
    type AttachmentsAdd() =
        inherit AsynchronousCommandLineAction()

        /// Runs the asynchronous attachments add action when System.CommandLine dispatches the parsed command.
        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                let! result = attachmentsAddHandler parseResult
                return result |> renderOutput parseResult
            }

    /// Writes attachment list table data through the CLI output contract.
    let private writeAttachmentListTable (attachments: Parameters.WorkItem.ListWorkItemAttachmentsResult) =
        let table = Table(Border = TableBorder.Rounded)
        table.AddColumn("[bold]Artifact ID[/]") |> ignore
        table.AddColumn("[bold]Type[/]") |> ignore
        table.AddColumn("[bold]Mime type[/]") |> ignore
        table.AddColumn("[bold]Size (bytes)[/]") |> ignore
        table.AddColumn("[bold]Created at[/]") |> ignore

        let attachmentArray = attachments.Attachments |> Seq.toArray
        let mutable i = 0

        while i < attachmentArray.Length do
            let attachment = attachmentArray[i]

            table.AddRow(
                Markup.Escape(attachment.ArtifactId),
                Markup.Escape(attachment.AttachmentType),
                Markup.Escape(attachment.MimeType),
                attachment.Size.ToString(),
                Markup.Escape(attachment.CreatedAt)
            )
            |> ignore

            i <- i + 1

        AnsiConsole.MarkupLine($"[bold]Work item ID:[/] {Markup.Escape(attachments.WorkItemId)}")
        AnsiConsole.MarkupLine($"[bold]Work item number:[/] {attachments.WorkItemNumber}")
        AnsiConsole.Write(table)

    /// Writes show attachment output data through the CLI output contract.
    let private writeShowAttachmentOutput (workItem: string) (showResult: Parameters.WorkItem.ShowWorkItemAttachmentResult) =
        let selection = if showResult.SelectedUsingLatest then "latest" else "earliest"

        AnsiConsole.MarkupLine($"[bold]Work item ID:[/] {Markup.Escape(showResult.WorkItemId)}")
        AnsiConsole.MarkupLine($"[bold]Work item number:[/] {showResult.WorkItemNumber}")
        AnsiConsole.MarkupLine($"[bold]Attachment type:[/] {Markup.Escape(showResult.AttachmentType)}")
        AnsiConsole.MarkupLine($"[bold]Artifact ID:[/] {Markup.Escape(showResult.ArtifactId)}")
        AnsiConsole.MarkupLine($"[bold]Mime type:[/] {Markup.Escape(showResult.MimeType)}")
        AnsiConsole.MarkupLine($"[bold]Size (bytes):[/] {showResult.Size}")
        AnsiConsole.MarkupLine($"[bold]Created at:[/] {Markup.Escape(showResult.CreatedAt)}")
        AnsiConsole.MarkupLine($"[bold]Selection:[/] {selection}")
        AnsiConsole.MarkupLine($"[bold]Available attachments of this type:[/] {showResult.AvailableAttachmentCount}")
        AnsiConsole.WriteLine()

        if showResult.IsTextContent then
            AnsiConsole.MarkupLine("[bold]Content:[/]")
            Console.WriteLine(showResult.Content)
        else
            AnsiConsole.MarkupLine("[yellow]Attachment content is binary or non-text and was not rendered inline.[/]")

            AnsiConsole.MarkupLine(
                $"[yellow]Use[/] [bold]grace workitem attachments download {Markup.Escape(workItem)} --artifact-id {Markup.Escape(showResult.ArtifactId)} --output-file <path>[/] [yellow]to save this attachment.[/]"
            )

    /// Routes the attachments list handler command from parsed options through validation, the SDK call, and result rendering.
    let private attachmentsListHandlerImpl (parseResult: ParseResult) =
        task {
            if parseResult |> verbose then printParseResult parseResult
            let graceIds = parseResult |> getNormalizedIdsAndNames
            let workItemRaw = parseResult.GetValue(Arguments.workItemIdentifier)

            match tryNormalizeWorkItemIdentifier workItemRaw parseResult with
            | Error error -> return Error error
            | Ok workItem ->
                let parameters =
                    Parameters.WorkItem.ListWorkItemAttachmentsParameters(
                        WorkItemId = workItem,
                        OwnerId = graceIds.OwnerIdString,
                        OwnerName = graceIds.OwnerName,
                        OrganizationId = graceIds.OrganizationIdString,
                        OrganizationName = graceIds.OrganizationName,
                        RepositoryId = graceIds.RepositoryIdString,
                        RepositoryName = graceIds.RepositoryName,
                        CorrelationId = graceIds.CorrelationId
                    )

                let! result = WorkItem.ListAttachments(parameters)

                match result with
                | Error error -> return Error error
                | Ok graceReturnValue ->
                    if
                        not (parseResult |> json)
                        && not (parseResult |> silent)
                    then
                        writeAttachmentListTable graceReturnValue.ReturnValue

                    return Ok graceReturnValue
        }

    /// Routes the attachments list command from parsed options through validation, the SDK call, and result rendering.
    let private attachmentsListHandler (parseResult: ParseResult) =
        task {
            try
                return! attachmentsListHandlerImpl parseResult
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    /// Executes the attachments list command by binding ParseResult values to the SDK request and CLI output contract.
    type AttachmentsList() =
        inherit AsynchronousCommandLineAction()

        /// Runs the asynchronous attachments list action when System.CommandLine dispatches the parsed command.
        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                let! result = attachmentsListHandler parseResult
                return result |> renderOutput parseResult
            }

    /// Routes the attachments show handler command from parsed options through validation, the SDK call, and result rendering.
    let private attachmentsShowHandlerImpl (parseResult: ParseResult) =
        task {
            if parseResult |> verbose then printParseResult parseResult
            let graceIds = parseResult |> getNormalizedIdsAndNames
            let workItemRaw = parseResult.GetValue(Arguments.workItemIdentifier)

            match tryNormalizeWorkItemIdentifier workItemRaw parseResult with
            | Error error -> return Error error
            | Ok workItem ->
                let attachmentType = parseResult.GetValue(Options.attachmentType)
                let latest = parseResult.GetValue(Options.latest)

                let parameters =
                    Parameters.WorkItem.ShowWorkItemAttachmentParameters(
                        WorkItemId = workItem,
                        AttachmentType = attachmentType.Label,
                        Latest = latest,
                        OwnerId = graceIds.OwnerIdString,
                        OwnerName = graceIds.OwnerName,
                        OrganizationId = graceIds.OrganizationIdString,
                        OrganizationName = graceIds.OrganizationName,
                        RepositoryId = graceIds.RepositoryIdString,
                        RepositoryName = graceIds.RepositoryName,
                        CorrelationId = graceIds.CorrelationId
                    )

                let! result = WorkItem.ShowAttachment(parameters)

                match result with
                | Error error -> return Error error
                | Ok graceReturnValue ->
                    if
                        not (parseResult |> json)
                        && not (parseResult |> silent)
                    then
                        writeShowAttachmentOutput workItem graceReturnValue.ReturnValue

                    return Ok graceReturnValue
        }

    /// Routes the attachments show command from parsed options through validation, the SDK call, and result rendering.
    let private attachmentsShowHandler (parseResult: ParseResult) =
        task {
            try
                return! attachmentsShowHandlerImpl parseResult
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    /// Executes the attachments show command by binding ParseResult values to the SDK request and CLI output contract.
    type AttachmentsShow() =
        inherit AsynchronousCommandLineAction()

        /// Runs the asynchronous attachments show action when System.CommandLine dispatches the parsed command.
        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                let! result = attachmentsShowHandler parseResult
                return result |> renderOutput parseResult
            }

    /// Routes the attachments download handler command from parsed options through validation, the SDK call, and result rendering.
    let private attachmentsDownloadHandlerImpl (parseResult: ParseResult) =
        task {
            if parseResult |> verbose then printParseResult parseResult
            let workItemRaw = parseResult.GetValue(Arguments.workItemIdentifier)
            let artifactIdRaw = parseResult.GetValue(Options.artifactId)

            match tryNormalizeWorkItemIdentifier workItemRaw parseResult with
            | Error error -> return Error error
            | Ok workItem ->
                match tryParseGuid artifactIdRaw WorkItemError.InvalidArtifactId parseResult with
                | Error error -> return Error error
                | Ok artifactId ->
                    match tryResolveOutputFilePath parseResult with
                    | Error error -> return Error error
                    | Ok outputFilePath ->
                        let graceIds = parseResult |> getNormalizedIdsAndNames

                        let parameters =
                            Parameters.WorkItem.DownloadWorkItemAttachmentParameters(
                                WorkItemId = workItem,
                                ArtifactId = artifactId.ToString(),
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                CorrelationId = graceIds.CorrelationId
                            )

                        match! WorkItem.DownloadAttachment(parameters) with
                        | Error error -> return Error error
                        | Ok returnValue ->
                            match! downloadAttachmentBytes returnValue.ReturnValue.DownloadUri parseResult with
                            | Error error -> return Error error
                            | Ok bytes ->
                                let outputDirectory = Path.GetDirectoryName(outputFilePath)

                                if not (String.IsNullOrWhiteSpace outputDirectory) then
                                    Directory.CreateDirectory(outputDirectory)
                                    |> ignore

                                do! File.WriteAllBytesAsync(outputFilePath, bytes)

                                if
                                    not (parseResult |> json)
                                    && not (parseResult |> silent)
                                then
                                    AnsiConsole.MarkupLine(
                                        $"[green]Downloaded[/] {Markup.Escape(returnValue.ReturnValue.AttachmentType)} [green]attachment[/] [grey](artifact {Markup.Escape(returnValue.ReturnValue.ArtifactId)})[/] [green]to[/] {Markup.Escape(outputFilePath)}"
                                    )

                                let output =
                                    {
                                        WorkItem = workItem
                                        ArtifactId = artifactId
                                        AttachmentType = returnValue.ReturnValue.AttachmentType
                                        OutputFile = outputFilePath
                                        Size = int64 bytes.LongLength
                                    }

                                return Ok(GraceReturnValue.Create output graceIds.CorrelationId)
        }

    /// Routes the attachments download command from parsed options through validation, the SDK call, and result rendering.
    let private attachmentsDownloadHandler (parseResult: ParseResult) =
        task {
            try
                return! attachmentsDownloadHandlerImpl parseResult
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    /// Executes the attachments download command by binding ParseResult values to the SDK request and CLI output contract.
    type AttachmentsDownload() =
        inherit AsynchronousCommandLineAction()

        /// Runs the asynchronous attachments download action when System.CommandLine dispatches the parsed command.
        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                let! result = attachmentsDownloadHandler parseResult
                return result |> renderOutput parseResult
            }

    /// Routes recoverable attachment deletion through the aligned SDK contract.
    let private attachmentsDeleteHandler (parseResult: ParseResult) =
        task {
            let graceIds = parseResult |> getNormalizedIdsAndNames
            let workItemRaw = parseResult.GetValue(Arguments.workItemIdentifier)
            let artifactId = parseResult.GetValue(Options.artifactId)
            let deleteReason = parseResult.GetValue(Options.deleteReason)

            match tryNormalizeWorkItemIdentifier workItemRaw parseResult with
            | Error error -> return Error error
            | Ok workItem ->
                let parameters =
                    Parameters.WorkItem.DeleteWorkItemAttachmentParameters(
                        WorkItemId = workItem,
                        ArtifactId = artifactId,
                        DeleteReason = deleteReason,
                        OwnerId = graceIds.OwnerIdString,
                        OrganizationId = graceIds.OrganizationIdString,
                        RepositoryId = graceIds.RepositoryIdString,
                        CorrelationId = graceIds.CorrelationId
                    )

                let! result = WorkItem.DeleteAttachment(parameters)

                match result with
                | Ok value when
                    not (parseResult |> json)
                    && not (parseResult |> silent)
                    ->
                    AnsiConsole.MarkupLine(
                        $"[green]Logically deleted attachment[/] [grey]{Markup.Escape(value.ReturnValue.ArtifactId.ToString())}[/] [green]until[/] {Markup.Escape(value.ReturnValue.PhysicalDeletionAt.ToString())}."
                    )
                | _ -> ()

                return result
        }

    /// Executes the attachment delete command through the asynchronous CLI action boundary.
    type AttachmentsDelete() =
        inherit AsynchronousCommandLineAction()

        /// Runs recoverable deletion without performing side effects during command introspection.
        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                let! result = attachmentsDeleteHandler parseResult
                return result |> renderOutput parseResult
            }

    /// Routes attachment recovery through the aligned SDK contract.
    let private attachmentsUndeleteHandler (parseResult: ParseResult) =
        task {
            let graceIds = parseResult |> getNormalizedIdsAndNames
            let workItemRaw = parseResult.GetValue(Arguments.workItemIdentifier)
            let artifactId = parseResult.GetValue(Options.artifactId)

            match tryNormalizeWorkItemIdentifier workItemRaw parseResult with
            | Error error -> return Error error
            | Ok workItem ->
                let parameters =
                    Parameters.WorkItem.UndeleteWorkItemAttachmentParameters(
                        WorkItemId = workItem,
                        ArtifactId = artifactId,
                        OwnerId = graceIds.OwnerIdString,
                        OrganizationId = graceIds.OrganizationIdString,
                        RepositoryId = graceIds.RepositoryIdString,
                        CorrelationId = graceIds.CorrelationId
                    )

                let! result = WorkItem.UndeleteAttachment(parameters)

                match result with
                | Ok _ when
                    not (parseResult |> json)
                    && not (parseResult |> silent)
                    ->
                    AnsiConsole.MarkupLine($"[green]Restored attachment[/] [grey]{Markup.Escape artifactId}[/].")
                | _ -> ()

                return result
        }

    /// Executes the attachment undelete command through the asynchronous CLI action boundary.
    type AttachmentsUndelete() =
        inherit AsynchronousCommandLineAction()

        /// Runs attachment recovery without performing side effects during command introspection.
        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                let! result = attachmentsUndeleteHandler parseResult
                return result |> renderOutput parseResult
            }

    /// Formats guid list values into the text shown in Spectre.Console tables or command output.
    let private formatGuidList (values: Guid list) =
        if values.IsEmpty then
            "-"
        else
            values
            |> List.map (fun value -> value.ToString())
            |> String.concat Environment.NewLine

    /// Writes links table data through the CLI output contract.
    let private writeLinksTable (links: WorkItemLinksDto) =
        let table = Table(Border = TableBorder.Rounded)

        table.AddColumn("[bold]Link category[/]")
        |> ignore

        table.AddColumn("[bold]Values[/]") |> ignore

        table.AddRow("Work item ID", Markup.Escape(links.WorkItemId.ToString()))
        |> ignore

        table.AddRow("Work item number", links.WorkItemNumber.ToString())
        |> ignore

        table.AddRow("References", Markup.Escape(formatGuidList links.ReferenceIds))
        |> ignore

        table.AddRow("Promotion sets", Markup.Escape(formatGuidList links.PromotionSetIds))
        |> ignore

        table.AddRow("Summary attachments", Markup.Escape(formatGuidList links.AgentSummaryArtifactIds))
        |> ignore

        table.AddRow("Prompt attachments", Markup.Escape(formatGuidList links.PromptArtifactIds))
        |> ignore

        table.AddRow("Notes attachments", Markup.Escape(formatGuidList links.ReviewNotesArtifactIds))
        |> ignore

        table.AddRow("Other attachments", Markup.Escape(formatGuidList links.OtherArtifactIds))
        |> ignore

        AnsiConsole.Write(table)

    /// Routes the links list handler command from parsed options through validation, the SDK call, and result rendering.
    let private linksListHandlerImpl (parseResult: ParseResult) =
        task {
            if parseResult |> verbose then printParseResult parseResult
            let graceIds = parseResult |> getNormalizedIdsAndNames
            let workItemRaw = parseResult.GetValue(Arguments.workItemIdentifier)

            match tryNormalizeWorkItemIdentifier workItemRaw parseResult with
            | Error error -> return Error error
            | Ok workItem ->
                let parameters =
                    Parameters.WorkItem.GetWorkItemLinksParameters(
                        WorkItemId = workItem,
                        OwnerId = graceIds.OwnerIdString,
                        OwnerName = graceIds.OwnerName,
                        OrganizationId = graceIds.OrganizationIdString,
                        OrganizationName = graceIds.OrganizationName,
                        RepositoryId = graceIds.RepositoryIdString,
                        RepositoryName = graceIds.RepositoryName,
                        CorrelationId = graceIds.CorrelationId
                    )

                let! result = WorkItem.GetLinks(parameters)

                match result with
                | Error error -> return Error error
                | Ok graceReturnValue ->
                    if
                        not (parseResult |> json)
                        && not (parseResult |> silent)
                    then
                        writeLinksTable graceReturnValue.ReturnValue

                    return Ok graceReturnValue
        }

    /// Routes the links list command from parsed options through validation, the SDK call, and result rendering.
    let private linksListHandler (parseResult: ParseResult) =
        task {
            try
                return! linksListHandlerImpl parseResult
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    /// Executes the links list command by binding ParseResult values to the SDK request and CLI output contract.
    type LinksList() =
        inherit AsynchronousCommandLineAction()

        /// Runs the asynchronous links list action when System.CommandLine dispatches the parsed command.
        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                let! result = linksListHandler parseResult
                return result |> renderOutput parseResult
            }

    /// Routes the remove reference link command from parsed options through validation, the SDK call, and result rendering.
    let private removeReferenceLinkHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames
                let workItemRaw = parseResult.GetValue(Arguments.workItemIdentifier)
                let referenceIdRaw = parseResult.GetValue(Arguments.referenceId)

                match tryNormalizeWorkItemIdentifier workItemRaw parseResult with
                | Error error -> return Error error
                | Ok workItem ->
                    match tryParseGuid referenceIdRaw WorkItemError.InvalidReferenceId parseResult with
                    | Error error -> return Error error
                    | Ok referenceId ->
                        let parameters =
                            Parameters.WorkItem.RemoveReferenceLinkParameters(
                                WorkItemId = workItem,
                                ReferenceId = referenceId.ToString(),
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                CorrelationId = graceIds.CorrelationId
                            )

                        return! WorkItem.RemoveReferenceLink(parameters)
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    /// Executes the remove reference link command by binding ParseResult values to the SDK request and CLI output contract.
    type RemoveReferenceLink() =
        inherit AsynchronousCommandLineAction()

        /// Runs the asynchronous remove reference link action when System.CommandLine dispatches the parsed command.
        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                let! result = removeReferenceLinkHandler parseResult
                return result |> renderOutput parseResult
            }

    /// Routes the remove promotion set link command from parsed options through validation, the SDK call, and result rendering.
    let private removePromotionSetLinkHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames
                let workItemRaw = parseResult.GetValue(Arguments.workItemIdentifier)
                let promotionSetIdRaw = parseResult.GetValue(Arguments.promotionSetId)

                match tryNormalizeWorkItemIdentifier workItemRaw parseResult with
                | Error error -> return Error error
                | Ok workItem ->
                    match tryParseGuid promotionSetIdRaw WorkItemError.InvalidPromotionSetId parseResult with
                    | Error error -> return Error error
                    | Ok promotionSetId ->
                        let parameters =
                            Parameters.WorkItem.RemovePromotionSetLinkParameters(
                                WorkItemId = workItem,
                                PromotionSetId = promotionSetId.ToString(),
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                CorrelationId = graceIds.CorrelationId
                            )

                        return! WorkItem.RemovePromotionSetLink(parameters)
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    /// Executes the remove promotion set link command by binding ParseResult values to the SDK request and CLI output contract.
    type RemovePromotionSetLink() =
        inherit AsynchronousCommandLineAction()

        /// Runs the asynchronous remove promotion set link action when System.CommandLine dispatches the parsed command.
        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                let! result = removePromotionSetLinkHandler parseResult
                return result |> renderOutput parseResult
            }

    let Build =
        /// Adds options or child commands to a command definition.
        let addCommonOptions (command: Command) =
            command
            |> addOption Options.ownerName
            |> addOption Options.ownerId
            |> addOption Options.organizationName
            |> addOption Options.organizationId
            |> addOption Options.repositoryName
            |> addOption Options.repositoryId

        /// Adds options or child commands to a command definition.
        let addAttachInputOptions (command: Command) =
            command
            |> addOption Options.file
            |> addOption Options.text
            |> addOption Options.stdin

        let workCommand = new Command("workitem", Description = "Create and manage work items (GUID or positive-number identifiers).")
        workCommand.Aliases.Add("work")
        workCommand.Aliases.Add("work-item")
        workCommand.Aliases.Add("wi")

        let createCommand =
            new Command("create", Description = "Create a new work item.")
            |> addOption Options.workItemId
            |> addOption Options.title
            |> addOption Options.description
            |> addCommonOptions

        createCommand.Action <- new Create()
        workCommand.Subcommands.Add(createCommand)

        let showCommand =
            new Command("show", Description = "Show a work item by ID or number.")
            |> addCommonOptions

        showCommand.Arguments.Add(Arguments.workItemIdentifier)
        showCommand.Action <- new Show()
        workCommand.Subcommands.Add(showCommand)

        let setStatusCommand =
            new Command("set-status", Description = "Set the status of a work item by ID or number.")
            |> addOption Options.status
            |> addCommonOptions

        setStatusCommand.Arguments.Add(Arguments.workItemIdentifier)
        setStatusCommand.Action <- new SetStatus()
        workCommand.Subcommands.Add(setStatusCommand)

        let descriptionCommand = new Command("description", Description = "Manage the current immutable work-item description.")

        let setDescriptionCommand =
            new Command("set", Description = "Set the current Markdown description for a work item by ID or number.")
            |> addOption Options.descriptionText
            |> addCommonOptions

        setDescriptionCommand.Arguments.Add(Arguments.workItemIdentifier)
        setDescriptionCommand.Action <- new SetDescription()
        descriptionCommand.Subcommands.Add(setDescriptionCommand)
        workCommand.Subcommands.Add(descriptionCommand)

        let linkCommand = new Command("link", Description = "Link related entities to a work item.")

        let linkRefCommand =
            new Command("ref", Description = "Link a reference to a work item.")
            |> addCommonOptions

        linkRefCommand.Arguments.Add(Arguments.workItemIdentifier)
        linkRefCommand.Arguments.Add(Arguments.referenceId)
        linkRefCommand.Action <- new LinkReference()
        linkCommand.Subcommands.Add(linkRefCommand)

        let linkPromotionSetCommand =
            new Command("prset", Description = "Link a promotion set to a work item.")
            |> addCommonOptions

        linkPromotionSetCommand.Arguments.Add(Arguments.workItemIdentifier)
        linkPromotionSetCommand.Arguments.Add(Arguments.promotionSetId)
        linkPromotionSetCommand.Action <- new LinkPromotionSet()
        linkCommand.Subcommands.Add(linkPromotionSetCommand)

        workCommand.Subcommands.Add(linkCommand)

        let attachmentsCommand = new Command("attachments", Description = "Add, inspect, delete, and recover reviewer attachments by work item ID or number.")

        let attachmentsAddCommand =
            new Command("add", Description = "Add summary, prompt, or notes content to a work item.")
            |> addOption Options.attachmentType
            |> addAttachInputOptions
            |> addCommonOptions

        attachmentsAddCommand.Arguments.Add(Arguments.workItemIdentifier)
        attachmentsAddCommand.Action <- new AttachmentsAdd()
        attachmentsCommand.Subcommands.Add(attachmentsAddCommand)

        let attachmentsListCommand =
            new Command("list", Description = "List summary, prompt, and notes attachments for a work item.")
            |> addCommonOptions

        attachmentsListCommand.Arguments.Add(Arguments.workItemIdentifier)
        attachmentsListCommand.Action <- new AttachmentsList()
        attachmentsCommand.Subcommands.Add(attachmentsListCommand)

        let attachmentsShowCommand =
            new Command("show", Description = "Show one attachment with safe inline text rendering.")
            |> addOption Options.attachmentType
            |> addOption Options.latest
            |> addCommonOptions

        attachmentsShowCommand.Arguments.Add(Arguments.workItemIdentifier)
        attachmentsShowCommand.Action <- new AttachmentsShow()
        attachmentsCommand.Subcommands.Add(attachmentsShowCommand)

        let attachmentsDownloadCommand =
            new Command("download", Description = "Download attachment bytes to a local file path.")
            |> addOption Options.artifactId
            |> addOption Options.outputFile
            |> addCommonOptions

        attachmentsDownloadCommand.Arguments.Add(Arguments.workItemIdentifier)
        attachmentsDownloadCommand.Action <- new AttachmentsDownload()
        attachmentsCommand.Subcommands.Add(attachmentsDownloadCommand)

        let attachmentsDeleteCommand =
            new Command("delete", Description = "Logically delete one owned attachment using repository retention.")
            |> addOption Options.artifactId
            |> addOption Options.deleteReason
            |> addCommonOptions

        attachmentsDeleteCommand.Arguments.Add(Arguments.workItemIdentifier)
        attachmentsDeleteCommand.Action <- new AttachmentsDelete()
        attachmentsCommand.Subcommands.Add(attachmentsDeleteCommand)

        let attachmentsUndeleteCommand =
            new Command("undelete", Description = "Recover one logically deleted attachment before physical cleanup.")
            |> addOption Options.artifactId
            |> addCommonOptions

        attachmentsUndeleteCommand.Arguments.Add(Arguments.workItemIdentifier)
        attachmentsUndeleteCommand.Action <- new AttachmentsUndelete()
        attachmentsCommand.Subcommands.Add(attachmentsUndeleteCommand)

        workCommand.Subcommands.Add(attachmentsCommand)

        let linksCommand = new Command("links", Description = "Inspect and remove work item links.")

        let linksListCommand =
            new Command("list", Description = "List current links for a work item.")
            |> addCommonOptions

        linksListCommand.Arguments.Add(Arguments.workItemIdentifier)
        linksListCommand.Action <- new LinksList()
        linksCommand.Subcommands.Add(linksListCommand)

        let linksRemoveCommand = new Command("remove", Description = "Remove one or more links from a work item.")

        let removeReferenceCommand =
            new Command("ref", Description = "Remove a reference link from a work item.")
            |> addCommonOptions

        removeReferenceCommand.Arguments.Add(Arguments.workItemIdentifier)
        removeReferenceCommand.Arguments.Add(Arguments.referenceId)
        removeReferenceCommand.Action <- new RemoveReferenceLink()
        linksRemoveCommand.Subcommands.Add(removeReferenceCommand)

        let removePromotionSetCommand =
            new Command("prset", Description = "Remove a promotion set link from a work item.")
            |> addCommonOptions

        removePromotionSetCommand.Arguments.Add(Arguments.workItemIdentifier)
        removePromotionSetCommand.Arguments.Add(Arguments.promotionSetId)
        removePromotionSetCommand.Action <- new RemovePromotionSetLink()
        linksRemoveCommand.Subcommands.Add(removePromotionSetCommand)

        linksCommand.Subcommands.Add(linksRemoveCommand)
        workCommand.Subcommands.Add(linksCommand)

        workCommand
