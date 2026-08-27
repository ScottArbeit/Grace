namespace Grace.Types

open System
open Orleans

/// Defines the Product V1 contract for Server-signed Cache artifact grants.
module ArtifactGrant =

    /// Provides the fixed values used by every DirectoryVersion ZIP grant issuer and validator.
    [<RequireQualifiedAccess>]
    module CacheArtifactGrantContract =

        /// Identifies the only accepted compact-token signature algorithm.
        [<Literal>]
        let Algorithm = "ES256"

        /// Identifies the required compact-token type.
        [<Literal>]
        let TokenType = "JWT"

        /// Identifies the current Grace Server process as the grant issuer.
        [<Literal>]
        let Issuer = "Grace.Server.CacheArtifactGrant.v1"

        /// Identifies Grace Cache artifact admission as the only audience.
        [<Literal>]
        let Audience = "Grace.Cache.Artifact.v1"

        /// Identifies the only artifact kind accepted by this tracer.
        [<Literal>]
        let ArtifactKind = "DirectoryVersionZip"

        /// Identifies the only HTTP method authorized by a read grant.
        [<Literal>]
        let HttpMethod = "GET"

        /// Defines the declared read-grant lifetime without any expiry extension.
        let GrantLifetime = TimeSpan.FromMinutes(5.0)

        /// Defines the maximum tolerated future difference at request admission.
        let MaximumClockDifference = TimeSpan.FromSeconds(30.0)

    /// Carries only the public P-256 coordinates used by Cache process binding and grant validation.
    [<CLIMutable; GenerateSerializer>]
    type P256PublicJwk =
        {
            [<Id(0u)>]
            Kty: string
            [<Id(1u)>]
            Crv: string
            [<Id(2u)>]
            X: string
            [<Id(3u)>]
            Y: string
        }

        /// Builds the public JWK shape used by the fixed P-256 contract.
        static member Create(x: string, y: string) = { Kty = "EC"; Crv = "P-256"; X = x; Y = y }

    /// Identifies one exact immutable DirectoryVersion ZIP generation.
    [<CLIMutable; GenerateSerializer>]
    type DirectoryVersionZipCacheArtifact =
        {
            [<Id(0u)>]
            RepositoryId: string
            [<Id(1u)>]
            DirectoryVersionId: string
            [<Id(2u)>]
            Blake3Hash: string
        }

        /// Returns the exact loopback Cache route authorized for this artifact.
        member this.Route = $"/repositories/{this.RepositoryId}/directory-version-zips/{this.DirectoryVersionId}"

        /// Builds one exact ZIP generation from valid Grace identities and a lowercase BLAKE3 value.
        static member Create(repositoryId: Guid, directoryVersionId: Guid, blake3Hash: string) =
            if repositoryId = Guid.Empty then
                invalidArg (nameof repositoryId) "RepositoryId is required."

            if directoryVersionId = Guid.Empty then
                invalidArg (nameof directoryVersionId) "DirectoryVersionId is required."

            if String.IsNullOrEmpty(blake3Hash)
               || blake3Hash.Length <> 64
               || blake3Hash
                  |> Seq.exists (fun value ->
                      not (
                          (value >= '0' && value <= '9')
                          || (value >= 'a' && value <= 'f')
                      )) then
                invalidArg (nameof blake3Hash) "BLAKE3 must be exactly 64 lowercase hexadecimal characters."

            { RepositoryId = repositoryId.ToString("D"); DirectoryVersionId = directoryVersionId.ToString("D"); Blake3Hash = blake3Hash }

    /// Wraps one compact Server-signed grant without exposing token parsing as a string convention.
    [<Struct>]
    type CacheArtifactGrant =
        private
        | CacheArtifactGrant of value: string

        /// Returns the compact token carried in the HTTP Authorization header.
        member this.Value =
            let (CacheArtifactGrant value) = this
            value

        /// Wraps a non-empty compact token received from a trusted transport boundary.
        static member Create(value: string) =
            if String.IsNullOrWhiteSpace(value) then
                invalidArg (nameof value) "Cache artifact grant is required."

            CacheArtifactGrant value

    /// Carries one compact grant and the exact instant at which later requests must replace it.
    [<CLIMutable>]
    type IssuedCacheArtifactGrant = { Grant: CacheArtifactGrant; ExpiresAt: DateTimeOffset }

    /// Publishes the current Server process key and fixed validation contract without private material.
    [<CLIMutable; GenerateSerializer>]
    type CacheArtifactGrantValidationKey =
        {
            [<Id(0u)>]
            Issuer: string
            [<Id(1u)>]
            Audience: string
            [<Id(2u)>]
            Algorithm: string
            [<Id(3u)>]
            KeyId: string
            [<Id(4u)>]
            PublicJwk: P256PublicJwk
        }

    /// Supplies the settled inputs used to issue one five-minute exact ZIP grant.
    [<CLIMutable>]
    type CacheArtifactGrantIssueRequest =
        {
            Artifact: DirectoryVersionZipCacheArtifact
            HttpMethod: string
            Route: string
            IssuedAt: DateTimeOffset
        }

        /// Builds the only supported request shape for one exact artifact.
        static member Create(artifact: DirectoryVersionZipCacheArtifact, issuedAt: DateTimeOffset) =
            if isNull (box artifact) then nullArg (nameof artifact)

            { Artifact = artifact; HttpMethod = CacheArtifactGrantContract.HttpMethod; Route = artifact.Route; IssuedAt = issuedAt }

    /// Supplies the exact HTTP request and artifact expected at Cache admission.
    [<CLIMutable>]
    type CacheArtifactGrantValidationRequest =
        {
            Artifact: DirectoryVersionZipCacheArtifact
            HttpMethod: string
            Route: string
        }

        /// Builds the only supported validation request for one exact artifact.
        static member Create(artifact: DirectoryVersionZipCacheArtifact) =
            if isNull (box artifact) then nullArg (nameof artifact)

            { Artifact = artifact; HttpMethod = CacheArtifactGrantContract.HttpMethod; Route = artifact.Route }

    /// Classifies a local grant failure without retaining raw token or signing material.
    type CacheArtifactGrantValidationError =
        | Malformed
        | UnsupportedAlgorithm
        | UnknownKeyId of keyId: string
        | InvalidSignature
        | InvalidIssuer
        | InvalidAudience
        | NotYetValid
        | Expired
        | InvalidLifetime
        | WrongMethod
        | WrongRoute
        | WrongArtifactKind
        | WrongRepository
        | WrongDirectoryVersion
        | WrongBlake3
