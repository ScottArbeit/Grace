namespace Grace.Types.ExternalEvents

open Grace.Shared
open Grace.Types.Types
open NodaTime
open Orleans
open System
open System.Text.Json
open System.Text.Json.Serialization
open System.Runtime.Serialization


[<KnownType("GetKnownTypes")>]
type CanonicalEventName =
    | AgentWorkStarted
    | AgentWorkStopped
    | ArtifactCreated
    | BranchCreated
    | BranchDeleted
    | BranchRebased
    | BranchRestored
    | BranchUpdated
    | OrganizationCreated
    | OrganizationDeleted
    | OrganizationRestored
    | OrganizationUpdated
    | OwnerCreated
    | OwnerDeleted
    | OwnerRestored
    | OwnerUpdated
    | PolicyAcknowledged
    | PolicySnapshotCreated
    | PromotionSetApplied
    | PromotionSetApplyFailed
    | PromotionSetApplyStarted
    | PromotionSetBlocked
    | PromotionSetCreated
    | PromotionSetDeleted
    | PromotionSetInputsUpdated
    | PromotionSetRecomputeFailed
    | PromotionSetRecomputeStarted
    | PromotionSetStepsUpdated
    | QueueDegraded
    | QueueInitialized
    | QueuePaused
    | QueuePolicySnapshotUpdated
    | QueuePromotionSetDequeued
    | QueuePromotionSetEnqueued
    | QueueResumed
    | ReferenceCreated
    | ReferenceDeleted
    | ReferenceRestored
    | ReferenceUpdated
    | RepositoryCreated
    | RepositoryDeleted
    | RepositoryInitialized
    | RepositoryRestored
    | RepositoryUpdated
    | ReviewCheckpointAdded
    | ReviewFindingResolved
    | ReviewNotesUpserted
    | ValidationSetCreated
    | ValidationSetDeleted
    | ValidationSetUpdated
    | ValidationRequested
    | ValidationResultRecorded
    | WorkItemCreated
    | WorkItemUpdated

    static member GetKnownTypes() = Grace.Shared.Utilities.GetKnownTypes<CanonicalEventName>()

module CanonicalEventName =
    let toString eventName =
        match eventName with
        | CanonicalEventName.AgentWorkStarted -> "grace.agent.work-started"
        | CanonicalEventName.AgentWorkStopped -> "grace.agent.work-stopped"
        | CanonicalEventName.ArtifactCreated -> "grace.artifact.created"
        | CanonicalEventName.BranchCreated -> "grace.branch.created"
        | CanonicalEventName.BranchDeleted -> "grace.branch.deleted"
        | CanonicalEventName.BranchRebased -> "grace.branch.rebased"
        | CanonicalEventName.BranchRestored -> "grace.branch.restored"
        | CanonicalEventName.BranchUpdated -> "grace.branch.updated"
        | CanonicalEventName.OrganizationCreated -> "grace.organization.created"
        | CanonicalEventName.OrganizationDeleted -> "grace.organization.deleted"
        | CanonicalEventName.OrganizationRestored -> "grace.organization.restored"
        | CanonicalEventName.OrganizationUpdated -> "grace.organization.updated"
        | CanonicalEventName.OwnerCreated -> "grace.owner.created"
        | CanonicalEventName.OwnerDeleted -> "grace.owner.deleted"
        | CanonicalEventName.OwnerRestored -> "grace.owner.restored"
        | CanonicalEventName.OwnerUpdated -> "grace.owner.updated"
        | CanonicalEventName.PolicyAcknowledged -> "grace.policy.acknowledged"
        | CanonicalEventName.PolicySnapshotCreated -> "grace.policy.snapshot-created"
        | CanonicalEventName.PromotionSetApplied -> "grace.promotion-set.applied"
        | CanonicalEventName.PromotionSetApplyFailed -> "grace.promotion-set.apply-failed"
        | CanonicalEventName.PromotionSetApplyStarted -> "grace.promotion-set.apply-started"
        | CanonicalEventName.PromotionSetBlocked -> "grace.promotion-set.blocked"
        | CanonicalEventName.PromotionSetCreated -> "grace.promotion-set.created"
        | CanonicalEventName.PromotionSetDeleted -> "grace.promotion-set.deleted"
        | CanonicalEventName.PromotionSetInputsUpdated -> "grace.promotion-set.inputs-updated"
        | CanonicalEventName.PromotionSetRecomputeFailed -> "grace.promotion-set.recompute-failed"
        | CanonicalEventName.PromotionSetRecomputeStarted -> "grace.promotion-set.recompute-started"
        | CanonicalEventName.PromotionSetStepsUpdated -> "grace.promotion-set.steps-updated"
        | CanonicalEventName.QueueDegraded -> "grace.queue.degraded"
        | CanonicalEventName.QueueInitialized -> "grace.queue.initialized"
        | CanonicalEventName.QueuePaused -> "grace.queue.paused"
        | CanonicalEventName.QueuePolicySnapshotUpdated -> "grace.queue.policy-snapshot-updated"
        | CanonicalEventName.QueuePromotionSetDequeued -> "grace.queue.promotion-set-dequeued"
        | CanonicalEventName.QueuePromotionSetEnqueued -> "grace.queue.promotion-set-enqueued"
        | CanonicalEventName.QueueResumed -> "grace.queue.resumed"
        | CanonicalEventName.ReferenceCreated -> "grace.reference.created"
        | CanonicalEventName.ReferenceDeleted -> "grace.reference.deleted"
        | CanonicalEventName.ReferenceRestored -> "grace.reference.restored"
        | CanonicalEventName.ReferenceUpdated -> "grace.reference.updated"
        | CanonicalEventName.RepositoryCreated -> "grace.repository.created"
        | CanonicalEventName.RepositoryDeleted -> "grace.repository.deleted"
        | CanonicalEventName.RepositoryInitialized -> "grace.repository.initialized"
        | CanonicalEventName.RepositoryRestored -> "grace.repository.restored"
        | CanonicalEventName.RepositoryUpdated -> "grace.repository.updated"
        | CanonicalEventName.ReviewCheckpointAdded -> "grace.review.checkpoint-added"
        | CanonicalEventName.ReviewFindingResolved -> "grace.review.finding-resolved"
        | CanonicalEventName.ReviewNotesUpserted -> "grace.review.notes-upserted"
        | CanonicalEventName.ValidationSetCreated -> "grace.validation-set.created"
        | CanonicalEventName.ValidationSetDeleted -> "grace.validation-set.deleted"
        | CanonicalEventName.ValidationSetUpdated -> "grace.validation-set.updated"
        | CanonicalEventName.ValidationRequested -> "grace.validation.requested"
        | CanonicalEventName.ValidationResultRecorded -> "grace.validation.result-recorded"
        | CanonicalEventName.WorkItemCreated -> "grace.work-item.created"
        | CanonicalEventName.WorkItemUpdated -> "grace.work-item.updated"

    let all =
        [|
            CanonicalEventName.AgentWorkStarted
            CanonicalEventName.AgentWorkStopped
            CanonicalEventName.ArtifactCreated
            CanonicalEventName.BranchCreated
            CanonicalEventName.BranchDeleted
            CanonicalEventName.BranchRebased
            CanonicalEventName.BranchRestored
            CanonicalEventName.BranchUpdated
            CanonicalEventName.OrganizationCreated
            CanonicalEventName.OrganizationDeleted
            CanonicalEventName.OrganizationRestored
            CanonicalEventName.OrganizationUpdated
            CanonicalEventName.OwnerCreated
            CanonicalEventName.OwnerDeleted
            CanonicalEventName.OwnerRestored
            CanonicalEventName.OwnerUpdated
            CanonicalEventName.PolicyAcknowledged
            CanonicalEventName.PolicySnapshotCreated
            CanonicalEventName.PromotionSetApplied
            CanonicalEventName.PromotionSetApplyFailed
            CanonicalEventName.PromotionSetApplyStarted
            CanonicalEventName.PromotionSetBlocked
            CanonicalEventName.PromotionSetCreated
            CanonicalEventName.PromotionSetDeleted
            CanonicalEventName.PromotionSetInputsUpdated
            CanonicalEventName.PromotionSetRecomputeFailed
            CanonicalEventName.PromotionSetRecomputeStarted
            CanonicalEventName.PromotionSetStepsUpdated
            CanonicalEventName.QueueDegraded
            CanonicalEventName.QueueInitialized
            CanonicalEventName.QueuePaused
            CanonicalEventName.QueuePolicySnapshotUpdated
            CanonicalEventName.QueuePromotionSetDequeued
            CanonicalEventName.QueuePromotionSetEnqueued
            CanonicalEventName.QueueResumed
            CanonicalEventName.ReferenceCreated
            CanonicalEventName.ReferenceDeleted
            CanonicalEventName.ReferenceRestored
            CanonicalEventName.ReferenceUpdated
            CanonicalEventName.RepositoryCreated
            CanonicalEventName.RepositoryDeleted
            CanonicalEventName.RepositoryInitialized
            CanonicalEventName.RepositoryRestored
            CanonicalEventName.RepositoryUpdated
            CanonicalEventName.ReviewCheckpointAdded
            CanonicalEventName.ReviewFindingResolved
            CanonicalEventName.ReviewNotesUpserted
            CanonicalEventName.ValidationSetCreated
            CanonicalEventName.ValidationSetDeleted
            CanonicalEventName.ValidationSetUpdated
            CanonicalEventName.ValidationRequested
            CanonicalEventName.ValidationResultRecorded
            CanonicalEventName.WorkItemCreated
            CanonicalEventName.WorkItemUpdated
        |]

    let private parseIndex =
        lazy
            (all
             |> Array.map (fun eventName -> toString eventName, eventName)
             |> Map.ofArray)

    let tryParse (value: string) : CanonicalEventName option =
        if String.IsNullOrWhiteSpace value then
            None
        else
            parseIndex.Value |> Map.tryFind (value.Trim())

    let parse (value: string) : CanonicalEventName =
        match tryParse value with
        | Some eventName -> eventName
        | None -> invalidArg (nameof value) $"Unknown canonical event name: {value}"

    let publishedNames = all |> Array.map toString
    let publishedNameSet = publishedNames |> Set.ofArray
    let isPublished (value: string) = publishedNameSet.Contains value

type CanonicalActorIdentity = { ActorType: string; ActorId: string }

type CanonicalDocumentSize = { ByteCount: int; LimitBytes: int }

type CanonicalExternalEventEnvelope =
    {
        EventId: string
        EventName: string
        EventVersion: int
        OccurredAt: Instant
        CorrelationId: CorrelationId
        OwnerId: OwnerId option
        OrganizationId: OrganizationId option
        RepositoryId: RepositoryId option
        ActorType: string option
        ActorId: string option
        Payload: JsonElement
    }

type ReferenceLinkSummary = { Kind: string; ReferenceId: ReferenceId option; PromotionSetId: PromotionSetId option }

type LinksSummary = { Count: int; Links: ReferenceLinkSummary list }

[<GenerateSerializer>]
type MatchedRule = { RuleIndex: int; EventNames: string list; BranchNameGlob: string }

type RuleMatchSummary = { EventNames: string list; BranchNameGlob: string }

type RulesSummary = { RuleCount: int; Rules: RuleMatchSummary list }

[<GenerateSerializer>]
type ValidationSummary = { Name: string; Version: string; ExecutionMode: string; RequiredForApply: bool }

[<GenerateSerializer>]
type ValidationsSummary = { ValidationCount: int; Validations: ValidationSummary list }

type BaselineDriftReackThresholdSummary = { ChurnLines: int; FilesTouched: int }

type RuleSummary =
    {
        HumanReviewRuleCount: int
        DeepAnalysisRuleCount: int
        SensitivePathCount: int
        RedactionEnabled: bool
        RedactionPatternCount: int
        DenylistPathCount: int
        DefaultRequireHumanReview: bool
        DeepAnalysisDefaultEnabled: bool
        DeepAnalysisMaxTokens: int
        TriageDefaultEnabled: bool
        TriageMaxTokens: int
        QueueOnFailure: string
        BaselineDriftReackThreshold: BaselineDriftReackThresholdSummary
    }

type StepConflictCounts = { NoConflicts: int; AutoResolved: int; BlockedPendingReview: int; Failed: int }

type OriginalPromotionSummary = { BranchId: BranchId; ReferenceId: ReferenceId; DirectoryVersionId: DirectoryVersionId }

type StepSummary =
    {
        StepId: PromotionSetStepId
        Order: int
        OriginalPromotion: OriginalPromotionSummary
        OriginalBasePromotionReferenceId: ReferenceId option
        OriginalBaseDirectoryVersionId: DirectoryVersionId option
        ComputedAgainstBaseDirectoryVersionId: DirectoryVersionId option
        AppliedDirectoryVersionId: DirectoryVersionId option
        ConflictStatus: string
        ConflictSummaryArtifactId: ArtifactId option
    }

type StepsSummary = { StepCount: int; ConflictCounts: StepConflictCounts; Truncated: bool; Steps: StepSummary list }

module CanonicalExternalEventEnvelope =
    [<Literal>]
    let EventVersion = 1

    [<Literal>]
    let MaxDocumentBytes = 1_000_000

    let canonicalJsonSerializerOptions =
        let options = JsonSerializerOptions(Grace.Shared.Constants.JsonSerializerOptions)
        options.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
        options.DictionaryKeyPolicy <- JsonNamingPolicy.CamelCase
        options.WriteIndented <- false
        options

    let private normalizeGuid (guidValue: Guid) : Guid option = if guidValue = Guid.Empty then None else Some guidValue

    let private normalizeString (value: string) : string option = if String.IsNullOrWhiteSpace value then None else Some(value.Trim())

    let toPayloadElement<'T> payload =
        let payloadElement = JsonSerializer.SerializeToElement(payload, canonicalJsonSerializerOptions)

        if payloadElement.ValueKind <> JsonValueKind.Object then
            invalidArg (nameof payload) "Canonical external event payloads must serialize as JSON objects."

        payloadElement

    let emptyPayload = JsonSerializer.SerializeToElement(box {|  |}, canonicalJsonSerializerOptions)

    let tryCreate
        (eventName: CanonicalEventName)
        (occurredAt: Instant)
        (correlationId: CorrelationId)
        (ownerId: OwnerId)
        (organizationId: OrganizationId)
        (repositoryId: RepositoryId)
        (actorIdentity: CanonicalActorIdentity)
        (payload: JsonElement)
        =
        match normalizeString actorIdentity.ActorType, normalizeString actorIdentity.ActorId with
        | Some actorType, Some actorId when payload.ValueKind = JsonValueKind.Object ->
            Ok
                {
                    EventId = $"{actorType}_{actorId}_{correlationId}"
                    EventName = CanonicalEventName.toString eventName
                    EventVersion = EventVersion
                    OccurredAt = occurredAt
                    CorrelationId = correlationId
                    OwnerId = normalizeGuid ownerId
                    OrganizationId = normalizeGuid organizationId
                    RepositoryId = normalizeGuid repositoryId
                    ActorType = Some actorType
                    ActorId = Some actorId
                    Payload = payload
                }
        | Some _, Some _ -> Error "Canonical external event payloads must be JSON objects."
        | _ -> Error "Canonical external event publication requires non-empty actorType and actorId."

    let serialize (envelope: CanonicalExternalEventEnvelope) = JsonSerializer.Serialize(envelope, canonicalJsonSerializerOptions)

    let serializeToUtf8Bytes (envelope: CanonicalExternalEventEnvelope) = JsonSerializer.SerializeToUtf8Bytes(envelope, canonicalJsonSerializerOptions)

    let measureSize (envelope: CanonicalExternalEventEnvelope) =
        let byteCount = serializeToUtf8Bytes envelope |> Array.length
        { ByteCount = byteCount; LimitBytes = MaxDocumentBytes }

    let isWithinSizeLimit (envelope: CanonicalExternalEventEnvelope) =
        let size = measureSize envelope
        size.ByteCount < size.LimitBytes
