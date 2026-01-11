namespace Grace.Types

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.PromotionGroup
open Grace.Types.Types
open NodaTime
open Orleans
open System
open System.Runtime.Serialization

module WorkItem =
    /// Defines the status of a work item.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type WorkItemStatus =
        | Backlog
        | Active
        | Blocked
        | InReview
        | Done
        | Canceled

        static member GetKnownTypes() = GetKnownTypes<WorkItemStatus>()

    /// Defines the commands for the WorkItem actor.
    [<KnownType("GetKnownTypes")>]
    type WorkItemCommand =
        | Create of
            workItemId: WorkItemId *
            ownerId: OwnerId *
            organizationId: OrganizationId *
            repositoryId: RepositoryId *
            title: string *
            description: string
        | SetTitle of title: string
        | SetDescription of description: string
        | SetStatus of status: WorkItemStatus
        | AddParticipant of userId: UserId
        | RemoveParticipant of userId: UserId
        | AddTag of tag: string
        | RemoveTag of tag: string
        | SetConstraints of constraints: string
        | SetNotes of notes: string
        | SetArchitecturalNotes of notes: string
        | SetMigrationNotes of notes: string
        | AddExternalRef of reference: string
        | RemoveExternalRef of reference: string
        | LinkBranch of branchId: BranchId
        | UnlinkBranch of branchId: BranchId
        | LinkReference of referenceId: ReferenceId
        | UnlinkReference of referenceId: ReferenceId
        | LinkPromotionGroup of promotionGroupId: PromotionGroupId
        | UnlinkPromotionGroup of promotionGroupId: PromotionGroupId
        | LinkCandidate of candidateId: CandidateId
        | UnlinkCandidate of candidateId: CandidateId
        | LinkReviewPacket of reviewPacketId: ReviewPacketId
        | UnlinkReviewPacket of reviewPacketId: ReviewPacketId
        | LinkReviewCheckpoint of reviewCheckpointId: ReviewCheckpointId
        | UnlinkReviewCheckpoint of reviewCheckpointId: ReviewCheckpointId
        | LinkGateAttestation of gateAttestationId: GateAttestationId
        | UnlinkGateAttestation of gateAttestationId: GateAttestationId

        static member GetKnownTypes() = GetKnownTypes<WorkItemCommand>()

    /// Defines the events for the WorkItem actor.
    [<KnownType("GetKnownTypes")>]
    type WorkItemEventType =
        | Created of
            workItemId: WorkItemId *
            ownerId: OwnerId *
            organizationId: OrganizationId *
            repositoryId: RepositoryId *
            title: string *
            description: string
        | TitleSet of title: string
        | DescriptionSet of description: string
        | StatusSet of status: WorkItemStatus
        | ParticipantAdded of userId: UserId
        | ParticipantRemoved of userId: UserId
        | TagAdded of tag: string
        | TagRemoved of tag: string
        | ConstraintsSet of constraints: string
        | NotesSet of notes: string
        | ArchitecturalNotesSet of notes: string
        | MigrationNotesSet of notes: string
        | ExternalRefAdded of reference: string
        | ExternalRefRemoved of reference: string
        | BranchLinked of branchId: BranchId
        | BranchUnlinked of branchId: BranchId
        | ReferenceLinked of referenceId: ReferenceId
        | ReferenceUnlinked of referenceId: ReferenceId
        | PromotionGroupLinked of promotionGroupId: PromotionGroupId
        | PromotionGroupUnlinked of promotionGroupId: PromotionGroupId
        | CandidateLinked of candidateId: CandidateId
        | CandidateUnlinked of candidateId: CandidateId
        | ReviewPacketLinked of reviewPacketId: ReviewPacketId
        | ReviewPacketUnlinked of reviewPacketId: ReviewPacketId
        | ReviewCheckpointLinked of reviewCheckpointId: ReviewCheckpointId
        | ReviewCheckpointUnlinked of reviewCheckpointId: ReviewCheckpointId
        | GateAttestationLinked of gateAttestationId: GateAttestationId
        | GateAttestationUnlinked of gateAttestationId: GateAttestationId

        static member GetKnownTypes() = GetKnownTypes<WorkItemEventType>()

    /// Record that holds the event type and metadata for a WorkItem event.
    type WorkItemEvent =
        {
            /// The WorkItemEventType case that describes the event.
            Event: WorkItemEventType
            /// The EventMetadata for the event. EventMetadata includes the Timestamp, CorrelationId, Principal, and a Properties dictionary.
            Metadata: EventMetadata
        }

    /// The WorkItemDto is a data transfer object that represents a work item in the system.
    type WorkItemDto =
        {
            Class: string
            WorkItemId: WorkItemId
            OwnerId: OwnerId
            OrganizationId: OrganizationId
            RepositoryId: RepositoryId
            Title: string
            Description: string
            Status: WorkItemStatus
            Participants: UserId list
            Tags: string list
            Constraints: string
            Notes: string
            ArchitecturalNotes: string
            MigrationNotes: string
            ExternalRefs: string list
            BranchIds: BranchId list
            ReferenceIds: ReferenceId list
            PromotionGroupIds: PromotionGroupId list
            CandidateIds: CandidateId list
            ReviewPacketIds: ReviewPacketId list
            ReviewCheckpointIds: ReviewCheckpointId list
            GateAttestationIds: GateAttestationId list
            CreatedBy: UserId
            CreatedAt: Instant
            UpdatedAt: Instant option
        }

        static member Default =
            {
                Class = nameof WorkItemDto
                WorkItemId = WorkItemId.Empty
                OwnerId = OwnerId.Empty
                OrganizationId = OrganizationId.Empty
                RepositoryId = RepositoryId.Empty
                Title = String.Empty
                Description = String.Empty
                Status = WorkItemStatus.Backlog
                Participants = []
                Tags = []
                Constraints = String.Empty
                Notes = String.Empty
                ArchitecturalNotes = String.Empty
                MigrationNotes = String.Empty
                ExternalRefs = []
                BranchIds = []
                ReferenceIds = []
                PromotionGroupIds = []
                CandidateIds = []
                ReviewPacketIds = []
                ReviewCheckpointIds = []
                GateAttestationIds = []
                CreatedBy = UserId String.Empty
                CreatedAt = Constants.DefaultTimestamp
                UpdatedAt = None
            }

        /// Updates the WorkItemDto based on the WorkItemEvent.
        static member UpdateDto workItemEvent currentWorkItemDto =
            let newWorkItemDto =
                match workItemEvent.Event with
                | Created (workItemId, ownerId, organizationId, repositoryId, title, description) ->
                    { WorkItemDto.Default with
                        WorkItemId = workItemId
                        OwnerId = ownerId
                        OrganizationId = organizationId
                        RepositoryId = repositoryId
                        Title = title
                        Description = description
                        Status = WorkItemStatus.Backlog
                        CreatedBy = UserId workItemEvent.Metadata.Principal
                        CreatedAt = workItemEvent.Metadata.Timestamp
                    }
                | TitleSet title -> { currentWorkItemDto with Title = title }
                | DescriptionSet description -> { currentWorkItemDto with Description = description }
                | StatusSet status -> { currentWorkItemDto with Status = status }
                | ParticipantAdded userId ->
                    { currentWorkItemDto with
                        Participants =
                            currentWorkItemDto.Participants
                            |> List.append [ userId ]
                            |> List.distinct
                    }
                | ParticipantRemoved userId ->
                    { currentWorkItemDto with
                        Participants =
                            currentWorkItemDto.Participants
                            |> List.filter (fun existing -> existing <> userId)
                    }
                | TagAdded tag ->
                    { currentWorkItemDto with
                        Tags =
                            currentWorkItemDto.Tags
                            |> List.append [ tag ]
                            |> List.distinct
                    }
                | TagRemoved tag ->
                    { currentWorkItemDto with
                        Tags =
                            currentWorkItemDto.Tags
                            |> List.filter (fun existing -> existing <> tag)
                    }
                | ConstraintsSet constraints -> { currentWorkItemDto with Constraints = constraints }
                | NotesSet notes -> { currentWorkItemDto with Notes = notes }
                | ArchitecturalNotesSet notes -> { currentWorkItemDto with ArchitecturalNotes = notes }
                | MigrationNotesSet notes -> { currentWorkItemDto with MigrationNotes = notes }
                | ExternalRefAdded reference ->
                    { currentWorkItemDto with
                        ExternalRefs =
                            currentWorkItemDto.ExternalRefs
                            |> List.append [ reference ]
                            |> List.distinct
                    }
                | ExternalRefRemoved reference ->
                    { currentWorkItemDto with
                        ExternalRefs =
                            currentWorkItemDto.ExternalRefs
                            |> List.filter (fun existing -> existing <> reference)
                    }
                | BranchLinked branchId ->
                    { currentWorkItemDto with
                        BranchIds =
                            currentWorkItemDto.BranchIds
                            |> List.append [ branchId ]
                            |> List.distinct
                    }
                | BranchUnlinked branchId ->
                    { currentWorkItemDto with
                        BranchIds =
                            currentWorkItemDto.BranchIds
                            |> List.filter (fun existing -> existing <> branchId)
                    }
                | ReferenceLinked referenceId ->
                    { currentWorkItemDto with
                        ReferenceIds =
                            currentWorkItemDto.ReferenceIds
                            |> List.append [ referenceId ]
                            |> List.distinct
                    }
                | ReferenceUnlinked referenceId ->
                    { currentWorkItemDto with
                        ReferenceIds =
                            currentWorkItemDto.ReferenceIds
                            |> List.filter (fun existing -> existing <> referenceId)
                    }
                | PromotionGroupLinked promotionGroupId ->
                    { currentWorkItemDto with
                        PromotionGroupIds =
                            currentWorkItemDto.PromotionGroupIds
                            |> List.append [ promotionGroupId ]
                            |> List.distinct
                    }
                | PromotionGroupUnlinked promotionGroupId ->
                    { currentWorkItemDto with
                        PromotionGroupIds =
                            currentWorkItemDto.PromotionGroupIds
                            |> List.filter (fun existing -> existing <> promotionGroupId)
                    }
                | CandidateLinked candidateId ->
                    { currentWorkItemDto with
                        CandidateIds =
                            currentWorkItemDto.CandidateIds
                            |> List.append [ candidateId ]
                            |> List.distinct
                    }
                | CandidateUnlinked candidateId ->
                    { currentWorkItemDto with
                        CandidateIds =
                            currentWorkItemDto.CandidateIds
                            |> List.filter (fun existing -> existing <> candidateId)
                    }
                | ReviewPacketLinked reviewPacketId ->
                    { currentWorkItemDto with
                        ReviewPacketIds =
                            currentWorkItemDto.ReviewPacketIds
                            |> List.append [ reviewPacketId ]
                            |> List.distinct
                    }
                | ReviewPacketUnlinked reviewPacketId ->
                    { currentWorkItemDto with
                        ReviewPacketIds =
                            currentWorkItemDto.ReviewPacketIds
                            |> List.filter (fun existing -> existing <> reviewPacketId)
                    }
                | ReviewCheckpointLinked reviewCheckpointId ->
                    { currentWorkItemDto with
                        ReviewCheckpointIds =
                            currentWorkItemDto.ReviewCheckpointIds
                            |> List.append [ reviewCheckpointId ]
                            |> List.distinct
                    }
                | ReviewCheckpointUnlinked reviewCheckpointId ->
                    { currentWorkItemDto with
                        ReviewCheckpointIds =
                            currentWorkItemDto.ReviewCheckpointIds
                            |> List.filter (fun existing -> existing <> reviewCheckpointId)
                    }
                | GateAttestationLinked gateAttestationId ->
                    { currentWorkItemDto with
                        GateAttestationIds =
                            currentWorkItemDto.GateAttestationIds
                            |> List.append [ gateAttestationId ]
                            |> List.distinct
                    }
                | GateAttestationUnlinked gateAttestationId ->
                    { currentWorkItemDto with
                        GateAttestationIds =
                            currentWorkItemDto.GateAttestationIds
                            |> List.filter (fun existing -> existing <> gateAttestationId)
                    }

            { newWorkItemDto with UpdatedAt = Some workItemEvent.Metadata.Timestamp }
