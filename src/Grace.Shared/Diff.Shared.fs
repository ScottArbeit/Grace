namespace Grace.Shared

open DiffPlex
open DiffPlex.Chunkers
open DiffPlex.DiffBuilder.Model
open Grace.Shared
open Grace.Shared.Dto
open Grace.Types.Types
open Grace.Shared.Utilities
open NodaTime
open System
open System.Collections.Generic
open System.IO
open System.Linq
open System.Text
open System.Threading.Tasks
open System.IO.Compression

module Diff =

    let lineChunker = Chunkers.LineChunker()
    let wordChunker = Chunkers.WordChunker()

    /// Captures sections of file changes, with prefixed and suffixed unchanged lines for context.
    let processDiffModel (diffLines: List<DiffPiece>) (includeImaginary: bool) =
        let mutable mostRecentUnchangedLines = 0
        let mutable changeIsInProcess = false
        let diffList = List<DiffPiece>()
        let diffSections = List<DiffPiece[]>()

        for i = 0 to diffLines.Count - 1 do
            let diffLine = diffLines[i]

            // If we have two consecutive unchanged lines, and we're already in the middle of a change, finish out that change
            //  by adding those two unchanged lines.
            if
                diffLine.Type = ChangeType.Unchanged
                && (i < diffLines.Count - 1)
                && (diffLines[i + 1].Type = ChangeType.Unchanged)
            then
                mostRecentUnchangedLines <- i

                if changeIsInProcess then
                    // We've hit the first spot with unchanged lines after dealing with changes, so write the
                    // unchanged lines and finish this section by saving it in DiffSections
                    diffList.Add(diffLines[mostRecentUnchangedLines])
                    diffList.Add(diffLines[mostRecentUnchangedLines + 1])
                    changeIsInProcess <- false
                    diffSections.Add(diffList.ToArray())
                    diffList.Clear()

            // We have changes to process.
            elif
                diffLine.Type = ChangeType.Deleted
                || diffLine.Type = ChangeType.Inserted
                || diffLine.Type = ChangeType.Modified
                || (diffLine.Type = ChangeType.Imaginary && includeImaginary)
            then
                if not <| changeIsInProcess then
                    // We're starting a new diff section, so flip the flag and write the most recent
                    // unchanged lines to start the section.
                    changeIsInProcess <- true

                    if mostRecentUnchangedLines > 0 then
                        diffList.Add(diffLines[mostRecentUnchangedLines])
                        diffList.Add(diffLines[mostRecentUnchangedLines + 1])
                // Write this change to the list.
                diffList.Add(diffLine)

        // Capture changes in the last couple of lines, if any.
        if changeIsInProcess then
            if diffLines[diffLines.Count - 1].Type = ChangeType.Unchanged then
                diffList.Add(diffLines[diffLines.Count - 1])

            diffSections.Add(diffList.ToArray())

        diffSections

    /// Generates the inline and side-by-side diffs for two files.
    ///
    /// NOTE: The input streams are expected to be uncompressed.
    let diffTwoFiles (fileStream1: Stream) (fileStream2: Stream) =
        task {
            use textReader1 = new StreamReader(fileStream1)
            let! fileContents1 = textReader1.ReadToEndAsync()
            do! fileStream1.DisposeAsync()

            use textReader2 = new StreamReader(fileStream2)
            let! fileContents2 = textReader2.ReadToEndAsync()
            do! fileStream2.DisposeAsync()

            let inlineDiff = DiffPlex.DiffBuilder.InlineDiffBuilder(Differ.Instance)

            let inlineDiffPaneModel =
                inlineDiff.BuildDiffModel(fileContents1, fileContents2, ignoreWhitespace = true, ignoreCase = false, chunker = lineChunker)

            let sideBySideDiff = DiffBuilder.SideBySideDiffBuilder(Differ.Instance, lineChunker, wordChunker)

            let sideBySideDiffModel = sideBySideDiff.BuildDiffModel(fileContents1, fileContents2, ignoreWhitespace = true, ignoreCase = false)

            let inlineDiffSections =
                if inlineDiffPaneModel.HasDifferences then
                    let diffLines = inlineDiffPaneModel.Lines
                    processDiffModel diffLines false
                else
                    List<DiffPiece[]>()

            let sideBySideOldSections =
                if sideBySideDiffModel.OldText.HasDifferences then
                    let diffLines = sideBySideDiffModel.OldText.Lines
                    processDiffModel diffLines true
                else
                    List<DiffPiece[]>()

            let sideBySideNewSections =
                if sideBySideDiffModel.NewText.HasDifferences then
                    let diffLines = sideBySideDiffModel.NewText.Lines
                    processDiffModel diffLines true
                else
                    List<DiffPiece[]>()

            return {| InlineDiff = inlineDiffSections; SideBySideOld = sideBySideOldSections; SideBySideNew = sideBySideNewSections |}
        }
