namespace Grace.Server.Tests

open Grace.Server
open Grace.Types.Artifact
open Grace.Types.Types
open NodaTime
open NUnit.Framework
open System

[<Parallelizable(ParallelScope.All)>]
type WorkItemAttachmentUnitTests() =
    let artifactId = Guid.Parse("7d535f96-e634-4313-b5ff-d9293ee9db57")

    let metadata artifactType mimeType size createdAt =
        { ArtifactMetadata.Default with ArtifactId = artifactId; ArtifactType = artifactType; MimeType = mimeType; Size = size; CreatedAt = createdAt }

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
            { ArtifactId = artifactId; Metadata = metadata ArtifactType.Prompt "application/json" 4096L createdAt; AttachmentType = "prompt" }

        let descriptor = WorkItemAttachments.toAttachmentDescriptor attachment

        Assert.That(descriptor.ArtifactId, Is.EqualTo(artifactId.ToString()))
        Assert.That(descriptor.AttachmentType, Is.EqualTo("prompt"))
        Assert.That(descriptor.MimeType, Is.EqualTo("application/json"))
        Assert.That(descriptor.Size, Is.EqualTo(4096L))
        Assert.That(descriptor.CreatedAt, Is.EqualTo(createdAt.ToString()))
