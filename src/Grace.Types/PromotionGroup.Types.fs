namespace Grace.Types

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Types
open NodaTime
open Orleans
open System
open System.Runtime.Serialization

module PromotionGroup =

    /// The Id of the promotion group.
    type PromotionGroupId = Guid

    /// Defines the status of a promotion group.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type PromotionGroupStatus =
        | Draft // Being edited: promotions can be added/removed/reordered.
        | Ready // Marked ready for execution (subject to schedule).
        | Running // Currently being applied atomically to the parent branch.
        | Succeeded // Fully applied, final promotion recorded.
        | Failed // Failed due to conflicts/tests/other conditions; branch rolled back.
        | Blocked // Waiting on human review (e.g. policy requires review).

        static member GetKnownTypes() = GetKnownTypes<PromotionGroupStatus>()

    /// Defines the outcome of a conflict resolution by the model.
    [<GenerateSerializer>]
    type ConflictResolutionOutcome =
        {
            ModelResolution: string
            Confidence: float // 0.0 to 1.0
            Accepted: bool option
        }

        static member Default = { ModelResolution = String.Empty; Confidence = 0.0; Accepted = None } // None = not reviewed yet

    /// Represents a hunk of conflicting content.
    [<GenerateSerializer>]
    type ConflictHunk = { StartLine: int; EndLine: int; OursContent: string; TheirsContent: string }

    /// Analysis of a conflict in a file.
    [<GenerateSerializer>]
    type ConflictAnalysis =
        {
            FilePath: string
            OriginalHunks: ConflictHunk list
            Resolution: ConflictResolutionOutcome option
        }

        static member Default = { FilePath = String.Empty; OriginalHunks = []; Resolution = None }

    /// Represents a promotion within a promotion group.
    [<GenerateSerializer>]
    type PromotionInGroup =
        {
            PromotionId: ReferenceId
            Order: int
            Conflicts: ConflictAnalysis list option
        }

        static member Default = { PromotionId = ReferenceId.Empty; Order = 0; Conflicts = None }

    /// Event types recorded during promotion group execution.
    [<GenerateSerializer>]
    type PromotionGroupExecutionEvent = { Timestamp: Instant; Description: string; PromotionId: ReferenceId option; ConflictCount: int option }

    /// Defines the commands for the PromotionGroup actor.
    [<KnownType("GetKnownTypes")>]
    type PromotionGroupCommand =
        | Create of
            promotionGroupId: PromotionGroupId *
            ownerId: OwnerId *
            organizationId: OrganizationId *
            repositoryId: RepositoryId *
            targetBranchId: BranchId *
            description: string *
            scheduledAt: Instant option
        | AddPromotion of promotionId: ReferenceId
        | RemovePromotion of promotionId: ReferenceId
        | ReorderPromotions of promotionIds: ReferenceId list
        | Schedule of scheduledAt: Instant option
        | MarkReady
        | Start
        | Complete of success: bool
        | Block of reason: string
        | DeleteLogical of force: bool * DeleteReason: DeleteReason

        static member GetKnownTypes() = GetKnownTypes<PromotionGroupCommand>()

    /// Defines the events for the PromotionGroup actor.
    [<KnownType("GetKnownTypes")>]
    type PromotionGroupEventType =
        | Created of
            promotionGroupId: PromotionGroupId *
            ownerId: OwnerId *
            organizationId: OrganizationId *
            repositoryId: RepositoryId *
            targetBranchId: BranchId *
            description: string *
            scheduledAt: Instant option
        | PromotionAdded of promotionId: ReferenceId * order: int
        | PromotionRemoved of promotionId: ReferenceId
        | PromotionsReordered of promotionIds: ReferenceId list
        | Scheduled of scheduledAt: Instant option
        | MarkedReady
        | Started
        | Completed of success: bool
        | Blocked of reason: string
        | ConflictAnalyzed of promotionId: ReferenceId * analysis: ConflictAnalysis
        | LogicalDeleted of force: bool * DeleteReason: DeleteReason

        static member GetKnownTypes() = GetKnownTypes<PromotionGroupEventType>()

    /// Record that holds the event type and metadata for a PromotionGroup event.
    type PromotionGroupEvent =
        {
            /// The PromotionGroupEventType case that describes the event.
            Event: PromotionGroupEventType
            /// The EventMetadata for the event. EventMetadata includes the Timestamp, CorrelationId, Principal, and a Properties dictionary.
            Metadata: EventMetadata
        }

    /// The PromotionGroupDto is a data transfer object that represents a promotion group in the system.
    type PromotionGroupDto =
        {
            Class: string
            PromotionGroupId: PromotionGroupId
            OwnerId: OwnerId
            OrganizationId: OrganizationId
            RepositoryId: RepositoryId
            TargetBranchId: BranchId
            Status: PromotionGroupStatus
            CreatedBy: UserId
            Description: string
            CreatedAt: Instant
            ScheduledAt: Instant option
            Promotions: PromotionInGroup list
            ExecutionLog: PromotionGroupExecutionEvent list
            UpdatedAt: Instant option
            DeletedAt: Instant option
            DeleteReason: DeleteReason
        }

        static member Default =
            {
                Class = nameof PromotionGroupDto
                PromotionGroupId = PromotionGroupId.Empty
                OwnerId = OwnerId.Empty
                OrganizationId = OrganizationId.Empty
                RepositoryId = RepositoryId.Empty
                TargetBranchId = BranchId.Empty
                Status = PromotionGroupStatus.Draft
                CreatedBy = UserId String.Empty
                Description = String.Empty
                CreatedAt = Constants.DefaultTimestamp
                ScheduledAt = None
                Promotions = []
                ExecutionLog = []
                UpdatedAt = None
                DeletedAt = None
                DeleteReason = String.Empty
            }

        /// Updates the PromotionGroupDto based on the PromotionGroupEvent.
        static member UpdateDto promotionGroupEvent currentDto =
            let newDto =
                match promotionGroupEvent.Event with
                | Created (promotionGroupId, ownerId, organizationId, repositoryId, targetBranchId, description, scheduledAt) ->
                    { PromotionGroupDto.Default with
                        PromotionGroupId = promotionGroupId
                        OwnerId = ownerId
                        OrganizationId = organizationId
                        RepositoryId = repositoryId
                        TargetBranchId = targetBranchId
                        Description = description
                        ScheduledAt = scheduledAt
                        CreatedBy = UserId promotionGroupEvent.Metadata.Principal
                        CreatedAt = promotionGroupEvent.Metadata.Timestamp
                    }
                | PromotionAdded (promotionId, order) ->
                    let newPromotion = { PromotionId = promotionId; Order = order; Conflicts = None }
                    { currentDto with Promotions = currentDto.Promotions @ [ newPromotion ] }
                | PromotionRemoved promotionId ->
                    let filtered =
                        currentDto.Promotions
                        |> List.filter (fun p -> p.PromotionId <> promotionId)
                    // Re-normalize ordering
                    let renumbered =
                        filtered
                        |> List.mapi (fun i p -> { p with Order = i })

                    { currentDto with Promotions = renumbered }
                | PromotionsReordered promotionIds ->
                    let reordered =
                        promotionIds
                        |> List.mapi (fun i pid ->
                            let existing =
                                currentDto.Promotions
                                |> List.find (fun p -> p.PromotionId = pid)

                            { existing with Order = i })

                    { currentDto with Promotions = reordered }
                | Scheduled scheduledAt -> { currentDto with ScheduledAt = scheduledAt }
                | MarkedReady -> { currentDto with Status = PromotionGroupStatus.Ready }
                | Started -> { currentDto with Status = PromotionGroupStatus.Running }
                | Completed success ->
                    let newStatus = if success then PromotionGroupStatus.Succeeded else PromotionGroupStatus.Failed
                    { currentDto with Status = newStatus }
                | Blocked reason -> { currentDto with Status = PromotionGroupStatus.Blocked }
                | ConflictAnalyzed (promotionId, analysis) ->
                    let updatedPromotions =
                        currentDto.Promotions
                        |> List.map (fun p ->
                            if p.PromotionId = promotionId then
                                let existingConflicts = p.Conflicts |> Option.defaultValue []
                                { p with Conflicts = Some(existingConflicts @ [ analysis ]) }
                            else
                                p)

                    { currentDto with Promotions = updatedPromotions }
                | LogicalDeleted (force, deleteReason) -> { currentDto with DeletedAt = Some(getCurrentInstant ()); DeleteReason = deleteReason }

            { newDto with UpdatedAt = Some promotionGroupEvent.Metadata.Timestamp }
