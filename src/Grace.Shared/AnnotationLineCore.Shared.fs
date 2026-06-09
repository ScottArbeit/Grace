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
        let firstExistingLine = max lineRange.StartLine 1
        let lastExistingLine = min lineRange.EndLine document.Lines.Length

        [|
            for lineNumber in firstExistingLine..lastExistingLine do
                let index = lineNumber - 1

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

    let private validateIncludedSourceReferences (history: AnnotationHistoryDocument array) =
        [
            for document in history do
                let sourceReference = document.SourceReference

                if String.IsNullOrWhiteSpace sourceReference.SourceReferenceId then
                    "SourceReference contains blank SourceReferenceId."

                if not (isKnownReferenceTypeName sourceReference.ReferenceType) then
                    $"SourceReference '{sourceReference.SourceReferenceId}' contains unknown ReferenceType '{sourceReference.ReferenceType}'."

            let duplicatedSourceReferenceIds =
                history
                |> Array.map (fun document -> document.SourceReference.SourceReferenceId)
                |> Array.filter (String.IsNullOrWhiteSpace >> not)
                |> Array.countBy id
                |> Array.choose (fun (sourceReferenceId, count) -> if count > 1 then Some sourceReferenceId else None)

            for sourceReferenceId in duplicatedSourceReferenceIds do
                $"SourceReferenceId '{sourceReferenceId}' appears more than once in annotation history."
        ]

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

    let private buildComponentsFromSegments lines segments =
        let sourceRows = ResizeArray<AnnotationSourceRow>()
        let boundaries = ResizeArray<AnnotationBoundary>()
        let spans = ResizeArray<AnnotationSpan>()

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

    let buildComponents requestedLineRange lines states =
        let segments = segmentStates requestedLineRange states

        buildComponentsFromSegments lines segments

    let private missingTargetLine = { BoundaryKind = "TargetLineMissing"; SourceRows = Array.empty }

    let private maxShiftedAlignmentPairScans = 16_384L

    let private lineAt lineNumber (document: VisibleTextDocument) =
        let index = lineNumber - 1

        if index >= 0 && index < document.Lines.Length then
            Some document.Lines[index]
        else
            None

    let private shiftedAlignmentScanFitsBudget oldStart oldEnd newStart newEnd =
        let oldLength = int64 (oldEnd - oldStart + 1)
        let newLength = int64 (newEnd - newStart + 1)

        oldLength > 0L
        && newLength > 0L
        && oldLength * newLength
           <= maxShiftedAlignmentPairScans

    let private findLongestCommonBlock oldStart oldEnd newStart newEnd (oldLines: string array) (newLines: string array) =
        let mutable bestOldStart = 0
        let mutable bestNewStart = 0
        let mutable bestLength = 0

        if shiftedAlignmentScanFitsBudget oldStart oldEnd newStart newEnd then
            for oldIndex in oldStart..oldEnd do
                for newIndex in newStart..newEnd do
                    let mutable length = 0

                    while oldIndex + length <= oldEnd
                          && newIndex + length <= newEnd
                          && String.Equals(oldLines[oldIndex + length], newLines[newIndex + length], StringComparison.Ordinal) do
                        length <- length + 1

                    if length > bestLength then
                        bestOldStart <- oldIndex
                        bestNewStart <- newIndex
                        bestLength <- length

        if bestLength = 0 then None else Some(bestOldStart, bestNewStart, bestLength)

    let private canAnchorBlock oldStart oldEnd newStart newEnd oldBlockStart newBlockStart blockLength =
        let leftOldLength = oldBlockStart - oldStart
        let leftNewLength = newBlockStart - newStart
        let rightOldLength = oldEnd - (oldBlockStart + blockLength - 1)
        let rightNewLength = newEnd - (newBlockStart + blockLength - 1)

        let leftIsInsertionOrDeletion = leftOldLength = 0 || leftNewLength = 0
        let rightIsInsertionOrDeletion = rightOldLength = 0 || rightNewLength = 0

        let blockStayedInPlace =
            leftOldLength = leftNewLength
            && rightOldLength = rightNewLength

        let editsStayOnOneSide =
            (leftOldLength = 0 && rightOldLength = 0)
            || (leftNewLength = 0 && rightNewLength = 0)
            || leftOldLength = leftNewLength
            || rightOldLength = rightNewLength

        blockStayedInPlace
        || (leftIsInsertionOrDeletion
            && rightIsInsertionOrDeletion
            && editsStayOnOneSide)

    let private buildLineAlignment (oldDocument: VisibleTextDocument) (newDocument: VisibleTextDocument) =
        let mapping = Array.zeroCreate<int> newDocument.Lines.Length

        let rec alignRange oldStart oldEnd newStart newEnd =
            if oldStart <= oldEnd && newStart <= newEnd then
                let mutable oldFirst = oldStart
                let mutable newFirst = newStart

                while oldFirst <= oldEnd
                      && newFirst <= newEnd
                      && String.Equals(oldDocument.Lines[oldFirst], newDocument.Lines[newFirst], StringComparison.Ordinal) do
                    mapping[newFirst] <- oldFirst + 1
                    oldFirst <- oldFirst + 1
                    newFirst <- newFirst + 1

                let mutable oldLast = oldEnd
                let mutable newLast = newEnd

                while oldFirst <= oldLast
                      && newFirst <= newLast
                      && String.Equals(oldDocument.Lines[oldLast], newDocument.Lines[newLast], StringComparison.Ordinal) do
                    mapping[newLast] <- oldLast + 1
                    oldLast <- oldLast - 1
                    newLast <- newLast - 1

                if oldFirst <= oldLast && newFirst <= newLast then
                    match findLongestCommonBlock oldFirst oldLast newFirst newLast oldDocument.Lines newDocument.Lines with
                    | Some (oldBlockStart, newBlockStart, blockLength) when
                        canAnchorBlock oldFirst oldLast newFirst newLast oldBlockStart newBlockStart blockLength
                        ->
                        for offset in 0 .. blockLength - 1 do
                            mapping[newBlockStart + offset] <- oldBlockStart + offset + 1

                        alignRange oldFirst (oldBlockStart - 1) newFirst (newBlockStart - 1)
                        alignRange (oldBlockStart + blockLength) oldLast (newBlockStart + blockLength) newLast
                    | _ -> ()

        alignRange 0 (oldDocument.Lines.Length - 1) 0 (newDocument.Lines.Length - 1)
        mapping

    let private traceLineSource lineNumber (documents: (AnnotationHistoryDocument * VisibleTextDocument) array) (lineAlignments: int array array) =
        let targetDocument = documents[documents.Length - 1] |> snd

        match lineAt lineNumber targetDocument with
        | None -> Boundary missingTargetLine
        | Some targetText ->
            let mutable sourceIndex = documents.Length - 1
            let mutable sourceLineNumber = lineNumber
            let mutable keepSearching = true

            while keepSearching && sourceIndex > 0 do
                let previousLineNumber =
                    let alignment = lineAlignments[sourceIndex - 1]
                    let alignmentIndex = sourceLineNumber - 1

                    if alignmentIndex >= 0
                       && alignmentIndex < alignment.Length
                       && alignment[alignmentIndex] > 0 then
                        Some alignment[alignmentIndex]
                    else
                        None

                match previousLineNumber with
                | Some previousLine ->
                    let previousDocument = documents[sourceIndex - 1] |> snd

                    match lineAt previousLine previousDocument with
                    | Some previousText when String.Equals(previousText, targetText, StringComparison.Ordinal) ->
                        sourceIndex <- sourceIndex - 1
                        sourceLineNumber <- previousLine
                    | _ -> keepSearching <- false
                | None -> keepSearching <- false

            let sourceDocument = documents[sourceIndex] |> fst

            Resolved { SourceReferenceId = sourceDocument.SourceReference.SourceReferenceId; Path = sourceDocument.Path; LineNumber = sourceLineNumber }

    let private traceRequestedSegments requestedLineRange (targetDocument: VisibleTextDocument) traceDocuments (lineAlignments: int array array) =
        let segments = ResizeArray<int * int * AnnotationLineState>()
        let firstExistingLine = max requestedLineRange.StartLine 1
        let lastExistingLine = min requestedLineRange.EndLine targetDocument.Lines.Length

        if requestedLineRange.StartLine < firstExistingLine then
            segments.Add(requestedLineRange.StartLine, firstExistingLine - 1, Boundary missingTargetLine)

        if firstExistingLine <= lastExistingLine then
            let mutable segmentStart = firstExistingLine
            let mutable segmentState = traceLineSource firstExistingLine traceDocuments lineAlignments
            let mutable previousLine = firstExistingLine
            let mutable previousState = segmentState

            for lineNumber in firstExistingLine + 1 .. lastExistingLine do
                let currentState = traceLineSource lineNumber traceDocuments lineAlignments

                if canAppend previousState currentState previousLine lineNumber then
                    previousLine <- lineNumber
                    previousState <- currentState
                else
                    segments.Add(segmentStart, previousLine, segmentState)
                    segmentStart <- lineNumber
                    segmentState <- currentState
                    previousLine <- lineNumber
                    previousState <- currentState

            segments.Add(segmentStart, previousLine, segmentState)

        if lastExistingLine < requestedLineRange.EndLine then
            let missingStart = max requestedLineRange.StartLine (lastExistingLine + 1)

            segments.Add(missingStart, requestedLineRange.EndLine, Boundary missingTargetLine)

        segments.ToArray()

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

        let sourceReferenceErrors = validateIncludedSourceReferences includedHistory

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
              @ maxReferenceErrors
                @ historyErrors @ sourceReferenceErrors
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

                let lineAlignments =
                    traceDocuments
                    |> Array.pairwise
                    |> Array.map (fun ((_, previousDocument), (_, currentDocument)) -> buildLineAlignment previousDocument currentDocument)

                let lines =
                    if includeLineText then
                        getRequestedLines requestedLineRange targetDocument
                    else
                        Array.empty

                let segments = traceRequestedSegments requestedLineRange targetDocument traceDocuments lineAlignments

                let components = buildComponentsFromSegments lines segments

                let usedSourceReferenceIds =
                    components.SourceRows
                    |> Array.map (fun row -> row.SourceReferenceId)
                    |> Set.ofArray

                let sourceReferences =
                    traceDocuments
                    |> Array.map (fun (document, _) -> document.SourceReference)
                    |> Array.filter (fun reference -> usedSourceReferenceIds.Contains reference.SourceReferenceId)

                if sourceReferences.Length > maxReferences then
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
