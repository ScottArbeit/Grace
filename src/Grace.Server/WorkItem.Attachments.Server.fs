namespace Grace.Server

open Grace.Shared.Parameters.WorkItem
open Grace.Types.Artifact
open Grace.Types.Types
open System

module WorkItemAttachments =
    type internal WorkItemAttachment = { ArtifactId: ArtifactId; Metadata: ArtifactMetadata; AttachmentType: string }

    let private equalsIgnoreCase (expected: string) (actual: string) =
        not (isNull actual)
        && actual.Equals(expected, StringComparison.OrdinalIgnoreCase)

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

    let internal getAttachmentTypeName (artifactType: ArtifactType) =
        match tryClassifyArtifactType artifactType with
        | Some attachmentType -> attachmentType
        | None -> "other"

    let internal tryGetReviewerAttachmentTypeName (artifactType: ArtifactType) = tryClassifyArtifactType artifactType

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

    let internal toAttachmentDescriptor (attachment: WorkItemAttachment) =
        WorkItemAttachmentDescriptor(
            ArtifactId = attachment.ArtifactId.ToString(),
            AttachmentType = attachment.AttachmentType,
            MimeType = attachment.Metadata.MimeType,
            Size = attachment.Metadata.Size,
            CreatedAt = attachment.Metadata.CreatedAt.ToString()
        )
