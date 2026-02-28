namespace Grace.CLI.Command

open Grace.CLI.Common
open Grace.CLI.Services
open Grace.CLI.Text
open Grace.SDK
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Grace.Types.PromotionSet
open Grace.Types.Types
open Spectre.Console
open System
open System.CommandLine
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks

module PromotionSetCommand =
    module private Options =
        let promotionSetId =
            new Option<string>(
                "--promotion-set",
                [|
                    "--promotion-set-id"
                    "--promotionSetId"
                |],
                Required = true,
                Description = "The promotion set ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne
            )

        let promotionSetIdOptional =
            new Option<string>(
                "--promotion-set",
                [|
                    "--promotion-set-id"
                    "--promotionSetId"
                |],
                Required = false,
                Description = "The promotion set ID <Guid>. If omitted, a new one is generated.",
                Arity = ArgumentArity.ExactlyOne
            )

        let targetBranchId =
            new Option<string>(
                "--target-branch-id",
                [| "--target-branch" |],
                Required = true,
                Description = "The target branch ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne
            )

        let promotionPointersFile =
            new Option<string>(
                "--promotion-pointers-file",
                [| "--pointers-file"; "--input-file" |],
                Required = true,
                Description = "Path to a JSON file containing PromotionPointer entries.",
                Arity = ArgumentArity.ExactlyOne
            )

        let reason =
            new Option<string>(
                "--reason",
                [| "--recompute-reason" |],
                Required = false,
                Description = "Optional reason for recomputing the promotion set.",
                Arity = ArgumentArity.ExactlyOne
            )

        let force =
            new Option<bool>(
                OptionName.Force,
                [| "-f"; "--force" |],
                Required = false,
                Description = "Force logical deletion of the promotion set.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> false)
            )

        let deleteReason =
            new Option<string>(
                OptionName.DeleteReason,
                Required = false,
                Description = "Optional reason for deleting the promotion set.",
                Arity = ArgumentArity.ExactlyOne
            )

        let stepId =
            new Option<string>(
                "--step",
                [| "--step-id"; "--stepId" |],
                Required = true,
                Description = "The promotion set step ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne
            )

        let decisionsFile =
            new Option<string>(
                "--decisions-file",
                [| "--decisionsFile" |],
                Required = true,
                Description = "Path to a JSON file containing ConflictResolutionDecision entries.",
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

    type private ConflictStepDisplay =
        {
            StepId: PromotionSetStepId
            Order: int
            ConflictStatus: string
            ConflictSummaryArtifactId: ArtifactId option
            DownloadUri: UriWithSharedAccessSignature option
            DownloadUriError: string option
        }

    type private ConflictShowResult =
        {
            PromotionSetId: PromotionSetId
            Status: string
            StepsComputationStatus: string
            StepsComputationAttempt: int
            ConflictedSteps: ConflictStepDisplay list
        }

    type private ConflictDecisionsWrapper = { Decisions: ConflictResolutionDecision list }
    type private PromotionPointersWrapper = { PromotionPointers: PromotionPointer list }

    let private tryParseGuid (value: string) (errorMessage: string) (parseResult: ParseResult) =
        let mutable parsed = Guid.Empty

        if String.IsNullOrWhiteSpace(value)
           || Guid.TryParse(value, &parsed) = false
           || parsed = Guid.Empty then
            Error(GraceError.Create errorMessage (getCorrelationId parseResult))
        else
            Ok parsed

    let private parseOptionalPromotionSetId (value: string) (parseResult: ParseResult) =
        if String.IsNullOrWhiteSpace(value) then
            Ok(Guid.NewGuid())
        else
            tryParseGuid value (QueueError.getErrorMessage QueueError.InvalidPromotionSetId) parseResult

    let private tryDeserializeDecisionsFromFile (filePath: string) =
        if not <| File.Exists filePath then
            Error $"Decisions file does not exist: {filePath}"
        else
            try
                let fileContent = File.ReadAllText filePath

                let decisionsResult =
                    try
                        Ok(JsonSerializer.Deserialize<ConflictResolutionDecision list>(fileContent, Constants.JsonSerializerOptions))
                    with
                    | _ ->
                        try
                            let wrapped = JsonSerializer.Deserialize<ConflictDecisionsWrapper>(fileContent, Constants.JsonSerializerOptions)
                            Ok wrapped.Decisions
                        with
                        | _ -> Error "Decisions file must be valid JSON as an array or { \"decisions\": [...] }."

                match decisionsResult with
                | Ok decisions when List.isEmpty decisions -> Error "Decisions file must contain at least one decision."
                | Ok decisions -> Ok decisions
                | Error error -> Error error
            with
            | ex -> Error($"Unable to read decisions file: {ex.Message}")

    let private tryDeserializePromotionPointersFromFile (filePath: string) =
        if not <| File.Exists filePath then
            Error $"Promotion pointers file does not exist: {filePath}"
        else
            try
                let fileContent = File.ReadAllText filePath

                let pointersResult =
                    try
                        Ok(JsonSerializer.Deserialize<PromotionPointer list>(fileContent, Constants.JsonSerializerOptions))
                    with
                    | _ ->
                        try
                            let wrapped = JsonSerializer.Deserialize<PromotionPointersWrapper>(fileContent, Constants.JsonSerializerOptions)
                            Ok wrapped.PromotionPointers
                        with
                        | _ -> Error "Promotion pointers file must be valid JSON as an array or { \"promotionPointers\": [...] }."

                match pointersResult with
                | Ok pointers ->
                    let normalizedPointers = if obj.ReferenceEquals(box pointers, null) then [] else pointers

                    if List.isEmpty normalizedPointers then
                        Error "Promotion pointers file must contain at least one entry."
                    else
                        Ok normalizedPointers
                | Error error -> Error error
            with
            | ex -> Error($"Unable to read promotion pointers file: {ex.Message}")

    let private getPromotionSet (graceIds: GraceIds) (promotionSetId: PromotionSetId) =
        let parameters =
            Parameters.PromotionSet.GetPromotionSetParameters(
                PromotionSetId = promotionSetId.ToString(),
                OwnerId = graceIds.OwnerIdString,
                OwnerName = graceIds.OwnerName,
                OrganizationId = graceIds.OrganizationIdString,
                OrganizationName = graceIds.OrganizationName,
                RepositoryId = graceIds.RepositoryIdString,
                RepositoryName = graceIds.RepositoryName,
                CorrelationId = graceIds.CorrelationId
            )

        Grace.SDK.PromotionSet.Get(parameters)

    let private getArtifactDownloadUri (graceIds: GraceIds) (artifactId: ArtifactId) =
        let parameters =
            Parameters.Artifact.GetArtifactDownloadUriParameters(
                ArtifactId = artifactId.ToString(),
                OwnerId = graceIds.OwnerIdString,
                OwnerName = graceIds.OwnerName,
                OrganizationId = graceIds.OrganizationIdString,
                OrganizationName = graceIds.OrganizationName,
                RepositoryId = graceIds.RepositoryIdString,
                RepositoryName = graceIds.RepositoryName,
                CorrelationId = graceIds.CorrelationId
            )

        Artifact.GetDownloadUri(parameters)

    let private renderPromotionSet (parseResult: ParseResult) (promotionSet: PromotionSetDto) =
        if
            not (parseResult |> json)
            && not (parseResult |> silent)
        then
            let table = Table(Border = TableBorder.Rounded)
            table.AddColumn("Field") |> ignore
            table.AddColumn("Value") |> ignore

            table.AddRow("PromotionSetId", Markup.Escape(promotionSet.PromotionSetId.ToString()))
            |> ignore

            table.AddRow("TargetBranchId", Markup.Escape(promotionSet.TargetBranchId.ToString()))
            |> ignore

            table.AddRow("Status", Markup.Escape(getDiscriminatedUnionCaseName promotionSet.Status))
            |> ignore

            table.AddRow("StepsComputationStatus", Markup.Escape(getDiscriminatedUnionCaseName promotionSet.StepsComputationStatus))
            |> ignore

            table.AddRow("StepsComputationAttempt", promotionSet.StepsComputationAttempt.ToString())
            |> ignore

            table.AddRow("StepCount", promotionSet.Steps.Length.ToString())
            |> ignore

            AnsiConsole.Write(table)

    let private renderPromotionSetEvents (parseResult: ParseResult) (promotionSetId: PromotionSetId) (events: PromotionSetEvent seq) =
        if
            not (parseResult |> json)
            && not (parseResult |> silent)
        then
            let eventsList = events |> Seq.toList

            if List.isEmpty eventsList then
                AnsiConsole.MarkupLine($"[yellow]No events found for promotion set[/] {Markup.Escape(promotionSetId.ToString())}.")
            else
                let table = Table(Border = TableBorder.Rounded)
                table.AddColumn("Timestamp") |> ignore
                table.AddColumn("Event") |> ignore
                table.AddColumn("Principal") |> ignore

                eventsList
                |> List.iter (fun promotionSetEvent ->
                    table.AddRow(
                        Markup.Escape(promotionSetEvent.Metadata.Timestamp.ToString()),
                        Markup.Escape(getDiscriminatedUnionCaseName promotionSetEvent.Event),
                        Markup.Escape(promotionSetEvent.Metadata.Principal)
                    )
                    |> ignore)

                AnsiConsole.Write(table)

    let private buildConflictStepDisplay (graceIds: GraceIds) (step: PromotionSetStep) =
        task {
            let baseDisplay =
                {
                    StepId = step.StepId
                    Order = step.Order
                    ConflictStatus = getDiscriminatedUnionCaseName step.ConflictStatus
                    ConflictSummaryArtifactId = step.ConflictSummaryArtifactId
                    DownloadUri = Option.None
                    DownloadUriError = Option.None
                }

            match step.ConflictSummaryArtifactId with
            | Option.None -> return baseDisplay
            | Option.Some artifactId ->
                match! getArtifactDownloadUri graceIds artifactId with
                | Ok returnValue -> return { baseDisplay with DownloadUri = Option.Some returnValue.ReturnValue.DownloadUri }
                | Error error -> return { baseDisplay with DownloadUriError = Option.Some error.Error }
        }

    let private renderConflictSummary (parseResult: ParseResult) (result: ConflictShowResult) =
        if
            not (parseResult |> json)
            && not (parseResult |> silent)
        then
            AnsiConsole.MarkupLine($"[bold]Promotion Set[/] {Markup.Escape(result.PromotionSetId.ToString())}")

            AnsiConsole.MarkupLine(
                $"[bold]Status:[/] {Markup.Escape(result.Status)}  [bold]Computation:[/] {Markup.Escape(result.StepsComputationStatus)}  [bold]Attempt:[/] {result.StepsComputationAttempt}"
            )

            if List.isEmpty result.ConflictedSteps then
                AnsiConsole.MarkupLine("[yellow]No conflicts found on the current PromotionSet steps.[/]")
            else
                let table = Table(Border = TableBorder.Rounded)

                table.AddColumns(
                    [|
                        "Order"
                        "StepId"
                        "ConflictStatus"
                        "ArtifactId"
                        "DownloadUri"
                    |]
                )
                |> ignore

                result.ConflictedSteps
                |> List.iter (fun step ->
                    let artifactIdText =
                        match step.ConflictSummaryArtifactId with
                        | Option.Some artifactId -> Markup.Escape(artifactId.ToString())
                        | Option.None -> "-"

                    let downloadUriText =
                        match step.DownloadUri, step.DownloadUriError with
                        | Option.Some uri, _ -> Markup.Escape(uri.ToString())
                        | Option.None, Option.Some error -> Markup.Escape($"Unavailable: {error}")
                        | _ -> "-"

                    table.AddRow(
                        step.Order.ToString(),
                        Markup.Escape(step.StepId.ToString()),
                        Markup.Escape(step.ConflictStatus),
                        artifactIdText,
                        downloadUriText
                    )
                    |> ignore)

                AnsiConsole.Write(table)

    let private showConflictsHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames
                let promotionSetIdRaw = parseResult.GetValue(Options.promotionSetId)

                match tryParseGuid promotionSetIdRaw (QueueError.getErrorMessage QueueError.InvalidPromotionSetId) parseResult with
                | Error error -> return Error error
                | Ok promotionSetId ->
                    match! getPromotionSet graceIds promotionSetId with
                    | Error error -> return Error error
                    | Ok returnValue ->
                        let promotionSet = returnValue.ReturnValue

                        let conflictedSteps =
                            promotionSet.Steps
                            |> List.filter (fun step ->
                                step.ConflictStatus
                                <> StepConflictStatus.NoConflicts
                                || step.ConflictSummaryArtifactId.IsSome)

                        let! displays =
                            conflictedSteps
                            |> List.map (buildConflictStepDisplay graceIds)
                            |> List.toArray
                            |> Task.WhenAll

                        let result =
                            {
                                PromotionSetId = promotionSet.PromotionSetId
                                Status = getDiscriminatedUnionCaseName promotionSet.Status
                                StepsComputationStatus = getDiscriminatedUnionCaseName promotionSet.StepsComputationStatus
                                StepsComputationAttempt = promotionSet.StepsComputationAttempt
                                ConflictedSteps = displays |> Array.toList
                            }

                        renderConflictSummary parseResult result
                        return Ok(GraceReturnValue.Create result graceIds.CorrelationId)
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    type ShowConflicts() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) : Task<int> =
            task {
                let! result = showConflictsHandler parseResult
                return result |> renderOutput parseResult
            }

    let private createPromotionSetHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames
                let promotionSetIdRaw = parseResult.GetValue(Options.promotionSetIdOptional)
                let targetBranchIdRaw = parseResult.GetValue(Options.targetBranchId)

                match parseOptionalPromotionSetId promotionSetIdRaw parseResult with
                | Error error -> return Error error
                | Ok promotionSetId ->
                    match tryParseGuid targetBranchIdRaw (QueueError.getErrorMessage QueueError.InvalidTargetBranchId) parseResult with
                    | Error error -> return Error error
                    | Ok targetBranchId ->
                        let parameters =
                            Parameters.PromotionSet.CreatePromotionSetParameters(
                                PromotionSetId = promotionSetId.ToString(),
                                TargetBranchId = targetBranchId.ToString(),
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                CorrelationId = graceIds.CorrelationId
                            )

                        let! result = Grace.SDK.PromotionSet.Create(parameters)

                        match result with
                        | Ok returnValue ->
                            if
                                not (parseResult |> json)
                                && not (parseResult |> silent)
                            then
                                AnsiConsole.MarkupLine(
                                    $"[green]Created promotion set[/] {Markup.Escape(promotionSetId.ToString())} [green]for target branch[/] {Markup.Escape(targetBranchId.ToString())}"
                                )

                            return Ok returnValue
                        | Error error -> return Error error
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    type CreatePromotionSet() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) : Task<int> =
            task {
                let! result = createPromotionSetHandler parseResult
                return result |> renderOutput parseResult
            }

    let private getPromotionSetHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames
                let promotionSetIdRaw = parseResult.GetValue(Options.promotionSetId)

                match tryParseGuid promotionSetIdRaw (QueueError.getErrorMessage QueueError.InvalidPromotionSetId) parseResult with
                | Error error -> return Error error
                | Ok promotionSetId ->
                    match! getPromotionSet graceIds promotionSetId with
                    | Ok returnValue ->
                        renderPromotionSet parseResult returnValue.ReturnValue
                        return Ok returnValue
                    | Error error -> return Error error
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    type GetPromotionSet() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) : Task<int> =
            task {
                let! result = getPromotionSetHandler parseResult
                return result |> renderOutput parseResult
            }

    let private getPromotionSetEventsHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames
                let promotionSetIdRaw = parseResult.GetValue(Options.promotionSetId)

                match tryParseGuid promotionSetIdRaw (QueueError.getErrorMessage QueueError.InvalidPromotionSetId) parseResult with
                | Error error -> return Error error
                | Ok promotionSetId ->
                    let parameters =
                        Parameters.PromotionSet.GetPromotionSetEventsParameters(
                            PromotionSetId = promotionSetId.ToString(),
                            OwnerId = graceIds.OwnerIdString,
                            OwnerName = graceIds.OwnerName,
                            OrganizationId = graceIds.OrganizationIdString,
                            OrganizationName = graceIds.OrganizationName,
                            RepositoryId = graceIds.RepositoryIdString,
                            RepositoryName = graceIds.RepositoryName,
                            CorrelationId = graceIds.CorrelationId
                        )

                    let! result = Grace.SDK.PromotionSet.GetEvents(parameters)

                    match result with
                    | Ok returnValue ->
                        renderPromotionSetEvents parseResult promotionSetId returnValue.ReturnValue
                        return Ok returnValue
                    | Error error -> return Error error
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    type GetPromotionSetEvents() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) : Task<int> =
            task {
                let! result = getPromotionSetEventsHandler parseResult
                return result |> renderOutput parseResult
            }

    let private updateInputPromotionsHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames
                let promotionSetIdRaw = parseResult.GetValue(Options.promotionSetId)
                let promotionPointersFilePath = parseResult.GetValue(Options.promotionPointersFile)

                match tryParseGuid promotionSetIdRaw (QueueError.getErrorMessage QueueError.InvalidPromotionSetId) parseResult with
                | Error error -> return Error error
                | Ok promotionSetId ->
                    match tryDeserializePromotionPointersFromFile promotionPointersFilePath with
                    | Error errorText -> return Error(GraceError.Create errorText (getCorrelationId parseResult))
                    | Ok promotionPointers ->
                        let parameters =
                            Parameters.PromotionSet.UpdatePromotionSetInputPromotionsParameters(
                                PromotionSetId = promotionSetId.ToString(),
                                PromotionPointers = promotionPointers,
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                CorrelationId = graceIds.CorrelationId
                            )

                        let! result = Grace.SDK.PromotionSet.UpdateInputPromotions(parameters)

                        match result with
                        | Ok returnValue ->
                            if
                                not (parseResult |> json)
                                && not (parseResult |> silent)
                            then
                                AnsiConsole.MarkupLine(
                                    $"[green]Updated input promotions for promotion set[/] {Markup.Escape(promotionSetId.ToString())} [green]with[/] {promotionPointers.Length} [green]pointer(s).[/]"
                                )

                            return Ok returnValue
                        | Error error -> return Error error
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    type UpdateInputPromotions() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) : Task<int> =
            task {
                let! result = updateInputPromotionsHandler parseResult
                return result |> renderOutput parseResult
            }

    let private recomputePromotionSetHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames
                let promotionSetIdRaw = parseResult.GetValue(Options.promotionSetId)

                let reasonText =
                    parseResult.GetValue(Options.reason)
                    |> Option.ofObj
                    |> Option.defaultValue String.Empty

                match tryParseGuid promotionSetIdRaw (QueueError.getErrorMessage QueueError.InvalidPromotionSetId) parseResult with
                | Error error -> return Error error
                | Ok promotionSetId ->
                    let parameters =
                        Parameters.PromotionSet.RecomputePromotionSetParameters(
                            PromotionSetId = promotionSetId.ToString(),
                            Reason = reasonText,
                            OwnerId = graceIds.OwnerIdString,
                            OwnerName = graceIds.OwnerName,
                            OrganizationId = graceIds.OrganizationIdString,
                            OrganizationName = graceIds.OrganizationName,
                            RepositoryId = graceIds.RepositoryIdString,
                            RepositoryName = graceIds.RepositoryName,
                            CorrelationId = graceIds.CorrelationId
                        )

                    let! result = Grace.SDK.PromotionSet.Recompute(parameters)

                    match result with
                    | Ok returnValue ->
                        if
                            not (parseResult |> json)
                            && not (parseResult |> silent)
                        then
                            AnsiConsole.MarkupLine($"[green]Requested recompute for promotion set[/] {Markup.Escape(promotionSetId.ToString())}")

                        return Ok returnValue
                    | Error error -> return Error error
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    type RecomputePromotionSet() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) : Task<int> =
            task {
                let! result = recomputePromotionSetHandler parseResult
                return result |> renderOutput parseResult
            }

    let private applyPromotionSetHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames
                let promotionSetIdRaw = parseResult.GetValue(Options.promotionSetId)

                match tryParseGuid promotionSetIdRaw (QueueError.getErrorMessage QueueError.InvalidPromotionSetId) parseResult with
                | Error error -> return Error error
                | Ok promotionSetId ->
                    let parameters =
                        Parameters.PromotionSet.ApplyPromotionSetParameters(
                            PromotionSetId = promotionSetId.ToString(),
                            OwnerId = graceIds.OwnerIdString,
                            OwnerName = graceIds.OwnerName,
                            OrganizationId = graceIds.OrganizationIdString,
                            OrganizationName = graceIds.OrganizationName,
                            RepositoryId = graceIds.RepositoryIdString,
                            RepositoryName = graceIds.RepositoryName,
                            CorrelationId = graceIds.CorrelationId
                        )

                    let! result = Grace.SDK.PromotionSet.Apply(parameters)

                    match result with
                    | Ok returnValue ->
                        if
                            not (parseResult |> json)
                            && not (parseResult |> silent)
                        then
                            AnsiConsole.MarkupLine($"[green]Requested apply for promotion set[/] {Markup.Escape(promotionSetId.ToString())}")

                        return Ok returnValue
                    | Error error -> return Error error
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    type ApplyPromotionSet() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) : Task<int> =
            task {
                let! result = applyPromotionSetHandler parseResult
                return result |> renderOutput parseResult
            }

    let private deletePromotionSetHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames
                let promotionSetIdRaw = parseResult.GetValue(Options.promotionSetId)
                let force = parseResult.GetValue(Options.force)

                let deleteReason =
                    parseResult.GetValue(Options.deleteReason)
                    |> Option.ofObj
                    |> Option.defaultValue String.Empty

                match tryParseGuid promotionSetIdRaw (QueueError.getErrorMessage QueueError.InvalidPromotionSetId) parseResult with
                | Error error -> return Error error
                | Ok promotionSetId ->
                    let parameters =
                        Parameters.PromotionSet.DeletePromotionSetParameters(
                            PromotionSetId = promotionSetId.ToString(),
                            Force = force,
                            DeleteReason = deleteReason,
                            OwnerId = graceIds.OwnerIdString,
                            OwnerName = graceIds.OwnerName,
                            OrganizationId = graceIds.OrganizationIdString,
                            OrganizationName = graceIds.OrganizationName,
                            RepositoryId = graceIds.RepositoryIdString,
                            RepositoryName = graceIds.RepositoryName,
                            CorrelationId = graceIds.CorrelationId
                        )

                    let! result = Grace.SDK.PromotionSet.Delete(parameters)

                    match result with
                    | Ok returnValue ->
                        if
                            not (parseResult |> json)
                            && not (parseResult |> silent)
                        then
                            AnsiConsole.MarkupLine($"[green]Deleted promotion set[/] {Markup.Escape(promotionSetId.ToString())}")

                        return Ok returnValue
                    | Error error -> return Error error
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    type DeletePromotionSet() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) : Task<int> =
            task {
                let! result = deletePromotionSetHandler parseResult
                return result |> renderOutput parseResult
            }

    let private resolveConflictsHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames
                let promotionSetIdRaw = parseResult.GetValue(Options.promotionSetId)
                let stepIdRaw = parseResult.GetValue(Options.stepId)
                let decisionsFilePath = parseResult.GetValue(Options.decisionsFile)

                match tryParseGuid promotionSetIdRaw (QueueError.getErrorMessage QueueError.InvalidPromotionSetId) parseResult with
                | Error error -> return Error error
                | Ok promotionSetId ->
                    match tryParseGuid stepIdRaw (ValidationResultError.getErrorMessage ValidationResultError.InvalidPromotionSetStepId) parseResult with
                    | Error error -> return Error error
                    | Ok stepId ->
                        match tryDeserializeDecisionsFromFile decisionsFilePath with
                        | Error errorText -> return Error(GraceError.Create errorText (getCorrelationId parseResult))
                        | Ok decisions ->
                            match! getPromotionSet graceIds promotionSetId with
                            | Error error -> return Error error
                            | Ok promotionSetReturnValue ->
                                let promotionSet = promotionSetReturnValue.ReturnValue

                                if promotionSet.StepsComputationAttempt <= 0 then
                                    return
                                        Error(
                                            GraceError.Create
                                                (ValidationResultError.getErrorMessage ValidationResultError.InvalidStepsComputationAttempt)
                                                (getCorrelationId parseResult)
                                        )
                                else
                                    let parameters =
                                        Parameters.PromotionSet.ResolvePromotionSetConflictsParameters(
                                            PromotionSetId = promotionSetId.ToString(),
                                            StepId = stepId.ToString(),
                                            Decisions = decisions,
                                            StepsComputationAttempt = promotionSet.StepsComputationAttempt,
                                            OwnerId = graceIds.OwnerIdString,
                                            OwnerName = graceIds.OwnerName,
                                            OrganizationId = graceIds.OrganizationIdString,
                                            OrganizationName = graceIds.OrganizationName,
                                            RepositoryId = graceIds.RepositoryIdString,
                                            RepositoryName = graceIds.RepositoryName,
                                            CorrelationId = graceIds.CorrelationId
                                        )

                                    let! result = Grace.SDK.PromotionSet.ResolveConflicts(parameters)

                                    match result with
                                    | Ok returnValue ->
                                        if
                                            not (parseResult |> json)
                                            && not (parseResult |> silent)
                                        then
                                            AnsiConsole.MarkupLine(
                                                $"[green]Submitted conflict decisions for promotion set[/] {Markup.Escape(promotionSetId.ToString())} [green]step[/] {Markup.Escape(stepId.ToString())}"
                                            )

                                        return Ok returnValue
                                    | Error error -> return Error error
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    type ResolveConflicts() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) : Task<int> =
            task {
                let! result = resolveConflictsHandler parseResult
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

        let promotionSetCommand = new Command("promotion-set", Description = "Manage promotion sets.")
        promotionSetCommand.Aliases.Add("prset")
        let conflictsCommand = new Command("conflicts", Description = "Inspect and resolve promotion set conflicts.")

        let createCommand =
            new Command("create", Description = "Create a promotion set for a target branch.")
            |> addOption Options.promotionSetIdOptional
            |> addOption Options.targetBranchId
            |> addCommonOptions

        createCommand.Action <- new CreatePromotionSet()
        promotionSetCommand.Subcommands.Add(createCommand)

        let getCommand =
            new Command("get", Description = "Get promotion set details.")
            |> addOption Options.promotionSetId
            |> addCommonOptions

        getCommand.Action <- new GetPromotionSet()
        promotionSetCommand.Subcommands.Add(getCommand)

        let getEventsCommand =
            new Command("get-events", Description = "Get promotion set events.")
            |> addOption Options.promotionSetId
            |> addCommonOptions

        getEventsCommand.Action <- new GetPromotionSetEvents()
        promotionSetCommand.Subcommands.Add(getEventsCommand)

        let updateInputPromotionsCommand =
            new Command("update-input-promotions", Description = "Update input promotion pointers for a promotion set.")
            |> addOption Options.promotionSetId
            |> addOption Options.promotionPointersFile
            |> addCommonOptions

        updateInputPromotionsCommand.Action <- new UpdateInputPromotions()
        promotionSetCommand.Subcommands.Add(updateInputPromotionsCommand)

        let recomputeCommand =
            new Command("recompute", Description = "Request recomputation of promotion set steps.")
            |> addOption Options.promotionSetId
            |> addOption Options.reason
            |> addCommonOptions

        recomputeCommand.Action <- new RecomputePromotionSet()
        promotionSetCommand.Subcommands.Add(recomputeCommand)

        let applyCommand =
            new Command("apply", Description = "Apply a promotion set.")
            |> addOption Options.promotionSetId
            |> addCommonOptions

        applyCommand.Action <- new ApplyPromotionSet()
        promotionSetCommand.Subcommands.Add(applyCommand)

        let deleteCommand =
            new Command("delete", Description = "Logically delete a promotion set.")
            |> addOption Options.promotionSetId
            |> addOption Options.force
            |> addOption Options.deleteReason
            |> addCommonOptions

        deleteCommand.Action <- new DeletePromotionSet()
        promotionSetCommand.Subcommands.Add(deleteCommand)

        let showConflictsCommand =
            new Command("show", Description = "Show conflicts for a promotion set and print conflict artifact URIs.")
            |> addOption Options.promotionSetId
            |> addCommonOptions

        showConflictsCommand.Action <- new ShowConflicts()
        conflictsCommand.Subcommands.Add(showConflictsCommand)

        let resolveConflictsCommand =
            new Command("resolve", Description = "Resolve blocked promotion set conflicts from a decisions JSON file.")
            |> addOption Options.promotionSetId
            |> addOption Options.stepId
            |> addOption Options.decisionsFile
            |> addCommonOptions

        resolveConflictsCommand.Action <- new ResolveConflicts()
        conflictsCommand.Subcommands.Add(resolveConflictsCommand)

        promotionSetCommand.Subcommands.Add(conflictsCommand)
        promotionSetCommand
