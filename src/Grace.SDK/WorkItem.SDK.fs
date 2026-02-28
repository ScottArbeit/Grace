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

    /// Links an artifact to a work item.
    static member public LinkArtifact(parameters: LinkArtifactParameters) =
        postServer<LinkArtifactParameters, string> (parameters |> ensureCorrelationIdIsSet, "work/link/artifact")

    /// Adds summary content (and optional prompt content) to a work item using the canonical add-summary contract.
    static member public AddSummary(parameters: AddSummaryParameters) =
        postServer<AddSummaryParameters, AddSummaryResult> (parameters |> ensureCorrelationIdIsSet, "work/add-summary")

    /// Links a promotion set to a work item.
    static member public LinkPromotionSet(parameters: LinkPromotionSetParameters) =
        postServer<LinkPromotionSetParameters, string> (parameters |> ensureCorrelationIdIsSet, "work/link/promotion-set")

    /// Gets categorized links for a work item.
    static member public GetLinks(parameters: GetWorkItemLinksParameters) =
        postServer<GetWorkItemLinksParameters, WorkItemLinksDto> (parameters |> ensureCorrelationIdIsSet, "work/links/list")

    /// Lists reviewer attachments for a work item.
    static member public ListAttachments(parameters: ListWorkItemAttachmentsParameters) =
        postServer<ListWorkItemAttachmentsParameters, ListWorkItemAttachmentsResult> (parameters |> ensureCorrelationIdIsSet, "work/attachments/list")

    /// Shows a reviewer attachment for a work item using deterministic selection semantics.
    static member public ShowAttachment(parameters: ShowWorkItemAttachmentParameters) =
        postServer<ShowWorkItemAttachmentParameters, ShowWorkItemAttachmentResult> (parameters |> ensureCorrelationIdIsSet, "work/attachments/show")

    /// Gets attachment download metadata for a linked reviewer attachment.
    static member public DownloadAttachment(parameters: DownloadWorkItemAttachmentParameters) =
        postServer<DownloadWorkItemAttachmentParameters, DownloadWorkItemAttachmentResult> (parameters |> ensureCorrelationIdIsSet, "work/attachments/download")

    /// Removes a reference link from a work item.
    static member public RemoveReferenceLink(parameters: RemoveReferenceLinkParameters) =
        postServer<RemoveReferenceLinkParameters, string> (parameters |> ensureCorrelationIdIsSet, "work/links/remove/reference")

    /// Removes a promotion-set link from a work item.
    static member public RemovePromotionSetLink(parameters: RemovePromotionSetLinkParameters) =
        postServer<RemovePromotionSetLinkParameters, string> (parameters |> ensureCorrelationIdIsSet, "work/links/remove/promotion-set")

    /// Removes an artifact link from a work item.
    static member public RemoveArtifactLink(parameters: RemoveArtifactLinkParameters) =
        postServer<RemoveArtifactLinkParameters, string> (parameters |> ensureCorrelationIdIsSet, "work/links/remove/artifact")

    /// Removes all artifact links of a specific artifact type from a work item.
    static member public RemoveArtifactTypeLinks(parameters: RemoveArtifactTypeLinksParameters) =
        postServer<RemoveArtifactTypeLinksParameters, string> (parameters |> ensureCorrelationIdIsSet, "work/links/remove/artifact-type")
