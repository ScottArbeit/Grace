namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open Grace.Shared.Types
open System

module Diff =
    
    /// Parameters used by multiple endpoints in the /diff path.
    type DiffParameters() = 
        inherit CommonParameters()
        member val public OwnerId = String.Empty with get, set
        member val public OwnerName: OwnerName = String.Empty with get, set
        member val public OrganizationId = String.Empty with get, set
        member val public OrganizationName: OrganizationName = String.Empty with get, set
        member val public RepositoryId = String.Empty with get, set
        member val public RepositoryName: RepositoryName = String.Empty with get, set
        member val public DirectoryId1: DirectoryId = DirectoryId.Empty with get, set
        member val public DirectoryId2: DirectoryId = DirectoryId.Empty with get, set

    /// Parameters used by the /diff/populate endpoint.
    type PopulateParameters() =
        inherit DiffParameters()

    /// Parameters used by the /diff/get endpoint.
    type GetDiffParameters() =
        inherit DiffParameters()

    type GetDiffByReferenceTypeParameters() =
        inherit DiffParameters()
        member val public BranchId = String.Empty with get, set
        member val public BranchName = BranchName String.Empty with get, set

    /// Parameters used by the /diff/getDiffBySha256Hash endpoint.
    type GetDiffBySha256HashParameters() =
        inherit DiffParameters()
        member val public Sha256Hash1 = Sha256Hash String.Empty with get, set
        member val public Sha256Hash2 = Sha256Hash String.Empty with get, set
