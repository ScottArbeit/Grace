namespace Grace.Actors

open Grace.Types.ManifestContributionAccounting
open System.Threading
open System.Threading.Tasks

/// Provides provider-neutral access to current exact relationships for actor workflows.
type IExactRelationshipStore =

    /// Ensures one current relationship exists without appending operation history.
    abstract member EnsurePresentAsync: relationship: ExactRelationship * cancellationToken: CancellationToken -> Task<ExactRelationshipWriteOutcome>

    /// Ensures one current relationship is absent without appending operation history.
    abstract member EnsureAbsentAsync: relationship: ExactRelationship * cancellationToken: CancellationToken -> Task<ExactRelationshipWriteOutcome>

    /// Enumerates one explicitly bounded relationship partition.
    abstract member EnumerateAsync:
        partition: ExactRelationshipPartition * bound: ExactRelationshipReadBound * continuationToken: string option * cancellationToken: CancellationToken ->
            Task<ExactRelationshipPage>

    /// Re-reads one relationship directly so callers never infer absence from a cache or timeout.
    abstract member VerifyAsync: relationship: ExactRelationship * cancellationToken: CancellationToken -> Task<ExactRelationshipPresence>
