namespace Grace.Shared

open Grace.Shared.ArtifactGrant
open Grace.Types.ArtifactGrant
open Grace.Types.CacheRegistration
open NodaTime
open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json

/// Provides canonical Cache identity proof creation and fail-closed validation helpers.
module CacheRegistrationProof =

    /// Identifies the canonical runtime operation for registration liveness refresh.
    [<Literal>]
    let RefreshOperation = "cache-registration-refresh-v1"

    /// Identifies the canonical runtime operation for Cache public-key rotation.
    [<Literal>]
    let RotateKeyOperation = "cache-registration-rotate-key-v1"

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

    /// Computes the canonical digest bound into a key-rotation proof without including its signature.
    let rotationRequestDigest (request: CacheKeyRotationRequest) =
        digest (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("cache", request.CacheId.ToString("D"))
            writer.WriteString("class", request.Class)
            writer.WritePropertyName("newKey")
            writePublicKey writer request.NewPublicKey
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

    /// Verifies one current Cache key proof against its exact operation, canonical request digest, and 30-second protocol tolerance.
    let validate
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

        if
            not (isValidPublicKey publicKey)
            || isNull (box proof)
            || proof.Class <> nameof SignedCacheRequestProof
            || isNull (box proof.Payload)
            || proof.Payload.Class
               <> nameof CacheRequestProofPayload
            || proof.Payload.CacheId <> cacheId
            || not (String.Equals(proof.Payload.Operation, operation, StringComparison.Ordinal))
            || not (String.Equals(proof.Payload.RequestDigest, requestDigest, StringComparison.Ordinal))
            || String.IsNullOrWhiteSpace proof.Signature
            || not (validTimestamp proof.Payload.IssuedAt)
        then
            false
        else
            match Base64Url.tryDecode publicKey.PublicKeyX, Base64Url.tryDecode publicKey.PublicKeyY, Base64Url.tryDecode proof.Signature with
            | Some x, Some y, Some signature ->
                try
                    use verifier = ECDsa.Create(ECParameters(Curve = ECCurve.NamedCurves.nistP256, Q = ECPoint(X = x, Y = y)))
                    verifier.VerifyData(canonicalProofPayloadBytes proof.Payload, signature, HashAlgorithmName.SHA256)
                with
                | :? CryptographicException
                | :? ArgumentException
                | :? PlatformNotSupportedException -> false
            | _ -> false
