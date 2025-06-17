namespace Grace.CLI

open Grace.CLI.Services
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Resources.Text
open Grace.Types.Types
open Grace.Shared.Utilities
open Spectre.Console
open System
open System.CommandLine
open System.CommandLine.Parsing
open System.Globalization
open System.Linq
open System.Text.Json
open System.Threading.Tasks
open Spectre.Console.Rendering
open Spectre.Console.Json
open System.Text.RegularExpressions

module Common =

    type ParameterBase() =
        member val public CorrelationId: string = String.Empty with get, set
        member val public Json: bool = false with get, set
        member val public OutputFormat: string = String.Empty with get, set

    /// The output format for the command.
    type OutputFormat =
        | Normal
        | Json
        | Minimal
        | Silent
        | Verbose

    /// Adds an option (i.e. parameter) to a command, so you can do cool stuff like `|> addOption Options.someOption |> addOption Options.anotherOption`.
    let addOption (option: Option) (command: Command) =
        command.Options.Add(option)
        command

    let public Language = CultureInfo.CurrentCulture.TwoLetterISOLanguageName

    /// Gets the "... ago" text.
    let ago = ago Language

    module Options =
        let correlationId =
            new Option<String>(
                "--correlationId",
                [| "-c" |],
                Required = false,
                Description = "CorrelationId for end-to-end tracking <String>.",
                Arity = ArgumentArity.ExactlyOne,
                Recursive = true,
                DefaultValueFactory = (fun _ -> generateCorrelationId ())
            )

        let output =
            (new Option<String>(
                "--output",
                [| "-o" |],
                Required = false,
                Description = "The style of output.",
                Arity = ArgumentArity.ExactlyOne,
                Recursive = true,
                DefaultValueFactory = (fun _ -> "Normal")
            ))

        output.AcceptOnlyFromAmong(listCases<OutputFormat> ())

    /// Checks if the output format from the command line is a specific format.
    let isOutputFormat (outputFormat: OutputFormat) (parseResult: ParseResult) =
        let outputOption = parseResult.CommandResult.GetResult(Options.output)

        match outputOption with
        | null ->
            // The command didn't have an output option set, which means it defaults to Normal.
            if outputFormat = OutputFormat.Normal then true else false
        | _ ->
            // The command had an output option set, so we check if it matches the expected output format.
            let formatFromCommand = parseResult.GetValue<string>(Options.output)

            if outputFormat = discriminatedUnionFromString<OutputFormat>(formatFromCommand).Value then
                true
            else
                false

    /// Checks if the output format from the command line is Json.
    let json parseResult = parseResult |> isOutputFormat Json

    /// Checks if the output format from the command line is Minimal.
    let minimal parseResult = parseResult |> isOutputFormat Minimal

    /// Checks if the output format from the command line is Normal.
    let normal parseResult = parseResult |> isOutputFormat Normal

    /// Checks if the output format from the command line is Silent.
    let silent parseResult = parseResult |> isOutputFormat Silent

    /// Checks if the output format from the command line is Verbose.
    let verbose parseResult = parseResult |> isOutputFormat Verbose

    /// Checks if the output format from the command line is either Normal or Verbose; i.e. it has output.
    let hasOutput parseResult = parseResult |> normal || parseResult |> verbose

    let startProgressTask showOutput (t: ProgressTask) = if showOutput then t.StartTask()

    let setProgressTaskValue showOutput (value: float) (t: ProgressTask) = if showOutput then t.Value <- value

    let incrementProgressTaskValue showOutput (value: float) (t: ProgressTask) = if showOutput then t.Increment(value)

    let emptyTask = ProgressTask(0, "Empty progress task", 0.0, autoStart = false)

    /// Gets the correlationId parameter from the command line.
    let getCorrelationId (parseResult: ParseResult) = parseResult.GetValue(Options.correlationId)

    /// Rewrites "[" to "[[" and "]" to "]]".
    let escapeBrackets s = s.ToString().Replace("[", "[[").Replace("]", "]]")

    /// Prints the ParseResult with markup.
    let printParseResult (parseResult: ParseResult) =
        if not <| isNull parseResult then
            AnsiConsole.MarkupLine($"[{Colors.Verbose}]{escapeBrackets (parseResult.ToString())}[/]")
            AnsiConsole.WriteLine()

    /// Prints AnsiConsole markup to the console.
    let writeMarkup (markup: IRenderable) =
        AnsiConsole.Write(markup)
        AnsiConsole.WriteLine()

    /// Prints output to the console, depending on the output format.
    let renderOutput (parseResult: ParseResult) (result: GraceResult<'T>) =
        let outputFormat =
            discriminatedUnionFromString<OutputFormat>(parseResult.GetValue(Options.output))
                .Value

        match result with
        | Ok graceReturnValue ->
            match outputFormat with
            | Json -> AnsiConsole.WriteLine(Markup.Escape($"{graceReturnValue}"))
            | Minimal -> () //AnsiConsole.MarkupLine($"""[{Colors.Highlighted}]{Markup.Escape($"{graceReturnValue.ReturnValue}")}[/]""")
            | Silent -> ()
            | Verbose ->
                AnsiConsole.WriteLine()

                AnsiConsole.MarkupLine($"""[{Colors.Verbose}]EventTime: {formatInstantExtended graceReturnValue.EventTime}[/]""")

                AnsiConsole.MarkupLine($"""[{Colors.Verbose}]CorrelationId: "{graceReturnValue.CorrelationId}"[/]""")

                AnsiConsole.MarkupLine($"""[{Colors.Verbose}]Properties: {Markup.Escape(serialize graceReturnValue.Properties)}[/]""")

                AnsiConsole.WriteLine()
            | Normal -> () // Return unit because in the Normal case, we expect to print output within each command.

            0
        | Error error ->
            let json =
                if error.Error.Contains("Stack trace") then
                    Uri.UnescapeDataString(error.Error)
                else
                    Uri.UnescapeDataString(serialize error)

            let errorText =
                if error.Error.Contains("Stack trace") then
                    try
                        let exceptionResponse = deserialize<ExceptionResponse> error.Error
                        Uri.UnescapeDataString($"{exceptionResponse}")
                    with ex ->
                        Uri.UnescapeDataString(error.Error)
                else
                    Uri.UnescapeDataString(error.Error)

            match outputFormat with
            | Json -> AnsiConsole.WriteLine($"{Markup.Escape(json)}")
            | Minimal -> AnsiConsole.MarkupLine($"[{Colors.Error}]{Markup.Escape(errorText)}[/]")
            | Silent -> ()
            | Verbose ->
                AnsiConsole.MarkupLine($"[{Colors.Error}]{Markup.Escape(errorText)}[/]")
                AnsiConsole.WriteLine()
                AnsiConsole.MarkupLine($"[{Colors.Verbose}]{Markup.Escape(json)}[/]")
                AnsiConsole.WriteLine()
            | Normal -> AnsiConsole.MarkupLine($"[{Colors.Error}]{Markup.Escape(errorText)}[/]")

            -1

    let progressBarColumn = new ProgressBarColumn()
    progressBarColumn.FinishedStyle <- new Style(foreground = Color.Green)

    let percentageColumn = new PercentageColumn()
    percentageColumn.Style <- new Style(foreground = Color.Yellow)
    percentageColumn.CompletedStyle <- new Style(foreground = Color.Yellow)

    let spinnerColumn = new SpinnerColumn(Spinner.Known.Dots)

    let progressColumns: ProgressColumn[] =
        [| new TaskDescriptionColumn(Alignment = Justify.Right)
           progressBarColumn
           percentageColumn
           spinnerColumn |]

    let progress = AnsiConsole.Progress(AutoRefresh = true, AutoClear = false, HideCompleted = false)
