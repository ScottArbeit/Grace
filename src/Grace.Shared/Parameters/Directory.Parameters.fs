namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open Grace.Shared.Types
open System
open System.Collections.Generic

module DirectoryVersion =

    type DirectoryVersionParameters() =
        inherit CommonParameters()
        member val public OwnerId = String.Empty with get, set
        member val public OwnerName: OwnerName = String.Empty with get, set
        member val public OrganizationId = String.Empty with get, set
        member val public OrganizationName: OrganizationName = String.Empty with get, set
        member val public RepositoryId = String.Empty with get, set
        member val public RepositoryName: RepositoryName = String.Empty with get, set
        member val public DirectoryVersionId = String.Empty with get, set

    type CreateParameters() =
        inherit DirectoryVersionParameters()
        member val public DirectoryVersion = DirectoryVersion.Default with get, set

    type GetParameters() =
        inherit DirectoryVersionParameters()

    type GetByDirectoryIdsParameters() =
        inherit DirectoryVersionParameters()
        member val public DirectoryIds = List<DirectoryVersionId>() with get, set

    type GetBySha256HashParameters() =
        inherit DirectoryVersionParameters()
        member val public Sha256Hash = String.Empty with get, set

    type GetZipFileParameters() =
        inherit DirectoryVersionParameters()
        member val public Sha256Hash = String.Empty with get, set

    type SaveDirectoryVersionsParameters() =
        inherit DirectoryVersionParameters()
        member val public DirectoryVersions = List<DirectoryVersion>() with get, set
