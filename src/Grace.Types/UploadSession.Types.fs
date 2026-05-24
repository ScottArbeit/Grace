namespace Grace.Types

open Grace.Shared
open Grace.Shared.Utilities
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

    [<KnownType("GetKnownTypes")>]
    type UploadSessionCommand =
        | Start of start: StartUploadSession
        | IssueDedupeDiscovery of operationId: UploadSessionOperationId
        | RegisterBlockUploadIntent of operationId: UploadSessionOperationId * contentBlockAddress: ContentBlockAddress
        | ConfirmBlockUploaded of operationId: UploadSessionOperationId * contentBlockAddress: ContentBlockAddress
        | ClaimReuseRanges of operationId: UploadSessionOperationId
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
