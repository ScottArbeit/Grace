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
    type LibraryCursor = string

    /// Identifies one feed generation by equality, without ordering or timestamp meaning.
    [<Struct; NoComparison>]
    type LibraryCursorEpoch = LibraryCursorEpoch of Guid

    /// Converts feed generation identities at GUID text boundaries without interpreting cursor tokens.
    [<RequireQualifiedAccess>]
    module LibraryCursorEpoch =
        /// Wraps the server's existing generation identity without accepting another domain identifier implicitly.
        let ofGuid value = LibraryCursorEpoch value

        /// Writes the canonical hyphenated GUID representation used on the wire and in SQLite.
        let toString (LibraryCursorEpoch value) = value.ToString("D")

        /// Rejects malformed or non-D text before local state can authorize synchronization effects.
        let parse (text: string) =
            match Guid.TryParseExact(text, "D") with
            | true, value when not (isNull text) && text.Length = 36 -> LibraryCursorEpoch value
            | _ -> raise (FormatException("Library cursor epoch must be a GUID in D format."))

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

    /// Describes one current parent/name placement and its independent concurrency version.
    [<CLIMutable; GenerateSerializer>]
    type LibraryNamespaceDto =
        {
            [<Id(0u)>]
            Parent: LibraryParentDto
            [<Id(1u)>]
            Name: string
            [<Id(2u)>]
            NamespaceVersion: LibraryNamespaceVersion
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

    /// Records the durable deletion facts retained on one stable Library item.
    [<CLIMutable; GenerateSerializer>]
    type LibraryTombstoneDto =
        {
            [<Id(0u)>]
            DeletedAt: Instant
            [<Id(1u)>]
            DeletedBy: PrincipalId
            [<Id(2u)>]
            DeleteCursor: LibraryCursor
            [<Id(3u)>]
            LastNamespace: LibraryNamespaceDto
            [<Id(4u)>]
            LastContentVersionId: LibraryContentVersionId option
        }

    /// Records the causal base that produced a deterministic conflict copy.
    [<CLIMutable; GenerateSerializer>]
    type LibraryConflictProvenanceDto =
        {
            [<Id(0u)>]
            OriginalItemId: LibraryItemId
            [<Id(1u)>]
            BaseContentVersionId: LibraryContentVersionId option
            [<Id(2u)>]
            BaseContentRevision: LibraryCursor option
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
            LastChangeCursor: LibraryCursor
            [<Id(3u)>]
            Namespace: LibraryNamespaceDto option
            [<Id(4u)>]
            Content: LibraryContentVersionDto option
            [<Id(5u)>]
            ContentRevision: LibraryCursor option
            [<Id(6u)>]
            Tombstone: LibraryTombstoneDto option
        }

    /// Describes one occupied or remembered-vacant parent/name namespace slot.
    [<CLIMutable; GenerateSerializer>]
    type LibraryNamespaceSlotDto =
        {
            [<Id(0u)>]
            Parent: LibraryParentDto
            [<Id(1u)>]
            Name: string
            [<Id(2u)>]
            SlotVersion: LibraryNamespaceSlotVersion
            [<Id(3u)>]
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
            [<Id(2u)>]
            ExpectedContentRevision: LibraryCursor
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

    /// Describes one accepted repository-ordered Library change and its resulting item.
    [<CLIMutable; GenerateSerializer>]
    type LibraryChangeDto =
        {
            [<Id(0u)>]
            OperationId: LibraryOperationId
            [<Id(1u)>]
            ChangeKind: string
            [<Id(2u)>]
            AcceptedAt: Instant
            [<Id(3u)>]
            AcceptedBy: PrincipalId
            [<Id(4u)>]
            LibraryCatalogVersion: LibraryCatalogVersion
            [<Id(5u)>]
            Item: LibraryItemDto
            [<Id(6u)>]
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
            Change: LibraryChangeDto option
            [<Id(4u)>]
            ReasonCode: string option
            [<Id(5u)>]
            CurrentLibraryCatalog: LibraryCatalogDto option
            [<Id(6u)>]
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

    /// Carries one bounded immutable current-state bootstrap page.
    [<CLIMutable; GenerateSerializer>]
    type LibraryBootstrapPageDto =
        {
            [<Id(0u)>]
            BootstrapId: LibraryBootstrapId
            [<Id(1u)>]
            BoundaryCursor: LibraryCursor
            [<Id(2u)>]
            CursorEpoch: LibraryCursorEpoch
            [<Id(3u)>]
            LibraryCatalog: LibraryCatalogDto
            [<Id(4u)>]
            Items: LibraryItemDto array
            [<Id(5u)>]
            NextPageToken: LibraryPageToken option
        }

    /// Carries ordered accepted changes or a typed rebaseline instruction.
    [<CLIMutable; GenerateSerializer>]
    type LibraryChangePageDto =
        {
            [<Id(0u)>]
            Outcome: string
            [<Id(1u)>]
            CursorEpoch: LibraryCursorEpoch
            [<Id(2u)>]
            Changes: LibraryChangeDto array
            [<Id(3u)>]
            LastCursor: LibraryCursor
            [<Id(4u)>]
            HasMore: bool
            [<Id(5u)>]
            NextPageToken: LibraryPageToken option
            [<Id(6u)>]
            Rebaseline: LibraryRebaselineDto option
        }

    /// Binds immutable bytes to the existing authorized upload session used by a later Library change.
    [<CLIMutable; GenerateSerializer>]
    type LibraryContentPreparationDto =
        {
            [<Id(0u)>]
            UploadSessionId: UploadSessionId
            [<Id(1u)>]
            Blake3Hash: string
            [<Id(2u)>]
            Sha256Hash: string
            [<Id(3u)>]
            Size: int64
            [<Id(4u)>]
            AuthorizedScope: string
            [<Id(5u)>]
            StoragePoolId: StoragePoolId
            [<Id(6u)>]
            ExpiresAt: Instant
        }

    /// Grants one authorized short-lived read of exact accepted immutable bytes.
    [<CLIMutable; GenerateSerializer>]
    type LibraryContentReadDto =
        {
            [<Id(0u)>]
            DownloadPath: string
            [<Id(1u)>]
            Content: LibraryContentVersionDto
            [<Id(2u)>]
            ExpiresAt: Instant
        }

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
            [<Id(0u)>]
            EventName: string
            [<Id(1u)>]
            RepositoryId: RepositoryId
            [<Id(2u)>]
            CursorEpoch: LibraryCursorEpoch
            [<Id(3u)>]
            AvailableAfterCursor: LibraryCursor
            [<Id(4u)>]
            LibraryCatalogVersion: LibraryCatalogVersion
            [<Id(5u)>]
            OccurredAt: Instant
            [<Id(6u)>]
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

    /// Stores the exact serialized notification retained after a terminal Service Bus failure.
    [<CLIMutable; GenerateSerializer>]
    type FailedGraceEventEnvelope =
        {
            [<Id(0u)>]
            TopicName: string
            [<Id(1u)>]
            MessageId: string
            [<Id(2u)>]
            Body: byte array
            [<Id(3u)>]
            ContentType: string
            [<Id(4u)>]
            Subject: string
            [<Id(5u)>]
            CorrelationId: CorrelationId
            [<Id(6u)>]
            ApplicationProperties: Collections.Generic.Dictionary<string, string>
        }

    /// Carries one validated Library change after HTTP shape checks and before actor-owned state checks.
    type LibraryChangeCommand =
        | CreateFile of LibraryOperationId * string * LibraryCatalogVersion * LibraryCreationSlotExpectationDto * UploadSessionId
        | CreateDirectory of LibraryOperationId * string * LibraryCatalogVersion * LibraryCreationSlotExpectationDto
        | UpdateContent of
            LibraryOperationId *
            string *
            LibraryCatalogVersion *
            LibraryItemId *
            LibraryNamespacePreconditionDto option *
            LibraryContentPreconditionDto *
            UploadSessionId
        | Rename of LibraryOperationId * string * LibraryCatalogVersion * LibraryItemId * LibraryNamespacePreconditionDto * string
        | Move of LibraryOperationId * string * LibraryCatalogVersion * LibraryItemId * LibraryNamespacePreconditionDto * LibraryParentDto
        | Delete of LibraryOperationId * string * LibraryCatalogVersion * LibraryItemId * LibraryNamespacePreconditionDto * LibraryContentPreconditionDto option

    /// Persists one immutable accepted item change in the repository journal.
    [<CLIMutable; GenerateSerializer>]
    type LibraryAcceptedChangeRecord =
        {
            [<Id(0u)>]
            SchemaVersion: int
            [<Id(1u)>]
            Cursor: int64
            [<Id(2u)>]
            RequestHash: string
            [<Id(3u)>]
            CorrelationId: CorrelationId
            [<Id(4u)>]
            Change: LibraryChangeDto
            [<Id(5u)>]
            PriorNamespace: LibraryNamespaceDto option
            [<Id(6u)>]
            PriorContentVersionId: LibraryContentVersionId option
            [<Id(7u)>]
            ConsumedNamespaceVersion: LibraryNamespaceVersion option
            [<Id(8u)>]
            ConsumedContentVersionId: LibraryContentVersionId option
            [<Id(9u)>]
            ConsumedContentRevision: LibraryCursor option
            [<Id(10u)>]
            ConsumedSlotVersion: LibraryNamespaceSlotVersion option
            [<Id(11u)>]
            AddedItemRecord: bool
            [<Id(12u)>]
            AddedSlotRecord: bool
        }

    /// Persists the complete accepted decision needed to finish one interrupted repository turn.
    type LibraryPendingDecision =
        | ItemChange of LibraryAcceptedChangeRecord
        | CatalogChange of LibraryOperationId * string * LibraryCatalogVersion * LibraryCatalogVersion * bool * string * Instant * PrincipalId

    /// Persists the bounded serialized command lane and its independent background progress.
    [<CLIMutable; GenerateSerializer>]
    type LibraryControlDocument =
        {
            [<Id(0u)>]
            SchemaVersion: int
            [<Id(1u)>]
            Catalog: LibraryCatalogDto
            [<Id(2u)>]
            Epoch: Guid
            [<Id(3u)>]
            CommittedCursor: int64
            [<Id(4u)>]
            ReplayFloor: int64
            [<Id(5u)>]
            Pending: LibraryPendingDecision option
            [<Id(6u)>]
            ItemRecordCount: int
            [<Id(7u)>]
            SlotRecordCount: int
            [<Id(8u)>]
            HistoryThrough: int64
            [<Id(9u)>]
            NotifyThrough: int64
        }

    /// Stores one current item and the newest history segment that mentions it.
    [<CLIMutable; GenerateSerializer>]
    type LibraryCurrentItemDocument =
        {
            [<Id(0u)>]
            SchemaVersion: int
            [<Id(1u)>]
            Item: LibraryItemDto
            [<Id(2u)>]
            LastCursor: int64
            [<Id(3u)>]
            HistoryTailSegment: string option
        }

    /// Stores one remembered parent/name slot and the newest history segment that mentions it.
    [<CLIMutable; GenerateSerializer>]
    type LibraryCurrentSlotDocument =
        {
            [<Id(0u)>]
            SchemaVersion: int
            [<Id(1u)>]
            Slot: LibraryNamespaceSlotDto
            [<Id(2u)>]
            LastCursor: int64
            [<Id(3u)>]
            HistoryTailSegment: string option
        }

    /// Distinguishes the three durable operation-result forms stored at one receipt key.
    type LibraryOperationOutcome =
        | AcceptedChange of int64
        | RejectedChange of LibraryOperationReceiptDto
        | CatalogResult of LibraryCatalogChangeResultDto

    /// Stores one permanent deterministic operation result for retry and audit lookup.
    [<CLIMutable; GenerateSerializer>]
    type LibraryReceiptDocument =
        {
            [<Id(0u)>]
            SchemaVersion: int
            [<Id(1u)>]
            OperationId: LibraryOperationId
            [<Id(2u)>]
            RequestHash: string
            [<Id(3u)>]
            Outcome: LibraryOperationOutcome
        }

    /// Stores one compact exact-key history segment containing permanent journal cursors.
    [<CLIMutable; GenerateSerializer>]
    type LibraryHistorySegmentDocument =
        {
            [<Id(0u)>]
            SchemaVersion: int
            [<Id(1u)>]
            PreviousSegment: string option
            [<Id(2u)>]
            Cursors: int64 array
        }

    /// Stores one immutable byte-bounded current-state baseline shard.
    [<CLIMutable; GenerateSerializer>]
    type LibraryBaselineShardDocument =
        {
            [<Id(0u)>]
            SchemaVersion: int
            [<Id(1u)>]
            Items: LibraryItemDto array
        }

    /// Identifies and verifies one immutable baseline shard.
    [<CLIMutable; GenerateSerializer>]
    type LibraryBaselineShardReference =
        {
            [<Id(0u)>]
            Ordinal: int
            [<Id(1u)>]
            Blake3Hash: string
            [<Id(2u)>]
            ItemCount: int
        }

    /// Publishes a baseline only after every referenced shard is durable.
    [<CLIMutable; GenerateSerializer>]
    type LibraryBaselineManifestDocument =
        {
            [<Id(0u)>]
            SchemaVersion: int
            [<Id(1u)>]
            Epoch: Guid
            [<Id(2u)>]
            BoundaryCursor: int64
            [<Id(3u)>]
            Catalog: LibraryCatalogDto
            [<Id(4u)>]
            CreatedAt: Instant
            [<Id(5u)>]
            Shards: LibraryBaselineShardReference array
        }

    /// Retains the complete upload descriptor and manifest for accepted historical reads.
    [<CLIMutable; GenerateSerializer>]
    type LibraryContentLocationDocument =
        {
            [<Id(0u)>]
            SchemaVersion: int
            [<Id(1u)>]
            Content: LibraryContentVersionDto
            [<Id(2u)>]
            AuthorizedScope: string
            [<Id(3u)>]
            Manifest: FileManifest
        }
