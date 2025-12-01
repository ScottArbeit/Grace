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
        | BranchEvent of Branch.BranchEvent
        | DirectoryVersionEvent of DirectoryVersion.DirectoryVersionEvent
        | OrganizationEvent of Organization.OrganizationEvent
        | OwnerEvent of Owner.OwnerEvent
        | PromotionGroupEvent of PromotionGroup.PromotionGroupEvent
        | ReferenceEvent of Reference.ReferenceEvent
        | RepositoryEvent of Repository.RepositoryEvent

        static member GetKnownTypes() = GetKnownTypes<GraceEvent>()
