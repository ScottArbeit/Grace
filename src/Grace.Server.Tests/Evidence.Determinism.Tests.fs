namespace Grace.Server.Tests

open DiffPlex.DiffBuilder.Model
open Grace.Shared
open Grace.Types.Diff
open Grace.Types.Review
open Grace.Types.Types
open NUnit.Framework
open NodaTime
open System
open System.Collections.Generic

[<Parallelizable(ParallelScope.All)>]
type EvidenceDeterminism() =
    let instant = Instant.FromUtc(2025, 1, 1, 0, 0)

    let buildSection (line: string) (position: int) = [| DiffPiece(line, ChangeType.Modified, Nullable<int>(position)) |]

    let buildFileDiff (relativePath: string) (lines: (string * int) list) =
        let inlineDiff = List<DiffPiece[]>()

        for (text, position) in lines do
            inlineDiff.Add(buildSection text position)

        FileDiff.Create relativePath (Sha256Hash "sha1") instant (Sha256Hash "sha2") instant false inlineDiff (List<DiffPiece[]>()) (List<DiffPiece[]>())

    let buildDiff (fileDiffs: FileDiff list) = { DiffDto.Default with HasDifferences = true; FileDiffs = List<FileDiff>(fileDiffs) }

    [<Test>]
    member _.EvidenceSelectionIsDeterministicAcrossFileOrdering() =
        let fileA = buildFileDiff "a.txt" [ "alpha", 1; "beta", 2 ]
        let fileB = buildFileDiff "b.txt" [ "gamma", 1 ]

        let budget = { MaxFiles = 5; MaxHunksPerFile = 5; MaxLinesPerHunk = 5; MaxTotalBytes = 4096; MaxTokens = 2000 }

        let diffOne = buildDiff [ fileB; fileA ]
        let diffTwo = buildDiff [ fileA; fileB ]

        let evidenceOne, summaryOne = Evidence.buildEvidenceSet EvidenceStage.Triage budget None [] diffOne
        let evidenceTwo, summaryTwo = Evidence.buildEvidenceSet EvidenceStage.Triage budget None [] diffTwo

        let selectedFilesMatch = summaryOne.SelectedFiles = summaryTwo.SelectedFiles
        let summariesMatch = summaryOne.SliceSummaries = summaryTwo.SliceSummaries
        let slicesMatch = evidenceOne.Slices = evidenceTwo.Slices

        Assert.That(selectedFilesMatch, Is.True)
        Assert.That(summariesMatch, Is.True)
        Assert.That(slicesMatch, Is.True)

    [<Test>]
    member _.EvidenceSelectionRespectsSortedBudgetOrdering() =
        let fileA = buildFileDiff "a.txt" [ "alpha", 1 ]
        let fileB = buildFileDiff "b.txt" [ "gamma", 1 ]

        let budget = { MaxFiles = 1; MaxHunksPerFile = 5; MaxLinesPerHunk = 5; MaxTotalBytes = 4096; MaxTokens = 2000 }

        let diff = buildDiff [ fileB; fileA ]
        let _, summary = Evidence.buildEvidenceSet EvidenceStage.Triage budget None [] diff

        let selected = summary.SelectedFiles = [ "a.txt" ]
        Assert.That(selected, Is.True)
