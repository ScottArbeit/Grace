namespace Grace.Server.Security

open Grace.Shared
open Grace.Shared.Parameters
open Grace.Types.Authorization
open Grace.Types.Common
open System

/// Contains Grace Server storage authorization resources behavior and supporting helpers.
module internal StorageAuthorizationResources =
    /// Builds the authorization resource for a repository path.
    let private pathResource ownerId organizationId repositoryId relativePath = Resource.Path(ownerId, organizationId, repositoryId, relativePath)

    /// Builds authorization resources for upload metadata access.
    let uploadMetadataResources ownerId organizationId repositoryId (parameters: Storage.GetUploadMetadataForFilesParameters) =
        parameters.FileVersions
        |> Seq.map (fun fileVersion -> pathResource ownerId organizationId repositoryId fileVersion.RelativePath)
        |> Seq.toList

    /// Builds authorization resources for issuing upload URIs.
    let uploadUriResources ownerId organizationId repositoryId (parameters: Storage.GetUploadUriParameters) =
        parameters.FileVersions
        |> Seq.map (fun fileVersion -> pathResource ownerId organizationId repositoryId fileVersion.RelativePath)
        |> Seq.toList

    /// Builds the authorization resource for issuing a download URI.
    let downloadUriResource ownerId organizationId repositoryId (parameters: Storage.GetDownloadUriParameters) =
        pathResource ownerId organizationId repositoryId parameters.FileVersion.RelativePath

    /// Builds the authorization resource for uploading a content block.
    let contentBlockUploadResource ownerId organizationId repositoryId (parameters: Storage.GetContentBlockUploadUriParameters) =
        let path =
            if String.IsNullOrWhiteSpace parameters.AuthorizedScope then
                StorageKeys.contentBlockObjectKey parameters.ContentBlockAddress
            else
                parameters.AuthorizedScope

        pathResource ownerId organizationId repositoryId path

    /// Builds the authorization resource for downloading a content block.
    let private contentBlockDownloadResource ownerId organizationId repositoryId (parameters: Storage.GetContentBlockDownloadUriParameters) =
        pathResource ownerId organizationId repositoryId parameters.AuthorizedScope

    /// Attempts to content block download resource and returns an option or result instead of throwing.
    let tryContentBlockDownloadResource correlationId ownerId organizationId repositoryId (parameters: Storage.GetContentBlockDownloadUriParameters) =
        if String.IsNullOrWhiteSpace parameters.AuthorizedScope then
            Error(GraceError.Create "AuthorizedScope is required for ContentBlock manifest download authorization." correlationId)
        else
            Ok(contentBlockDownloadResource ownerId organizationId repositoryId parameters)

    /// Builds the authorization resource for an upload session.
    let uploadSessionResource ownerId organizationId repositoryId (parameters: Storage.UploadSessionStorageParameters) =
        pathResource ownerId organizationId repositoryId parameters.AuthorizedScope
