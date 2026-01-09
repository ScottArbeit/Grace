namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Parameters.Queue
open Grace.Types.Queue
open System.Threading.Tasks

/// The Queue module provides a set of functions for interacting with promotion queues in the Grace API.
type Queue() =
    /// Gets the status of a promotion queue for a target branch.
    static member public Status(parameters: QueueStatusParameters) =
        postServer<QueueStatusParameters, PromotionQueue> (parameters |> ensureCorrelationIdIsSet, "queue/status")

    /// Enqueues a candidate in a promotion queue.
    static member public Enqueue(parameters: EnqueueParameters) =
        postServer<EnqueueParameters, string> (parameters |> ensureCorrelationIdIsSet, "queue/enqueue")

    /// Pauses a promotion queue.
    static member public Pause(parameters: QueueActionParameters) =
        postServer<QueueActionParameters, string> (parameters |> ensureCorrelationIdIsSet, "queue/pause")

    /// Resumes a promotion queue.
    static member public Resume(parameters: QueueActionParameters) =
        postServer<QueueActionParameters, string> (parameters |> ensureCorrelationIdIsSet, "queue/resume")

    /// Dequeues a candidate from a promotion queue.
    static member public Dequeue(parameters: CandidateActionParameters) =
        postServer<CandidateActionParameters, string> (parameters |> ensureCorrelationIdIsSet, "queue/dequeue")
