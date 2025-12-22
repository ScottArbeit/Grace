namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open Grace.Types.Types
open System

module Operation =

    type OperationParameters() =
        inherit CommonParameters()
        member val public OwnerId = String.Empty with get, set
        member val public OwnerName: OwnerName = String.Empty with get, set
        member val public OrganizationId = String.Empty with get, set
        member val public OrganizationName: OrganizationName = String.Empty with get, set
        member val public RepositoryId = String.Empty with get, set
        member val public RepositoryName: RepositoryName = String.Empty with get, set
        member val public OperationId = String.Empty with get, set

    type GetOperationParameters() =
        inherit OperationParameters()

    type CancelOperationParameters() =
        inherit OperationParameters()

    type ApproveConflictParameters() =
        inherit OperationParameters()
        member val public Accepted = false with get, set
        member val public Resolution = String.Empty with get, set
