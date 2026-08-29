namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Repository
open Grace.Types.Library
open System

/// Defines request records for the complete remote `/libraries` route family.
module Library =

    /// Provides the repository locator inherited by every library request.
    type LibraryParameters() =
        inherit RepositoryParameters()

    /// Requests the current persisted synchronization root configuration.
    type GetLibraryCatalogParameters() =
        inherit LibraryParameters()

    /// Requests the current sorted synchronization libraries.
    type ListLibrariesParameters() =
        inherit LibraryParameters()

    /// Adds one normalized root under an exact configuration version.
    type AddLibraryParameters() =
        inherit LibraryParameters()
        member val ExpectedVersion = Guid.Empty with get, set
        member val LibraryPath = String.Empty with get, set
        member val OperationId = Guid.Empty with get, set

    /// Removes one normalized root under an exact configuration version.
    type RemoveLibraryParameters() =
        inherit LibraryParameters()
        member val ExpectedVersion = Guid.Empty with get, set
        member val LibraryPath = String.Empty with get, set
        member val OperationId = Guid.Empty with get, set

    /// Starts a bounded bootstrap from the latest published current-state baseline.
    type StartLibraryBootstrapParameters() =
        inherit LibraryParameters()
        member val PageSize = 500 with get, set

    /// Continues one immutable bootstrap baseline page sequence.
    type ContinueLibraryBootstrapParameters() =
        inherit LibraryParameters()
        member val BootstrapId = Guid.Empty with get, set
        member val PageToken = String.Empty with get, set
        member val PageSize = 500 with get, set

    /// Reads repository-ordered accepted changes after one opaque cursor.
    type GetLibraryChangesParameters() =
        inherit LibraryParameters()
        member val AfterCursor = String.Empty with get, set
        member val PageToken: string = null with get, set
        member val PageSize = 500 with get, set

    /// Submits one exact idempotent Library namespace or content change.
    type SubmitLibraryChangeParameters() =
        inherit LibraryParameters()
        member val OperationId = Guid.Empty with get, set
        member val LibraryCatalogVersion = Guid.Empty with get, set
        member val ChangeKind = String.Empty with get, set
        member val ItemKind = String.Empty with get, set
        member val ItemId: Nullable<Guid> = Nullable() with get, set
        member val NamespacePrecondition: LibraryNamespacePreconditionDto option = None with get, set
        member val ContentPrecondition: LibraryContentPreconditionDto option = None with get, set
        member val CreationSlotExpectation: LibraryCreationSlotExpectationDto option = None with get, set
        member val DestinationParent: LibraryParentDto option = None with get, set
        member val DestinationName: string = null with get, set
        member val PreparedContentId: Nullable<Guid> = Nullable() with get, set

    /// Reads the deterministic receipt for one authorized operation identity.
    type GetLibraryOperationParameters() =
        inherit LibraryParameters()
        member val OperationId = Guid.Empty with get, set

    /// Prepares exact immutable bytes for a later Library change.
    type PrepareLibraryContentParameters() =
        inherit LibraryParameters()
        member val OperationId = Guid.Empty with get, set
        member val Blake3Hash = String.Empty with get, set
        member val Sha256Hash = String.Empty with get, set
        member val Size = 0L with get, set

    /// Requests a one-use read grant for an authorized retained content version.
    type PrepareLibraryContentReadParameters() =
        inherit LibraryParameters()
        member val ItemId = Guid.Empty with get, set
        member val ContentVersionId = Guid.Empty with get, set

    /// Reads one current Library item after repository authorization.
    type GetLibraryItemParameters() =
        inherit LibraryParameters()
        member val ItemId = Guid.Empty with get, set

    /// Reads one normalized namespace slot and its current vacancy token.
    type GetLibraryNamespaceSlotParameters() =
        inherit LibraryParameters()
        member val Parent: LibraryParentDto option = None with get, set
        member val Name = String.Empty with get, set

    /// Requests content-free synchronization service status for one repository.
    type GetLibraryStatusParameters() =
        inherit LibraryParameters()
