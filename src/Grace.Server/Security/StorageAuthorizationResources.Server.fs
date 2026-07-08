namespace Grace.Server.Security

open Grace.Shared
open Grace.Shared.Parameters
open Grace.Types.Authorization
open Grace.Types.Common
open Microsoft.AspNetCore.Http
open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json

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
        pathResource ownerId organizationId repositoryId parameters.RelativePath

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

/// Contains canonical request binding shared by storage path authorization and handler processing.
module internal StorageDownloadRequestBinding =
    let private getDownloadUriRequestCacheKey = "Grace.Server.Storage.GetDownloadUriParameters"

    let private getDownloadUriJsonPropertyNames =
        [|
            "CorrelationId"
            "Principal"
            "OwnerId"
            "OwnerName"
            "OrganizationId"
            "OrganizationName"
            "RepositoryId"
            "RepositoryName"
            "ReferenceId"
            "RelativePath"
            "Sha256Hash"
            "Blake3Hash"
        |]

    let private getDownloadUriJsonOptions =
        let options = JsonSerializerOptions(JsonSerializerDefaults.Web)
        options.PropertyNameCaseInsensitive <- true
        options

    /// Resolves a request property name to the canonical getDownloadUri semantic field.
    let private tryCanonicalGetDownloadUriPropertyName propertyName =
        getDownloadUriJsonPropertyNames
        |> Array.tryFind (fun candidate -> String.Equals(candidate, propertyName, StringComparison.OrdinalIgnoreCase))

    /// Rejects ambiguous getDownloadUri JSON bodies before authorization and handler binding can diverge.
    let private tryValidateGetDownloadUriJsonShape (document: JsonDocument) correlationId =
        if document.RootElement.ValueKind
           <> JsonValueKind.Object then
            Error(GraceError.Create "Malformed getDownloadUri request body: expected a JSON object." correlationId)
        else
            let seenProperties = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            let mutable duplicateError: GraceError option = None

            let properties = document.RootElement.EnumerateObject()

            for property in properties do
                if duplicateError.IsNone then
                    match tryCanonicalGetDownloadUriPropertyName property.Name with
                    | None -> ()
                    | Some canonicalName ->
                        match seenProperties.TryGetValue canonicalName with
                        | true, firstName ->
                            duplicateError <-
                                Some(
                                    GraceError.Create
                                        $"Duplicate getDownloadUri request property '{property.Name}' conflicts with '{firstName}' for semantic field '{canonicalName}'. Use only the canonical '{canonicalName}' property."
                                        correlationId
                                )
                        | false, _ -> seenProperties[canonicalName] <- property.Name

            match duplicateError with
            | Some error -> Error error
            | None -> Ok()

    /// Binds the canonical getDownloadUri request view shared by path authorization and handler processing.
    let bindGetDownloadUriParameters (context: HttpContext) correlationId =
        task {
            match context.Items.TryGetValue getDownloadUriRequestCacheKey with
            | true, (:? Result<Storage.GetDownloadUriParameters, GraceError> as cachedResult) -> return cachedResult
            | _ ->
                context.Request.EnableBuffering()

                context.Request.Body.Seek(0L, SeekOrigin.Begin)
                |> ignore

                use reader = new StreamReader(context.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks = true, bufferSize = 1024, leaveOpen = true)

                let! requestBody = reader.ReadToEndAsync()

                context.Request.Body.Seek(0L, SeekOrigin.Begin)
                |> ignore

                let result =
                    try
                        use document = JsonDocument.Parse requestBody

                        match tryValidateGetDownloadUriJsonShape document correlationId with
                        | Error error -> Error error
                        | Ok () ->
                            let parameters = JsonSerializer.Deserialize<Storage.GetDownloadUriParameters>(requestBody, getDownloadUriJsonOptions)

                            if Object.ReferenceEquals(parameters, null) then
                                Error(GraceError.Create "Malformed getDownloadUri request body: expected a JSON object." correlationId)
                            else
                                Ok parameters
                    with
                    | :? JsonException as ex -> Error(GraceError.Create $"Malformed getDownloadUri request body: {ex.Message}" correlationId)

                context.Items[ getDownloadUriRequestCacheKey ] <- result
                return result
        }
