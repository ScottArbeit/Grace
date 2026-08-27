namespace Grace.Types

open Grace.Types.Authorization
open Grace.Types.Common
open NodaTime
open Orleans
open System
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks

/// Defines the complete wire-safe remote Synchronized Content contract.
module SynchronizedContent =

    type SynchronizedItemId = Guid
    type SynchronizedOperationId = Guid
    type SynchronizedRootConfigurationVersion = Guid
    type SynchronizedNamespaceVersion = Guid
    type SynchronizedNamespaceSlotVersion = Guid
    type SynchronizedContentVersionId = Guid
    type SynchronizedBootstrapId = Guid
    type SynchronizedPreparedContentId = Guid
    type SynchronizedCursor = string
    type SynchronizedCursorEpoch = string
    type SynchronizedPageToken = string

    /// Provides the accepted lower-camel wire values for synchronized item kinds.
    [<RequireQualifiedAccess>]
    module ItemKind =
        [<Literal>]
        let File = "file"

        [<Literal>]
        let Directory = "directory"

    /// Provides the accepted lower-camel wire values for synchronized mutations.
    [<RequireQualifiedAccess>]
    module MutationKind =
        [<Literal>]
        let CreateFile = "createFile"

        [<Literal>]
        let CreateDirectory = "createDirectory"

        [<Literal>]
        let UpdateContent = "updateContent"

        [<Literal>]
        let Rename = "rename"

        [<Literal>]
        let Move = "move"

        [<Literal>]
        let Delete = "delete"

    /// Provides the accepted lower-camel wire values for synchronized outcomes.
    [<RequireQualifiedAccess>]
    module OutcomeKind =
        [<Literal>]
        let Accepted = "accepted"

        [<Literal>]
        let Unchanged = "unchanged"

        [<Literal>]
        let Rejected = "rejected"

        [<Literal>]
        let ConflictCopy = "conflictCopy"

        [<Literal>]
        let StalePolicy = "stalePolicy"

        [<Literal>]
        let RebaselineRequired = "rebaselineRequired"

        [<Literal>]
        let LocalIncomplete = "localIncomplete"

    /// Provides the exact rejection reason values returned by remote mutation admission.
    [<RequireQualifiedAccess>]
    module RejectionReason =
        [<Literal>]
        let NamespaceChanged = "namespaceChanged"

        [<Literal>]
        let ContentChanged = "contentChanged"

        [<Literal>]
        let SlotOccupied = "slotOccupied"

        [<Literal>]
        let ItemMissing = "itemMissing"

        [<Literal>]
        let ItemTombstoned = "itemTombstoned"

        [<Literal>]
        let DirectoryNotEmpty = "directoryNotEmpty"

        [<Literal>]
        let KindMismatch = "kindMismatch"

        [<Literal>]
        let PreparedContentExpired = "preparedContentExpired"

        [<Literal>]
        let OperationIdentityMismatch = "operationIdentityMismatch"

    /// Provides the exact rejection values used by root configuration changes.
    [<RequireQualifiedAccess>]
    module RootRejectionReason =
        [<Literal>]
        let SlotOccupied = "slotOccupied"

        [<Literal>]
        let OutgoingSystemNotEmpty = "outgoingSystemNotEmpty"

        [<Literal>]
        let RootOverlap = "rootOverlap"

        [<Literal>]
        let RootLimitExceeded = "rootLimitExceeded"

        [<Literal>]
        let UnsupportedPath = "unsupportedPath"

    /// Identifies a synchronized item's repository-owned parent.
    [<CLIMutable; GenerateSerializer>]
    type SynchronizedParentDto = { Kind: string; RootPath: string option; ItemId: SynchronizedItemId option }

    /// Describes one current parent/name placement and its independent concurrency versions.
    [<CLIMutable; GenerateSerializer>]
    type SynchronizedNamespaceDto =
        {
            Parent: SynchronizedParentDto
            Name: string
            NormalizedPath: string
            NamespaceVersion: SynchronizedNamespaceVersion
            SlotVersion: SynchronizedNamespaceSlotVersion
        }

    /// Identifies one immutable complete-byte value without exposing its storage placement.
    [<CLIMutable; GenerateSerializer>]
    type SynchronizedContentVersionDto =
        {
            ContentVersionId: SynchronizedContentVersionId
            Blake3Hash: string
            Sha256Hash: string
            Size: int64
            CreatedAt: Instant
        }

    /// Records the durable deletion of one stable synchronized item identity.
    [<CLIMutable; GenerateSerializer>]
    type SynchronizedTombstoneDto =
        {
            ItemId: SynchronizedItemId
            ItemKind: string
            DeletedAt: Instant
            DeletedBy: PrincipalId
            DeleteCursor: SynchronizedCursor
            LastNamespaceVersion: SynchronizedNamespaceVersion
            LastContentVersionId: SynchronizedContentVersionId option
        }

    /// Records how competing complete bytes became a deterministic conflict copy.
    [<CLIMutable; GenerateSerializer>]
    type SynchronizedConflictProvenanceDto =
        {
            SourceOperationId: SynchronizedOperationId
            SourceItemId: SynchronizedItemId
            CanonicalItemId: SynchronizedItemId
            ConflictItemId: SynchronizedItemId
            ConflictPath: string
            AcceptedAt: Instant
            SourceContentVersionId: SynchronizedContentVersionId option
            BaseContentVersionId: SynchronizedContentVersionId option
        }

    /// Describes the current live or tombstoned state for one stable synchronized item.
    [<CLIMutable; GenerateSerializer>]
    type SynchronizedItemDto =
        {
            ItemId: SynchronizedItemId
            ItemKind: string
            State: string
            LastMutationCursor: SynchronizedCursor
            RootConfigurationVersion: SynchronizedRootConfigurationVersion
            Namespace: SynchronizedNamespaceDto option
            Content: SynchronizedContentVersionDto option
            Tombstone: SynchronizedTombstoneDto option
        }

    /// Describes one occupied or remembered-vacant normalized namespace slot.
    [<CLIMutable; GenerateSerializer>]
    type SynchronizedNamespaceSlotDto =
        {
            Parent: SynchronizedParentDto
            Name: string
            NormalizedPath: string
            SlotVersion: SynchronizedNamespaceSlotVersion
            State: string
            OccupantItemId: SynchronizedItemId option
        }

    /// Carries the exact current repository-owned synchronization root set.
    [<CLIMutable; GenerateSerializer>]
    type SynchronizedRootConfigurationDto =
        {
            RepositoryId: RepositoryId
            Version: SynchronizedRootConfigurationVersion
            Roots: string array
            CreatedAt: Instant
            CreatedBy: PrincipalId
            PreviousVersion: SynchronizedRootConfigurationVersion option
        }

        /// Builds the persisted empty root configuration represented by a repository's immutable creation event.
        static member CreateInitial(repositoryId: RepositoryId, createdAt: Instant, createdBy: PrincipalId) =
            let seed = Encoding.UTF8.GetBytes($"Grace.SynchronizedContent.RootConfiguration.v1:{repositoryId:D}")
            let hash = SHA256.HashData(seed)
            let versionBytes = hash[0..15]
            versionBytes[6] <- (versionBytes[6] &&& 0x0Fuy) ||| 0x50uy
            versionBytes[8] <- (versionBytes[8] &&& 0x3Fuy) ||| 0x80uy

            {
                RepositoryId = repositoryId
                Version = Guid versionBytes
                Roots = Array.empty
                CreatedAt = createdAt
                CreatedBy = createdBy
                PreviousVersion = None
            }

    /// Proves the namespace dimension observed by a rename, move, or delete caller.
    [<CLIMutable; GenerateSerializer>]
    type SynchronizedNamespacePreconditionDto = { ItemId: SynchronizedItemId; ExpectedNamespaceVersion: SynchronizedNamespaceVersion }

    /// Proves the content dimension observed by an update or file delete caller.
    [<CLIMutable; GenerateSerializer>]
    type SynchronizedContentPreconditionDto = { ItemId: SynchronizedItemId; ExpectedContentVersionId: SynchronizedContentVersionId }

    /// Proves that the caller observed one exact vacant destination slot.
    [<CLIMutable; GenerateSerializer>]
    type SynchronizedCreationSlotExpectationDto =
        {
            Parent: SynchronizedParentDto
            Name: string
            ExpectedSlotVersion: SynchronizedNamespaceSlotVersion
            ExpectedState: string
        }

    /// Describes one accepted repository-ordered synchronized mutation.
    [<CLIMutable; GenerateSerializer>]
    type SynchronizedMutationDto =
        {
            Cursor: SynchronizedCursor
            OperationId: SynchronizedOperationId
            MutationKind: string
            ItemId: SynchronizedItemId
            ItemKind: string
            AcceptedAt: Instant
            AcceptedBy: PrincipalId
            RootConfigurationVersion: SynchronizedRootConfigurationVersion
            Namespace: SynchronizedNamespaceDto option
            Content: SynchronizedContentVersionDto option
            Tombstone: SynchronizedTombstoneDto option
            Conflict: SynchronizedConflictProvenanceDto option
        }

    /// Directs a client to restart from the current published baseline.
    [<CLIMutable; GenerateSerializer>]
    type SynchronizedRebaselineDto =
        {
            Reason: string
            CurrentEpoch: SynchronizedCursorEpoch
            ServiceFloorCursor: SynchronizedCursor
            RecommendedBootstrap: bool
        }

    /// Carries the stable result recorded for one normalized operation request.
    [<CLIMutable; GenerateSerializer>]
    type SynchronizedOperationReceiptDto =
        {
            OperationId: SynchronizedOperationId
            RequestHash: string
            Outcome: string
            RootConfigurationVersion: SynchronizedRootConfigurationVersion
            RecordedAt: Instant
            PrincipalId: PrincipalId
            Mutation: SynchronizedMutationDto option
            Cursor: SynchronizedCursor option
            Item: SynchronizedItemDto option
            Conflict: SynchronizedConflictProvenanceDto option
            ReasonCode: string option
            CurrentRootConfiguration: SynchronizedRootConfigurationDto option
            Rebaseline: SynchronizedRebaselineDto option
        }

    /// Carries one exact-version root add or remove result.
    [<CLIMutable; GenerateSerializer>]
    type SynchronizedRootMutationResultDto =
        {
            OperationId: SynchronizedOperationId
            Outcome: string
            RootConfiguration: SynchronizedRootConfigurationDto
            ReasonCode: string option
            RecordedAt: Instant
        }

    /// Carries one bounded immutable current-state bootstrap page.
    [<CLIMutable; GenerateSerializer>]
    type SynchronizedBootstrapPageDto =
        {
            BootstrapId: SynchronizedBootstrapId
            BoundaryCursor: SynchronizedCursor
            CursorEpoch: SynchronizedCursorEpoch
            RootConfiguration: SynchronizedRootConfigurationDto
            Items: SynchronizedItemDto array
            NextPageToken: SynchronizedPageToken option
        }

    /// Carries ordered accepted mutations or a typed rebaseline instruction.
    [<CLIMutable; GenerateSerializer>]
    type SynchronizedDeltaResultDto =
        {
            Outcome: string
            CursorEpoch: SynchronizedCursorEpoch
            Mutations: SynchronizedMutationDto array
            LastCursor: SynchronizedCursor
            HasMore: bool
            NextPageToken: SynchronizedPageToken option
            Rebaseline: SynchronizedRebaselineDto option
        }

    /// Binds immutable bytes to an authorized short-lived mutation preparation.
    [<CLIMutable; GenerateSerializer>]
    type SynchronizedPreparedContentDto =
        {
            PreparedContentId: SynchronizedPreparedContentId
            Blake3Hash: string
            Sha256Hash: string
            Size: int64
            UploadRequired: bool
            UploadInstructions: string option
            ExpiresAt: Instant
        }

    /// Grants one authorized short-lived read of exact immutable bytes.
    [<CLIMutable; GenerateSerializer>]
    type SynchronizedContentReadGrantDto = { GrantId: string; DownloadPath: string; Content: SynchronizedContentVersionDto; ExpiresAt: Instant }

    /// Reports server synchronization progress without exposing storage or content details.
    [<CLIMutable; GenerateSerializer>]
    type SynchronizedRepositoryStatusDto =
        {
            State: string
            RepositoryId: RepositoryId
            RootConfigurationVersion: SynchronizedRootConfigurationVersion
            IsCaughtUp: bool
            RebaselineRequired: bool
            IsBlocked: bool
            PendingOperationCount: int
            OldestPendingAgeMilliseconds: int64 option
            ProjectionLagCount: int64
            LastCompletedAt: Instant option
        }

    /// Publishes a coarse hint that authorized clients should pull after their durable cursor.
    [<CLIMutable; GenerateSerializer>]
    type SynchronizedContentAvailable =
        {
            EventName: string
            RepositoryId: RepositoryId
            CursorEpoch: SynchronizedCursorEpoch
            AvailableAfterCursor: SynchronizedCursor
            RootConfigurationVersion: SynchronizedRootConfigurationVersion
            OccurredAt: Instant
            CorrelationId: CorrelationId
        }

        /// Builds the only Product V1 public wake event.
        static member Create(repositoryId, cursorEpoch, availableAfterCursor, rootConfigurationVersion, occurredAt, correlationId) =
            {
                EventName = "SynchronizedContentAvailable.v1"
                RepositoryId = repositoryId
                CursorEpoch = cursorEpoch
                AvailableAfterCursor = availableAfterCursor
                RootConfigurationVersion = rootConfigurationVersion
                OccurredAt = occurredAt
                CorrelationId = correlationId
            }

    /// Carries the internal mutation request after public validation and authorization are complete.
    [<GenerateSerializer>]
    type SynchronizedMutationCommand =
        {
            RepositoryId: RepositoryId
            OperationId: SynchronizedOperationId
            RequestHash: string
            RootConfigurationVersion: SynchronizedRootConfigurationVersion
            MutationKind: string
            ItemKind: string
            ItemId: SynchronizedItemId option
            NamespacePrecondition: SynchronizedNamespacePreconditionDto option
            ContentPrecondition: SynchronizedContentPreconditionDto option
            CreationSlotExpectation: SynchronizedCreationSlotExpectationDto option
            DestinationParent: SynchronizedParentDto option
            DestinationName: string option
            PreparedContentId: SynchronizedPreparedContentId option
            PreparedContent: SynchronizedContentVersionDto option
            PreparedContentExpiresAt: Instant option
        }

    /// Tracks the independently repairable projection positions for one repository.
    [<CLIMutable; GenerateSerializer>]
    type SynchronizedProjectionWatermarks =
        {
            Current: int64
            History: int64
            Receipts: int64
            Baselines: int64
        }

        /// Returns the empty projection state before any accepted mutation.
        static member Empty = { Current = 0L; History = 0L; Receipts = 0L; Baselines = 0L }

    /// Persists the immutable canonical commit record for one accepted mutation.
    [<CLIMutable; GenerateSerializer>]
    type SynchronizedCanonicalMutationDocument =
        {
            id: string
            RepositoryId: RepositoryId
            Scope: string
            SchemaVersion: int
            Cursor: int64
            PublicCursor: SynchronizedCursor
            OperationId: SynchronizedOperationId
            RequestHash: string
            Mutation: SynchronizedMutationDto
            PriorNamespace: SynchronizedNamespaceDto option
            PriorContentVersionId: SynchronizedContentVersionId option
            ConsumedNamespaceVersion: SynchronizedNamespaceVersion option
            ConsumedContentVersionId: SynchronizedContentVersionId option
            ConsumedSlotVersion: SynchronizedNamespaceSlotVersion option
            CorrelationId: CorrelationId
        }

    /// Persists the complete deterministic reservation that activation can finish without guessing.
    [<CLIMutable; GenerateSerializer>]
    type SynchronizedPendingCommandDocument =
        {
            OperationId: SynchronizedOperationId
            RequestHash: string
            Cursor: int64
            Receipt: SynchronizedOperationReceiptDto
            CanonicalMutation: SynchronizedCanonicalMutationDocument
            ExpectedRootConfigurationVersion: SynchronizedRootConfigurationVersion
            PrincipalId: PrincipalId
            CorrelationId: CorrelationId
            ReservedAt: Instant
            TargetItemIds: SynchronizedItemId array
        }

    /// Persists the bounded serialized command lane and repository synchronization configuration.
    [<CLIMutable; GenerateSerializer>]
    type SynchronizedControlDocument =
        {
            id: string
            RepositoryId: RepositoryId
            Scope: string
            SchemaVersion: int
            CursorEpoch: Guid
            NextCursor: int64
            AppliedThrough: int64
            ReplayFloor: int64
            RootConfiguration: SynchronizedRootConfigurationDto
            Pending: SynchronizedPendingCommandDocument option
            CurrentBaselineId: SynchronizedBootstrapId option
            CurrentBaselineCursor: int64 option
            ProjectionWatermarks: SynchronizedProjectionWatermarks
            UpdatedAt: Instant
        }

    /// Stores one rebuildable current item projection and its canonical position.
    [<CLIMutable; GenerateSerializer>]
    type SynchronizedCurrentItemDocument =
        {
            id: string
            RepositoryId: RepositoryId
            Scope: string
            SchemaVersion: int
            Item: SynchronizedItemDto
            LastCursor: int64
            AppliedThrough: int64
        }

    /// Stores one rebuildable namespace-slot projection and its canonical position.
    [<CLIMutable; GenerateSerializer>]
    type SynchronizedCurrentSlotDocument =
        {
            id: string
            RepositoryId: RepositoryId
            Scope: string
            SchemaVersion: int
            Slot: SynchronizedNamespaceSlotDto
            LastCursor: int64
            AppliedThrough: int64
        }

    /// Stores one deterministic operation receipt for response-loss recovery.
    [<CLIMutable; GenerateSerializer>]
    type SynchronizedReceiptDocument =
        {
            id: string
            RepositoryId: RepositoryId
            Scope: string
            SchemaVersion: int
            OperationId: SynchronizedOperationId
            RequestHash: string
            Receipt: SynchronizedOperationReceiptDto
            Cursor: int64 option
            AppliedThrough: int64
        }

    /// Stores one canonical-derived item or path history entry.
    [<CLIMutable; GenerateSerializer>]
    type SynchronizedHistoryEntry =
        {
            Cursor: int64
            PublicCursor: SynchronizedCursor
            OperationId: SynchronizedOperationId
            ItemId: SynchronizedItemId
            PriorNamespace: SynchronizedNamespaceDto option
            ResultingNamespace: SynchronizedNamespaceDto option
            PriorContentVersionId: SynchronizedContentVersionId option
            ResultingContentVersionId: SynchronizedContentVersionId option
            Tombstone: SynchronizedTombstoneDto option
            Conflict: SynchronizedConflictProvenanceDto option
            PrincipalId: PrincipalId
            AcceptedAt: Instant
        }

    /// Stores at most 512 canonical-derived history entries under a byte bound.
    [<CLIMutable; GenerateSerializer>]
    type SynchronizedHistorySegmentDocument =
        {
            id: string
            RepositoryId: RepositoryId
            Scope: string
            SchemaVersion: int
            Segment: int64
            FirstCursor: int64
            LastCursor: int64
            EntryCount: int
            Entries: SynchronizedHistoryEntry array
        }

    /// Stores one immutable byte-bounded current-state baseline shard.
    [<CLIMutable; GenerateSerializer>]
    type SynchronizedBaselineShardDocument =
        {
            id: string
            RepositoryId: RepositoryId
            Scope: string
            SchemaVersion: int
            BaselineId: SynchronizedBootstrapId
            BoundaryCursor: int64
            Items: SynchronizedItemDto array
            ItemCount: int
            SerializedBytes: int
        }

    /// Publishes a baseline only after every named shard is durable and hash-verified.
    [<CLIMutable; GenerateSerializer>]
    type SynchronizedBaselineManifestDocument =
        {
            id: string
            RepositoryId: RepositoryId
            Scope: string
            SchemaVersion: int
            BaselineId: SynchronizedBootstrapId
            BoundaryCursor: int64
            CursorEpoch: Guid
            RootConfigurationVersion: SynchronizedRootConfigurationVersion
            ShardIds: string array
            ShardHashes: string array
            ShardItemCounts: int array
            TotalItemCount: int
            CreatedAt: Instant
        }

    /// Persists one principal-bound immutable-content preparation until its existing upload session is finalized or expires.
    [<CLIMutable; GenerateSerializer>]
    type SynchronizedPreparedContentDocument =
        {
            id: string
            RepositoryId: RepositoryId
            Scope: string
            SchemaVersion: int
            PreparedContentId: SynchronizedPreparedContentId
            OperationId: SynchronizedOperationId
            PrincipalId: PrincipalId
            Content: SynchronizedPreparedContentDto
            UploadSessionId: UploadSessionId
            AuthorizedScope: string
            StoragePoolId: StoragePoolId
            FinalizedManifest: FileManifest option
        }

    /// Retains the existing immutable-content location behind one public content-version identity.
    [<CLIMutable; GenerateSerializer>]
    type SynchronizedContentLocationDocument =
        {
            id: string
            RepositoryId: RepositoryId
            Scope: string
            SchemaVersion: int
            Content: SynchronizedContentVersionDto
            AuthorizedScope: string
            Manifest: FileManifest
        }

    /// Persists one principal-bound, one-use immutable-byte read grant without exposing storage placement.
    [<CLIMutable; GenerateSerializer>]
    type SynchronizedContentReadGrantDocument =
        {
            id: string
            RepositoryId: RepositoryId
            Scope: string
            SchemaVersion: int
            GrantId: Guid
            PrincipalId: PrincipalId
            ItemId: SynchronizedItemId
            Content: SynchronizedContentVersionDto
            AuthorizedScope: string
            Manifest: FileManifest
            ExpiresAt: Instant
            ConsumedAt: Instant option
        }

    /// Couples one Cosmos document read with the private ETag used for exact replacement.
    type SynchronizedStoreRead<'T> = { Document: 'T; ETag: string }

    /// Reports whether an exact conditional control replacement succeeded.
    type SynchronizedControlWriteResult =
        | Replaced of etag: string
        | PreconditionFailed

    /// Defines the direct durable operations used by the bounded repository coordinator.
    type ISynchronizedContentStore =

        /// Creates the repository control document when absent and returns its current exact state.
        abstract member EnsureControlAsync:
            repositoryId: RepositoryId * rootConfiguration: SynchronizedRootConfigurationDto * cancellationToken: CancellationToken ->
                Task<SynchronizedStoreRead<SynchronizedControlDocument>>

        /// Reads the current exact control document.
        abstract member ReadControlAsync:
            repositoryId: RepositoryId * cancellationToken: CancellationToken -> Task<SynchronizedStoreRead<SynchronizedControlDocument>>

        /// Replaces the control document only while its previously observed ETag remains current.
        abstract member ReplaceControlAsync:
            control: SynchronizedControlDocument * etag: string * cancellationToken: CancellationToken -> Task<SynchronizedControlWriteResult>

        /// Reads a deterministic receipt by operation identity.
        abstract member ReadReceiptAsync:
            repositoryId: RepositoryId * operationId: SynchronizedOperationId * cancellationToken: CancellationToken -> Task<SynchronizedReceiptDocument option>

        /// Reads a canonical mutation by its reserved internal cursor.
        abstract member ReadCanonicalAsync:
            repositoryId: RepositoryId * cursor: int64 * cancellationToken: CancellationToken -> Task<SynchronizedCanonicalMutationDocument option>

        /// Creates the immutable canonical commit record or verifies an exact retry.
        abstract member CreateCanonicalAsync: mutation: SynchronizedCanonicalMutationDocument * cancellationToken: CancellationToken -> Task

        /// Reads one current item projection.
        abstract member ReadItemAsync:
            repositoryId: RepositoryId * itemId: SynchronizedItemId * cancellationToken: CancellationToken -> Task<SynchronizedCurrentItemDocument option>

        /// Reads one current normalized namespace slot projection.
        abstract member ReadSlotAsync:
            repositoryId: RepositoryId * normalizedPath: string * cancellationToken: CancellationToken -> Task<SynchronizedCurrentSlotDocument option>

        /// Applies the current item projection idempotently by canonical cursor.
        abstract member UpsertItemAsync: item: SynchronizedCurrentItemDocument * cancellationToken: CancellationToken -> Task

        /// Applies the namespace slot projection idempotently by canonical cursor.
        abstract member UpsertSlotAsync: slot: SynchronizedCurrentSlotDocument * cancellationToken: CancellationToken -> Task

        /// Applies the deterministic operation receipt idempotently.
        abstract member UpsertReceiptAsync: receipt: SynchronizedReceiptDocument * cancellationToken: CancellationToken -> Task

        /// Appends one bounded item-history entry idempotently.
        abstract member AppendItemHistoryAsync:
            repositoryId: RepositoryId * itemId: SynchronizedItemId * entry: SynchronizedHistoryEntry * cancellationToken: CancellationToken -> Task

        /// Appends one bounded path-history entry idempotently.
        abstract member AppendPathHistoryAsync:
            repositoryId: RepositoryId * normalizedPath: string * entry: SynchronizedHistoryEntry * cancellationToken: CancellationToken -> Task

        /// Reads one ordered page of canonical accepted mutations.
        abstract member ReadDeltasAsync:
            repositoryId: RepositoryId * afterCursor: int64 * maximumCount: int * cancellationToken: CancellationToken ->
                Task<SynchronizedCanonicalMutationDocument array>

        /// Enumerates current live and tombstoned item projections for baseline publication.
        abstract member ReadCurrentItemsAsync: repositoryId: RepositoryId * cancellationToken: CancellationToken -> Task<SynchronizedCurrentItemDocument array>

        /// Reports whether a live synchronized item remains below one normalized directory path.
        abstract member HasLiveDescendantsAsync:
            repositoryId: RepositoryId * normalizedDirectoryPath: string * cancellationToken: CancellationToken -> Task<bool>

        /// Publishes immutable byte-bounded baseline shards and then their manifest for one caught-up boundary.
        abstract member EnsureBaselineAsync:
            repositoryId: RepositoryId *
            boundaryCursor: int64 *
            cursorEpoch: Guid *
            rootConfiguration: SynchronizedRootConfigurationDto *
            items: SynchronizedItemDto array *
            cancellationToken: CancellationToken ->
                Task<SynchronizedBaselineManifestDocument>

        /// Reads one published immutable baseline and all of its verified current-item shards.
        abstract member ReadBaselineAsync:
            repositoryId: RepositoryId * baselineId: SynchronizedBootstrapId * cancellationToken: CancellationToken ->
                Task<(SynchronizedBaselineManifestDocument * SynchronizedItemDto array) option>

    /// Defines durable preparation, retained-content, and one-use read-grant operations over existing immutable storage.
    type ISynchronizedContentTransferStore =

        /// Creates one immutable principal- and operation-bound preparation or verifies its exact replay.
        abstract member CreatePreparedAsync: preparation: SynchronizedPreparedContentDocument * cancellationToken: CancellationToken -> Task

        /// Reads one content preparation without revealing it across repository boundaries.
        abstract member ReadPreparedAsync:
            repositoryId: RepositoryId * preparedContentId: SynchronizedPreparedContentId * cancellationToken: CancellationToken ->
                Task<SynchronizedStoreRead<SynchronizedPreparedContentDocument> option>

        /// Records the exact manifest completed by the preparation's existing upload session.
        abstract member FinalizePreparedAsync:
            repositoryId: RepositoryId * preparedContentId: SynchronizedPreparedContentId * manifest: FileManifest * cancellationToken: CancellationToken ->
                Task

        /// Retains the private immutable-content location behind a public content-version identity.
        abstract member UpsertContentLocationAsync: location: SynchronizedContentLocationDocument * cancellationToken: CancellationToken -> Task

        /// Reads the retained location for one public content-version identity.
        abstract member ReadContentLocationAsync:
            repositoryId: RepositoryId * contentVersionId: SynchronizedContentVersionId * cancellationToken: CancellationToken ->
                Task<SynchronizedContentLocationDocument option>

        /// Creates one principal-bound read grant after item and content authorization.
        abstract member CreateReadGrantAsync: grant: SynchronizedContentReadGrantDocument * cancellationToken: CancellationToken -> Task

        /// Reads one read grant for exact one-use redemption.
        abstract member ReadReadGrantAsync:
            repositoryId: RepositoryId * grantId: Guid * cancellationToken: CancellationToken ->
                Task<SynchronizedStoreRead<SynchronizedContentReadGrantDocument> option>

        /// Marks one still-current grant consumed through exact ETag replacement.
        abstract member ConsumeReadGrantAsync:
            grant: SynchronizedContentReadGrantDocument * etag: string * cancellationToken: CancellationToken -> Task<SynchronizedControlWriteResult>

    /// Defines integrity protection for opaque repository cursor values.
    type ISynchronizedCursorCodec =

        /// Protects one internal repository position without exposing its numeric value.
        abstract member Encode: repositoryId: RepositoryId * epoch: Guid * cursor: int64 -> SynchronizedCursor

        /// Validates one protected cursor against its repository and returns its private epoch and position.
        abstract member TryDecode: repositoryId: RepositoryId * cursor: SynchronizedCursor -> (Guid * int64) option

    /// Defines the application service invoked inside the bounded repository coordinator grain.
    type ISynchronizedContentCoordinator =

        /// Repairs any reserved command and submits one validated, authorized deterministic mutation.
        abstract member SubmitAsync:
            command: SynchronizedMutationCommand *
            rootConfiguration: SynchronizedRootConfigurationDto *
            principalId: PrincipalId *
            correlationId: CorrelationId *
            cancellationToken: CancellationToken ->
                Task<SynchronizedOperationReceiptDto>

        /// Repairs one repository's pending publication lifecycle without accepting another command.
        abstract member RepairAsync:
            repositoryId: RepositoryId * rootConfiguration: SynchronizedRootConfigurationDto * cancellationToken: CancellationToken -> Task

        /// Returns truthful content-free server state after repairing any pending accepted command.
        abstract member GetStatusAsync:
            repositoryId: RepositoryId * rootConfiguration: SynchronizedRootConfigurationDto * cancellationToken: CancellationToken ->
                Task<SynchronizedRepositoryStatusDto>
