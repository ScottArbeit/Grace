namespace Grace.Types

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.ContentBlockMetadata
open Grace.Types.Types
open NodaTime
open Orleans
open System
open System.Runtime.Serialization

module UploadSession =

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type UploadSessionLifecycleState =
        | NotStarted
        | Started
        | Discovering
        | UploadingBlocks
        | ClaimingRanges
        | FinalizingManifest
        | Finalized
        | Abandoned
        | Expired
        | RetentionPending
        | StateDeleted

        static member GetKnownTypes() = GetKnownTypes<UploadSessionLifecycleState>()

    [<GenerateSerializer>]
    type StartUploadSession =
        {
            UploadSessionId: UploadSessionId
            OwnerId: OwnerId
            OrganizationId: OrganizationId
            RepositoryId: RepositoryId
            AuthorizedScope: RelativePath
            FileContentHash: FileContentHash
            ExpectedSize: int64
            ChunkingSuiteId: ChunkingSuiteId
            SamplingPolicySnapshot: string
            OperationId: UploadSessionOperationId
        }

    [<GenerateSerializer>]
    type RegisterBlockUploadIntent =
        {
            OperationId: UploadSessionOperationId
            ContentBlockAddress: ContentBlockAddress
            LogicalOffset: int64
            LogicalLength: int64
            ExpectedPayloadLength: int64
        }

    [<GenerateSerializer>]
    type ConfirmBlockUploaded =
        {
            OperationId: UploadSessionOperationId
            ContentBlockAddress: ContentBlockAddress
            Payload: byte array
            StoragePlacement: ContentBlockStoragePlacement
        }

    [<GenerateSerializer>]
    type BlockUploadIntent =
        {
            ContentBlockAddress: ContentBlockAddress
            LogicalOffset: int64
            LogicalLength: int64
            ExpectedPayloadLength: int64
            RegisteredAt: Instant
        }

    [<GenerateSerializer>]
    type ConfirmedBlockUpload =
        {
            ContentBlockAddress: ContentBlockAddress
            PayloadLength: int64
            StoragePlacement: ContentBlockStoragePlacement
            Ranges: ContentBlockMetadataRange array
            ConfirmedAt: Instant
        }

    /// Non-authoritative discovery evidence that may be used once, before expiry, to request reuse of an exact range.
    [<GenerateSerializer>]
    type ContentBlockReuseRangeHint =
        {
            StoragePoolId: StoragePoolId
            ContentBlockAddress: ContentBlockAddress
            OrdinalStart: int
            OrdinalCount: int
            MetadataVersion: MetadataVersion
        }

    /// Records the bounded policy returned by discovery so later claims can fail safely when hints age out.
    [<GenerateSerializer>]
    type DedupeDiscoverySnapshot =
        {
            OperationId: UploadSessionOperationId
            ExpiresAt: Instant
            MinimumReuseRunLength: int
            Hints: ContentBlockReuseRangeHint array
        }

    [<GenerateSerializer>]
    type IssueDedupeDiscovery =
        {
            OperationId: UploadSessionOperationId
            ExpiresAt: Instant
            MinimumReuseRunLength: int
            Hints: ContentBlockReuseRangeHint array
        }

    /// Claim request for a reuse range. Metadata must be the authoritative ContentBlockMetadata read at claim time.
    [<GenerateSerializer>]
    type ClaimReuseRange = { Hint: ContentBlockReuseRangeHint; Metadata: ContentBlockMetadata }

    [<GenerateSerializer>]
    type ClaimReuseRanges = { OperationId: UploadSessionOperationId; DiscoveryOperationId: UploadSessionOperationId; Ranges: ClaimReuseRange array }

    [<GenerateSerializer>]
    type ClaimedReuseRange =
        {
            StoragePoolId: StoragePoolId
            ContentBlockAddress: ContentBlockAddress
            OrdinalStart: int
            OrdinalCount: int
            PhysicalOffset: int64
            PhysicalLength: int64
            MetadataVersion: MetadataVersion
            ClaimedAt: Instant
        }

    [<KnownType("GetKnownTypes")>]
    type UploadSessionCommand =
        | Start of start: StartUploadSession
        | IssueDedupeDiscovery of discovery: IssueDedupeDiscovery
        | RegisterBlockUploadIntent of intent: RegisterBlockUploadIntent
        | ConfirmBlockUploaded of confirmation: ConfirmBlockUploaded
        | ClaimReuseRanges of claim: ClaimReuseRanges
        | FinalizeManifest of operationId: UploadSessionOperationId * manifestAddress: ManifestAddress
        | Abandon of operationId: UploadSessionOperationId
        | Expire of operationId: UploadSessionOperationId
        | DeletePhysicalState of operationId: UploadSessionOperationId

        static member GetKnownTypes() = GetKnownTypes<UploadSessionCommand>()

    [<KnownType("GetKnownTypes")>]
    type UploadSessionEventType =
        | Started of start: StartUploadSession
        | Abandoned of operationId: UploadSessionOperationId
        | Expired of operationId: UploadSessionOperationId
        | Finalized of operationId: UploadSessionOperationId * manifestAddress: ManifestAddress
        | CleanupReminderScheduled of operationId: UploadSessionOperationId * reminderTime: Instant
        | PhysicalStateDeleted of operationId: UploadSessionOperationId
        | BlockUploadIntentRegistered of operationId: UploadSessionOperationId * intent: BlockUploadIntent
        | BlockUploadConfirmed of operationId: UploadSessionOperationId * confirmedBlock: ConfirmedBlockUpload
        | DedupeDiscoveryIssued of operationId: UploadSessionOperationId * discovery: DedupeDiscoverySnapshot
        | ReuseRangesClaimed of operationId: UploadSessionOperationId * claimedRanges: ClaimedReuseRange array

        static member GetKnownTypes() = GetKnownTypes<UploadSessionEventType>()

    type UploadSessionEvent = { Event: UploadSessionEventType; Metadata: EventMetadata }

    [<GenerateSerializer>]
    type UploadSessionDto =
        {
            Class: string
            UploadSessionId: UploadSessionId
            OwnerId: OwnerId
            OrganizationId: OrganizationId
            RepositoryId: RepositoryId
            AuthorizedScope: RelativePath
            FileContentHash: FileContentHash
            ExpectedSize: int64
            ChunkingSuiteId: ChunkingSuiteId
            SamplingPolicySnapshot: string
            LifecycleState: UploadSessionLifecycleState
            StartedAt: Instant
            CompletedAt: Instant option
            FinalizedManifestAddress: ManifestAddress option
            BlockUploadIntents: BlockUploadIntent array
            ConfirmedBlockUploads: ConfirmedBlockUpload array
            DedupeDiscovery: DedupeDiscoverySnapshot option
            ClaimedReuseRanges: ClaimedReuseRange array
            CleanupReminderScheduledAt: Instant option
            CleanupReminderOperationId: UploadSessionOperationId option
            LastOperationId: UploadSessionOperationId option
        }

        static member Default =
            {
                Class = nameof UploadSessionDto
                UploadSessionId = UploadSessionId.Empty
                OwnerId = OwnerId.Empty
                OrganizationId = OrganizationId.Empty
                RepositoryId = RepositoryId.Empty
                AuthorizedScope = RelativePath String.Empty
                FileContentHash = FileContentHash String.Empty
                ExpectedSize = 0L
                ChunkingSuiteId = ChunkingSuiteId String.Empty
                SamplingPolicySnapshot = String.Empty
                LifecycleState = UploadSessionLifecycleState.NotStarted
                StartedAt = Constants.DefaultTimestamp
                CompletedAt = None
                FinalizedManifestAddress = None
                BlockUploadIntents = Array.empty
                ConfirmedBlockUploads = Array.empty
                DedupeDiscovery = None
                ClaimedReuseRanges = Array.empty
                CleanupReminderScheduledAt = None
                CleanupReminderOperationId = None
                LastOperationId = None
            }

        static member UpdateDto uploadSessionEvent current =
            match uploadSessionEvent.Event with
            | UploadSessionEventType.Started start ->
                { UploadSessionDto.Default with
                    UploadSessionId = start.UploadSessionId
                    OwnerId = start.OwnerId
                    OrganizationId = start.OrganizationId
                    RepositoryId = start.RepositoryId
                    AuthorizedScope = start.AuthorizedScope
                    FileContentHash = start.FileContentHash
                    ExpectedSize = start.ExpectedSize
                    ChunkingSuiteId = start.ChunkingSuiteId
                    SamplingPolicySnapshot = start.SamplingPolicySnapshot
                    LifecycleState = UploadSessionLifecycleState.Started
                    StartedAt = uploadSessionEvent.Metadata.Timestamp
                    LastOperationId = Some start.OperationId
                }
            | UploadSessionEventType.Abandoned operationId ->
                { current with
                    LifecycleState = UploadSessionLifecycleState.Abandoned
                    CompletedAt = Some uploadSessionEvent.Metadata.Timestamp
                    LastOperationId = Some operationId
                }
            | UploadSessionEventType.Expired operationId ->
                { current with
                    LifecycleState = UploadSessionLifecycleState.Expired
                    CompletedAt = Some uploadSessionEvent.Metadata.Timestamp
                    LastOperationId = Some operationId
                }
            | UploadSessionEventType.Finalized (operationId, manifestAddress) ->
                { current with
                    LifecycleState = UploadSessionLifecycleState.Finalized
                    FinalizedManifestAddress = Some manifestAddress
                    CompletedAt = Some uploadSessionEvent.Metadata.Timestamp
                    LastOperationId = Some operationId
                }
            | UploadSessionEventType.CleanupReminderScheduled (operationId, reminderTime) ->
                { current with
                    LifecycleState = UploadSessionLifecycleState.RetentionPending
                    CleanupReminderScheduledAt = Some reminderTime
                    CleanupReminderOperationId = Some operationId
                    LastOperationId = Some operationId
                }
            | UploadSessionEventType.PhysicalStateDeleted operationId ->
                { current with LifecycleState = UploadSessionLifecycleState.StateDeleted; LastOperationId = Some operationId }
            | UploadSessionEventType.BlockUploadIntentRegistered (operationId, intent) ->
                { current with
                    LifecycleState = UploadSessionLifecycleState.UploadingBlocks
                    BlockUploadIntents = Array.append current.BlockUploadIntents [| intent |]
                    LastOperationId = Some operationId
                }
            | UploadSessionEventType.BlockUploadConfirmed (operationId, confirmedBlock) ->
                let existing =
                    current.ConfirmedBlockUploads
                    |> Array.filter (fun existingBlock ->
                        existingBlock.ContentBlockAddress
                        <> confirmedBlock.ContentBlockAddress)

                { current with
                    LifecycleState = UploadSessionLifecycleState.UploadingBlocks
                    ConfirmedBlockUploads = Array.append existing [| confirmedBlock |]
                    LastOperationId = Some operationId
                }
            | UploadSessionEventType.DedupeDiscoveryIssued (operationId, discovery) ->
                { current with LifecycleState = UploadSessionLifecycleState.Discovering; DedupeDiscovery = Some discovery; LastOperationId = Some operationId }
            | UploadSessionEventType.ReuseRangesClaimed (operationId, claimedRanges) ->
                { current with
                    LifecycleState = UploadSessionLifecycleState.ClaimingRanges
                    ClaimedReuseRanges = Array.append current.ClaimedReuseRanges claimedRanges
                    LastOperationId = Some operationId
                }

    [<GenerateSerializer>]
    type UploadSessionDecision =
        {
            Session: UploadSessionDto
            OperationId: UploadSessionOperationId
            Events: UploadSessionEvent list
            WasIdempotentReplay: bool
            Message: string
        }

    [<GenerateSerializer>]
    type PhysicalDeletionReminderState =
        {
            UploadSessionId: UploadSessionId
            RepositoryId: RepositoryId
            OperationId: UploadSessionOperationId
            DeleteReason: DeleteReason
            CorrelationId: CorrelationId
        }
