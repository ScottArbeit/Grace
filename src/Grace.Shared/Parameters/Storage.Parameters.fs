namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open Grace.Types.Types
open System

module Storage =

    /// Parameters used by multiple endpoints in the /diff path.
    type StorageParameters() =
        inherit CommonParameters()
        member val public OwnerId = String.Empty with get, set
        member val public OwnerName: OwnerName = String.Empty with get, set
        member val public OrganizationId = String.Empty with get, set
        member val public OrganizationName: OrganizationName = String.Empty with get, set
        member val public RepositoryId = String.Empty with get, set
        member val public RepositoryName: RepositoryName = String.Empty with get, set

    /// Parameters used by the /diff/populate endpoint.
    type DeleteAllParameters() =
        inherit StorageParameters()

    /// Parameters used by the /diff/get endpoint.
    type GetUploadUriParameters() =
        inherit StorageParameters()
        member val public FileVersions = Array.empty<FileVersion> with get, set

    type GetDownloadUriParameters() =
        inherit StorageParameters()
        member val public FileVersion = FileVersion.Default with get, set

    type GetUploadMetadataForFilesParameters() =
        inherit StorageParameters()
        member val public FileVersions = Array.empty<FileVersion> with get, set
