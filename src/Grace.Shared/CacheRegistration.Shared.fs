namespace Grace.Shared

open Grace.Shared.ArtifactGrant
open Grace.Types.Common
open Grace.Types.ArtifactGrant
open Grace.Types.CacheRegistration
open NodaTime
open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json

/// Identifies the precise reason a Cache proof cannot authenticate the requested operation.
type CacheProofValidationFailure =
    | InvalidPublicKey
    | InvalidProofShape
    | WrongCacheId
    | WrongOperation
    | WrongRequestDigest
    | InvalidSignature
    | TimestampOutsideTolerance

/// Provides canonical Cache identity proof creation and fail-closed validation helpers.
module CacheRegistrationProof =

    /// Identifies the canonical runtime operation for registration liveness refresh.
    [<Literal>]
    let RefreshOperation = "cache-registration-refresh-v1"

    /// Identifies the canonical active-key proof used to submit or replay the one candidate public key.
    [<Literal>]
    let SubmitCandidateOperation = "cache-registration-submit-candidate-v1"

    /// Names the sole GraceError property that permits a Cache candidate submission to retry with a newly signed proof.
    [<Literal>]
    let CandidateProofTimestampStalePropertyKey = "cacheCandidateProofTimestamp"

    /// Marks a candidate proof whose otherwise valid timestamp falls outside the existing protocol tolerance.
    [<Literal>]
    let CandidateProofTimestampStalePropertyValue = "stale"

    /// Builds the exact GraceError payload that allows only a stale candidate timestamp to be retried with a newly signed proof.
    let candidateProofTimestampStaleError correlationId =
        let error = GraceError.Create "Cache candidate-submission proof is stale." correlationId
        error.enhance (CandidateProofTimestampStalePropertyKey, CandidateProofTimestampStalePropertyValue)

    /// Writes one Cache identity public key in canonical field order.
    let private writePublicKey (writer: Utf8JsonWriter) (key: CacheIdentityPublicKey) =
        writer.WriteStartObject()
        writer.WriteString("alg", key.Algorithm)
        writer.WriteString("class", key.Class)
        writer.WriteString("crv", key.Curve)
        writer.WriteString("x", key.PublicKeyX)
        writer.WriteString("y", key.PublicKeyY)
        writer.WriteEndObject()

    /// Hashes canonical request bytes with SHA-256 and encodes the result as base64url.
    let private digest (write: Utf8JsonWriter -> unit) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = false, SkipValidation = false))
        write writer
        writer.Flush()

        stream.ToArray()
        |> SHA256.HashData
        |> Base64Url.encode

    /// Computes the canonical digest bound into a refresh proof without including its signature.
    let refreshRequestDigest (request: CacheRegistrationRefreshRequest) =
        digest (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("cache", request.CacheId.ToString("D"))
            writer.WriteString("class", request.Class)
            writer.WriteString("endpoint", request.Endpoint)
            writer.WriteNumber("health", int request.Health)
            writer.WriteNumber("observedAt", request.ObservedAt.ToUnixTimeMilliseconds())
            writer.WriteBoolean("prefetch", request.PrefetchSupported)
            writer.WriteString("protocol", request.ProtocolVersion)
            writer.WriteString("software", request.SoftwareVersion)
            writer.WriteEndObject())

    /// Computes the canonical digest bound into an active-key candidate-submission proof without including its signature.
    let candidateRequestDigest (request: CacheKeyCandidateRequest) =
        digest (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("cache", request.CacheId.ToString("D"))
            writer.WriteString("class", request.Class)
            writer.WritePropertyName("candidate")
            writePublicKey writer request.CandidatePublicKey
            writer.WriteNumber("intervalMinutes", request.RotationIntervalMinutes)
            writer.WriteBoolean("startup", request.IsStartup)
            writer.WriteEndObject())

    /// Encodes the signed Cache proof payload in canonical JSON field order.
    let canonicalProofPayloadBytes (payload: CacheRequestProofPayload) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = false, SkipValidation = false))
        writer.WriteStartObject()
        writer.WriteString("cache", payload.CacheId.ToString("D"))
        writer.WriteString("class", payload.Class)
        writer.WriteNumber("iat", payload.IssuedAt.ToUnixTimeMilliseconds())
        writer.WriteString("operation", payload.Operation)
        writer.WriteString("request", payload.RequestDigest)
        writer.WriteEndObject()
        writer.Flush()
        stream.ToArray()

    /// Returns true only for a canonical importable P-256 Cache identity public key.
    let isValidPublicKey (key: CacheIdentityPublicKey) =
        if isNull (box key)
           || key.Class <> nameof CacheIdentityPublicKey
           || key.Algorithm <> ArtifactGrantContract.Algorithm
           || key.Curve <> ArtifactGrantContract.HolderKeyCurve then
            false
        else
            match Base64Url.tryDecode key.PublicKeyX, Base64Url.tryDecode key.PublicKeyY with
            | Some x, Some y when
                x.Length = 32
                && y.Length = 32
                && Base64Url.encode x = key.PublicKeyX
                && Base64Url.encode y = key.PublicKeyY
                ->
                try
                    use _ = ECDsa.Create(ECParameters(Curve = ECCurve.NamedCurves.nistP256, Q = ECPoint(X = x, Y = y)))
                    true
                with
                | :? CryptographicException
                | :? ArgumentException
                | :? PlatformNotSupportedException -> false
            | _ -> false

    /// Creates a Cache proof for test vectors and future Cache-host callers that own the private identity key.
    let createProof (privateKey: ECDsa) (cacheId: Guid) (operation: string) (requestDigest: string) (issuedAt: Instant) =
        let payload = CacheRequestProofPayload.Create(cacheId, operation, requestDigest, issuedAt)
        let signature = privateKey.SignData(canonicalProofPayloadBytes payload, HashAlgorithmName.SHA256)
        SignedCacheRequestProof.Create(payload, Base64Url.encode signature)

    /// Classifies one Cache proof after all identity bindings and the signature are proven before timestamp tolerance is considered.
    let classify
        (now: Instant)
        (publicKey: CacheIdentityPublicKey)
        (cacheId: Guid)
        (operation: string)
        (requestDigest: string)
        (proof: SignedCacheRequestProof)
        =
        let validTimestamp timestamp =
            let tolerance = ArtifactGrantContract.MaximumProofClockSkew
            let earliestAcceptedTimestamp = now.Minus tolerance
            let latestAcceptedTimestamp = now.Plus tolerance

            timestamp >= earliestAcceptedTimestamp
            && timestamp <= latestAcceptedTimestamp

        if not (isValidPublicKey publicKey) then
            Error InvalidPublicKey
        elif isNull (box proof)
             || proof.Class <> nameof SignedCacheRequestProof
             || isNull (box proof.Payload)
             || proof.Payload.Class
                <> nameof CacheRequestProofPayload
             || String.IsNullOrWhiteSpace proof.Signature then
            Error InvalidProofShape
        elif proof.Payload.CacheId <> cacheId then
            Error WrongCacheId
        elif not (String.Equals(proof.Payload.Operation, operation, StringComparison.Ordinal)) then
            Error WrongOperation
        elif not (String.Equals(proof.Payload.RequestDigest, requestDigest, StringComparison.Ordinal)) then
            Error WrongRequestDigest
        else
            match Base64Url.tryDecode publicKey.PublicKeyX, Base64Url.tryDecode publicKey.PublicKeyY, Base64Url.tryDecode proof.Signature with
            | Some x, Some y, Some signature ->
                try
                    use verifier = ECDsa.Create(ECParameters(Curve = ECCurve.NamedCurves.nistP256, Q = ECPoint(X = x, Y = y)))

                    if not (verifier.VerifyData(canonicalProofPayloadBytes proof.Payload, signature, HashAlgorithmName.SHA256)) then
                        Error InvalidSignature
                    elif not (validTimestamp proof.Payload.IssuedAt) then
                        Error TimestampOutsideTolerance
                    else
                        Ok()
                with
                | :? CryptographicException
                | :? ArgumentException
                | :? PlatformNotSupportedException -> Error InvalidSignature
            | _ -> Error InvalidSignature

    /// Verifies one current Cache key proof against its exact operation, canonical request digest, and 30-second protocol tolerance.
    let validate now publicKey cacheId operation requestDigest proof =
        classify now publicKey cacheId operation requestDigest proof
        |> Result.isOk
