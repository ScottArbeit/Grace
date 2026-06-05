namespace Grace.Types

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Common
open NodaTime
open Orleans
open System
open System.Runtime.Serialization

module Artifact =

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ArtifactType =
        | AgentSummary
        | ConflictReport
        | Prompt
        | ValidationOutput
        | ReviewNotes
        | Other of kind: string

        static member GetKnownTypes() = GetKnownTypes<ArtifactType>()

    [<GenerateSerializer>]
    type ArtifactMetadata =
        {
            [<Id(0u)>]
            Class: string
            [<Id(1u)>]
            ArtifactId: ArtifactId
            [<Id(2u)>]
            OwnerId: OwnerId
            [<Id(3u)>]
            OrganizationId: OrganizationId
            [<Id(4u)>]
            RepositoryId: RepositoryId
            [<Id(5u)>]
            ArtifactType: ArtifactType
            [<Id(6u)>]
            MimeType: string
            [<Id(7u)>]
            Size: int64
            [<Id(8u)>]
            Sha256: Sha256Hash option
            [<Id(9u)>]
            BlobPath: string
            [<Id(10u)>]
            CreatedAt: Instant
            [<Id(11u)>]
            CreatedBy: UserId
        }

        static member Default =
            {
                Class = nameof ArtifactMetadata
                ArtifactId = ArtifactId.Empty
                OwnerId = OwnerId.Empty
                OrganizationId = OrganizationId.Empty
                RepositoryId = RepositoryId.Empty
                ArtifactType = ArtifactType.Other String.Empty
                MimeType = String.Empty
                Size = 0L
                Sha256 = None
                BlobPath = String.Empty
                CreatedAt = Constants.DefaultTimestamp
                CreatedBy = UserId String.Empty
            }

    [<GenerateSerializer>]
    type ArtifactCreateResult = { ArtifactId: ArtifactId; UploadUri: UriWithSharedAccessSignature; BlobPath: string }

    [<GenerateSerializer>]
    type ArtifactDownloadUriResult = { ArtifactId: ArtifactId; DownloadUri: UriWithSharedAccessSignature }

    [<CLIMutable; GenerateSerializer>]
    type ArtifactCreated =
        {
            [<Id(0u)>]
            ArtifactId: ArtifactId
            [<Id(1u)>]
            OwnerId: OwnerId
            [<Id(2u)>]
            OrganizationId: OrganizationId
            [<Id(3u)>]
            RepositoryId: RepositoryId
            [<Id(4u)>]
            ArtifactType: string
            [<Id(5u)>]
            OtherArtifactType: string
            [<Id(6u)>]
            MimeType: string
            [<Id(7u)>]
            Size: int64
            [<Id(8u)>]
            Sha256: string
            [<Id(9u)>]
            BlobPath: string
            [<Id(10u)>]
            CreatedAtUnixTimeTicks: int64
            [<Id(11u)>]
            CreatedBy: string
        }

        static member FromMetadata(artifact: ArtifactMetadata) =
            let artifactType, otherArtifactType =
                match artifact.ArtifactType with
                | ArtifactType.AgentSummary -> "AgentSummary", String.Empty
                | ArtifactType.ConflictReport -> "ConflictReport", String.Empty
                | ArtifactType.Prompt -> "Prompt", String.Empty
                | ArtifactType.ValidationOutput -> "ValidationOutput", String.Empty
                | ArtifactType.ReviewNotes -> "ReviewNotes", String.Empty
                | ArtifactType.Other kind -> "Other", kind

            {
                ArtifactId = artifact.ArtifactId
                OwnerId = artifact.OwnerId
                OrganizationId = artifact.OrganizationId
                RepositoryId = artifact.RepositoryId
                ArtifactType = artifactType
                OtherArtifactType = otherArtifactType
                MimeType = artifact.MimeType
                Size = artifact.Size
                Sha256 =
                    artifact.Sha256
                    |> Option.map string
                    |> Option.defaultValue String.Empty
                BlobPath = artifact.BlobPath
                CreatedAtUnixTimeTicks = artifact.CreatedAt.ToUnixTimeTicks()
                CreatedBy = artifact.CreatedBy
            }

        member this.ToMetadata() =
            let artifactType =
                match this.ArtifactType with
                | value when String.Equals(value, "AgentSummary", StringComparison.OrdinalIgnoreCase) -> ArtifactType.AgentSummary
                | value when String.Equals(value, "ConflictReport", StringComparison.OrdinalIgnoreCase) -> ArtifactType.ConflictReport
                | value when String.Equals(value, "Prompt", StringComparison.OrdinalIgnoreCase) -> ArtifactType.Prompt
                | value when String.Equals(value, "ValidationOutput", StringComparison.OrdinalIgnoreCase) -> ArtifactType.ValidationOutput
                | value when String.Equals(value, "ReviewNotes", StringComparison.OrdinalIgnoreCase) -> ArtifactType.ReviewNotes
                | value when String.Equals(value, "Other", StringComparison.OrdinalIgnoreCase) -> ArtifactType.Other this.OtherArtifactType
                | value -> ArtifactType.Other value

            { ArtifactMetadata.Default with
                ArtifactId = this.ArtifactId
                OwnerId = this.OwnerId
                OrganizationId = this.OrganizationId
                RepositoryId = this.RepositoryId
                ArtifactType = artifactType
                MimeType = this.MimeType
                Size = this.Size
                Sha256 =
                    if String.IsNullOrWhiteSpace this.Sha256 then
                        None
                    else
                        Some(Sha256Hash this.Sha256)
                BlobPath = this.BlobPath
                CreatedAt = Instant.FromUnixTimeTicks this.CreatedAtUnixTimeTicks
                CreatedBy = UserId this.CreatedBy
            }

    [<CLIMutable; GenerateSerializer>]
    type ArtifactCommand =
        {
            [<Id(0u)>]
            Command: string
            [<Id(1u)>]
            ArtifactId: ArtifactId
            [<Id(2u)>]
            OwnerId: OwnerId
            [<Id(3u)>]
            OrganizationId: OrganizationId
            [<Id(4u)>]
            RepositoryId: RepositoryId
            [<Id(5u)>]
            ArtifactType: string
            [<Id(6u)>]
            OtherArtifactType: string
            [<Id(7u)>]
            MimeType: string
            [<Id(8u)>]
            Size: int64
            [<Id(9u)>]
            Sha256: string
            [<Id(10u)>]
            BlobPath: string
            [<Id(11u)>]
            CreatedAtUnixTimeTicks: int64
            [<Id(12u)>]
            CreatedBy: string
        }

        static member Create(artifact: ArtifactCreated) =
            {
                Command = "Create"
                ArtifactId = artifact.ArtifactId
                OwnerId = artifact.OwnerId
                OrganizationId = artifact.OrganizationId
                RepositoryId = artifact.RepositoryId
                ArtifactType = artifact.ArtifactType
                OtherArtifactType = artifact.OtherArtifactType
                MimeType = artifact.MimeType
                Size = artifact.Size
                Sha256 = artifact.Sha256
                BlobPath = artifact.BlobPath
                CreatedAtUnixTimeTicks = artifact.CreatedAtUnixTimeTicks
                CreatedBy = artifact.CreatedBy
            }

        member this.ToCreated() =
            {
                ArtifactId = this.ArtifactId
                OwnerId = this.OwnerId
                OrganizationId = this.OrganizationId
                RepositoryId = this.RepositoryId
                ArtifactType = this.ArtifactType
                OtherArtifactType = this.OtherArtifactType
                MimeType = this.MimeType
                Size = this.Size
                Sha256 = this.Sha256
                BlobPath = this.BlobPath
                CreatedAtUnixTimeTicks = this.CreatedAtUnixTimeTicks
                CreatedBy = this.CreatedBy
            }

    module ArtifactCommandNames =
        let Create = "Create"

    [<CLIMutable; GenerateSerializer>]
    type ArtifactEvent =
        {
            [<Id(0u)>]
            Event: string
            [<Id(1u)>]
            ArtifactId: ArtifactId
            [<Id(2u)>]
            OwnerId: OwnerId
            [<Id(3u)>]
            OrganizationId: OrganizationId
            [<Id(4u)>]
            RepositoryId: RepositoryId
            [<Id(5u)>]
            ArtifactType: string
            [<Id(6u)>]
            OtherArtifactType: string
            [<Id(7u)>]
            MimeType: string
            [<Id(8u)>]
            Size: int64
            [<Id(9u)>]
            Sha256: string
            [<Id(10u)>]
            BlobPath: string
            [<Id(11u)>]
            CreatedAtUnixTimeTicks: int64
            [<Id(12u)>]
            CreatedBy: string
            [<Id(13u)>]
            Metadata: EventMetadata
        }

        static member FromCreated(eventName: string, artifact: ArtifactCreated, metadata: EventMetadata) =
            {
                Event = eventName
                ArtifactId = artifact.ArtifactId
                OwnerId = artifact.OwnerId
                OrganizationId = artifact.OrganizationId
                RepositoryId = artifact.RepositoryId
                ArtifactType = artifact.ArtifactType
                OtherArtifactType = artifact.OtherArtifactType
                MimeType = artifact.MimeType
                Size = artifact.Size
                Sha256 = artifact.Sha256
                BlobPath = artifact.BlobPath
                CreatedAtUnixTimeTicks = artifact.CreatedAtUnixTimeTicks
                CreatedBy = artifact.CreatedBy
                Metadata = metadata
            }

        member this.ToMetadata() =
            {
                ArtifactId = this.ArtifactId
                OwnerId = this.OwnerId
                OrganizationId = this.OrganizationId
                RepositoryId = this.RepositoryId
                ArtifactType = this.ArtifactType
                OtherArtifactType = this.OtherArtifactType
                MimeType = this.MimeType
                Size = this.Size
                Sha256 = this.Sha256
                BlobPath = this.BlobPath
                CreatedAtUnixTimeTicks = this.CreatedAtUnixTimeTicks
                CreatedBy = this.CreatedBy
            }
                .ToMetadata()

    module ArtifactEventNames =
        let Created = "Created"

    module ArtifactMetadata =
        let UpdateDto (artifactEvent: ArtifactEvent) (current: ArtifactMetadata) =
            if String.Equals(artifactEvent.Event, ArtifactEventNames.Created, StringComparison.OrdinalIgnoreCase) then
                artifactEvent.ToMetadata()
            else
                current
