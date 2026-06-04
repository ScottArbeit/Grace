namespace Grace.Server.Tests

open Grace.Server
open Grace.Types.Artifact
open Grace.Types.Common
open Grace.Types.WorkItem
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic

[<Parallelizable(ParallelScope.All)>]
type WorkItemAttachmentUnitTests() =
    let artifactId (value: string) = Guid.Parse(value)

    let defaultArtifactId = artifactId "7d535f96-e634-4313-b5ff-d9293ee9db57"

    let metadata artifactId artifactType mimeType size createdAt =
        { ArtifactMetadata.Default with ArtifactId = artifactId; ArtifactType = artifactType; MimeType = mimeType; Size = size; CreatedAt = createdAt }

    let attachment artifactId artifactType createdAt =
        let metadata = metadata artifactId artifactType "text/markdown" 1024L createdAt

        let attachment: WorkItemAttachments.WorkItemAttachment =
            { ArtifactId = artifactId; Metadata = metadata; AttachmentType = WorkItemAttachments.getAttachmentTypeName artifactType }

        attachment

    [<Test>]
    member _.ReviewerAttachmentClassificationMapsSupportedArtifactTypesAndAliases() =
        let cases =
            [
                Unchecked.defaultof<ArtifactType>, "summary"
                ArtifactType.AgentSummary, "summary"
                ArtifactType.Prompt, "prompt"
                ArtifactType.ReviewNotes, "notes"
                ArtifactType.Other "summary", "summary"
                ArtifactType.Other "agentsummary", "summary"
                ArtifactType.Other "prompt", "prompt"
                ArtifactType.Other "notes", "notes"
                ArtifactType.Other "reviewnotes", "notes"
                ArtifactType.Other "AgEnTsUmMaRy", "summary"
                ArtifactType.Other "ReViEwNoTeS", "notes"
            ]

        for artifactType, expectedAttachmentType in cases do
            Assert.That(
                WorkItemAttachments.tryGetReviewerAttachmentTypeName artifactType,
                Is.EqualTo(Some expectedAttachmentType),
                $"Expected {artifactType} to classify as {expectedAttachmentType}."
            )

            Assert.That(
                WorkItemAttachments.getAttachmentTypeName artifactType,
                Is.EqualTo(expectedAttachmentType),
                $"Expected {artifactType} display type to be {expectedAttachmentType}."
            )

    [<Test>]
    member _.ReviewerAttachmentClassificationExcludesUnsupportedOtherArtifactTypes() =
        let unsupportedTypes =
            [
                ArtifactType.Other "unknown"
                ArtifactType.Other "summary-report"
                ArtifactType.Other "binary"
                ArtifactType.ConflictReport
                ArtifactType.ValidationOutput
            ]

        for artifactType in unsupportedTypes do
            Assert.That(
                WorkItemAttachments.tryGetReviewerAttachmentTypeName artifactType,
                Is.EqualTo(None),
                $"Expected {artifactType} to be excluded from reviewer attachments."
            )

            Assert.That(
                WorkItemAttachments.getAttachmentTypeName artifactType,
                Is.EqualTo("other"),
                $"Expected {artifactType} display type to fall back to other."
            )

    [<Test>]
    member _.TextMimeDetectionAcceptsTextJsonXmlYamlTomlAndJavascript() =
        let textMimeTypes =
            [
                "text/plain"
                " text/markdown "
                "TEXT/HTML"
                "application/json"
                "application/problem+json"
                "application/xml"
                "application/atom+xml"
                "application/yaml"
                "application/x-yaml"
                "application/toml"
                "application/javascript"
                "application/x-javascript"
            ]

        for mimeType in textMimeTypes do
            Assert.That(WorkItemAttachments.isTextMimeType mimeType, Is.True, $"Expected '{mimeType}' to be text-readable.")

    [<Test>]
    member _.TextMimeDetectionRejectsBinaryAndBlankMimeValues() =
        let binaryMimeTypes =
            [
                null
                String.Empty
                "   "
                "application/octet-stream"
                "image/png"
                "application/pdf"
                "application/zip"
                "application/x-protobuf"
            ]

        for mimeType in binaryMimeTypes do
            Assert.That(WorkItemAttachments.isTextMimeType mimeType, Is.False, $"Expected '{mimeType}' to be non-text.")

    [<Test>]
    member _.AttachmentDescriptorPreservesIdentityTypeMimeSizeAndTimestamp() =
        let createdAt = Instant.FromUtc(2026, 6, 3, 12, 34, 56)

        let attachment: WorkItemAttachments.WorkItemAttachment =
            {
                ArtifactId = defaultArtifactId
                Metadata = metadata defaultArtifactId ArtifactType.Prompt "application/json" 4096L createdAt
                AttachmentType = "prompt"
            }

        let descriptor = WorkItemAttachments.toAttachmentDescriptor attachment

        Assert.That(descriptor.ArtifactId, Is.EqualTo(defaultArtifactId.ToString()))
        Assert.That(descriptor.AttachmentType, Is.EqualTo("prompt"))
        Assert.That(descriptor.MimeType, Is.EqualTo("application/json"))
        Assert.That(descriptor.Size, Is.EqualTo(4096L))
        Assert.That(descriptor.CreatedAt, Is.EqualTo(createdAt.ToString()))

    [<Test>]
    member _.AttachmentSelectionReturnsNoneWhenNoAttachmentsExist() =
        let selected = WorkItemAttachments.selectAttachmentDeterministically [] false

        Assert.That(selected, Is.EqualTo(None))

    [<Test>]
    member _.AttachmentSelectionReturnsOldestAndLatestByTimestamp() =
        let olderId = artifactId "10000000-0000-0000-0000-000000000001"
        let newerId = artifactId "10000000-0000-0000-0000-000000000002"
        let older = attachment olderId ArtifactType.AgentSummary (Instant.FromUtc(2026, 6, 3, 8, 0))
        let newer = attachment newerId ArtifactType.AgentSummary (Instant.FromUtc(2026, 6, 3, 9, 0))
        let attachments = [ newer; older ]

        let oldest = WorkItemAttachments.selectAttachmentDeterministically attachments false
        let latest = WorkItemAttachments.selectAttachmentDeterministically attachments true

        Assert.That(
            oldest
            |> Option.map (fun selected -> selected.ArtifactId),
            Is.EqualTo(Some olderId)
        )

        Assert.That(
            latest
            |> Option.map (fun selected -> selected.ArtifactId),
            Is.EqualTo(Some newerId)
        )

    [<Test>]
    member _.AttachmentSelectionBreaksTimestampTiesByArtifactId() =
        let lowerId = artifactId "10000000-0000-0000-0000-000000000001"
        let upperId = artifactId "10000000-0000-0000-0000-000000000002"
        let sameTimestamp = Instant.FromUtc(2026, 6, 3, 8, 0)
        let lower = attachment lowerId ArtifactType.ReviewNotes sameTimestamp
        let upper = attachment upperId ArtifactType.ReviewNotes sameTimestamp
        let attachments = [ upper; lower ]

        let oldest = WorkItemAttachments.selectAttachmentDeterministically attachments false
        let latest = WorkItemAttachments.selectAttachmentDeterministically attachments true

        Assert.That(
            oldest
            |> Option.map (fun selected -> selected.ArtifactId),
            Is.EqualTo(Some lowerId)
        )

        Assert.That(
            latest
            |> Option.map (fun selected -> selected.ArtifactId),
            Is.EqualTo(Some upperId)
        )

    [<Test>]
    member _.BuildLinksDtoGroupsKnownUnknownAndMissingMetadataWhilePreservingArtifactIds() =
        let summaryId = artifactId "10000000-0000-0000-0000-000000000001"
        let promptId = artifactId "10000000-0000-0000-0000-000000000002"
        let notesId = artifactId "10000000-0000-0000-0000-000000000003"
        let unknownId = artifactId "10000000-0000-0000-0000-000000000004"
        let missingMetadataId = artifactId "10000000-0000-0000-0000-000000000005"

        let artifactIds =
            [
                unknownId
                notesId
                missingMetadataId
                summaryId
                promptId
            ]

        let workItemDto =
            { WorkItemDto.Default with WorkItemId = artifactId "20000000-0000-0000-0000-000000000001"; WorkItemNumber = 194L; ArtifactIds = artifactIds }

        let metadataById =
            Dictionary<ArtifactId, ArtifactMetadata option>(
                [
                    KeyValuePair(summaryId, Some(metadata summaryId ArtifactType.AgentSummary "text/markdown" 10L Instant.MinValue))
                    KeyValuePair(promptId, Some(metadata promptId ArtifactType.Prompt "text/markdown" 11L Instant.MinValue))
                    KeyValuePair(notesId, Some(metadata notesId ArtifactType.ReviewNotes "text/markdown" 12L Instant.MinValue))
                    KeyValuePair(unknownId, Some(metadata unknownId (ArtifactType.Other "mystery") "application/octet-stream" 13L Instant.MinValue))
                    KeyValuePair(missingMetadataId, None)
                ]
            )

        let links = WorkItemAttachments.buildLinksDto workItemDto metadataById

        Assert.That(links.ArtifactIds = artifactIds, Is.True)
        Assert.That(links.AgentSummaryArtifactIds = [ summaryId ], Is.True)
        Assert.That(links.PromptArtifactIds = [ promptId ], Is.True)
        Assert.That(links.ReviewNotesArtifactIds = [ notesId ], Is.True)
        Assert.That(links.OtherArtifactIds = [ unknownId; missingMetadataId ], Is.True)
