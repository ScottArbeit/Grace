namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open Grace.Shared.Types
open System

module Reference =

    /// Parameters for many endpoints in the /branch path.
    type ReferenceParameters() =
        inherit CommonParameters()
        member val public OwnerId = String.Empty with get, set
        member val public OwnerName: OwnerName = String.Empty with get, set
        member val public OrganizationId = String.Empty with get, set
        member val public OrganizationName: OrganizationName = String.Empty with get, set
        member val public RepositoryId = String.Empty with get, set
        member val public RepositoryName: RepositoryName = String.Empty with get, set
        member val public BranchId = String.Empty with get, set
        member val public BranchName: BranchName = String.Empty with get, set

    /// Parameters for the /reference/assign endpoint.
    type AssignParameters() =
        inherit ReferenceParameters()
        member val public DirectoryVersionId: DirectoryVersionId = Guid.Empty with get, set
        member val public Sha256Hash: Sha256Hash = String.Empty with get, set
        member val public Message = String.Empty with get, set

    /// Parameters for the various /reference/create[reference] endpoints.
    type CreateReferenceParameters() =
        inherit ReferenceParameters()
        member val public DirectoryVersionId: DirectoryVersionId = Guid.Empty with get, set
        member val public Sha256Hash: Sha256Hash = String.Empty with get, set
        member val public Message = String.Empty with get, set
