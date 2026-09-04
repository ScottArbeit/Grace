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
    type LibraryParentDto =
        {
            [<Id(0u)>]
            Kind: string
            [<Id(1u)>]
            LibraryPath: string option
            [<Id(2u)>]
            ItemId: LibraryItemId option
        }

    /// Describes one current parent/name placement and its independent concurrency versions.
    [<CLIMutable; GenerateSerializer>]
    type LibraryNamespaceDto =
        {
            [<Id(0u)>]
            Parent: LibraryParentDto
            [<Id(1u)>]
            Name: string
            [<Id(2u)>]
            NormalizedPath: string
            [<Id(3u)>]
            NamespaceVersion: LibraryNamespaceVersion
            [<Id(4u)>]
            SlotVersion: LibraryNamespaceSlotVersion
        }

    /// Identifies one immutable complete-byte value without exposing its storage placement.
    [<CLIMutable; GenerateSerializer>]
    type LibraryContentVersionDto =
        {
            [<Id(0u)>]
            ContentVersionId: LibraryContentVersionId
            [<Id(1u)>]
            Blake3Hash: string
            [<Id(2u)>]
            Sha256Hash: string
            [<Id(3u)>]
            Size: int64
            [<Id(4u)>]
            CreatedAt: Instant
        }

    /// Records the durable deletion of one stable Library item identity.
    [<CLIMutable; GenerateSerializer>]
    type LibraryTombstoneDto =
        {
            [<Id(0u)>]
            ItemId: LibraryItemId
            [<Id(1u)>]
            ItemKind: string
            [<Id(2u)>]
            DeletedAt: Instant
            [<Id(3u)>]
            DeletedBy: PrincipalId
            [<Id(4u)>]
            DeleteCursor: LibraryCursor
            [<Id(5u)>]
            LastNamespaceVersion: LibraryNamespaceVersion
            [<Id(6u)>]
            LastContentVersionId: LibraryContentVersionId option
        }

    /// Records how competing complete bytes became a deterministic conflict copy.
    [<CLIMutable; GenerateSerializer>]
    type LibraryConflictProvenanceDto =
        {
            [<Id(0u)>]
            SourceOperationId: LibraryOperationId
            [<Id(1u)>]
            SourceItemId: LibraryItemId
            [<Id(2u)>]
            CanonicalItemId: LibraryItemId
            [<Id(3u)>]
            ConflictItemId: LibraryItemId
            [<Id(4u)>]
            ConflictPath: string
            [<Id(5u)>]
            AcceptedAt: Instant
            [<Id(6u)>]
            SourceContentVersionId: LibraryContentVersionId option
            [<Id(7u)>]
            BaseContentVersionId: LibraryContentVersionId option
        }

    /// Describes the current live or tombstoned state for one stable Library item.
    [<CLIMutable; GenerateSerializer>]
    type LibraryItemDto =
        {
            [<Id(0u)>]
            ItemId: LibraryItemId
            [<Id(1u)>]
            ItemKind: string
            [<Id(2u)>]
            State: string
            [<Id(3u)>]
            LastChangeCursor: LibraryCursor
            [<Id(4u)>]
            LibraryCatalogVersion: LibraryCatalogVersion
            [<Id(5u)>]
            Namespace: LibraryNamespaceDto option
            [<Id(6u)>]
            Content: LibraryContentVersionDto option
            [<Id(7u)>]
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
            [<Id(0u)>]
            RepositoryId: RepositoryId
            [<Id(1u)>]
            Version: LibraryCatalogVersion
            [<Id(2u)>]
            Libraries: string array
            [<Id(3u)>]
            CreatedAt: Instant
            [<Id(4u)>]
            CreatedBy: PrincipalId
            [<Id(5u)>]
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
    type LibraryNamespacePreconditionDto =
        {
            [<Id(0u)>]
            ItemId: LibraryItemId
            [<Id(1u)>]
            ExpectedNamespaceVersion: LibraryNamespaceVersion
        }

    /// Proves the content dimension observed by an update or file delete caller.
    [<CLIMutable; GenerateSerializer>]
    type LibraryContentPreconditionDto =
        {
            [<Id(0u)>]
            ItemId: LibraryItemId
            [<Id(1u)>]
            ExpectedContentVersionId: LibraryContentVersionId
        }

    /// Proves that the caller observed one exact vacant destination slot.
    [<CLIMutable; GenerateSerializer>]
    type LibraryCreationSlotExpectationDto =
        {
            [<Id(0u)>]
            Parent: LibraryParentDto
            [<Id(1u)>]
            Name: string
            [<Id(2u)>]
            ExpectedSlotVersion: LibraryNamespaceSlotVersion
            [<Id(3u)>]
            ExpectedState: string
        }

    /// Describes one accepted repository-ordered Library change.
    [<CLIMutable; GenerateSerializer>]
    type LibraryChangeDto =
        {
            [<Id(0u)>]
            Cursor: LibraryCursor
            [<Id(1u)>]
            OperationId: LibraryOperationId
            [<Id(2u)>]
            ChangeKind: string
            [<Id(3u)>]
            ItemId: LibraryItemId
            [<Id(4u)>]
            ItemKind: string
            [<Id(5u)>]
            AcceptedAt: Instant
            [<Id(6u)>]
            AcceptedBy: PrincipalId
            [<Id(7u)>]
            LibraryCatalogVersion: LibraryCatalogVersion
            [<Id(8u)>]
            Namespace: LibraryNamespaceDto option
            [<Id(9u)>]
            Content: LibraryContentVersionDto option
            [<Id(10u)>]
            Tombstone: LibraryTombstoneDto option
            [<Id(11u)>]
            Conflict: LibraryConflictProvenanceDto option
        }

    /// Directs a client to restart from the current published baseline.
    [<CLIMutable; GenerateSerializer>]
    type LibraryRebaselineDto =
        {
            [<Id(0u)>]
            Reason: string
            [<Id(1u)>]
            CurrentEpoch: LibraryCursorEpoch
            [<Id(2u)>]
            ServiceFloorCursor: LibraryCursor
            [<Id(3u)>]
            RecommendedBootstrap: bool
        }

    /// Carries the stable result recorded for one normalized operation request.
    [<CLIMutable; GenerateSerializer>]
    type LibraryOperationReceiptDto =
        {
            [<Id(0u)>]
            OperationId: LibraryOperationId
            [<Id(1u)>]
            RequestHash: string
            [<Id(2u)>]
            Outcome: string
            [<Id(3u)>]
            LibraryCatalogVersion: LibraryCatalogVersion
            [<Id(4u)>]
            RecordedAt: Instant
            [<Id(5u)>]
            PrincipalId: PrincipalId
            [<Id(6u)>]
            Change: LibraryChangeDto option
            [<Id(7u)>]
            Cursor: LibraryCursor option
            [<Id(8u)>]
            Item: LibraryItemDto option
            [<Id(9u)>]
            Conflict: LibraryConflictProvenanceDto option
            [<Id(10u)>]
            ReasonCode: string option
            [<Id(11u)>]
            CurrentLibraryCatalog: LibraryCatalogDto option
            [<Id(12u)>]
            Rebaseline: LibraryRebaselineDto option
        }

    /// Carries one exact-version root add or remove result.
    [<CLIMutable; GenerateSerializer>]
    type LibraryCatalogChangeResultDto =
        {
            [<Id(0u)>]
            OperationId: LibraryOperationId
            [<Id(1u)>]
            Outcome: string
            [<Id(2u)>]
            LibraryCatalog: LibraryCatalogDto
            [<Id(3u)>]
            ReasonCode: string option
            [<Id(4u)>]
            RecordedAt: Instant
        }

    /// Carries the authenticated facts rechecked against current repository-scoped Library authority inside the serialized actor call.
    [<GenerateSerializer>]
    type LibraryWriteAuthorization =
        {
            [<Id(0u)>]
            OwnerId: OwnerId
            [<Id(1u)>]
            OrganizationId: OrganizationId
            [<Id(2u)>]
            Principals: Principal array
            [<Id(3u)>]
            EffectiveClaims: string array
        }

    /// Reports whether the actor admitted a Library change after its final current-authority check.
    [<CLIMutable; GenerateSerializer>]
    type LibrarySubmitResult =
        {
            [<Id(0u)>]
            Receipt: LibraryOperationReceiptDto option
            [<Id(1u)>]
            ForbiddenReason: string option
        }

        /// Builds an admitted result around its durable operation receipt.
        static member Submitted receipt = { Receipt = Some receipt; ForbiddenReason = None }

        /// Builds a denied result without a durable operation receipt.
        static member Forbidden reason = { Receipt = None; ForbiddenReason = Some reason }

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
            [<Id(0u)>]
            State: string
            [<Id(1u)>]
            RepositoryId: RepositoryId
            [<Id(2u)>]
            LibraryCatalogVersion: LibraryCatalogVersion
            [<Id(3u)>]
            IsCaughtUp: bool
            [<Id(4u)>]
            RebaselineRequired: bool
            [<Id(5u)>]
            IsBlocked: bool
            [<Id(6u)>]
            PendingOperationCount: int
            [<Id(7u)>]
            OldestPendingAgeMilliseconds: int64 option
            [<Id(8u)>]
            ProjectionLagCount: int64
            [<Id(9u)>]
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

    /// Stores one stable Service Bus application property in an exact failed-delivery envelope.
    [<CLIMutable; GenerateSerializer>]
    type GraceEventEnvelopeProperty =
        {
            [<Id(0u)>]
            Key: string
            [<Id(1u)>]
            Value: string
        }

    /// Stores the exact serialized Library notification retained only after terminal Service Bus failure.
    [<CLIMutable; GenerateSerializer>]
    type FailedGraceEventEnvelope =
        {
            [<Id(0u)>]
            RepositoryId: RepositoryId
            [<Id(1u)>]
            RecordKind: string
            [<Id(2u)>]
            RecordKey: string
            [<Id(3u)>]
            TopicName: string
            [<Id(4u)>]
            MessageId: string
            [<Id(5u)>]
            Body: byte array
            [<Id(6u)>]
            ContentType: string
            [<Id(7u)>]
            Subject: string
            [<Id(8u)>]
            CorrelationId: CorrelationId
            [<Id(9u)>]
            ApplicationProperties: GraceEventEnvelopeProperty array
        }

    /// Carries the internal change request after public validation and authorization are complete.
    [<GenerateSerializer>]
    type LibraryChangeCommand =
        {
            [<Id(0u)>]
            RepositoryId: RepositoryId
            [<Id(1u)>]
            OperationId: LibraryOperationId
            [<Id(2u)>]
            RequestHash: string
            [<Id(3u)>]
            LibraryCatalogVersion: LibraryCatalogVersion
            [<Id(4u)>]
            ChangeKind: string
            [<Id(5u)>]
            ItemKind: string
            [<Id(6u)>]
            ItemId: LibraryItemId option
            [<Id(7u)>]
            NamespacePrecondition: LibraryNamespacePreconditionDto option
            [<Id(8u)>]
            ContentPrecondition: LibraryContentPreconditionDto option
            [<Id(9u)>]
            CreationSlotExpectation: LibraryCreationSlotExpectationDto option
            [<Id(10u)>]
            DestinationParent: LibraryParentDto option
            [<Id(11u)>]
            DestinationName: string option
            [<Id(12u)>]
            PreparedContentId: LibraryPreparedContentId option
            [<Id(13u)>]
            PreparedContent: LibraryContentVersionDto option
            [<Id(14u)>]
            PreparedContentExpiresAt: Instant option
        }

    /// Tracks the independently repairable projection positions for one repository.
    [<CLIMutable; GenerateSerializer>]
    type LibraryProjectionWatermarks =
        {
            [<Id(0u)>]
            Current: int64
            [<Id(1u)>]
            History: int64
            [<Id(2u)>]
            Receipts: int64
            [<Id(3u)>]
            Baselines: int64
        }

        /// Returns the empty projection state before any accepted change.
        static member Empty = { Current = 0L; History = 0L; Receipts = 0L; Baselines = 0L }

    /// Persists the immutable canonical commit record for one accepted change.
    [<CLIMutable; GenerateSerializer>]
    type LibraryCanonicalChangeDocument =
        {
            [<Id(0u)>]
            id: string
            [<Id(1u)>]
            RepositoryId: RepositoryId
            [<Id(2u)>]
            StreamSegment: string
            [<Id(3u)>]
            SchemaVersion: int
            [<Id(4u)>]
            Cursor: int64
            [<Id(5u)>]
            PublicCursor: LibraryCursor
            [<Id(6u)>]
            OperationId: LibraryOperationId
            [<Id(7u)>]
            RequestHash: string
            [<Id(8u)>]
            Change: LibraryChangeDto
            [<Id(9u)>]
            PriorNamespace: LibraryNamespaceDto option
            [<Id(10u)>]
            PriorContentVersionId: LibraryContentVersionId option
            [<Id(11u)>]
            ConsumedNamespaceVersion: LibraryNamespaceVersion option
            [<Id(12u)>]
            ConsumedContentVersionId: LibraryContentVersionId option
            [<Id(13u)>]
            ConsumedSlotVersion: LibraryNamespaceSlotVersion option
            [<Id(14u)>]
            CorrelationId: CorrelationId
        }

    /// Persists the complete deterministic reservation that activation can finish without guessing.
    [<CLIMutable; GenerateSerializer>]
    type LibraryPendingCommandDocument =
        {
            [<Id(0u)>]
            OperationId: LibraryOperationId
            [<Id(1u)>]
            RequestHash: string
            [<Id(2u)>]
            Cursor: int64
            [<Id(3u)>]
            Receipt: LibraryOperationReceiptDto
            [<Id(4u)>]
            CanonicalChange: LibraryCanonicalChangeDocument
            [<Id(5u)>]
            ExpectedLibraryCatalogVersion: LibraryCatalogVersion
            [<Id(6u)>]
            PrincipalId: PrincipalId
            [<Id(7u)>]
            CorrelationId: CorrelationId
            [<Id(8u)>]
            ReservedAt: Instant
            [<Id(9u)>]
            TargetItemIds: LibraryItemId array
        }

    /// Persists the bounded serialized command lane and repository synchronization configuration.
    [<CLIMutable; GenerateSerializer>]
    type LibraryControlDocument =
        {
            [<Id(0u)>]
            id: string
            [<Id(1u)>]
            RepositoryId: RepositoryId
            [<Id(2u)>]
            SchemaVersion: int
            [<Id(3u)>]
            CursorEpoch: Guid
            [<Id(4u)>]
            NextCursor: int64
            [<Id(5u)>]
            AppliedThrough: int64
            [<Id(6u)>]
            ReplayFloor: int64
            [<Id(7u)>]
            LibraryCatalog: LibraryCatalogDto
            [<Id(8u)>]
            Pending: LibraryPendingCommandDocument option
            [<Id(9u)>]
            CurrentBaselineId: LibraryBootstrapId option
            [<Id(10u)>]
            CurrentBaselineCursor: int64 option
            [<Id(11u)>]
            ProjectionWatermarks: LibraryProjectionWatermarks
            [<Id(12u)>]
            UpdatedAt: Instant
        }

    /// Stores one rebuildable current item projection and its canonical position.
    [<CLIMutable; GenerateSerializer>]
    type LibraryCurrentItemDocument =
        {
            [<Id(0u)>]
            id: string
            [<Id(1u)>]
            RepositoryId: RepositoryId
            [<Id(2u)>]
            ProjectionKind: string
            [<Id(3u)>]
            SchemaVersion: int
            [<Id(4u)>]
            Item: LibraryItemDto
            [<Id(5u)>]
            LastCursor: int64
            [<Id(6u)>]
            AppliedThrough: int64
        }

    /// Stores one rebuildable namespace-slot projection and its canonical position.
    [<CLIMutable; GenerateSerializer>]
    type LibraryCurrentSlotDocument =
        {
            [<Id(0u)>]
            id: string
            [<Id(1u)>]
            RepositoryId: RepositoryId
            [<Id(2u)>]
            ProjectionKind: string
            [<Id(3u)>]
            SchemaVersion: int
            [<Id(4u)>]
            Slot: LibraryNamespaceSlotDto
            [<Id(5u)>]
            LastCursor: int64
            [<Id(6u)>]
            AppliedThrough: int64
        }

    /// Stores one deterministic operation receipt for response-loss recovery.
    [<CLIMutable; GenerateSerializer>]
    type LibraryReceiptDocument =
        {
            [<Id(0u)>]
            id: string
            [<Id(1u)>]
            RepositoryId: RepositoryId
            [<Id(2u)>]
            RecordKind: string
            [<Id(3u)>]
            RecordKey: string
            [<Id(4u)>]
            SchemaVersion: int
            [<Id(5u)>]
            OperationId: LibraryOperationId
            [<Id(6u)>]
            RequestHash: string
            [<Id(7u)>]
            Receipt: LibraryOperationReceiptDto
            [<Id(8u)>]
            Cursor: int64 option
            [<Id(9u)>]
            AppliedThrough: int64
        }

    /// Stores one canonical-derived item or path history entry.
    [<CLIMutable; GenerateSerializer>]
    type LibraryHistoryEntry =
        {
            [<Id(0u)>]
            Cursor: int64
            [<Id(1u)>]
            PublicCursor: LibraryCursor
            [<Id(2u)>]
            OperationId: LibraryOperationId
            [<Id(3u)>]
            ItemId: LibraryItemId
            [<Id(4u)>]
            PriorNamespace: LibraryNamespaceDto option
            [<Id(5u)>]
            ResultingNamespace: LibraryNamespaceDto option
            [<Id(6u)>]
            PriorContentVersionId: LibraryContentVersionId option
            [<Id(7u)>]
            ResultingContentVersionId: LibraryContentVersionId option
            [<Id(8u)>]
            Tombstone: LibraryTombstoneDto option
            [<Id(9u)>]
            Conflict: LibraryConflictProvenanceDto option
            [<Id(10u)>]
            PrincipalId: PrincipalId
            [<Id(11u)>]
            AcceptedAt: Instant
        }

    /// Stores at most 512 canonical-derived history entries under a byte bound.
    [<CLIMutable; GenerateSerializer>]
    type LibraryHistorySegmentDocument =
        {
            [<Id(0u)>]
            id: string
            [<Id(1u)>]
            RepositoryId: RepositoryId
            [<Id(2u)>]
            HistoryKey: string
            [<Id(3u)>]
            SchemaVersion: int
            [<Id(4u)>]
            HistorySegment: string
            [<Id(5u)>]
            FirstCursor: int64
            [<Id(6u)>]
            LastCursor: int64
            [<Id(7u)>]
            EntryCount: int
            [<Id(8u)>]
            Entries: LibraryHistoryEntry array
        }

    /// Stores one immutable byte-bounded current-state baseline shard.
    [<CLIMutable; GenerateSerializer>]
    type LibraryBaselineShardDocument =
        {
            [<Id(0u)>]
            id: string
            [<Id(1u)>]
            RepositoryId: RepositoryId
            [<Id(2u)>]
            SchemaVersion: int
            [<Id(3u)>]
            BaselineId: LibraryBootstrapId
            [<Id(4u)>]
            ShardKey: string
            [<Id(5u)>]
            BoundaryCursor: int64
            [<Id(6u)>]
            Items: LibraryItemDto array
            [<Id(7u)>]
            ItemCount: int
            [<Id(8u)>]
            SerializedBytes: int
        }

    /// Publishes a baseline only after every named shard is durable and hash-verified.
    [<CLIMutable; GenerateSerializer>]
    type LibraryBaselineManifestDocument =
        {
            [<Id(0u)>]
            id: string
            [<Id(1u)>]
            RepositoryId: RepositoryId
            [<Id(2u)>]
            SchemaVersion: int
            [<Id(3u)>]
            BaselineId: LibraryBootstrapId
            [<Id(4u)>]
            ShardKey: string
            [<Id(5u)>]
            BoundaryCursor: int64
            [<Id(6u)>]
            CursorEpoch: Guid
            [<Id(7u)>]
            LibraryCatalogVersion: LibraryCatalogVersion
            [<Id(8u)>]
            LibraryCatalog: LibraryCatalogDto
            [<Id(9u)>]
            ShardIds: string array
            [<Id(10u)>]
            ShardHashes: string array
            [<Id(11u)>]
            ShardItemCounts: int array
            [<Id(12u)>]
            TotalItemCount: int
            [<Id(13u)>]
            CreatedAt: Instant
        }

    /// Stores at most 512 deterministic record identities in one current-projection hash bucket.
    [<CLIMutable; GenerateSerializer>]
    type LibraryCurrentIndexBucketDocument =
        {
            [<Id(0u)>]
            Identities: string array
        }

        /// Returns an empty bounded current-projection index bucket.
        static member Empty = { Identities = Array.empty }

    /// Stores the exact occupancy of the 256 bounded buckets below one index-directory prefix.
    [<CLIMutable; GenerateSerializer>]
    type LibraryCurrentIndexDirectoryDocument =
        {
            [<Id(0u)>]
            Counts: int array
        }

        /// Returns an empty fixed-width directory whose entries correspond to one low hash byte.
        static member Empty = { Counts = Array.zeroCreate 256 }

    /// Stores one immutable catalog-operation result in the same repository partition as its atomic catalog mutation.
    [<CLIMutable; GenerateSerializer>]
    type LibraryCatalogOperationDocument =
        {
            [<Id(0u)>]
            id: string
            [<Id(1u)>]
            RepositoryId: RepositoryId
            [<Id(2u)>]
            SchemaVersion: int
            [<Id(3u)>]
            OperationId: LibraryOperationId
            [<Id(4u)>]
            RequestHash: string
            [<Id(5u)>]
            Result: LibraryCatalogChangeResultDto
        }

    /// Retains the existing immutable-content location behind one public content-version identity.
    [<CLIMutable; GenerateSerializer>]
    type LibraryContentLocationDocument =
        {
            [<Id(0u)>]
            id: string
            [<Id(1u)>]
            RepositoryId: RepositoryId
            [<Id(2u)>]
            RecordKind: string
            [<Id(3u)>]
            RecordKey: string
            [<Id(4u)>]
            SchemaVersion: int
            [<Id(5u)>]
            Content: LibraryContentVersionDto
            [<Id(6u)>]
            AuthorizedScope: string
            [<Id(7u)>]
            Manifest: FileManifest
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

        /// Reads one immutable catalog-operation result by its deterministic operation identity.
        abstract member ReadCatalogOperationAsync:
            repositoryId: RepositoryId * operationId: LibraryOperationId * cancellationToken: CancellationToken -> Task<LibraryCatalogOperationDocument option>

        /// Atomically replaces the catalog control state and creates its immutable operation result in one repository partition.
        abstract member ReplaceControlAndCreateCatalogOperationAsync:
            control: LibraryControlDocument * etag: string * operation: LibraryCatalogOperationDocument * cancellationToken: CancellationToken ->
                Task<LibraryControlWriteResult>

        /// Creates an immutable non-mutating catalog-operation result or verifies its exact replay.
        abstract member CreateCatalogOperationAsync: operation: LibraryCatalogOperationDocument * cancellationToken: CancellationToken -> Task

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

    /// Defines the retained immutable-content locations used by Library uploads and authorized reads.
    type ILibraryTransferStore =

        /// Retains the private immutable-content location behind a public content-version identity.
        abstract member UpsertContentLocationAsync: location: LibraryContentLocationDocument * cancellationToken: CancellationToken -> Task

        /// Reads the retained location for one public content-version identity.
        abstract member ReadContentLocationAsync:
            repositoryId: RepositoryId * contentVersionId: LibraryContentVersionId * cancellationToken: CancellationToken ->
                Task<LibraryContentLocationDocument option>

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
            repositoryId: RepositoryId * requestHash: string * result: LibraryCatalogChangeResultDto * cancellationToken: CancellationToken ->
                Task<LibraryCatalogChangeResultDto>

        /// Classifies one normalized repository-relative path against the catalog snapshot read for this call.
        abstract member IsInLibraryAsync: repositoryId: RepositoryId * relativePath: string * cancellationToken: CancellationToken -> Task<bool>

        /// Repairs any reserved command and submits one validated, authorized deterministic change.
        abstract member SubmitAsync:
            command: LibraryChangeCommand * principalId: PrincipalId * correlationId: CorrelationId * cancellationToken: CancellationToken ->
                Task<LibraryOperationReceiptDto>

        /// Repairs one repository's pending publication lifecycle without accepting another command.
        abstract member RepairAsync: repositoryId: RepositoryId * cancellationToken: CancellationToken -> Task

        /// Replays asynchronous history idempotently from accepted canonical changes through the current commit boundary.
        abstract member ProjectHistoryAsync: repositoryId: RepositoryId * cancellationToken: CancellationToken -> Task

        /// Returns truthful content-free server state after repairing any pending accepted command.
        abstract member GetStatusAsync: repositoryId: RepositoryId * cancellationToken: CancellationToken -> Task<LibraryRepositoryStatusDto>

    /// Rechecks current Library-write authority immediately before the actor can enter the durable reservation path.
    type ILibraryWriteAuthorizer =

        /// Evaluates the supplied authenticated facts against the current repository-scoped Library permission source.
        abstract member CheckAsync:
            repositoryId: RepositoryId * authorization: LibraryWriteAuthorization * cancellationToken: CancellationToken -> Task<PermissionCheckResult>
