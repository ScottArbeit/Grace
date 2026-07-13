namespace Grace.Types

open Grace.Types.Common
open Grace.Types
open NodaTime
open Orleans
open System
open System.Collections.Generic

/// Contains signed artifact grant contracts shared by Grace Server and Grace Cache validators.
module ArtifactGrant =

    /// Normalizes signed protocol timestamps to the Unix-millisecond precision preserved by every generated client.
    let private toProtocolInstant (instant: Instant) = Instant.FromUnixTimeMilliseconds(instant.ToUnixTimeMilliseconds())

    /// Provides the signed artifact grant TTL, key lifetime, and algorithm constants.
    [<RequireQualifiedAccess>]
    module ArtifactGrantContract =

        /// Identifies the only signing algorithm accepted for V1 artifact grants.
        [<Literal>]
        let Algorithm = "ES256"

        /// Identifies the stable issuer value used in canonical V1 artifact grants.
        [<Literal>]
        let Issuer = "Grace.Server.ArtifactGrant.v1"

        /// The default artifact grant lifetime used when an issuer does not request a shorter lifetime.
        let DefaultGrantTtl = Duration.FromMinutes 5L

        /// The longest artifact grant lifetime accepted by local validation.
        let MaximumAcceptedGrantTtl = Duration.FromMinutes 15L

        /// The active lifetime for one server signing key before a newer key should become current.
        let SigningKeyActiveLifetime = Duration.FromHours 2

        /// The time a cache should keep one validation-key publication before refreshing.
        let ValidationKeyCacheTtl = Duration.FromMinutes 15L

        /// The longest interval between request-proof issuance and expiry.
        let MaximumProofPresentationLifetime = Duration.FromSeconds 30L

        /// The maximum clock difference accepted while admitting a request proof.
        let MaximumProofClockSkew = Duration.FromSeconds 30L

        /// Identifies the P-256 curve used by holder proof-of-possession keys.
        [<Literal>]
        let HolderKeyCurve = "P-256"

    /// Identifies the authenticated requester category bound into a V1 artifact grant.
    [<GenerateSerializer>]
    type ArtifactGrantRequesterPrincipalType =
        | User = 1

    /// Carries the canonical public half of an ephemeral P-256 artifact-request holder key.
    [<CLIMutable; GenerateSerializer>]
    type ArtifactGrantHolderPublicKey =
        {
            [<Id(0u)>]
            Class: string
            [<Id(1u)>]
            Algorithm: string
            [<Id(2u)>]
            Curve: string
            [<Id(3u)>]
            PublicKeyX: string
            [<Id(4u)>]
            PublicKeyY: string
        }

        /// Builds the public holder-key contract from base64url-encoded P-256 coordinates.
        static member Create(publicKeyX: string, publicKeyY: string) =
            {
                Class = nameof ArtifactGrantHolderPublicKey
                Algorithm = ArtifactGrantContract.Algorithm
                Curve = ArtifactGrantContract.HolderKeyCurve
                PublicKeyX = publicKeyX
                PublicKeyY = publicKeyY
            }

    /// Describes the canonical signed-grant header.
    [<CLIMutable; GenerateSerializer>]
    type ArtifactGrantHeader =
        {
            [<Id(0u)>]
            Class: string
            [<Id(1u)>]
            Algorithm: string
            [<Id(2u)>]
            KeyId: string
        }

        /// Builds a header for the configured V1 artifact grant signature algorithm.
        static member Create(keyId: string) = { Class = nameof ArtifactGrantHeader; Algorithm = ArtifactGrantContract.Algorithm; KeyId = keyId }

    /// Describes the authorization payload bound into a signed artifact grant.
    [<CLIMutable; GenerateSerializer>]
    type ArtifactGrantPayload =
        {
            [<Id(0u)>]
            Class: string
            [<Id(1u)>]
            Issuer: string
            [<Id(2u)>]
            RequesterPrincipalType: ArtifactGrantRequesterPrincipalType
            [<Id(3u)>]
            RequesterPrincipalId: string
            [<Id(4u)>]
            HolderKeyThumbprint: string
            [<Id(5u)>]
            CacheId: string
            [<Id(6u)>]
            CacheEndpoint: string
            [<Id(7u)>]
            TargetRootDirectoryVersionId: DirectoryVersionId
            [<Id(8u)>]
            ExecutionMode: MaterializationExecutionMode
            [<Id(9u)>]
            ArtifactIdentities: List<string>
            [<Id(10u)>]
            IssuedAt: Instant
            [<Id(11u)>]
            NotBefore: Instant
            [<Id(12u)>]
            ExpiresAt: Instant
        }

        /// Builds a grant payload with defensive artifact-identity copies for JSON and Orleans callers.
        static member Create
            (
                requesterPrincipalId: string,
                holderKeyThumbprint: string,
                cacheId: string,
                cacheEndpoint: string,
                targetRootDirectoryVersionId: DirectoryVersionId,
                executionMode: MaterializationExecutionMode,
                artifactIdentities: string seq,
                issuedAt: Instant,
                ttl: Duration
            ) =
            let issuedAt = toProtocolInstant issuedAt

            {
                Class = nameof ArtifactGrantPayload
                Issuer = ArtifactGrantContract.Issuer
                RequesterPrincipalType = ArtifactGrantRequesterPrincipalType.User
                RequesterPrincipalId = requesterPrincipalId
                HolderKeyThumbprint = holderKeyThumbprint
                CacheId = cacheId
                CacheEndpoint = cacheEndpoint
                TargetRootDirectoryVersionId = targetRootDirectoryVersionId
                ExecutionMode = executionMode
                ArtifactIdentities = List<string>(artifactIdentities)
                IssuedAt = issuedAt
                NotBefore = issuedAt
                ExpiresAt = issuedAt.Plus ttl
            }

    /// Stores one persisted P-256 private signing key and its complete lifecycle metadata.
    [<CLIMutable; GenerateSerializer>]
    type ArtifactGrantSigningKeyState =
        {
            [<Id(0u)>]
            KeyId: string
            [<Id(1u)>]
            CreatedAt: Instant
            [<Id(2u)>]
            ActiveUntil: Instant
            [<Id(3u)>]
            ExpiresAt: Instant
            [<Id(4u)>]
            PrivateKeyPkcs8: byte array
        }

    /// Stores the deployment-wide active and overlap artifact-grant signing keys.
    [<CLIMutable; GenerateSerializer>]
    type ArtifactGrantSigningKeyRingState =
        {
            [<Id(0u)>]
            Class: string
            [<Id(1u)>]
            Keys: ArtifactGrantSigningKeyState array
        }

        /// Represents an actor state with no signing key created yet.
        static member Empty = { Class = nameof ArtifactGrantSigningKeyRingState; Keys = Array.empty }

    /// Carries the validated inputs the deployment-wide signing-key actor uses to issue one grant.
    [<CLIMutable; GenerateSerializer>]
    type ArtifactGrantSigningRequest =
        {
            [<Id(0u)>]
            RequesterPrincipalType: ArtifactGrantRequesterPrincipalType
            [<Id(1u)>]
            RequesterPrincipalId: string
            [<Id(2u)>]
            HolderPublicKey: ArtifactGrantHolderPublicKey
            [<Id(3u)>]
            CacheId: string
            [<Id(4u)>]
            CacheEndpoint: string
            [<Id(5u)>]
            TargetRootDirectoryVersionId: DirectoryVersionId
            [<Id(6u)>]
            ExecutionMode: MaterializationExecutionMode
            [<Id(7u)>]
            ArtifactIdentities: string array
            [<Id(8u)>]
            RequestedTtl: Duration option
        }

    /// Represents a non-secret failure to issue a requester- and holder-bound grant.
    [<GenerateSerializer>]
    type ArtifactGrantIssueError =
        | InvalidRequesterPrincipal
        | InvalidHolderKeyThumbprint
        | InvalidCacheId
        | InvalidCacheEndpoint
        | InvalidTargetRoot
        | InvalidExecutionMode
        | InvalidArtifactIdentities
        | RequestedTtlTooLong

    /// Contains safe diagnostic text for artifact-grant issuance failures.
    [<RequireQualifiedAccess>]
    module ArtifactGrantIssueError =

        /// Converts an issuance failure to text that contains no grant or signing material.
        let toMessage error =
            match error with
            | InvalidRequesterPrincipal -> "An authenticated user requester is required."
            | InvalidHolderKeyThumbprint -> "A canonical holder-key thumbprint is required."
            | InvalidCacheId -> "CacheId is required."
            | InvalidCacheEndpoint -> "CacheEndpoint is required."
            | InvalidTargetRoot -> "Target root DirectoryVersionId is required."
            | InvalidExecutionMode -> "Artifact grant execution mode is not supported."
            | InvalidArtifactIdentities -> "Artifact grants must bind at least one explicit artifact identity."
            | RequestedTtlTooLong -> "Artifact grant TTL exceeds the accepted maximum."

    /// Represents one signed artifact grant envelope.
    [<CLIMutable; GenerateSerializer>]
    type SignedArtifactGrant =
        {
            [<Id(0u)>]
            Class: string
            [<Id(1u)>]
            Header: ArtifactGrantHeader
            [<Id(2u)>]
            Payload: ArtifactGrantPayload
            [<Id(3u)>]
            Signature: string
        }

        /// Builds a signed artifact grant envelope from a canonical header, payload, and base64url signature.
        static member Create(header: ArtifactGrantHeader, payload: ArtifactGrantPayload, signature: string) =
            { Class = nameof SignedArtifactGrant; Header = header; Payload = payload; Signature = signature }

    /// Returns true only when a grant has the complete structural envelope required before offline signature verification.
    let hasCompleteSignedEnvelope (grant: SignedArtifactGrant) =
        not (isNull (box grant))
        && not (isNull (box grant.Header))
        && not (isNull (box grant.Payload))
        && grant.Class = nameof SignedArtifactGrant
        && grant.Header.Class = nameof ArtifactGrantHeader
        && grant.Header.Algorithm = ArtifactGrantContract.Algorithm
        && not (String.IsNullOrWhiteSpace grant.Header.KeyId)
        && grant.Payload.Class = nameof ArtifactGrantPayload
        && not (String.IsNullOrWhiteSpace grant.Signature)

    /// Describes one published public validation key for artifact grant verification.
    [<CLIMutable; GenerateSerializer>]
    type ArtifactGrantValidationKey =
        {
            [<Id(0u)>]
            Class: string
            [<Id(1u)>]
            KeyId: string
            [<Id(2u)>]
            Algorithm: string
            [<Id(3u)>]
            CreatedAt: Instant
            [<Id(4u)>]
            NotBefore: Instant
            [<Id(5u)>]
            ExpiresAt: Instant
            [<Id(6u)>]
            PublicKeyX: string
            [<Id(7u)>]
            PublicKeyY: string
        }

        /// Builds one validation key using base64url-encoded P-256 public coordinates.
        static member Create(keyId: string, createdAt: Instant, expiresAt: Instant, publicKeyX: string, publicKeyY: string) =
            let createdAt = toProtocolInstant createdAt
            let expiresAt = toProtocolInstant expiresAt

            {
                Class = nameof ArtifactGrantValidationKey
                KeyId = keyId
                Algorithm = ArtifactGrantContract.Algorithm
                CreatedAt = createdAt
                NotBefore = createdAt
                ExpiresAt = expiresAt
                PublicKeyX = publicKeyX
                PublicKeyY = publicKeyY
            }

    /// Publishes current and overlap validation keys for local cache-side grant verification.
    [<CLIMutable; GenerateSerializer>]
    type ArtifactGrantValidationKeySet =
        {
            [<Id(0u)>]
            Class: string
            [<Id(1u)>]
            Issuer: string
            [<Id(2u)>]
            PublishedAt: Instant
            [<Id(3u)>]
            CacheTtl: Duration
            [<Id(4u)>]
            Keys: List<ArtifactGrantValidationKey>
        }

        /// Builds a validation-key publication with a defensive key copy.
        static member Create(publishedAt: Instant, keys: ArtifactGrantValidationKey seq) =
            {
                Class = nameof ArtifactGrantValidationKeySet
                Issuer = ArtifactGrantContract.Issuer
                PublishedAt = toProtocolInstant publishedAt
                CacheTtl = ArtifactGrantContract.ValidationKeyCacheTtl
                Keys = List<ArtifactGrantValidationKey>(keys)
            }

    /// Describes the expected local validation scope for one artifact request.
    [<CLIMutable; GenerateSerializer>]
    type ArtifactGrantValidationRequest =
        {
            [<Id(0u)>]
            Class: string
            [<Id(1u)>]
            CacheId: string
            [<Id(2u)>]
            CacheEndpoint: string
            [<Id(3u)>]
            TargetRootDirectoryVersionId: DirectoryVersionId
            [<Id(4u)>]
            ExecutionMode: MaterializationExecutionMode
            [<Id(5u)>]
            ArtifactIdentity: string
        }

        /// Builds the local validation request that cache runtime code checks before serving one artifact.
        static member Create
            (
                cacheId: string,
                cacheEndpoint: string,
                targetRootDirectoryVersionId: DirectoryVersionId,
                executionMode: MaterializationExecutionMode,
                artifactIdentity: string
            ) =
            {
                Class = nameof ArtifactGrantValidationRequest
                CacheId = cacheId
                CacheEndpoint = cacheEndpoint
                TargetRootDirectoryVersionId = targetRootDirectoryVersionId
                ExecutionMode = executionMode
                ArtifactIdentity = artifactIdentity
            }

    /// Binds local validation to one exact HTTP artifact request at admission time.
    [<CLIMutable; GenerateSerializer>]
    type ArtifactRequestValidationRequest =
        {
            [<Id(0u)>]
            Class: string
            [<Id(1u)>]
            CacheId: string
            [<Id(2u)>]
            CacheEndpoint: string
            [<Id(3u)>]
            TargetRootDirectoryVersionId: DirectoryVersionId
            [<Id(4u)>]
            ExecutionMode: MaterializationExecutionMode
            [<Id(5u)>]
            ArtifactIdentity: string
            [<Id(6u)>]
            HttpMethod: string
            [<Id(7u)>]
            Route: string
        }

        /// Builds the full local admission contract for one artifact HTTP request.
        static member Create
            (
                cacheId: string,
                cacheEndpoint: string,
                targetRootDirectoryVersionId: DirectoryVersionId,
                executionMode: MaterializationExecutionMode,
                artifactIdentity: string,
                httpMethod: string,
                route: string
            ) =
            {
                Class = nameof ArtifactRequestValidationRequest
                CacheId = cacheId
                CacheEndpoint = cacheEndpoint
                TargetRootDirectoryVersionId = targetRootDirectoryVersionId
                ExecutionMode = executionMode
                ArtifactIdentity = artifactIdentity
                HttpMethod = httpMethod
                Route = route
            }

    /// Describes the canonical request-specific statement signed by an ephemeral holder key.
    [<CLIMutable; GenerateSerializer>]
    type ArtifactRequestProofPayload =
        {
            [<Id(0u)>]
            Class: string
            [<Id(1u)>]
            GrantDigest: string
            [<Id(2u)>]
            HttpMethod: string
            [<Id(3u)>]
            NormalizedRoute: string
            [<Id(4u)>]
            ArtifactIdentity: string
            [<Id(5u)>]
            IssuedAt: Instant
            [<Id(6u)>]
            ExpiresAt: Instant
        }

        /// Builds one already-normalized holder proof statement.
        static member Create(grantDigest, httpMethod, normalizedRoute, artifactIdentity, issuedAt, ttl) =
            let issuedAt = toProtocolInstant issuedAt

            {
                Class = nameof ArtifactRequestProofPayload
                GrantDigest = grantDigest
                HttpMethod = httpMethod
                NormalizedRoute = normalizedRoute
                ArtifactIdentity = artifactIdentity
                IssuedAt = issuedAt
                ExpiresAt = issuedAt.Plus ttl
            }

    /// Carries an ephemeral public holder key and its signature over one exact artifact request.
    [<CLIMutable; GenerateSerializer>]
    type SignedArtifactRequestProof =
        {
            [<Id(0u)>]
            Class: string
            [<Id(1u)>]
            HolderPublicKey: ArtifactGrantHolderPublicKey
            [<Id(2u)>]
            Payload: ArtifactRequestProofPayload
            [<Id(3u)>]
            Signature: string
        }

        /// Builds the holder proof envelope from canonical public key, payload, and signature fields.
        static member Create(holderPublicKey, payload, signature) =
            { Class = nameof SignedArtifactRequestProof; HolderPublicKey = holderPublicKey; Payload = payload; Signature = signature }
