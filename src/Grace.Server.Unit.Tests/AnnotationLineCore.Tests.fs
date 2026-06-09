namespace Grace.Server.Tests

open Grace.Shared.AnnotationLineCore
open Grace.Types.Annotation
open Grace.Types.Common
open NUnit.Framework
open System
open System.Text

[<Parallelizable(ParallelScope.All)>]
type AnnotationLineCoreTests() =

    let targetReferenceId = Guid.Parse("11111111-1111-1111-1111-111111111111")
    let oldReferenceId = Guid.Parse("22222222-2222-2222-2222-222222222222")
    let newReferenceId = Guid.Parse("33333333-3333-3333-3333-333333333333")
    let oldDirectoryVersionId = Guid.Parse("44444444-4444-4444-4444-444444444444")
    let newDirectoryVersionId = Guid.Parse("55555555-5555-5555-5555-555555555555")

    let bytes (text: string) = Encoding.UTF8.GetBytes(text)

    let sourceReference sourceReferenceId referenceId referenceText directoryVersionId =
        { AnnotationSourceReference.Default with
            SourceReferenceId = sourceReferenceId
            ReferenceId = referenceId
            ReferenceType = "Commit"
            ReferenceText = referenceText
            DirectoryVersionId = directoryVersionId
        }

    let oldReference = sourceReference "source-reference-old" oldReferenceId "old" oldDirectoryVersionId
    let newReference = sourceReference "source-reference-new" newReferenceId "new" newDirectoryVersionId

    let historyDocument sourceReference content = { SourceReference = sourceReference; Path = "src/App.fs"; Content = bytes content }

    let build requestedRange history =
        buildAnnotation (requestedRange, targetReferenceId, "src/App.fs", [| ReferenceType.Commit |], DefaultMaxReferences, true, history)

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
