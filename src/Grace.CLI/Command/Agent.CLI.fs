namespace Grace.CLI.Command

open Grace.CLI.Common
open Grace.CLI.Services
open Grace.CLI.Text
open Grace.SDK
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Parameters.Common
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Grace.Types.Automation
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
        let addSummaryWorkItemId =
            new Option<string>(
                "--work-item-id",
                [| "--workItemId"; "-w" |],
                Required = true,
                Description = "The work item ID <Guid> or work item number <positive integer>.",
                Arity = ArgumentArity.ExactlyOne
            )

        let startWorkItemId =
            new Option<string>(
                "--work-item-id",
                [| "--workItemId"; "-w" |],
                Required = true,
                Description = "The work item ID <Guid> or work item number <positive integer>.",
                Arity = ArgumentArity.ExactlyOne
            )

        let optionalWorkItemId =
            new Option<string>(
                "--work-item-id",
                [| "--workItemId"; "-w" |],
                Required = false,
                Description = "Optional work item ID <Guid> or work item number <positive integer>.",
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

        let promptOrigin =
            new Option<string>(
                "--prompt-origin",
                [| "--promptOrigin" |],
                Required = false,
                Description = "Optional prompt origin metadata.",
                Arity = ArgumentArity.ExactlyOne
            )

        let addSummaryPromotionSetId =
            new Option<string>(
                "--promotion-set-id",
                [| "--promotion-set" |],
                Required = false,
                Description = "Optional promotion set ID <Guid> to link as part of add-summary.",
                Arity = ArgumentArity.ExactlyOne
            )

        let agentId =
            new Option<string>("--agent-id", [| "--agentId" |], Required = true, Description = "The agent ID <Guid>.", Arity = ArgumentArity.ExactlyOne)

        let displayName =
            new Option<string>(
                "--display-name",
                [| "--displayName" |],
                Required = true,
                Description = "The agent display name.",
                Arity = ArgumentArity.ExactlyOne
            )

        let source =
            new Option<string>(
                OptionName.Source,
                Required = false,
                Description = "The source identifier for this agent session. [default: cli]",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> "cli")
            )

        let promotionSetId =
            new Option<string>(
                "--promotion-set-id",
                [| "--promotion-set" |],
                Required = false,
                Description = "Optional promotion set ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne
            )

        let sessionId =
            new Option<string>(
                "--session-id",
                [| "--sessionId" |],
                Required = false,
                Description = "Optional session ID to stop or inspect.",
                Arity = ArgumentArity.ExactlyOne
            )

        let stopReason =
            new Option<string>(
                "--reason",
                [| "--stop-reason"; "--stopReason" |],
                Required = false,
                Description = "Optional reason to record when stopping work.",
                Arity = ArgumentArity.ExactlyOne
            )

        let operationId =
            new Option<string>(
                "--operation-id",
                [| "--operationId" |],
                Required = false,
                Description = "Optional idempotency token for deterministic replay.",
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

    type private AddSummaryResult = { WorkItem: string; SummaryArtifactId: string; PromptArtifactId: string option; PromotionSetId: string option }

    type private LocalAgentSessionState =
        {
            AgentId: string
            AgentDisplayName: string
            Source: string
            ActiveSessionId: string
            ActiveWorkItemIdOrNumber: string
            ActivePromotionSetId: string
            LastOperationId: string
            LastCorrelationId: string
            LastUpdatedAtUtc: DateTime
        }

    let private localAgentSessionStateDefault =
        {
            AgentId = String.Empty
            AgentDisplayName = String.Empty
            Source = "cli"
            ActiveSessionId = String.Empty
            ActiveWorkItemIdOrNumber = String.Empty
            ActivePromotionSetId = String.Empty
            LastOperationId = String.Empty
            LastCorrelationId = String.Empty
            LastUpdatedAtUtc = DateTime.MinValue
        }

    let private localStateFileName = "agent-session-state.json"

    let private tryParseGuid (value: string) (errorMessage: string) (parseResult: ParseResult) =
        let mutable parsed = Guid.Empty

        if String.IsNullOrWhiteSpace(value)
           || Guid.TryParse(value, &parsed) = false
           || parsed = Guid.Empty then
            Error(GraceError.Create errorMessage (getCorrelationId parseResult))
        else
            Ok parsed

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

    let private tryGetOptionString (parseResult: ParseResult) (option: Option<string>) =
        parseResult.GetValue(option)
        |> Option.ofObj
        |> Option.map (fun value -> value.Trim())
        |> Option.filter (fun value -> not <| String.IsNullOrWhiteSpace(value))

    let private hasRepositoryContextOption (parseResult: ParseResult) =
        [
            OptionName.OwnerId
            OptionName.OwnerName
            OptionName.OrganizationId
            OptionName.OrganizationName
            OptionName.RepositoryId
            OptionName.RepositoryName
        ]
        |> List.exists (isOptionPresent parseResult)

    let private ensureRepositoryContextIsAvailable (parseResult: ParseResult) =
        if configurationFileExists ()
           || hasRepositoryContextOption parseResult then
            Ok()
        else
            Error(
                GraceError.Create
                    "No Grace repository configuration was found. Run `grace config write` first, or provide `--owner-id`, `--organization-id`, and `--repository-id`."
                    (getCorrelationId parseResult)
            )

    let private ensureRepositoryContextIsComplete (graceIds: GraceIds) (parseResult: ParseResult) =
        if
            graceIds.OwnerId = Guid.Empty
            && String.IsNullOrWhiteSpace(graceIds.OwnerName)
        then
            Error(GraceError.Create (getErrorMessage OwnerError.EitherOwnerIdOrOwnerNameRequired) (getCorrelationId parseResult))
        elif
            graceIds.OrganizationId = Guid.Empty
            && String.IsNullOrWhiteSpace(graceIds.OrganizationName)
        then
            Error(GraceError.Create (getErrorMessage OrganizationError.EitherOrganizationIdOrOrganizationNameRequired) (getCorrelationId parseResult))
        elif
            graceIds.RepositoryId = Guid.Empty
            && String.IsNullOrWhiteSpace(graceIds.RepositoryName)
        then
            Error(GraceError.Create (getErrorMessage RepositoryError.EitherRepositoryIdOrRepositoryNameRequired) (getCorrelationId parseResult))
        else
            Ok()

    let private getLocalStateFilePath () =
        let graceDirectory =
            if configurationFileExists () then
                Current().GraceDirectory
            else
                Path.Combine(Environment.CurrentDirectory, Constants.GraceConfigDirectory)

        Path.GetFullPath(Path.Combine(graceDirectory, localStateFileName))

    let private tryReadLocalState (parseResult: ParseResult) =
        let stateFilePath = getLocalStateFilePath ()
        let correlationId = getCorrelationId parseResult

        try
            if not <| File.Exists(stateFilePath) then
                Ok(stateFilePath, localAgentSessionStateDefault)
            else
                let fileContent = File.ReadAllText(stateFilePath)

                if String.IsNullOrWhiteSpace(fileContent) then
                    Ok(stateFilePath, localAgentSessionStateDefault)
                else
                    let state = deserialize<LocalAgentSessionState> fileContent
                    let normalizedState = if isNull (box state) then localAgentSessionStateDefault else state
                    Ok(stateFilePath, normalizedState)
        with
        | ex ->
            Error(
                GraceError.Create
                    ($"Unable to read local agent session state from {stateFilePath}. Run `grace agent bootstrap --agent-id <Guid> --display-name <Name>` to reset local state. Details: {ex.Message}")
                    correlationId
            )

    let private tryWriteLocalState (parseResult: ParseResult) (stateFilePath: string) (state: LocalAgentSessionState) =
        let correlationId = getCorrelationId parseResult

        try
            Directory.CreateDirectory(Path.GetDirectoryName(stateFilePath))
            |> ignore

            File.WriteAllText(stateFilePath, serialize state)
            Ok()
        with
        | ex -> Error(GraceError.Create ($"Unable to write local agent session state to {stateFilePath}: {ex.Message}") correlationId)

    let private hasBootstrappedIdentity (state: LocalAgentSessionState) =
        not <| String.IsNullOrWhiteSpace(state.AgentId)
        && not
           <| String.IsNullOrWhiteSpace(state.AgentDisplayName)

    let private hasActiveSession (state: LocalAgentSessionState) =
        not
        <| String.IsNullOrWhiteSpace(state.ActiveSessionId)
        || not
           <| String.IsNullOrWhiteSpace(state.ActiveWorkItemIdOrNumber)

    let private createDefaultOperationId (prefix: string) (correlationId: string) = $"{prefix}:{correlationId}"

    let private normalizeOperationId (explicitOperationId: string option) (fallbackPrefix: string) (correlationId: string) =
        explicitOperationId
        |> Option.defaultValue (createDefaultOperationId fallbackPrefix correlationId)

    let private staleStateError (parseResult: ParseResult) (details: string) =
        GraceError.Create $"{details} Run `grace agent work status` and then `grace agent work stop` to reconcile local state." (getCorrelationId parseResult)

    let private ensureBootstrapped (parseResult: ParseResult) (state: LocalAgentSessionState) =
        if hasBootstrappedIdentity state then
            Ok()
        else
            Error(
                GraceError.Create
                    "Agent identity is not bootstrapped. Run `grace agent bootstrap --agent-id <Guid> --display-name <Name>` first."
                    (getCorrelationId parseResult)
            )

    let private toSessionInfo (state: LocalAgentSessionState) (lifecycleState: AgentSessionLifecycleState) =
        { AgentSessionInfo.Default with
            SessionId = state.ActiveSessionId
            AgentId = state.AgentId
            AgentDisplayName = state.AgentDisplayName
            WorkItemIdOrNumber = state.ActiveWorkItemIdOrNumber
            PromotionSetId = state.ActivePromotionSetId
            Source = state.Source
            LifecycleState = lifecycleState
        }

    let private createLocalOperationResult
        (state: LocalAgentSessionState)
        (lifecycleState: AgentSessionLifecycleState)
        (message: string)
        (operationId: string)
        (wasReplay: bool)
        =
        { AgentSessionOperationResult.Default with
            Session = toSessionInfo state lifecycleState
            Message = message
            OperationId = operationId
            WasIdempotentReplay = wasReplay
        }

    let private writeOperationSummary (parseResult: ParseResult) (result: AgentSessionOperationResult) =
        if
            not (parseResult |> json)
            && not (parseResult |> silent)
        then
            let lifecycleState = getDiscriminatedUnionCaseName result.Session.LifecycleState
            AnsiConsole.MarkupLine($"[green]{Markup.Escape(result.Message)}[/]")

            if
                not
                <| String.IsNullOrWhiteSpace(result.Session.SessionId)
            then
                AnsiConsole.MarkupLine($"[bold]Session:[/] {Markup.Escape(result.Session.SessionId)}")

            if
                not
                <| String.IsNullOrWhiteSpace(result.Session.WorkItemIdOrNumber)
            then
                AnsiConsole.MarkupLine($"[bold]Work Item:[/] {Markup.Escape(result.Session.WorkItemIdOrNumber)}")

            if
                not
                <| String.IsNullOrWhiteSpace(result.OperationId)
            then
                AnsiConsole.MarkupLine($"[bold]Operation:[/] {Markup.Escape(result.OperationId)}")

            AnsiConsole.MarkupLine($"[bold]State:[/] {Markup.Escape(lifecycleState)}")

            if result.WasIdempotentReplay then
                AnsiConsole.MarkupLine("[yellow]Operation was handled as an idempotent replay.[/]")

    let private clearActiveSession (state: LocalAgentSessionState) (operationId: string) (correlationId: string) =
        { state with
            ActiveSessionId = String.Empty
            ActiveWorkItemIdOrNumber = String.Empty
            ActivePromotionSetId = String.Empty
            LastOperationId = operationId
            LastCorrelationId = correlationId
            LastUpdatedAtUtc = DateTime.UtcNow
        }

    let private tryNormalizePromotionSetId (value: string option) (parseResult: ParseResult) =
        match value with
        | None -> Ok String.Empty
        | Some promotionSetId ->
            match tryParseGuid promotionSetId "Promotion set ID must be a valid non-empty Guid." parseResult with
            | Error error -> Error error
            | Ok parsed -> Ok(parsed.ToString())

    let private inferMimeType (filePath: string) =
        match Path.GetExtension(filePath).ToLowerInvariant() with
        | ".md" -> "text/markdown"
        | ".txt" -> "text/plain"
        | ".json" -> "application/json"
        | _ -> "application/octet-stream"

    let private tryReadFileContent (filePath: string) (displayName: string) (parseResult: ParseResult) =
        try
            Ok(File.ReadAllText(filePath))
        with
        | ex -> Error(GraceError.Create ($"Failed to read {displayName} file '{filePath}': {ex.Message}") (getCorrelationId parseResult))

    let private addSummaryHandler (parseResult: ParseResult) =
        async {
            if verbose parseResult then printParseResult parseResult

            let graceIds = parseResult |> getNormalizedIdsAndNames
            let workItemIdRaw = parseResult.GetValue(Options.addSummaryWorkItemId)
            let summaryFilePath = parseResult.GetValue(Options.summaryFile)

            let promptOrigin =
                tryGetOptionString parseResult Options.promptOrigin
                |> Option.defaultValue String.Empty

            let promotionSetIdOption = tryGetOptionString parseResult Options.addSummaryPromotionSetId

            let promptFilePath =
                parseResult.GetValue(Options.promptFile)
                |> Option.ofObj
                |> Option.defaultValue String.Empty

            match ensureRepositoryContextIsComplete graceIds parseResult with
            | Error error -> return Error error
            | Ok _ ->
                match tryNormalizeWorkItemIdentifier workItemIdRaw parseResult with
                | Error error -> return Error error
                | Ok workItem ->
                    if not <| File.Exists(summaryFilePath) then
                        return Error(GraceError.Create $"Summary file does not exist: {summaryFilePath}" (getCorrelationId parseResult))
                    elif
                        not (String.IsNullOrWhiteSpace(promptFilePath))
                        && not <| File.Exists(promptFilePath)
                    then
                        return Error(GraceError.Create $"Prompt file does not exist: {promptFilePath}" (getCorrelationId parseResult))
                    elif
                        not (String.IsNullOrWhiteSpace(promptOrigin))
                        && String.IsNullOrWhiteSpace(promptFilePath)
                    then
                        return Error(GraceError.Create "Prompt origin can only be provided when --prompt-file is provided." (getCorrelationId parseResult))
                    else
                        match tryNormalizePromotionSetId promotionSetIdOption parseResult with
                        | Error error -> return Error error
                        | Ok promotionSetId ->
                            match tryReadFileContent summaryFilePath "summary" parseResult with
                            | Error error -> return Error error
                            | Ok summaryContent ->
                                let promptContentResult =
                                    if String.IsNullOrWhiteSpace(promptFilePath) then
                                        Ok String.Empty
                                    else
                                        tryReadFileContent promptFilePath "prompt" parseResult

                                match promptContentResult with
                                | Error error -> return Error error
                                | Ok promptContent ->
                                    let parameters =
                                        Parameters.WorkItem.AddSummaryParameters(
                                            WorkItemId = workItem,
                                            SummaryContent = summaryContent,
                                            SummaryMimeType = inferMimeType summaryFilePath,
                                            PromptContent = promptContent,
                                            PromptMimeType =
                                                (if String.IsNullOrWhiteSpace(promptFilePath) then
                                                     String.Empty
                                                 else
                                                     inferMimeType promptFilePath),
                                            PromptOrigin = promptOrigin,
                                            PromotionSetId = promotionSetId,
                                            OwnerId = graceIds.OwnerIdString,
                                            OwnerName = graceIds.OwnerName,
                                            OrganizationId = graceIds.OrganizationIdString,
                                            OrganizationName = graceIds.OrganizationName,
                                            RepositoryId = graceIds.RepositoryIdString,
                                            RepositoryName = graceIds.RepositoryName,
                                            CorrelationId = graceIds.CorrelationId
                                        )

                                    let! addSummaryResponse = WorkItem.AddSummary(parameters) |> Async.AwaitTask

                                    match addSummaryResponse with
                                    | Error error -> return Error error
                                    | Ok addSummaryResult ->
                                        let response = addSummaryResult.ReturnValue

                                        let promptArtifactId =
                                            response.PromptArtifactId
                                            |> Option.ofObj
                                            |> Option.map (fun value -> value.Trim())
                                            |> Option.filter (fun value -> not <| String.IsNullOrWhiteSpace(value))

                                        let promotionSetIdResult =
                                            response.PromotionSetId
                                            |> Option.ofObj
                                            |> Option.map (fun value -> value.Trim())
                                            |> Option.filter (fun value -> not <| String.IsNullOrWhiteSpace(value))

                                        let summaryArtifactId =
                                            response.SummaryArtifactId
                                            |> Option.ofObj
                                            |> Option.map (fun value -> value.Trim())
                                            |> Option.filter (fun value -> not <| String.IsNullOrWhiteSpace(value))
                                            |> Option.defaultValue String.Empty

                                        let result =
                                            {
                                                WorkItem = workItem
                                                SummaryArtifactId = summaryArtifactId
                                                PromptArtifactId = promptArtifactId
                                                PromotionSetId = promotionSetIdResult
                                            }

                                        if
                                            not (parseResult |> json)
                                            && not (parseResult |> silent)
                                        then
                                            AnsiConsole.MarkupLine(
                                                $"[green]Linked AgentSummary artifact[/] {Markup.Escape(result.SummaryArtifactId)} [green]to work item[/] {Markup.Escape(workItem)}"
                                            )

                                            match result.PromptArtifactId with
                                            | Some artifactId ->
                                                AnsiConsole.MarkupLine(
                                                    $"[green]Linked Prompt artifact[/] {Markup.Escape(artifactId)} [green]to work item[/] {Markup.Escape(workItem)}"
                                                )
                                            | None -> ()

                                            match result.PromotionSetId with
                                            | Some linkedPromotionSetId ->
                                                AnsiConsole.MarkupLine(
                                                    $"[green]Linked promotion set[/] {Markup.Escape(linkedPromotionSetId)} [green]to work item[/] {Markup.Escape(workItem)}"
                                                )
                                            | None -> ()

                                        return Ok(GraceReturnValue.Create result graceIds.CorrelationId)
        }
        |> Async.StartAsTask

    let private bootstrapHandler (parseResult: ParseResult) =
        async {
            let correlationId = getCorrelationId parseResult
            let stateFilePath = getLocalStateFilePath ()

            let existingState =
                match tryReadLocalState parseResult with
                | Ok (_, state) -> state
                | Error _ -> localAgentSessionStateDefault

            let agentIdRaw =
                parseResult.GetValue(Options.agentId)
                |> Option.ofObj
                |> Option.defaultValue String.Empty

            let displayName =
                tryGetOptionString parseResult Options.displayName
                |> Option.defaultValue String.Empty

            let source =
                tryGetOptionString parseResult Options.source
                |> Option.defaultValue "cli"

            match tryParseGuid agentIdRaw "Agent ID must be a valid non-empty Guid." parseResult with
            | Error error -> return Error error
            | Ok parsedAgentId ->
                if String.IsNullOrWhiteSpace(displayName) then
                    return Error(GraceError.Create "Display name is required." correlationId)
                elif
                    hasActiveSession existingState
                    && hasBootstrappedIdentity existingState
                    && not (existingState.AgentId.Equals(parsedAgentId.ToString(), StringComparison.OrdinalIgnoreCase))
                then
                    return
                        Error(
                            staleStateError
                                parseResult
                                ($"Local state contains an active session for agent '{existingState.AgentId}'. Stop that session before bootstrapping a different agent.")
                        )
                else
                    let normalizedSource = if String.IsNullOrWhiteSpace(source) then "cli" else source

                    let unchanged =
                        existingState.AgentId.Equals(parsedAgentId.ToString(), StringComparison.OrdinalIgnoreCase)
                        && existingState.AgentDisplayName.Equals(displayName, StringComparison.Ordinal)
                        && existingState.Source.Equals(normalizedSource, StringComparison.Ordinal)

                    let operationId = createDefaultOperationId "bootstrap" correlationId

                    let updatedState =
                        { existingState with
                            AgentId = parsedAgentId.ToString()
                            AgentDisplayName = displayName
                            Source = normalizedSource
                            LastOperationId = operationId
                            LastCorrelationId = correlationId
                            LastUpdatedAtUtc = DateTime.UtcNow
                        }

                    match tryWriteLocalState parseResult stateFilePath updatedState with
                    | Error error -> return Error error
                    | Ok _ ->
                        let lifecycleState =
                            if hasActiveSession updatedState then
                                AgentSessionLifecycleState.Active
                            else
                                AgentSessionLifecycleState.Inactive

                        let message =
                            if unchanged then
                                "Agent identity already bootstrapped. Reusing existing local state."
                            else
                                "Agent identity bootstrapped successfully."

                        let operationResult = createLocalOperationResult updatedState lifecycleState message operationId unchanged
                        writeOperationSummary parseResult operationResult
                        return Ok(GraceReturnValue.Create operationResult correlationId)
        }
        |> Async.StartAsTask

    let internal workStartWith (startSession: StartAgentSessionParameters -> Task<GraceResult<AgentSessionOperationResult>>) (parseResult: ParseResult) =
        async {
            match ensureRepositoryContextIsAvailable parseResult with
            | Error error -> return Error error
            | Ok _ ->
                let graceIds = parseResult |> getNormalizedIdsAndNames

                match ensureRepositoryContextIsComplete graceIds parseResult with
                | Error error -> return Error error
                | Ok _ ->
                    let workItemIdRaw = parseResult.GetValue(Options.startWorkItemId)

                    match tryNormalizeWorkItemIdentifier workItemIdRaw parseResult with
                    | Error error -> return Error error
                    | Ok workItemId ->
                        let promotionSetIdOption = tryGetOptionString parseResult Options.promotionSetId

                        match tryNormalizePromotionSetId promotionSetIdOption parseResult with
                        | Error error -> return Error error
                        | Ok promotionSetId ->
                            match tryReadLocalState parseResult with
                            | Error error -> return Error error
                            | Ok (stateFilePath, state) ->
                                match ensureBootstrapped parseResult state with
                                | Error error -> return Error error
                                | Ok _ ->
                                    if hasActiveSession state then
                                        if state.ActiveWorkItemIdOrNumber = workItemId then
                                            let operationId =
                                                if String.IsNullOrWhiteSpace(state.LastOperationId) then
                                                    createDefaultOperationId "start" graceIds.CorrelationId
                                                else
                                                    state.LastOperationId

                                            let operationResult =
                                                createLocalOperationResult
                                                    state
                                                    AgentSessionLifecycleState.Active
                                                    "Work session is already active for this work item."
                                                    operationId
                                                    true

                                            writeOperationSummary parseResult operationResult
                                            return Ok(GraceReturnValue.Create operationResult graceIds.CorrelationId)
                                        else
                                            return
                                                Error(
                                                    staleStateError
                                                        parseResult
                                                        ($"Local state indicates an active session for work item '{state.ActiveWorkItemIdOrNumber}', but start requested '{workItemId}'.")
                                                )
                                    else
                                        let operationId =
                                            normalizeOperationId (tryGetOptionString parseResult Options.operationId) "start" graceIds.CorrelationId

                                        let sourceFromState = if String.IsNullOrWhiteSpace(state.Source) then "cli" else state.Source

                                        let source =
                                            tryGetOptionString parseResult Options.source
                                            |> Option.defaultValue sourceFromState

                                        let startParameters =
                                            StartAgentSessionParameters(
                                                WorkItemIdOrNumber = workItemId,
                                                PromotionSetId = promotionSetId,
                                                Source = source,
                                                OperationId = operationId,
                                                OwnerId = graceIds.OwnerIdString,
                                                OwnerName = graceIds.OwnerName,
                                                OrganizationId = graceIds.OrganizationIdString,
                                                OrganizationName = graceIds.OrganizationName,
                                                RepositoryId = graceIds.RepositoryIdString,
                                                RepositoryName = graceIds.RepositoryName,
                                                AgentId = state.AgentId,
                                                AgentDisplayName = state.AgentDisplayName,
                                                CorrelationId = graceIds.CorrelationId
                                            )

                                        let! startResult = startSession startParameters |> Async.AwaitTask

                                        match startResult with
                                        | Error error -> return Error error
                                        | Ok returnValue ->
                                            let normalizedResult =
                                                if String.IsNullOrWhiteSpace(returnValue.ReturnValue.OperationId) then
                                                    { returnValue.ReturnValue with OperationId = operationId }
                                                else
                                                    returnValue.ReturnValue

                                            let session = normalizedResult.Session

                                            let updatedState =
                                                { state with
                                                    AgentId =
                                                        if String.IsNullOrWhiteSpace(session.AgentId) then
                                                            state.AgentId
                                                        else
                                                            session.AgentId
                                                    AgentDisplayName =
                                                        if String.IsNullOrWhiteSpace(session.AgentDisplayName) then
                                                            state.AgentDisplayName
                                                        else
                                                            session.AgentDisplayName
                                                    Source = if String.IsNullOrWhiteSpace(session.Source) then source else session.Source
                                                    ActiveSessionId =
                                                        if String.IsNullOrWhiteSpace(session.SessionId) then
                                                            operationId
                                                        else
                                                            session.SessionId
                                                    ActiveWorkItemIdOrNumber =
                                                        if String.IsNullOrWhiteSpace(session.WorkItemIdOrNumber) then
                                                            workItemId
                                                        else
                                                            session.WorkItemIdOrNumber
                                                    ActivePromotionSetId =
                                                        if String.IsNullOrWhiteSpace(session.PromotionSetId) then
                                                            promotionSetId
                                                        else
                                                            session.PromotionSetId
                                                    LastOperationId = normalizedResult.OperationId
                                                    LastCorrelationId = graceIds.CorrelationId
                                                    LastUpdatedAtUtc = DateTime.UtcNow
                                                }

                                            match tryWriteLocalState parseResult stateFilePath updatedState with
                                            | Error error -> return Error error
                                            | Ok _ ->
                                                writeOperationSummary parseResult normalizedResult
                                                return Ok({ returnValue with ReturnValue = normalizedResult })
        }
        |> Async.StartAsTask

    let internal workStopWith (stopSession: StopAgentSessionParameters -> Task<GraceResult<AgentSessionOperationResult>>) (parseResult: ParseResult) =
        async {
            match ensureRepositoryContextIsAvailable parseResult with
            | Error error -> return Error error
            | Ok _ ->
                let graceIds = parseResult |> getNormalizedIdsAndNames

                match ensureRepositoryContextIsComplete graceIds parseResult with
                | Error error -> return Error error
                | Ok _ ->
                    match tryReadLocalState parseResult with
                    | Error error -> return Error error
                    | Ok (stateFilePath, state) ->
                        match ensureBootstrapped parseResult state with
                        | Error error -> return Error error
                        | Ok _ ->
                            let requestedSessionId =
                                tryGetOptionString parseResult Options.sessionId
                                |> Option.defaultValue String.Empty

                            let requestedWorkItemRaw = tryGetOptionString parseResult Options.optionalWorkItemId

                            let normalizedRequestedWorkItemResult =
                                match requestedWorkItemRaw with
                                | None -> Ok String.Empty
                                | Some workItemRaw -> tryNormalizeWorkItemIdentifier workItemRaw parseResult

                            match normalizedRequestedWorkItemResult with
                            | Error error -> return Error error
                            | Ok requestedWorkItemId ->
                                if
                                    hasActiveSession state
                                    && not
                                       <| String.IsNullOrWhiteSpace(requestedSessionId)
                                    && not (state.ActiveSessionId.Equals(requestedSessionId, StringComparison.OrdinalIgnoreCase))
                                then
                                    return
                                        Error(
                                            staleStateError
                                                parseResult
                                                ($"Local state indicates session '{state.ActiveSessionId}', but stop requested session '{requestedSessionId}'.")
                                        )
                                elif
                                    hasActiveSession state
                                    && not
                                       <| String.IsNullOrWhiteSpace(requestedWorkItemId)
                                    && not (state.ActiveWorkItemIdOrNumber.Equals(requestedWorkItemId, StringComparison.OrdinalIgnoreCase))
                                then
                                    return
                                        Error(
                                            staleStateError
                                                parseResult
                                                ($"Local state indicates work item '{state.ActiveWorkItemIdOrNumber}', but stop requested work item '{requestedWorkItemId}'.")
                                        )
                                elif
                                    not (hasActiveSession state)
                                    && String.IsNullOrWhiteSpace(requestedSessionId)
                                    && String.IsNullOrWhiteSpace(requestedWorkItemId)
                                then
                                    let operationId = createDefaultOperationId "stop" graceIds.CorrelationId
                                    let updatedState = clearActiveSession state operationId graceIds.CorrelationId

                                    match tryWriteLocalState parseResult stateFilePath updatedState with
                                    | Error error -> return Error error
                                    | Ok _ ->
                                        let operationResult =
                                            createLocalOperationResult
                                                updatedState
                                                AgentSessionLifecycleState.Inactive
                                                "No active local work session was found. Nothing to stop."
                                                operationId
                                                true

                                        writeOperationSummary parseResult operationResult
                                        return Ok(GraceReturnValue.Create operationResult graceIds.CorrelationId)
                                else
                                    let operationId = normalizeOperationId (tryGetOptionString parseResult Options.operationId) "stop" graceIds.CorrelationId

                                    let sessionId =
                                        if String.IsNullOrWhiteSpace(requestedSessionId) then
                                            state.ActiveSessionId
                                        else
                                            requestedSessionId

                                    let workItemId =
                                        if String.IsNullOrWhiteSpace(requestedWorkItemId) then
                                            state.ActiveWorkItemIdOrNumber
                                        else
                                            requestedWorkItemId

                                    let stopParameters =
                                        StopAgentSessionParameters(
                                            SessionId = sessionId,
                                            WorkItemIdOrNumber = workItemId,
                                            StopReason =
                                                (tryGetOptionString parseResult Options.stopReason
                                                 |> Option.defaultValue String.Empty),
                                            OperationId = operationId,
                                            OwnerId = graceIds.OwnerIdString,
                                            OwnerName = graceIds.OwnerName,
                                            OrganizationId = graceIds.OrganizationIdString,
                                            OrganizationName = graceIds.OrganizationName,
                                            RepositoryId = graceIds.RepositoryIdString,
                                            RepositoryName = graceIds.RepositoryName,
                                            AgentId = state.AgentId,
                                            AgentDisplayName = state.AgentDisplayName,
                                            CorrelationId = graceIds.CorrelationId
                                        )

                                    let! stopResult = stopSession stopParameters |> Async.AwaitTask

                                    match stopResult with
                                    | Error error -> return Error error
                                    | Ok returnValue ->
                                        let normalizedResult =
                                            if String.IsNullOrWhiteSpace(returnValue.ReturnValue.OperationId) then
                                                { returnValue.ReturnValue with OperationId = operationId }
                                            else
                                                returnValue.ReturnValue

                                        let clearedState = clearActiveSession state normalizedResult.OperationId graceIds.CorrelationId

                                        match tryWriteLocalState parseResult stateFilePath clearedState with
                                        | Error error -> return Error error
                                        | Ok _ ->
                                            writeOperationSummary parseResult normalizedResult
                                            return Ok({ returnValue with ReturnValue = normalizedResult })
        }
        |> Async.StartAsTask

    let internal workStatusWith (getStatus: GetAgentSessionStatusParameters -> Task<GraceResult<AgentSessionOperationResult>>) (parseResult: ParseResult) =
        async {
            match ensureRepositoryContextIsAvailable parseResult with
            | Error error -> return Error error
            | Ok _ ->
                let graceIds = parseResult |> getNormalizedIdsAndNames

                match ensureRepositoryContextIsComplete graceIds parseResult with
                | Error error -> return Error error
                | Ok _ ->
                    match tryReadLocalState parseResult with
                    | Error error -> return Error error
                    | Ok (stateFilePath, state) ->
                        match ensureBootstrapped parseResult state with
                        | Error error -> return Error error
                        | Ok _ ->
                            let requestedSessionId =
                                tryGetOptionString parseResult Options.sessionId
                                |> Option.defaultValue String.Empty

                            let requestedWorkItemRaw = tryGetOptionString parseResult Options.optionalWorkItemId

                            let normalizedRequestedWorkItemResult =
                                match requestedWorkItemRaw with
                                | None -> Ok String.Empty
                                | Some workItemRaw -> tryNormalizeWorkItemIdentifier workItemRaw parseResult

                            match normalizedRequestedWorkItemResult with
                            | Error error -> return Error error
                            | Ok requestedWorkItemId ->
                                if
                                    hasActiveSession state
                                    && not
                                       <| String.IsNullOrWhiteSpace(requestedSessionId)
                                    && not (state.ActiveSessionId.Equals(requestedSessionId, StringComparison.OrdinalIgnoreCase))
                                then
                                    return
                                        Error(
                                            staleStateError
                                                parseResult
                                                ($"Local state indicates session '{state.ActiveSessionId}', but status requested session '{requestedSessionId}'.")
                                        )
                                elif
                                    hasActiveSession state
                                    && not
                                       <| String.IsNullOrWhiteSpace(requestedWorkItemId)
                                    && not (state.ActiveWorkItemIdOrNumber.Equals(requestedWorkItemId, StringComparison.OrdinalIgnoreCase))
                                then
                                    return
                                        Error(
                                            staleStateError
                                                parseResult
                                                ($"Local state indicates work item '{state.ActiveWorkItemIdOrNumber}', but status requested work item '{requestedWorkItemId}'.")
                                        )
                                else
                                    let sessionId =
                                        if String.IsNullOrWhiteSpace(requestedSessionId) then
                                            state.ActiveSessionId
                                        else
                                            requestedSessionId

                                    let workItemId =
                                        if String.IsNullOrWhiteSpace(requestedWorkItemId) then
                                            state.ActiveWorkItemIdOrNumber
                                        else
                                            requestedWorkItemId

                                    if
                                        String.IsNullOrWhiteSpace(sessionId)
                                        && String.IsNullOrWhiteSpace(workItemId)
                                    then
                                        return
                                            Error(
                                                GraceError.Create
                                                    "No active local work session is available. Run `grace agent work start --work-item-id <id>` first, or provide `--session-id` or `--work-item-id`."
                                                    (getCorrelationId parseResult)
                                            )
                                    else
                                        let statusParameters =
                                            GetAgentSessionStatusParameters(
                                                SessionId = sessionId,
                                                WorkItemIdOrNumber = workItemId,
                                                OwnerId = graceIds.OwnerIdString,
                                                OwnerName = graceIds.OwnerName,
                                                OrganizationId = graceIds.OrganizationIdString,
                                                OrganizationName = graceIds.OrganizationName,
                                                RepositoryId = graceIds.RepositoryIdString,
                                                RepositoryName = graceIds.RepositoryName,
                                                AgentId = state.AgentId,
                                                AgentDisplayName = state.AgentDisplayName,
                                                CorrelationId = graceIds.CorrelationId
                                            )

                                        let! statusResult = getStatus statusParameters |> Async.AwaitTask

                                        match statusResult with
                                        | Error error -> return Error error
                                        | Ok returnValue ->
                                            let session = returnValue.ReturnValue.Session

                                            let updatedState =
                                                match session.LifecycleState with
                                                | AgentSessionLifecycleState.Active
                                                | AgentSessionLifecycleState.Stopping ->
                                                    { state with
                                                        ActiveSessionId = session.SessionId
                                                        ActiveWorkItemIdOrNumber = session.WorkItemIdOrNumber
                                                        ActivePromotionSetId = session.PromotionSetId
                                                        LastOperationId = returnValue.ReturnValue.OperationId
                                                        LastCorrelationId = graceIds.CorrelationId
                                                        LastUpdatedAtUtc = DateTime.UtcNow
                                                    }
                                                | AgentSessionLifecycleState.Inactive
                                                | AgentSessionLifecycleState.Stopped ->
                                                    clearActiveSession state returnValue.ReturnValue.OperationId graceIds.CorrelationId

                                            match tryWriteLocalState parseResult stateFilePath updatedState with
                                            | Error error -> return Error error
                                            | Ok _ ->
                                                writeOperationSummary parseResult returnValue.ReturnValue
                                                return Ok returnValue
        }
        |> Async.StartAsTask

    let private workStartHandler (parseResult: ParseResult) = workStartWith AgentSession.Start parseResult
    let private workStopHandler (parseResult: ParseResult) = workStopWith AgentSession.Stop parseResult
    let private workStatusHandler (parseResult: ParseResult) = workStatusWith AgentSession.Status parseResult

    type AddSummary() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                let! result = addSummaryHandler parseResult
                return result |> renderOutput parseResult
            }

    type Bootstrap() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) : Task<int> =
            task {
                let! result = bootstrapHandler parseResult
                return result |> renderOutput parseResult
            }

    type WorkStart() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) : Task<int> =
            task {
                let! result = workStartHandler parseResult
                return result |> renderOutput parseResult
            }

    type WorkStop() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) : Task<int> =
            task {
                let! result = workStopHandler parseResult
                return result |> renderOutput parseResult
            }

    type WorkStatus() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) : Task<int> =
            task {
                let! result = workStatusHandler parseResult
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

        let bootstrapCommand =
            new Command("bootstrap", Description = "Bootstrap local agent identity for deterministic session workflows.")
            |> addOption Options.agentId
            |> addOption Options.displayName
            |> addOption Options.source

        bootstrapCommand.Action <- new Bootstrap()

        let workCommand = new Command("work", Description = "Manage agent work sessions.")

        let workStartCommand =
            new Command("start", Description = "Start work on a work item.")
            |> addOption Options.startWorkItemId
            |> addOption Options.promotionSetId
            |> addOption Options.source
            |> addOption Options.operationId
            |> addCommonOptions

        workStartCommand.Action <- new WorkStart()

        let workStopCommand =
            new Command("stop", Description = "Stop the current or specified work session.")
            |> addOption Options.sessionId
            |> addOption Options.optionalWorkItemId
            |> addOption Options.stopReason
            |> addOption Options.operationId
            |> addCommonOptions

        workStopCommand.Action <- new WorkStop()

        let workStatusCommand =
            new Command("status", Description = "Get status for the current or specified work session.")
            |> addOption Options.sessionId
            |> addOption Options.optionalWorkItemId
            |> addCommonOptions

        workStatusCommand.Action <- new WorkStatus()

        workCommand.Subcommands.Add(workStartCommand)
        workCommand.Subcommands.Add(workStopCommand)
        workCommand.Subcommands.Add(workStatusCommand)

        let addSummaryCommand =
            new Command("add-summary", Description = "Submit summary content (and optional prompt content) and link canonical artifacts to a work item.")
            |> addOption Options.addSummaryWorkItemId
            |> addOption Options.summaryFile
            |> addOption Options.promptFile
            |> addOption Options.promptOrigin
            |> addOption Options.addSummaryPromotionSetId
            |> addCommonOptions

        addSummaryCommand.Action <- new AddSummary()
        agentCommand.Subcommands.Add(bootstrapCommand)
        agentCommand.Subcommands.Add(workCommand)
        agentCommand.Subcommands.Add(addSummaryCommand)
        agentCommand
