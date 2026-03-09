namespace Grace.Types.ExternalEvents

open System


type ExternalEventClassification =
    | PublishCanonical
    | PublishDerived
    | InternalOnly

type ExternalEventRegistryEntry =
    {
        RawEventCase: string
        Classification: ExternalEventClassification
        PublishedEventNames: CanonicalEventName list
        EventVersion: int option
        ActorTypeRule: string option
        ActorIdRule: string option
        Rationale: string
        RequiredPayloadFields: string list
        OverflowStrategy: string option
        SourceResolutionRule: string
        Notes: string list
    }

module RawEventCase =
    let owner caseName = $"OwnerEventType.{caseName}"
    let organization caseName = $"OrganizationEventType.{caseName}"
    let repository caseName = $"RepositoryEventType.{caseName}"
    let branch caseName = $"BranchEventType.{caseName}"
    let reference caseName = $"ReferenceEventType.{caseName}"
    let directoryVersion caseName = $"DirectoryVersionEventType.{caseName}"
    let workItem caseName = $"WorkItemEventType.{caseName}"
    let policy caseName = $"PolicyEventType.{caseName}"
    let queue caseName = $"PromotionQueueEventType.{caseName}"
    let promotionSet caseName = $"PromotionSetEventType.{caseName}"
    let validationSet caseName = $"ValidationSetEventType.{caseName}"
    let validationResult caseName = $"ValidationResultEventType.{caseName}"
    let review caseName = $"ReviewEventType.{caseName}"
    let artifact caseName = $"ArtifactEventType.{caseName}"
    let derived caseName = $"Derived.{caseName}"
    let runtime caseName = $"Runtime.{caseName}"

module Registry =
    let private published actorType actorId rationale payloadFields sourceRule notes eventNames rawEventCase =
        {
            RawEventCase = rawEventCase
            Classification = PublishCanonical
            PublishedEventNames = eventNames
            EventVersion = Some CanonicalExternalEventEnvelope.EventVersion
            ActorTypeRule = Some actorType
            ActorIdRule = Some actorId
            Rationale = rationale
            RequiredPayloadFields = payloadFields
            OverflowStrategy = None
            SourceResolutionRule = sourceRule
            Notes = notes
        }

    let private derived actorType actorId rationale payloadFields sourceRule notes eventNames rawEventCase =
        {
            RawEventCase = rawEventCase
            Classification = PublishDerived
            PublishedEventNames = eventNames
            EventVersion = Some CanonicalExternalEventEnvelope.EventVersion
            ActorTypeRule = Some actorType
            ActorIdRule = Some actorId
            Rationale = rationale
            RequiredPayloadFields = payloadFields
            OverflowStrategy = None
            SourceResolutionRule = sourceRule
            Notes = notes
        }

    let private internalOnly rationale sourceRule notes rawEventCase =
        {
            RawEventCase = rawEventCase
            Classification = InternalOnly
            PublishedEventNames = []
            EventVersion = None
            ActorTypeRule = None
            ActorIdRule = None
            Rationale = rationale
            RequiredPayloadFields = []
            OverflowStrategy = None
            SourceResolutionRule = sourceRule
            Notes = notes
        }

    let private publishedForCases caseBuilder caseNames actorType actorId rationale payloadFields sourceRule notes eventName =
        caseNames
        |> List.map (fun caseName -> published actorType actorId rationale payloadFields sourceRule notes [ eventName ] (caseBuilder caseName))

    let private internalOnlyForCases caseBuilder caseNames rationale sourceRule notes =
        caseNames
        |> List.map (fun caseName -> internalOnly rationale sourceRule notes (caseBuilder caseName))

    let all =
        [
            yield!
                publishedForCases
                    RawEventCase.owner
                    [ "Created" ]
                    "Owner"
                    "ownerId"
                    "Owner creation is a stable public lifecycle fact."
                    [ "ownerId"; "ownerName" ]
                    "Use raw event fields and actor identity."
                    []
                    CanonicalEventName.OwnerCreated
            yield!
                publishedForCases
                    RawEventCase.owner
                    [
                        "NameSet"
                        "TypeSet"
                        "SearchVisibilitySet"
                        "DescriptionSet"
                    ]
                    "Owner"
                    "ownerId"
                    "Owner property changes are compact public updates."
                    [ "ownerId"; "changeKind"; "changed" ]
                    "Use owner actor identity plus changed value from raw event."
                    []
                    CanonicalEventName.OwnerUpdated
            yield!
                publishedForCases
                    RawEventCase.owner
                    [ "LogicalDeleted" ]
                    "Owner"
                    "ownerId"
                    "Logical owner deletion is the public delete signal."
                    [ "ownerId"; "force"; "deleteReason" ]
                    "Use owner actor identity plus delete metadata from raw event."
                    []
                    CanonicalEventName.OwnerDeleted
            yield!
                internalOnlyForCases
                    RawEventCase.owner
                    [ "PhysicalDeleted" ]
                    "Physical owner deletion is an internal storage concern."
                    "No public publication."
                    []
            yield!
                publishedForCases
                    RawEventCase.owner
                    [ "Undeleted" ]
                    "Owner"
                    "ownerId"
                    "Owner restore is a public lifecycle fact."
                    [ "ownerId" ]
                    "Use owner actor identity."
                    []
                    CanonicalEventName.OwnerRestored

            yield!
                publishedForCases
                    RawEventCase.organization
                    [ "Created" ]
                    "Organization"
                    "organizationId"
                    "Organization creation is a stable public lifecycle fact."
                    [
                        "organizationId"
                        "organizationName"
                        "ownerId"
                    ]
                    "Use raw event fields and actor identity."
                    []
                    CanonicalEventName.OrganizationCreated
            yield!
                publishedForCases
                    RawEventCase.organization
                    [
                        "NameSet"
                        "TypeSet"
                        "SearchVisibilitySet"
                        "DescriptionSet"
                    ]
                    "Organization"
                    "organizationId"
                    "Organization property changes are compact public updates."
                    [
                        "organizationId"
                        "changeKind"
                        "changed"
                    ]
                    "Use organization actor identity plus changed value from raw event."
                    []
                    CanonicalEventName.OrganizationUpdated
            yield!
                publishedForCases
                    RawEventCase.organization
                    [ "LogicalDeleted" ]
                    "Organization"
                    "organizationId"
                    "Logical organization deletion is the public delete signal."
                    [
                        "organizationId"
                        "force"
                        "deleteReason"
                    ]
                    "Use organization actor identity plus delete metadata from raw event."
                    []
                    CanonicalEventName.OrganizationDeleted
            yield!
                internalOnlyForCases
                    RawEventCase.organization
                    [ "PhysicalDeleted" ]
                    "Physical organization deletion is an internal storage concern."
                    "No public publication."
                    []
            yield!
                publishedForCases
                    RawEventCase.organization
                    [ "Undeleted" ]
                    "Organization"
                    "organizationId"
                    "Organization restore is a public lifecycle fact."
                    [ "organizationId" ]
                    "Use organization actor identity."
                    []
                    CanonicalEventName.OrganizationRestored

            yield!
                publishedForCases
                    RawEventCase.repository
                    [ "Created" ]
                    "Repository"
                    "repositoryId"
                    "Repository creation is a stable public lifecycle fact."
                    [
                        "repositoryId"
                        "repositoryName"
                        "ownerId"
                        "organizationId"
                    ]
                    "Use raw event fields and actor identity."
                    []
                    CanonicalEventName.RepositoryCreated
            yield!
                publishedForCases
                    RawEventCase.repository
                    [ "Initialized" ]
                    "Repository"
                    "repositoryId"
                    "Repository initialization is a public milestone."
                    [ "repositoryId" ]
                    "Use repository actor identity."
                    []
                    CanonicalEventName.RepositoryInitialized
            yield!
                publishedForCases
                    RawEventCase.repository
                    [
                        "ObjectStorageProviderSet"
                        "StorageAccountNameSet"
                        "StorageContainerNameSet"
                        "RepositoryTypeSet"
                        "RepositoryStatusSet"
                        "AllowsLargeFilesSet"
                        "AnonymousAccessSet"
                        "RecordSavesSet"
                        "DefaultServerApiVersionSet"
                        "DefaultBranchNameSet"
                        "LogicalDeleteDaysSet"
                        "SaveDaysSet"
                        "CheckpointDaysSet"
                        "DirectoryVersionCacheDaysSet"
                        "DiffCacheDaysSet"
                        "NameSet"
                        "DescriptionSet"
                        "ConflictResolutionPolicySet"
                    ]
                    "Repository"
                    "repositoryId"
                    "Repository configuration changes are public updates."
                    [
                        "repositoryId"
                        "changeKind"
                        "changed"
                    ]
                    "Use repository actor identity plus changed value from raw event."
                    []
                    CanonicalEventName.RepositoryUpdated
            yield!
                publishedForCases
                    RawEventCase.repository
                    [ "LogicalDeleted" ]
                    "Repository"
                    "repositoryId"
                    "Logical repository deletion is the public delete signal."
                    [
                        "repositoryId"
                        "force"
                        "deleteReason"
                    ]
                    "Use repository actor identity plus delete metadata from raw event."
                    []
                    CanonicalEventName.RepositoryDeleted
            yield!
                internalOnlyForCases
                    RawEventCase.repository
                    [ "PhysicalDeleted" ]
                    "Physical repository deletion is an internal storage concern."
                    "No public publication."
                    []
            yield!
                publishedForCases
                    RawEventCase.repository
                    [ "Undeleted" ]
                    "Repository"
                    "repositoryId"
                    "Repository restore is a public lifecycle fact."
                    [ "repositoryId" ]
                    "Use repository actor identity."
                    []
                    CanonicalEventName.RepositoryRestored

            yield!
                publishedForCases
                    RawEventCase.branch
                    [ "Created" ]
                    "Branch"
                    "branchId"
                    "Branch creation is a stable public lifecycle fact."
                    [
                        "branchId"
                        "branchName"
                        "parentBranchId"
                    ]
                    "Use raw event fields and actor identity."
                    []
                    CanonicalEventName.BranchCreated
            yield!
                publishedForCases
                    RawEventCase.branch
                    [ "Rebased" ]
                    "Branch"
                    "branchId"
                    "Branch rebase is a meaningful public branch lifecycle event."
                    [ "branchId"; "changeKind"; "changed" ]
                    "Use branch actor identity plus based-on reference from raw event or metadata."
                    []
                    CanonicalEventName.BranchRebased
            yield!
                publishedForCases
                    RawEventCase.branch
                    [
                        "NameSet"
                        "EnabledAssign"
                        "EnabledPromotion"
                        "EnabledCommit"
                        "EnabledCheckpoint"
                        "EnabledSave"
                        "EnabledTag"
                        "EnabledExternal"
                        "EnabledAutoRebase"
                        "PromotionModeSet"
                        "ReferenceRemoved"
                        "ParentBranchUpdated"
                    ]
                    "Branch"
                    "branchId"
                    "Branch metadata changes are public updates."
                    [ "branchId"; "changeKind"; "changed" ]
                    "Use branch actor identity plus changed value from raw event."
                    []
                    CanonicalEventName.BranchUpdated
            yield!
                internalOnlyForCases
                    RawEventCase.branch
                    [
                        "Assigned"
                        "Promoted"
                        "Committed"
                        "Checkpointed"
                        "Saved"
                        "Tagged"
                        "ExternalCreated"
                    ]
                    "Reference-producing branch events stay internal-only to avoid duplicate public branch/reference signals."
                    "Treat raw reference-producing branch events as internal-only outwardly."
                    [
                        "ReferenceEventType.Created is authoritative for public reference and promotion signals."
                    ]
            yield!
                publishedForCases
                    RawEventCase.branch
                    [ "LogicalDeleted" ]
                    "Branch"
                    "branchId"
                    "Logical branch deletion is the public delete signal."
                    [ "branchId"; "force"; "deleteReason" ]
                    "Use branch actor identity plus delete metadata from raw event."
                    []
                    CanonicalEventName.BranchDeleted
            yield!
                internalOnlyForCases
                    RawEventCase.branch
                    [ "PhysicalDeleted" ]
                    "Physical branch deletion is an internal storage concern."
                    "No public publication."
                    []
            yield!
                publishedForCases
                    RawEventCase.branch
                    [ "Undeleted" ]
                    "Branch"
                    "branchId"
                    "Branch restore is a public lifecycle fact."
                    [ "branchId" ]
                    "Use branch actor identity."
                    []
                    CanonicalEventName.BranchRestored

            yield
                derived
                    "Reference"
                    "referenceId"
                    "Reference creation conditionally yields either reference-created or promotion-set-applied."
                    [
                        "referenceId"
                        "branchId"
                        "directoryVersionId"
                    ]
                    "Inspect reference type and links; terminal promotion references publish applied, otherwise reference-created."
                    [
                        "The mapping must be mutually exclusive."
                        "Terminal promotion ReferenceEventType.Created is the only authoritative raw source for grace.promotion-set.applied."
                    ]
                    [
                        CanonicalEventName.ReferenceCreated
                        CanonicalEventName.PromotionSetApplied
                    ]
                    (RawEventCase.reference "Created")
            yield!
                publishedForCases
                    RawEventCase.reference
                    [ "LinkAdded"; "LinkRemoved" ]
                    "Reference"
                    "referenceId"
                    "Reference link changes are public updates."
                    [
                        "referenceId"
                        "changeKind"
                        "changed"
                    ]
                    "Use reference actor identity plus changed link from raw event."
                    []
                    CanonicalEventName.ReferenceUpdated
            yield!
                publishedForCases
                    RawEventCase.reference
                    [ "LogicalDeleted" ]
                    "Reference"
                    "referenceId"
                    "Logical reference deletion is the public delete signal."
                    [
                        "referenceId"
                        "force"
                        "deleteReason"
                    ]
                    "Use reference actor identity plus delete metadata from raw event."
                    []
                    CanonicalEventName.ReferenceDeleted
            yield!
                internalOnlyForCases
                    RawEventCase.reference
                    [ "PhysicalDeleted" ]
                    "Physical reference deletion is an internal storage concern."
                    "No public publication."
                    []
            yield!
                publishedForCases
                    RawEventCase.reference
                    [ "Undeleted" ]
                    "Reference"
                    "referenceId"
                    "Reference restore is a public lifecycle fact."
                    [ "referenceId" ]
                    "Use reference actor identity."
                    []
                    CanonicalEventName.ReferenceRestored

            yield!
                internalOnlyForCases
                    RawEventCase.directoryVersion
                    [
                        "Created"
                        "RecursiveSizeSet"
                        "LogicalDeleted"
                        "PhysicalDeleted"
                        "Undeleted"
                    ]
                    "All directory-version cases are internal-only in the canonical taxonomy."
                    "No public publication."
                    []

            yield!
                publishedForCases
                    RawEventCase.workItem
                    [ "Created" ]
                    "WorkItem"
                    "workItemId"
                    "Work-item creation is a stable public lifecycle fact."
                    [
                        "workItemId"
                        "workItemNumber"
                        "title"
                    ]
                    "Use raw event fields and actor identity."
                    []
                    CanonicalEventName.WorkItemCreated
            yield!
                publishedForCases
                    RawEventCase.workItem
                    [
                        "TitleSet"
                        "DescriptionSet"
                        "StatusSet"
                        "ParticipantAdded"
                        "ParticipantRemoved"
                        "TagAdded"
                        "TagRemoved"
                        "ConstraintsSet"
                        "NotesSet"
                        "ArchitecturalNotesSet"
                        "MigrationNotesSet"
                        "ExternalRefAdded"
                        "ExternalRefRemoved"
                        "BranchLinked"
                        "BranchUnlinked"
                        "ReferenceLinked"
                        "ReferenceUnlinked"
                        "ArtifactLinked"
                        "ArtifactUnlinked"
                        "PromotionSetLinked"
                        "PromotionSetUnlinked"
                        "ReviewNotesLinked"
                        "ReviewNotesUnlinked"
                        "ReviewCheckpointLinked"
                        "ReviewCheckpointUnlinked"
                        "ValidationResultLinked"
                        "ValidationResultUnlinked"
                    ]
                    "WorkItem"
                    "workItemId"
                    "Granular work-item setter and linkage events are public updates with compact changed payloads."
                    [
                        "workItemId"
                        "changeKind"
                        "changed"
                    ]
                    "Use work-item actor identity plus changed value from raw event."
                    [
                        "WorkItemEventType.ArtifactLinked must remain a work-item update, not an agent summary event."
                    ]
                    CanonicalEventName.WorkItemUpdated

            yield!
                publishedForCases
                    RawEventCase.policy
                    [ "SnapshotCreated" ]
                    "Policy"
                    "policySnapshotId"
                    "Policy snapshot creation is a stable public compliance event."
                    [
                        "policySnapshotId"
                        "targetBranchId"
                        "ruleSummary"
                    ]
                    "Use snapshot payload from raw event."
                    []
                    CanonicalEventName.PolicySnapshotCreated
            yield!
                publishedForCases
                    RawEventCase.policy
                    [ "Acknowledged" ]
                    "Policy"
                    "policySnapshotId"
                    "Policy acknowledgement is a public compliance event."
                    [ "policySnapshotId"; "acknowledgedBy" ]
                    "Use raw event payload plus policy actor context for repository and branch ids."
                    []
                    CanonicalEventName.PolicyAcknowledged

            yield!
                publishedForCases
                    RawEventCase.queue
                    [ "Initialized" ]
                    "Queue"
                    "targetBranchId"
                    "Queue initialization is a public branch-queue milestone."
                    [ "targetBranchId"; "policySnapshotId" ]
                    "Use queue actor identity as target branch id and raw event payload."
                    []
                    CanonicalEventName.QueueInitialized
            yield!
                publishedForCases
                    RawEventCase.queue
                    [ "PromotionSetEnqueued" ]
                    "Queue"
                    "targetBranchId"
                    "Queue enqueue operations are meaningful public routing events."
                    [ "targetBranchId"; "promotionSetId" ]
                    "Use queue actor identity as target branch id and raw event payload."
                    []
                    CanonicalEventName.QueuePromotionSetEnqueued
            yield!
                publishedForCases
                    RawEventCase.queue
                    [ "PromotionSetDequeued" ]
                    "Queue"
                    "targetBranchId"
                    "Queue dequeue operations are meaningful public routing events."
                    [ "targetBranchId"; "promotionSetId" ]
                    "Use queue actor identity as target branch id and raw event payload."
                    []
                    CanonicalEventName.QueuePromotionSetDequeued
            yield!
                internalOnlyForCases
                    RawEventCase.queue
                    [ "RunningPromotionSetSet" ]
                    "Running promotion-set selection is an internal queue execution detail."
                    "No public publication."
                    []
            yield!
                publishedForCases
                    RawEventCase.queue
                    [ "Paused" ]
                    "Queue"
                    "targetBranchId"
                    "Queue paused is a public branch-queue state change."
                    [ "targetBranchId" ]
                    "Use queue actor identity as target branch id."
                    []
                    CanonicalEventName.QueuePaused
            yield!
                publishedForCases
                    RawEventCase.queue
                    [ "Resumed" ]
                    "Queue"
                    "targetBranchId"
                    "Queue resumed is a public branch-queue state change."
                    [ "targetBranchId" ]
                    "Use queue actor identity as target branch id."
                    []
                    CanonicalEventName.QueueResumed
            yield!
                publishedForCases
                    RawEventCase.queue
                    [ "Degraded" ]
                    "Queue"
                    "targetBranchId"
                    "Queue degraded is a public branch-queue state change."
                    [ "targetBranchId" ]
                    "Use queue actor identity as target branch id."
                    []
                    CanonicalEventName.QueueDegraded
            yield!
                publishedForCases
                    RawEventCase.queue
                    [ "PolicySnapshotUpdated" ]
                    "Queue"
                    "targetBranchId"
                    "Queue policy snapshot changes are public queue context updates."
                    [ "targetBranchId"; "policySnapshotId" ]
                    "Use queue actor identity as target branch id and raw event payload."
                    []
                    CanonicalEventName.QueuePolicySnapshotUpdated

            yield!
                publishedForCases
                    RawEventCase.promotionSet
                    [ "Created" ]
                    "PromotionSet"
                    "promotionSetId"
                    "Promotion-set creation is a stable public lifecycle fact."
                    [ "promotionSetId"; "targetBranchId" ]
                    "Use raw event fields and actor identity."
                    []
                    CanonicalEventName.PromotionSetCreated
            yield!
                publishedForCases
                    RawEventCase.promotionSet
                    [ "InputPromotionsUpdated" ]
                    "PromotionSet"
                    "promotionSetId"
                    "Promotion-set input changes are public updates."
                    [
                        "promotionSetId"
                        "targetBranchId"
                        "changed"
                    ]
                    "Use promotion-set actor identity and raw event payload."
                    []
                    CanonicalEventName.PromotionSetInputsUpdated
            yield!
                publishedForCases
                    RawEventCase.promotionSet
                    [ "RecomputeStarted" ]
                    "PromotionSet"
                    "promotionSetId"
                    "Recompute start is a public progress event."
                    [
                        "promotionSetId"
                        "targetBranchId"
                        "computedAgainstTerminalReferenceId"
                    ]
                    "Use promotion-set actor identity and raw event payload."
                    []
                    CanonicalEventName.PromotionSetRecomputeStarted
            yield
                { (published
                      "PromotionSet"
                      "promotionSetId"
                      "Steps updated is the public recompute-success signal."
                      [
                          "promotionSetId"
                          "targetBranchId"
                          "stepsSummary"
                      ]
                      "Use promotion-set actor identity and raw event payload."
                      [
                          "Do not emit a synthetic recompute-succeeded duplicate."
                      ]
                      [
                          CanonicalEventName.PromotionSetStepsUpdated
                      ]
                      (RawEventCase.promotionSet "StepsUpdated")) with
                    OverflowStrategy = Some "Preserve counts, ordered prefix steps, and set truncated=true before hitting the 1 MB limit."
                }
            yield!
                publishedForCases
                    RawEventCase.promotionSet
                    [ "RecomputeFailed" ]
                    "PromotionSet"
                    "promotionSetId"
                    "Recompute failures are public progress events."
                    [
                        "promotionSetId"
                        "targetBranchId"
                        "reason"
                    ]
                    "Use promotion-set actor identity and raw event payload."
                    []
                    CanonicalEventName.PromotionSetRecomputeFailed
            yield!
                publishedForCases
                    RawEventCase.promotionSet
                    [ "Blocked" ]
                    "PromotionSet"
                    "promotionSetId"
                    "Promotion-set blocked is a meaningful public status event."
                    [
                        "promotionSetId"
                        "targetBranchId"
                        "reason"
                    ]
                    "Use promotion-set actor identity and raw event payload."
                    []
                    CanonicalEventName.PromotionSetBlocked
            yield!
                publishedForCases
                    RawEventCase.promotionSet
                    [ "ApplyStarted" ]
                    "PromotionSet"
                    "promotionSetId"
                    "Apply started is a public progress event."
                    [ "promotionSetId"; "targetBranchId" ]
                    "Use promotion-set actor identity."
                    []
                    CanonicalEventName.PromotionSetApplyStarted
            yield!
                internalOnlyForCases
                    RawEventCase.promotionSet
                    [ "Applied" ]
                    "PromotionSet.Applied is internal-only because terminal Reference.Created is the sole authoritative public applied signal."
                    "No public publication from this raw case."
                    [
                        "grace.promotion-set.applied must only come from terminal ReferenceEventType.Created."
                    ]
            yield!
                publishedForCases
                    RawEventCase.promotionSet
                    [ "ApplyFailed" ]
                    "PromotionSet"
                    "promotionSetId"
                    "Apply failure is a public progress event."
                    [
                        "promotionSetId"
                        "targetBranchId"
                        "reason"
                    ]
                    "Use promotion-set actor identity and raw event payload."
                    []
                    CanonicalEventName.PromotionSetApplyFailed
            yield!
                publishedForCases
                    RawEventCase.promotionSet
                    [ "LogicalDeleted" ]
                    "PromotionSet"
                    "promotionSetId"
                    "Logical promotion-set deletion is the public delete signal."
                    [
                        "promotionSetId"
                        "force"
                        "deleteReason"
                    ]
                    "Use promotion-set actor identity plus delete metadata from raw event."
                    []
                    CanonicalEventName.PromotionSetDeleted

            yield!
                publishedForCases
                    RawEventCase.validationSet
                    [ "Created" ]
                    "ValidationSet"
                    "validationSetId"
                    "Validation-set creation is a stable public lifecycle fact."
                    [
                        "validationSetId"
                        "targetBranchId"
                        "rulesSummary"
                        "validationsSummary"
                    ]
                    "Use validation-set dto from raw event."
                    []
                    CanonicalEventName.ValidationSetCreated
            yield!
                publishedForCases
                    RawEventCase.validationSet
                    [ "Updated" ]
                    "ValidationSet"
                    "validationSetId"
                    "Validation-set updates are public configuration changes."
                    [
                        "validationSetId"
                        "targetBranchId"
                        "rulesSummary"
                        "validationsSummary"
                    ]
                    "Use validation-set dto from raw event."
                    []
                    CanonicalEventName.ValidationSetUpdated
            yield!
                publishedForCases
                    RawEventCase.validationSet
                    [ "LogicalDeleted" ]
                    "ValidationSet"
                    "validationSetId"
                    "Logical validation-set deletion is the public delete signal."
                    [
                        "validationSetId"
                        "force"
                        "deleteReason"
                    ]
                    "Use validation-set actor identity plus delete metadata from raw event."
                    []
                    CanonicalEventName.ValidationSetDeleted

            yield!
                publishedForCases
                    RawEventCase.validationResult
                    [ "Recorded" ]
                    "ValidationResult"
                    "validationResultId"
                    "Validation-result recording is a stable public result event."
                    [
                        "validationResultId"
                        "status"
                        "summary"
                    ]
                    "Use validation-result dto from raw event."
                    []
                    CanonicalEventName.ValidationResultRecorded

            yield!
                publishedForCases
                    RawEventCase.review
                    [ "NotesUpserted" ]
                    "ReviewNotes"
                    "reviewNotesId"
                    "Review-notes upserts are stable public review summaries."
                    [
                        "reviewNotesId"
                        "summary"
                        "chapterCount"
                        "findingCount"
                    ]
                    "Use review notes payload from raw event."
                    []
                    CanonicalEventName.ReviewNotesUpserted
            yield!
                publishedForCases
                    RawEventCase.review
                    [ "FindingResolved" ]
                    "ReviewFinding"
                    "findingId"
                    "Finding resolution is a stable public review event."
                    [ "findingId"; "resolutionState" ]
                    "Use finding id from raw event; resolve additional review context from actor state if required."
                    []
                    CanonicalEventName.ReviewFindingResolved
            yield!
                publishedForCases
                    RawEventCase.review
                    [ "CheckpointAdded" ]
                    "ReviewCheckpoint"
                    "reviewCheckpointId"
                    "Checkpoint creation is a stable public review event."
                    [
                        "reviewCheckpointId"
                        "reviewedUpToReferenceId"
                    ]
                    "Use checkpoint payload from raw event."
                    []
                    CanonicalEventName.ReviewCheckpointAdded

            yield!
                publishedForCases
                    RawEventCase.artifact
                    [ "Created" ]
                    "Artifact"
                    "artifactId"
                    "Artifact creation is a stable public lifecycle fact."
                    [
                        "artifactId"
                        "artifactType"
                        "mimeType"
                    ]
                    "Use raw artifact metadata payload."
                    []
                    CanonicalEventName.ArtifactCreated

            yield
                derived
                    "ValidationSet"
                    "validationSetId"
                    "Validation requested is a derived public event emitted from successfully built canonical source events."
                    [
                        "validationSetId"
                        "sourceEventName"
                        "targetBranchId"
                        "validationsSummary"
                    ]
                    "Derive once per matching validation set from a successfully built canonical source event."
                    [
                        "Validation rules must match canonical published event names only."
                    ]
                    [
                        CanonicalEventName.ValidationRequested
                    ]
                    (RawEventCase.derived "grace.validation.requested")
            yield
                published
                    "AgentSession"
                    "sessionId"
                    "Agent work start is an approved non-GraceEvent canonical runtime event."
                    [
                        "agentId"
                        "sessionId"
                        "repositoryId"
                        "startedAt"
                        "lifecycleState"
                    ]
                    "Build from persisted runtime source record before outward publication."
                    []
                    [ CanonicalEventName.AgentWorkStarted ]
                    (RawEventCase.runtime "grace.agent.work-started")
            yield
                published
                    "AgentSession"
                    "sessionId"
                    "Agent work stop is an approved non-GraceEvent canonical runtime event."
                    [
                        "agentId"
                        "sessionId"
                        "repositoryId"
                        "stoppedAt"
                        "lifecycleState"
                    ]
                    "Build from persisted runtime source record before outward publication."
                    []
                    [ CanonicalEventName.AgentWorkStopped ]
                    (RawEventCase.runtime "grace.agent.work-stopped")
        ]

    let private index =
        lazy
            (all
             |> Seq.map (fun entry -> entry.RawEventCase, entry)
             |> Map.ofSeq)

    let tryFind rawEventCase = index.Value |> Map.tryFind rawEventCase

    let find rawEventCase =
        tryFind rawEventCase
        |> Option.defaultWith (fun () -> invalidOp $"Missing external event registry entry for {rawEventCase}.")

    let publishedEntries =
        all
        |> List.filter (fun entry -> entry.Classification <> InternalOnly)

    let publishedEventNames =
        publishedEntries
        |> Seq.collect (fun entry -> entry.PublishedEventNames)
        |> Seq.distinct
        |> Seq.toArray

    let publishedEventNameStrings =
        publishedEventNames
        |> Array.map CanonicalEventName.toString

    let publishedEventNameSet = publishedEventNameStrings |> Set.ofArray

    let isPublishedEventName value =
        if String.IsNullOrWhiteSpace value then
            false
        else
            publishedEventNameSet.Contains(value.Trim())
