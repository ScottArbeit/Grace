namespace Grace.Types

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Common
open NodaTime
open Orleans
open System
open System.Runtime.Serialization

/// Contains artifact helpers.
module Artifact =

    /// Represents artifact type.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ArtifactType =
        | AgentSummary
        | ConflictReport
        | Prompt
        | ValidationOutput
        | ReviewNotes
        | Other of kind: string

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ArtifactType>()

    /// Captures the exact attachment deletion identity delivered by the durable cleanup reminder.
    [<GenerateSerializer>]
    type PhysicalDeletionReminderState =
        {
            [<Id(0u)>]
            ArtifactId: ArtifactId
            [<Id(1u)>]
            RepositoryId: RepositoryId
            [<Id(2u)>]
            WorkItemId: WorkItemId
            [<Id(3u)>]
            DeletionGeneration: Guid
            [<Id(4u)>]
            DeletedAt: Instant
            [<Id(5u)>]
            PhysicalDeletionAt: Instant
            [<Id(6u)>]
            CorrelationId: CorrelationId
        }

    /// Represents artifact metadata.
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
            [<Id(12u)>]
            WorkItemId: WorkItemId option
            [<Id(13u)>]
            DeletedAt: Instant option
            [<Id(14u)>]
            DeleteReason: DeleteReason
            [<Id(15u)>]
            DeletionGeneration: Guid
            [<Id(16u)>]
            PhysicalDeletionAt: Instant option
            [<Id(17u)>]
            BlobDeleted: bool
            [<Id(18u)>]
            WorkItemLinkRemoved: bool
        }

        /// Represents the deterministic default instance used when callers need an initialized contract value.
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
                WorkItemId = None
                DeletedAt = None
                DeleteReason = String.Empty
                DeletionGeneration = Guid.Empty
                PhysicalDeletionAt = None
                BlobDeleted = false
                WorkItemLinkRemoved = false
            }

        /// Reports whether the artifact is unavailable because logical deletion is active.
        member this.IsDeleted = this.DeletedAt.IsSome

    /// Represents the artifact create result contract.
    [<GenerateSerializer>]
    type ArtifactCreateResult = { ArtifactId: ArtifactId; UploadUri: UriWithSharedAccessSignature; BlobPath: string }

    /// Represents the artifact download uri result contract.
    [<GenerateSerializer>]
    type ArtifactDownloadUriResult = { ArtifactId: ArtifactId; DownloadUri: UriWithSharedAccessSignature }

    /// Describes the accepted recoverable deletion generation and its immutable cleanup deadline.
    [<GenerateSerializer>]
    type ArtifactDeletionResult =
        {
            ArtifactId: ArtifactId
            WorkItemId: WorkItemId
            DeletionGeneration: Guid
            DeletedAt: Instant
            PhysicalDeletionAt: Instant
            DeleteReason: DeleteReason
        }

    /// Represents artifact created.
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
            [<Id(12u)>]
            WorkItemId: WorkItemId
        }

        /// Rehydrates the artifact contract from persisted metadata fields.
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
                WorkItemId =
                    artifact.WorkItemId
                    |> Option.defaultValue WorkItemId.Empty
            }

        /// Projects the artifact contract into the metadata shape stored outside the aggregate.
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
                WorkItemId = if this.WorkItemId = WorkItemId.Empty then None else Some this.WorkItemId
            }

    /// Represents artifact command.
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
            [<Id(13u)>]
            WorkItemId: WorkItemId
            [<Id(14u)>]
            DeleteReason: DeleteReason
            [<Id(15u)>]
            DeletionGeneration: Guid
            [<Id(16u)>]
            DeletedAtUnixTimeTicks: int64
            [<Id(17u)>]
            PhysicalDeletionAtUnixTimeTicks: int64
        }

        /// Builds the contract value from required caller inputs and generated defaults used by this surface.
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
                WorkItemId = artifact.WorkItemId
                DeleteReason = String.Empty
                DeletionGeneration = Guid.Empty
                DeletedAtUnixTimeTicks = 0L
                PhysicalDeletionAtUnixTimeTicks = 0L
            }

        /// Requests logical deletion for one exact owning work item and immutable retention deadline.
        static member DeleteLogical(workItemId, deleteReason, deletionGeneration, deletedAt: Instant, physicalDeletionAt: Instant) =
            { ArtifactCommand.Create(ArtifactCreated.FromMetadata ArtifactMetadata.Default) with
                Command = "DeleteLogical"
                WorkItemId = workItemId
                DeleteReason = deleteReason
                DeletionGeneration = deletionGeneration
                DeletedAtUnixTimeTicks = deletedAt.ToUnixTimeTicks()
                PhysicalDeletionAtUnixTimeTicks = physicalDeletionAt.ToUnixTimeTicks()
            }

        /// Requests recovery of a logically deleted attachment owned by the exact work item.
        static member Undelete(workItemId) =
            { ArtifactCommand.Create(ArtifactCreated.FromMetadata ArtifactMetadata.Default) with Command = "Undelete"; WorkItemId = workItemId }

        /// Projects the artifact contract into the event payload emitted when the artifact is first recorded.
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
                WorkItemId = this.WorkItemId
            }

    /// Contains artifact command names helpers.
    module ArtifactCommandNames =
        let Create = "Create"
        let DeleteLogical = "DeleteLogical"
        let Undelete = "Undelete"

    /// Represents artifact event.
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
            [<Id(14u)>]
            WorkItemId: WorkItemId
            [<Id(15u)>]
            DeletedAtUnixTimeTicks: int64
            [<Id(16u)>]
            DeleteReason: DeleteReason
            [<Id(17u)>]
            DeletionGeneration: Guid
            [<Id(18u)>]
            PhysicalDeletionAtUnixTimeTicks: int64
            [<Id(19u)>]
            BlobDeleted: bool
            [<Id(20u)>]
            WorkItemLinkRemoved: bool
        }

        /// Rehydrates the artifact contract from the creation event payload.
        static member FromCreated(eventName: string, artifact: ArtifactCreated, metadata: EventMetadata) =
            ArtifactEvent.FromMetadata(eventName, artifact.ToMetadata(), metadata)

        /// Captures a complete artifact state snapshot for deterministic event replay.
        static member FromMetadata(eventName: string, artifact: ArtifactMetadata, metadata: EventMetadata) =
            let created = ArtifactCreated.FromMetadata artifact

            {
                Event = eventName
                ArtifactId = created.ArtifactId
                OwnerId = created.OwnerId
                OrganizationId = created.OrganizationId
                RepositoryId = created.RepositoryId
                ArtifactType = created.ArtifactType
                OtherArtifactType = created.OtherArtifactType
                MimeType = created.MimeType
                Size = created.Size
                Sha256 = created.Sha256
                BlobPath = created.BlobPath
                CreatedAtUnixTimeTicks = created.CreatedAtUnixTimeTicks
                CreatedBy = created.CreatedBy
                Metadata = metadata
                WorkItemId = created.WorkItemId
                DeletedAtUnixTimeTicks =
                    artifact.DeletedAt
                    |> Option.map (fun value -> value.ToUnixTimeTicks())
                    |> Option.defaultValue 0L
                DeleteReason = artifact.DeleteReason
                DeletionGeneration = artifact.DeletionGeneration
                PhysicalDeletionAtUnixTimeTicks =
                    artifact.PhysicalDeletionAt
                    |> Option.map (fun value -> value.ToUnixTimeTicks())
                    |> Option.defaultValue 0L
                BlobDeleted = artifact.BlobDeleted
                WorkItemLinkRemoved = artifact.WorkItemLinkRemoved
            }

        /// Projects the artifact contract into the metadata shape stored outside the aggregate.
        member this.ToMetadata() =
            let created =
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
                    WorkItemId = this.WorkItemId
                }

            let metadata = created.ToMetadata()

            { metadata with
                DeletedAt =
                    if this.DeletedAtUnixTimeTicks = 0L then
                        None
                    else
                        Some(Instant.FromUnixTimeTicks this.DeletedAtUnixTimeTicks)
                DeleteReason = this.DeleteReason
                DeletionGeneration = this.DeletionGeneration
                PhysicalDeletionAt =
                    if this.PhysicalDeletionAtUnixTimeTicks = 0L then
                        None
                    else
                        Some(Instant.FromUnixTimeTicks this.PhysicalDeletionAtUnixTimeTicks)
                BlobDeleted = this.BlobDeleted
                WorkItemLinkRemoved = this.WorkItemLinkRemoved
            }

    /// Contains artifact event names helpers.
    module ArtifactEventNames =
        let Created = "Created"
        let LogicalDeleted = "LogicalDeleted"
        let Undeleted = "Undeleted"
        let BlobDeleted = "BlobDeleted"
        let WorkItemLinkRemoved = "WorkItemLinkRemoved"

    /// Contains artifact metadata helpers.
    module ArtifactMetadata =
        /// Carries optional artifact fields that can be patched without rebuilding the full artifact record.
        let UpdateDto (artifactEvent: ArtifactEvent) (current: ArtifactMetadata) = artifactEvent.ToMetadata()
