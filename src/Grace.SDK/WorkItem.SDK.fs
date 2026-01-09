namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Parameters.WorkItem
open Grace.Types.WorkItem
open System.Threading.Tasks

/// The WorkItem module provides a set of functions for interacting with work items in the Grace API.
type WorkItem() =
    /// Creates a new work item.
    static member public Create(parameters: CreateWorkItemParameters) =
        postServer<CreateWorkItemParameters, string> (parameters |> ensureCorrelationIdIsSet, "work/create")

    /// Gets a work item by ID.
    static member public Get(parameters: GetWorkItemParameters) =
        postServer<GetWorkItemParameters, WorkItemDto> (parameters |> ensureCorrelationIdIsSet, "work/get")

    /// Updates a work item.
    static member public Update(parameters: UpdateWorkItemParameters) =
        postServer<UpdateWorkItemParameters, string> (parameters |> ensureCorrelationIdIsSet, "work/update")

    /// Links a reference to a work item.
    static member public LinkReference(parameters: LinkReferenceParameters) =
        postServer<LinkReferenceParameters, string> (parameters |> ensureCorrelationIdIsSet, "work/link/reference")

    /// Links a promotion group to a work item.
    static member public LinkPromotionGroup(parameters: LinkPromotionGroupParameters) =
        postServer<LinkPromotionGroupParameters, string> (parameters |> ensureCorrelationIdIsSet, "work/link/promotion-group")
