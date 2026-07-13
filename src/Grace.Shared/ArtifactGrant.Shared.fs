namespace Grace.Shared

open Grace.Types.ArtifactGrant
open Grace.Types.MaterializationPlan
open NodaTime
open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json

/// Contains deterministic signing and validation helpers for Grace artifact grants.
module ArtifactGrant =

    /// Represents a local artifact grant validation failure that must fail closed.
    type ArtifactGrantValidationError =
        | MissingGrant
        | MissingHeader
        | MissingPayload
        | MissingKeyId
        | MissingSignature
        | UnsupportedAlgorithm of string
        | UnknownKeyId of string
        | ExpiredGrant
        | GrantNotYetValid
        | GrantTtlTooLong
        | ExpiredValidationKey
        | ValidationKeyNotYetValid
        | InvalidValidationKeySet
        | WrongCacheService
        | WrongTargetRoot
        | WrongExecutionMode
        | WrongArtifact
        | InvalidArtifactBinding
        | InvalidIssuer
        | InvalidClass
        | InvalidSignature
        | InvalidRequesterPrincipal
        | MissingHolderKeyBinding
        | MissingProof
        | MissingHolderPublicKey
        | InvalidHolderPublicKey
        | HolderKeyMismatch
        | MissingProofSignature
        | ProofNotYetValid
        | ExpiredProof
        | ProofLifetimeTooLong
        | WrongGrantDigest
        | WrongProofMethod
        | WrongProofRoute
        | WrongProofArtifact
        | InvalidProofSignature

    /// Represents the outcome of a key refresh attempt for an unknown artifact grant key id.
    type UnknownKeyRefreshDecision =
        | RefreshAttempted of ArtifactGrantValidationKeySet
        | RefreshSkipped

    /// Contains artifact grant validation error text that avoids logging raw grants or signing material.
    [<RequireQualifiedAccess>]
    module ArtifactGrantValidationError =

        /// Converts a validation failure into a non-secret diagnostic message.
        let toMessage error =
            match error with
            | MissingGrant -> "Artifact grant is required."
            | MissingHeader -> "Artifact grant header is required."
            | MissingPayload -> "Artifact grant payload is required."
            | MissingKeyId -> "Artifact grant key id is required."
            | MissingSignature -> "Artifact grant signature is required."
            | UnsupportedAlgorithm algorithm -> $"Artifact grant algorithm '{algorithm}' is not supported."
            | UnknownKeyId _ -> "Artifact grant key id is not recognized."
            | ExpiredGrant -> "Artifact grant has expired."
            | GrantNotYetValid -> "Artifact grant is not valid yet."
            | GrantTtlTooLong -> "Artifact grant TTL exceeds the accepted maximum."
            | ExpiredValidationKey -> "Artifact grant validation key has expired."
            | ValidationKeyNotYetValid -> "Artifact grant validation key is not valid yet."
            | InvalidValidationKeySet -> "Artifact grant validation-key set is malformed."
            | WrongCacheService -> "Artifact grant is not bound to this Cache service."
            | WrongTargetRoot -> "Artifact grant is not bound to the requested target root."
            | WrongExecutionMode -> "Artifact grant is not bound to the requested execution mode."
            | WrongArtifact -> "Artifact grant is not bound to the requested artifact."
            | InvalidArtifactBinding -> "Artifact grant must bind at least one explicit artifact identity."
            | InvalidIssuer -> "Artifact grant issuer is not recognized."
            | InvalidClass -> "Artifact grant contract class is invalid."
            | InvalidSignature -> "Artifact grant signature is invalid."
            | InvalidRequesterPrincipal -> "Artifact grant is not bound to an authenticated user requester."
            | MissingHolderKeyBinding -> "Artifact grant holder-key binding is required."
            | MissingProof -> "Artifact request holder proof is required."
            | MissingHolderPublicKey -> "Artifact request holder public key is required."
            | InvalidHolderPublicKey -> "Artifact request holder public key is invalid."
            | HolderKeyMismatch -> "Artifact request holder key does not match the signed grant."
            | MissingProofSignature -> "Artifact request proof signature is required."
            | ProofNotYetValid -> "Artifact request proof is not valid yet."
            | ExpiredProof -> "Artifact request proof has expired."
            | ProofLifetimeTooLong -> "Artifact request proof lifetime exceeds the accepted maximum."
            | WrongGrantDigest -> "Artifact request proof is not bound to this grant."
            | WrongProofMethod -> "Artifact request proof is not bound to this HTTP method."
            | WrongProofRoute -> "Artifact request proof is not bound to this route."
            | WrongProofArtifact -> "Artifact request proof is not bound to this artifact."
            | InvalidProofSignature -> "Artifact request proof signature is invalid."

    /// Provides base64url encoding used by signed artifact grant envelopes and validation keys.
    [<RequireQualifiedAccess>]
    module Base64Url =

        /// Encodes bytes using unpadded base64url.
        let encode (bytes: byte array) =
            Convert.ToBase64String bytes
            |> fun value ->
                value
                    .TrimEnd('=')
                    .Replace('+', '-')
                    .Replace('/', '_')

        /// Decodes unpadded base64url into bytes.
        let tryDecode (value: string) =
            if String.IsNullOrWhiteSpace value then
                None
            else
                try
                    let padded =
                        value.Replace('-', '+').Replace('_', '/')
                        |> fun normalized ->
                            match normalized.Length % 4 with
                            | 0 -> normalized
                            | 2 -> normalized + "=="
                            | 3 -> normalized + "="
                            | _ -> normalized

                    Some(Convert.FromBase64String padded)
                with
                | :? FormatException -> None

    /// Provides canonical JSON encoding for the signed artifact grant header and payload.
    [<RequireQualifiedAccess>]
    module Canonical =

        /// Converts an Instant to the Unix-millisecond representation preserved by every generated client.
        let instantMilliseconds (instant: Instant) = instant.ToUnixTimeMilliseconds()

        /// Converts a Duration to the numeric canonical representation used for signatures.
        let durationTicks (duration: Duration) = duration.ToTimeSpan().Ticks

        /// Normalizes an HTTP method for request-proof signing and admission checks.
        let httpMethod (value: string) = if isNull value then String.Empty else value.Trim().ToUpperInvariant()

        /// Normalizes an HTTP route path without treating query or fragment text as artifact identity.
        let route (value: string) =
            if String.IsNullOrWhiteSpace value then
                String.Empty
            else
                let trimmed = value.Trim()
                let delimiterIndex = trimmed.IndexOfAny([| '?'; '#' |])

                let path =
                    if delimiterIndex < 0 then trimmed
                    elif delimiterIndex = 0 then String.Empty
                    else trimmed[.. delimiterIndex - 1]

                if String.IsNullOrEmpty path then String.Empty
                elif path.StartsWith('/') then path
                else $"/{path}"

        /// Writes one string array property in stable order.
        let private writeStringArray (writer: Utf8JsonWriter) (propertyName: string) (values: string seq) =
            writer.WritePropertyName propertyName
            writer.WriteStartArray()

            values |> Seq.iter writer.WriteStringValue

            writer.WriteEndArray()

        /// Encodes a grant header as canonical UTF-8 JSON bytes.
        let headerBytes (header: ArtifactGrantHeader) =
            use stream = new IO.MemoryStream()

            use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = false, SkipValidation = false))

            writer.WriteStartObject()
            writer.WriteString("alg", header.Algorithm)
            writer.WriteString("class", header.Class)
            writer.WriteString("kid", header.KeyId)
            writer.WriteEndObject()
            writer.Flush()
            stream.ToArray()

        /// Encodes a grant payload as canonical UTF-8 JSON bytes.
        let payloadBytes (payload: ArtifactGrantPayload) =
            use stream = new IO.MemoryStream()

            use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = false, SkipValidation = false))

            writer.WriteStartObject()
            writeStringArray writer "artifacts" payload.ArtifactIdentities
            writer.WriteString("cache", payload.CacheId)
            writer.WriteString("class", payload.Class)
            writer.WriteNumber("exp", instantMilliseconds payload.ExpiresAt)
            writer.WriteString("holder", payload.HolderKeyThumbprint)
            writer.WriteString("issuer", payload.Issuer)
            writer.WriteNumber("iat", instantMilliseconds payload.IssuedAt)
            writer.WriteNumber("mode", int payload.ExecutionMode)
            writer.WriteNumber("nbf", instantMilliseconds payload.NotBefore)
            writer.WriteString("requesterId", payload.RequesterPrincipalId)
            writer.WriteNumber("requesterType", int payload.RequesterPrincipalType)
            writer.WriteString("root", payload.TargetRootDirectoryVersionId.ToString("D"))
            writer.WriteEndObject()
            writer.Flush()
            stream.ToArray()

        /// Builds the canonical signing input from a grant header and payload.
        let signingInput (header: ArtifactGrantHeader) (payload: ArtifactGrantPayload) =
            let headerPart = headerBytes header |> Base64Url.encode
            let payloadPart = payloadBytes payload |> Base64Url.encode
            Encoding.ASCII.GetBytes($"{headerPart}.{payloadPart}")

        /// Encodes one holder public key for a stable SHA-256 thumbprint.
        let holderPublicKeyBytes (holderPublicKey: ArtifactGrantHolderPublicKey) =
            use stream = new IO.MemoryStream()
            use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = false, SkipValidation = false))
            writer.WriteStartObject()
            writer.WriteString("alg", holderPublicKey.Algorithm)
            writer.WriteString("class", holderPublicKey.Class)
            writer.WriteString("crv", holderPublicKey.Curve)
            writer.WriteString("x", holderPublicKey.PublicKeyX)
            writer.WriteString("y", holderPublicKey.PublicKeyY)
            writer.WriteEndObject()
            writer.Flush()
            stream.ToArray()

        /// Encodes one request-proof payload as canonical UTF-8 JSON bytes.
        let requestProofPayloadBytes (payload: ArtifactRequestProofPayload) =
            use stream = new IO.MemoryStream()
            use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = false, SkipValidation = false))
            writer.WriteStartObject()
            writer.WriteString("artifact", payload.ArtifactIdentity)
            writer.WriteString("class", payload.Class)
            writer.WriteNumber("exp", instantMilliseconds payload.ExpiresAt)
            writer.WriteString("grant", payload.GrantDigest)
            writer.WriteNumber("iat", instantMilliseconds payload.IssuedAt)
            writer.WriteString("method", payload.HttpMethod)
            writer.WriteString("route", payload.NormalizedRoute)
            writer.WriteEndObject()
            writer.Flush()
            stream.ToArray()

    /// Builds a SHA-256 ECDSA signature for a canonical artifact grant payload.
    let sign (key: ECDsa) (header: ArtifactGrantHeader) (payload: ArtifactGrantPayload) =
        let signature = key.SignData(Canonical.signingInput header payload, HashAlgorithmName.SHA256)
        SignedArtifactGrant.Create(header, payload, Base64Url.encode signature)

    /// Exports a P-256 ECDSA public key as the validation-key DTO used by cache validators.
    let exportValidationKey keyId createdAt expiresAt (key: ECDsa) =
        let parameters = key.ExportParameters false

        ArtifactGrantValidationKey.Create(keyId, createdAt, expiresAt, Base64Url.encode parameters.Q.X, Base64Url.encode parameters.Q.Y)

    /// Exports the public half of an ephemeral P-256 holder key.
    let exportHolderPublicKey (key: ECDsa) =
        let parameters = key.ExportParameters false
        ArtifactGrantHolderPublicKey.Create(Base64Url.encode parameters.Q.X, Base64Url.encode parameters.Q.Y)

    /// Computes the canonical SHA-256 thumbprint bound into an artifact grant.
    let holderKeyThumbprint (holderPublicKey: ArtifactGrantHolderPublicKey) =
        Canonical.holderPublicKeyBytes holderPublicKey
        |> SHA256.HashData
        |> Base64Url.encode

    /// Computes the digest that binds one holder proof to the complete signed grant envelope.
    let grantDigest (grant: SignedArtifactGrant) =
        let signingInput = Canonical.signingInput grant.Header grant.Payload
        let signature = Encoding.ASCII.GetBytes(if isNull grant.Signature then String.Empty else grant.Signature)

        let bytes =
            Array.concat [ signingInput
                           [| byte '.' |]
                           signature ]

        bytes |> SHA256.HashData |> Base64Url.encode

    /// Creates one request-specific proof from an ephemeral holder private key.
    let createRequestProof
        (holderPrivateKey: ECDsa)
        (holderPublicKey: ArtifactGrantHolderPublicKey)
        (grant: SignedArtifactGrant)
        (httpMethod: string)
        (route: string)
        (artifactIdentity: string)
        (issuedAt: Instant)
        (ttl: Duration)
        =
        let payload =
            ArtifactRequestProofPayload.Create(grantDigest grant, Canonical.httpMethod httpMethod, Canonical.route route, artifactIdentity, issuedAt, ttl)

        let signature = holderPrivateKey.SignData(Canonical.requestProofPayloadBytes payload, HashAlgorithmName.SHA256)
        SignedArtifactRequestProof.Create(holderPublicKey, payload, Base64Url.encode signature)

    /// Creates a P-256 ECDSA verifier from one published validation key.
    let private tryCreateVerifier (validationKey: ArtifactGrantValidationKey) =
        if isNull (box validationKey) then
            None
        else
            match Base64Url.tryDecode validationKey.PublicKeyX, Base64Url.tryDecode validationKey.PublicKeyY with
            | Some x, Some y when
                x.Length = 32
                && y.Length = 32
                && Base64Url.encode x = validationKey.PublicKeyX
                && Base64Url.encode y = validationKey.PublicKeyY
                ->
                try
                    let parameters = ECParameters(Curve = ECCurve.NamedCurves.nistP256, Q = ECPoint(X = x, Y = y))

                    Some(ECDsa.Create parameters)
                with
                | :? CryptographicException
                | :? ArgumentException
                | :? PlatformNotSupportedException -> None
            | _ -> None

    /// Returns true only when an entire validation-key publication is canonical and importable.
    let private isValidValidationKeySet (keySet: ArtifactGrantValidationKeySet) =
        if isNull (box keySet)
           || keySet.Class
              <> nameof ArtifactGrantValidationKeySet
           || keySet.Issuer <> ArtifactGrantContract.Issuer
           || keySet.CacheTtl <= Duration.Zero
           || isNull keySet.Keys then
            false
        else
            let keys = keySet.Keys |> Seq.toArray
            let canonicalLifetime = ArtifactGrantContract.SigningKeyActiveLifetime.Plus ArtifactGrantContract.MaximumAcceptedGrantTtl

            let structurallyValid =
                keys
                |> Array.forall (fun key ->
                    not (isNull (box key))
                    && key.Class = nameof ArtifactGrantValidationKey
                    && not (String.IsNullOrWhiteSpace key.KeyId)
                    && key.Algorithm = ArtifactGrantContract.Algorithm
                    && key.CreatedAt = key.NotBefore
                    && key.ExpiresAt - key.NotBefore = canonicalLifetime
                    && not (String.IsNullOrWhiteSpace key.PublicKeyX)
                    && not (String.IsNullOrWhiteSpace key.PublicKeyY)
                    && match tryCreateVerifier key with
                       | Some verifier ->
                           verifier.Dispose()
                           true
                       | None -> false)

            structurallyValid
            && (keys
                |> Array.map (fun key -> key.KeyId)
                |> Array.distinct
                |> Array.length) = keys.Length

    /// Contains malformed signature and key material inside the typed validation contract.
    let private verifyData (verifier: ECDsa) (data: byte array) (signature: byte array) =
        try
            verifier.VerifyData(data, signature, HashAlgorithmName.SHA256)
        with
        | :? CryptographicException
        | :? ArgumentException
        | :? PlatformNotSupportedException -> false

    /// Detects a future signed timestamp beyond the accepted tolerance without overflowing Instant arithmetic.
    let private isBeyondFutureTolerance (now: Instant) (timestamp: Instant) (tolerance: Duration) = timestamp > now && timestamp - now > tolerance

    /// Detects a past signed timestamp beyond the accepted tolerance without overflowing Instant arithmetic.
    let private isBeyondPastTolerance (now: Instant) (timestamp: Instant) (tolerance: Duration) = now > timestamp && now - timestamp > tolerance

    /// Creates a verifier only for a canonical P-256 holder public key.
    let private tryCreateHolderVerifier (holderPublicKey: ArtifactGrantHolderPublicKey) =
        if isNull (box holderPublicKey)
           || holderPublicKey.Class
              <> nameof ArtifactGrantHolderPublicKey
           || holderPublicKey.Algorithm
              <> ArtifactGrantContract.Algorithm
           || holderPublicKey.Curve
              <> ArtifactGrantContract.HolderKeyCurve then
            None
        else
            match Base64Url.tryDecode holderPublicKey.PublicKeyX, Base64Url.tryDecode holderPublicKey.PublicKeyY with
            | Some x, Some y when
                x.Length = 32
                && y.Length = 32
                && Base64Url.encode x = holderPublicKey.PublicKeyX
                && Base64Url.encode y = holderPublicKey.PublicKeyY
                ->
                try
                    let parameters = ECParameters(Curve = ECCurve.NamedCurves.nistP256, Q = ECPoint(X = x, Y = y))
                    Some(ECDsa.Create parameters)
                with
                | :? CryptographicException
                | :? ArgumentException
                | :? PlatformNotSupportedException -> None
            | _ -> None

    /// Returns true only for a canonical, importable P-256 holder public key.
    let isValidHolderPublicKey holderPublicKey =
        match tryCreateHolderVerifier holderPublicKey with
        | Some verifier ->
            verifier.Dispose()
            true
        | None -> false

    /// Validates one artifact grant against a current validation-key publication and expected artifact request.
    let validateWithKeySet (now: Instant) (keySet: ArtifactGrantValidationKeySet) (request: ArtifactGrantValidationRequest) (grant: SignedArtifactGrant) =
        let clockTolerance = ArtifactGrantContract.MaximumProofClockSkew

        if not (isValidValidationKeySet keySet) then
            Error InvalidValidationKeySet
        elif isNull (box grant) then
            Error MissingGrant
        elif isNull (box grant.Header) then
            Error MissingHeader
        elif isNull (box grant.Payload) then
            Error MissingPayload
        elif isNull (box request)
             || request.Class
                <> nameof ArtifactGrantValidationRequest
             || String.IsNullOrWhiteSpace request.CacheId
             || request.TargetRootDirectoryVersionId = Guid.Empty
             || not (Grace.Types.MaterializationPlan.Validation.isSupportedExecutionMode request.ExecutionMode)
             || String.IsNullOrWhiteSpace request.ArtifactIdentity then
            Error InvalidClass
        elif String.IsNullOrWhiteSpace grant.Header.KeyId then
            Error MissingKeyId
        elif String.IsNullOrWhiteSpace grant.Signature then
            Error MissingSignature
        elif grant.Class <> nameof SignedArtifactGrant
             || grant.Header.Class <> nameof ArtifactGrantHeader
             || grant.Payload.Class <> nameof ArtifactGrantPayload then
            Error InvalidClass
        elif not (String.Equals(grant.Header.Algorithm, ArtifactGrantContract.Algorithm, StringComparison.Ordinal)) then
            Error(UnsupportedAlgorithm grant.Header.Algorithm)
        elif not (String.Equals(grant.Payload.Issuer, ArtifactGrantContract.Issuer, StringComparison.Ordinal)) then
            Error InvalidIssuer
        elif grant.Payload.RequesterPrincipalType
             <> ArtifactGrantRequesterPrincipalType.User
             || String.IsNullOrWhiteSpace grant.Payload.RequesterPrincipalId then
            Error InvalidRequesterPrincipal
        elif String.IsNullOrWhiteSpace grant.Payload.HolderKeyThumbprint then
            Error MissingHolderKeyBinding
        elif grant.Payload.NotBefore <> grant.Payload.IssuedAt then
            Error GrantTtlTooLong
        elif grant.Payload.ExpiresAt <= grant.Payload.IssuedAt then
            Error GrantTtlTooLong
        elif isBeyondFutureTolerance now grant.Payload.NotBefore clockTolerance then
            Error GrantNotYetValid
        elif isBeyondPastTolerance now grant.Payload.ExpiresAt clockTolerance then
            Error ExpiredGrant
        elif (grant.Payload.ExpiresAt - grant.Payload.IssuedAt) > ArtifactGrantContract.MaximumAcceptedGrantTtl then
            Error GrantTtlTooLong
        elif not (String.Equals(grant.Payload.CacheId, request.CacheId, StringComparison.Ordinal)) then
            Error WrongCacheService
        elif grant.Payload.TargetRootDirectoryVersionId
             <> request.TargetRootDirectoryVersionId then
            Error WrongTargetRoot
        elif grant.Payload.ExecutionMode = MaterializationExecutionMode.Direct then
            Error WrongExecutionMode
        elif grant.Payload.ExecutionMode
             <> request.ExecutionMode then
            Error WrongExecutionMode
        elif isNull grant.Payload.ArtifactIdentities
             || grant.Payload.ArtifactIdentities.Count = 0
             || grant.Payload.ArtifactIdentities
                |> Seq.exists String.IsNullOrWhiteSpace then
            Error InvalidArtifactBinding
        elif not (grant.Payload.ArtifactIdentities.Contains request.ArtifactIdentity) then
            Error WrongArtifact
        else
            let key =
                keySet.Keys
                |> Seq.tryFind (fun key -> String.Equals(key.KeyId, grant.Header.KeyId, StringComparison.Ordinal))

            match key with
            | None -> Error(UnknownKeyId grant.Header.KeyId)
            | Some validationKey when isBeyondFutureTolerance now validationKey.NotBefore clockTolerance -> Error ValidationKeyNotYetValid
            | Some validationKey when isBeyondPastTolerance now validationKey.ExpiresAt clockTolerance -> Error ExpiredValidationKey
            | Some validationKey ->
                match Base64Url.tryDecode grant.Signature, tryCreateVerifier validationKey with
                | Some signature, Some verifier ->
                    use verifier = verifier

                    if verifyData verifier (Canonical.signingInput grant.Header grant.Payload) signature then
                        Ok()
                    else
                        Error InvalidSignature
                | _ -> Error InvalidSignature

    /// Validates a grant and one fresh holder proof at the admission boundary for an exact HTTP artifact request.
    let validateArtifactRequestWithKeySet
        (now: Instant)
        (keySet: ArtifactGrantValidationKeySet)
        (request: ArtifactRequestValidationRequest)
        (grant: SignedArtifactGrant option)
        (proof: SignedArtifactRequestProof option)
        =
        if
            isNull (box request)
            || request.Class
               <> nameof ArtifactRequestValidationRequest
            || String.IsNullOrWhiteSpace request.CacheId
            || request.TargetRootDirectoryVersionId = Guid.Empty
            || not (Grace.Types.MaterializationPlan.Validation.isSupportedExecutionMode request.ExecutionMode)
            || String.IsNullOrWhiteSpace request.ArtifactIdentity
            || String.IsNullOrWhiteSpace request.HttpMethod
            || String.IsNullOrWhiteSpace(Canonical.route request.Route)
        then
            Error InvalidClass
        elif request.ExecutionMode = MaterializationExecutionMode.Direct then
            Ok()
        elif not (isValidValidationKeySet keySet) then
            Error InvalidValidationKeySet
        else
            match grant, proof with
            | None, _ -> Error MissingGrant
            | _, None -> Error MissingProof
            | Some signedGrant, _ when isNull (box signedGrant) -> Error MissingGrant
            | _, Some signedProof when isNull (box signedProof) -> Error MissingProof
            | Some signedGrant, Some signedProof ->
                let grantRequest =
                    ArtifactGrantValidationRequest.Create(
                        request.CacheId,
                        request.TargetRootDirectoryVersionId,
                        request.ExecutionMode,
                        request.ArtifactIdentity
                    )

                match validateWithKeySet now keySet grantRequest signedGrant with
                | Error error -> Error error
                | Ok () when isNull (box signedProof.HolderPublicKey) -> Error MissingHolderPublicKey
                | Ok () when isNull (box signedProof.Payload) -> Error MissingPayload
                | Ok () when
                    signedProof.Class
                    <> nameof SignedArtifactRequestProof
                    || signedProof.Payload.Class
                       <> nameof ArtifactRequestProofPayload
                    ->
                    Error InvalidClass
                | Ok () when String.IsNullOrWhiteSpace signedProof.Signature -> Error MissingProofSignature
                | Ok () ->
                    match tryCreateHolderVerifier signedProof.HolderPublicKey with
                    | None -> Error InvalidHolderPublicKey
                    | Some verifier ->
                        use verifier = verifier

                        if
                            not
                                (
                                    String.Equals(
                                        holderKeyThumbprint signedProof.HolderPublicKey,
                                        signedGrant.Payload.HolderKeyThumbprint,
                                        StringComparison.Ordinal
                                    )
                                )
                        then
                            Error HolderKeyMismatch
                        elif signedProof.Payload.ExpiresAt
                             <= signedProof.Payload.IssuedAt
                             || signedProof.Payload.ExpiresAt
                                - signedProof.Payload.IssuedAt > ArtifactGrantContract.MaximumProofPresentationLifetime then
                            Error ProofLifetimeTooLong
                        elif isBeyondFutureTolerance now signedProof.Payload.IssuedAt ArtifactGrantContract.MaximumProofClockSkew then
                            Error ProofNotYetValid
                        elif isBeyondPastTolerance now signedProof.Payload.ExpiresAt ArtifactGrantContract.MaximumProofClockSkew then
                            Error ExpiredProof
                        elif not (String.Equals(signedProof.Payload.GrantDigest, grantDigest signedGrant, StringComparison.Ordinal)) then
                            Error WrongGrantDigest
                        elif
                            String.IsNullOrEmpty(Canonical.httpMethod request.HttpMethod)
                            || not (String.Equals(signedProof.Payload.HttpMethod, Canonical.httpMethod request.HttpMethod, StringComparison.Ordinal))
                        then
                            Error WrongProofMethod
                        elif
                            String.IsNullOrEmpty(Canonical.route request.Route)
                            || not (String.Equals(signedProof.Payload.NormalizedRoute, Canonical.route request.Route, StringComparison.Ordinal))
                        then
                            Error WrongProofRoute
                        elif not (String.Equals(signedProof.Payload.ArtifactIdentity, request.ArtifactIdentity, StringComparison.Ordinal)) then
                            Error WrongProofArtifact
                        else
                            match Base64Url.tryDecode signedProof.Signature with
                            | Some signature when verifyData verifier (Canonical.requestProofPayloadBytes signedProof.Payload) signature -> Ok()
                            | _ -> Error InvalidProofSignature

    /// Validates with one throttled refresh attempt when a grant references an unknown key id.
    let validateWithRefresh
        (now: Instant)
        (refreshUnknownKey: string -> UnknownKeyRefreshDecision)
        (keySet: ArtifactGrantValidationKeySet)
        (request: ArtifactGrantValidationRequest)
        (grant: SignedArtifactGrant)
        =
        match validateWithKeySet now keySet request grant with
        | Error (UnknownKeyId keyId) ->
            match refreshUnknownKey keyId with
            | RefreshAttempted refreshedKeySet -> validateWithKeySet now refreshedKeySet request grant
            | RefreshSkipped -> Error(UnknownKeyId keyId)
        | result -> result

    /// Skips grant validation for Direct mode and otherwise validates one required signed grant.
    let validateRequiredForCacheMode
        (now: Instant)
        (refreshUnknownKey: string -> UnknownKeyRefreshDecision)
        (keySet: ArtifactGrantValidationKeySet)
        (request: ArtifactGrantValidationRequest)
        (grant: SignedArtifactGrant option)
        =
        if isNull (box request)
           || request.Class
              <> nameof ArtifactGrantValidationRequest
           || String.IsNullOrWhiteSpace request.CacheId
           || request.TargetRootDirectoryVersionId = Guid.Empty
           || not (Grace.Types.MaterializationPlan.Validation.isSupportedExecutionMode request.ExecutionMode)
           || String.IsNullOrWhiteSpace request.ArtifactIdentity then
            Error InvalidClass
        elif request.ExecutionMode = MaterializationExecutionMode.Direct then
            Ok()
        elif not (isValidValidationKeySet keySet) then
            Error InvalidValidationKeySet
        else
            match grant with
            | None -> Error MissingGrant
            | Some signedGrant when isNull (box signedGrant) -> Error MissingGrant
            | Some signedGrant -> validateWithRefresh now refreshUnknownKey keySet request signedGrant
