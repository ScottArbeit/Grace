namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Parameters.Review
open Grace.Types.Review
open System.Threading.Tasks

/// The Review module provides a set of functions for interacting with reviews in the Grace API.
type Review() =
    /// Gets review notes for a promotion set.
    static member public GetNotes(parameters: GetReviewNotesParameters) =
        postServer<GetReviewNotesParameters, ReviewNotes option> (parameters |> ensureCorrelationIdIsSet, "review/notes")

    /// Records a review checkpoint.
    static member public Checkpoint(parameters: ReviewCheckpointParameters) =
        postServer<ReviewCheckpointParameters, string> (parameters |> ensureCorrelationIdIsSet, "review/checkpoint")

    /// Resolves a review finding.
    static member public ResolveFinding(parameters: ResolveFindingParameters) =
        postServer<ResolveFindingParameters, string> (parameters |> ensureCorrelationIdIsSet, "review/resolve")

    /// Requests deeper analysis for a promotion set.
    static member public Deepen(parameters: DeepenReviewParameters) =
        postServer<DeepenReviewParameters, string> (parameters |> ensureCorrelationIdIsSet, "review/deepen")
