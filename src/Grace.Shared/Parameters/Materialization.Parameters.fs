namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open Grace.Types.MaterializationPlan
open System

/// Contains Materialization Plan parameter contracts shared by server routes and SDK clients.
module Materialization =

    /// Parameters for the /materialization/plan endpoint.
    type PlanParameters() =
        inherit CommonParameters()
        member val public OwnerId = String.Empty with get, set
        member val public OwnerName = String.Empty with get, set
        member val public OrganizationId = String.Empty with get, set
        member val public OrganizationName = String.Empty with get, set
        member val public RepositoryId = String.Empty with get, set
        member val public RepositoryName = String.Empty with get, set
        member val public Request: MaterializationPlanRequest = Unchecked.defaultof<MaterializationPlanRequest> with get, set
