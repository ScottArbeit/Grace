namespace Grace.Shared

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open Grace.Types.ArtifactGrant

/// Issues and validates the fixed compact ES256 grant used by Grace Cache artifact reads.
module ArtifactGrant =

    [<CLIMutable>]
    type private WireHeader =
        {
            [<JsonPropertyName("alg")>]
            Algorithm: string
            [<JsonPropertyName("kid")>]
            KeyId: string
            [<JsonPropertyName("typ")>]
            Type: string
        }

    [<CLIMutable>]
    type private WireClaims =
        {
            [<JsonPropertyName("iss")>]
            Issuer: string
            [<JsonPropertyName("aud")>]
            Audience: string
            [<JsonPropertyName("iat")>]
            IssuedAt: int64
            [<JsonPropertyName("nbf")>]
            NotBefore: int64
            [<JsonPropertyName("exp")>]
            ExpiresAt: int64
            [<JsonPropertyName("artifactKind")>]
            ArtifactKind: string
            [<JsonPropertyName("repositoryId")>]
            RepositoryId: string
            [<JsonPropertyName("directoryVersionId")>]
            DirectoryVersionId: string
            [<JsonPropertyName("blake3Hash")>]
            Blake3Hash: string
            [<JsonPropertyName("method")>]
            Method: string
            [<JsonPropertyName("route")>]
            Route: string
        }

    /// Encodes bytes with the compact-token base64url alphabet and no padding.
    let private encode (bytes: byte array) =
        Convert
            .ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_')

    /// Decodes only the canonical unpadded base64url form accepted by the grant protocol.
    let private tryDecode (value: string) =
        try
            if String.IsNullOrEmpty(value) then
                None
            else
                let padded =
                    value.Replace('-', '+').Replace('_', '/')
                    + String.replicate ((4 - value.Length % 4) % 4) "="

                let bytes = Convert.FromBase64String(padded)
                if encode bytes = value then Some bytes else None
        with
        | :? FormatException -> None

    /// Reads the three compact-token segments without logging or returning their raw contents.
    let private tryReadSegments (grant: CacheArtifactGrant) =
        let parts = grant.Value.Split('.')
        if parts.Length = 3 then Some(parts[0], parts[1], parts[2]) else None

    /// Deserializes one compact-token segment using its explicit protocol field names.
    let private tryDeserialize<'T> segment =
        match tryDecode segment with
        | None -> None
        | Some bytes ->
            try
                let value = JsonSerializer.Deserialize<'T>(bytes, Constants.JsonSerializerOptions)
                if isNull (box value) then None else Some value
            with
            | :? JsonException -> None

    /// Imports one canonical public P-256 JWK for ES256 signature verification.
    let private tryCreateVerifier (jwk: P256PublicJwk) =
        if isNull (box jwk)
           || jwk.Kty <> "EC"
           || jwk.Crv <> "P-256" then
            None
        else
            match tryDecode jwk.X, tryDecode jwk.Y with
            | Some x, Some y when x.Length = 32 && y.Length = 32 ->
                try
                    Some(ECDsa.Create(ECParameters(Curve = ECCurve.NamedCurves.nistP256, Q = ECPoint(X = x, Y = y))))
                with
                | :? CryptographicException
                | :? ArgumentException
                | :? PlatformNotSupportedException -> None
            | _ -> None

    /// Builds the public validation-key response for one current Server process key.
    let createValidationKey keyId (key: ECDsa) =
        if String.IsNullOrWhiteSpace(keyId) then
            invalidArg (nameof keyId) "Cache artifact grant key id is required."

        if isNull key then nullArg (nameof key)

        let parameters = key.ExportParameters(false)

        {
            Issuer = CacheArtifactGrantContract.Issuer
            Audience = CacheArtifactGrantContract.Audience
            Algorithm = CacheArtifactGrantContract.Algorithm
            KeyId = keyId
            PublicJwk = P256PublicJwk.Create(encode parameters.Q.X, encode parameters.Q.Y)
        }

    /// Issues one compact grant for exactly five minutes using the current Server process key.
    let issue keyId (key: ECDsa) (request: CacheArtifactGrantIssueRequest) =
        if String.IsNullOrWhiteSpace(keyId) then
            invalidArg (nameof keyId) "Cache artifact grant key id is required."

        if isNull key then nullArg (nameof key)
        if isNull (box request) then nullArg (nameof request)

        if isNull (box request.Artifact) then
            invalidArg (nameof request) "Cache artifact is required."

        if request.HttpMethod
           <> CacheArtifactGrantContract.HttpMethod
           || request.Route <> request.Artifact.Route then
            invalidArg (nameof request) "Cache artifact grant issuance requires the exact supported GET route."

        let issuedAt = request.IssuedAt.ToUnixTimeSeconds()
        let expiresAt = request.IssuedAt.Add(CacheArtifactGrantContract.GrantLifetime)

        let header = { Algorithm = CacheArtifactGrantContract.Algorithm; KeyId = keyId; Type = CacheArtifactGrantContract.TokenType }

        let claims =
            {
                Issuer = CacheArtifactGrantContract.Issuer
                Audience = CacheArtifactGrantContract.Audience
                IssuedAt = issuedAt
                NotBefore = issuedAt
                ExpiresAt = expiresAt.ToUnixTimeSeconds()
                ArtifactKind = CacheArtifactGrantContract.ArtifactKind
                RepositoryId = request.Artifact.RepositoryId
                DirectoryVersionId = request.Artifact.DirectoryVersionId
                Blake3Hash = request.Artifact.Blake3Hash
                Method = request.HttpMethod
                Route = request.Route
            }

        let encodedHeader =
            JsonSerializer.SerializeToUtf8Bytes(header, Constants.JsonSerializerOptions)
            |> encode

        let encodedClaims =
            JsonSerializer.SerializeToUtf8Bytes(claims, Constants.JsonSerializerOptions)
            |> encode

        let signingInput = $"{encodedHeader}.{encodedClaims}"

        let signature =
            key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
            |> encode

        { Grant = CacheArtifactGrant.Create($"{signingInput}.{signature}"); ExpiresAt = expiresAt }

    /// Reads the key id needed to choose a cached validation key before signature verification.
    let tryReadKeyId (grant: CacheArtifactGrant) =
        match tryReadSegments grant with
        | Some (headerSegment, _, _) ->
            match tryDeserialize<WireHeader> headerSegment with
            | Some header when not (String.IsNullOrWhiteSpace(header.KeyId)) -> Some header.KeyId
            | _ -> None
        | None -> None

    /// Reads the declared expiry so a client can replace an expired grant before starting another request.
    let tryReadExpiresAt (grant: CacheArtifactGrant) =
        match tryReadSegments grant with
        | Some (_, claimsSegment, _) ->
            match tryDeserialize<WireClaims> claimsSegment with
            | Some claims ->
                try
                    Some(DateTimeOffset.FromUnixTimeSeconds(claims.ExpiresAt))
                with
                | :? ArgumentOutOfRangeException -> None
            | None -> None
        | None -> None

    /// Reads the untrusted artifact claims needed to select the exact local tuple before full validation.
    let tryReadArtifact (grant: CacheArtifactGrant) =
        match tryReadSegments grant with
        | Some (_, claimsSegment, _) ->
            match tryDeserialize<WireClaims> claimsSegment with
            | Some claims when claims.ArtifactKind = CacheArtifactGrantContract.ArtifactKind ->
                match Guid.TryParse(claims.RepositoryId), Guid.TryParse(claims.DirectoryVersionId) with
                | (true, repositoryId), (true, directoryVersionId) ->
                    try
                        Some(DirectoryVersionZipCacheArtifact.Create(repositoryId, directoryVersionId, claims.Blake3Hash))
                    with
                    | :? ArgumentException -> None
                | _ -> None
            | _ -> None
        | None -> None

    /// Validates signature, issuer, audience, time bounds, HTTP binding, and exact BLAKE3 artifact identity.
    let validate
        (now: DateTimeOffset)
        (validationKey: CacheArtifactGrantValidationKey)
        (request: CacheArtifactGrantValidationRequest)
        (grant: CacheArtifactGrant)
        =
        if
            isNull (box validationKey) || isNull (box request)
            || isNull (box request.Artifact)
        then
            Error CacheArtifactGrantValidationError.Malformed
        elif
            validationKey.Issuer
            <> CacheArtifactGrantContract.Issuer
            || validationKey.Audience
               <> CacheArtifactGrantContract.Audience
            || validationKey.Algorithm
               <> CacheArtifactGrantContract.Algorithm
            || String.IsNullOrWhiteSpace(validationKey.KeyId)
        then
            Error CacheArtifactGrantValidationError.Malformed
        else
            match tryReadSegments grant with
            | None -> Error CacheArtifactGrantValidationError.Malformed
            | Some (headerSegment, claimsSegment, signatureSegment) ->
                match tryDeserialize<WireHeader> headerSegment, tryDeserialize<WireClaims> claimsSegment, tryDecode signatureSegment with
                | Some header, Some claims, Some signature ->
                    if header.Algorithm
                       <> CacheArtifactGrantContract.Algorithm
                       || header.Type
                          <> CacheArtifactGrantContract.TokenType then
                        Error CacheArtifactGrantValidationError.UnsupportedAlgorithm
                    elif header.KeyId <> validationKey.KeyId then
                        Error(CacheArtifactGrantValidationError.UnknownKeyId header.KeyId)
                    else
                        match tryCreateVerifier validationKey.PublicJwk with
                        | None -> Error CacheArtifactGrantValidationError.Malformed
                        | Some verifier ->
                            use verifier = verifier

                            let signatureValid =
                                try
                                    verifier.VerifyData(
                                        Encoding.ASCII.GetBytes($"{headerSegment}.{claimsSegment}"),
                                        signature,
                                        HashAlgorithmName.SHA256,
                                        DSASignatureFormat.IeeeP1363FixedFieldConcatenation
                                    )
                                with
                                | :? CryptographicException
                                | :? ArgumentException
                                | :? PlatformNotSupportedException -> false

                            if not signatureValid then
                                Error CacheArtifactGrantValidationError.InvalidSignature
                            elif claims.Issuer <> CacheArtifactGrantContract.Issuer then
                                Error CacheArtifactGrantValidationError.InvalidIssuer
                            elif claims.Audience
                                 <> CacheArtifactGrantContract.Audience then
                                Error CacheArtifactGrantValidationError.InvalidAudience
                            elif claims.NotBefore <> claims.IssuedAt
                                 || claims.ExpiresAt - claims.IssuedAt
                                    <> int64 CacheArtifactGrantContract.GrantLifetime.TotalSeconds then
                                Error CacheArtifactGrantValidationError.InvalidLifetime
                            else
                                let nowSeconds = now.ToUnixTimeSeconds()

                                let futureLimit =
                                    now
                                        .Add(CacheArtifactGrantContract.MaximumClockDifference)
                                        .ToUnixTimeSeconds()

                                if claims.IssuedAt > futureLimit
                                   || claims.NotBefore > futureLimit then
                                    Error CacheArtifactGrantValidationError.NotYetValid
                                elif nowSeconds >= claims.ExpiresAt then
                                    Error CacheArtifactGrantValidationError.Expired
                                elif claims.Method <> request.HttpMethod then
                                    Error CacheArtifactGrantValidationError.WrongMethod
                                elif claims.Route <> request.Route then
                                    Error CacheArtifactGrantValidationError.WrongRoute
                                elif claims.ArtifactKind
                                     <> CacheArtifactGrantContract.ArtifactKind then
                                    Error CacheArtifactGrantValidationError.WrongArtifactKind
                                elif claims.RepositoryId
                                     <> request.Artifact.RepositoryId then
                                    Error CacheArtifactGrantValidationError.WrongRepository
                                elif claims.DirectoryVersionId
                                     <> request.Artifact.DirectoryVersionId then
                                    Error CacheArtifactGrantValidationError.WrongDirectoryVersion
                                elif claims.Blake3Hash <> request.Artifact.Blake3Hash then
                                    Error CacheArtifactGrantValidationError.WrongBlake3
                                else
                                    Ok()
                | _ -> Error CacheArtifactGrantValidationError.Malformed
