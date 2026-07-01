namespace Grace.Types

open Grace.Types.Common
open Grace.Shared.Utilities
open global.MessagePack
open global.NodaTime
open Orleans
open System

/// Contains annotation helpers.
module Annotation =

    [<Literal>]
    let DefaultMaxReferences = 1000

    [<Literal>]
    let MaximumMaxReferences = 5000

    /// Represents annotation boundary id.
    type AnnotationBoundaryId = string

    /// Represents annotation span id.
    type AnnotationSpanId = string

    /// Represents annotation source reference id.
    type AnnotationSourceReferenceId = string

    /// Represents annotation source row id.
    type AnnotationSourceRowId = string

    /// Represents reference type name.
    type ReferenceTypeName = string

    /// Names the public reference type carried in annotation filters and source rows.
    let private referenceTypeName (referenceType: ReferenceType) = getDiscriminatedUnionCaseName referenceType

    let private referenceTypeNames = listCases<ReferenceType> () |> Set.ofArray

    /// Represents annotation line range.
    [<MessagePackObject; GenerateSerializer>]
    type AnnotationLineRange =
        {
            [<Key(0)>]
            StartLine: int
            [<Key(1)>]
            EndLine: int
        }

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default = { StartLine = 1; EndLine = 1 }

    /// Represents annotation line.
    [<MessagePackObject; GenerateSerializer>]
    type AnnotationLine =
        {
            [<Key(0)>]
            LineNumber: int
            [<Key(1)>]
            Text: string
        }

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default = { LineNumber = 1; Text = String.Empty }

    /// Represents annotation source reference.
    [<MessagePackObject; GenerateSerializer>]
    type AnnotationSourceReference =
        {
            [<Key(0)>]
            SourceReferenceId: AnnotationSourceReferenceId
            [<Key(1)>]
            ReferenceId: ReferenceId
            [<Key(2)>]
            ReferenceType: ReferenceTypeName
            [<Key(3)>]
            ReferenceText: ReferenceText
            [<Key(4)>]
            DirectoryVersionId: DirectoryVersionId
            [<Key(5)>]
            CreatedAt: Instant option
            [<Key(6)>]
            CreatedBy: string option
        }

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default =
            {
                SourceReferenceId = String.Empty
                ReferenceId = ReferenceId.Empty
                ReferenceType = referenceTypeName ReferenceType.Commit
                ReferenceText = String.Empty
                DirectoryVersionId = DirectoryVersionId.Empty
                CreatedAt = None
                CreatedBy = None
            }

    /// Represents annotation source row.
    [<MessagePackObject; GenerateSerializer>]
    type AnnotationSourceRow =
        {
            [<Key(0)>]
            SourceRowId: AnnotationSourceRowId
            [<Key(1)>]
            SourceReferenceId: AnnotationSourceReferenceId
            [<Key(2)>]
            Path: RelativePath
            [<Key(3)>]
            LineRange: AnnotationLineRange
        }

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default = { SourceRowId = String.Empty; SourceReferenceId = String.Empty; Path = String.Empty; LineRange = AnnotationLineRange.Default }

    /// Represents annotation boundary.
    [<MessagePackObject; GenerateSerializer>]
    type AnnotationBoundary =
        {
            [<Key(0)>]
            BoundaryId: AnnotationBoundaryId
            [<Key(1)>]
            LineRange: AnnotationLineRange
            [<Key(2)>]
            SourceRowIds: AnnotationSourceRowId array
            [<Key(3)>]
            BoundaryKind: string
        }

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default = { BoundaryId = String.Empty; LineRange = AnnotationLineRange.Default; SourceRowIds = Array.empty; BoundaryKind = String.Empty }

    /// Represents annotation span.
    [<MessagePackObject; GenerateSerializer>]
    type AnnotationSpan =
        {
            [<Key(0)>]
            SpanId: AnnotationSpanId
            [<Key(1)>]
            BoundaryId: AnnotationBoundaryId
            [<Key(2)>]
            LineRange: AnnotationLineRange
            [<Key(3)>]
            SourceRowIds: AnnotationSourceRowId array
        }

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default = { SpanId = String.Empty; BoundaryId = String.Empty; LineRange = AnnotationLineRange.Default; SourceRowIds = Array.empty }

    /// Represents branch annotation dto.
    [<MessagePackObject; GenerateSerializer>]
    type BranchAnnotationDto =
        {
            [<Key(0)>]
            Class: string
            [<Key(1)>]
            RequestedLineRange: AnnotationLineRange
            [<Key(2)>]
            TargetReferenceId: ReferenceId
            [<Key(3)>]
            Path: RelativePath
            [<Key(4)>]
            ReferenceTypeFilter: ReferenceTypeName array
            [<Key(5)>]
            MaxReferences: int
            [<Key(6)>]
            IncludeLineText: bool
            [<Key(7)>]
            Lines: AnnotationLine array
            [<Key(8)>]
            Boundaries: AnnotationBoundary array
            [<Key(9)>]
            Spans: AnnotationSpan array
            [<Key(10)>]
            SourceRows: AnnotationSourceRow array
            [<Key(11)>]
            SourceReferences: AnnotationSourceReference array
        }

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default =
            {
                Class = nameof BranchAnnotationDto
                RequestedLineRange = AnnotationLineRange.Default
                TargetReferenceId = ReferenceId.Empty
                Path = String.Empty
                ReferenceTypeFilter = Array.empty
                MaxReferences = DefaultMaxReferences
                IncludeLineText = false
                Lines = Array.empty
                Boundaries = Array.empty
                Spans = Array.empty
                SourceRows = Array.empty
                SourceReferences = Array.empty
            }

        /// Builds the contract value from required caller inputs and generated defaults used by this surface.
        static member Create
            (
                requestedLineRange: AnnotationLineRange,
                targetReferenceId: ReferenceId,
                path: RelativePath,
                referenceTypeFilter: ReferenceType array,
                maxReferences: int,
                includeLineText: bool,
                lines: AnnotationLine array,
                boundaries: AnnotationBoundary array,
                spans: AnnotationSpan array,
                sourceRows: AnnotationSourceRow array,
                sourceReferences: AnnotationSourceReference array
            ) =
            { BranchAnnotationDto.Default with
                RequestedLineRange = requestedLineRange
                TargetReferenceId = targetReferenceId
                Path = path
                ReferenceTypeFilter = referenceTypeFilter |> Array.map referenceTypeName
                MaxReferences = maxReferences
                IncludeLineText = includeLineText
                Lines = if includeLineText then lines else Array.empty
                Boundaries = boundaries
                Spans = spans
                SourceRows = sourceRows
                SourceReferences = sourceReferences
            }

    /// Appends a validation message only when the supplied condition is true.
    let private appendIf condition error errors = if condition then error :: errors else errors

    /// Validates line range.
    let validateLineRange (lineRange: AnnotationLineRange) =
        []
        |> appendIf (lineRange.StartLine < 1) "StartLine must be greater than or equal to 1."
        |> appendIf (lineRange.EndLine < 1) "EndLine must be greater than or equal to 1."
        |> appendIf (lineRange.EndLine < lineRange.StartLine) "EndLine must be greater than or equal to StartLine."
        |> function
            | [] -> Ok()
            | errors -> Error(List.rev errors)

    /// Validates max references.
    let validateMaxReferences maxReferences =
        []
        |> appendIf (maxReferences < 1) "MaxReferences must be greater than or equal to 1."
        |> appendIf (maxReferences > MaximumMaxReferences) $"MaxReferences must be less than or equal to {MaximumMaxReferences}."
        |> function
            | [] -> Ok()
            | errors -> Error(List.rev errors)

    /// Checks whether a reference type name is outside the known annotation contract.
    let private isUnknownReferenceTypeName referenceTypeName = not (referenceTypeNames.Contains referenceTypeName)

    /// Detects duplicate non-empty values in validation input.
    let private hasDuplicates values =
        values
        |> Seq.filter (String.IsNullOrWhiteSpace >> not)
        |> Seq.countBy id
        |> Seq.exists (fun (_, count) -> count > 1)

    /// Detects blank values in validation input.
    let private hasBlanks values = values |> Seq.exists String.IsNullOrWhiteSpace

    /// Collects validation messages for invalid annotation line ranges.
    let private collectRangeErrors label lineRange =
        match validateLineRange lineRange with
        | Ok () -> []
        | Error errors ->
            errors
            |> List.map (fun error -> $"{label}: {error}")

    /// Checks whether one annotation range fully contains another.
    let private containsLineRange (outer: AnnotationLineRange) (inner: AnnotationLineRange) =
        inner.StartLine >= outer.StartLine
        && inner.EndLine <= outer.EndLine

    /// Validates link integrity.
    let validateLinkIntegrity (annotation: BranchAnnotationDto) =
        let sourceReferenceIds =
            annotation.SourceReferences
            |> Seq.map (fun sourceReference -> sourceReference.SourceReferenceId)
            |> Set.ofSeq

        let sourceRowIds =
            annotation.SourceRows
            |> Seq.map (fun sourceRow -> sourceRow.SourceRowId)
            |> Set.ofSeq

        let boundaryIds =
            annotation.Boundaries
            |> Seq.map (fun boundary -> boundary.BoundaryId)
            |> Set.ofSeq

        let sourceRowReferenceErrors =
            annotation.SourceRows
            |> Seq.filter (fun sourceRow -> not (sourceReferenceIds.Contains sourceRow.SourceReferenceId))
            |> Seq.map (fun sourceRow -> $"SourceRow '{sourceRow.SourceRowId}' references missing SourceReference '{sourceRow.SourceReferenceId}'.")
            |> Seq.toList

        let boundarySourceRowErrors =
            annotation.Boundaries
            |> Seq.collect (fun boundary ->
                boundary.SourceRowIds
                |> Seq.filter (fun sourceRowId -> not (sourceRowIds.Contains sourceRowId))
                |> Seq.map (fun sourceRowId -> $"Boundary '{boundary.BoundaryId}' references missing SourceRow '{sourceRowId}'."))
            |> Seq.toList

        let spanBoundaryErrors =
            annotation.Spans
            |> Seq.filter (fun span ->
                not (String.IsNullOrWhiteSpace span.BoundaryId)
                && not (boundaryIds.Contains span.BoundaryId))
            |> Seq.map (fun span -> $"Span '{span.SpanId}' references missing Boundary '{span.BoundaryId}'.")
            |> Seq.toList

        let spanSourceRowErrors =
            annotation.Spans
            |> Seq.collect (fun span ->
                span.SourceRowIds
                |> Seq.filter (fun sourceRowId -> not (sourceRowIds.Contains sourceRowId))
                |> Seq.map (fun sourceRowId -> $"Span '{span.SpanId}' references missing SourceRow '{sourceRowId}'."))
            |> Seq.toList

        let resolvedSpanSourceRowErrors =
            annotation.Spans
            |> Seq.filter (fun span ->
                String.IsNullOrWhiteSpace span.BoundaryId
                && span.SourceRowIds.Length = 0)
            |> Seq.map (fun span -> $"Span '{span.SpanId}' without a BoundaryId must reference at least one SourceRow.")
            |> Seq.toList

        let duplicateErrors =
            []
            |> appendIf
                (hasDuplicates (
                    annotation.SourceReferences
                    |> Seq.map (fun sourceReference -> sourceReference.SourceReferenceId)
                ))
                "SourceReferences must not contain duplicate SourceReferenceId values."
            |> appendIf
                (hasDuplicates (
                    annotation.SourceRows
                    |> Seq.map (fun sourceRow -> sourceRow.SourceRowId)
                ))
                "SourceRows must not contain duplicate SourceRowId values."
            |> appendIf
                (hasDuplicates (
                    annotation.Boundaries
                    |> Seq.map (fun boundary -> boundary.BoundaryId)
                ))
                "Boundaries must not contain duplicate BoundaryId values."
            |> appendIf
                (hasDuplicates (
                    annotation.Spans
                    |> Seq.map (fun span -> span.SpanId)
                ))
                "Spans must not contain duplicate SpanId values."
            |> List.rev

        let blankIdentifierErrors =
            []
            |> appendIf
                (hasBlanks (
                    annotation.SourceReferences
                    |> Seq.map (fun sourceReference -> sourceReference.SourceReferenceId)
                ))
                "SourceReferences must not contain blank SourceReferenceId values."
            |> appendIf
                (hasBlanks (
                    annotation.SourceRows
                    |> Seq.map (fun sourceRow -> sourceRow.SourceRowId)
                ))
                "SourceRows must not contain blank SourceRowId values."
            |> appendIf
                (hasBlanks (
                    annotation.Boundaries
                    |> Seq.map (fun boundary -> boundary.BoundaryId)
                ))
                "Boundaries must not contain blank BoundaryId values."
            |> appendIf
                (hasBlanks (
                    annotation.Boundaries
                    |> Seq.map (fun boundary -> boundary.BoundaryKind)
                ))
                "Boundaries must not contain blank BoundaryKind values."
            |> appendIf
                (hasBlanks (
                    annotation.Spans
                    |> Seq.map (fun span -> span.SpanId)
                ))
                "Spans must not contain blank SpanId values."
            |> List.rev

        let rangeErrors =
            [
                yield! collectRangeErrors "RequestedLineRange" annotation.RequestedLineRange

                for line in annotation.Lines do
                    yield! collectRangeErrors $"Line '{line.LineNumber}'" { StartLine = line.LineNumber; EndLine = line.LineNumber }

                for sourceRow in annotation.SourceRows do
                    yield! collectRangeErrors $"SourceRow '{sourceRow.SourceRowId}'" sourceRow.LineRange

                for boundary in annotation.Boundaries do
                    yield! collectRangeErrors $"Boundary '{boundary.BoundaryId}'" boundary.LineRange

                for span in annotation.Spans do
                    yield! collectRangeErrors $"Span '{span.SpanId}'" span.LineRange
            ]

        let requestedRangeErrors =
            [
                for line in annotation.Lines do
                    let lineRange = { StartLine = line.LineNumber; EndLine = line.LineNumber }

                    if not (containsLineRange annotation.RequestedLineRange lineRange) then
                        $"Line '{line.LineNumber}' must stay inside RequestedLineRange."

                for boundary in annotation.Boundaries do
                    if not (containsLineRange annotation.RequestedLineRange boundary.LineRange) then
                        $"Boundary '{boundary.BoundaryId}' LineRange must stay inside RequestedLineRange."

                for span in annotation.Spans do
                    if not (containsLineRange annotation.RequestedLineRange span.LineRange) then
                        $"Span '{span.SpanId}' LineRange must stay inside RequestedLineRange."
            ]

        let sourceRowPathErrors =
            annotation.SourceRows
            |> Seq.filter (fun sourceRow -> not (String.Equals(sourceRow.Path, annotation.Path, StringComparison.Ordinal)))
            |> Seq.map (fun sourceRow -> $"SourceRow '{sourceRow.SourceRowId}' Path must match annotation Path '{annotation.Path}'.")
            |> Seq.toList

        let sourceReferenceBudgetErrors =
            []
            |> appendIf
                (annotation.SourceReferences.Length > annotation.MaxReferences)
                $"SourceReferences must contain no more than MaxReferences ({annotation.MaxReferences}) entries."
            |> List.rev

        let lineTextErrors =
            []
            |> appendIf
                (not annotation.IncludeLineText
                 && annotation.Lines.Length > 0)
                "Lines must be empty when IncludeLineText is false."
            |> List.rev

        let referenceTypeErrors =
            [
                for referenceTypeName in annotation.ReferenceTypeFilter do
                    if isUnknownReferenceTypeName referenceTypeName then
                        $"ReferenceTypeFilter contains unknown ReferenceType '{referenceTypeName}'."

                for sourceReference in annotation.SourceReferences do
                    if isUnknownReferenceTypeName sourceReference.ReferenceType then
                        $"SourceReference '{sourceReference.SourceReferenceId}' contains unknown ReferenceType '{sourceReference.ReferenceType}'."
            ]

        let errors =
            [
                yield! rangeErrors
                yield! duplicateErrors
                yield! blankIdentifierErrors
                yield! sourceRowReferenceErrors
                yield! boundarySourceRowErrors
                yield! spanBoundaryErrors
                yield! spanSourceRowErrors
                yield! resolvedSpanSourceRowErrors
                yield! requestedRangeErrors
                yield! sourceRowPathErrors
                yield! sourceReferenceBudgetErrors
                yield! lineTextErrors
                yield! referenceTypeErrors
            ]

        match errors with
        | [] -> Ok()
        | errors -> Error errors

    /// Validates validate.
    let validate (annotation: BranchAnnotationDto) =
        [
            match validateMaxReferences annotation.MaxReferences with
            | Ok () -> ()
            | Error errors -> yield! errors

            match validateLinkIntegrity annotation with
            | Ok () -> ()
            | Error errors -> yield! errors
        ]
        |> function
            | [] -> Ok()
            | errors -> Error errors
