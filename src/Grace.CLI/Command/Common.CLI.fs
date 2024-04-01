namespace Grace.CLI

open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Resources.Text
open Grace.Shared.Types
open Grace.Shared.Utilities
open Spectre.Console
open System
open System.CommandLine
open System.CommandLine.Parsing
open System.Globalization
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

    type CommandLineInstruction<'a, 'b> =
        | UpdateProgress of ProgressTask * float
        | Command of (ParseResult * 'a -> Task<'b>)

    /// Adds an option (i.e. parameter) to a command, so you can do cool stuff like `|> addOption Options.someOption |> addOption Options.anotherOption`.
    let addOption (option: Option) (command: Command) =
        command.AddOption(option)
        command

    let public Language = CultureInfo.CurrentCulture.TwoLetterISOLanguageName

    /// Gets the "... ago" text.
    let ago = ago Language

    module Options =
        let correlationId =
            new Option<String>(
                [| "--correlationId"; "-c" |],
                IsRequired = false,
                Description = "CorrelationId for end-to-end tracking <String>.",
                Arity = ArgumentArity.ExactlyOne
            )

        correlationId.SetDefaultValue(generateCorrelationId ())

        let output =
            (new Option<String>([| "--output"; "-o" |], IsRequired = false, Description = "The style of output.", Arity = ArgumentArity.ExactlyOne))
                .FromAmong(listCases<OutputFormat> ())

        output.SetDefaultValue("Normal")

    /// Checks if the output format from the command line is a specific format.
    let isOutputFormat (outputFormat: OutputFormat) (parseResult: ParseResult) =
        if parseResult.HasOption(Options.output) then
            let format = parseResult.FindResultFor(Options.output).GetValueOrDefault<String>()

            String.Equals(format, getDiscriminatedUnionCaseName (outputFormat), StringComparison.CurrentCultureIgnoreCase)
        else if outputFormat = OutputFormat.Normal then
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
    let hasOutput parseResult =
        parseResult |> normal || parseResult |> verbose

    let startProgressTask showOutput (t: ProgressTask) =
        if showOutput then
            t.StartTask()

    let setProgressTaskValue showOutput (value: float) (t: ProgressTask) =
        if showOutput then
            t.Value <- value

    let incrementProgressTaskValue showOutput (value: float) (t: ProgressTask) =
        if showOutput then
            t.Increment(value)

    let emptyTask = ProgressTask(0, "Empty progress task", 0.0, autoStart = false)

    /// Gets the correlationId parameter from the command line.
    let getCorrelationId (parseResult: ParseResult) =
        parseResult.FindResultFor(Options.correlationId).GetValueOrDefault<String>()

    /// Rewrites "[" to "[[" and "]" to "]]".
    let escapeBrackets s =
        s.ToString().Replace("[", "[[").Replace("]", "]]")

    /// Prints the ParseResult with markup.
    let printParseResult (parseResult: ParseResult) =
        if not <| isNull parseResult then
            AnsiConsole.MarkupLine($"[{Colors.Verbose}]{escapeBrackets parseResult}[/]")
            AnsiConsole.WriteLine()

    /// Prints AnsiConsole markup to the console.
    let writeMarkup (markup: IRenderable) =
        AnsiConsole.Write(markup)
        AnsiConsole.WriteLine()

    /// Prints output to the console, depending on the output format.
    let renderOutput (parseResult: ParseResult) (result: GraceResult<'T>) =
        let outputFormat =
            discriminatedUnionFromString<OutputFormat>(parseResult.FindResultFor(Options.output).GetValueOrDefault<String>())
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
                    Regex.Unescape(error.Error)
                else
                    Regex.Unescape(serialize error)

            let errorText =
                if error.Error.Contains("Stack trace") then
                    try
                        let exceptionResponse = deserialize<ExceptionResponse> error.Error
                        Regex.Unescape($"{exceptionResponse}")
                    with ex ->
                        Regex.Unescape(error.Error)
                else
                    Regex.Unescape(error.Error)

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

    let progress =
        AnsiConsole.Progress(AutoRefresh = true, AutoClear = false, HideCompleted = false)
