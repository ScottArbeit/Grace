namespace Grace.Cli

open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Types
open Grace.Shared.Utilities
open Spectre.Console
open System
open System.CommandLine
open System.CommandLine.Parsing
open System.Text.Json
open Spectre.Console.Rendering

module Common =

    type ParameterBase() = 
        member val public CorrelationId: string = String.Empty with get, set
        member val public Json: bool = false with get, set
        member val public OutputFormat: string = String.Empty with get, set

    type OutputFormat =
        | Normal
        | Json
        | Minimal
        | Silent
        | Verbose

    let addOption (option: Option) (command: Command) =
        command.AddOption(option)
        command

    module Options =
        let correlationId = new Option<String>([|"--correlationId"; "-c"|], IsRequired = false, Description = "CorrelationId for end-to-end tracking <String>.", Arity = ArgumentArity.ExactlyOne)
        correlationId.SetDefaultValue(Guid.NewGuid().ToString())

        let output = (new Option<String>("--output", IsRequired = false, Description = "The style of output.", Arity = ArgumentArity.ExactlyOne))
                            .FromAmong(listCases(typeof<OutputFormat>))
        output.SetDefaultValue("Normal")

    let isOutputFormat (outputFormat: OutputFormat) (parseResult: ParseResult) =
        if parseResult.HasOption(Options.output) then
            let format = parseResult.FindResultFor(Options.output).GetValueOrDefault<String>()
            format = discriminatedUnionFullNameToString(outputFormat).Replace($"{nameof(OutputFormat)}.","")
        else
            if outputFormat = OutputFormat.Normal then true else false

    let json parseResult = parseResult |> isOutputFormat Json
    let minimal parseResult = parseResult |> isOutputFormat Minimal
    let normal parseResult = parseResult |> isOutputFormat Normal
    let silent parseResult = parseResult |> isOutputFormat Silent
    let verbose parseResult = parseResult |> isOutputFormat Verbose
    let showOutput parseResult = parseResult |> normal || parseResult |> verbose

    /// Gets the correlationId parameter from the command line.
    let getCorrelationId(parseResult: ParseResult) = parseResult.FindResultFor(Options.correlationId).GetValueOrDefault<String>()

    /// Rewrites "[" to "[[" and "]" to "]]".
    let escapeBrackets s  = s.ToString().Replace("[", "[[").Replace("]", "]]")

    /// Prints the ParseResult with markup.
    let printParseResult (parseResult: ParseResult) =
        AnsiConsole.MarkupLine($"[{Colors.Verbose}]{escapeBrackets parseResult}[/]")
        AnsiConsole.WriteLine()

    let writeMarkup (markup: IRenderable) =
        AnsiConsole.Write(markup)
        AnsiConsole.WriteLine()

    let renderOutput (parseResult: ParseResult) (result: GraceResult<'T>) =
        let outputFormat = discriminatedUnionFromString<OutputFormat>(parseResult.FindResultFor(Options.output).GetValueOrDefault<String>()).Value
        match result with
        | Ok graceReturnValue -> 
            match outputFormat with
            | Json -> AnsiConsole.WriteLine(Markup.Escape($"{graceReturnValue}"))
            | Minimal -> AnsiConsole.MarkupLine($"""[{Colors.Highlighted}]{Markup.Escape($"{graceReturnValue.ReturnValue}")}[/]""")
            | Silent -> ()
            | Verbose -> AnsiConsole.MarkupLine($"""[{Colors.Verbose}]{Markup.Escape($"{graceReturnValue}")}[/]""")
                         AnsiConsole.WriteLine()
                         AnsiConsole.MarkupLine($"""[{Colors.Highlighted}]{Markup.Escape($"{graceReturnValue.ReturnValue}")}[/]""")
            | Normal -> AnsiConsole.MarkupLine($"""[{Colors.Highlighted}]{Markup.Escape($"{graceReturnValue.ReturnValue}")}[/]""")
            0
        | Error error -> 
            let json = if error.Error.Contains("Stack trace") then 
                                let exceptionResponse = JsonSerializer.Deserialize<ExceptionResponse>(error.Error, Constants.JsonSerializerOptions)
                                sprintf "%A" exceptionResponse
                            else
                                JsonSerializer.Serialize(error, Constants.JsonSerializerOptions)

            let errorText = if error.Error.Contains("Stack trace") then 
                                let exceptionResponse = JsonSerializer.Deserialize<ExceptionResponse>(error.Error, Constants.JsonSerializerOptions)
                                sprintf "%A" exceptionResponse
                            else
                                error.Error

            match outputFormat with
            | Json -> AnsiConsole.WriteLine($"{Markup.Escape(json)}")
            | Minimal -> AnsiConsole.MarkupLine($"[{Colors.Error}]{Markup.Escape(errorText)}[/]")
            | Silent -> ()
            | Verbose -> AnsiConsole.MarkupLine($"[{Colors.Verbose}]{Markup.Escape(json)}[/]")
                         AnsiConsole.WriteLine()
                         AnsiConsole.MarkupLine($"[{Colors.Error}]{Markup.Escape(errorText)}[/]")
            | Normal -> AnsiConsole.MarkupLine($"[{Colors.Error}]{Markup.Escape(errorText)}[/]")
            -1

    let progressBarColumn = new ProgressBarColumn()
    progressBarColumn.FinishedStyle <- new Style(foreground = Color.Green)

    let percentageColumn = new PercentageColumn()
    percentageColumn.Style <- new Style(foreground = Color.Yellow)
    percentageColumn.CompletedStyle <- new Style(foreground = Color.Yellow)

    let spinnerColumn = new SpinnerColumn(Spinner.Known.Dots)

    let progressColumns: ProgressColumn[] = [| new TaskDescriptionColumn(Alignment = Justify.Right); progressBarColumn; percentageColumn; spinnerColumn; |]

    let progress = AnsiConsole.Progress(AutoRefresh = true, AutoClear = false, HideCompleted = false)
