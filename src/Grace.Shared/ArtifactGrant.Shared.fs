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
        | WrongCacheService
        | WrongTargetRoot
        | WrongExecutionMode
        | WrongArtifact
        | InvalidArtifactBinding
        | InvalidIssuer
        | InvalidClass
        | InvalidSignature

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
            | WrongCacheService -> "Artifact grant is not bound to this Cache service."
            | WrongTargetRoot -> "Artifact grant is not bound to the requested target root."
            | WrongExecutionMode -> "Artifact grant is not bound to the requested execution mode."
            | WrongArtifact -> "Artifact grant is not bound to the requested artifact."
            | InvalidArtifactBinding -> "Artifact grant must bind at least one explicit artifact identity."
            | InvalidIssuer -> "Artifact grant issuer is not recognized."
            | InvalidClass -> "Artifact grant contract class is invalid."
            | InvalidSignature -> "Artifact grant signature is invalid."

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

        /// Converts an Instant to the numeric canonical representation used for signatures.
        let instantTicks (instant: Instant) = instant.ToUnixTimeTicks()

        /// Converts a Duration to the numeric canonical representation used for signatures.
        let durationTicks (duration: Duration) = duration.ToTimeSpan().Ticks

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
            writer.WriteString("cache", payload.CacheServicePrincipalId)
            writer.WriteString("class", payload.Class)
            writer.WriteNumber("exp", instantTicks payload.ExpiresAt)
            writer.WriteString("issuer", payload.Issuer)
            writer.WriteNumber("iat", instantTicks payload.IssuedAt)
            writer.WriteNumber("mode", int payload.ExecutionMode)
            writer.WriteNumber("nbf", instantTicks payload.NotBefore)
            writer.WriteString("root", payload.TargetRootDirectoryVersionId.ToString("D"))
            writer.WriteEndObject()
            writer.Flush()
            stream.ToArray()

        /// Builds the canonical signing input from a grant header and payload.
        let signingInput (header: ArtifactGrantHeader) (payload: ArtifactGrantPayload) =
            let headerPart = headerBytes header |> Base64Url.encode
            let payloadPart = payloadBytes payload |> Base64Url.encode
            Encoding.ASCII.GetBytes($"{headerPart}.{payloadPart}")

    /// Builds a SHA-256 ECDSA signature for a canonical artifact grant payload.
    let sign (key: ECDsa) (header: ArtifactGrantHeader) (payload: ArtifactGrantPayload) =
        let signature = key.SignData(Canonical.signingInput header payload, HashAlgorithmName.SHA256)
        SignedArtifactGrant.Create(header, payload, Base64Url.encode signature)

    /// Exports a P-256 ECDSA public key as the validation-key DTO used by cache validators.
    let exportValidationKey keyId createdAt expiresAt (key: ECDsa) =
        let parameters = key.ExportParameters false

        ArtifactGrantValidationKey.Create(keyId, createdAt, expiresAt, Base64Url.encode parameters.Q.X, Base64Url.encode parameters.Q.Y)

    /// Creates a P-256 ECDSA verifier from one published validation key.
    let private tryCreateVerifier (validationKey: ArtifactGrantValidationKey) =
        match Base64Url.tryDecode validationKey.PublicKeyX, Base64Url.tryDecode validationKey.PublicKeyY with
        | Some x, Some y ->
            try
                let parameters = ECParameters(Curve = ECCurve.NamedCurves.nistP256, Q = ECPoint(X = x, Y = y))

                Some(ECDsa.Create parameters)
            with
            | :? CryptographicException -> None
        | _ -> None

    /// Validates one artifact grant against a current validation-key publication and expected artifact request.
    let validateWithKeySet (now: Instant) (keySet: ArtifactGrantValidationKeySet) (request: ArtifactGrantValidationRequest) (grant: SignedArtifactGrant) =
        if isNull (box grant) then
            Error MissingGrant
        elif isNull (box grant.Header) then
            Error MissingHeader
        elif isNull (box grant.Payload) then
            Error MissingPayload
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
        elif now < grant.Payload.NotBefore then
            Error GrantNotYetValid
        elif now >= grant.Payload.ExpiresAt then
            Error ExpiredGrant
        elif (grant.Payload.ExpiresAt - grant.Payload.IssuedAt) > ArtifactGrantContract.MaximumAcceptedGrantTtl then
            Error GrantTtlTooLong
        elif not (String.Equals(grant.Payload.CacheServicePrincipalId, request.CacheServicePrincipalId, StringComparison.Ordinal)) then
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
                if isNull (box keySet) || isNull keySet.Keys then
                    None
                else
                    keySet.Keys
                    |> Seq.tryFind (fun key -> String.Equals(key.KeyId, grant.Header.KeyId, StringComparison.Ordinal))

            match key with
            | None -> Error(UnknownKeyId grant.Header.KeyId)
            | Some validationKey when
                validationKey.Algorithm
                <> ArtifactGrantContract.Algorithm
                ->
                Error(UnsupportedAlgorithm validationKey.Algorithm)
            | Some validationKey when now < validationKey.NotBefore -> Error ValidationKeyNotYetValid
            | Some validationKey when now >= validationKey.ExpiresAt -> Error ExpiredValidationKey
            | Some validationKey ->
                match Base64Url.tryDecode grant.Signature, tryCreateVerifier validationKey with
                | Some signature, Some verifier ->
                    use verifier = verifier

                    if verifier.VerifyData(Canonical.signingInput grant.Header grant.Payload, signature, HashAlgorithmName.SHA256) then
                        Ok()
                    else
                        Error InvalidSignature
                | _ -> Error InvalidSignature

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
        if request.ExecutionMode = MaterializationExecutionMode.Direct then
            Ok()
        else
            match grant with
            | None -> Error MissingGrant
            | Some signedGrant -> validateWithRefresh now refreshUnknownKey keySet request signedGrant
