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

module Branch =

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type BranchCommand =
        | Create of
            branchId: BranchId *
            branchName: BranchName *
            parentBranchId: BranchId *
            basedOn: ReferenceId *
            repositoryId: RepositoryId *
            initialPermissions: ReferenceType[]
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
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type BranchEventType =
        | Created of
            branchId: BranchId *
            branchName: BranchName *
            parentBranchId: BranchId *
            basedOn: ReferenceId *
            repositoryId: RepositoryId *
            initialPermissions: ReferenceType[]
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
    [<GenerateSerializer>]
    type BranchEvent =
        {
            /// The BranchEventType case that describes the event.
            Event: BranchEventType
            /// The EventMetadata for the event. EventMetadata includes the Timestamp, CorrelationId, Principal, and a Properties dictionary.
            Metadata: EventMetadata
        }

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type BranchDto =
        { Class: string
          BranchId: BranchId
          BranchName: BranchName
          ParentBranchId: BranchId
          BasedOn: ReferenceDto
          RepositoryId: RepositoryId
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
            { Class = nameof (BranchDto)
              BranchId = BranchId.Empty
              BranchName = BranchName "root"
              ParentBranchId = Constants.DefaultParentBranchId
              BasedOn = ReferenceDto.Default
              RepositoryId = RepositoryId.Empty
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
              ShouldRecomputeLatestReferences = false
              CreatedAt = Constants.DefaultTimestamp
              UpdatedAt = None
              DeletedAt = None
              DeleteReason = String.Empty }

        static member UpdateDto branchEvent correlationId currentBranchDto =
            task {
                let branchEventType = branchEvent.Event

                let! newBranchDto =
                    match branchEventType with
                    | Created(branchId, branchName, parentBranchId, basedOn, repositoryId, initialPermissions) ->
                        let basedOnReferenceDto = deserialize<ReferenceDto> branchEvent.Metadata.Properties["basedOnReferenceDto"]

                        let mutable branchDto =
                            { BranchDto.Default with
                                BranchId = branchId
                                BranchName = branchName
                                ParentBranchId = parentBranchId
                                BasedOn = basedOnReferenceDto
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

                        branchDto |> returnTask
                    | Rebased referenceId ->
                        let basedOnReferenceDto = deserialize<ReferenceDto> branchEvent.Metadata.Properties["basedOnReferenceDto"]
                        { currentBranchDto with BasedOn = basedOnReferenceDto } |> returnTask
                    | NameSet branchName -> { currentBranchDto with BranchName = branchName } |> returnTask
                    | Assigned(referenceDto, directoryVersion, sha256Hash, referenceText) ->
                        { currentBranchDto with LatestPromotion = referenceDto; BasedOn = referenceDto; ShouldRecomputeLatestReferences = true }
                        |> returnTask
                    | Promoted(referenceDto, directoryVersion, sha256Hash, referenceText) ->
                        { currentBranchDto with LatestPromotion = referenceDto; BasedOn = referenceDto; ShouldRecomputeLatestReferences = true }
                        |> returnTask
                    | Committed(referenceDto, directoryVersion, sha256Hash, referenceText) ->
                        { currentBranchDto with LatestCommit = referenceDto; ShouldRecomputeLatestReferences = true }
                        |> returnTask
                    | Checkpointed(referenceDto, directoryVersion, sha256Hash, referenceText) ->
                        { currentBranchDto with LatestCheckpoint = referenceDto; ShouldRecomputeLatestReferences = true }
                        |> returnTask
                    | Saved(referenceDto, directoryVersion, sha256Hash, referenceText) ->
                        { currentBranchDto with LatestSave = referenceDto; ShouldRecomputeLatestReferences = true }
                        |> returnTask
                    | Tagged(referenceDto, directoryVersion, sha256Hash, referenceText) ->
                        { currentBranchDto with ShouldRecomputeLatestReferences = true } |> returnTask
                    | ExternalCreated(referenceDto, directoryVersion, sha256Hash, referenceText) ->
                        { currentBranchDto with ShouldRecomputeLatestReferences = true } |> returnTask
                    | EnabledAssign enabled -> { currentBranchDto with AssignEnabled = enabled } |> returnTask
                    | EnabledPromotion enabled -> { currentBranchDto with PromotionEnabled = enabled } |> returnTask
                    | EnabledCommit enabled -> { currentBranchDto with CommitEnabled = enabled } |> returnTask
                    | EnabledCheckpoint enabled -> { currentBranchDto with CheckpointEnabled = enabled } |> returnTask
                    | EnabledSave enabled -> { currentBranchDto with SaveEnabled = enabled } |> returnTask
                    | EnabledTag enabled -> { currentBranchDto with TagEnabled = enabled } |> returnTask
                    | EnabledExternal enabled -> { currentBranchDto with ExternalEnabled = enabled } |> returnTask
                    | EnabledAutoRebase enabled -> { currentBranchDto with AutoRebaseEnabled = enabled } |> returnTask
                    | ReferenceRemoved _ -> currentBranchDto |> returnTask
                    | LogicalDeleted(force, deleteReason) ->
                        { currentBranchDto with DeletedAt = Some(getCurrentInstant ()); DeleteReason = deleteReason }
                        |> returnTask
                    | PhysicalDeleted -> currentBranchDto |> returnTask // Do nothing because it's about to be deleted anyway.
                    | Undeleted ->
                        { currentBranchDto with DeletedAt = None; DeleteReason = String.Empty }
                        |> returnTask

                return { newBranchDto with UpdatedAt = Some branchEvent.Metadata.Timestamp }
            }

        static member GetKnownTypes() = GetKnownTypes<BranchDto>()
