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
        let paths = [ "src/App.fs"; "docs/readme.md"; "src/Utils.fs"; "docs/guide.md" ]
        let shuffled = [ "docs/guide.md"; "src/Utils.fs"; "docs/readme.md"; "src/App.fs" ]
        let evidence = paths |> List.map evidenceSummary

        let first = buildChapters paths evidence
        let second = buildChapters shuffled evidence

        let matches = first = second
        Assert.That(matches, Is.True)

    [<Test>]
    member _.BaselineDriftTargetsAffectedChaptersAndFindings() =
        let threshold = { ChurnLines = 1; FilesTouched = 1 }
        let policy = { PolicySnapshot.Default with Rules = { PolicySnapshot.Default.Rules with ApprovalRules = { BaselineDriftReackThreshold = threshold } } }

        let changedPath = "src/Auth.fs"

        let riskProfile =
            { DeterministicRiskProfile.Default with
                ChangedPaths = [ { RelativePath = changedPath; ChangeType = PathChangeType.Modified } ]
                Churn = { LinesAdded = 10; LinesRemoved = 0; FilesChanged = 1; RenamedCount = 0 } }

        let chapters = buildChapters [ changedPath ] [ evidenceSummary changedPath ]
        let chapterId = chapters |> List.head |> (fun chapter -> chapter.ChapterId)

        let findingId = Guid.NewGuid()

        let finding =
            { FindingId = findingId
              Severity = FindingSeverity.High
              Category = FindingCategory.Security
              Description = "Risk"
              Rationale = "Rationale"
              RequiredActionType = "Review"
              EvidenceReferences = [ { RelativePath = changedPath; StartLine = 1; EndLine = 2 } ]
              ResolutionState = FindingResolutionState.Open
              ResolvedBy = None
              ResolvedAt = None
              ResolutionNote = None }

        let result = BaselineDrift.evaluate policy riskProfile chapters [ finding ]

        Assert.That(result.IsMeaningful, Is.True)
        Assert.That(result.AffectedChapterIds, Does.Contain(chapterId))
        Assert.That(result.AffectedFindingIds, Does.Contain(findingId))
