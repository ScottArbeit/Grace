namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Parameters.Queue
open Grace.Types.Queue
open Grace.Types.RequiredAction
open System.Threading.Tasks

/// The Candidate module provides a set of functions for interacting with integration candidates in the Grace API.
type Candidate() =
    /// Gets an integration candidate by ID.
    static member public Get(parameters: CandidateParameters) =
        postServer<CandidateParameters, IntegrationCandidate> (parameters |> ensureCorrelationIdIsSet, "candidate/get")

    /// Cancels a candidate in the promotion queue.
    static member public Cancel(parameters: CandidateActionParameters) =
        postServer<CandidateActionParameters, string> (parameters |> ensureCorrelationIdIsSet, "candidate/cancel")

    /// Retries a candidate by re-enqueueing it.
    static member public Retry(parameters: CandidateActionParameters) =
        postServer<CandidateActionParameters, string> (parameters |> ensureCorrelationIdIsSet, "candidate/retry")

    /// Gets required actions for a candidate.
    static member public RequiredActions(parameters: CandidateParameters) =
        postServer<CandidateParameters, RequiredActionDto list> (parameters |> ensureCorrelationIdIsSet, "candidate/required-actions")

    /// Gets gate attestations for a candidate.
    static member public Attestations(parameters: CandidateAttestationsParameters) =
        postServer<CandidateAttestationsParameters, GateAttestation list> (parameters |> ensureCorrelationIdIsSet, "candidate/attestations")

    /// Reruns a gate for a candidate.
    static member public RerunGate(parameters: CandidateGateRerunParameters) =
        postServer<CandidateGateRerunParameters, GateAttestation> (parameters |> ensureCorrelationIdIsSet, "candidate/gate/rerun")
