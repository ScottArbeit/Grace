namespace Grace.Server.Tests

open Grace.Shared.AnnotationLineCore
open Grace.Types.Annotation
open Grace.Types.Common
open NUnit.Framework
open System
open System.Text

/// Covers annotation Line Core behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type AnnotationLineCoreTests() =

    let oldReferenceId = Guid.Parse("22222222-2222-2222-2222-222222222222")
    let newReferenceId = Guid.Parse("33333333-3333-3333-3333-333333333333")
    let targetReferenceId = newReferenceId
    let mismatchedTargetReferenceId = Guid.Parse("11111111-1111-1111-1111-111111111111")
    let saveReferenceId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
    let tagReferenceId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc")
    let oldDirectoryVersionId = Guid.Parse("44444444-4444-4444-4444-444444444444")
    let newDirectoryVersionId = Guid.Parse("55555555-5555-5555-5555-555555555555")
    let saveDirectoryVersionId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")
    let tagDirectoryVersionId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd")

    let bytes (text: string) = Encoding.UTF8.GetBytes(text)

    /// Builds source Reference With Type test data for the server unit annotation Line Core scenarios in this file.
    let sourceReferenceWithType sourceReferenceId referenceId referenceType referenceText directoryVersionId =
        { AnnotationSourceReference.Default with
            SourceReferenceId = sourceReferenceId
            ReferenceId = referenceId
            ReferenceType = referenceType
            ReferenceText = referenceText
            DirectoryVersionId = directoryVersionId
        }

    /// Builds source Reference test data for the server unit annotation Line Core scenarios in this file.
    let sourceReference sourceReferenceId referenceId referenceText directoryVersionId =
        sourceReferenceWithType sourceReferenceId referenceId "Commit" referenceText directoryVersionId

    let oldReference = sourceReference "source-reference-old" oldReferenceId "old" oldDirectoryVersionId
    let newReference = sourceReference "source-reference-new" newReferenceId "new" newDirectoryVersionId
    let saveReference = sourceReferenceWithType "source-reference-save" saveReferenceId "Save" "save" saveDirectoryVersionId
    let tagReference = sourceReferenceWithType "source-reference-tag" tagReferenceId "Tag" "tag" tagDirectoryVersionId

    let historyDocument sourceReference content = { SourceReference = sourceReference; Path = "src/App.fs"; Content = bytes content }

    let historyDocumentWithPath sourceReference path content = { SourceReference = sourceReference; Path = path; Content = bytes content }

    let historyDocumentBytes sourceReference content = { SourceReference = sourceReference; Path = "src/App.fs"; Content = content }

    /// Builds effective History Document test data for the server unit annotation Line Core scenarios in this file.
    let effectiveHistoryDocument document basedOnReferenceId isAuthorized =
        { Document = document; BasedOnReferenceId = basedOnReferenceId; IsAuthorized = isAuthorized; BoundaryKind = None }

    /// Builds boundary Effective History Document test data for the server unit annotation Line Core scenarios in this file.
    let boundaryEffectiveHistoryDocument document boundaryKind =
        { Document = document; BasedOnReferenceId = None; IsAuthorized = true; BoundaryKind = Some boundaryKind }

    /// Builds build test data for the server unit annotation Line Core scenarios in this file.
    let build requestedRange history =
        buildAnnotation (requestedRange, targetReferenceId, "src/App.fs", [| ReferenceType.Commit |], DefaultMaxReferences, true, history)

    /// Builds without Line Text fixtures used by the server unit annotation Line Core assertions.
    let buildWithoutLineText requestedRange history =
        buildAnnotation (requestedRange, targetReferenceId, "src/App.fs", [| ReferenceType.Commit |], DefaultMaxReferences, false, history)

    /// Builds without Reference Type Filter fixtures used by the server unit annotation Line Core assertions.
    let buildWithoutReferenceTypeFilter requestedRange history =
        buildAnnotation (requestedRange, targetReferenceId, "src/App.fs", Array.empty, DefaultMaxReferences, true, history)

    /// Builds with Max References fixtures used by the server unit annotation Line Core assertions.
    let buildWithMaxReferences maxReferences requestedRange history =
        buildAnnotation (requestedRange, targetReferenceId, "src/App.fs", [| ReferenceType.Commit |], maxReferences, true, history)

    /// Builds with Reference Types fixtures used by the server unit annotation Line Core assertions.
    let buildWithReferenceTypes referenceTypes requestedRange history =
        buildAnnotation (requestedRange, targetReferenceId, "src/App.fs", referenceTypes, DefaultMaxReferences, true, history)

    /// Builds from Traversal fixtures used by the server unit annotation Line Core assertions.
    let buildFromTraversal requestedRange traversalResult =
        buildAnnotationFromEffectiveHistoryTraversal (
            requestedRange,
            targetReferenceId,
            "src/App.fs",
            [| ReferenceType.Commit |],
            DefaultMaxReferences,
            true,
            traversalResult
        )

    /// Asserts the ok condition so failures identify the violated server unit annotation Line Core invariant.
    let assertOk result =
        match result with
        | Ok value -> value
        | Error errors -> failwithf "%A" errors

    /// Asserts the valid condition so failures identify the violated server unit annotation Line Core invariant.
    let assertValid (annotation: BranchAnnotationDto) =
        match Grace.Types.Annotation.validate annotation with
        | Ok () -> ()
        | Error errors -> Assert.Fail(String.Join(Environment.NewLine, errors :> seq<string>))

    /// Builds covered Lines test data for the server unit annotation Line Core scenarios in this file.
    let coveredLines (annotation: BranchAnnotationDto) =
        annotation.Spans
        |> Array.collect (fun span ->
            [|
                span.LineRange.StartLine .. span.LineRange.EndLine
            |])

    /// Asserts the covered Exactly Once condition so failures identify the violated server unit annotation Line Core invariant.
    let assertCoveredExactlyOnce startLine endLine (annotation: BranchAnnotationDto) =
        let lines = coveredLines annotation

        Assert.Multiple(
            Action (fun () ->
                Assert.That(lines, Is.EquivalentTo([| startLine..endLine |]))

                lines
                |> Array.countBy id
                |> Array.iter (fun (lineNumber, count) -> Assert.That(count, Is.EqualTo(1), $"line {lineNumber} should be covered once")))
        )

    /// Builds history Source Reference Ids test data for the server unit annotation Line Core scenarios in this file.
    let historySourceReferenceIds result =
        result.History
        |> Array.map (fun document -> document.SourceReference.SourceReferenceId)

    /// Verifies that traverse Effective Branch History Orders Based On Ancestors Before Target.
    [<Test>]
    member _.TraverseEffectiveBranchHistoryOrdersBasedOnAncestorsBeforeTarget() =
        let oldDocument = historyDocument oldReference "parent"
        let newDocument = historyDocument newReference "child"

        let result =
            traverseEffectiveBranchHistory
                targetReferenceId
                DefaultMaxReferences
                [|
                    effectiveHistoryDocument newDocument (Some oldReferenceId) true
                    effectiveHistoryDocument oldDocument None true
                |]

        let sourceReferenceIds = historySourceReferenceIds result

        let sourceReferenceIdsMatch =
            sourceReferenceIds = [|
                "source-reference-old"
                "source-reference-new"
            |]

        Assert.Multiple(
            Action (fun () ->
                Assert.That(result.BoundaryKind.IsNone, Is.True)

                Assert.That(sourceReferenceIdsMatch, Is.True))
        )

    /// Verifies that traverse Effective Branch History Stops At Unauthorized Ancestor.
    [<Test>]
    member _.TraverseEffectiveBranchHistoryStopsAtUnauthorizedAncestor() =
        let oldDocument = historyDocument oldReference "parent"
        let newDocument = historyDocument newReference "child"

        let result =
            traverseEffectiveBranchHistory
                targetReferenceId
                DefaultMaxReferences
                [|
                    effectiveHistoryDocument newDocument (Some oldReferenceId) true
                    effectiveHistoryDocument oldDocument None false
                |]

        let sourceReferenceIds = historySourceReferenceIds result
        let sourceReferenceIdsMatch = sourceReferenceIds = [| "source-reference-new" |]

        Assert.Multiple(
            Action (fun () ->
                Assert.That(result.BoundaryKind, Is.EqualTo(Some "UnauthorizedAncestor"))

                Assert.That(sourceReferenceIdsMatch, Is.True))
        )

    /// Verifies that traverse Effective Branch History Stops At Missing Parent Reference.
    [<Test>]
    member _.TraverseEffectiveBranchHistoryStopsAtMissingParentReference() =
        let newDocument = historyDocument newReference "child"

        let result =
            traverseEffectiveBranchHistory
                targetReferenceId
                DefaultMaxReferences
                [|
                    effectiveHistoryDocument newDocument (Some oldReferenceId) true
                |]

        let sourceReferenceIds = historySourceReferenceIds result
        let sourceReferenceIdsMatch = sourceReferenceIds = [| "source-reference-new" |]

        Assert.Multiple(
            Action (fun () ->
                Assert.That(result.BoundaryKind, Is.EqualTo(Some "MissingParentReference"))

                Assert.That(sourceReferenceIdsMatch, Is.True))
        )

    /// Verifies that traverse Effective Branch History Stops At Based On Loop Or Repeated Link.
    [<Test>]
    member _.TraverseEffectiveBranchHistoryStopsAtBasedOnLoopOrRepeatedLink() =
        let oldDocument = historyDocument oldReference "parent"
        let newDocument = historyDocument newReference "child"

        let result =
            traverseEffectiveBranchHistory
                targetReferenceId
                DefaultMaxReferences
                [|
                    effectiveHistoryDocument newDocument (Some oldReferenceId) true
                    effectiveHistoryDocument oldDocument (Some targetReferenceId) true
                |]

        let sourceReferenceIds = historySourceReferenceIds result

        let sourceReferenceIdsMatch =
            sourceReferenceIds = [|
                "source-reference-old"
                "source-reference-new"
            |]

        Assert.Multiple(
            Action (fun () ->
                Assert.That(result.BoundaryKind, Is.EqualTo(Some "BasedOnLoopOrRepeatedLink"))

                Assert.That(sourceReferenceIdsMatch, Is.True))
        )

    /// Verifies that traverse Effective Branch History Stops At Traversal Budget Boundary.
    [<Test>]
    member _.TraverseEffectiveBranchHistoryStopsAtTraversalBudgetBoundary() =
        let oldDocument = historyDocument oldReference "parent"
        let newDocument = historyDocument newReference "child"

        let result =
            traverseEffectiveBranchHistory
                targetReferenceId
                1
                [|
                    effectiveHistoryDocument newDocument (Some oldReferenceId) true
                    effectiveHistoryDocument oldDocument None true
                |]

        let sourceReferenceIds = historySourceReferenceIds result
        let sourceReferenceIdsMatch = sourceReferenceIds = [| "source-reference-new" |]

        Assert.Multiple(
            Action (fun () ->
                Assert.That(result.BoundaryKind, Is.EqualTo(Some "TraversalBudgetReached"))

                Assert.That(sourceReferenceIdsMatch, Is.True))
        )

    /// Verifies that traverse Effective Branch History Stops At Unreadable Ancestor Boundary.
    [<Test>]
    member _.TraverseEffectiveBranchHistoryStopsAtUnreadableAncestorBoundary() =
        let oldDocument = historyDocument oldReference String.Empty
        let newDocument = historyDocument newReference "child"

        let result =
            traverseEffectiveBranchHistory
                targetReferenceId
                DefaultMaxReferences
                [|
                    effectiveHistoryDocument newDocument (Some oldReferenceId) true
                    boundaryEffectiveHistoryDocument oldDocument Grace.Server.Branch.unreadableAncestorBoundaryKind
                |]

        let sourceReferenceIds = historySourceReferenceIds result
        let sourceReferenceIdsMatch = sourceReferenceIds = [| "source-reference-new" |]

        Assert.Multiple(
            Action (fun () ->
                Assert.That(result.BoundaryKind, Is.EqualTo(Some Grace.Server.Branch.unreadableAncestorBoundaryKind))

                Assert.That(sourceReferenceIdsMatch, Is.True))
        )

    /// Verifies that decode Visible Text Normalizes Line Endings.
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

    /// Verifies that decode Visible Text Does Not Create Extra Line For Terminal Newline.
    [<Test>]
    member _.DecodeVisibleTextDoesNotCreateExtraLineForTerminalNewline() =
        let document = decodeVisibleText (bytes "one\ntwo\n") |> assertOk

        Assert.That(document.Lines = [| "one"; "two" |], Is.True)

    /// Verifies that decode Visible Text Keeps Empty File As Zero Visible Lines.
    [<Test>]
    member _.DecodeVisibleTextKeepsEmptyFileAsZeroVisibleLines() =
        let document = decodeVisibleText Array.empty |> assertOk

        Assert.That(document.Lines, Is.Empty)

    /// Verifies that decode Visible Text Rejects Invalid Utf8.
    [<Test>]
    member _.DecodeVisibleTextRejectsInvalidUtf8() =
        let result = decodeVisibleText [| 0xC3uy; 0x28uy |]

        match result with
        | Ok _ -> Assert.Fail("Invalid UTF-8 bytes should be rejected.")
        | Error (InvalidUtf8 message) -> Assert.That(message, Is.Not.Empty)

    /// Verifies that decode Visible Text Strips Utf8 Bom.
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

    /// Verifies that build Annotation Uses Exact Path Continuity And Counts Whitespace Changes.
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

    /// Verifies that build Annotation Preserves Shifted Stable Lines After Insertion.
    [<Test>]
    member _.BuildAnnotationPreservesShiftedStableLinesAfterInsertion() =
        let annotation =
            build
                { StartLine = 1; EndLine = 4 }
                [|
                    historyDocument oldReference "a\nb\nc"
                    historyDocument newReference "x\na\nb\nc"
                |]
            |> assertOk

        assertValid annotation
        assertCoveredExactlyOnce 1 4 annotation

        Assert.Multiple(
            Action (fun () ->
                Assert.That(annotation.Spans, Has.Length.EqualTo(2))
                Assert.That(annotation.Spans[0].LineRange, Is.EqualTo({ StartLine = 1; EndLine = 1 }))
                Assert.That(annotation.SourceRows[0].SourceReferenceId, Is.EqualTo("source-reference-new"))
                Assert.That(annotation.SourceRows[0].LineRange, Is.EqualTo({ StartLine = 1; EndLine = 1 }))
                Assert.That(annotation.Spans[1].LineRange, Is.EqualTo({ StartLine = 2; EndLine = 4 }))
                Assert.That(annotation.SourceRows[1].SourceReferenceId, Is.EqualTo("source-reference-old"))
                Assert.That(annotation.SourceRows[1].LineRange, Is.EqualTo({ StartLine = 1; EndLine = 3 })))
        )

    /// Verifies that build Annotation Preserves Shifted Stable Lines After Deletion.
    [<Test>]
    member _.BuildAnnotationPreservesShiftedStableLinesAfterDeletion() =
        let annotation =
            build
                { StartLine = 1; EndLine = 3 }
                [|
                    historyDocument oldReference "x\na\nb\nc"
                    historyDocument newReference "a\nb\nc"
                |]
            |> assertOk

        assertValid annotation
        assertCoveredExactlyOnce 1 3 annotation

        Assert.Multiple(
            Action (fun () ->
                Assert.That(annotation.Spans, Has.Length.EqualTo(1))
                Assert.That(annotation.Spans[0].LineRange, Is.EqualTo({ StartLine = 1; EndLine = 3 }))
                Assert.That(annotation.SourceRows[0].SourceReferenceId, Is.EqualTo("source-reference-old"))
                Assert.That(annotation.SourceRows[0].LineRange, Is.EqualTo({ StartLine = 2; EndLine = 4 })))
        )

    /// Verifies that build Annotation Preserves Unchanged Middle Lines Between Replacements.
    [<Test>]
    member _.BuildAnnotationPreservesUnchangedMiddleLinesBetweenReplacements() =
        let annotation =
            build
                { StartLine = 1; EndLine = 3 }
                [|
                    historyDocument oldReference "before\nkeep\nafter"
                    historyDocument newReference "changed\nkeep\nchanged"
                |]
            |> assertOk

        assertValid annotation
        assertCoveredExactlyOnce 1 3 annotation

        Assert.Multiple(
            Action (fun () ->
                Assert.That(annotation.Spans, Has.Length.EqualTo(3))
                Assert.That(annotation.Spans[0].LineRange, Is.EqualTo({ StartLine = 1; EndLine = 1 }))
                Assert.That(annotation.SourceRows[0].SourceReferenceId, Is.EqualTo("source-reference-new"))
                Assert.That(annotation.SourceRows[0].LineRange, Is.EqualTo({ StartLine = 1; EndLine = 1 }))
                Assert.That(annotation.Spans[1].LineRange, Is.EqualTo({ StartLine = 2; EndLine = 2 }))
                Assert.That(annotation.SourceRows[1].SourceReferenceId, Is.EqualTo("source-reference-old"))
                Assert.That(annotation.SourceRows[1].LineRange, Is.EqualTo({ StartLine = 2; EndLine = 2 }))
                Assert.That(annotation.Spans[2].LineRange, Is.EqualTo({ StartLine = 3; EndLine = 3 }))
                Assert.That(annotation.SourceRows[2].SourceReferenceId, Is.EqualTo("source-reference-new"))
                Assert.That(annotation.SourceRows[2].LineRange, Is.EqualTo({ StartLine = 3; EndLine = 3 })))
        )

    /// Verifies that build Annotation Preserves Anchored Middle Line When Moved Tie Appears First.
    [<Test>]
    member _.BuildAnnotationPreservesAnchoredMiddleLineWhenMovedTieAppearsFirst() =
        let annotation =
            build
                { StartLine = 1; EndLine = 3 }
                [|
                    historyDocument oldReference "a\nkeep\nb"
                    historyDocument newReference "b\nkeep\na"
                |]
            |> assertOk

        assertValid annotation
        assertCoveredExactlyOnce 1 3 annotation

        Assert.Multiple(
            Action (fun () ->
                Assert.That(annotation.Spans, Has.Length.EqualTo(3))
                Assert.That(annotation.Spans[0].LineRange, Is.EqualTo({ StartLine = 1; EndLine = 1 }))
                Assert.That(annotation.SourceRows[0].SourceReferenceId, Is.EqualTo("source-reference-new"))
                Assert.That(annotation.SourceRows[0].LineRange, Is.EqualTo({ StartLine = 1; EndLine = 1 }))
                Assert.That(annotation.Spans[1].LineRange, Is.EqualTo({ StartLine = 2; EndLine = 2 }))
                Assert.That(annotation.SourceRows[1].SourceReferenceId, Is.EqualTo("source-reference-old"))
                Assert.That(annotation.SourceRows[1].LineRange, Is.EqualTo({ StartLine = 2; EndLine = 2 }))
                Assert.That(annotation.Spans[2].LineRange, Is.EqualTo({ StartLine = 3; EndLine = 3 }))
                Assert.That(annotation.SourceRows[2].SourceReferenceId, Is.EqualTo("source-reference-new"))
                Assert.That(annotation.SourceRows[2].LineRange, Is.EqualTo({ StartLine = 3; EndLine = 3 })))
        )

    /// Verifies that build Annotation Prefers Shorter Anchored Block Over Longer Moved Block.
    [<Test>]
    member _.BuildAnnotationPrefersShorterAnchoredBlockOverLongerMovedBlock() =
        let annotation =
            build
                { StartLine = 3; EndLine = 3 }
                [|
                    historyDocument oldReference "a\nb\nKEEP\nc\nd"
                    historyDocument newReference "c\nd\nKEEP\nx\ny"
                |]
            |> assertOk

        assertValid annotation
        assertCoveredExactlyOnce 3 3 annotation

        Assert.Multiple(
            Action (fun () ->
                Assert.That(annotation.Spans, Has.Length.EqualTo(1))
                Assert.That(annotation.SourceRows, Has.Length.EqualTo(1))
                Assert.That(annotation.SourceRows[0].SourceReferenceId, Is.EqualTo("source-reference-old"))
                Assert.That(annotation.SourceRows[0].LineRange, Is.EqualTo({ StartLine = 3; EndLine = 3 })))
        )

    /// Verifies that build Annotation Does Not Treat Moved Repeated Text As Proof.
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

    /// Verifies that build Annotation Bounds Large Shifted Repeated Alignment.
    [<Test>]
    member _.BuildAnnotationBoundsLargeShiftedRepeatedAlignment() =
        let repeatedLines = Array.create 160 "repeat"
        let oldText = String.Join("\n", Array.append [| "old-start" |] (Array.append repeatedLines [| "old-end" |]))
        let newText = String.Join("\n", repeatedLines)

        let annotation =
            build
                { StartLine = 1; EndLine = repeatedLines.Length }
                [|
                    historyDocument oldReference oldText
                    historyDocument newReference newText
                |]
            |> assertOk

        assertValid annotation
        assertCoveredExactlyOnce 1 repeatedLines.Length annotation

        Assert.Multiple(
            Action (fun () ->
                Assert.That(annotation.Spans, Has.Length.EqualTo(1))
                Assert.That(annotation.Spans[0].LineRange, Is.EqualTo({ StartLine = 1; EndLine = repeatedLines.Length }))
                Assert.That(annotation.Boundaries, Has.Length.EqualTo(1))
                Assert.That(annotation.Boundaries[0].LineRange, Is.EqualTo({ StartLine = 1; EndLine = repeatedLines.Length }))
                Assert.That(annotation.Spans[0].BoundaryId, Is.EqualTo(annotation.Boundaries[0].BoundaryId))
                Assert.That(annotation.Spans[0].SourceRowIds, Is.Empty)
                Assert.That(annotation.SourceRows, Is.Empty))
        )

    /// Verifies that build Annotation Uses Boundary When Shifted Alignment Exceeds Budget.
    [<Test>]
    member _.BuildAnnotationUsesBoundaryWhenShiftedAlignmentExceedsBudget() =
        let stableLines =
            [|
                for index in 1..129 do
                    $"stable-{index}"
            |]

        let oldText =
            String.Join(
                "\n",
                Array.concat [| [| "deleted-header" |]
                                stableLines
                                [| "old-tail" |] |]
            )

        let newText = String.Join("\n", Array.append stableLines [| "new-tail" |])

        let annotation =
            build
                { StartLine = 1; EndLine = stableLines.Length }
                [|
                    historyDocument oldReference oldText
                    historyDocument newReference newText
                |]
            |> assertOk

        assertValid annotation
        assertCoveredExactlyOnce 1 stableLines.Length annotation

        Assert.Multiple(
            Action (fun () ->
                Assert.That(annotation.Spans, Has.Length.EqualTo(1))
                Assert.That(annotation.Spans[0].LineRange, Is.EqualTo({ StartLine = 1; EndLine = stableLines.Length }))
                Assert.That(annotation.Boundaries, Has.Length.EqualTo(1))
                Assert.That(annotation.Boundaries[0].LineRange, Is.EqualTo({ StartLine = 1; EndLine = stableLines.Length }))
                Assert.That(annotation.Spans[0].BoundaryId, Is.EqualTo(annotation.Boundaries[0].BoundaryId))
                Assert.That(annotation.Spans[0].SourceRowIds, Is.Empty)
                Assert.That(annotation.SourceRows, Is.Empty))
        )

    /// Verifies that build Annotation Returns Bounded Boundary For Tiny Request In Large File.
    [<Test>]
    member _.BuildAnnotationReturnsBoundedBoundaryForTinyRequestInLargeFile() =
        let largeText =
            String.Join(
                "\n",
                [|
                    for index in 1..20_001 do
                        $"line-{index}"
                |]
            )

        let annotation =
            buildWithoutLineText
                { StartLine = 10_001; EndLine = 10_001 }
                [|
                    historyDocument oldReference largeText
                    historyDocument newReference largeText
                |]
            |> assertOk

        assertValid annotation
        assertCoveredExactlyOnce 10_001 10_001 annotation

        Assert.Multiple(
            Action (fun () ->
                Assert.That(annotation.Lines, Is.Empty)
                Assert.That(annotation.SourceRows, Is.Empty)
                Assert.That(annotation.Boundaries, Has.Length.EqualTo(1))
                Assert.That(annotation.Boundaries[0].LineRange, Is.EqualTo({ StartLine = 10_001; EndLine = 10_001 }))
                Assert.That(annotation.Spans, Has.Length.EqualTo(1))
                Assert.That(annotation.Spans[0].BoundaryId, Is.EqualTo(annotation.Boundaries[0].BoundaryId))
                Assert.That(annotation.Spans[0].SourceRowIds, Is.Empty))
        )

    /// Verifies that build Annotation Traverses Filtered Save And Projects Attribution To Commit.
    [<Test>]
    member _.BuildAnnotationTraversesFilteredSaveAndProjectsAttributionToCommit() =
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

    /// Verifies that build Annotation Traverses Filtered Save Without Severing Continuity To Earlier Commit.
    [<Test>]
    member _.BuildAnnotationTraversesFilteredSaveWithoutSeveringContinuityToEarlierCommit() =
        let annotation =
            build
                { StartLine = 1; EndLine = 1 }
                [|
                    historyDocument oldReference "stable"
                    historyDocument saveReference "changed"
                    historyDocument newReference "stable"
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

    /// Verifies that build Annotation Rejects Filtered Reference At Different Path Because V1 Does Not Follow Renames.
    [<Test>]
    member _.BuildAnnotationRejectsFilteredReferenceAtDifferentPathBecauseV1DoesNotFollowRenames() =
        let result =
            build
                { StartLine = 1; EndLine = 1 }
                [|
                    historyDocument oldReference "old"
                    historyDocumentWithPath saveReference "src/Renamed.fs" "new"
                    historyDocument newReference "new"
                |]

        match result with
        | Ok annotation ->
            assertValid annotation
            Assert.Fail("Exact-path annotation should not ignore filtered references at a different path.")
        | Error errors -> Assert.That(errors, Has.Some.Contains("must match annotation path"))

    /// Verifies that build Annotation Keeps Unchanged Rebase From Becoming Last Changed Reference.
    [<Test>]
    member _.BuildAnnotationKeepsUnchangedRebaseFromBecomingLastChangedReference() =
        let rebaseReference = sourceReferenceWithType "source-reference-rebase" targetReferenceId "Rebase" "rebase" newDirectoryVersionId

        let annotation =
            buildAnnotation (
                { StartLine = 1; EndLine = 1 },
                targetReferenceId,
                "src/App.fs",
                [| ReferenceType.Commit |],
                DefaultMaxReferences,
                true,
                [|
                    historyDocument oldReference "stable"
                    historyDocument rebaseReference "stable"
                |]
            )
            |> assertOk

        assertValid annotation
        assertCoveredExactlyOnce 1 1 annotation

        Assert.Multiple(
            Action (fun () ->
                Assert.That(annotation.SourceRows, Has.Length.EqualTo(1))
                Assert.That(annotation.SourceRows[0].SourceReferenceId, Is.EqualTo("source-reference-old"))
                Assert.That(annotation.SourceReferences[0].ReferenceType, Is.EqualTo("Commit")))
        )

    /// Verifies that build Annotation Projects Changed Rebase To Explicit Boundary When Filtered Out.
    [<Test>]
    member _.BuildAnnotationProjectsChangedRebaseToExplicitBoundaryWhenFilteredOut() =
        let rebaseReference = sourceReferenceWithType "source-reference-rebase" targetReferenceId "Rebase" "rebase" newDirectoryVersionId

        let annotation =
            buildAnnotation (
                { StartLine = 1; EndLine = 1 },
                targetReferenceId,
                "src/App.fs",
                [| ReferenceType.Commit |],
                DefaultMaxReferences,
                true,
                [|
                    historyDocument oldReference "old"
                    historyDocument rebaseReference "new"
                |]
            )
            |> assertOk

        assertValid annotation
        assertCoveredExactlyOnce 1 1 annotation

        Assert.Multiple(
            Action (fun () ->
                Assert.That(annotation.Boundaries, Has.Length.EqualTo(1))
                Assert.That(annotation.Spans[0].BoundaryId, Is.EqualTo(annotation.Boundaries[0].BoundaryId))
                Assert.That(annotation.SourceRows[0].SourceReferenceId, Is.EqualTo("source-reference-rebase"))
                Assert.That(annotation.SourceReferences, Has.Length.EqualTo(1))
                Assert.That(annotation.SourceReferences[0].ReferenceType, Is.EqualTo("Rebase")))
        )

    /// Verifies that build Annotation Treats Tag As Eligible Without Filter And When Selected.
    [<Test>]
    member _.BuildAnnotationTreatsTagAsEligibleWithoutFilterAndWhenSelected() =
        let tagTargetReference = { tagReference with ReferenceId = targetReferenceId; DirectoryVersionId = newDirectoryVersionId }

        let withoutFilter =
            buildWithoutReferenceTypeFilter
                { StartLine = 1; EndLine = 1 }
                [|
                    historyDocument oldReference "old"
                    historyDocument tagTargetReference "new"
                |]
            |> assertOk

        let tagOnly =
            buildWithReferenceTypes
                [| ReferenceType.Tag |]
                { StartLine = 1; EndLine = 1 }
                [|
                    historyDocument oldReference "old"
                    historyDocument tagTargetReference "new"
                |]
            |> assertOk

        Assert.Multiple(
            Action (fun () ->
                assertValid withoutFilter
                assertValid tagOnly
                Assert.That(withoutFilter.SourceReferences[0].ReferenceType, Is.EqualTo("Tag"))
                Assert.That(tagOnly.SourceReferences[0].ReferenceType, Is.EqualTo("Tag")))
        )

    /// Verifies that build Annotation Bounds Unauthorized Ancestor When History Starts Mid Span.
    [<Test>]
    member _.BuildAnnotationBoundsUnauthorizedAncestorWhenHistoryStartsMidSpan() =
        let annotation =
            buildWithMaxReferences
                1
                { StartLine = 1; EndLine = 1 }
                [|
                    historyDocument oldReference "same"
                    historyDocument newReference "same"
                |]
            |> assertOk

        assertValid annotation
        assertCoveredExactlyOnce 1 1 annotation

        Assert.Multiple(
            Action (fun () ->
                Assert.That(annotation.Boundaries, Has.Length.EqualTo(1))
                Assert.That(annotation.Boundaries[0].LineRange, Is.EqualTo({ StartLine = 1; EndLine = 1 }))
                Assert.That(annotation.Spans[0].BoundaryId, Is.EqualTo(annotation.Boundaries[0].BoundaryId))
                Assert.That(annotation.SourceRows[0].SourceReferenceId, Is.EqualTo("source-reference-new"))
                Assert.That(annotation.SourceReferences, Has.Length.EqualTo(1)))
        )

    /// Verifies that build Annotation Preserves Unauthorized Ancestor Boundary From Traversal Result.
    [<Test>]
    member _.BuildAnnotationPreservesUnauthorizedAncestorBoundaryFromTraversalResult() =
        let oldDocument = historyDocument oldReference "same"
        let newDocument = historyDocument newReference "same"

        let traversalResult =
            traverseEffectiveBranchHistory
                targetReferenceId
                DefaultMaxReferences
                [|
                    effectiveHistoryDocument newDocument (Some oldReferenceId) true
                    effectiveHistoryDocument oldDocument None false
                |]

        let annotation =
            traversalResult
            |> buildFromTraversal { StartLine = 1; EndLine = 1 }
            |> assertOk

        assertValid annotation
        assertCoveredExactlyOnce 1 1 annotation

        Assert.Multiple(
            Action (fun () ->
                Assert.That(traversalResult.BoundaryKind, Is.EqualTo(Some "UnauthorizedAncestor"))
                Assert.That(annotation.Boundaries, Has.Length.EqualTo(1))
                Assert.That(annotation.Boundaries[0].LineRange, Is.EqualTo({ StartLine = 1; EndLine = 1 }))
                Assert.That(annotation.Spans[0].BoundaryId, Is.EqualTo(annotation.Boundaries[0].BoundaryId))
                Assert.That(annotation.SourceRows[0].SourceReferenceId, Is.EqualTo("source-reference-new"))
                Assert.That(annotation.SourceReferences, Has.Length.EqualTo(1)))
        )

    /// Verifies that build Annotation Preserves Missing Parent Boundary From Traversal Result.
    [<Test>]
    member _.BuildAnnotationPreservesMissingParentBoundaryFromTraversalResult() =
        let newDocument = historyDocument newReference "same"

        let traversalResult =
            traverseEffectiveBranchHistory
                targetReferenceId
                DefaultMaxReferences
                [|
                    effectiveHistoryDocument newDocument (Some oldReferenceId) true
                |]

        let annotation =
            traversalResult
            |> buildFromTraversal { StartLine = 1; EndLine = 1 }
            |> assertOk

        assertValid annotation
        assertCoveredExactlyOnce 1 1 annotation

        Assert.Multiple(
            Action (fun () ->
                Assert.That(traversalResult.BoundaryKind, Is.EqualTo(Some "MissingParentReference"))
                Assert.That(annotation.Boundaries, Has.Length.EqualTo(1))
                Assert.That(annotation.Boundaries[0].LineRange, Is.EqualTo({ StartLine = 1; EndLine = 1 }))
                Assert.That(annotation.Spans[0].BoundaryId, Is.EqualTo(annotation.Boundaries[0].BoundaryId))
                Assert.That(annotation.SourceRows[0].SourceReferenceId, Is.EqualTo("source-reference-new"))
                Assert.That(annotation.SourceReferences, Has.Length.EqualTo(1)))
        )

    /// Verifies that build Annotation Preserves Based On Loop Boundary From Traversal Result.
    [<Test>]
    member _.BuildAnnotationPreservesBasedOnLoopBoundaryFromTraversalResult() =
        let oldDocument = historyDocument oldReference "same\nold"
        let newDocument = historyDocument newReference "same\nnew"

        let traversalResult =
            traverseEffectiveBranchHistory
                targetReferenceId
                DefaultMaxReferences
                [|
                    effectiveHistoryDocument newDocument (Some oldReferenceId) true
                    effectiveHistoryDocument oldDocument (Some targetReferenceId) true
                |]

        let annotation =
            traversalResult
            |> buildFromTraversal { StartLine = 1; EndLine = 2 }
            |> assertOk

        assertValid annotation
        assertCoveredExactlyOnce 1 2 annotation

        Assert.Multiple(
            Action (fun () ->
                Assert.That(traversalResult.BoundaryKind, Is.EqualTo(Some "BasedOnLoopOrRepeatedLink"))
                Assert.That(annotation.Boundaries, Has.Length.EqualTo(1))
                Assert.That(annotation.Boundaries[0].LineRange, Is.EqualTo({ StartLine = 1; EndLine = 1 }))
                Assert.That(annotation.Spans, Has.Length.EqualTo(2))
                Assert.That(annotation.Spans[0].BoundaryId, Is.EqualTo(annotation.Boundaries[0].BoundaryId))
                Assert.That(annotation.Spans[1].BoundaryId, Is.Empty)
                Assert.That(annotation.SourceRows[0].SourceReferenceId, Is.EqualTo("source-reference-old"))
                Assert.That(annotation.SourceRows[1].SourceReferenceId, Is.EqualTo("source-reference-new"))
                Assert.That(annotation.SourceReferences, Has.Length.EqualTo(2)))
        )

    /// Verifies that build Annotation Bounds Traversal Budget Reached Mid Span.
    [<Test>]
    member _.BuildAnnotationBoundsTraversalBudgetReachedMidSpan() =
        let middleReference = sourceReferenceWithType "source-reference-middle" saveReferenceId "Commit" "middle" saveDirectoryVersionId

        let annotation =
            buildWithMaxReferences
                2
                { StartLine = 1; EndLine = 2 }
                [|
                    historyDocument oldReference "same\nold"
                    historyDocument middleReference "same\nmiddle"
                    historyDocument newReference "same\nnew"
                |]
            |> assertOk

        assertValid annotation
        assertCoveredExactlyOnce 1 2 annotation

        Assert.Multiple(
            Action (fun () ->
                Assert.That(annotation.Boundaries, Has.Length.EqualTo(1))
                Assert.That(annotation.Boundaries[0].LineRange, Is.EqualTo({ StartLine = 1; EndLine = 1 }))
                Assert.That(annotation.Spans[0].BoundaryId, Is.EqualTo(annotation.Boundaries[0].BoundaryId))
                Assert.That(annotation.SourceRows[0].SourceReferenceId, Is.EqualTo("source-reference-middle"))
                Assert.That(annotation.SourceRows[1].SourceReferenceId, Is.EqualTo("source-reference-new"))
                Assert.That(annotation.SourceReferences, Has.Length.EqualTo(2)))
        )

    /// Verifies that build Annotation Ignores Pruned Different Path Ancestor When Budget Bounded.
    [<Test>]
    member _.BuildAnnotationIgnoresPrunedDifferentPathAncestorWhenBudgetBounded() =
        let middleReference = sourceReferenceWithType "source-reference-middle" saveReferenceId "Commit" "middle" saveDirectoryVersionId

        let annotation =
            buildWithMaxReferences
                2
                { StartLine = 1; EndLine = 2 }
                [|
                    historyDocumentWithPath oldReference "src/RenamedBeforeBudget.fs" "same\nrenamed"
                    historyDocument middleReference "same\nmiddle"
                    historyDocument newReference "same\nnew"
                |]
            |> assertOk

        assertValid annotation
        assertCoveredExactlyOnce 1 2 annotation

        Assert.Multiple(
            Action (fun () ->
                Assert.That(annotation.Boundaries, Has.Length.EqualTo(1))
                Assert.That(annotation.Boundaries[0].LineRange, Is.EqualTo({ StartLine = 1; EndLine = 1 }))
                Assert.That(annotation.Spans[0].BoundaryId, Is.EqualTo(annotation.Boundaries[0].BoundaryId))
                Assert.That(annotation.SourceRows[0].SourceReferenceId, Is.EqualTo("source-reference-middle"))
                Assert.That(annotation.SourceRows[1].SourceReferenceId, Is.EqualTo("source-reference-new"))
                Assert.That(annotation.SourceReferences, Has.Length.EqualTo(2)))
        )

    /// Verifies that build Annotation Rejects Unknown Target Reference Type Without Filter.
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

    /// Verifies that build Annotation Rejects Unknown Target Reference Type Past End Of File Without Filter.
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

    /// Verifies that build Annotation Rejects Unknown Source Reference Type Without Filter.
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

    /// Verifies that build Annotation Rejects Blank Used Source Reference Id.
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

    /// Verifies that build Annotation Rejects Duplicate Included Source Reference Ids.
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

    /// Verifies that build Annotation Rejects History When Last Reference Does Not Match Target Reference Id.
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

    /// Verifies that build Annotation Rejects Filtered Invalid Utf8 Reference Because Traversal Needs Content.
    [<Test>]
    member _.BuildAnnotationRejectsFilteredInvalidUtf8ReferenceBecauseTraversalNeedsContent() =
        let result =
            build
                { StartLine = 1; EndLine = 1 }
                [|
                    historyDocument oldReference "old"
                    historyDocumentBytes saveReference [| 0xC3uy; 0x28uy |]
                    historyDocument newReference "new"
                |]

        match result with
        | Ok annotation ->
            assertValid annotation
            Assert.Fail("Filtered references still participate in traversal, so invalid UTF-8 must be rejected.")
        | Error errors -> Assert.That(errors, Has.Some.Contains("invalid UTF-8"))

    /// Verifies that build Annotation Coalesces Only Contiguous Matching Source Details.
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

    /// Verifies that build Annotation Adds Boundary Span For Requested Line Past End Of File.
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

    /// Verifies that build Annotation Collapses Huge Missing Range Without Per Line State.
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

    /// Verifies that build Annotation Rejects Required Source References Above Budget.
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

    /// Verifies that build Components Does Not Collapse When Boundary State Differs.
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
                Assert.That(components.Boundaries[0].BoundaryKind, Is.EqualTo(firstBoundary.BoundaryKind))
                Assert.That(components.Boundaries[1].BoundaryKind, Is.EqualTo(secondBoundary.BoundaryKind))
                Assert.That(components.Spans, Has.Length.EqualTo(2))
                Assert.That(components.Spans[0].BoundaryId, Is.EqualTo(components.Boundaries[0].BoundaryId))
                Assert.That(components.Spans[1].BoundaryId, Is.EqualTo(components.Boundaries[1].BoundaryId)))
        )
