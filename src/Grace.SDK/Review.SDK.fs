namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Parameters.Review
open Grace.Types.Review
open System.Threading.Tasks

/// The Review module provides a set of functions for interacting with reviews in the Grace API.
type Review() =
    /// Gets a review packet for a candidate.
    static member public GetPacket(parameters: GetReviewPacketParameters) =
        postServer<GetReviewPacketParameters, ReviewPacket option> (parameters |> ensureCorrelationIdIsSet, "review/packet")

    /// Records a review checkpoint.
    static member public Checkpoint(parameters: ReviewCheckpointParameters) =
        postServer<ReviewCheckpointParameters, string> (parameters |> ensureCorrelationIdIsSet, "review/checkpoint")

    /// Resolves a review finding.
    static member public ResolveFinding(parameters: ResolveFindingParameters) =
        postServer<ResolveFindingParameters, string> (parameters |> ensureCorrelationIdIsSet, "review/resolve")

    /// Requests deeper analysis for a candidate.
    static member public Deepen(parameters: DeepenReviewParameters) =
        postServer<DeepenReviewParameters, string> (parameters |> ensureCorrelationIdIsSet, "review/deepen")
