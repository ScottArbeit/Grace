namespace Grace.Types

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.ContentBlockMetadata
open Grace.Types.Common
open Grace.Types.Library
open Grace.Types.Authorization
open NodaTime
open Orleans
open System
open System.Runtime.Serialization

/// Contains upload session helpers.
module UploadSession =

    /// Represents upload session lifecycle state.
    [<KnownType("GetKnownTypes")>]
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

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<UploadSessionLifecycleState>()

    /// Binds one Library operation to the existing temporary upload-session lifecycle.
    [<GenerateSerializer>]
    type LibraryUploadPreparation =
        {
            [<Id(0u)>]
            OperationId: LibraryOperationId
            [<Id(1u)>]
            PrincipalId: PrincipalId
            [<Id(2u)>]
            ExpectedSha256: string
        }

    /// Represents start upload session.
    [<GenerateSerializer>]
    type StartUploadSession =
        {
            [<Id(0u)>]
            UploadSessionId: UploadSessionId
            [<Id(1u)>]
            OwnerId: OwnerId
            [<Id(2u)>]
            OrganizationId: OrganizationId
            [<Id(3u)>]
            RepositoryId: RepositoryId
            [<Id(4u)>]
            StoragePoolId: StoragePoolId
            [<Id(5u)>]
            AuthorizedScope: RelativePath
            [<Id(6u)>]
            FileContentHash: FileContentHash
            [<Id(7u)>]
            ExpectedSize: int64
            [<Id(8u)>]
            ChunkingSuiteId: ChunkingSuiteId
            [<Id(9u)>]
            SamplingPolicySnapshot: string
            [<Id(10u)>]
            OperationId: UploadSessionOperationId
            [<Id(11u)>]
            LibraryPreparation: LibraryUploadPreparation option
        }

    /// Represents register block upload intent.
    [<GenerateSerializer>]
    type RegisterBlockUploadIntent =
        {
            [<Id(0u)>]
            OperationId: UploadSessionOperationId
            [<Id(1u)>]
            ContentBlockAddress: ContentBlockAddress
            [<Id(2u)>]
            LogicalOffset: int64
            [<Id(3u)>]
            LogicalLength: int64
            [<Id(4u)>]
            ExpectedPayloadLength: int64
        }

    /// Represents confirm block uploaded.
    [<GenerateSerializer>]
    type ConfirmBlockUploaded =
        {
            [<Id(0u)>]
            OperationId: UploadSessionOperationId
            [<Id(1u)>]
            ContentBlockAddress: ContentBlockAddress
            [<Id(2u)>]
            Payload: byte array
            [<Id(3u)>]
            StoragePlacement: ContentBlockStoragePlacement
        }

    /// Represents block upload intent.
    [<GenerateSerializer>]
    type BlockUploadIntent =
        {
            [<Id(0u)>]
            ContentBlockAddress: ContentBlockAddress
            [<Id(1u)>]
            LogicalOffset: int64
            [<Id(2u)>]
            LogicalLength: int64
            [<Id(3u)>]
            ExpectedPayloadLength: int64
            [<Id(4u)>]
            RegisteredAt: Instant
            [<Id(5u)>]
            PreparedPlacement: ContentBlockStoragePlacement option
        }

    /// Represents confirmed block upload.
    [<GenerateSerializer>]
    type ConfirmedBlockUpload =
        {
            [<Id(0u)>]
            ContentBlockAddress: ContentBlockAddress
            [<Id(1u)>]
            PayloadLength: int64
            [<Id(2u)>]
            StoragePlacement: ContentBlockStoragePlacement
            [<Id(3u)>]
            Ranges: ContentBlockMetadataRange array
            [<Id(4u)>]
            ConfirmedAt: Instant
        }

    /// Non-authoritative discovery evidence that may be used once, before expiry, to request reuse of an exact range.
    [<GenerateSerializer>]
    type ContentBlockReuseRangeHint =
        {
            [<Id(0u)>]
            StoragePoolId: StoragePoolId
            [<Id(1u)>]
            ContentBlockAddress: ContentBlockAddress
            [<Id(2u)>]
            OrdinalStart: int
            [<Id(3u)>]
            OrdinalCount: int
            [<Id(4u)>]
            MetadataVersion: MetadataVersion
        }

    /// Records the bounded policy returned by discovery so later claims can fail safely when hints age out.
    [<GenerateSerializer>]
    type DedupeDiscoverySnapshot =
        {
            [<Id(0u)>]
            OperationId: UploadSessionOperationId
            [<Id(1u)>]
            ExpiresAt: Instant
            [<Id(2u)>]
            MinimumReuseRunLength: int
            [<Id(3u)>]
            Hints: ContentBlockReuseRangeHint array
        }

    /// Represents issue dedupe discovery.
    [<GenerateSerializer>]
    type IssueDedupeDiscovery =
        {
            [<Id(0u)>]
            OperationId: UploadSessionOperationId
            [<Id(1u)>]
            ExpiresAt: Instant
            [<Id(2u)>]
            MinimumReuseRunLength: int
            [<Id(3u)>]
            Hints: ContentBlockReuseRangeHint array
        }

    /// Claim request for a reuse range. Metadata must be the authoritative ContentBlockMetadata read at claim time.
    [<GenerateSerializer>]
    type ClaimReuseRange =
        {
            [<Id(0u)>]
            Hint: ContentBlockReuseRangeHint
            [<Id(1u)>]
            Metadata: ContentBlockMetadata
        }

    /// Represents the claim reuse ranges contract.
    [<GenerateSerializer>]
    type ClaimReuseRanges =
        {
            [<Id(0u)>]
            OperationId: UploadSessionOperationId
            [<Id(1u)>]
            DiscoveryOperationId: UploadSessionOperationId
            [<Id(2u)>]
            Ranges: ClaimReuseRange array
        }

    /// Represents claimed reuse range.
    [<GenerateSerializer>]
    type ClaimedReuseRange =
        {
            [<Id(0u)>]
            StoragePoolId: StoragePoolId
            [<Id(1u)>]
            ContentBlockAddress: ContentBlockAddress
            [<Id(2u)>]
            OrdinalStart: int
            [<Id(3u)>]
            OrdinalCount: int
            [<Id(4u)>]
            PhysicalOffset: int64
            [<Id(5u)>]
            PhysicalLength: int64
            [<Id(6u)>]
            MetadataVersion: MetadataVersion
            [<Id(7u)>]
            ClaimedAt: Instant
        }

    /// Represents the finalize manifest block payload contract.
    [<GenerateSerializer>]
    type FinalizeManifestBlockPayload =
        {
            [<Id(0u)>]
            Address: ContentBlockAddress
            [<Id(1u)>]
            Payload: byte array
        }

    /// Represents finalize manifest.
    [<GenerateSerializer>]
    type FinalizeManifest =
        {
            [<Id(0u)>]
            OperationId: UploadSessionOperationId
            [<Id(1u)>]
            Manifest: FileManifest
            [<Id(2u)>]
            BlockPayloads: FinalizeManifestBlockPayload array
            [<Id(3u)>]
            ClaimedMetadata: ContentBlockMetadata array
        }

    /// Represents upload session command.
    [<KnownType("GetKnownTypes")>]
    type UploadSessionCommand =
        | Start of start: StartUploadSession
        | IssueDedupeDiscovery of discovery: IssueDedupeDiscovery
        | RegisterBlockUploadIntent of intent: RegisterBlockUploadIntent
        | ConfirmBlockUploaded of confirmation: ConfirmBlockUploaded
        | ClaimReuseRanges of claim: ClaimReuseRanges
        | FinalizeManifest of finalize: FinalizeManifest
        | Abandon of operationId: UploadSessionOperationId
        | Expire of operationId: UploadSessionOperationId
        | DeletePhysicalState of operationId: UploadSessionOperationId

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<UploadSessionCommand>()

    /// Represents upload session event type.
    [<KnownType("GetKnownTypes")>]
    type UploadSessionEventType =
        | Started of start: StartUploadSession
        | Abandoned of operationId: UploadSessionOperationId
        | Expired of operationId: UploadSessionOperationId
        | Finalized of operationId: UploadSessionOperationId * manifest: FileManifest
        | CleanupReminderScheduled of operationId: UploadSessionOperationId * reminderTime: Instant
        | PhysicalStateDeleted of operationId: UploadSessionOperationId
        | BlockUploadIntentRegistered of operationId: UploadSessionOperationId * intent: BlockUploadIntent
        | BlockUploadConfirmed of operationId: UploadSessionOperationId * confirmedBlock: ConfirmedBlockUpload
        | DedupeDiscoveryIssued of operationId: UploadSessionOperationId * discovery: DedupeDiscoverySnapshot
        | ReuseRangesClaimed of operationId: UploadSessionOperationId * claimedRanges: ClaimedReuseRange array
        | RetryWindowRecovered of observedAt: Instant
        | RetryWindowClosed
        | BlockUploadPrepared of address: ContentBlockAddress * placement: ContentBlockStoragePlacement

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<UploadSessionEventType>()

    /// Represents the upload session event contract.
    [<GenerateSerializer>]
    type UploadSessionEvent =
        {
            [<Id(0u)>]
            Event: UploadSessionEventType
            [<Id(1u)>]
            Metadata: EventMetadata
        }

    /// Represents upload session dto.
    [<GenerateSerializer>]
    type UploadSessionDto =
        {
            [<Id(0u)>]
            Class: string
            [<Id(1u)>]
            UploadSessionId: UploadSessionId
            [<Id(2u)>]
            OwnerId: OwnerId
            [<Id(3u)>]
            OrganizationId: OrganizationId
            [<Id(4u)>]
            RepositoryId: RepositoryId
            [<Id(5u)>]
            StoragePoolId: StoragePoolId
            [<Id(6u)>]
            AuthorizedScope: RelativePath
            [<Id(7u)>]
            FileContentHash: FileContentHash
            [<Id(8u)>]
            ExpectedSize: int64
            [<Id(9u)>]
            ChunkingSuiteId: ChunkingSuiteId
            [<Id(10u)>]
            SamplingPolicySnapshot: string
            [<Id(11u)>]
            LifecycleState: UploadSessionLifecycleState
            [<Id(12u)>]
            StartedAt: Instant
            [<Id(13u)>]
            CompletedAt: Instant option
            [<Id(14u)>]
            FinalizedManifestAddress: ManifestAddress option
            [<Id(15u)>]
            FinalizedManifest: FileManifest option
            [<Id(16u)>]
            LibraryPreparation: LibraryUploadPreparation option
            [<Id(17u)>]
            BlockUploadIntents: BlockUploadIntent array
            [<Id(18u)>]
            ConfirmedBlockUploads: ConfirmedBlockUpload array
            [<Id(19u)>]
            DedupeDiscovery: DedupeDiscoverySnapshot option
            [<Id(20u)>]
            ClaimedReuseRanges: ClaimedReuseRange array
            [<Id(21u)>]
            CleanupReminderScheduledAt: Instant option
            [<Id(22u)>]
            CleanupReminderOperationId: UploadSessionOperationId option
            [<Id(23u)>]
            LastOperationId: UploadSessionOperationId option
            [<Id(24u)>]
            RetryExpiresAt: Instant option
            [<Id(25u)>]
            RetryWindowAdvancedAt: Instant option
        }

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default =
            {
                Class = nameof UploadSessionDto
                UploadSessionId = UploadSessionId.Empty
                OwnerId = OwnerId.Empty
                OrganizationId = OrganizationId.Empty
                RepositoryId = RepositoryId.Empty
                StoragePoolId = StoragePoolId String.Empty
                AuthorizedScope = RelativePath String.Empty
                FileContentHash = FileContentHash String.Empty
                ExpectedSize = 0L
                ChunkingSuiteId = ChunkingSuiteId String.Empty
                SamplingPolicySnapshot = String.Empty
                LifecycleState = UploadSessionLifecycleState.NotStarted
                StartedAt = Constants.DefaultTimestamp
                CompletedAt = None
                FinalizedManifestAddress = None
                FinalizedManifest = None
                LibraryPreparation = None
                BlockUploadIntents = Array.empty
                ConfirmedBlockUploads = Array.empty
                DedupeDiscovery = None
                ClaimedReuseRanges = Array.empty
                CleanupReminderScheduledAt = None
                CleanupReminderOperationId = None
                LastOperationId = None
                RetryExpiresAt = None
                RetryWindowAdvancedAt = None
            }

        /// Creates the DTO shape used to carry partial updates without mutating the persisted aggregate directly.
        static member UpdateDto uploadSessionEvent current =
            let progressTime = uploadSessionEvent.Metadata.Timestamp

            /// Resets the one shared retry deadline only for a newly persisted progress event.
            let advance (session: UploadSessionDto) =
                { session with RetryExpiresAt = Some(progressTime + Duration.FromHours 1); RetryWindowAdvancedAt = Some progressTime }

            match uploadSessionEvent.Event with
            | UploadSessionEventType.BlockUploadPrepared (address, placement) ->
                { current with
                    BlockUploadIntents =
                        current.BlockUploadIntents
                        |> Array.map (fun intent ->
                            if intent.ContentBlockAddress = address then
                                { intent with PreparedPlacement = Some placement }
                            else
                                intent)
                }
            | UploadSessionEventType.RetryWindowRecovered observedAt ->
                { current with RetryExpiresAt = Some(observedAt + Duration.FromHours 1); RetryWindowAdvancedAt = Some observedAt }
            | UploadSessionEventType.RetryWindowClosed -> { current with RetryExpiresAt = None }
            | UploadSessionEventType.Started start ->
                { UploadSessionDto.Default with
                    UploadSessionId = start.UploadSessionId
                    OwnerId = start.OwnerId
                    OrganizationId = start.OrganizationId
                    RepositoryId = start.RepositoryId
                    StoragePoolId = start.StoragePoolId
                    AuthorizedScope = start.AuthorizedScope
                    FileContentHash = start.FileContentHash
                    ExpectedSize = start.ExpectedSize
                    ChunkingSuiteId = start.ChunkingSuiteId
                    SamplingPolicySnapshot = start.SamplingPolicySnapshot
                    LibraryPreparation = start.LibraryPreparation
                    LifecycleState = UploadSessionLifecycleState.Started
                    StartedAt = uploadSessionEvent.Metadata.Timestamp
                    LastOperationId = Some start.OperationId
                }
                |> advance
            | UploadSessionEventType.Abandoned operationId ->
                { current with
                    LifecycleState = UploadSessionLifecycleState.Abandoned
                    CompletedAt = Some uploadSessionEvent.Metadata.Timestamp
                    LastOperationId = Some operationId
                    RetryExpiresAt = None
                }
            | UploadSessionEventType.Expired operationId ->
                { current with
                    LifecycleState = UploadSessionLifecycleState.Expired
                    CompletedAt = Some uploadSessionEvent.Metadata.Timestamp
                    LastOperationId = Some operationId
                    RetryExpiresAt = None
                }
            | UploadSessionEventType.Finalized (operationId, manifest) ->
                { current with
                    LifecycleState = UploadSessionLifecycleState.Finalized
                    FinalizedManifestAddress = Some manifest.ManifestAddress
                    FinalizedManifest = Some manifest
                    CompletedAt = Some uploadSessionEvent.Metadata.Timestamp
                    LastOperationId = Some operationId
                }
                |> advance
            | UploadSessionEventType.CleanupReminderScheduled (operationId, reminderTime) ->
                { current with
                    LifecycleState = UploadSessionLifecycleState.RetentionPending
                    CleanupReminderScheduledAt = Some reminderTime
                    CleanupReminderOperationId = Some operationId
                    LastOperationId = Some operationId
                }
            | UploadSessionEventType.PhysicalStateDeleted operationId ->
                { current with
                    LifecycleState = UploadSessionLifecycleState.StateDeleted
                    BlockUploadIntents = Array.empty
                    ConfirmedBlockUploads = Array.empty
                    DedupeDiscovery = None
                    ClaimedReuseRanges = Array.empty
                    CleanupReminderScheduledAt = None
                    CleanupReminderOperationId = None
                    FinalizedManifest = None
                    LibraryPreparation = None
                    LastOperationId = Some operationId
                    RetryExpiresAt = None
                }
            | UploadSessionEventType.BlockUploadIntentRegistered (operationId, intent) ->
                { current with
                    LifecycleState = UploadSessionLifecycleState.UploadingBlocks
                    BlockUploadIntents = Array.append current.BlockUploadIntents [| intent |]
                    LastOperationId = Some operationId
                }
            | UploadSessionEventType.BlockUploadConfirmed (operationId, confirmedBlock) ->
                let newAddress =
                    current.ConfirmedBlockUploads
                    |> Array.forall (fun block ->
                        block.ContentBlockAddress
                        <> confirmedBlock.ContentBlockAddress)

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
                |> fun next -> if newAddress then advance next else next
            | UploadSessionEventType.DedupeDiscoveryIssued (operationId, discovery) ->
                { current with LifecycleState = UploadSessionLifecycleState.Discovering; DedupeDiscovery = Some discovery; LastOperationId = Some operationId }
            | UploadSessionEventType.ReuseRangesClaimed (operationId, claimedRanges) ->
                let newRange =
                    claimedRanges
                    |> Array.exists (fun range ->
                        current.ClaimedReuseRanges
                        |> Array.forall (fun prior ->
                            prior.ContentBlockAddress
                            <> range.ContentBlockAddress
                            || prior.OrdinalStart <> range.OrdinalStart
                            || prior.OrdinalCount <> range.OrdinalCount))

                { current with
                    LifecycleState = UploadSessionLifecycleState.ClaimingRanges
                    ClaimedReuseRanges = Array.append current.ClaimedReuseRanges claimedRanges
                    LastOperationId = Some operationId
                }
                |> fun next -> if newRange then advance next else next

    /// Represents upload session decision.
    [<GenerateSerializer>]
    type UploadSessionDecision =
        {
            [<Id(0u)>]
            Session: UploadSessionDto
            [<Id(1u)>]
            OperationId: UploadSessionOperationId
            [<Id(2u)>]
            Events: UploadSessionEvent list
            [<Id(3u)>]
            WasIdempotentReplay: bool
            [<Id(4u)>]
            Message: string
        }

    /// Indicates whether the upload session still points at the supplied finalized manifest address.
    let retainsFinalizedManifest manifestAddress (session: UploadSessionDto) =
        not (String.IsNullOrWhiteSpace manifestAddress)
        && session.FinalizedManifestAddress = Some manifestAddress

    /// Represents physical deletion reminder state.
    [<GenerateSerializer>]
    type PhysicalDeletionReminderState =
        {
            [<Id(0u)>]
            UploadSessionId: UploadSessionId
            [<Id(1u)>]
            RepositoryId: RepositoryId
            [<Id(2u)>]
            OperationId: UploadSessionOperationId
            [<Id(3u)>]
            DeleteReason: DeleteReason
            [<Id(4u)>]
            CorrelationId: CorrelationId
        }
