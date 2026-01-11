namespace Grace.Server

open Grace.Server.ApplicationContext
open Grace.Shared.Utilities
open Grace.Types.Policy
open Grace.Types.Review
open Grace.Types.Types
open Microsoft.Extensions.Caching.Memory
open System
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks

module ReviewAnalysis =
    type CachedTriage = { Result: ReviewModels.TriageResult; Receipt: AnalysisReceipt }

    let triagePromptTemplateVersion = "v1"
    let deepPromptTemplateVersion = "v1"
    let private moreContextMarker = "NEED_MORE_CONTEXT"

    let private hashString (value: string) =
        let bytes = Encoding.UTF8.GetBytes(value)
        let hashBytes = SHA256.HashData(bytes)
        Sha256Hash(byteArrayToString hashBytes)

    let private computeEvidenceHash (riskProfile: DeterministicRiskProfile) (evidenceSummary: EvidenceSetSummary) =
        hashString (serialize (riskProfile, evidenceSummary))

    let private buildCacheKey (policySnapshotId: PolicySnapshotId) (evidenceHash: Sha256Hash) (modelId: string) (promptVersion: string) =
        $"{policySnapshotId}|{evidenceHash}|{modelId}|{promptVersion}"

    let private tryGetCachedTriage (cache: IMemoryCache) (cacheKey: string) =
        let mutable cached: obj = null

        if cache.TryGetValue(cacheKey, &cached) then
            cached :?> CachedTriage |> Some
        else
            None

    let private storeCachedTriage (cache: IMemoryCache) (cacheKey: string) (cached: CachedTriage) =
        cache.Set(cacheKey, cached, MemoryCacheEntryOptions(AbsoluteExpirationRelativeToNow = Nullable(TimeSpan.FromHours(12.0))))
        |> ignore

    let runTriage
        (provider: ReviewModels.IReviewModelProvider)
        (policySnapshot: PolicySnapshot)
        (riskProfile: DeterministicRiskProfile)
        (evidence: EvidenceSet)
        (evidenceSummary: EvidenceSetSummary)
        (triggerReasons: string list)
        (principal: UserId)
        =
        task {
            let cache = memoryCache
            let evidenceHash = computeEvidenceHash riskProfile evidenceSummary
            let cacheKey = buildCacheKey policySnapshot.PolicySnapshotId evidenceHash provider.TriageModelId triagePromptTemplateVersion

            match tryGetCachedTriage cache cacheKey with
            | Some cached -> return cached.Result, cached.Receipt
            | None ->
                let! triageResult = provider.RunTriage { PolicySnapshot = policySnapshot; RiskProfile = riskProfile; Evidence = evidence }

                let outputHash = hashString (serialize triageResult)

                let receipt =
                    {
                        AnalysisReceiptId = Guid.NewGuid()
                        Stage = EvidenceStage.Triage
                        PolicySnapshotId = policySnapshot.PolicySnapshotId
                        EvidenceHash = evidenceHash
                        EvidenceSummary = evidenceSummary
                        ModelId = provider.TriageModelId
                        MaxTokens = evidence.Budget.MaxTokens
                        OutputHash = outputHash
                        TriggerReasons = triggerReasons
                        CreatedAt = getCurrentInstant ()
                        Principal = principal
                    }

                storeCachedTriage cache cacheKey { Result = triageResult; Receipt = receipt }

                return triageResult, receipt
        }

    type CachedDeepReview = { Packet: ReviewPacket; Receipts: AnalysisReceipt list }

    let private requiresMoreContext (packet: ReviewPacket) =
        not (String.IsNullOrWhiteSpace packet.Summary)
        && packet.Summary.IndexOf(moreContextMarker, StringComparison.OrdinalIgnoreCase)
           >= 0

    let runDeepReview
        (provider: ReviewModels.IReviewModelProvider)
        (policySnapshot: PolicySnapshot)
        (riskProfile: DeterministicRiskProfile)
        (evidence: EvidenceSet)
        (evidenceSummary: EvidenceSetSummary)
        (triggerReasons: string list)
        (principal: UserId)
        (progressiveEvidence: (unit -> EvidenceSet * EvidenceSetSummary) option)
        =
        task {
            let cache = memoryCache
            let initialEvidenceHash = computeEvidenceHash riskProfile evidenceSummary
            let cacheKey = buildCacheKey policySnapshot.PolicySnapshotId initialEvidenceHash provider.DeepModelId deepPromptTemplateVersion

            let mutable cached: obj = null

            if cache.TryGetValue(cacheKey, &cached) then
                let cachedDeep = cached :?> CachedDeepReview
                return cachedDeep.Packet, cachedDeep.Receipts
            else
                let! deepPacket =
                    provider.RunDeepReview { PolicySnapshot = policySnapshot; RiskProfile = riskProfile; Evidence = evidence; WorkItemContext = None }

                let outputHash = hashString (serialize deepPacket)

                let receipt =
                    {
                        AnalysisReceiptId = Guid.NewGuid()
                        Stage = EvidenceStage.Deep
                        PolicySnapshotId = policySnapshot.PolicySnapshotId
                        EvidenceHash = initialEvidenceHash
                        EvidenceSummary = evidenceSummary
                        ModelId = provider.DeepModelId
                        MaxTokens = evidence.Budget.MaxTokens
                        OutputHash = outputHash
                        TriggerReasons = triggerReasons
                        CreatedAt = getCurrentInstant ()
                        Principal = principal
                    }

                match progressiveEvidence with
                | Some getMoreEvidence when requiresMoreContext deepPacket ->
                    let additionalEvidence, additionalSummary = getMoreEvidence ()

                    let! refinedPacket =
                        provider.RunDeepReview
                            { PolicySnapshot = policySnapshot; RiskProfile = riskProfile; Evidence = additionalEvidence; WorkItemContext = None }

                    let refinedHash = hashString (serialize refinedPacket)
                    let additionalEvidenceHash = computeEvidenceHash riskProfile additionalSummary

                    let followupReceipt =
                        {
                            AnalysisReceiptId = Guid.NewGuid()
                            Stage = EvidenceStage.Deep
                            PolicySnapshotId = policySnapshot.PolicySnapshotId
                            EvidenceHash = additionalEvidenceHash
                            EvidenceSummary = additionalSummary
                            ModelId = provider.DeepModelId
                            MaxTokens = additionalEvidence.Budget.MaxTokens
                            OutputHash = refinedHash
                            TriggerReasons = triggerReasons
                            CreatedAt = getCurrentInstant ()
                            Principal = principal
                        }

                    cache.Set(cacheKey, { Packet = refinedPacket; Receipts = [ receipt; followupReceipt ] })
                    |> ignore

                    return refinedPacket, [ receipt; followupReceipt ]
                | _ ->
                    cache.Set(cacheKey, { Packet = deepPacket; Receipts = [ receipt ] })
                    |> ignore

                    return deepPacket, [ receipt ]
        }
