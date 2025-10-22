namespace Grace.Types

open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types.Reference
open Grace.Types.Types
open NodaTime
open Orleans
open System
open System.Runtime.Serialization
open System.Collections

module Branch =

    /// The state held in the database when creating a physical deletion reminder for a branch.
    type PhysicalDeletionReminderState =
        { RepositoryId: RepositoryId
          BranchId: BranchId
          BranchName: BranchName
          ParentBranchId: ParentBranchId
          DeleteReason: DeleteReason
          CorrelationId: CorrelationId }

    /// Defines the commands for the Branch actor.
    [<KnownType("GetKnownTypes")>]
    type BranchCommand =
        | Create of
            branchId: BranchId *
            branchName: BranchName *
            parentBranchId: BranchId *
            basedOn: ReferenceId *
            ownerId: OwnerId *
            organizationId: OrganizationId *
            repositoryId: RepositoryId *
            initialPermissions: ReferenceType seq
        | Rebase of basedOn: ReferenceId
        | SetName of newName: BranchName
        | Assign of directoryVersionId: DirectoryVersionId * sha256Hash: Sha256Hash * referenceText: ReferenceText
        | Promote of directoryVersionId: DirectoryVersionId * sha256Hash: Sha256Hash * referenceText: ReferenceText
        | Commit of directoryVersionId: DirectoryVersionId * sha256Hash: Sha256Hash * referenceText: ReferenceText
        | Checkpoint of directoryVersionId: DirectoryVersionId * sha256Hash: Sha256Hash * referenceText: ReferenceText
        | Save of directoryVersionId: DirectoryVersionId * sha256Hash: Sha256Hash * referenceText: ReferenceText
        | Tag of directoryVersionId: DirectoryVersionId * sha256Hash: Sha256Hash * referenceText: ReferenceText
        | CreateExternal of directoryVersionId: DirectoryVersionId * sha256Hash: Sha256Hash * referenceText: ReferenceText
        | EnableAssign of enabled: bool
        | EnablePromotion of enabled: bool
        | EnableCommit of enabled: bool
        | EnableCheckpoint of enabled: bool
        | EnableSave of enabled: bool
        | EnableTag of enabled: bool
        | EnableExternal of enabled: bool
        | EnableAutoRebase of enabled: bool
        | RemoveReference of referenceId: ReferenceId
        | DeleteLogical of force: bool * DeleteReason: DeleteReason
        | DeletePhysical
        | Undelete

        static member GetKnownTypes() = GetKnownTypes<BranchCommand>()

    /// Defines the events for the Branch actor.
    [<KnownType("GetKnownTypes")>]
    type BranchEventType =
        | Created of
            branchId: BranchId *
            branchName: BranchName *
            parentBranchId: BranchId *
            basedOn: ReferenceId *
            ownerId: OwnerId *
            organizationId: OrganizationId *
            repositoryId: RepositoryId *
            initialPermissions: ReferenceType seq
        | Rebased of basedOn: ReferenceId
        | NameSet of newName: BranchName
        | Assigned of referenceDto: ReferenceDto * directoryVersionId: DirectoryVersionId * sha256Hash: Sha256Hash * referenceText: ReferenceText
        | Promoted of referenceDto: ReferenceDto * directoryVersionId: DirectoryVersionId * sha256Hash: Sha256Hash * referenceText: ReferenceText
        | Committed of referenceDto: ReferenceDto * directoryVersionId: DirectoryVersionId * sha256Hash: Sha256Hash * referenceText: ReferenceText
        | Checkpointed of referenceDto: ReferenceDto * directoryVersionId: DirectoryVersionId * sha256Hash: Sha256Hash * referenceText: ReferenceText
        | Saved of referenceDto: ReferenceDto * directoryVersionId: DirectoryVersionId * sha256Hash: Sha256Hash * referenceText: ReferenceText
        | Tagged of referenceDto: ReferenceDto * directoryVersionId: DirectoryVersionId * sha256Hash: Sha256Hash * referenceText: ReferenceText
        | ExternalCreated of referenceDto: ReferenceDto * directoryVersionId: DirectoryVersionId * sha256Hash: Sha256Hash * referenceText: ReferenceText
        | EnabledAssign of enabled: bool
        | EnabledPromotion of enabled: bool
        | EnabledCommit of enabled: bool
        | EnabledCheckpoint of enabled: bool
        | EnabledSave of enabled: bool
        | EnabledTag of enabled: bool
        | EnabledExternal of enabled: bool
        | EnabledAutoRebase of enabled: bool
        | ReferenceRemoved of referenceId: ReferenceId
        | LogicalDeleted of force: bool * DeleteReason: DeleteReason
        | PhysicalDeleted
        | Undeleted

        static member GetKnownTypes() = GetKnownTypes<BranchEventType>()

    /// Record that holds the event type and metadata for a Branch event.
    type BranchEvent =
        {
            /// The BranchEventType case that describes the event.
            Event: BranchEventType
            /// The EventMetadata for the event. EventMetadata includes the Timestamp, CorrelationId, Principal, and a Properties dictionary.
            Metadata: EventMetadata
        }

    /// The BranchDto is a data transfer object that represents a branch in the system.
    type BranchDto =
        { Class: string
          BranchId: BranchId
          BranchName: BranchName
          ParentBranchId: BranchId
          OwnerId: OwnerId
          OrganizationId: OrganizationId
          RepositoryId: RepositoryId
          BasedOn: ReferenceDto
          UserId: UserId
          AssignEnabled: bool
          PromotionEnabled: bool
          CommitEnabled: bool
          CheckpointEnabled: bool
          SaveEnabled: bool
          TagEnabled: bool
          ExternalEnabled: bool
          AutoRebaseEnabled: bool
          LatestReference: ReferenceDto
          LatestPromotion: ReferenceDto
          LatestCommit: ReferenceDto
          LatestCheckpoint: ReferenceDto
          LatestSave: ReferenceDto
          ShouldRecomputeLatestReferences: bool
          CreatedAt: Instant
          UpdatedAt: Instant option
          DeletedAt: Instant option
          DeleteReason: DeleteReason }

        static member Default =
            { Class = nameof BranchDto
              BranchId = BranchId.Empty
              BranchName = BranchName "root"
              ParentBranchId = Constants.DefaultParentBranchId
              OwnerId = OwnerId.Empty
              OrganizationId = OrganizationId.Empty
              RepositoryId = RepositoryId.Empty
              BasedOn = ReferenceDto.Default
              UserId = UserId String.Empty
              AssignEnabled = false
              PromotionEnabled = false
              CommitEnabled = false
              CheckpointEnabled = false
              SaveEnabled = false
              TagEnabled = false
              ExternalEnabled = false
              AutoRebaseEnabled = true
              LatestReference = ReferenceDto.Default
              LatestPromotion = ReferenceDto.Default
              LatestCommit = ReferenceDto.Default
              LatestCheckpoint = ReferenceDto.Default
              LatestSave = ReferenceDto.Default
              ShouldRecomputeLatestReferences = true
              CreatedAt = Constants.DefaultTimestamp
              UpdatedAt = None
              DeletedAt = None
              DeleteReason = String.Empty }

        static member UpdateDto branchEvent currentBranchDto =
            let branchEventType = branchEvent.Event

            let newBranchDto =
                match branchEventType with
                | Created(branchId, branchName, parentBranchId, basedOn, ownerId, organizationId, repositoryId, initialPermissions) ->
                    let basedOnReferenceDto = deserialize<ReferenceDto> (branchEvent.Metadata.Properties["basedOnReferenceDto"].ToString())

                    let mutable branchDto =
                        { BranchDto.Default with
                            BranchId = branchId
                            BranchName = branchName
                            ParentBranchId = parentBranchId
                            BasedOn = basedOnReferenceDto
                            OwnerId = ownerId
                            OrganizationId = organizationId
                            RepositoryId = repositoryId
                            CreatedAt = branchEvent.Metadata.Timestamp }

                    for referenceType in initialPermissions do
                        branchDto <-
                            match referenceType with
                            | ReferenceType.Promotion -> { branchDto with PromotionEnabled = true }
                            | ReferenceType.Commit -> { branchDto with CommitEnabled = true }
                            | ReferenceType.Checkpoint -> { branchDto with CheckpointEnabled = true }
                            | ReferenceType.Save -> { branchDto with SaveEnabled = true }
                            | ReferenceType.Tag -> { branchDto with TagEnabled = true }
                            | ReferenceType.External -> { branchDto with ExternalEnabled = true }
                            | ReferenceType.Rebase -> branchDto // Rebase is always allowed. (Auto-rebase is optional, but rebase itself is always allowed.)

                    branchDto
                | Rebased referenceId ->
                    let basedOnReferenceDto = deserialize<ReferenceDto> (branchEvent.Metadata.Properties["basedOnReferenceDto"].ToString())
                    { currentBranchDto with BasedOn = basedOnReferenceDto }
                | NameSet branchName -> { currentBranchDto with BranchName = branchName }
                | Assigned(referenceDto, directoryVersion, sha256Hash, referenceText) ->
                    { currentBranchDto with LatestPromotion = referenceDto; BasedOn = referenceDto; ShouldRecomputeLatestReferences = true }

                | Promoted(referenceDto, directoryVersion, sha256Hash, referenceText) ->
                    { currentBranchDto with LatestPromotion = referenceDto; BasedOn = referenceDto; ShouldRecomputeLatestReferences = true }

                | Committed(referenceDto, directoryVersion, sha256Hash, referenceText) ->
                    { currentBranchDto with LatestCommit = referenceDto; ShouldRecomputeLatestReferences = true }

                | Checkpointed(referenceDto, directoryVersion, sha256Hash, referenceText) ->
                    { currentBranchDto with LatestCheckpoint = referenceDto; ShouldRecomputeLatestReferences = true }

                | Saved(referenceDto, directoryVersion, sha256Hash, referenceText) ->
                    { currentBranchDto with LatestSave = referenceDto; ShouldRecomputeLatestReferences = true }

                | Tagged(referenceDto, directoryVersion, sha256Hash, referenceText) -> { currentBranchDto with ShouldRecomputeLatestReferences = true }
                | ExternalCreated(referenceDto, directoryVersion, sha256Hash, referenceText) -> { currentBranchDto with ShouldRecomputeLatestReferences = true }
                | EnabledAssign enabled -> { currentBranchDto with AssignEnabled = enabled }
                | EnabledPromotion enabled -> { currentBranchDto with PromotionEnabled = enabled }
                | EnabledCommit enabled -> { currentBranchDto with CommitEnabled = enabled }
                | EnabledCheckpoint enabled -> { currentBranchDto with CheckpointEnabled = enabled }
                | EnabledSave enabled -> { currentBranchDto with SaveEnabled = enabled }
                | EnabledTag enabled -> { currentBranchDto with TagEnabled = enabled }
                | EnabledExternal enabled -> { currentBranchDto with ExternalEnabled = enabled }
                | EnabledAutoRebase enabled -> { currentBranchDto with AutoRebaseEnabled = enabled }
                | ReferenceRemoved _ -> currentBranchDto
                | LogicalDeleted(force, deleteReason) -> { currentBranchDto with DeletedAt = Some(getCurrentInstant ()); DeleteReason = deleteReason }

                | PhysicalDeleted -> currentBranchDto // Do nothing because it's about to be deleted anyway.
                | Undeleted -> { currentBranchDto with DeletedAt = None; DeleteReason = String.Empty }


            { newBranchDto with UpdatedAt = Some branchEvent.Metadata.Timestamp }
