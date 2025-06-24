namespace Grace.Types

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Types
open NodaTime
open Orleans
open System.Collections.Generic
open System.Runtime.Serialization

module Diff =

    /// Represents a Diff between two DirectoryVersions in a repository.
    type DiffDto =
        { Class: string
          OwnerId: OwnerId
          OrganizationId: OrganizationId
          RepositoryId: RepositoryId
          DirectoryVersionId1: DirectoryVersionId
          Directory1CreatedAt: Instant
          DirectoryVersionId2: DirectoryVersionId
          Directory2CreatedAt: Instant
          HasDifferences: bool
          Differences: List<FileSystemDifference>
          FileDiffs: List<FileDiff> }

        static member Default =
            { Class = nameof (DiffDto)
              OwnerId = OwnerId.Empty
              OrganizationId = OrganizationId.Empty
              RepositoryId = RepositoryId.Empty
              DirectoryVersionId1 = DirectoryVersionId.Empty
              Directory1CreatedAt = Constants.DefaultTimestamp
              DirectoryVersionId2 = DirectoryVersionId.Empty
              Directory2CreatedAt = Constants.DefaultTimestamp
              Differences = List<FileSystemDifference>()
              HasDifferences = false
              FileDiffs = List<FileDiff>() }
