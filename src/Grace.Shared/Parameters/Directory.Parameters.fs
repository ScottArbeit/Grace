namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open Grace.Types.Common
open System
open System.Collections.Generic

/// Contains directory version helpers.
module DirectoryVersion =

    /// Represents directory version parameters.
    type DirectoryVersionParameters() =
        inherit CommonParameters()
        member val public OwnerId = String.Empty with get, set
        member val public OwnerName: OwnerName = String.Empty with get, set
        member val public OrganizationId = String.Empty with get, set
        member val public OrganizationName: OrganizationName = String.Empty with get, set
        member val public RepositoryId = String.Empty with get, set
        member val public RepositoryName: RepositoryName = String.Empty with get, set
        member val public DirectoryVersionId = String.Empty with get, set

    /// Represents create parameters.
    type CreateParameters() =
        inherit DirectoryVersionParameters()
        member val public DirectoryVersion = DirectoryVersion.Default with get, set

    /// Represents get parameters.
    type GetParameters() =
        inherit DirectoryVersionParameters()

    /// Represents get by directory ids parameters.
    type GetByDirectoryIdsParameters() =
        inherit DirectoryVersionParameters()
        member val public DirectoryIds = List<DirectoryVersionId>() with get, set

    /// Represents get by sha256 hash parameters.
    type GetBySha256HashParameters() =
        inherit DirectoryVersionParameters()
        member val public Sha256Hash = String.Empty with get, set

    /// Represents get by blake3 hash parameters.
    type GetByBlake3HashParameters() =
        inherit DirectoryVersionParameters()
        member val public Blake3Hash = String.Empty with get, set

    /// Represents get zip file parameters.
    type GetZipFileParameters() =
        inherit DirectoryVersionParameters()
        member val public Sha256Hash = String.Empty with get, set

    /// Represents save directory versions parameters.
    type SaveDirectoryVersionsParameters() =
        inherit DirectoryVersionParameters()
        member val public DirectoryVersions = List<DirectoryVersion>() with get, set
