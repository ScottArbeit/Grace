namespace Grace.Server.Tests

open Grace.Shared
open Grace.Shared.ReviewPacket
open Grace.Types.Policy
open Grace.Types.Review
open Grace.Types.Types
open NUnit.Framework
open System

[<Parallelizable(ParallelScope.All)>]
type ReviewPacketDeterminism() =
    let evidenceSummary path = { RelativePath = path; StartLine = 1; EndLine = 2; Score = 1.0; Reasons = [] }

    [<Test>]
    member _.ChapteringIsDeterministicAcrossOrdering() =
        let paths =
            [
                "src/App.fs"
                "docs/readme.md"
                "src/Utils.fs"
                "docs/guide.md"
            ]

        let shuffled =
            [
                "docs/guide.md"
                "src/Utils.fs"
                "docs/readme.md"
                "src/App.fs"
            ]

        let evidence = paths |> List.map evidenceSummary

        let first = buildChapters paths evidence
        let second = buildChapters shuffled evidence

        let matches = first = second
        Assert.That(matches, Is.True)

    [<Test>]
    member _.ChaptersUseTopLevelPathSegment() =
        let paths =
            [
                "README.md"
                "src/App.fs"
                "src/Utils.fs"
                "docs/guide.md"
            ]

        let evidence = paths |> List.map evidenceSummary

        let chapters = buildChapters paths evidence

        let readmeChapter =
            chapters
            |> List.find (fun chapter -> chapter.Title = "README.md")

        let srcChapter =
            chapters
            |> List.find (fun chapter -> chapter.Title = "src")

        let docsChapter =
            chapters
            |> List.find (fun chapter -> chapter.Title = "docs")

        let readmeMatch = readmeChapter.Paths = [ "README.md" ]
        let srcMatch = srcChapter.Paths = [ "src/App.fs"; "src/Utils.fs" ]
        let docsMatch = docsChapter.Paths = [ "docs/guide.md" ]

        Assert.That(readmeMatch, Is.True)
        Assert.That(srcMatch, Is.True)
        Assert.That(docsMatch, Is.True)

    [<Test>]
    member _.ChapterIdsStayStableWhenEvidenceOrderingChanges() =
        let paths = [ "src/App.fs"; "docs/readme.md" ]
        let evidenceOne = paths |> List.map evidenceSummary

        let evidenceTwo =
            [ "docs/readme.md"; "src/App.fs" ]
            |> List.map evidenceSummary

        let chapterIdsFirst =
            buildChapters paths evidenceOne
            |> List.map (fun chapter -> chapter.ChapterId)

        let chapterIdsSecond =
            buildChapters paths evidenceTwo
            |> List.map (fun chapter -> chapter.ChapterId)

        let idsMatch = chapterIdsFirst = chapterIdsSecond
        Assert.That(idsMatch, Is.True)

    [<Test>]
    member _.BaselineDriftTargetsAffectedChaptersAndFindings() =
        let threshold = { ChurnLines = 1; FilesTouched = 1 }
        let policy = { PolicySnapshot.Default with Rules = { PolicySnapshot.Default.Rules with ApprovalRules = { BaselineDriftReackThreshold = threshold } } }

        let changedPath = "src/Auth.fs"

        let riskProfile =
            { DeterministicRiskProfile.Default with
                ChangedPaths =
                    [
                        { RelativePath = changedPath; ChangeType = PathChangeType.Modified }
                    ]
                Churn = { LinesAdded = 10; LinesRemoved = 0; FilesChanged = 1; RenamedCount = 0 }
            }

        let chapters =
            buildChapters [ changedPath ] [
                evidenceSummary changedPath
            ]

        let chapterId =
            chapters
            |> List.head
            |> (fun chapter -> chapter.ChapterId)

        let findingId = Guid.NewGuid()

        let finding =
            {
                FindingId = findingId
                Severity = FindingSeverity.High
                Category = FindingCategory.Security
                Description = "Risk"
                Rationale = "Rationale"
                RequiredActionType = "Review"
                EvidenceReferences =
                    [
                        { RelativePath = changedPath; StartLine = 1; EndLine = 2 }
                    ]
                ResolutionState = FindingResolutionState.Open
                ResolvedBy = None
                ResolvedAt = None
                ResolutionNote = None
            }

        let result = BaselineDrift.evaluate policy riskProfile chapters [ finding ]

        Assert.That(result.IsMeaningful, Is.True)
        Assert.That(result.AffectedChapterIds, Does.Contain(chapterId))
        Assert.That(result.AffectedFindingIds, Does.Contain(findingId))

    [<Test>]
    member _.BaselineDriftBelowThresholdIsNotMeaningful() =
        let threshold = { ChurnLines = 10; FilesTouched = 5 }
        let policy = { PolicySnapshot.Default with Rules = { PolicySnapshot.Default.Rules with ApprovalRules = { BaselineDriftReackThreshold = threshold } } }

        let riskProfile =
            { DeterministicRiskProfile.Default with
                ChangedPaths =
                    [
                        { RelativePath = "src/Auth.fs"; ChangeType = PathChangeType.Modified }
                    ]
                Churn = { LinesAdded = 1; LinesRemoved = 1; FilesChanged = 1; RenamedCount = 0 }
            }

        let chapters =
            buildChapters [ "src/Auth.fs" ] [
                evidenceSummary "src/Auth.fs"
            ]

        let result = BaselineDrift.evaluate policy riskProfile chapters []

        Assert.That(result.IsMeaningful, Is.False)

    [<Test>]
    member _.BaselineDriftSkipsChaptersAndFindingsWithoutMatchingPaths() =
        let threshold = { ChurnLines = 1; FilesTouched = 1 }
        let policy = { PolicySnapshot.Default with Rules = { PolicySnapshot.Default.Rules with ApprovalRules = { BaselineDriftReackThreshold = threshold } } }

        let riskProfile =
            { DeterministicRiskProfile.Default with
                ChangedPaths =
                    [
                        { RelativePath = "src/Auth.fs"; ChangeType = PathChangeType.Modified }
                    ]
                Churn = { LinesAdded = 5; LinesRemoved = 0; FilesChanged = 1; RenamedCount = 0 }
            }

        let chapters =
            buildChapters [ "docs/readme.md" ] [
                evidenceSummary "docs/readme.md"
            ]

        let finding =
            {
                FindingId = Guid.NewGuid()
                Severity = FindingSeverity.Low
                Category = FindingCategory.Tests
                Description = "No match"
                Rationale = "No match"
                RequiredActionType = "Review"
                EvidenceReferences =
                    [
                        { RelativePath = "docs/readme.md"; StartLine = 1; EndLine = 2 }
                    ]
                ResolutionState = FindingResolutionState.Open
                ResolvedBy = None
                ResolvedAt = None
                ResolutionNote = None
            }

        let result = BaselineDrift.evaluate policy riskProfile chapters [ finding ]

        Assert.That(result.AffectedChapterIds, Is.Empty)
        Assert.That(result.AffectedFindingIds, Is.Empty)

    [<Test>]
    member _.BaselineDriftIsMeaningfulAtThreshold() =
        let threshold = { ChurnLines = 4; FilesTouched = 2 }
        let policy = { PolicySnapshot.Default with Rules = { PolicySnapshot.Default.Rules with ApprovalRules = { BaselineDriftReackThreshold = threshold } } }

        let riskProfile =
            { DeterministicRiskProfile.Default with
                ChangedPaths =
                    [
                        { RelativePath = "src/Auth.fs"; ChangeType = PathChangeType.Modified }
                    ]
                Churn = { LinesAdded = 4; LinesRemoved = 0; FilesChanged = 0; RenamedCount = 0 }
            }

        let chapters =
            buildChapters [ "src/Auth.fs" ] [
                evidenceSummary "src/Auth.fs"
            ]

        let result = BaselineDrift.evaluate policy riskProfile chapters []

        Assert.That(result.IsMeaningful, Is.True)
