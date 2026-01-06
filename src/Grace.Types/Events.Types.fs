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

module Events =

    /// A discriminated union that holds all of the possible events for Grace. Used for publishing events to graceEventStream.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type GraceEvent =
        | OwnerEvent of Owner.OwnerEvent
        | BranchEvent of Branch.BranchEvent
        | DirectoryVersionEvent of DirectoryVersion.DirectoryVersionEvent
        | OrganizationEvent of Organization.OrganizationEvent
        | PromotionGroupEvent of PromotionGroup.PromotionGroupEvent
        | ReferenceEvent of Reference.ReferenceEvent
        | RepositoryEvent of Repository.RepositoryEvent
        | WorkItemEvent of WorkItem.WorkItemEvent
        | PolicyEvent of Policy.PolicyEvent
        | ReviewEvent of Review.ReviewEvent
        | Stage0Event of Review.Stage0Event
        | CandidateEvent of Queue.CandidateEvent
        | GateAttestationEvent of Queue.GateAttestationEvent
        | ConflictReceiptEvent of Queue.ConflictReceiptEvent
        | QueueEvent of Queue.PromotionQueueEvent

        static member GetKnownTypes() = GetKnownTypes<GraceEvent>()

        override this.ToString() =
            match this with
            | OwnerEvent e -> serialize e
            | BranchEvent e -> serialize e
            | DirectoryVersionEvent e -> serialize e
            | OrganizationEvent e -> serialize e
            | PromotionGroupEvent e -> serialize e
            | ReferenceEvent e -> serialize e
            | RepositoryEvent e -> serialize e
            | WorkItemEvent e -> serialize e
            | PolicyEvent e -> serialize e
            | ReviewEvent e -> serialize e
            | Stage0Event e -> serialize e
            | CandidateEvent e -> serialize e
            | GateAttestationEvent e -> serialize e
            | ConflictReceiptEvent e -> serialize e
            | QueueEvent e -> serialize e
