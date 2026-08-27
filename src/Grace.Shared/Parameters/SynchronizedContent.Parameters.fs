namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Repository
open Grace.Types.SynchronizedContent
open System

/// Defines request records for the complete remote `/sync` route family.
module SynchronizedContent =

    /// Provides the repository locator inherited by every synchronized-content request.
    type SynchronizedContentParameters() =
        inherit RepositoryParameters()

    /// Requests the current persisted synchronization root configuration.
    type GetSynchronizedRootConfigurationParameters() =
        inherit SynchronizedContentParameters()

    /// Requests the current sorted synchronization roots.
    type ListSynchronizedRootsParameters() =
        inherit SynchronizedContentParameters()

    /// Adds one normalized root under an exact configuration version.
    type AddSynchronizedRootParameters() =
        inherit SynchronizedContentParameters()
        member val ExpectedVersion = Guid.Empty with get, set
        member val RootPath = String.Empty with get, set
        member val OperationId = Guid.Empty with get, set

    /// Removes one normalized root under an exact configuration version.
    type RemoveSynchronizedRootParameters() =
        inherit SynchronizedContentParameters()
        member val ExpectedVersion = Guid.Empty with get, set
        member val RootPath = String.Empty with get, set
        member val OperationId = Guid.Empty with get, set

    /// Starts a bounded bootstrap from the latest published current-state baseline.
    type StartSynchronizedBootstrapParameters() =
        inherit SynchronizedContentParameters()
        member val PageSize = 500 with get, set

    /// Continues one immutable bootstrap baseline page sequence.
    type ContinueSynchronizedBootstrapParameters() =
        inherit SynchronizedContentParameters()
        member val BootstrapId = Guid.Empty with get, set
        member val PageToken = String.Empty with get, set
        member val PageSize = 500 with get, set

    /// Reads repository-ordered accepted mutations after one opaque cursor.
    type GetSynchronizedDeltasParameters() =
        inherit SynchronizedContentParameters()
        member val AfterCursor = String.Empty with get, set
        member val PageToken: string = null with get, set
        member val PageSize = 500 with get, set

    /// Submits one exact idempotent synchronized namespace or content mutation.
    type SubmitSynchronizedMutationParameters() =
        inherit SynchronizedContentParameters()
        member val OperationId = Guid.Empty with get, set
        member val RootConfigurationVersion = Guid.Empty with get, set
        member val MutationKind = String.Empty with get, set
        member val ItemKind = String.Empty with get, set
        member val ItemId: Nullable<Guid> = Nullable() with get, set
        member val NamespacePrecondition: SynchronizedNamespacePreconditionDto option = None with get, set
        member val ContentPrecondition: SynchronizedContentPreconditionDto option = None with get, set
        member val CreationSlotExpectation: SynchronizedCreationSlotExpectationDto option = None with get, set
        member val DestinationParent: SynchronizedParentDto option = None with get, set
        member val DestinationName: string = null with get, set
        member val PreparedContentId: Nullable<Guid> = Nullable() with get, set

    /// Reads the deterministic receipt for one authorized operation identity.
    type GetSynchronizedOperationParameters() =
        inherit SynchronizedContentParameters()
        member val OperationId = Guid.Empty with get, set

    /// Prepares exact immutable bytes for a later synchronized mutation.
    type PrepareSynchronizedContentParameters() =
        inherit SynchronizedContentParameters()
        member val OperationId = Guid.Empty with get, set
        member val Blake3Hash = String.Empty with get, set
        member val Sha256Hash = String.Empty with get, set
        member val Size = 0L with get, set

    /// Requests a one-use read grant for an authorized retained content version.
    type PrepareSynchronizedContentReadParameters() =
        inherit SynchronizedContentParameters()
        member val ItemId = Guid.Empty with get, set
        member val ContentVersionId = Guid.Empty with get, set

    /// Reads one current synchronized item after repository authorization.
    type GetSynchronizedItemParameters() =
        inherit SynchronizedContentParameters()
        member val ItemId = Guid.Empty with get, set

    /// Reads one normalized namespace slot and its current vacancy token.
    type GetSynchronizedNamespaceSlotParameters() =
        inherit SynchronizedContentParameters()
        member val Parent: SynchronizedParentDto option = None with get, set
        member val Name = String.Empty with get, set

    /// Requests content-free synchronization service status for one repository.
    type GetSynchronizedStatusParameters() =
        inherit SynchronizedContentParameters()
