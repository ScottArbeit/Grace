namespace Grace.Server.Security

open Grace.Shared
open Grace.Shared.Parameters
open Grace.Types.Authorization
open Grace.Types.Common
open System

module internal StorageAuthorizationResources =
    let private pathResource ownerId organizationId repositoryId relativePath = Resource.Path(ownerId, organizationId, repositoryId, relativePath)

    let uploadMetadataResources ownerId organizationId repositoryId (parameters: Storage.GetUploadMetadataForFilesParameters) =
        parameters.FileVersions
        |> Seq.map (fun fileVersion -> pathResource ownerId organizationId repositoryId fileVersion.RelativePath)
        |> Seq.toList

    let uploadUriResources ownerId organizationId repositoryId (parameters: Storage.GetUploadUriParameters) =
        parameters.FileVersions
        |> Seq.map (fun fileVersion -> pathResource ownerId organizationId repositoryId fileVersion.RelativePath)
        |> Seq.toList

    let downloadUriResource ownerId organizationId repositoryId (parameters: Storage.GetDownloadUriParameters) =
        pathResource ownerId organizationId repositoryId parameters.FileVersion.RelativePath

    let contentBlockUploadResource ownerId organizationId repositoryId (parameters: Storage.GetContentBlockUploadUriParameters) =
        let path =
            if String.IsNullOrWhiteSpace parameters.AuthorizedScope then
                StorageKeys.contentBlockObjectKey parameters.ContentBlockAddress
            else
                parameters.AuthorizedScope

        pathResource ownerId organizationId repositoryId path

    let contentBlockDownloadResource ownerId organizationId repositoryId (parameters: Storage.GetContentBlockDownloadUriParameters) =
        pathResource ownerId organizationId repositoryId (StorageKeys.contentBlockObjectKey parameters.ContentBlockAddress)

    let uploadSessionResource ownerId organizationId repositoryId (parameters: Storage.UploadSessionStorageParameters) =
        pathResource ownerId organizationId repositoryId parameters.AuthorizedScope
