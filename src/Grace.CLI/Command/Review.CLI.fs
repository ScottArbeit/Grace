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

    let private deltaHandler (parseResult: ParseResult) =
        task {
            let graceError = GraceError.Create "Review delta is not implemented yet." (getCorrelationId parseResult)

            return Error graceError
        }

    type Delta() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) : Task<int> =
            task {
                let! result = deltaHandler parseResult
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

    let Build =
        let addCommonOptions (command: Command) =
            command
            |> addOption Options.ownerName
            |> addOption Options.ownerId
            |> addOption Options.organizationName
            |> addOption Options.organizationId
            |> addOption Options.repositoryName
            |> addOption Options.repositoryId

        let reviewCommand = new Command("review", Description = "Review notes and findings.")

        let inboxCommand = new Command("inbox", Description = "Show review inbox (stub).")

        inboxCommand
        |> addOption Options.targetBranch
        |> addCommonOptions
        |> ignore

        inboxCommand.Action <- new Inbox()
        reviewCommand.Subcommands.Add(inboxCommand)

        let openCommand = new Command("open", Description = "Open review notes.")

        openCommand
        |> addOption Options.promotionSetId
        |> addCommonOptions
        |> ignore

        openCommand.Action <- new Open()
        reviewCommand.Subcommands.Add(openCommand)

        let checkpointCommand =
            new Command("checkpoint", Description = "Record a review checkpoint.")
            |> addOption Options.promotionSetId
            |> addOption Options.referenceId
            |> addOption Options.policySnapshotId
            |> addCommonOptions

        checkpointCommand.Action <- new Checkpoint()
        reviewCommand.Subcommands.Add(checkpointCommand)

        let deltaCommand =
            new Command("delta", Description = "Show changes since last checkpoint (stub).")
            |> addOption Options.promotionSetId
            |> addCommonOptions

        deltaCommand.Action <- new Delta()
        reviewCommand.Subcommands.Add(deltaCommand)

        let resolveCommand =
            new Command("resolve", Description = "Resolve a review finding.")
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

        reviewCommand
