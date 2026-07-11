namespace Grace.Types

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Common
open Orleans
open System
open System.Runtime.Serialization

/// Contains the internal pooled-storage admission contract for a repository owner's billing account.
module BillingAccount =

    /// Included pooled storage for a free BillingAccount.
    [<Literal>]
    let IncludedStorageBytes = 1_073_741_824L

    /// Default percentage of pooled capacity reserved for external contributions.
    [<Literal>]
    let DefaultExternalPercent = 10

    /// Smallest configurable external contribution percentage.
    [<Literal>]
    let MinimumExternalPercent = 1

    /// Largest configurable external contribution percentage.
    [<Literal>]
    let MaximumExternalPercent = 50

    /// Default number of concurrently retained external contribution branches.
    [<Literal>]
    let DefaultExternalBranchLimit = 10

    /// Identifies whether retained bytes consume the protected external-contribution budget.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type StorageClass =
        | Repository
        | ExternalContribution

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<StorageClass>()

    /// Describes one durable reservation and its terminal accounting state.
    [<GenerateSerializer>]
    type Reservation =
        {
            OperationId: string
            RepositoryId: RepositoryId
            ContributorId: UserId
            RetainingBranchId: BranchId option
            Bytes: int64
            StorageClass: StorageClass
            Settled: bool
            Promoted: bool
            Released: bool
        }

    /// Holds O(1) pooled and external storage counters plus durable retry evidence.
    [<GenerateSerializer>]
    type BillingAccountDto =
        {
            Class: string
            OwnerId: OwnerId
            CapacityBytes: int64
            UsedBytes: int64
            ExternalBytes: int64
            ActiveExternalBranches: int
            ExternalBranchReservationCounts: Map<BranchId, int>
            ExternalPercent: int
            ExternalBranchLimit: int
            Reservations: Map<string, Reservation>
        }

        static member Default =
            {
                Class = nameof BillingAccountDto
                OwnerId = OwnerId.Empty
                CapacityBytes = IncludedStorageBytes
                UsedBytes = 0L
                ExternalBytes = 0L
                ActiveExternalBranches = 0
                ExternalBranchReservationCounts = Map.empty
                ExternalPercent = DefaultExternalPercent
                ExternalBranchLimit = DefaultExternalBranchLimit
                Reservations = Map.empty
            }

    /// Requests an atomic transition of pooled storage admission state.
    [<KnownType("GetKnownTypes")>]
    type BillingAccountCommand =
        | Reserve of operationId: string * ownerId: OwnerId * repositoryId: RepositoryId * contributorId: UserId * bytes: int64 * storageClass: StorageClass
        | Settle of operationId: string * reservationOperationId: string * repositoryId: RepositoryId * branchId: BranchId
        | Promote of operationId: string * reservationOperationId: string * repositoryId: RepositoryId * branchId: BranchId
        | Release of operationId: string * reservationOperationId: string * repositoryId: RepositoryId * branchId: BranchId

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<BillingAccountCommand>()

    /// Records a complete post-command aggregate snapshot for deterministic replay and idempotency.
    and BillingAccountEvent = { OperationId: string; Command: BillingAccountCommand; State: BillingAccountDto; Metadata: EventMetadata }

    /// Returns the aggregate state and whether the operation was an idempotent replay.
    [<GenerateSerializer>]
    type BillingAccountDecision = { Account: BillingAccountDto; OperationId: string; Events: BillingAccountEvent list; WasIdempotentReplay: bool }
