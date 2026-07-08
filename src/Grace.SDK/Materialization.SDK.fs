namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Parameters.Materialization
open Grace.Types.MaterializationPlan

/// SDK entry point for Materialization Plan endpoints.
type Materialization() =

    /// Requests the server-side Materialization Plan for a selected target.
    static member public Plan(parameters: PlanParameters) =
        postServer<PlanParameters, MaterializationPlan> (parameters |> ensureCorrelationIdIsSet, "materialization/plan")
