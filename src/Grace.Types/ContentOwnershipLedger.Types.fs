namespace Grace.Types

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Common
open Orleans
open System
open System.Collections.Generic
open System.Runtime.Serialization

/// Contains manifest-backed content ownership ledger contracts.
module ContentOwnershipLedger =

    /// Identifies the active-usage owner that is billed for a manifest-backed content entry.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ContentOwnershipOwnerScope =
        | ContributorOwned of repositoryId: RepositoryId * creatorUserId: UserId
        | RepositoryOwned of repositoryId: RepositoryId

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ContentOwnershipOwnerScope>()

    /// Captures accepted promotion evidence that makes a transfer retry-stable.
    [<GenerateSerializer>]
    type AcceptedContentTransferEvidence =
        {
            [<Id(0u)>]
            PromotionSetId: PromotionSetId

            [<Id(1u)>]
            PromotionSetStepId: PromotionSetStepId

            [<Id(2u)>]
            StepsComputationAttempt: int

            [<Id(3u)>]
            AppliedDirectoryVersionId: DirectoryVersionId
        }

    /// Represents a content ownership ledger command.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ContentOwnershipLedgerCommand =
        | AddActiveUsage of
            operationId: ContentOwnershipLedgerOperationId *
            repositoryId: RepositoryId *
            storagePoolId: StoragePoolId *
            manifestAddress: ManifestAddress *
            activeUsageId: ContentOwnershipLedgerActiveUsageId *
            ownerScope: ContentOwnershipOwnerScope
        | RemoveActiveUsage of
            operationId: ContentOwnershipLedgerOperationId *
            repositoryId: RepositoryId *
            storagePoolId: StoragePoolId *
            manifestAddress: ManifestAddress *
            activeUsageId: ContentOwnershipLedgerActiveUsageId
        | TransferAcceptedContent of
            operationId: ContentOwnershipLedgerOperationId *
            repositoryId: RepositoryId *
            storagePoolId: StoragePoolId *
            manifestAddress: ManifestAddress *
            sourceOwnerScope: ContentOwnershipOwnerScope *
            targetOwnerScope: ContentOwnershipOwnerScope *
            acceptedEvidence: AcceptedContentTransferEvidence

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ContentOwnershipLedgerCommand>()

    /// Represents active usage added to the manifest-backed content ownership ledger.
    [<GenerateSerializer>]
    type ContentOwnershipActiveUsageAdded =
        {
            [<Id(0u)>]
            OperationId: ContentOwnershipLedgerOperationId

            [<Id(1u)>]
            RepositoryId: RepositoryId

            [<Id(2u)>]
            StoragePoolId: StoragePoolId

            [<Id(3u)>]
            ManifestAddress: ManifestAddress

            [<Id(4u)>]
            ActiveUsageId: ContentOwnershipLedgerActiveUsageId

            [<Id(5u)>]
            OwnerScope: ContentOwnershipOwnerScope
        }

    /// Represents active usage removed from the manifest-backed content ownership ledger.
    [<GenerateSerializer>]
    type ContentOwnershipActiveUsageRemoved =
        {
            [<Id(0u)>]
            OperationId: ContentOwnershipLedgerOperationId

            [<Id(1u)>]
            RepositoryId: RepositoryId

            [<Id(2u)>]
            StoragePoolId: StoragePoolId

            [<Id(3u)>]
            ManifestAddress: ManifestAddress

            [<Id(4u)>]
            ActiveUsageId: ContentOwnershipLedgerActiveUsageId

            [<Id(5u)>]
            RemovedOwnerScope: ContentOwnershipOwnerScope
        }

    /// Represents an accepted promotion transfer applied to active manifest-backed usage.
    [<GenerateSerializer>]
    type AcceptedContentOwnershipTransferred =
        {
            [<Id(0u)>]
            OperationId: ContentOwnershipLedgerOperationId

            [<Id(1u)>]
            RepositoryId: RepositoryId

            [<Id(2u)>]
            StoragePoolId: StoragePoolId

            [<Id(3u)>]
            ManifestAddress: ManifestAddress

            [<Id(4u)>]
            SourceOwnerScope: ContentOwnershipOwnerScope

            [<Id(5u)>]
            TargetOwnerScope: ContentOwnershipOwnerScope

            [<Id(6u)>]
            AcceptedEvidence: AcceptedContentTransferEvidence

            [<Id(7u)>]
            TransferredActiveUsageIds: ContentOwnershipLedgerActiveUsageId array
        }

    /// Represents a content ownership ledger event type.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ContentOwnershipLedgerEventType =
        | ActiveUsageAdded of ContentOwnershipActiveUsageAdded
        | ActiveUsageRemoved of ContentOwnershipActiveUsageRemoved
        | AcceptedContentTransferred of AcceptedContentOwnershipTransferred

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ContentOwnershipLedgerEventType>()

    /// Represents the content ownership ledger event contract.
    [<GenerateSerializer>]
    type ContentOwnershipLedgerEvent = { Event: ContentOwnershipLedgerEventType; Metadata: EventMetadata }

    /// Represents the active ownership snapshot for one manifest-backed content identity.
    [<GenerateSerializer>]
    type ContentOwnershipLedgerDto =
        {
            Class: string
            StoragePoolId: StoragePoolId
            ManifestAddress: ManifestAddress
            ActiveUsageOwners: Dictionary<ContentOwnershipLedgerActiveUsageId, ContentOwnershipOwnerScope>
            ActiveUsageByOwner: Dictionary<ContentOwnershipOwnerScope, ReferenceCount>
            LastOperationId: ContentOwnershipLedgerOperationId option
        }

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default =
            {
                Class = nameof ContentOwnershipLedgerDto
                StoragePoolId = String.Empty
                ManifestAddress = String.Empty
                ActiveUsageOwners = Dictionary<ContentOwnershipLedgerActiveUsageId, ContentOwnershipOwnerScope>(StringComparer.Ordinal)
                ActiveUsageByOwner = Dictionary<ContentOwnershipOwnerScope, ReferenceCount>()
                LastOperationId = None
            }

        /// Clones active usage owners so event replay never mutates a previous snapshot.
        static member private CloneActiveUsageOwners(activeUsageOwners: Dictionary<ContentOwnershipLedgerActiveUsageId, ContentOwnershipOwnerScope>) =
            Dictionary<ContentOwnershipLedgerActiveUsageId, ContentOwnershipOwnerScope>(activeUsageOwners, StringComparer.Ordinal)

        /// Rebuilds owner-scope counts from active usage entries after an ownership transition.
        static member private OwnerCounts(activeUsageOwners: Dictionary<ContentOwnershipLedgerActiveUsageId, ContentOwnershipOwnerScope>) =
            let counts = Dictionary<ContentOwnershipOwnerScope, ReferenceCount>()

            for ownerScope in activeUsageOwners.Values do
                let mutable current = 0L

                if counts.TryGetValue(ownerScope, &current) then
                    counts[ownerScope] <- current + 1L
                else
                    counts[ownerScope] <- 1L

            counts

        /// Creates the DTO shape used to carry partial updates without mutating the persisted aggregate directly.
        static member UpdateDto ledgerEvent current =
            match ledgerEvent.Event with
            | ContentOwnershipLedgerEventType.ActiveUsageAdded added ->
                let activeUsageOwners = ContentOwnershipLedgerDto.CloneActiveUsageOwners current.ActiveUsageOwners
                activeUsageOwners[added.ActiveUsageId] <- added.OwnerScope

                { current with
                    StoragePoolId =
                        if String.IsNullOrWhiteSpace current.StoragePoolId then
                            added.StoragePoolId
                        else
                            current.StoragePoolId
                    ManifestAddress =
                        if String.IsNullOrWhiteSpace current.ManifestAddress then
                            added.ManifestAddress
                        else
                            current.ManifestAddress
                    ActiveUsageOwners = activeUsageOwners
                    ActiveUsageByOwner = ContentOwnershipLedgerDto.OwnerCounts activeUsageOwners
                    LastOperationId = Some added.OperationId
                }
            | ContentOwnershipLedgerEventType.ActiveUsageRemoved removed ->
                let activeUsageOwners = ContentOwnershipLedgerDto.CloneActiveUsageOwners current.ActiveUsageOwners

                activeUsageOwners.Remove removed.ActiveUsageId
                |> ignore

                { current with
                    ActiveUsageOwners = activeUsageOwners
                    ActiveUsageByOwner = ContentOwnershipLedgerDto.OwnerCounts activeUsageOwners
                    LastOperationId = Some removed.OperationId
                }
            | ContentOwnershipLedgerEventType.AcceptedContentTransferred transferred ->
                let activeUsageOwners = ContentOwnershipLedgerDto.CloneActiveUsageOwners current.ActiveUsageOwners

                for activeUsageId in transferred.TransferredActiveUsageIds do
                    activeUsageOwners[activeUsageId] <- transferred.TargetOwnerScope

                { current with
                    StoragePoolId =
                        if String.IsNullOrWhiteSpace current.StoragePoolId then
                            transferred.StoragePoolId
                        else
                            current.StoragePoolId
                    ManifestAddress =
                        if String.IsNullOrWhiteSpace current.ManifestAddress then
                            transferred.ManifestAddress
                        else
                            current.ManifestAddress
                    ActiveUsageOwners = activeUsageOwners
                    ActiveUsageByOwner = ContentOwnershipLedgerDto.OwnerCounts activeUsageOwners
                    LastOperationId = Some transferred.OperationId
                }

    /// Represents a content ownership ledger decision.
    [<GenerateSerializer>]
    type ContentOwnershipLedgerDecision =
        {
            Ledger: ContentOwnershipLedgerDto
            OperationId: ContentOwnershipLedgerOperationId
            Events: ContentOwnershipLedgerEvent list
            WasIdempotentReplay: bool
            Message: string
        }
