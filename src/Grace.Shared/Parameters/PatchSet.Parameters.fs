namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open Grace.Types.Types
open System

module PatchSet =

    type PatchSetParameters() =
        inherit CommonParameters()
        member val public OwnerId = String.Empty with get, set
        member val public OwnerName: OwnerName = String.Empty with get, set
        member val public OrganizationId = String.Empty with get, set
        member val public OrganizationName: OrganizationName = String.Empty with get, set
        member val public RepositoryId = String.Empty with get, set
        member val public RepositoryName: RepositoryName = String.Empty with get, set
        member val public PatchSetId = String.Empty with get, set

    type CreatePatchSetParameters() =
        inherit PatchSetParameters()
        member val public BaseDirectoryId: DirectoryVersionId = DirectoryVersionId.Empty with get, set
        member val public HeadDirectoryId: DirectoryVersionId = DirectoryVersionId.Empty with get, set

    type GetPatchSetParameters() =
        inherit PatchSetParameters()
