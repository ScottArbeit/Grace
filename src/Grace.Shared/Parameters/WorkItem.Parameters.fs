namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open System
open System.Collections.Generic

module WorkItem =
    /// Base parameters for WorkItem endpoints.
    type WorkItemParameters() =
        inherit CommonParameters()
        member val public OwnerId = String.Empty with get, set
        member val public OwnerName = String.Empty with get, set
        member val public OrganizationId = String.Empty with get, set
        member val public OrganizationName = String.Empty with get, set
        member val public RepositoryId = String.Empty with get, set
        member val public RepositoryName = String.Empty with get, set
        member val public WorkItemId = String.Empty with get, set

    /// Parameters for /work/create.
    type CreateWorkItemParameters() =
        inherit WorkItemParameters()
        member val public Title = String.Empty with get, set
        member val public Description = String.Empty with get, set

    /// Parameters for /work/{id}.
    type GetWorkItemParameters() =
        inherit WorkItemParameters()

    /// Parameters for /work/{id}/update.
    type UpdateWorkItemParameters() =
        inherit WorkItemParameters()
        member val public Title = String.Empty with get, set
        member val public Description = String.Empty with get, set
        member val public Status = String.Empty with get, set
        member val public Constraints = String.Empty with get, set
        member val public Notes = String.Empty with get, set
        member val public ArchitecturalNotes = String.Empty with get, set
        member val public MigrationNotes = String.Empty with get, set

    /// Parameters for /work/{id}/link/reference.
    type LinkReferenceParameters() =
        inherit WorkItemParameters()
        member val public ReferenceId = String.Empty with get, set

    /// Parameters for /work/{id}/link/artifact.
    type LinkArtifactParameters() =
        inherit WorkItemParameters()
        member val public ArtifactId = String.Empty with get, set

    /// Parameters for /work/{id}/link/promotion-set.
    type LinkPromotionSetParameters() =
        inherit WorkItemParameters()
        member val public PromotionSetId = String.Empty with get, set

    /// Parameters for /work/links/list.
    type GetWorkItemLinksParameters() =
        inherit WorkItemParameters()

    /// Parameters for /work/attachments/list.
    type ListWorkItemAttachmentsParameters() =
        inherit WorkItemParameters()

    /// Parameters for /work/attachments/show.
    type ShowWorkItemAttachmentParameters() =
        inherit WorkItemParameters()
        member val public AttachmentType = String.Empty with get, set
        member val public Latest = false with get, set

    /// Parameters for /work/attachments/download.
    type DownloadWorkItemAttachmentParameters() =
        inherit WorkItemParameters()
        member val public ArtifactId = String.Empty with get, set

    /// Parameters for /work/links/remove/reference.
    type RemoveReferenceLinkParameters() =
        inherit WorkItemParameters()
        member val public ReferenceId = String.Empty with get, set

    /// Parameters for /work/links/remove/promotion-set.
    type RemovePromotionSetLinkParameters() =
        inherit WorkItemParameters()
        member val public PromotionSetId = String.Empty with get, set

    /// Parameters for /work/links/remove/artifact.
    type RemoveArtifactLinkParameters() =
        inherit WorkItemParameters()
        member val public ArtifactId = String.Empty with get, set

    /// Parameters for /work/links/remove/artifact-type.
    type RemoveArtifactTypeLinksParameters() =
        inherit WorkItemParameters()
        member val public ArtifactType = String.Empty with get, set

    /// Parameters for /work/add-summary.
    type AddSummaryParameters() =
        inherit WorkItemParameters()
        member val public SummaryContent = String.Empty with get, set
        member val public SummaryMimeType = String.Empty with get, set
        member val public PromptContent = String.Empty with get, set
        member val public PromptMimeType = String.Empty with get, set
        member val public PromptOrigin = String.Empty with get, set
        member val public PromotionSetId = String.Empty with get, set
        member val public SummaryArtifactId = String.Empty with get, set
        member val public PromptArtifactId = String.Empty with get, set

    /// Result for /work/add-summary.
    type AddSummaryResult() =
        member val public WorkItemId = String.Empty with get, set
        member val public SummaryArtifactId = String.Empty with get, set
        member val public PromptArtifactId = String.Empty with get, set
        member val public PromotionSetId = String.Empty with get, set

    /// Attachment metadata for reviewer attachment retrieval endpoints.
    type WorkItemAttachmentDescriptor() =
        member val public ArtifactId = String.Empty with get, set
        member val public AttachmentType = String.Empty with get, set
        member val public MimeType = String.Empty with get, set
        member val public Size = 0L with get, set
        member val public CreatedAt = String.Empty with get, set

    /// Result for /work/attachments/list.
    type ListWorkItemAttachmentsResult() =
        member val public WorkItemId = String.Empty with get, set
        member val public WorkItemNumber = 0L with get, set
        member val public Attachments = List<WorkItemAttachmentDescriptor>() with get, set

    /// Result for /work/attachments/show.
    type ShowWorkItemAttachmentResult() =
        member val public WorkItemId = String.Empty with get, set
        member val public WorkItemNumber = 0L with get, set
        member val public AttachmentType = String.Empty with get, set
        member val public ArtifactId = String.Empty with get, set
        member val public MimeType = String.Empty with get, set
        member val public Size = 0L with get, set
        member val public CreatedAt = String.Empty with get, set
        member val public IsTextContent = false with get, set
        member val public Content = String.Empty with get, set
        member val public AvailableAttachmentCount = 0 with get, set
        member val public SelectedUsingLatest = false with get, set

    /// Result for /work/attachments/download.
    type DownloadWorkItemAttachmentResult() =
        member val public WorkItemId = String.Empty with get, set
        member val public WorkItemNumber = 0L with get, set
        member val public AttachmentType = String.Empty with get, set
        member val public ArtifactId = String.Empty with get, set
        member val public MimeType = String.Empty with get, set
        member val public Size = 0L with get, set
        member val public CreatedAt = String.Empty with get, set
        member val public DownloadUri = String.Empty with get, set
