namespace Grace.Shared

open Grace.Shared.Utilities
open Grace.Types.Annotation
open Grace.Types.Common
open System
open System.Text

module AnnotationLineCore =

    type AnnotationTextError = InvalidUtf8 of Message: string

    type VisibleTextDocument = { Text: string; Lines: string array }

    type AnnotationHistoryDocument = { SourceReference: AnnotationSourceReference; Path: RelativePath; Content: byte array }

    type AnnotationLineSource = { SourceReferenceId: AnnotationSourceReferenceId; Path: RelativePath; LineNumber: int }

    type AnnotationBoundaryState = { BoundaryKind: string; SourceRows: AnnotationLineSource array }

    type AnnotationLineState =
        | Resolved of AnnotationLineSource
        | Boundary of AnnotationBoundaryState

    type AnnotationLineCoreResult =
        {
            Lines: AnnotationLine array
            Boundaries: AnnotationBoundary array
            Spans: AnnotationSpan array
            SourceRows: AnnotationSourceRow array
        }

    let private strictUtf8 = UTF8Encoding(false, true)

    let private hasUtf8Bom (bytes: byte array) =
        bytes.Length >= 3
        && bytes[0] = 0xEFuy
        && bytes[1] = 0xBBuy
        && bytes[2] = 0xBFuy

    let private normalizeLineEndings (text: string) = text.Replace("\r\n", "\n").Replace('\r', '\n')

    let decodeVisibleText (content: byte array) =
        try
            let decoded = strictUtf8.GetString(content)

            let withoutBom =
                if hasUtf8Bom content
                   && decoded.Length > 0
                   && decoded[0] = '\uFEFF' then
                    decoded.Substring(1)
                else
                    decoded

            let normalized = normalizeLineEndings withoutBom

            let lines =
                if normalized.Length = 0 then
                    Array.empty
                else
                    let split = normalized.Split('\n')

                    if normalized.EndsWith("\n", StringComparison.Ordinal) then
                        split[.. split.Length - 2]
                    else
                        split

            Ok { Text = normalized; Lines = lines }
        with
        | :? DecoderFallbackException as ex -> Error(InvalidUtf8 ex.Message)

    let getRequestedLines (lineRange: AnnotationLineRange) (document: VisibleTextDocument) =
        [|
            for lineNumber in lineRange.StartLine .. lineRange.EndLine do
                let index = lineNumber - 1

                if index >= 0 && index < document.Lines.Length then
                    { LineNumber = lineNumber; Text = document.Lines[index] }
        |]

    let private sourceKey (source: AnnotationLineSource) = source.SourceReferenceId, source.Path

    let private boundaryKey (boundary: AnnotationBoundaryState) =
        boundary.BoundaryKind,
        boundary.SourceRows
        |> Array.map (fun source -> source.SourceReferenceId, source.Path, source.LineNumber)
        |> Array.toList

    let private canAppend previousState currentState previousTargetLine currentTargetLine =
        currentTargetLine = previousTargetLine + 1
        && match previousState, currentState with
           | Resolved previous, Resolved current ->
               sourceKey previous = sourceKey current
               && current.LineNumber = previous.LineNumber + 1
           | Boundary previous, Boundary current -> boundaryKey previous = boundaryKey current
           | _ -> false

    let private lineRange startLine endLine = { StartLine = startLine; EndLine = endLine }

    let private sourceRowId index = $"source-row-{index}"

    let private spanId index = $"span-{index}"

    let private boundaryId index = $"boundary-{index}"

    let private referenceTypeName (referenceType: ReferenceType) = getDiscriminatedUnionCaseName referenceType

    let private knownReferenceTypeNames = listCases<ReferenceType> () |> Set.ofArray

    let private isKnownReferenceTypeName referenceTypeName = knownReferenceTypeNames.Contains referenceTypeName

    let private matchesReferenceTypeFilter (referenceTypeNames: Set<string>) (document: AnnotationHistoryDocument) =
        referenceTypeNames.Count = 0
        || (isKnownReferenceTypeName document.SourceReference.ReferenceType
            && referenceTypeNames.Contains document.SourceReference.ReferenceType)

    let private appendSourceRow (rows: ResizeArray<AnnotationSourceRow>) path sourceReferenceId sourceRange =
        let rowId = sourceRowId (rows.Count + 1)

        rows.Add({ SourceRowId = rowId; SourceReferenceId = sourceReferenceId; Path = path; LineRange = sourceRange })

        rowId

    let private appendSourceRowsForBoundary rows (boundary: AnnotationBoundaryState) =
        boundary.SourceRows
        |> Array.map (fun source -> appendSourceRow rows source.Path source.SourceReferenceId (lineRange source.LineNumber source.LineNumber))

    let private segmentStates requestedLineRange states =
        let expectedLineCount =
            requestedLineRange.EndLine
            - requestedLineRange.StartLine
            + 1

        if expectedLineCount <> Array.length states then
            invalidArg (nameof states) "States must cover every requested line exactly once."

        if expectedLineCount = 0 then
            Array.empty
        else
            let segments = ResizeArray<int * int * AnnotationLineState>()
            let mutable segmentStart = requestedLineRange.StartLine
            let mutable segmentState = states[0]
            let mutable previousLine = requestedLineRange.StartLine
            let mutable previousState = states[0]

            for offset in 1 .. states.Length - 1 do
                let currentLine = requestedLineRange.StartLine + offset
                let currentState = states[offset]

                if canAppend previousState currentState previousLine currentLine then
                    previousLine <- currentLine
                    previousState <- currentState
                else
                    segments.Add(segmentStart, previousLine, segmentState)
                    segmentStart <- currentLine
                    segmentState <- currentState
                    previousLine <- currentLine
                    previousState <- currentState

            segments.Add(segmentStart, previousLine, segmentState)
            segments.ToArray()

    let buildComponents requestedLineRange lines states =
        let sourceRows = ResizeArray<AnnotationSourceRow>()
        let boundaries = ResizeArray<AnnotationBoundary>()
        let spans = ResizeArray<AnnotationSpan>()
        let segments = segmentStates requestedLineRange states

        for segmentStart, segmentEnd, state in segments do
            match state with
            | Resolved source ->
                let rowId =
                    appendSourceRow
                        sourceRows
                        source.Path
                        source.SourceReferenceId
                        (lineRange source.LineNumber (source.LineNumber + (segmentEnd - segmentStart)))

                spans.Add(
                    { SpanId = spanId (spans.Count + 1); BoundaryId = String.Empty; LineRange = lineRange segmentStart segmentEnd; SourceRowIds = [| rowId |] }
                )
            | Boundary boundary ->
                let rowIds = appendSourceRowsForBoundary sourceRows boundary
                let id = boundaryId (boundaries.Count + 1)

                boundaries.Add({ BoundaryId = id; LineRange = lineRange segmentStart segmentEnd; SourceRowIds = rowIds })

                spans.Add({ SpanId = spanId (spans.Count + 1); BoundaryId = id; LineRange = lineRange segmentStart segmentEnd; SourceRowIds = rowIds })

        { Lines = lines; Boundaries = boundaries.ToArray(); Spans = spans.ToArray(); SourceRows = sourceRows.ToArray() }

    let private missingTargetLine = { BoundaryKind = "TargetLineMissing"; SourceRows = Array.empty }

    let private lineAt lineNumber (document: VisibleTextDocument) =
        let index = lineNumber - 1

        if index >= 0 && index < document.Lines.Length then
            Some document.Lines[index]
        else
            None

    let private traceLineSource lineNumber (documents: (AnnotationHistoryDocument * VisibleTextDocument) array) =
        let targetDocument = documents[documents.Length - 1] |> snd

        match lineAt lineNumber targetDocument with
        | None -> Boundary missingTargetLine
        | Some targetText ->
            let mutable sourceIndex = documents.Length - 1
            let mutable keepSearching = true

            while keepSearching && sourceIndex > 0 do
                let previousDocument = documents[sourceIndex - 1] |> snd

                match lineAt lineNumber previousDocument with
                | Some previousText when String.Equals(previousText, targetText, StringComparison.Ordinal) -> sourceIndex <- sourceIndex - 1
                | _ -> keepSearching <- false

            let sourceDocument = documents[sourceIndex] |> fst

            Resolved { SourceReferenceId = sourceDocument.SourceReference.SourceReferenceId; Path = sourceDocument.Path; LineNumber = lineNumber }

    let buildAnnotation
        (
            requestedLineRange: AnnotationLineRange,
            targetReferenceId: ReferenceId,
            path: RelativePath,
            referenceTypeFilter: ReferenceType array,
            maxReferences: int,
            includeLineText: bool,
            history: AnnotationHistoryDocument array
        )
        =
        let lineRangeErrors =
            match validateLineRange requestedLineRange with
            | Ok () -> []
            | Error errors -> errors

        let maxReferenceErrors =
            match validateMaxReferences maxReferences with
            | Ok () -> []
            | Error errors -> errors

        let referenceTypeNames =
            referenceTypeFilter
            |> Array.map referenceTypeName
            |> Set.ofArray

        let includedHistory =
            history
            |> Array.filter (matchesReferenceTypeFilter referenceTypeNames)

        let historyErrors =
            [
                if history.Length = 0 then
                    "Annotation history must contain at least the target document."

                for document in includedHistory do
                    if not (String.Equals(document.Path, path, StringComparison.Ordinal)) then
                        $"Annotation history path '{document.Path}' must match annotation path '{path}'."

                if history.Length > 0
                   && history[history.Length - 1]
                       .SourceReference
                       .ReferenceId
                      <> targetReferenceId then
                    $"Target SourceReference '{history[history.Length - 1]
                                                   .SourceReference
                                                   .SourceReferenceId}' must match TargetReferenceId."

                if
                    history.Length > 0
                    && not (matchesReferenceTypeFilter referenceTypeNames history[history.Length - 1])
                then
                    $"Target SourceReference '{history[history.Length - 1]
                                                   .SourceReference
                                                   .SourceReferenceId}' must match ReferenceTypeFilter."
            ]

        match lineRangeErrors
              @ maxReferenceErrors @ historyErrors
            with
        | _ :: _ as errors -> Error errors
        | [] ->
            let decoded =
                includedHistory
                |> Array.map (fun document ->
                    match decodeVisibleText document.Content with
                    | Ok visible -> Ok(document, visible)
                    | Error (InvalidUtf8 message) -> Error $"SourceReference '{document.SourceReference.SourceReferenceId}' contains invalid UTF-8: {message}")

            let errors =
                decoded
                |> Array.choose (function
                    | Ok _ -> None
                    | Error error -> Some error)
                |> Array.toList

            match errors with
            | _ :: _ -> Error errors
            | [] ->
                let documents =
                    decoded
                    |> Array.choose (function
                        | Ok document -> Some document
                        | Error _ -> None)

                let traceDocuments = documents

                let targetDocument = documents[documents.Length - 1] |> snd

                let lines =
                    if includeLineText then
                        getRequestedLines requestedLineRange targetDocument
                    else
                        Array.empty

                let states =
                    [|
                        for lineNumber in requestedLineRange.StartLine .. requestedLineRange.EndLine do
                            traceLineSource lineNumber traceDocuments
                    |]

                let components = buildComponents requestedLineRange lines states

                let usedSourceReferenceIds =
                    components.SourceRows
                    |> Array.map (fun row -> row.SourceReferenceId)
                    |> Set.ofArray

                let sourceReferences =
                    traceDocuments
                    |> Array.map (fun (document, _) -> document.SourceReference)
                    |> Array.filter (fun reference -> usedSourceReferenceIds.Contains reference.SourceReferenceId)

                let sourceReferenceErrors =
                    [
                        for sourceReference in sourceReferences do
                            if not (isKnownReferenceTypeName sourceReference.ReferenceType) then
                                $"SourceReference '{sourceReference.SourceReferenceId}' contains unknown ReferenceType '{sourceReference.ReferenceType}'."
                    ]

                if not sourceReferenceErrors.IsEmpty then
                    Error sourceReferenceErrors
                elif sourceReferences.Length > maxReferences then
                    Error [ $"SourceReferences must contain no more than MaxReferences ({maxReferences}) entries." ]
                else
                    Ok(
                        BranchAnnotationDto.Create(
                            requestedLineRange,
                            targetReferenceId,
                            path,
                            referenceTypeFilter,
                            maxReferences,
                            includeLineText,
                            lines,
                            components.Boundaries,
                            components.Spans,
                            components.SourceRows,
                            sourceReferences
                        )
                    )
