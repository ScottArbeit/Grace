namespace Grace.Server.Tests

open Grace.Shared.AnnotationLineCore
open Grace.Types.Annotation
open Grace.Types.Common
open NUnit.Framework
open System
open System.Text

[<Parallelizable(ParallelScope.All)>]
type AnnotationLineCoreTests() =

    let oldReferenceId = Guid.Parse("22222222-2222-2222-2222-222222222222")
    let newReferenceId = Guid.Parse("33333333-3333-3333-3333-333333333333")
    let targetReferenceId = newReferenceId
    let mismatchedTargetReferenceId = Guid.Parse("11111111-1111-1111-1111-111111111111")
    let saveReferenceId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
    let oldDirectoryVersionId = Guid.Parse("44444444-4444-4444-4444-444444444444")
    let newDirectoryVersionId = Guid.Parse("55555555-5555-5555-5555-555555555555")
    let saveDirectoryVersionId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")

    let bytes (text: string) = Encoding.UTF8.GetBytes(text)

    let sourceReferenceWithType sourceReferenceId referenceId referenceType referenceText directoryVersionId =
        { AnnotationSourceReference.Default with
            SourceReferenceId = sourceReferenceId
            ReferenceId = referenceId
            ReferenceType = referenceType
            ReferenceText = referenceText
            DirectoryVersionId = directoryVersionId
        }

    let sourceReference sourceReferenceId referenceId referenceText directoryVersionId =
        sourceReferenceWithType sourceReferenceId referenceId "Commit" referenceText directoryVersionId

    let oldReference = sourceReference "source-reference-old" oldReferenceId "old" oldDirectoryVersionId
    let newReference = sourceReference "source-reference-new" newReferenceId "new" newDirectoryVersionId
    let saveReference = sourceReferenceWithType "source-reference-save" saveReferenceId "Save" "save" saveDirectoryVersionId

    let historyDocument sourceReference content = { SourceReference = sourceReference; Path = "src/App.fs"; Content = bytes content }

    let historyDocumentWithPath sourceReference path content = { SourceReference = sourceReference; Path = path; Content = bytes content }

    let historyDocumentBytes sourceReference content = { SourceReference = sourceReference; Path = "src/App.fs"; Content = content }

    let build requestedRange history =
        buildAnnotation (requestedRange, targetReferenceId, "src/App.fs", [| ReferenceType.Commit |], DefaultMaxReferences, true, history)

    let buildWithoutReferenceTypeFilter requestedRange history =
        buildAnnotation (requestedRange, targetReferenceId, "src/App.fs", Array.empty, DefaultMaxReferences, true, history)

    let buildWithMaxReferences maxReferences requestedRange history =
        buildAnnotation (requestedRange, targetReferenceId, "src/App.fs", [| ReferenceType.Commit |], maxReferences, true, history)

    let assertOk result =
        match result with
        | Ok value -> value
        | Error errors -> failwithf "%A" errors

    let assertValid (annotation: BranchAnnotationDto) =
        match Grace.Types.Annotation.validate annotation with
        | Ok () -> ()
        | Error errors -> Assert.Fail(String.Join(Environment.NewLine, errors :> seq<string>))

    let coveredLines (annotation: BranchAnnotationDto) =
        annotation.Spans
        |> Array.collect (fun span ->
            [|
                span.LineRange.StartLine .. span.LineRange.EndLine
            |])

    let assertCoveredExactlyOnce startLine endLine (annotation: BranchAnnotationDto) =
        let lines = coveredLines annotation

        Assert.Multiple(
            Action (fun () ->
                Assert.That(lines, Is.EquivalentTo([| startLine..endLine |]))

                lines
                |> Array.countBy id
                |> Array.iter (fun (lineNumber, count) -> Assert.That(count, Is.EqualTo(1), $"line {lineNumber} should be covered once")))
        )

    [<Test>]
    member _.DecodeVisibleTextNormalizesLineEndings() =
        let lf =
            decodeVisibleText (bytes "one\ntwo\nthree")
            |> assertOk

        let crlf =
            decodeVisibleText (bytes "one\r\ntwo\r\nthree")
            |> assertOk

        let cr =
            decodeVisibleText (bytes "one\rtwo\rthree")
            |> assertOk

        Assert.Multiple(
            Action (fun () ->
                Assert.That(crlf.Lines = lf.Lines, Is.True)
                Assert.That(cr.Lines = lf.Lines, Is.True))
        )

    [<Test>]
    member _.DecodeVisibleTextDoesNotCreateExtraLineForTerminalNewline() =
        let document = decodeVisibleText (bytes "one\ntwo\n") |> assertOk

        Assert.That(document.Lines = [| "one"; "two" |], Is.True)

    [<Test>]
    member _.DecodeVisibleTextKeepsEmptyFileAsZeroVisibleLines() =
        let document = decodeVisibleText Array.empty |> assertOk

        Assert.That(document.Lines, Is.Empty)

    [<Test>]
    member _.DecodeVisibleTextRejectsInvalidUtf8() =
        let result = decodeVisibleText [| 0xC3uy; 0x28uy |]

        match result with
        | Ok _ -> Assert.Fail("Invalid UTF-8 bytes should be rejected.")
        | Error (InvalidUtf8 message) -> Assert.That(message, Is.Not.Empty)

    [<Test>]
    member _.DecodeVisibleTextStripsUtf8Bom() =
        let document =
            decodeVisibleText [| 0xEFuy
                                 0xBBuy
                                 0xBFuy
                                 0x6Fuy
                                 0x6Buy |]
            |> assertOk

        Assert.That(document.Lines = [| "ok" |], Is.True)

    [<Test>]
    member _.BuildAnnotationUsesExactPathContinuityAndCountsWhitespaceChanges() =
        let annotation =
            build
                { StartLine = 1; EndLine = 3 }
                [|
                    historyDocument oldReference "let value = 1\nsame\n  whitespace"
                    historyDocument newReference "let value = 1\nsame\n whitespace"
                |]
            |> assertOk

        assertValid annotation
        assertCoveredExactlyOnce 1 3 annotation

        Assert.Multiple(
            Action (fun () ->
                Assert.That(annotation.Spans, Has.Length.EqualTo(2))
                Assert.That(annotation.Spans[0].LineRange, Is.EqualTo({ StartLine = 1; EndLine = 2 }))
                Assert.That(annotation.SourceRows[0].SourceReferenceId, Is.EqualTo("source-reference-old"))
                Assert.That(annotation.Spans[1].LineRange, Is.EqualTo({ StartLine = 3; EndLine = 3 }))
                Assert.That(annotation.SourceRows[1].SourceReferenceId, Is.EqualTo("source-reference-new")))
        )

    [<Test>]
    member _.BuildAnnotationDoesNotTreatMovedRepeatedTextAsProof() =
        let annotation =
            build
                { StartLine = 1; EndLine = 3 }
                [|
                    historyDocument oldReference "alpha\nrepeat\nrepeat"
                    historyDocument newReference "repeat\nalpha\nrepeat"
                |]
            |> assertOk

        assertValid annotation
        assertCoveredExactlyOnce 1 3 annotation

        let sourceByLine =
            annotation.Spans
            |> Array.collect (fun span ->
                span.SourceRowIds
                |> Array.map (fun rowId ->
                    span.LineRange,
                    annotation.SourceRows
                    |> Array.find (fun row -> row.SourceRowId = rowId)))

        Assert.Multiple(
            Action (fun () ->
                let firstRow = sourceByLine[0] |> snd
                let secondRow = sourceByLine[1] |> snd

                Assert.That(sourceByLine[0] |> fst, Is.EqualTo({ StartLine = 1; EndLine = 2 }))
                Assert.That(firstRow.SourceReferenceId, Is.EqualTo("source-reference-new"))
                Assert.That(sourceByLine[1] |> fst, Is.EqualTo({ StartLine = 3; EndLine = 3 }))
                Assert.That(secondRow.SourceReferenceId, Is.EqualTo("source-reference-old")))
        )

    [<Test>]
    member _.BuildAnnotationSkipsFilteredReferenceTypesBeforeTracing() =
        let annotation =
            build
                { StartLine = 1; EndLine = 1 }
                [|
                    historyDocument oldReference "old"
                    historyDocument saveReference "new"
                    historyDocument newReference "new"
                |]
            |> assertOk

        assertValid annotation
        assertCoveredExactlyOnce 1 1 annotation

        Assert.Multiple(
            Action (fun () ->
                Assert.That(annotation.SourceRows, Has.Length.EqualTo(1))
                Assert.That(annotation.SourceRows[0].SourceReferenceId, Is.EqualTo("source-reference-new"))
                Assert.That(annotation.SourceReferences, Has.Length.EqualTo(1))
                Assert.That(annotation.SourceReferences[0].SourceReferenceId, Is.EqualTo("source-reference-new"))
                Assert.That(annotation.SourceReferences[0].ReferenceType, Is.EqualTo("Commit")))
        )

    [<Test>]
    member _.BuildAnnotationSkipsFilteredReferenceTypesBeforePathValidation() =
        let annotation =
            build
                { StartLine = 1; EndLine = 1 }
                [|
                    historyDocument oldReference "old"
                    historyDocumentWithPath saveReference "src/Renamed.fs" "new"
                    historyDocument newReference "new"
                |]
            |> assertOk

        assertValid annotation
        assertCoveredExactlyOnce 1 1 annotation

        Assert.Multiple(
            Action (fun () ->
                Assert.That(annotation.SourceRows, Has.Length.EqualTo(1))
                Assert.That(annotation.SourceRows[0].SourceReferenceId, Is.EqualTo("source-reference-new"))
                Assert.That(annotation.SourceReferences, Has.Length.EqualTo(1))
                Assert.That(annotation.SourceReferences[0].ReferenceType, Is.EqualTo("Commit")))
        )

    [<Test>]
    member _.BuildAnnotationRejectsUnknownTargetReferenceTypeWithoutFilter() =
        let unknownReference = sourceReferenceWithType "source-reference-unknown" targetReferenceId "Unknown" "unknown" newDirectoryVersionId

        let result =
            buildWithoutReferenceTypeFilter
                { StartLine = 1; EndLine = 1 }
                [|
                    historyDocument unknownReference "new"
                |]

        match result with
        | Ok annotation ->
            assertValid annotation
            Assert.Fail("Unknown target reference types should be rejected.")
        | Error errors -> Assert.That(errors, Has.Some.Contains("unknown ReferenceType 'Unknown'"))

    [<Test>]
    member _.BuildAnnotationRejectsUnknownTargetReferenceTypePastEndOfFileWithoutFilter() =
        let unknownReference = sourceReferenceWithType "source-reference-unknown" targetReferenceId "Unknown" "unknown" newDirectoryVersionId

        let result =
            buildWithoutReferenceTypeFilter
                { StartLine = 1; EndLine = 1 }
                [|
                    historyDocument unknownReference String.Empty
                |]

        match result with
        | Ok annotation ->
            assertValid annotation
            Assert.Fail("Unknown target reference types should be rejected even when requested lines are missing.")
        | Error errors -> Assert.That(errors, Has.Some.Contains("unknown ReferenceType 'Unknown'"))

    [<Test>]
    member _.BuildAnnotationRejectsUnknownSourceReferenceTypeWithoutFilter() =
        let unknownReference = sourceReferenceWithType "source-reference-unknown" oldReferenceId "Unknown" "unknown" oldDirectoryVersionId

        let result =
            buildWithoutReferenceTypeFilter
                { StartLine = 1; EndLine = 1 }
                [|
                    historyDocument unknownReference "same"
                    historyDocument newReference "same"
                |]

        match result with
        | Ok annotation ->
            assertValid annotation
            Assert.Fail("Unknown source reference types should be rejected.")
        | Error errors -> Assert.That(errors, Has.Some.Contains("unknown ReferenceType 'Unknown'"))

    [<Test>]
    member _.BuildAnnotationRejectsBlankUsedSourceReferenceId() =
        let blankReference = sourceReference String.Empty oldReferenceId "blank" oldDirectoryVersionId

        let result =
            buildWithoutReferenceTypeFilter
                { StartLine = 1; EndLine = 1 }
                [|
                    historyDocument blankReference "same"
                    historyDocument newReference "same"
                |]

        match result with
        | Ok annotation ->
            assertValid annotation
            Assert.Fail("Blank source reference ids should be rejected before DTO creation.")
        | Error errors -> Assert.That(errors, Has.Some.Contains("blank SourceReferenceId"))

    [<Test>]
    member _.BuildAnnotationRejectsDuplicateIncludedSourceReferenceIds() =
        let duplicateOldReference = { oldReference with ReferenceId = saveReferenceId; ReferenceText = "duplicate old" }

        let result =
            buildWithoutReferenceTypeFilter
                { StartLine = 1; EndLine = 1 }
                [|
                    historyDocument oldReference "same"
                    historyDocument duplicateOldReference "same"
                    historyDocument newReference "same"
                |]

        match result with
        | Ok annotation ->
            assertValid annotation
            Assert.Fail("Duplicate source reference ids should be rejected before budgeting and DTO creation.")
        | Error errors -> Assert.That(errors, Has.Some.Contains("appears more than once"))

    [<Test>]
    member _.BuildAnnotationRejectsHistoryWhenLastReferenceDoesNotMatchTargetReferenceId() =
        let result =
            buildAnnotation (
                { StartLine = 1; EndLine = 1 },
                mismatchedTargetReferenceId,
                "src/App.fs",
                [| ReferenceType.Commit |],
                DefaultMaxReferences,
                true,
                [|
                    historyDocument oldReference "old"
                    historyDocument newReference "new"
                |]
            )

        match result with
        | Ok _ -> Assert.Fail("History ending at a different target reference should be rejected.")
        | Error errors -> Assert.That(errors, Has.Some.Contains("TargetReferenceId"))

    [<Test>]
    member _.BuildAnnotationSkipsFilteredInvalidUtf8ReferenceBeforeDecoding() =
        let annotation =
            build
                { StartLine = 1; EndLine = 1 }
                [|
                    historyDocument oldReference "old"
                    historyDocumentBytes saveReference [| 0xC3uy; 0x28uy |]
                    historyDocument newReference "new"
                |]
            |> assertOk

        assertValid annotation
        assertCoveredExactlyOnce 1 1 annotation

        Assert.Multiple(
            Action (fun () ->
                Assert.That(annotation.SourceRows, Has.Length.EqualTo(1))
                Assert.That(annotation.SourceRows[0].SourceReferenceId, Is.EqualTo("source-reference-new"))
                Assert.That(annotation.SourceReferences, Has.Length.EqualTo(1))
                Assert.That(annotation.SourceReferences[0].ReferenceType, Is.EqualTo("Commit")))
        )

    [<Test>]
    member _.BuildAnnotationCoalescesOnlyContiguousMatchingSourceDetails() =
        let annotation =
            build
                { StartLine = 1; EndLine = 4 }
                [|
                    historyDocument oldReference "a\nb\nc\nd"
                    historyDocument newReference "a\nb\nchanged\nd"
                |]
            |> assertOk

        assertValid annotation
        assertCoveredExactlyOnce 1 4 annotation

        Assert.Multiple(
            Action (fun () ->
                Assert.That(annotation.Spans, Has.Length.EqualTo(3))
                Assert.That(annotation.Spans[0].LineRange, Is.EqualTo({ StartLine = 1; EndLine = 2 }))
                Assert.That(annotation.SourceRows[0].LineRange, Is.EqualTo({ StartLine = 1; EndLine = 2 }))
                Assert.That(annotation.Spans[1].LineRange, Is.EqualTo({ StartLine = 3; EndLine = 3 }))
                Assert.That(annotation.Spans[2].LineRange, Is.EqualTo({ StartLine = 4; EndLine = 4 })))
        )

    [<Test>]
    member _.BuildAnnotationAddsBoundarySpanForRequestedLinePastEndOfFile() =
        let annotation =
            build
                { StartLine = 1; EndLine = 2 }
                [|
                    historyDocument newReference "only line\n"
                |]
            |> assertOk

        assertValid annotation
        assertCoveredExactlyOnce 1 2 annotation

        Assert.Multiple(
            Action (fun () ->
                Assert.That(
                    annotation.Lines =
                        [|
                            { LineNumber = 1; Text = "only line" }
                        |],
                    Is.True
                )

                Assert.That(annotation.Boundaries, Has.Length.EqualTo(1))
                Assert.That(annotation.Boundaries[0].LineRange, Is.EqualTo({ StartLine = 2; EndLine = 2 }))
                Assert.That(annotation.Spans[1].BoundaryId, Is.EqualTo(annotation.Boundaries[0].BoundaryId))
                Assert.That(annotation.Spans[1].SourceRowIds, Is.Empty))
        )

    [<Test>]
    member _.BuildAnnotationCollapsesHugeMissingRangeWithoutPerLineState() =
        let annotation =
            build
                { StartLine = 1; EndLine = 2_000_000 }
                [|
                    historyDocument newReference String.Empty
                |]
            |> assertOk

        assertValid annotation

        Assert.Multiple(
            Action (fun () ->
                Assert.That(annotation.Lines, Is.Empty)
                Assert.That(annotation.SourceRows, Is.Empty)
                Assert.That(annotation.Boundaries, Has.Length.EqualTo(1))
                Assert.That(annotation.Boundaries[0].LineRange, Is.EqualTo({ StartLine = 1; EndLine = 2_000_000 }))
                Assert.That(annotation.Spans, Has.Length.EqualTo(1))
                Assert.That(annotation.Spans[0].LineRange, Is.EqualTo({ StartLine = 1; EndLine = 2_000_000 }))
                Assert.That(annotation.Spans[0].BoundaryId, Is.EqualTo(annotation.Boundaries[0].BoundaryId))
                Assert.That(annotation.Spans[0].SourceRowIds, Is.Empty))
        )

    [<Test>]
    member _.BuildAnnotationRejectsRequiredSourceReferencesAboveBudget() =
        let result =
            buildWithMaxReferences
                1
                { StartLine = 1; EndLine = 2 }
                [|
                    historyDocument oldReference "same\nold"
                    historyDocument newReference "same\nnew"
                |]

        match result with
        | Ok annotation ->
            assertValid annotation
            Assert.That(annotation.SourceReferences, Has.Length.LessThanOrEqualTo(annotation.MaxReferences))
        | Error errors -> Assert.That(errors, Has.Some.Contains("MaxReferences"))

    [<Test>]
    member _.BuildComponentsDoesNotCollapseWhenBoundaryStateDiffers() =
        let requestedRange = { StartLine = 1; EndLine = 2 }

        let firstBoundary = { BoundaryKind = "TargetLineMissing"; SourceRows = Array.empty }

        let secondBoundary = { BoundaryKind = "ReferenceFiltered"; SourceRows = Array.empty }

        let components =
            buildComponents
                requestedRange
                Array.empty
                [|
                    Boundary firstBoundary
                    Boundary secondBoundary
                |]

        Assert.Multiple(
            Action (fun () ->
                Assert.That(components.Boundaries, Has.Length.EqualTo(2))
                Assert.That(components.Spans, Has.Length.EqualTo(2))
                Assert.That(components.Spans[0].BoundaryId, Is.EqualTo(components.Boundaries[0].BoundaryId))
                Assert.That(components.Spans[1].BoundaryId, Is.EqualTo(components.Boundaries[1].BoundaryId)))
        )
