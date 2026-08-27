namespace Grace.Types

open Grace.Types.Authorization
open Grace.Types.Common
open NodaTime
open Orleans
open System
open System.Security.Cryptography
open System.Text

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
    type SynchronizedContentReadGrantDto = { GrantId: Guid; DownloadPath: string; Content: SynchronizedContentVersionDto; ExpiresAt: Instant }

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
