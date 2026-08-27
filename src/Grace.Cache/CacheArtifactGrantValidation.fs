namespace Grace.Cache

open System
open System.Net.Http
open System.Net.Http.Json
open System.Threading
open System.Threading.Tasks
open Grace.Shared
open Grace.Types.ArtifactGrant
open Grace.Types.Common

/// Classifies Cache artifact admission failures without retaining or returning grant contents.
type CacheArtifactGrantAdmissionError =
    | Unauthorized
    | Forbidden
    | ValidationKeyUnavailable

/// Validates signed artifact grants locally and refreshes the Server key only for an unknown key id.
type CacheArtifactGrantValidator(serverUri: Uri, httpClient: HttpClient, clock: Func<DateTimeOffset>) =

    let refreshGate = new SemaphoreSlim(1, 1)
    let mutable cachedKey: CacheArtifactGrantValidationKey option = None

    /// Fetches the one public validation key without sending credentials or grant contents.
    let fetchValidationKey () =
        task {
            try
                use! response = httpClient.GetAsync(Uri(serverUri, "cache/artifact-grant-validation-key"), CancellationToken.None)

                if not response.IsSuccessStatusCode then
                    return Error ValidationKeyUnavailable
                else
                    let! envelope =
                        response.Content.ReadFromJsonAsync<GraceReturnValue<CacheArtifactGrantValidationKey>>(
                            Constants.JsonSerializerOptions,
                            CancellationToken.None
                        )

                    if
                        isNull (box envelope)
                        || isNull (box envelope.ReturnValue)
                    then
                        return Error ValidationKeyUnavailable
                    else
                        return Ok envelope.ReturnValue
            with
            | :? HttpRequestException
            | :? TaskCanceledException
            | :? NotSupportedException -> return Error ValidationKeyUnavailable
        }

    /// Returns a matching cached key or performs the request's one permitted unknown-key refresh.
    let resolveKey keyId =
        task {
            match cachedKey with
            | Some key when key.KeyId = keyId -> return Ok key
            | _ ->
                do! refreshGate.WaitAsync()

                try
                    match cachedKey with
                    | Some key when key.KeyId = keyId -> return Ok key
                    | _ ->
                        match! fetchValidationKey () with
                        | Error error -> return Error error
                        | Ok key ->
                            cachedKey <- Some key

                            if key.KeyId = keyId then return Ok key else return Error Unauthorized
                finally
                    refreshGate.Release() |> ignore
        }

    /// Validates the Bearer grant before returning the exact artifact tuple selected by its signed claims.
    member _.ValidateAsync(authorization: string, requestRoute: string) : Task<Result<DirectoryVersionZipCacheArtifact, CacheArtifactGrantAdmissionError>> =
        task {
            if
                String.IsNullOrWhiteSpace(authorization)
                || not (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            then
                return Error Unauthorized
            else
                let token = authorization[ "Bearer ".Length .. ].Trim()

                if
                    String.IsNullOrWhiteSpace(token)
                    || token.IndexOfAny([| ' '; '\t'; '\r'; '\n' |]) >= 0
                then
                    return Error Unauthorized
                else
                    try
                        let grant = CacheArtifactGrant.Create token

                        match ArtifactGrant.tryReadKeyId grant, ArtifactGrant.tryReadArtifact grant with
                        | Some keyId, Some artifact when artifact.Route = requestRoute ->
                            match! resolveKey keyId with
                            | Error error -> return Error error
                            | Ok key ->
                                match ArtifactGrant.validate (clock.Invoke()) key (CacheArtifactGrantValidationRequest.Create artifact) grant with
                                | Ok () -> return Ok artifact
                                | Error CacheArtifactGrantValidationError.WrongMethod
                                | Error CacheArtifactGrantValidationError.WrongRoute
                                | Error CacheArtifactGrantValidationError.WrongArtifactKind
                                | Error CacheArtifactGrantValidationError.WrongRepository
                                | Error CacheArtifactGrantValidationError.WrongDirectoryVersion
                                | Error CacheArtifactGrantValidationError.WrongBlake3 -> return Error Forbidden
                                | Error _ -> return Error Unauthorized
                        | Some _, Some _ -> return Error Forbidden
                        | _ -> return Error Unauthorized
                    with
                    | :? ArgumentException -> return Error Unauthorized
        }

    interface IDisposable with
        /// Releases the refresh gate owned by this Cache process validator.
        member _.Dispose() = refreshGate.Dispose()
