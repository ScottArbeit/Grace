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

/// Defines the complete wire-safe remote Libraries contract.
module Library =

    type LibraryItemId = Guid
    type LibraryOperationId = Guid
    type LibraryCatalogVersion = Guid
    type LibraryNamespaceVersion = Guid
    type LibraryNamespaceSlotVersion = Guid
    type LibraryContentVersionId = Guid
    type LibraryBootstrapId = Guid
    type LibraryPreparedContentId = Guid
    type LibraryCursor = string
    type LibraryCursorEpoch = string
    type LibraryPageToken = string

    /// Provides the accepted lower-camel wire values for Library item kinds.
    [<RequireQualifiedAccess>]
    module ItemKind =
        [<Literal>]
        let File = "file"

        [<Literal>]
        let Directory = "directory"

    /// Provides the accepted lower-camel wire values for Library changes.
    [<RequireQualifiedAccess>]
    module ChangeKind =
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

    /// Provides the accepted lower-camel wire values for Library outcomes.
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

    /// Provides the exact rejection reason values returned by remote change admission.
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
    module CatalogRejectionReason =
        [<Literal>]
        let SlotOccupied = "slotOccupied"

        [<Literal>]
        let OutgoingSystemNotEmpty = "outgoingSystemNotEmpty"

        [<Literal>]
        let LibraryOverlap = "libraryOverlap"

        [<Literal>]
        let LibraryLimitExceeded = "libraryLimitExceeded"

        [<Literal>]
        let UnsupportedPath = "unsupportedPath"

    /// Identifies a Library item's repository-owned parent.
    [<CLIMutable; GenerateSerializer>]
    type LibraryParentDto = { Kind: string; LibraryPath: string option; ItemId: LibraryItemId option }

    /// Describes one current parent/name placement and its independent concurrency versions.
    [<CLIMutable; GenerateSerializer>]
    type LibraryNamespaceDto =
        {
            Parent: LibraryParentDto
            Name: string
            NormalizedPath: string
            NamespaceVersion: LibraryNamespaceVersion
            SlotVersion: LibraryNamespaceSlotVersion
        }

    /// Identifies one immutable complete-byte value without exposing its storage placement.
    [<CLIMutable; GenerateSerializer>]
    type LibraryContentVersionDto = { ContentVersionId: LibraryContentVersionId; Blake3Hash: string; Sha256Hash: string; Size: int64; CreatedAt: Instant }

    /// Records the durable deletion of one stable Library item identity.
    [<CLIMutable; GenerateSerializer>]
    type LibraryTombstoneDto =
        {
            ItemId: LibraryItemId
            ItemKind: string
            DeletedAt: Instant
            DeletedBy: PrincipalId
            DeleteCursor: LibraryCursor
            LastNamespaceVersion: LibraryNamespaceVersion
            LastContentVersionId: LibraryContentVersionId option
        }

    /// Records how competing complete bytes became a deterministic conflict copy.
    [<CLIMutable; GenerateSerializer>]
    type LibraryConflictProvenanceDto =
        {
            SourceOperationId: LibraryOperationId
            SourceItemId: LibraryItemId
            CanonicalItemId: LibraryItemId
            ConflictItemId: LibraryItemId
            ConflictPath: string
            AcceptedAt: Instant
            SourceContentVersionId: LibraryContentVersionId option
            BaseContentVersionId: LibraryContentVersionId option
        }

    /// Describes the current live or tombstoned state for one stable Library item.
    [<CLIMutable; GenerateSerializer>]
    type LibraryItemDto =
        {
            ItemId: LibraryItemId
            ItemKind: string
            State: string
            LastChangeCursor: LibraryCursor
            LibraryCatalogVersion: LibraryCatalogVersion
            Namespace: LibraryNamespaceDto option
            Content: LibraryContentVersionDto option
            Tombstone: LibraryTombstoneDto option
        }

    /// Describes one occupied or remembered-vacant normalized namespace slot.
    [<CLIMutable; GenerateSerializer>]
    type LibraryNamespaceSlotDto =
        {
            Parent: LibraryParentDto
            Name: string
            NormalizedPath: string
            SlotVersion: LibraryNamespaceSlotVersion
            State: string
            OccupantItemId: LibraryItemId option
        }

    /// Carries the exact current repository-owned synchronization root set.
    [<CLIMutable; GenerateSerializer>]
    type LibraryCatalogDto =
        {
            RepositoryId: RepositoryId
            Version: LibraryCatalogVersion
            Libraries: string array
            CreatedAt: Instant
            CreatedBy: PrincipalId
            PreviousVersion: LibraryCatalogVersion option
        }

        /// Builds the persisted empty root configuration represented by a repository's immutable creation event.
        static member CreateInitial(repositoryId: RepositoryId, createdAt: Instant, createdBy: PrincipalId) =
            let seed = Encoding.UTF8.GetBytes($"Grace.Libraries.LibraryCatalog.v1:{repositoryId:D}")
            let hash = SHA256.HashData(seed)
            let versionBytes = hash[0..15]
            versionBytes[6] <- (versionBytes[6] &&& 0x0Fuy) ||| 0x50uy
            versionBytes[8] <- (versionBytes[8] &&& 0x3Fuy) ||| 0x80uy

            {
                RepositoryId = repositoryId
                Version = Guid versionBytes
                Libraries = Array.empty
                CreatedAt = createdAt
                CreatedBy = createdBy
                PreviousVersion = None
            }

    /// Proves the namespace dimension observed by a rename, move, or delete caller.
    [<CLIMutable; GenerateSerializer>]
    type LibraryNamespacePreconditionDto = { ItemId: LibraryItemId; ExpectedNamespaceVersion: LibraryNamespaceVersion }

    /// Proves the content dimension observed by an update or file delete caller.
    [<CLIMutable; GenerateSerializer>]
    type LibraryContentPreconditionDto = { ItemId: LibraryItemId; ExpectedContentVersionId: LibraryContentVersionId }

    /// Proves that the caller observed one exact vacant destination slot.
    [<CLIMutable; GenerateSerializer>]
    type LibraryCreationSlotExpectationDto = { Parent: LibraryParentDto; Name: string; ExpectedSlotVersion: LibraryNamespaceSlotVersion; ExpectedState: string }

    /// Describes one accepted repository-ordered Library change.
    [<CLIMutable; GenerateSerializer>]
    type LibraryChangeDto =
        {
            Cursor: LibraryCursor
            OperationId: LibraryOperationId
            ChangeKind: string
            ItemId: LibraryItemId
            ItemKind: string
            AcceptedAt: Instant
            AcceptedBy: PrincipalId
            LibraryCatalogVersion: LibraryCatalogVersion
            Namespace: LibraryNamespaceDto option
            Content: LibraryContentVersionDto option
            Tombstone: LibraryTombstoneDto option
            Conflict: LibraryConflictProvenanceDto option
        }

    /// Directs a client to restart from the current published baseline.
    [<CLIMutable; GenerateSerializer>]
    type LibraryRebaselineDto = { Reason: string; CurrentEpoch: LibraryCursorEpoch; ServiceFloorCursor: LibraryCursor; RecommendedBootstrap: bool }

    /// Carries the stable result recorded for one normalized operation request.
    [<CLIMutable; GenerateSerializer>]
    type LibraryOperationReceiptDto =
        {
            OperationId: LibraryOperationId
            RequestHash: string
            Outcome: string
            LibraryCatalogVersion: LibraryCatalogVersion
            RecordedAt: Instant
            PrincipalId: PrincipalId
            Change: LibraryChangeDto option
            Cursor: LibraryCursor option
            Item: LibraryItemDto option
            Conflict: LibraryConflictProvenanceDto option
            ReasonCode: string option
            CurrentLibraryCatalog: LibraryCatalogDto option
            Rebaseline: LibraryRebaselineDto option
        }

    /// Carries one exact-version root add or remove result.
    [<CLIMutable; GenerateSerializer>]
    type LibraryCatalogChangeResultDto =
        {
            OperationId: LibraryOperationId
            Outcome: string
            LibraryCatalog: LibraryCatalogDto
            ReasonCode: string option
            RecordedAt: Instant
        }

    /// Carries one bounded immutable current-state bootstrap page.
    [<CLIMutable; GenerateSerializer>]
    type LibraryBootstrapPageDto =
        {
            BootstrapId: LibraryBootstrapId
            BoundaryCursor: LibraryCursor
            CursorEpoch: LibraryCursorEpoch
            LibraryCatalog: LibraryCatalogDto
            Items: LibraryItemDto array
            NextPageToken: LibraryPageToken option
        }

    /// Carries ordered accepted changes or a typed rebaseline instruction.
    [<CLIMutable; GenerateSerializer>]
    type LibraryChangePageDto =
        {
            Outcome: string
            CursorEpoch: LibraryCursorEpoch
            Changes: LibraryChangeDto array
            LastCursor: LibraryCursor
            HasMore: bool
            NextPageToken: LibraryPageToken option
            Rebaseline: LibraryRebaselineDto option
        }

    /// Binds immutable bytes to an authorized short-lived change preparation.
    [<CLIMutable; GenerateSerializer>]
    type LibraryPreparedContentDto =
        {
            PreparedContentId: LibraryPreparedContentId
            Blake3Hash: string
            Sha256Hash: string
            Size: int64
            UploadRequired: bool
            UploadInstructions: string option
            ExpiresAt: Instant
        }

    /// Grants one authorized short-lived read of exact immutable bytes.
    [<CLIMutable; GenerateSerializer>]
    type LibraryContentReadGrantDto = { GrantId: string; DownloadPath: string; Content: LibraryContentVersionDto; ExpiresAt: Instant }

    /// Reports server synchronization progress without exposing storage or content details.
    [<CLIMutable; GenerateSerializer>]
    type LibraryRepositoryStatusDto =
        {
            State: string
            RepositoryId: RepositoryId
            LibraryCatalogVersion: LibraryCatalogVersion
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
    type LibraryContentAvailable =
        {
            EventName: string
            RepositoryId: RepositoryId
            CursorEpoch: LibraryCursorEpoch
            AvailableAfterCursor: LibraryCursor
            LibraryCatalogVersion: LibraryCatalogVersion
            OccurredAt: Instant
            CorrelationId: CorrelationId
        }

        /// Builds the only Product V1 public wake event.
        static member Create(repositoryId, cursorEpoch, availableAfterCursor, libraryCatalogVersion, occurredAt, correlationId) =
            {
                EventName = "LibraryContentAvailable.v1"
                RepositoryId = repositoryId
                CursorEpoch = cursorEpoch
                AvailableAfterCursor = availableAfterCursor
                LibraryCatalogVersion = libraryCatalogVersion
                OccurredAt = occurredAt
                CorrelationId = correlationId
            }

    /// Carries the internal change request after public validation and authorization are complete.
    [<GenerateSerializer>]
    type LibraryChangeCommand =
        {
            RepositoryId: RepositoryId
            OperationId: LibraryOperationId
            RequestHash: string
            LibraryCatalogVersion: LibraryCatalogVersion
            ChangeKind: string
            ItemKind: string
            ItemId: LibraryItemId option
            NamespacePrecondition: LibraryNamespacePreconditionDto option
            ContentPrecondition: LibraryContentPreconditionDto option
            CreationSlotExpectation: LibraryCreationSlotExpectationDto option
            DestinationParent: LibraryParentDto option
            DestinationName: string option
            PreparedContentId: LibraryPreparedContentId option
            PreparedContent: LibraryContentVersionDto option
            PreparedContentExpiresAt: Instant option
        }

    /// Tracks the independently repairable projection positions for one repository.
    [<CLIMutable; GenerateSerializer>]
    type LibraryProjectionWatermarks =
        {
            Current: int64
            History: int64
            Receipts: int64
            Baselines: int64
        }

        /// Returns the empty projection state before any accepted change.
        static member Empty = { Current = 0L; History = 0L; Receipts = 0L; Baselines = 0L }

    /// Persists the immutable canonical commit record for one accepted change.
    [<CLIMutable; GenerateSerializer>]
    type LibraryCanonicalChangeDocument =
        {
            id: string
            RepositoryId: RepositoryId
            StreamSegment: string
            SchemaVersion: int
            Cursor: int64
            PublicCursor: LibraryCursor
            OperationId: LibraryOperationId
            RequestHash: string
            Change: LibraryChangeDto
            PriorNamespace: LibraryNamespaceDto option
            PriorContentVersionId: LibraryContentVersionId option
            ConsumedNamespaceVersion: LibraryNamespaceVersion option
            ConsumedContentVersionId: LibraryContentVersionId option
            ConsumedSlotVersion: LibraryNamespaceSlotVersion option
            CorrelationId: CorrelationId
        }

    /// Persists the complete deterministic reservation that activation can finish without guessing.
    [<CLIMutable; GenerateSerializer>]
    type LibraryPendingCommandDocument =
        {
            OperationId: LibraryOperationId
            RequestHash: string
            Cursor: int64
            Receipt: LibraryOperationReceiptDto
            CanonicalChange: LibraryCanonicalChangeDocument
            ExpectedLibraryCatalogVersion: LibraryCatalogVersion
            PrincipalId: PrincipalId
            CorrelationId: CorrelationId
            ReservedAt: Instant
            TargetItemIds: LibraryItemId array
        }

    /// Persists the bounded serialized command lane and repository synchronization configuration.
    [<CLIMutable; GenerateSerializer>]
    type LibraryControlDocument =
        {
            id: string
            RepositoryId: RepositoryId
            SchemaVersion: int
            CursorEpoch: Guid
            NextCursor: int64
            AppliedThrough: int64
            ReplayFloor: int64
            LibraryCatalog: LibraryCatalogDto
            Pending: LibraryPendingCommandDocument option
            CurrentBaselineId: LibraryBootstrapId option
            CurrentBaselineCursor: int64 option
            ProjectionWatermarks: LibraryProjectionWatermarks
            UpdatedAt: Instant
        }

    /// Stores one rebuildable current item projection and its canonical position.
    [<CLIMutable; GenerateSerializer>]
    type LibraryCurrentItemDocument =
        {
            id: string
            RepositoryId: RepositoryId
            ProjectionKind: string
            SchemaVersion: int
            Item: LibraryItemDto
            LastCursor: int64
            AppliedThrough: int64
        }

    /// Stores one rebuildable namespace-slot projection and its canonical position.
    [<CLIMutable; GenerateSerializer>]
    type LibraryCurrentSlotDocument =
        {
            id: string
            RepositoryId: RepositoryId
            ProjectionKind: string
            SchemaVersion: int
            Slot: LibraryNamespaceSlotDto
            LastCursor: int64
            AppliedThrough: int64
        }

    /// Stores one deterministic operation receipt for response-loss recovery.
    [<CLIMutable; GenerateSerializer>]
    type LibraryReceiptDocument =
        {
            id: string
            RepositoryId: RepositoryId
            RecordKind: string
            RecordKey: string
            SchemaVersion: int
            OperationId: LibraryOperationId
            RequestHash: string
            Receipt: LibraryOperationReceiptDto
            Cursor: int64 option
            AppliedThrough: int64
        }

    /// Stores one canonical-derived item or path history entry.
    [<CLIMutable; GenerateSerializer>]
    type LibraryHistoryEntry =
        {
            Cursor: int64
            PublicCursor: LibraryCursor
            OperationId: LibraryOperationId
            ItemId: LibraryItemId
            PriorNamespace: LibraryNamespaceDto option
            ResultingNamespace: LibraryNamespaceDto option
            PriorContentVersionId: LibraryContentVersionId option
            ResultingContentVersionId: LibraryContentVersionId option
            Tombstone: LibraryTombstoneDto option
            Conflict: LibraryConflictProvenanceDto option
            PrincipalId: PrincipalId
            AcceptedAt: Instant
        }

    /// Stores at most 512 canonical-derived history entries under a byte bound.
    [<CLIMutable; GenerateSerializer>]
    type LibraryHistorySegmentDocument =
        {
            id: string
            RepositoryId: RepositoryId
            HistoryKey: string
            SchemaVersion: int
            HistorySegment: string
            FirstCursor: int64
            LastCursor: int64
            EntryCount: int
            Entries: LibraryHistoryEntry array
        }

    /// Stores one immutable byte-bounded current-state baseline shard.
    [<CLIMutable; GenerateSerializer>]
    type LibraryBaselineShardDocument =
        {
            id: string
            RepositoryId: RepositoryId
            SchemaVersion: int
            BaselineId: LibraryBootstrapId
            ShardKey: string
            BoundaryCursor: int64
            Items: LibraryItemDto array
            ItemCount: int
            SerializedBytes: int
        }

    /// Publishes a baseline only after every named shard is durable and hash-verified.
    [<CLIMutable; GenerateSerializer>]
    type LibraryBaselineManifestDocument =
        {
            id: string
            RepositoryId: RepositoryId
            SchemaVersion: int
            BaselineId: LibraryBootstrapId
            ShardKey: string
            BoundaryCursor: int64
            CursorEpoch: Guid
            LibraryCatalogVersion: LibraryCatalogVersion
            ShardIds: string array
            ShardHashes: string array
            ShardItemCounts: int array
            TotalItemCount: int
            CreatedAt: Instant
        }

    /// Persists one principal-bound immutable-content preparation until its existing upload session is finalized or expires.
    [<CLIMutable; GenerateSerializer>]
    type LibraryPreparedContentDocument =
        {
            id: string
            RepositoryId: RepositoryId
            RecordKind: string
            RecordKey: string
            SchemaVersion: int
            PreparedContentId: LibraryPreparedContentId
            OperationId: LibraryOperationId
            PrincipalId: PrincipalId
            Content: LibraryPreparedContentDto
            UploadSessionId: UploadSessionId
            AuthorizedScope: string
            StoragePoolId: StoragePoolId
            FinalizedManifest: FileManifest option
        }

    /// Retains the existing immutable-content location behind one public content-version identity.
    [<CLIMutable; GenerateSerializer>]
    type LibraryContentLocationDocument =
        {
            id: string
            RepositoryId: RepositoryId
            RecordKind: string
            RecordKey: string
            SchemaVersion: int
            Content: LibraryContentVersionDto
            AuthorizedScope: string
            Manifest: FileManifest
        }

    /// Persists one principal-bound, one-use immutable-byte read grant without exposing storage placement.
    [<CLIMutable; GenerateSerializer>]
    type LibraryContentReadGrantDocument =
        {
            id: string
            RepositoryId: RepositoryId
            RecordKind: string
            RecordKey: string
            SchemaVersion: int
            GrantId: Guid
            PrincipalId: PrincipalId
            ItemId: LibraryItemId
            Content: LibraryContentVersionDto
            AuthorizedScope: string
            Manifest: FileManifest
            ExpiresAt: Instant
            ConsumedAt: Instant option
        }

    /// Couples one Cosmos document read with the private ETag used for exact replacement.
    type LibraryStoreRead<'T> = { Document: 'T; ETag: string }

    /// Reports whether an exact conditional control replacement succeeded.
    type LibraryControlWriteResult =
        | Replaced of etag: string
        | PreconditionFailed

    /// Defines the direct durable operations used by the bounded repository coordinator.
    type ILibraryStore =

        /// Creates the repository control document when absent and returns its current exact state.
        abstract member EnsureControlAsync:
            repositoryId: RepositoryId * libraryCatalog: LibraryCatalogDto * cancellationToken: CancellationToken ->
                Task<LibraryStoreRead<LibraryControlDocument>>

        /// Reads the current exact control document.
        abstract member ReadControlAsync: repositoryId: RepositoryId * cancellationToken: CancellationToken -> Task<LibraryStoreRead<LibraryControlDocument>>

        /// Replaces the control document only while its previously observed ETag remains current.
        abstract member ReplaceControlAsync:
            control: LibraryControlDocument * etag: string * cancellationToken: CancellationToken -> Task<LibraryControlWriteResult>

        /// Reads a deterministic receipt by operation identity.
        abstract member ReadReceiptAsync:
            repositoryId: RepositoryId * operationId: LibraryOperationId * cancellationToken: CancellationToken -> Task<LibraryReceiptDocument option>

        /// Reads a canonical change by its reserved internal cursor.
        abstract member ReadCanonicalAsync:
            repositoryId: RepositoryId * cursor: int64 * cancellationToken: CancellationToken -> Task<LibraryCanonicalChangeDocument option>

        /// Creates the immutable canonical commit record or verifies an exact retry.
        abstract member CreateCanonicalAsync: change: LibraryCanonicalChangeDocument * cancellationToken: CancellationToken -> Task

        /// Reads one current item projection.
        abstract member ReadItemAsync:
            repositoryId: RepositoryId * itemId: LibraryItemId * cancellationToken: CancellationToken -> Task<LibraryCurrentItemDocument option>

        /// Reads one current normalized namespace slot projection.
        abstract member ReadSlotAsync:
            repositoryId: RepositoryId * normalizedPath: string * cancellationToken: CancellationToken -> Task<LibraryCurrentSlotDocument option>

        /// Rejects creation of new current projections before either repository projection kind exceeds 100,000 documents.
        abstract member EnsureCurrentProjectionCapacityAsync:
            repositoryId: RepositoryId * itemId: LibraryItemId * normalizedPath: string * cancellationToken: CancellationToken -> Task

        /// Applies the current item projection idempotently by canonical cursor.
        abstract member UpsertItemAsync: item: LibraryCurrentItemDocument * cancellationToken: CancellationToken -> Task

        /// Applies the namespace slot projection idempotently by canonical cursor.
        abstract member UpsertSlotAsync: slot: LibraryCurrentSlotDocument * cancellationToken: CancellationToken -> Task

        /// Applies the deterministic operation receipt idempotently.
        abstract member UpsertReceiptAsync: receipt: LibraryReceiptDocument * cancellationToken: CancellationToken -> Task

        /// Appends one bounded item-history entry idempotently.
        abstract member AppendItemHistoryAsync:
            repositoryId: RepositoryId * itemId: LibraryItemId * entry: LibraryHistoryEntry * cancellationToken: CancellationToken -> Task

        /// Appends one bounded path-history entry idempotently.
        abstract member AppendPathHistoryAsync:
            repositoryId: RepositoryId * normalizedPath: string * entry: LibraryHistoryEntry * cancellationToken: CancellationToken -> Task

        /// Reads one ordered page of canonical accepted changes.
        abstract member ReadChangesAsync:
            repositoryId: RepositoryId * afterCursor: int64 * maximumCount: int * cancellationToken: CancellationToken ->
                Task<LibraryCanonicalChangeDocument array>

        /// Enumerates current live and tombstoned item projections for baseline publication.
        abstract member ReadCurrentItemsAsync: repositoryId: RepositoryId * cancellationToken: CancellationToken -> Task<LibraryCurrentItemDocument array>

        /// Reports whether a live Library item remains below one normalized directory path.
        abstract member HasLiveDescendantsAsync:
            repositoryId: RepositoryId * normalizedDirectoryPath: string * cancellationToken: CancellationToken -> Task<bool>

        /// Publishes immutable byte-bounded baseline shards and then their manifest for one caught-up boundary.
        abstract member EnsureBaselineAsync:
            repositoryId: RepositoryId *
            boundaryCursor: int64 *
            cursorEpoch: Guid *
            libraryCatalog: LibraryCatalogDto *
            items: LibraryItemDto array *
            cancellationToken: CancellationToken ->
                Task<LibraryBaselineManifestDocument>

        /// Reads one published immutable baseline and all of its verified current-item shards.
        abstract member ReadBaselineAsync:
            repositoryId: RepositoryId * baselineId: LibraryBootstrapId * cancellationToken: CancellationToken ->
                Task<(LibraryBaselineManifestDocument * LibraryItemDto array) option>

    /// Defines durable preparation, retained-content, and one-use read-grant operations over existing immutable storage.
    type ILibraryTransferStore =

        /// Creates one immutable principal- and operation-bound preparation or verifies its exact replay.
        abstract member CreatePreparedAsync: preparation: LibraryPreparedContentDocument * cancellationToken: CancellationToken -> Task

        /// Reads one content preparation without revealing it across repository boundaries.
        abstract member ReadPreparedAsync:
            repositoryId: RepositoryId * preparedContentId: LibraryPreparedContentId * cancellationToken: CancellationToken ->
                Task<LibraryStoreRead<LibraryPreparedContentDocument> option>

        /// Records the exact manifest completed by the preparation's existing upload session.
        abstract member FinalizePreparedAsync:
            repositoryId: RepositoryId * preparedContentId: LibraryPreparedContentId * manifest: FileManifest * cancellationToken: CancellationToken -> Task

        /// Retains the private immutable-content location behind a public content-version identity.
        abstract member UpsertContentLocationAsync: location: LibraryContentLocationDocument * cancellationToken: CancellationToken -> Task

        /// Reads the retained location for one public content-version identity.
        abstract member ReadContentLocationAsync:
            repositoryId: RepositoryId * contentVersionId: LibraryContentVersionId * cancellationToken: CancellationToken ->
                Task<LibraryContentLocationDocument option>

        /// Creates one principal-bound read grant after item and content authorization.
        abstract member CreateReadGrantAsync: grant: LibraryContentReadGrantDocument * cancellationToken: CancellationToken -> Task

        /// Reads one read grant for exact one-use redemption.
        abstract member ReadReadGrantAsync:
            repositoryId: RepositoryId * grantId: Guid * cancellationToken: CancellationToken -> Task<LibraryStoreRead<LibraryContentReadGrantDocument> option>

        /// Marks one still-current grant consumed through exact ETag replacement.
        abstract member ConsumeReadGrantAsync:
            grant: LibraryContentReadGrantDocument * etag: string * cancellationToken: CancellationToken -> Task<LibraryControlWriteResult>

    /// Defines integrity protection for opaque repository cursor values.
    type ILibraryCursorCodec =

        /// Protects one internal repository position without exposing its numeric value.
        abstract member Encode: repositoryId: RepositoryId * epoch: Guid * cursor: int64 -> LibraryCursor

        /// Validates one protected cursor against its repository and returns its private epoch and position.
        abstract member TryDecode: repositoryId: RepositoryId * cursor: LibraryCursor -> (Guid * int64) option

    /// Defines the application service invoked inside the bounded repository coordinator grain.
    type ILibraryCoordinator =

        /// Creates the bounded repository Library control state from the immutable repository creation facts.
        abstract member InitializeAsync: repositoryId: RepositoryId * libraryCatalog: LibraryCatalogDto * cancellationToken: CancellationToken -> Task

        /// Reads the authoritative Library catalog from the repository's bounded control state.
        abstract member GetCatalogAsync: repositoryId: RepositoryId * cancellationToken: CancellationToken -> Task<LibraryCatalogDto>

        /// Replaces the authoritative Library catalog only when its exact predecessor remains current.
        abstract member SetCatalogAsync:
            repositoryId: RepositoryId * libraryCatalog: LibraryCatalogDto * cancellationToken: CancellationToken -> Task<LibraryCatalogDto>

        /// Classifies one normalized repository-relative path against the catalog snapshot read for this call.
        abstract member IsInLibraryAsync: repositoryId: RepositoryId * relativePath: string * cancellationToken: CancellationToken -> Task<bool>

        /// Repairs any reserved command and submits one validated, authorized deterministic change.
        abstract member SubmitAsync:
            command: LibraryChangeCommand * principalId: PrincipalId * correlationId: CorrelationId * cancellationToken: CancellationToken ->
                Task<LibraryOperationReceiptDto>

        /// Repairs one repository's pending publication lifecycle without accepting another command.
        abstract member RepairAsync: repositoryId: RepositoryId * cancellationToken: CancellationToken -> Task

        /// Returns truthful content-free server state after repairing any pending accepted command.
        abstract member GetStatusAsync: repositoryId: RepositoryId * cancellationToken: CancellationToken -> Task<LibraryRepositoryStatusDto>
