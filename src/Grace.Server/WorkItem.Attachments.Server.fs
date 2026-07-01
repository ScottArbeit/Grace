namespace Grace.Server

open Grace.Shared.Parameters.WorkItem
open Grace.Types.Artifact
open Grace.Types.Common
open Grace.Types.WorkItem
open System
open System.Collections.Generic

/// Contains Grace Server work item attachments behavior and supporting helpers.
module WorkItemAttachments =
    /// Represents work item attachment used by Grace Server APIs and background services.
    type internal WorkItemAttachment = { ArtifactId: ArtifactId; Metadata: ArtifactMetadata; AttachmentType: string }

    /// Implements equals ignore case for the server request pipeline.
    let private equalsIgnoreCase (expected: string) (actual: string) =
        not (isNull actual)
        && actual.Equals(expected, StringComparison.OrdinalIgnoreCase)

    /// Attempts to classify artifact type and returns an option or result instead of throwing.
    let private tryClassifyArtifactType (artifactType: ArtifactType) =
        if isNull (box artifactType) then
            Some "summary"
        else
            match artifactType with
            | ArtifactType.AgentSummary -> Some "summary"
            | ArtifactType.Prompt -> Some "prompt"
            | ArtifactType.ReviewNotes -> Some "notes"
            | ArtifactType.Other kind when
                equalsIgnoreCase "summary" kind
                || equalsIgnoreCase "agentsummary" kind
                ->
                Some "summary"
            | ArtifactType.Other kind when equalsIgnoreCase "prompt" kind -> Some "prompt"
            | ArtifactType.Other kind when
                equalsIgnoreCase "notes" kind
                || equalsIgnoreCase "reviewnotes" kind
                ->
                Some "notes"
            | _ -> None

    /// Converts an artifact type into the attachment bucket name exposed by work-item link DTOs.
    let internal getAttachmentTypeName (artifactType: ArtifactType) =
        match tryClassifyArtifactType artifactType with
        | Some attachmentType -> attachmentType
        | None -> "other"

    /// Gets try get reviewer attachment type name data needed by the server flow.
    let internal tryGetReviewerAttachmentTypeName (artifactType: ArtifactType) = tryClassifyArtifactType artifactType

    /// Implements select attachment deterministically for the server request pipeline.
    let internal selectAttachmentDeterministically (attachments: WorkItemAttachment list) (latest: bool) =
        if List.isEmpty attachments then
            None
        else
            let ordered =
                attachments
                |> List.sortBy (fun attachment -> attachment.Metadata.CreatedAt, attachment.ArtifactId.ToString("N"))

            if latest then
                ordered |> List.rev |> List.head |> Some
            else
                ordered |> List.head |> Some

    /// Groups a work item's artifact ids into attachment-link buckets using the already-loaded artifact metadata.
    let internal buildLinksDto (workItemDto: WorkItemDto) (artifactMetadataById: IReadOnlyDictionary<ArtifactId, ArtifactMetadata option>) =
        let agentSummaryArtifactIds = ResizeArray<ArtifactId>()
        let promptArtifactIds = ResizeArray<ArtifactId>()
        let reviewNotesArtifactIds = ResizeArray<ArtifactId>()
        let otherArtifactIds = ResizeArray<ArtifactId>()

        workItemDto.ArtifactIds
        |> Seq.iter (fun artifactId ->
            match artifactMetadataById[artifactId] with
            | Some artifactMetadata ->
                match artifactMetadata.ArtifactType with
                | ArtifactType.AgentSummary -> agentSummaryArtifactIds.Add(artifactId)
                | ArtifactType.Prompt -> promptArtifactIds.Add(artifactId)
                | ArtifactType.ReviewNotes -> reviewNotesArtifactIds.Add(artifactId)
                | _ -> otherArtifactIds.Add(artifactId)
            | None -> otherArtifactIds.Add(artifactId))

        {
            WorkItemId = workItemDto.WorkItemId
            WorkItemNumber = workItemDto.WorkItemNumber
            ReferenceIds = workItemDto.ReferenceIds
            PromotionSetIds = workItemDto.PromotionSetIds
            ArtifactIds = workItemDto.ArtifactIds
            AgentSummaryArtifactIds = agentSummaryArtifactIds |> Seq.toList
            PromptArtifactIds = promptArtifactIds |> Seq.toList
            ReviewNotesArtifactIds = reviewNotesArtifactIds |> Seq.toList
            OtherArtifactIds = otherArtifactIds |> Seq.toList
        }

    /// Determines whether text mime type.
    let internal isTextMimeType (mimeType: string) =
        if String.IsNullOrWhiteSpace(mimeType) then
            false
        else
            let normalized = mimeType.Trim().ToLowerInvariant()

            normalized.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("+json", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("+xml", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("application/yaml", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("application/x-yaml", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("application/toml", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("application/javascript", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("application/x-javascript", StringComparison.OrdinalIgnoreCase)

    /// Converts server authentication data into attachment descriptor.
    let internal toAttachmentDescriptor (attachment: WorkItemAttachment) =
        WorkItemAttachmentDescriptor(
            ArtifactId = attachment.ArtifactId.ToString(),
            AttachmentType = attachment.AttachmentType,
            MimeType = attachment.Metadata.MimeType,
            Size = attachment.Metadata.Size,
            CreatedAt = attachment.Metadata.CreatedAt.ToString()
        )
