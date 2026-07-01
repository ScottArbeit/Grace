namespace Grace.Shared

open Grace.Types.Policy
open Grace.Types.Review
open Grace.Types.Common

/// Contains baseline drift helpers.
module BaselineDrift =
    /// Represents the baseline drift result contract.
    type BaselineDriftResult = { IsMeaningful: bool; AffectedPaths: RelativePath list; AffectedChapterIds: ChapterId list; AffectedFindingIds: FindingId list }

    /// Compares baseline and working values and reports whether drift is present.
    let evaluate (policy: PolicySnapshot) (riskProfile: DeterministicRiskProfile) (chapters: Chapter list) (findings: Finding list) =
        let churnLines =
            riskProfile.Churn.LinesAdded
            + riskProfile.Churn.LinesRemoved

        let filesTouched = riskProfile.Churn.FilesChanged
        let threshold = policy.Rules.ApprovalRules.BaselineDriftReackThreshold

        let isMeaningful =
            churnLines >= threshold.ChurnLines
            || filesTouched >= threshold.FilesTouched

        let affectedPaths =
            riskProfile.ChangedPaths
            |> List.map (fun path -> path.RelativePath)
            |> List.distinct

        let affectedChapters =
            chapters
            |> List.filter (fun chapter ->
                chapter.Paths
                |> List.exists (fun path -> affectedPaths |> List.contains path))
            |> List.map (fun chapter -> chapter.ChapterId)

        let affectedFindings =
            findings
            |> List.filter (fun finding ->
                finding.EvidenceReferences
                |> List.exists (fun evidence ->
                    affectedPaths
                    |> List.contains evidence.RelativePath))
            |> List.map (fun finding -> finding.FindingId)

        { IsMeaningful = isMeaningful; AffectedPaths = affectedPaths; AffectedChapterIds = affectedChapters; AffectedFindingIds = affectedFindings }
