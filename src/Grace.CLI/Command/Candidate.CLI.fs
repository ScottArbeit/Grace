namespace Grace.CLI.Command

open Grace.CLI.Common
open Grace.CLI.Services
open Grace.CLI.Text
open Grace.SDK
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Types
open Spectre.Console
open System
open System.CommandLine
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.Threading
open System.Threading.Tasks

module CandidateCommand =
    module private Options =
        let candidateId =
            new Option<string>(
                "--candidate",
                [| "--candidate-id" |],
                Required = true,
                Description = "The candidate ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne
            )

        let gate = new Option<string>("--gate", Required = true, Description = "The gate name to rerun.", Arity = ArgumentArity.ExactlyOne)

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

    let internal tryParseCandidateId (candidateId: string) (parseResult: ParseResult) =
        let mutable parsed = Guid.Empty

        if String.IsNullOrWhiteSpace(candidateId)
           || not (Guid.TryParse(candidateId, &parsed))
           || parsed = Guid.Empty then
            Error(GraceError.Create "CandidateId must be a valid non-empty Guid." (getCorrelationId parseResult))
        else
            Ok(parsed.ToString())

    let internal buildCandidateProjectionParameters (graceIds: GraceIds) (candidateId: string) =
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

    let internal buildCandidateGateRerunParameters (graceIds: GraceIds) (candidateId: string) (gate: string) =
        Parameters.Review.CandidateGateRerunParameters(
            CandidateId = candidateId,
            Gate = gate,
            OwnerId = graceIds.OwnerIdString,
            OwnerName = graceIds.OwnerName,
            OrganizationId = graceIds.OrganizationIdString,
            OrganizationName = graceIds.OrganizationName,
            RepositoryId = graceIds.RepositoryIdString,
            RepositoryName = graceIds.RepositoryName,
            CorrelationId = graceIds.CorrelationId
        )

    let private renderCandidateSnapshot (parseResult: ParseResult) (snapshot: Parameters.Review.CandidateProjectionSnapshotResult) =
        if
            not (parseResult |> json)
            && not (parseResult |> silent)
        then
            let table = Table(Border = TableBorder.Rounded)
            table.AddColumn("Field") |> ignore
            table.AddColumn("Value") |> ignore

            table.AddRow("Candidate", Markup.Escape(snapshot.Identity.CandidateId))
            |> ignore

            table.AddRow("PromotionSet", Markup.Escape(snapshot.Identity.PromotionSetId))
            |> ignore

            table.AddRow("PromotionSetStatus", Markup.Escape(snapshot.PromotionSetStatus))
            |> ignore

            table.AddRow("StepsComputationStatus", Markup.Escape(snapshot.StepsComputationStatus))
            |> ignore

            table.AddRow("QueueState", Markup.Escape(snapshot.QueueState))
            |> ignore

            table.AddRow(
                "RunningPromotionSetId",
                if String.IsNullOrWhiteSpace(snapshot.RunningPromotionSetId) then
                    "-"
                else
                    Markup.Escape(snapshot.RunningPromotionSetId)
            )
            |> ignore

            table.AddRow("UnresolvedFindings", snapshot.UnresolvedFindingCount.ToString())
            |> ignore

            table.AddRow("ValidationSummaryAvailable", snapshot.ValidationSummaryAvailable.ToString())
            |> ignore

            table.AddRow("RequiredActions", Markup.Escape(String.Join(", ", snapshot.RequiredActions)))
            |> ignore

            if not snapshot.Diagnostics.IsEmpty then
                table.AddRow("Diagnostics", Markup.Escape(String.Join(" | ", snapshot.Diagnostics)))
                |> ignore

            AnsiConsole.Write(table)

    let private renderCandidateRequiredActions (parseResult: ParseResult) (result: Parameters.Review.CandidateRequiredActionsResult) =
        if
            not (parseResult |> json)
            && not (parseResult |> silent)
        then
            AnsiConsole.MarkupLine($"[bold]Candidate[/] {Markup.Escape(result.Identity.CandidateId)}")
            let requiredActionsText = String.Join(", ", result.RequiredActions)
            AnsiConsole.MarkupLine($"[bold]Required actions:[/] {Markup.Escape(requiredActionsText)}")

            if not result.Diagnostics.IsEmpty then
                let diagnosticsText = String.Join(" | ", result.Diagnostics)
                AnsiConsole.MarkupLine($"[yellow]Diagnostics:[/] {Markup.Escape(diagnosticsText)}")

    let private renderCandidateAttestations (parseResult: ParseResult) (result: Parameters.Review.CandidateAttestationsResult) =
        if
            not (parseResult |> json)
            && not (parseResult |> silent)
        then
            let table = Table(Border = TableBorder.Rounded)
            table.AddColumn("Attestation") |> ignore
            table.AddColumn("Status") |> ignore
            table.AddColumn("Detail") |> ignore

            result.Attestations
            |> List.iter (fun attestation ->
                table.AddRow(Markup.Escape(attestation.Name), Markup.Escape(attestation.Status), Markup.Escape(attestation.Detail))
                |> ignore)

            AnsiConsole.Write(table)

            if not result.Diagnostics.IsEmpty then
                let diagnosticsText = String.Join(" | ", result.Diagnostics)
                AnsiConsole.MarkupLine($"[yellow]Diagnostics:[/] {Markup.Escape(diagnosticsText)}")

    let private renderCandidateActionResult (parseResult: ParseResult) (result: Parameters.Review.CandidateActionResult) =
        if
            not (parseResult |> json)
            && not (parseResult |> silent)
        then
            AnsiConsole.MarkupLine(
                $"[green]Candidate action[/] {Markup.Escape(result.Action)} [green]completed for[/] {Markup.Escape(result.Identity.CandidateId)}"
            )

            if not result.AppliedOperations.IsEmpty then
                let operationsText = String.Join(" -> ", result.AppliedOperations)
                AnsiConsole.MarkupLine($"[bold]Operations:[/] {Markup.Escape(operationsText)}")

            if not result.Diagnostics.IsEmpty then
                let diagnosticsText = String.Join(" | ", result.Diagnostics)
                AnsiConsole.MarkupLine($"[yellow]Diagnostics:[/] {Markup.Escape(diagnosticsText)}")

    let private resolveCandidateFromParseResult (parseResult: ParseResult) =
        let candidateId = parseResult.GetValue(Options.candidateId)
        tryParseCandidateId candidateId parseResult

    let private getHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames

                match resolveCandidateFromParseResult parseResult with
                | Error error -> return Error error
                | Ok candidateId ->
                    let parameters = buildCandidateProjectionParameters graceIds candidateId
                    let! result = Review.GetCandidate(parameters)

                    match result with
                    | Ok returnValue ->
                        renderCandidateSnapshot parseResult returnValue.ReturnValue
                        return Ok returnValue
                    | Error error -> return Error error
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    type Get() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) : Task<int> =
            task {
                let! result = getHandler parseResult
                return result |> renderOutput parseResult
            }

    let private requiredActionsHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames

                match resolveCandidateFromParseResult parseResult with
                | Error error -> return Error error
                | Ok candidateId ->
                    let parameters = buildCandidateProjectionParameters graceIds candidateId
                    let! result = Review.GetCandidateRequiredActions(parameters)

                    match result with
                    | Ok returnValue ->
                        renderCandidateRequiredActions parseResult returnValue.ReturnValue
                        return Ok returnValue
                    | Error error -> return Error error
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    type RequiredActions() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) : Task<int> =
            task {
                let! result = requiredActionsHandler parseResult
                return result |> renderOutput parseResult
            }

    let private attestationsHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames

                match resolveCandidateFromParseResult parseResult with
                | Error error -> return Error error
                | Ok candidateId ->
                    let parameters = buildCandidateProjectionParameters graceIds candidateId
                    let! result = Review.GetCandidateAttestations(parameters)

                    match result with
                    | Ok returnValue ->
                        renderCandidateAttestations parseResult returnValue.ReturnValue
                        return Ok returnValue
                    | Error error -> return Error error
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    type Attestations() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) : Task<int> =
            task {
                let! result = attestationsHandler parseResult
                return result |> renderOutput parseResult
            }

    let private retryHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames

                match resolveCandidateFromParseResult parseResult with
                | Error error -> return Error error
                | Ok candidateId ->
                    let parameters = buildCandidateProjectionParameters graceIds candidateId
                    let! result = Review.RetryCandidate(parameters)

                    match result with
                    | Ok returnValue ->
                        renderCandidateActionResult parseResult returnValue.ReturnValue
                        return Ok returnValue
                    | Error error -> return Error error
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    type Retry() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) : Task<int> =
            task {
                let! result = retryHandler parseResult
                return result |> renderOutput parseResult
            }

    let private cancelHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames

                match resolveCandidateFromParseResult parseResult with
                | Error error -> return Error error
                | Ok candidateId ->
                    let parameters = buildCandidateProjectionParameters graceIds candidateId
                    let! result = Review.CancelCandidate(parameters)

                    match result with
                    | Ok returnValue ->
                        renderCandidateActionResult parseResult returnValue.ReturnValue
                        return Ok returnValue
                    | Error error -> return Error error
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    type Cancel() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) : Task<int> =
            task {
                let! result = cancelHandler parseResult
                return result |> renderOutput parseResult
            }

    let private gateRerunHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames

                let gate =
                    parseResult.GetValue(Options.gate)
                    |> Option.ofObj
                    |> Option.defaultValue String.Empty

                if String.IsNullOrWhiteSpace gate then
                    return Error(GraceError.Create "Gate is required for candidate gate rerun." (getCorrelationId parseResult))
                else
                    match resolveCandidateFromParseResult parseResult with
                    | Error error -> return Error error
                    | Ok candidateId ->
                        let parameters = buildCandidateGateRerunParameters graceIds candidateId gate
                        let! result = Review.RerunCandidateGate(parameters)

                        match result with
                        | Ok returnValue ->
                            renderCandidateActionResult parseResult returnValue.ReturnValue
                            return Ok returnValue
                        | Error error -> return Error error
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId parseResult))
        }

    type GateRerun() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) : Task<int> =
            task {
                let! result = gateRerunHandler parseResult
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

        let candidateCommand =
            new Command("candidate", Description = "Candidate-first reviewer operations projected over PromotionSet-backed runtime semantics.")

        let getCommand =
            new Command("get", Description = "Get candidate projection details.")
            |> addOption Options.candidateId
            |> addCommonOptions

        getCommand.Action <- new Get()
        candidateCommand.Subcommands.Add(getCommand)

        let requiredActionsCommand =
            new Command("required-actions", Description = "Get deterministic required actions for a candidate.")
            |> addOption Options.candidateId
            |> addCommonOptions

        requiredActionsCommand.Action <- new RequiredActions()
        candidateCommand.Subcommands.Add(requiredActionsCommand)

        let attestationsCommand =
            new Command("attestations", Description = "Get candidate attestation state from policy and review checkpoints.")
            |> addOption Options.candidateId
            |> addCommonOptions

        attestationsCommand.Action <- new Attestations()
        candidateCommand.Subcommands.Add(attestationsCommand)

        let retryCommand =
            new Command("retry", Description = "Retry candidate processing through recompute and queue operations.")
            |> addOption Options.candidateId
            |> addCommonOptions

        retryCommand.Action <- new Retry()
        candidateCommand.Subcommands.Add(retryCommand)

        let cancelCommand =
            new Command("cancel", Description = "Cancel queued candidate processing.")
            |> addOption Options.candidateId
            |> addCommonOptions

        cancelCommand.Action <- new Cancel()
        candidateCommand.Subcommands.Add(cancelCommand)

        let gateCommand = new Command("gate", Description = "Gate-related candidate operations.")

        let gateRerunCommand =
            new Command("rerun", Description = "Rerun candidate gate evaluation semantics.")
            |> addOption Options.candidateId
            |> addOption Options.gate
            |> addCommonOptions

        gateRerunCommand.Action <- new GateRerun()
        gateCommand.Subcommands.Add(gateRerunCommand)
        candidateCommand.Subcommands.Add(gateCommand)

        candidateCommand
