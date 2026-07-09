namespace Grace.Types

open Grace.Types.Common
open Grace.Types.MaterializationPlan
open NodaTime
open Orleans
open System.Collections.Generic

/// Contains signed artifact grant contracts shared by Grace Server and Grace Cache validators.
module ArtifactGrant =

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

    /// Describes the canonical signed-grant header.
    [<CLIMutable; GenerateSerializer>]
    type ArtifactGrantHeader =
        {
            Class: string
            Algorithm: string
            KeyId: string
        }

        /// Builds a header for the configured V1 artifact grant signature algorithm.
        static member Create(keyId: string) = { Class = nameof ArtifactGrantHeader; Algorithm = ArtifactGrantContract.Algorithm; KeyId = keyId }

    /// Describes the authorization payload bound into a signed artifact grant.
    [<CLIMutable; GenerateSerializer>]
    type ArtifactGrantPayload =
        {
            Class: string
            Issuer: string
            CacheServicePrincipalId: string
            TargetRootDirectoryVersionId: DirectoryVersionId
            ExecutionMode: MaterializationExecutionMode
            ArtifactIdentities: List<string>
            IssuedAt: Instant
            NotBefore: Instant
            ExpiresAt: Instant
        }

        /// Builds a grant payload with defensive artifact-identity copies for JSON and Orleans callers.
        static member Create
            (
                cacheServicePrincipalId: string,
                targetRootDirectoryVersionId: DirectoryVersionId,
                executionMode: MaterializationExecutionMode,
                artifactIdentities: string seq,
                issuedAt: Instant,
                ttl: Duration
            ) =
            {
                Class = nameof ArtifactGrantPayload
                Issuer = ArtifactGrantContract.Issuer
                CacheServicePrincipalId = cacheServicePrincipalId
                TargetRootDirectoryVersionId = targetRootDirectoryVersionId
                ExecutionMode = executionMode
                ArtifactIdentities = List<string>(artifactIdentities)
                IssuedAt = issuedAt
                NotBefore = issuedAt
                ExpiresAt = issuedAt.Plus ttl
            }

    /// Represents one signed artifact grant envelope.
    [<CLIMutable; GenerateSerializer>]
    type SignedArtifactGrant =
        {
            Class: string
            Header: ArtifactGrantHeader
            Payload: ArtifactGrantPayload
            Signature: string
        }

        /// Builds a signed artifact grant envelope from a canonical header, payload, and base64url signature.
        static member Create(header: ArtifactGrantHeader, payload: ArtifactGrantPayload, signature: string) =
            { Class = nameof SignedArtifactGrant; Header = header; Payload = payload; Signature = signature }

    /// Describes one published public validation key for artifact grant verification.
    [<CLIMutable; GenerateSerializer>]
    type ArtifactGrantValidationKey =
        {
            Class: string
            KeyId: string
            Algorithm: string
            CreatedAt: Instant
            NotBefore: Instant
            ExpiresAt: Instant
            PublicKeyX: string
            PublicKeyY: string
        }

        /// Builds one validation key using base64url-encoded P-256 public coordinates.
        static member Create(keyId: string, createdAt: Instant, expiresAt: Instant, publicKeyX: string, publicKeyY: string) =
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
            Class: string
            Issuer: string
            PublishedAt: Instant
            CacheTtl: Duration
            Keys: List<ArtifactGrantValidationKey>
        }

        /// Builds a validation-key publication with a defensive key copy.
        static member Create(publishedAt: Instant, keys: ArtifactGrantValidationKey seq) =
            {
                Class = nameof ArtifactGrantValidationKeySet
                Issuer = ArtifactGrantContract.Issuer
                PublishedAt = publishedAt
                CacheTtl = ArtifactGrantContract.ValidationKeyCacheTtl
                Keys = List<ArtifactGrantValidationKey>(keys)
            }

    /// Describes the expected local validation scope for one artifact request.
    [<CLIMutable; GenerateSerializer>]
    type ArtifactGrantValidationRequest =
        {
            Class: string
            CacheServicePrincipalId: string
            TargetRootDirectoryVersionId: DirectoryVersionId
            ExecutionMode: MaterializationExecutionMode
            ArtifactIdentity: string
        }

        /// Builds the local validation request that cache runtime code checks before serving one artifact.
        static member Create
            (
                cacheServicePrincipalId: string,
                targetRootDirectoryVersionId: DirectoryVersionId,
                executionMode: MaterializationExecutionMode,
                artifactIdentity: string
            ) =
            {
                Class = nameof ArtifactGrantValidationRequest
                CacheServicePrincipalId = cacheServicePrincipalId
                TargetRootDirectoryVersionId = targetRootDirectoryVersionId
                ExecutionMode = executionMode
                ArtifactIdentity = artifactIdentity
            }
