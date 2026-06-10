namespace Grace.CLI.Command

open Grace.CLI.Common
open Grace.CLI.Text
open Grace.Shared
open Grace.Types.Common
open Spectre.Console
open System
open System.Collections.Generic
open System.CommandLine
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.Threading
open System.Threading.Tasks

module Doctor =

    [<Literal>]
    let ReportVersion = "doctor-report-v1"

    module private Options =
        let full =
            new Option<bool>(
                OptionName.Full,
                Required = false,
                Description = "Include checks reserved for the full doctor profile.",
                Arity = ArgumentArity.Zero,
                DefaultValueFactory = (fun _ -> false)
            )

        let offline =
            new Option<bool>(
                OptionName.Offline,
                Required = false,
                Description = "Limit the scaffolded report to checks that can run without network access.",
                Arity = ArgumentArity.Zero,
                DefaultValueFactory = (fun _ -> false)
            )

        let listChecks =
            new Option<bool>(
                OptionName.ListChecks,
                Required = false,
                Description = "List the inert doctor check catalog without running diagnostics.",
                Arity = ArgumentArity.Zero,
                DefaultValueFactory = (fun _ -> false)
            )

        let check =
            new Option<string []>(
                OptionName.Check,
                Required = false,
                Description = "Filter the scaffolded doctor report by check ID or category. Repeat or separate values with commas.",
                Arity = ArgumentArity.ZeroOrMore
            )

        let strict =
            new Option<bool>(
                OptionName.Strict,
                Required = false,
                Description = "Return a failing exit code when the doctor report status is Warning.",
                Arity = ArgumentArity.Zero,
                DefaultValueFactory = (fun _ -> false)
            )

    let catalog: LocalOutputDto.DoctorCheckDto array =
        [|
            {
                Id = "cli.catalog"
                Category = "CLI"
                Title = "CLI command catalog"
                Description = "Verifies that the Grace CLI command catalog is available. Scaffold only; no runtime probe is executed."
                DefaultEnabled = true
                SupportsOffline = true
            }
            {
                Id = "configuration.config-file"
                Category = "Configuration"
                Title = "Grace configuration file"
                Description = "Reserved for a later configuration-file diagnostic. Scaffold only; no file is read in this slice."
                DefaultEnabled = true
                SupportsOffline = true
            }
            {
                Id = "identity.auth-session"
                Category = "Identity"
                Title = "Authentication session"
                Description = "Reserved for a later authentication diagnostic. Scaffold only; no auth state is read in this slice."
                DefaultEnabled = false
                SupportsOffline = false
            }
            {
                Id = "server.connectivity"
                Category = "Server"
                Title = "Grace server connectivity"
                Description = "Reserved for a later server connectivity diagnostic. Scaffold only; no network probe is executed."
                DefaultEnabled = false
                SupportsOffline = false
            }
        |]

    let private tokenizeChecks (values: string array) =
        if isNull values then
            Array.empty
        else
            values
            |> Array.collect (fun value ->
                value.Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries
                    ||| StringSplitOptions.TrimEntries
                ))
            |> Array.filter (String.IsNullOrWhiteSpace >> not)
            |> Array.distinctBy (fun value -> value.ToUpperInvariant())

    let private selectedCatalogEntries full offline requestedTokens =
        let profileEntries =
            catalog
            |> Array.filter (fun check ->
                (full || check.DefaultEnabled)
                && (not offline || check.SupportsOffline))

        if Array.isEmpty requestedTokens then
            Ok(profileEntries)
        else
            let selected = ResizeArray<LocalOutputDto.DoctorCheckDto>()
            let unknown = ResizeArray<string>()

            for token in requestedTokens do
                let matches =
                    catalog
                    |> Array.filter (fun check ->
                        check.Id.Equals(token, StringComparison.OrdinalIgnoreCase)
                        || check.Category.Equals(token, StringComparison.OrdinalIgnoreCase))

                if Array.isEmpty matches then
                    unknown.Add(token)
                else
                    for check in matches do
                        if
                            (full || check.DefaultEnabled)
                            && (not offline || check.SupportsOffline)
                            && not (selected.Exists(fun existing -> existing.Id.Equals(check.Id, StringComparison.OrdinalIgnoreCase)))
                        then
                            selected.Add(check)

            if unknown.Count > 0 then
                Error(unknown |> Seq.toArray)
            else
                Ok(selected |> Seq.toArray)

    let private resultForCheck (check: LocalOutputDto.DoctorCheckDto) : LocalOutputDto.DoctorCheckResultDto =
        {
            Id = check.Id
            Category = check.Category
            Title = check.Title
            Status = "Ok"
            Severity = "Info"
            Summary = "Scaffolded check only; no diagnostic probe ran in this slice."
        }

    let private summarize (checks: LocalOutputDto.DoctorCheckResultDto array) =
        let count status =
            checks
            |> Array.filter (fun check -> check.Status.Equals(status, StringComparison.OrdinalIgnoreCase))
            |> Array.length

        {
            LocalOutputDto.DoctorSummaryDto.Total = checks.Length
            LocalOutputDto.DoctorSummaryDto.Ok = count "Ok"
            LocalOutputDto.DoctorSummaryDto.Warning = count "Warning"
            LocalOutputDto.DoctorSummaryDto.Failed = count "Failed"
            LocalOutputDto.DoctorSummaryDto.Skipped = count "Skipped"
        }

    let private reportStatus (summary: LocalOutputDto.DoctorSummaryDto) =
        if summary.Failed > 0 then "Failed"
        elif summary.Warning > 0 then "Warning"
        else "Ok"

    let diagnosticExitCode strict (report: LocalOutputDto.DoctorReportDto) =
        if report.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase) then
            1
        elif
            strict
            && report.Status.Equals("Warning", StringComparison.OrdinalIgnoreCase)
        then
            1
        else
            0

    let createReportForChecks full offline listOnly (checks: LocalOutputDto.DoctorCheckDto array) =
        let results = checks |> Array.map resultForCheck
        let summary = summarize results
        let status = reportStatus summary

        let report: LocalOutputDto.DoctorReportDto =
            {
                ReportVersion = ReportVersion
                Status = status
                ExitCode = 0
                Full = full
                Offline = offline
                Strict = false
                ListOnly = listOnly
                RequestedChecks = Array.empty
                Catalog = checks
                Checks = results
                Summary = summary
            }

        { report with ExitCode = diagnosticExitCode false report }

    let withStatus status (report: LocalOutputDto.DoctorReportDto) =
        let checks =
            if report.Checks.Length = 0 then
                report.Checks
            else
                report.Checks
                |> Array.mapi (fun index check -> if index = 0 then { check with Status = status } else check)

        let summary = summarize checks
        let normalizedStatus = reportStatus summary
        let updated = { report with Status = normalizedStatus; Checks = checks; Summary = summary }
        { updated with ExitCode = diagnosticExitCode updated.Strict updated }

    let private createReport full offline strict listOnly requestedTokens checks =
        let report = createReportForChecks full offline listOnly checks

        let report = { report with Strict = strict; RequestedChecks = requestedTokens }

        { report with ExitCode = diagnosticExitCode strict report }

    let private renderHumanReport (report: LocalOutputDto.DoctorReportDto) =
        AnsiConsole.MarkupLine("[bold]Grace doctor[/]")
        AnsiConsole.MarkupLine($"Status: {report.Status}; checks: {report.Summary.Total}; exit code: {report.ExitCode}")

        let table = Table(Border = TableBorder.Rounded)
        table.AddColumn("Check") |> ignore
        table.AddColumn("Category") |> ignore
        table.AddColumn("Status") |> ignore
        table.AddColumn("Summary") |> ignore

        for check in report.Checks do
            table.AddRow(Markup.Escape(check.Id), Markup.Escape(check.Category), Markup.Escape(check.Status), Markup.Escape(check.Summary))
            |> ignore

        AnsiConsole.Write(table)

    let private validateChecks parseResult full offline requestedTokens =
        match selectedCatalogEntries full offline requestedTokens with
        | Ok checks -> Ok checks
        | Error unknown ->
            let tokens = String.Join(", ", unknown)
            Error(GraceError.Create $"Unknown doctor check token: {tokens}." (getCorrelationId parseResult))

    type Invoke() =
        inherit SynchronousCommandLineAction()

        override _.Invoke(parseResult: ParseResult) : int =
            if parseResult |> verbose then printParseResult parseResult

            let full = parseResult.GetValue(Options.full)
            let offline = parseResult.GetValue(Options.offline)
            let strict = parseResult.GetValue(Options.strict)
            let listChecks = parseResult.GetValue(Options.listChecks)

            let requestedTokens =
                parseResult.GetValue(Options.check)
                |> tokenizeChecks

            match validateChecks parseResult full offline requestedTokens with
            | Error error -> renderOutput parseResult (Error error)
            | Ok checks ->
                let report = createReport full offline strict listChecks requestedTokens checks

                if parseResult |> json then
                    renderOutput parseResult (Ok(GraceReturnValue.Create report (getCorrelationId parseResult)))
                    |> ignore
                else
                    renderHumanReport report

                diagnosticExitCode strict report

    let Build =
        let doctorCommand =
            new Command("doctor", Description = "Inspect the local Grace environment with inert scaffolded diagnostics.")
            |> addOption Options.full
            |> addOption Options.offline
            |> addOption Options.listChecks
            |> addOption Options.check
            |> addOption Options.strict

        doctorCommand.Action <- Invoke()
        doctorCommand
