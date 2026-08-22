namespace Grace.Server

open System
open System.IO
open System.Net.Sockets
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Azure.Storage.Blobs
open Giraffe
open Grace.Actors.Extensions.ActorProxy
open Grace.Server.Security
open Grace.Server.Services
open Grace.Shared
open Grace.Shared.Parameters.Cache
open Grace.Shared.Utilities
open Grace.Types.Authorization
open Grace.Types.Common
open Grace.Types.Repository
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection

/// Implements the two narrow Grace Server operations for a permit-bound DirectoryVersion ZIP fill.
module Cache =

    [<CLIMutable>]
    type private PermitPayload = { UserId: string; PublicKey: CachePublicJwk; Descriptor: CacheArtifactDescriptor; ExpiresAtUnixSeconds: int64 }

    let private permitSigningKey = RandomNumberGenerator.GetBytes(32)

    /// Encodes bytes without padding using the URL-safe alphabet.
    let private base64UrlEncode (bytes: byte array) =
        Convert
            .ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_')

    /// Decodes one unpadded base64url value.
    let private tryBase64UrlDecode (value: string) =
        try
            let padded = value.Replace('-', '+').Replace('_', '/')

            let padded =
                padded
                + String.replicate ((4 - padded.Length % 4) % 4) "="

            Some(Convert.FromBase64String(padded))
        with
        | :? FormatException -> None

    /// Serializes and authenticates one stateless 60-second permit.
    let private createPermit payload =
        let body = JsonSerializer.SerializeToUtf8Bytes(payload, Constants.JsonSerializerOptions)
        use hmac = new HMACSHA256(permitSigningKey)
        let signature = hmac.ComputeHash(body)
        $"{base64UrlEncode body}.{base64UrlEncode signature}"

    /// Validates one permit before any authority or source lookup occurs.
    let private tryReadPermit (permit: string) =
        let parts = permit.Split('.')

        if parts.Length <> 2 then
            None
        else
            match tryBase64UrlDecode parts[0], tryBase64UrlDecode parts[1] with
            | Some body, Some suppliedSignature ->
                use hmac = new HMACSHA256(permitSigningKey)
                let expectedSignature = hmac.ComputeHash(body)

                if not (CryptographicOperations.FixedTimeEquals(expectedSignature, suppliedSignature)) then
                    None
                else
                    try
                        let payload = JsonSerializer.Deserialize<PermitPayload>(body, Constants.JsonSerializerOptions)
                        if isNull (box payload) then None else Some payload
                    with
                    | :? JsonException -> None
            | _ -> None

    /// Imports the supported P-256 public JWK or rejects malformed coordinates and alternate curves.
    let private tryCreatePublicKey jwk =
        if jwk.Kty <> "EC" || jwk.Crv <> "P-256" then
            None
        else
            match tryBase64UrlDecode jwk.X, tryBase64UrlDecode jwk.Y with
            | Some x, Some y when x.Length = 32 && y.Length = 32 ->
                try
                    let parameters = ECParameters(Curve = ECCurve.NamedCurves.nistP256, Q = ECPoint(X = x, Y = y))
                    Some(ECDsa.Create(parameters))
                with
                | :? CryptographicException -> None
            | _ -> None

    /// Creates a permit through the production serializer and HMAC seam for focused binding tests.
    let internal createPermitForTest userId publicKey descriptor (expiresAt: DateTimeOffset) =
        createPermit { UserId = userId; PublicKey = publicKey; Descriptor = descriptor; ExpiresAtUnixSeconds = expiresAt.ToUnixTimeSeconds() }

    /// Verifies production permit authentication, expiry, and P-256 process binding without performing authority reads.
    let internal verifyPermitBindingForTest permit signature (now: DateTimeOffset) =
        match tryReadPermit permit, tryBase64UrlDecode signature with
        | Some payload, Some signatureBytes when
            payload.ExpiresAtUnixSeconds
            >= now.ToUnixTimeSeconds()
            ->
            match tryCreatePublicKey payload.PublicKey with
            | Some publicKey ->
                use publicKey = publicKey

                publicKey.VerifyData(
                    Encoding.UTF8.GetBytes(permit),
                    signatureBytes,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation
                )
            | None -> false
        | _ -> false

    /// Loads repository and immutable directory facts and rejects cross-repository identities.
    let private loadArtifact repositoryId directoryVersionId correlationId =
        task {
            let repositoryProxy = Repository.CreateActorProxy Guid.Empty repositoryId correlationId
            let! repository = repositoryProxy.Get correlationId
            let directoryProxy = DirectoryVersion.CreateActorProxy directoryVersionId repositoryId correlationId
            let! directory = directoryProxy.Get correlationId

            if repository.RepositoryId <> repositoryId
               || directory.DirectoryVersion.RepositoryId
                  <> repositoryId
               || directory.DirectoryVersion.RelativePath
                  <> Constants.RootDirectoryPath
                  && directory.DirectoryVersion.RelativePath <> "/" then
                return Error "Repository or immutable root directory version was not found."
            else
                return Ok(repository, directoryProxy)
        }

    /// Rechecks RepositoryRead against current Grace role assignments.
    let private revalidateUserAccess (context: HttpContext) userId (repository: RepositoryDto) =
        task {
            let evaluator = context.RequestServices.GetRequiredService<IGracePermissionEvaluator>()

            let principals =
                [
                    { PrincipalType = PrincipalType.User; PrincipalId = userId }
                ]

            let resource = Resource.Repository(repository.OwnerId, repository.OrganizationId, repository.RepositoryId)
            let! decision = evaluator.CheckAsync(principals, Set.empty, Operation.RepositoryRead, resource)
            return decision
        }

    /// Returns a redacted forbidden response for invalid permits and changed access.
    let private forbidden detail : HttpHandler =
        setStatusCode StatusCodes.Status403Forbidden
        >=> json { Code = "CachePermitRedemptionFailed"; Detail = detail }

    /// Returns true only for the explicit Grace integration-test process mode.
    let private isGraceTestingEnabled () = String.Equals(Environment.GetEnvironmentVariable("GRACE_TESTING"), "1", StringComparison.Ordinal)

    /// Pauses only a selected GRACE_TESTING redemption after descriptor preparation and before final access revalidation.
    let private enterPostDescriptorTestGate (context: HttpContext) =
        task {
            match Environment.GetEnvironmentVariable("GRACE_TEST_DESCRIPTION_CLEAR_PRE_APPEND_PORT"),
                  context.Request.Headers.TryGetValue("X-Grace-Test-Cache-Redemption-Gate-Port")
                with
            | configuredPort, (true, requestedPort) when
                isGraceTestingEnabled ()
                && not (String.IsNullOrWhiteSpace configuredPort)
                && String.Equals(configuredPort, string requestedPort, StringComparison.Ordinal)
                ->
                match Int32.TryParse configuredPort with
                | true, port when port > 0 && port <= 65535 ->
                    use client = new TcpClient(AddressFamily.InterNetwork)
                    use timeout = new Threading.CancellationTokenSource(TimeSpan.FromSeconds(20.0))
                    do! client.ConnectAsync("127.0.0.1", port, timeout.Token)
                    use stream = client.GetStream()
                    use reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true)
                    use writer = new StreamWriter(stream, Encoding.UTF8, 1024, true)
                    do! writer.WriteLineAsync("cache-descriptor-ready".AsMemory(), timeout.Token)
                    do! writer.FlushAsync(timeout.Token)
                    let! release = reader.ReadLineAsync(timeout.Token)

                    if release <> "release" then
                        invalidOp "The Cache redemption test gate received an invalid release."
                | _ -> ()
            | _ -> ()
        }

    /// Reads the immutable descriptor metadata from the exact ZIP Blob without returning its SAS.
    let private readDescriptor repositoryId directoryVersionId (sourceUri: Uri) =
        task {
            let client = BlobClient(sourceUri)
            let! properties = client.GetPropertiesAsync()
            let metadata = properties.Value.Metadata
            let mutable sha256 = String.Empty
            let mutable sizeText = String.Empty

            if
                not (metadata.TryGetValue("grace_sha256", &sha256))
                || not (metadata.TryGetValue("grace_size", &sizeText))
            then
                return Error "DirectoryVersion ZIP descriptor metadata is unavailable."
            else
                match Int64.TryParse(sizeText) with
                | true, size when
                    size >= 0L
                    && sha256.Length = 64
                    && sha256
                       |> Seq.forall (fun value ->
                           Char.IsDigit(value)
                           || (value >= 'a' && value <= 'f'))
                    ->
                    return
                        Ok
                            {
                                RepositoryId = string repositoryId
                                DirectoryVersionId = string directoryVersionId
                                Kind = "DirectoryVersionZip"
                                Sha256 = sha256
                                Size = size
                            }
                | _ -> return Error "DirectoryVersion ZIP descriptor metadata is invalid."
        }

    /// Prepares one exact artifact after current authenticated access and Blob descriptor validation.
    let PrepareDirectoryVersionZip: HttpHandler =
        fun next context ->
            task {
                try
                    let! parameters =
                        context
                        |> parse<PrepareDirectoryVersionZipParameters>

                    let mutable repositoryId = Guid.Empty
                    let mutable directoryVersionId = Guid.Empty

                    match PrincipalMapper.tryGetUserId context.User,
                          Guid.TryParse(parameters.RepositoryId, &repositoryId),
                          Guid.TryParse(parameters.DirectoryVersionId, &directoryVersionId),
                          tryCreatePublicKey parameters.CachePublicKey
                        with
                    | Some userId, true, true, Some publicKey ->
                        use _publicKey = publicKey

                        match! loadArtifact repositoryId directoryVersionId (getCorrelationId context) with
                        | Error message ->
                            return!
                                context
                                |> result400BadRequest (GraceError.Create message (getCorrelationId context))
                        | Ok (repository, directoryProxy) ->
                            match! revalidateUserAccess context userId repository with
                            | Denied _ -> return! forbidden "Repository access is required." next context
                            | Allowed _ ->
                                let! sourceUri = directoryProxy.GetZipFileUri(getCorrelationId context)

                                match! readDescriptor repositoryId directoryVersionId sourceUri with
                                | Error message ->
                                    return!
                                        context
                                        |> result400BadRequest (GraceError.Create message (getCorrelationId context))
                                | Ok descriptor ->
                                    let expiresAt = DateTimeOffset.UtcNow.AddSeconds(60.0)

                                    let payload =
                                        {
                                            UserId = userId
                                            PublicKey = parameters.CachePublicKey
                                            Descriptor = descriptor
                                            ExpiresAtUnixSeconds = expiresAt.ToUnixTimeSeconds()
                                        }

                                    let permit = createPermit payload

                                    let prepared =
                                        {
                                            Descriptor = descriptor
                                            Permit = permit
                                            PermitExpiresAt = expiresAt
                                            RedemptionBytes =
                                                permit
                                                |> Encoding.UTF8.GetBytes
                                                |> base64UrlEncode
                                        }

                                    return!
                                        context
                                        |> result200Ok (GraceReturnValue.Create prepared (getCorrelationId context))
                    | _ ->
                        return!
                            context
                            |> result400BadRequest (
                                GraceError.Create "Repository, directory version, and valid P-256 public JWK are required." (getCorrelationId context)
                            )
                with
                | ex ->
                    return!
                        context
                        |> result500ServerError (GraceError.Create "Cache preparation failed." (getCorrelationId context))
            }

    /// Redeems one permit only after signature, expiry, artifact binding, and current access all revalidate.
    let RedeemDirectoryVersionZipFill: HttpHandler =
        fun next context ->
            task {
                try
                    let! parameters =
                        context
                        |> parse<RedeemDirectoryVersionZipFillParameters>

                    match tryReadPermit parameters.Permit, tryBase64UrlDecode parameters.Signature with
                    | Some payload, Some signature when
                        payload.ExpiresAtUnixSeconds
                        >= DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        ->
                        match tryCreatePublicKey payload.PublicKey with
                        | None -> return! forbidden "Permit binding failed." next context
                        | Some publicKey ->
                            use publicKey = publicKey

                            let validSignature =
                                publicKey.VerifyData(
                                    Encoding.UTF8.GetBytes(parameters.Permit),
                                    signature,
                                    HashAlgorithmName.SHA256,
                                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation
                                )

                            if not validSignature then
                                return! forbidden "Permit binding failed." next context
                            else
                                let repositoryId = Guid.Parse(payload.Descriptor.RepositoryId)
                                let directoryVersionId = Guid.Parse(payload.Descriptor.DirectoryVersionId)

                                match! loadArtifact repositoryId directoryVersionId (getCorrelationId context) with
                                | Error _ -> return! forbidden "Permit binding failed." next context
                                | Ok (repository, directoryProxy) ->
                                    match! revalidateUserAccess context payload.UserId repository with
                                    | Denied _ -> return! forbidden "Repository access changed." next context
                                    | Allowed _ ->
                                        let! sourceUri = directoryProxy.GetZipFileUri(getCorrelationId context)

                                        match! readDescriptor repositoryId directoryVersionId sourceUri with
                                        | Ok current when current = payload.Descriptor ->
                                            do! enterPostDescriptorTestGate context

                                            match! revalidateUserAccess context payload.UserId repository with
                                            | Denied _ -> return! forbidden "Repository access changed." next context
                                            | Allowed _ ->
                                                let source =
                                                    {
                                                        Descriptor = current
                                                        SourceUri = string sourceUri
                                                        SourceExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15.0)
                                                    }

                                                return!
                                                    context
                                                    |> result200Ok (GraceReturnValue.Create source (getCorrelationId context))
                                        | _ -> return! forbidden "Artifact binding changed." next context
                    | _ -> return! forbidden "Permit validation failed." next context
                with
                | _ -> return! forbidden "Permit validation failed." next context
            }
