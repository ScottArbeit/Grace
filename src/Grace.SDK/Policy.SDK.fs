namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Parameters.Policy
open Grace.Types.Policy
open System.Threading.Tasks

/// SDK entry point for reading and acknowledging branch policy snapshots.
type Policy() =
    /// Gets the current policy snapshot for a target branch.
    static member public GetCurrent(parameters: GetPolicyParameters) =
        postServer<GetPolicyParameters, PolicySnapshot option> (parameters |> ensureCorrelationIdIsSet, "policy/current")

    /// Acknowledges a policy snapshot.
    static member public Acknowledge(parameters: AcknowledgePolicyParameters) =
        postServer<AcknowledgePolicyParameters, string> (parameters |> ensureCorrelationIdIsSet, "policy/acknowledge")
