namespace Grace.CLI.Command

open Grace.CLI.Common
open Grace.CLI.Services
open Grace.CLI.Text
open Grace.SDK
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Grace.Types.Policy
open Grace.Types.Review
open Grace.Types.Types
open Spectre.Console
open System
open System.CommandLine
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks

module ReviewCommand =
    module private Options =
        let promotionSetId =
            new Option<string>(
                "--promotion-set",
                [| "--promotion-set-id" |],
                Required = false,
                Description = "The promotion set ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne
            )

        let referenceId =
            new Option<string>(
                OptionName.ReferenceId,
                Required = true,
                Description = "The reference ID <Guid> to mark reviewed.",
                Arity = ArgumentArity.ExactlyOne
            )

        let policySnapshotId =
            new Option<string>(
                "--policy-snapshot-id",
                Required = false,
                Description = "Policy snapshot ID for this checkpoint.",
                Arity = ArgumentArity.ExactlyOne
            )

        let findingId = new Option<string>("--finding-id", Required = true, Description = "The finding ID <Guid>.", Arity = ArgumentArity.ExactlyOne)

        let approve =
            new Option<bool>(
                "--approve",
                Required = false,
                Description = "Approve the finding.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> false)
            )

        let requestChanges =
            new Option<bool>(
                "--request-changes",
                Required = false,
                Description = "Request changes for the finding.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> false)
            )

        let note = new Option<string>("--note", Required = false, Description = "Optional note for the resolution.", Arity = ArgumentArity.ExactlyOne)

        let chapterId =
            new Option<string>("--chapter", Required = false, Description = "Chapter ID <Sha256Hash> for targeted deepening.", Arity = ArgumentArity.ExactlyOne)

        let candidateId =
            new Option<string>(
                "--candidate",
                [| "--candidate-id" |],
                Required = true,
                Description = "The candidate ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne
            )

        let reportFormat = new Option<string>("--format", Required = true, Description = "Export format: markdown or json.", Arity = ArgumentArity.ExactlyOne)

        let outputFile =
            new Option<string>(
                "--output-file",
                [| "-f" |],
                Required = true,
                Description = "Write exported report content to this file path.",
                Arity = ArgumentArity.ExactlyOne
            )

        let targetBranch =
            new Option<string>(
                "--target-branch",
                Required = false,
                Description = "Target branch ID or name for review inbox.",
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

    let private tryParseGuid (value: string) (error: ReviewError) (parseResult: ParseResult) =
        let mutable parsed = Guid.Empty

        if String.IsNullOrWhiteSpace(value)
           || Guid.TryParse(value, &parsed) = false
           || parsed = Guid.Empty then
            Error(GraceError.Create (ReviewError.getErrorMessage error) (getCorrelationId parseResult))
        else
            Ok parsed

    let internal resolvePolicySnapshotIdWith
        (getPromotionSet: Parameters.PromotionSet.GetPromotionSetParameters -> Task<GraceResult<Grace.Types.PromotionSet.PromotionSetDto>>)
        (getPolicy: Parameters.Policy.GetPolicyParameters -> Task<GraceResult<PolicySnapshot option>>)
        (parseResult: ParseResult)
        (graceIds: GraceIds)
        (promotionSetId: Guid)
        =
        task {
            let rawPolicySnapshotId =
                parseResult.GetValue(Options.policySnapshotId)
                |> Option.ofObj
                |> Option.defaultValue String.Empty

            if not (String.IsNullOrWhiteSpace rawPolicySnapshotId) then
                return Ok rawPolicySnapshotId
            else
                let promotionSetParameters =
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

                match! getPromotionSet promotionSetParameters with
                | Error error -> return Error error
                | Ok promotionSetReturnValue ->
                    let promotionSet = promotionSetReturnValue.ReturnValue

                    let policyParameters =
                        Parameters.Policy.GetPolicyParameters(
                            TargetBranchId = promotionSet.TargetBranchId.ToString(),
                            OwnerId = graceIds.OwnerIdString,
                            OwnerName = graceIds.OwnerName,
                            OrganizationId = graceIds.OrganizationIdString,
                            OrganizationName = graceIds.OrganizationName,
                            RepositoryId = graceIds.RepositoryIdString,
                            RepositoryName = graceIds.RepositoryName,
                            CorrelationId = graceIds.CorrelationId
                        )

                    match! getPolicy policyParameters with
                    | Error error -> return Error error
                    | Ok policyReturnValue ->
                        match policyReturnValue.ReturnValue with
                        | Some snapshot when not (String.IsNullOrWhiteSpace snapshot.PolicySnapshotId) -> return Ok snapshot.PolicySnapshotId
                        | _ -> return Error(GraceError.Create (ReviewError.getErrorMessage ReviewError.InvalidPolicySnapshotId) (getCorrelationId parseResult))
        }

    let private resolvePolicySnapshotId (parseResult: ParseResult) (graceIds: GraceIds) (promotionSetId: Guid) =
        resolvePolicySnapshotIdWith PromotionSet.Get Policy.GetCurrent parseResult graceIds promotionSetId

    type private ReportExportFormat =
        | Markdown
        | Json

    let private parseReportExportFormat (rawValue: string) (parseResult: ParseResult) =
        match rawValue.Trim().ToLowerInvariant() with
        | "markdown" -> Ok Markdown
        | "json" -> Ok Json
        | _ -> Error(GraceError.Create "Format must be either 'markdown' or 'json'." (getCorrelationId parseResult))

    let private resolveCandidateId (parseResult: ParseResult) =
        let candidateIdRaw =
            parseResult.GetValue(Options.candidateId)
            |> Option.ofObj
            |> Option.defaultValue String.Empty

        let mutable parsed = Guid.Empty

        if String.IsNullOrWhiteSpace(candidateIdRaw)
           || not (Guid.TryParse(candidateIdRaw, &parsed))
           || parsed = Guid.Empty then
            Error(GraceError.Create "CandidateId must be a valid non-empty Guid." (getCorrelationId parseResult))
        else
            Ok(parsed.ToString())

    let private buildCandidateProjectionParameters (graceIds: GraceIds) (candidateId: string) =
        Parameters.Review.CandidateProjectionParameters(
            CandidateId = candidateId,
            OwnerId = graceIds.OwnerIdString,
            OwnerName = graceIds.OwnerName,
            OrganizationId = graceIds.OrganizationIdString,
            OrganizationName = graceIds.OrganizationName,
            RepositoryId = graceIds.RepositoryIdString,
            RepositoryName = graceIds.RepositoryName,
            CorrelationId = graceIds.CorrelationId
        )

    let internal normalizeReviewReportForOutput (report: ReviewReportResult) =
        let normalized = ReviewReportResult()
        normalized.ReviewReportSchemaVersion <- report.ReviewReportSchemaVersion
        normalized.SectionOrder <- report.SectionOrder

        let sectionOrderRank =
            normalized.SectionOrder
            |> List.mapi (fun index section -> section, index)
            |> dict

        let normalizedSections =
            report.Sections
            |> List.map (fun section ->
                let normalizedSection = ReviewReportSection()
                normalizedSection.Section <- section.Section
                normalizedSection.Title <- section.Title
                normalizedSection.SourceState <- section.SourceState

                normalizedSection.SourceStates <-
                    section.SourceStates
                    |> List.sortBy (fun sourceState -> sourceState.Section, sourceState.SourceState, sourceState.Detail)

                normalizedSection.Entries <-
                    section.Entries
                    |> List.sortBy (fun entry -> entry.Key)
                    |> List.map (fun entry ->
                        let normalizedEntry = ReviewReportEntry()
                        normalizedEntry.Key <- entry.Key
                        normalizedEntry.Values <- entry.Values |> List.sort
                        normalizedEntry)

                normalizedSection.Diagnostics <- section.Diagnostics |> List.sort
                normalizedSection)
            |> List.sortBy (fun section ->
                (if sectionOrderRank.ContainsKey(section.Section) then
                     sectionOrderRank[section.Section]
                 else
                     Int32.MaxValue),
                section.Section)

        normalized.Sections <- normalizedSections
        normalized

    let internal renderReviewReportMarkdown (report: ReviewReportResult) =
        let normalized = normalizeReviewReportForOutput report
        let markdown = StringBuilder()

        markdown.AppendLine($"# Review Report (schema {normalized.ReviewReportSchemaVersion})")
        |> ignore

        for section in normalized.Sections do
            markdown.AppendLine() |> ignore

            markdown.AppendLine($"## {section.Title}")
            |> ignore

            markdown.AppendLine($"- Section: {section.Section}")
            |> ignore

            markdown.AppendLine($"- SourceState: {section.SourceState}")
            |> ignore

            for entry in section.Entries do
                if entry.Values.IsEmpty then
                    markdown.AppendLine($"- {entry.Key}: NotAvailable")
                    |> ignore
                elif entry.Values.Length = 1 then
                    markdown.AppendLine($"- {entry.Key}: {entry.Values[0]}")
                    |> ignore
                else
                    markdown.AppendLine($"- {entry.Key}:") |> ignore

                    for value in entry.Values do
                        markdown.AppendLine($"  - {value}") |> ignore

            if not section.Diagnostics.IsEmpty then
                markdown.AppendLine("- Diagnostics:") |> ignore

                for diagnostic in section.Diagnostics do
                    markdown.AppendLine($"  - {diagnostic}") |> ignore

            if not section.SourceStates.IsEmpty then
                markdown.AppendLine("- SourceStates:") |> ignore

                for sourceState in section.SourceStates do
                    markdown.AppendLine($"  - {sourceState.Section}: {sourceState.SourceState} ({sourceState.Detail})")
                    |> ignore

        markdown.ToString().TrimEnd()

    let internal serializeReviewReportJson (report: ReviewReportResult) =
        let normalized = normalizeReviewReportForOutput report
        serialize normalized

    let private writeNotesSummary (parseResult: ParseResult) (notes: ReviewNotes) =
        if
            not (parseResult |> json)
            && not (parseResult |> silent)
        then
            AnsiConsole.MarkupLine($"[bold]Review Notes[/] {Markup.Escape(notes.ReviewNotesId.ToString())}")

            if not (String.IsNullOrWhiteSpace notes.Summary) then
                AnsiConsole.MarkupLine($"[bold]Summary:[/] {Markup.Escape(notes.Summary)}")

            AnsiConsole.MarkupLine($"[bold]Chapters:[/] {notes.Chapters.Length}  [bold]Findings:[/] {notes.Findings.Length}")

    let private inboxHandler (parseResult: ParseResult) =
        task {
            let graceError = GraceError.Create "Review inbox is not implemented yet." (getCorrelationId parseResult)

            return Error graceError
        }

    type Inbox() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) : Task<int> =
            task {
                let! result = inboxHandler parseResult
                return result |> renderOutput parseResult
            }

    let private openHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames

                let promotionSetIdRaw =
                    parseResult.GetValue(Options.promotionSetId)
                    |> Option.ofObj
                    |> Option.defaultValue String.Empty

                if String.IsNullOrWhiteSpace promotionSetIdRaw then
                    return Error(GraceError.Create (ReviewError.getErrorMessage ReviewError.InvalidPromotionSetId) (getCorrelationId parseResult))
                else
                    match tryParseGuid promotionSetIdRaw ReviewError.InvalidPromotionSetId parseResult with
                    | Error error -> return Error error
                    | Ok promotionSetId ->
                        let parameters =
                            Parameters.Review.GetReviewNotesParameters(
                                PromotionSetId = promotionSetId.ToString(),
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                CorrelationId = graceIds.CorrelationId
                            )

                        let! result = Review.GetNotes(parameters)

                        match result with
                        | Ok returnValue ->
                            match returnValue.ReturnValue with
                            | Some notes -> writeNotesSummary parseResult notes
                            | None ->
                                if
                                    not (parseResult |> json)
                                    && not (parseResult |> silent)
                                then
                                    AnsiConsole.MarkupLine("[yellow]No review notes found.[/]")

                            return Ok returnValue
                        | Error error -> return Error error
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    type Open() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) : Task<int> =
            task {
                let! result = openHandler parseResult
                return result |> renderOutput parseResult
            }

    let private checkpointHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames

                let promotionSetIdRaw =
                    parseResult.GetValue(Options.promotionSetId)
                    |> Option.ofObj
                    |> Option.defaultValue String.Empty

                match tryParseGuid promotionSetIdRaw ReviewError.InvalidPromotionSetId parseResult with
                | Error error -> return Error error
                | Ok promotionSetId ->
                    let referenceIdRaw = parseResult.GetValue(Options.referenceId)

                    match tryParseGuid referenceIdRaw ReviewError.InvalidReferenceId parseResult with
                    | Error error -> return Error error
                    | Ok referenceId ->
                        let! policySnapshotIdResult = resolvePolicySnapshotId parseResult graceIds promotionSetId

                        match policySnapshotIdResult with
                        | Error error -> return Error error
                        | Ok policySnapshotId ->
                            let parameters =
                                Parameters.Review.ReviewCheckpointParameters(
                                    PromotionSetId = promotionSetId.ToString(),
                                    ReviewedUpToReferenceId = referenceId.ToString(),
                                    PolicySnapshotId = policySnapshotId,
                                    OwnerId = graceIds.OwnerIdString,
                                    OwnerName = graceIds.OwnerName,
                                    OrganizationId = graceIds.OrganizationIdString,
                                    OrganizationName = graceIds.OrganizationName,
                                    RepositoryId = graceIds.RepositoryIdString,
                                    RepositoryName = graceIds.RepositoryName,
                                    CorrelationId = graceIds.CorrelationId
                                )

                            return! Review.Checkpoint(parameters)
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    type Checkpoint() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) : Task<int> =
            task {
                let! result = checkpointHandler parseResult
                return result |> renderOutput parseResult
            }

    let private resolveHandlerImpl (parseResult: ParseResult) =
        if parseResult |> verbose then printParseResult parseResult
        let graceIds = parseResult |> getNormalizedIdsAndNames

        let promotionSetIdRaw =
            parseResult.GetValue(Options.promotionSetId)
            |> Option.ofObj
            |> Option.defaultValue String.Empty

        match tryParseGuid promotionSetIdRaw ReviewError.InvalidPromotionSetId parseResult with
        | Error error -> Task.FromResult(Error error)
        | Ok promotionSetId ->
            let findingIdRaw = parseResult.GetValue(Options.findingId)

            match tryParseGuid findingIdRaw ReviewError.InvalidFindingId parseResult with
            | Error error -> Task.FromResult(Error error)
            | Ok findingId ->
                let approve = parseResult.GetValue(Options.approve)
                let requestChanges = parseResult.GetValue(Options.requestChanges)

                if approve = requestChanges then
                    Task.FromResult(Error(GraceError.Create "Specify exactly one of --approve or --request-changes." (getCorrelationId parseResult)))
                else
                    let resolutionState =
                        if approve then
                            FindingResolutionState.Approved
                        else
                            FindingResolutionState.NeedsChanges

                    let note =
                        parseResult.GetValue(Options.note)
                        |> Option.ofObj
                        |> Option.defaultValue String.Empty

                    let parameters =
                        Parameters.Review.ResolveFindingParameters(
                            PromotionSetId = promotionSetId.ToString(),
                            FindingId = findingId.ToString(),
                            ResolutionState = getDiscriminatedUnionCaseName resolutionState,
                            Note = note,
                            OwnerId = graceIds.OwnerIdString,
                            OwnerName = graceIds.OwnerName,
                            OrganizationId = graceIds.OrganizationIdString,
                            OrganizationName = graceIds.OrganizationName,
                            RepositoryId = graceIds.RepositoryIdString,
                            RepositoryName = graceIds.RepositoryName,
                            CorrelationId = graceIds.CorrelationId
                        )

                    Review.ResolveFinding(parameters)

    let private resolveHandler (parseResult: ParseResult) =
        task {
            try
                return! resolveHandlerImpl parseResult
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    type Resolve() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) : Task<int> =
            task {
                let! result = resolveHandler parseResult
                return result |> renderOutput parseResult
            }

    let private deepenHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames

                let promotionSetIdRaw =
                    parseResult.GetValue(Options.promotionSetId)
                    |> Option.ofObj
                    |> Option.defaultValue String.Empty

                match tryParseGuid promotionSetIdRaw ReviewError.InvalidPromotionSetId parseResult with
                | Error error -> return Error error
                | Ok promotionSetId ->
                    let chapterId =
                        parseResult.GetValue(Options.chapterId)
                        |> Option.ofObj
                        |> Option.defaultValue String.Empty

                    let parameters =
                        Parameters.Review.DeepenReviewParameters(
                            PromotionSetId = promotionSetId.ToString(),
                            ChapterId = chapterId,
                            OwnerId = graceIds.OwnerIdString,
                            OwnerName = graceIds.OwnerName,
                            OrganizationId = graceIds.OrganizationIdString,
                            OrganizationName = graceIds.OrganizationName,
                            RepositoryId = graceIds.RepositoryIdString,
                            RepositoryName = graceIds.RepositoryName,
                            CorrelationId = graceIds.CorrelationId
                        )

                    return! Review.Deepen(parameters)
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    type Deepen() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) : Task<int> =
            task {
                let! result = deepenHandler parseResult
                return result |> renderOutput parseResult
            }

    let private reportShowHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames

                match resolveCandidateId parseResult with
                | Error error -> return Error error
                | Ok candidateId ->
                    let parameters = buildCandidateProjectionParameters graceIds candidateId
                    let! result = Review.GetReviewReport(parameters)

                    match result with
                    | Ok returnValue ->
                        if
                            not (parseResult |> json)
                            && not (parseResult |> silent)
                        then
                            let markdown = renderReviewReportMarkdown returnValue.ReturnValue
                            Console.WriteLine(markdown)

                        return Ok returnValue
                    | Error error -> return Error error
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    type ReportShow() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) : Task<int> =
            task {
                let! result = reportShowHandler parseResult
                return result |> renderOutput parseResult
            }

    let private reportExportHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames

                let outputFile =
                    parseResult.GetValue(Options.outputFile)
                    |> Option.ofObj
                    |> Option.defaultValue String.Empty

                if String.IsNullOrWhiteSpace outputFile then
                    return Error(GraceError.Create "Output file path is required." (getCorrelationId parseResult))
                else
                    let reportFormatRaw =
                        parseResult.GetValue(Options.reportFormat)
                        |> Option.ofObj
                        |> Option.defaultValue String.Empty

                    match parseReportExportFormat reportFormatRaw parseResult with
                    | Error error -> return Error error
                    | Ok reportFormat ->
                        match resolveCandidateId parseResult with
                        | Error error -> return Error error
                        | Ok candidateId ->
                            let parameters = buildCandidateProjectionParameters graceIds candidateId
                            let! result = Review.GetReviewReport(parameters)

                            match result with
                            | Error error -> return Error error
                            | Ok returnValue ->
                                let report = returnValue.ReturnValue

                                let content =
                                    match reportFormat with
                                    | Markdown -> renderReviewReportMarkdown report
                                    | Json -> serializeReviewReportJson report

                                let outputDirectory = Path.GetDirectoryName(outputFile)

                                if not (String.IsNullOrWhiteSpace outputDirectory) then
                                    Directory.CreateDirectory(outputDirectory)
                                    |> ignore

                                do! File.WriteAllTextAsync(outputFile, content)

                                if
                                    not (parseResult |> json)
                                    && not (parseResult |> silent)
                                then
                                    let formatText =
                                        match reportFormat with
                                        | Markdown -> "markdown"
                                        | Json -> "json"

                                    AnsiConsole.MarkupLine($"[green]Review report exported ({formatText}) to[/] {Markup.Escape(outputFile)}")

                                return Ok returnValue
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    type ReportExport() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) : Task<int> =
            task {
                let! result = reportExportHandler parseResult
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

        let reviewCommand = new Command("review", Description = "Promotion-set review operations plus candidate report output.")

        let inboxCommand = new Command("inbox", Description = "Show review inbox (stub).")

        inboxCommand
        |> addOption Options.targetBranch
        |> addCommonOptions
        |> ignore

        inboxCommand.Action <- new Inbox()
        reviewCommand.Subcommands.Add(inboxCommand)

        let openCommand = new Command("open", Description = "Open review notes for a promotion set.")

        openCommand
        |> addOption Options.promotionSetId
        |> addCommonOptions
        |> ignore

        openCommand.Action <- new Open()
        reviewCommand.Subcommands.Add(openCommand)

        let checkpointCommand =
            new Command("checkpoint", Description = "Record a review checkpoint for a promotion set.")
            |> addOption Options.promotionSetId
            |> addOption Options.referenceId
            |> addOption Options.policySnapshotId
            |> addCommonOptions

        checkpointCommand.Action <- new Checkpoint()
        reviewCommand.Subcommands.Add(checkpointCommand)

        let resolveCommand =
            new Command("resolve", Description = "Resolve a review finding for a promotion set.")
            |> addOption Options.promotionSetId
            |> addOption Options.findingId
            |> addOption Options.approve
            |> addOption Options.requestChanges
            |> addOption Options.note
            |> addCommonOptions

        resolveCommand.Action <- new Resolve()
        reviewCommand.Subcommands.Add(resolveCommand)

        let deepenCommand =
            new Command("deepen", Description = "Request deeper analysis (stub).")
            |> addOption Options.promotionSetId
            |> addOption Options.chapterId
            |> addCommonOptions

        deepenCommand.Action <- new Deepen()
        reviewCommand.Subcommands.Add(deepenCommand)

        let reportCommand = new Command("report", Description = "Generate candidate-first unified review reports.")

        let reportShowCommand =
            new Command("show", Description = "Show review report sections in deterministic markdown order.")
            |> addOption Options.candidateId
            |> addCommonOptions

        reportShowCommand.Action <- new ReportShow()
        reportCommand.Subcommands.Add(reportShowCommand)

        let reportExportCommand =
            new Command("export", Description = "Export review report as markdown or json.")
            |> addOption Options.candidateId
            |> addOption Options.reportFormat
            |> addOption Options.outputFile
            |> addCommonOptions

        reportExportCommand.Action <- new ReportExport()
        reportCommand.Subcommands.Add(reportExportCommand)
        reviewCommand.Subcommands.Add(reportCommand)

        reviewCommand
