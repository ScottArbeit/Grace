namespace Grace.Types

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Types
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
            Class: string
            ArtifactId: ArtifactId
            OwnerId: OwnerId
            OrganizationId: OrganizationId
            RepositoryId: RepositoryId
            ArtifactType: ArtifactType
            MimeType: string
            Size: int64
            Sha256: Sha256Hash option
            BlobPath: string
            CreatedAt: Instant
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

    [<KnownType("GetKnownTypes")>]
    type ArtifactCommand =
        | Create of artifact: ArtifactMetadata

        static member GetKnownTypes() = GetKnownTypes<ArtifactCommand>()

    [<KnownType("GetKnownTypes")>]
    type ArtifactEventType =
        | Created of artifact: ArtifactMetadata

        static member GetKnownTypes() = GetKnownTypes<ArtifactEventType>()

    type ArtifactEvent = { Event: ArtifactEventType; Metadata: EventMetadata }

    module ArtifactMetadata =
        let UpdateDto (artifactEvent: ArtifactEvent) (_current: ArtifactMetadata) =
            match artifactEvent.Event with
            | ArtifactEventType.Created artifact -> artifact
