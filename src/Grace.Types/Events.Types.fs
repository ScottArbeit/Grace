namespace Grace.Types

open Grace.Types
open Grace.Types.Types
open Grace.Shared.Utilities
open NodaTime
open Orleans
open System.Runtime.Serialization

module Events =

    /// A discriminated union that holds all of the possible events for Grace. Used for publishing events to graceEventStream.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type GraceEvent =
        | ChangeEvent of Change.ChangeEvent
        | PatchSetEvent of PatchSet.PatchSetEvent
        | StackEvent of Stack.StackEvent
        | OperationEvent of Operation.OperationEvent
        | TrainEvent of Train.TrainEvent
        | OwnerEvent of Owner.OwnerEvent
        | BranchEvent of Branch.BranchEvent
        | DirectoryVersionEvent of DirectoryVersion.DirectoryVersionEvent
        | OrganizationEvent of Organization.OrganizationEvent
        | PromotionGroupEvent of PromotionGroup.PromotionGroupEvent
        | ReferenceEvent of Reference.ReferenceEvent
        | RepositoryEvent of Repository.RepositoryEvent

        static member GetKnownTypes() = GetKnownTypes<GraceEvent>()

        override this.ToString() =
            match this with
            | ChangeEvent e -> serialize e
            | PatchSetEvent e -> serialize e
            | StackEvent e -> serialize e
            | OperationEvent e -> serialize e
            | TrainEvent e -> serialize e
            | OwnerEvent e -> serialize e
            | BranchEvent e -> serialize e
            | DirectoryVersionEvent e -> serialize e
            | OrganizationEvent e -> serialize e
            | PromotionGroupEvent e -> serialize e
            | ReferenceEvent e -> serialize e
            | RepositoryEvent e -> serialize e
