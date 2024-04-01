namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open Grace.Shared.Types
open System

module Reference =

    type ReferenceParameters() =
        inherit CommonParameters()
        member val public ReferenceId: string = String.Empty with get, set
        member val public ReferenceType: string = String.Empty with get, set
        member val public RepositoryText: string = String.Empty with get, set
        member val public RepositoryName: string = String.Empty with get, set
