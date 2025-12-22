namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open Grace.Types.Types
open System

module Change =

    type ChangeParameters() =
        inherit CommonParameters()
        member val public OwnerId = String.Empty with get, set
        member val public OwnerName: OwnerName = String.Empty with get, set
        member val public OrganizationId = String.Empty with get, set
        member val public OrganizationName: OrganizationName = String.Empty with get, set
        member val public RepositoryId = String.Empty with get, set
        member val public RepositoryName: RepositoryName = String.Empty with get, set
        member val public ChangeId = String.Empty with get, set

    type CreateChangeParameters() =
        inherit ChangeParameters()
        member val public Title = String.Empty with get, set
        member val public Description = String.Empty with get, set

    type UpdateChangeMetadataParameters() =
        inherit ChangeParameters()
        member val public Title = String.Empty with get, set
        member val public Description = String.Empty with get, set

    type GetChangeParameters() =
        inherit ChangeParameters()

    type ListChangesParameters() =
        inherit ChangeParameters()
        member val public MaxCount = 50 with get, set
