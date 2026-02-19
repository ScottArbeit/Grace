namespace Grace.Types

open Grace.Types
open Grace.Types.Types
open Grace.Shared.Utilities
open NodaTime
open Orleans
open System.Runtime.Serialization
open Grace.Types.WorkItem
open Grace.Types.Policy
open Grace.Types.Review
open Grace.Types.Queue
open Grace.Types.PromotionSet
open Grace.Types.Validation
open Grace.Types.Artifact

module Events =

    /// A discriminated union that holds all of the possible events for Grace. Used for publishing events to graceEventStream.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type GraceEvent =
        | OwnerEvent of Owner.OwnerEvent
        | BranchEvent of Branch.BranchEvent
        | DirectoryVersionEvent of DirectoryVersion.DirectoryVersionEvent
        | OrganizationEvent of Organization.OrganizationEvent
        | ReferenceEvent of Reference.ReferenceEvent
        | RepositoryEvent of Repository.RepositoryEvent
        | WorkItemEvent of WorkItem.WorkItemEvent
        | PolicyEvent of Policy.PolicyEvent
        | ReviewEvent of Review.ReviewEvent
        | QueueEvent of Queue.PromotionQueueEvent
        | PromotionSetEvent of PromotionSet.PromotionSetEvent
        | ValidationSetEvent of Validation.ValidationSetEvent
        | ValidationResultEvent of Validation.ValidationResultEvent
        | ArtifactEvent of Artifact.ArtifactEvent

        static member GetKnownTypes() = GetKnownTypes<GraceEvent>()

        override this.ToString() =
            match this with
            | OwnerEvent e -> serialize e
            | BranchEvent e -> serialize e
            | DirectoryVersionEvent e -> serialize e
            | OrganizationEvent e -> serialize e
            | ReferenceEvent e -> serialize e
            | RepositoryEvent e -> serialize e
            | WorkItemEvent e -> serialize e
            | PolicyEvent e -> serialize e
            | ReviewEvent e -> serialize e
            | QueueEvent e -> serialize e
            | PromotionSetEvent e -> serialize e
            | ValidationSetEvent e -> serialize e
            | ValidationResultEvent e -> serialize e
            | ArtifactEvent e -> serialize e
