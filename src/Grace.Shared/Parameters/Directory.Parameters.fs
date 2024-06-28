namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open Grace.Shared.Types
open System
open System.Collections.Generic

module Directory =

    type DirectoryParameters() =
        inherit CommonParameters()
        member val public DirectoryId = String.Empty with get, set

    type CreateParameters() =
        inherit DirectoryParameters()
        member val public DirectoryVersion = DirectoryVersion.Default with get, set

    type GetParameters() =
        inherit DirectoryParameters()
        member val public RepositoryId = String.Empty with get, set

    type GetByDirectoryIdsParameters() =
        inherit DirectoryParameters()
        member val public RepositoryId = String.Empty with get, set
        member val public DirectoryIds = List<DirectoryVersionId>() with get, set

    type GetBySha256HashParameters() =
        inherit DirectoryParameters()
        member val public RepositoryId = String.Empty with get, set
        member val public Sha256Hash = String.Empty with get, set

    type SaveDirectoryVersionsParameters() =
        inherit DirectoryParameters()
        member val public DirectoryVersions = List<DirectoryVersion>() with get, set
