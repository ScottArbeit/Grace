namespace Grace.Shared

open Grace.Shared.Utilities
open Grace.Types.Policy
open Grace.Types.PromotionGroup
open Grace.Types.Review
open Grace.Types.Types
open NodaTime
open System
open System.Security.Cryptography
open System.Text

module ReviewPacket =
    let private normalizePath (relativePath: RelativePath) = relativePath.Replace('\\', '/')

    let private getChapterKey (relativePath: RelativePath) =
        let normalized = normalizePath relativePath
        let segments = normalized.Split([| '/' |], StringSplitOptions.RemoveEmptyEntries)

        if segments.Length = 0 then normalized else segments[0]

    let private computeChapterId (paths: RelativePath list) =
        let signature = paths |> List.sort |> String.concat "|"
        let bytes = Encoding.UTF8.GetBytes(signature)
        let hashBytes = SHA256.HashData(bytes)
        Sha256Hash(byteArrayToString hashBytes)

    let buildChapters (paths: RelativePath list) (evidence: EvidenceSliceSummary list) =
        paths
        |> List.groupBy getChapterKey
        |> List.sortBy fst
        |> List.map (fun (chapterKey, chapterPaths) ->
            let orderedPaths = chapterPaths |> List.distinct |> List.sort

            let chapterEvidence =
                evidence
                |> List.filter (fun slice -> orderedPaths |> List.contains slice.RelativePath)

            {
                ChapterId = computeChapterId orderedPaths
                Title = chapterKey
                Summary = String.Empty
                Paths = orderedPaths
                FindingIds = []
                Evidence = chapterEvidence
            })

    let assemblePacket
        (reviewPacketId: ReviewPacketId)
        (ownerId: OwnerId)
        (organizationId: OrganizationId)
        (repositoryId: RepositoryId)
        (candidateId: CandidateId option)
        (promotionGroupId: PromotionGroupId option)
        (policySnapshotId: PolicySnapshotId)
        (riskProfile: DeterministicRiskProfile option)
        (evidenceSummary: EvidenceSetSummary option)
        (createdAt: Instant)
        =
        let paths =
            riskProfile
            |> Option.map (fun profile ->
                profile.ChangedPaths
                |> List.map (fun path -> path.RelativePath))
            |> Option.defaultValue []

        let evidence =
            evidenceSummary
            |> Option.map (fun summary -> summary.SliceSummaries)
            |> Option.defaultValue []

        { ReviewPacket.Default with
            ReviewPacketId = reviewPacketId
            OwnerId = ownerId
            OrganizationId = organizationId
            RepositoryId = repositoryId
            CandidateId = candidateId
            PromotionGroupId = promotionGroupId
            PolicySnapshotId = policySnapshotId
            Chapters = buildChapters paths evidence
            EvidenceSetSummary = evidenceSummary
            CreatedAt = createdAt
        }
