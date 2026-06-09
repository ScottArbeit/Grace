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
    let tagReferenceId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc")
    let oldDirectoryVersionId = Guid.Parse("44444444-4444-4444-4444-444444444444")
    let newDirectoryVersionId = Guid.Parse("55555555-5555-5555-5555-555555555555")
    let saveDirectoryVersionId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")
    let tagDirectoryVersionId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd")

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
    let tagReference = sourceReferenceWithType "source-reference-tag" tagReferenceId "Tag" "tag" tagDirectoryVersionId

    let historyDocument sourceReference content = { SourceReference = sourceReference; Path = "src/App.fs"; Content = bytes content }

    let historyDocumentWithPath sourceReference path content = { SourceReference = sourceReference; Path = path; Content = bytes content }

    let historyDocumentBytes sourceReference content = { SourceReference = sourceReference; Path = "src/App.fs"; Content = content }

    let effectiveHistoryDocument document basedOnReferenceId isAuthorized =
        { Document = document; BasedOnReferenceId = basedOnReferenceId; IsAuthorized = isAuthorized }

    let build requestedRange history =
        buildAnnotation (requestedRange, targetReferenceId, "src/App.fs", [| ReferenceType.Commit |], DefaultMaxReferences, true, history)

    let buildWithoutLineText requestedRange history =
        buildAnnotation (requestedRange, targetReferenceId, "src/App.fs", [| ReferenceType.Commit |], DefaultMaxReferences, false, history)

    let buildWithoutReferenceTypeFilter requestedRange history =
        buildAnnotation (requestedRange, targetReferenceId, "src/App.fs", Array.empty, DefaultMaxReferences, true, history)

    let buildWithMaxReferences maxReferences requestedRange history =
        buildAnnotation (requestedRange, targetReferenceId, "src/App.fs", [| ReferenceType.Commit |], maxReferences, true, history)

    let buildWithReferenceTypes referenceTypes requestedRange history =
        buildAnnotation (requestedRange, targetReferenceId, "src/App.fs", referenceTypes, DefaultMaxReferences, true, history)

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

    let historySourceReferenceIds result =
        result.History
        |> Array.map (fun document -> document.SourceReference.SourceReferenceId)

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
