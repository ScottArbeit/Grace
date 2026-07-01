namespace Grace.CLI

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Common
open System
open System.Collections.Generic
open System.Text.Json
open System.Text.RegularExpressions

/// Groups the select projection command parser, handlers, and output helpers.
module SelectProjection =

    /// Models selector path values passed between the parser and select projection handlers.
    type SelectorPath = private { Segments: string list }

    let private identifier = Regex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)

    let private forbiddenSegments =
        HashSet<string>(
            [
                "CorrelationId"
                "EventTime"
                "Error"
                "Properties"
                "ReturnValue"
            ],
            StringComparer.OrdinalIgnoreCase
        )

    /// Formats path values into the text shown in Spectre.Console tables or command output.
    let private formatPath (segments: string list) = String.Join(".", segments)

    /// Projects selected fields from command responses for table, text, or JSON output.
    let private error correlationId message = Error(GraceError.Create message correlationId)

    /// Tries to map parse and returns a GraceError instead of throwing on unsupported input.
    let tryParse correlationId (selector: string) =
        if String.IsNullOrWhiteSpace selector then
            error correlationId "--select requires a non-empty ReturnValue property path."
        else
            let trimmed = selector.Trim()

            if trimmed <> selector then
                error correlationId "--select paths cannot include leading or trailing whitespace."
            elif
                trimmed.IndexOfAny
                    (
                        [|
                            '['
                            ']'
                            '('
                            ')'
                            '*'
                            '''
                            '"'
                            '$'
                            '@'
                            '?'
                            ':'
                            ','
                            ' '
                            '\t'
                            '\r'
                            '\n'
                        |]
                    )
                >= 0
            then
                error
                    correlationId
                    "--select supports only dot-separated ReturnValue property names; predicates, wildcards, functions, and expressions are not supported."
            else
                let segments =
                    trimmed.Split('.', StringSplitOptions.None)
                    |> Array.toList

                match segments with
                | [] -> error correlationId "--select requires a ReturnValue property path."
                | _ when segments |> List.exists String.IsNullOrWhiteSpace -> error correlationId "--select paths must not contain empty property segments."
                | _ ->
                    match
                        segments
                        |> List.tryFind (identifier.IsMatch >> not)
                        with
                    | Some segment -> error correlationId $"--select property segment '{segment}' is not valid."
                    | None ->
                        match segments
                              |> List.tryFind forbiddenSegments.Contains
                            with
                        | Some segment ->
                            error correlationId $"--select cannot project '{segment}'. Selectors are relative to ReturnValue and cannot read envelope metadata."
                        | None -> Ok { Segments = segments }

    /// Projects selected fields from command responses for table, text, or JSON output.
    let project correlationId (selector: SelectorPath) (returnValue: obj) =
        let json = serialize returnValue
        use document = JsonDocument.Parse(json)

        /// Projects selected fields from command responses for table, text, or JSON output.
        let rec loop (current: JsonElement) remaining =
            match remaining with
            | [] -> Ok(current.Clone())
            | (segment: string) :: rest ->
                if current.ValueKind <> JsonValueKind.Object then
                    error
                        correlationId
                        $"--select path '{formatPath selector.Segments}' cannot read '{segment}' because the current ReturnValue value is {current.ValueKind}."
                else
                    let mutable value = Unchecked.defaultof<JsonElement>

                    if current.TryGetProperty(segment, &value) then
                        loop value rest
                    else
                        error correlationId $"--select path '{formatPath selector.Segments}' was not found in ReturnValue."

        loop document.RootElement selector.Segments

    /// Renders selected json results only when the selected output mode includes human-readable console text.
    let renderSelectedJson (selected: JsonElement) = selected.GetRawText()
