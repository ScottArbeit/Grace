namespace Grace.Types

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Common
open NodaTime
open Orleans
open System.Collections.Generic
open System.Runtime.Serialization

/// Contains diff helpers.
module Diff =

    /// The state held in the database when creating a physical deletion reminder for a cached state.
    type DeleteCachedStateReminderState = { DeleteReason: DeleteReason; CorrelationId: CorrelationId }

    //[<GenerateSerializer>]
    /// Represents diff dto.
    type DiffDto =
        {
            Class: string
            OwnerId: OwnerId
            OrganizationId: OrganizationId
            RepositoryId: RepositoryId
            DirectoryVersionId1: DirectoryVersionId
            Directory1CreatedAt: Instant
            DirectoryVersionId2: DirectoryVersionId
            Directory2CreatedAt: Instant
            HasDifferences: bool
            Differences: List<FileSystemDifference>
            FileDiffs: List<FileDiff>
        }

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default =
            {
                Class = nameof DiffDto
                OwnerId = OwnerId.Empty
                OrganizationId = OrganizationId.Empty
                RepositoryId = RepositoryId.Empty
                DirectoryVersionId1 = DirectoryVersionId.Empty
                Directory1CreatedAt = Constants.DefaultTimestamp
                DirectoryVersionId2 = DirectoryVersionId.Empty
                Directory2CreatedAt = Constants.DefaultTimestamp
                Differences = List<FileSystemDifference>()
                HasDifferences = false
                FileDiffs = List<FileDiff>()
            }
