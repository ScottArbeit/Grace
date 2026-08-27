namespace Grace.Server.Security

open System
open System.Security.Cryptography
open Grace.Shared
open Grace.Types.ArtifactGrant

/// Owns one ephemeral Server process key used to sign exact Cache artifact read grants.
type CacheArtifactGrantSigner private (keyId: string, signingKey: ECDsa) =

    let validationKey = ArtifactGrant.createValidationKey keyId signingKey

    /// Publishes the public P-256 validation material for the current Server process key.
    member _.ValidationKey = validationKey

    /// Issues one fixed five-minute grant for an exact immutable DirectoryVersion ZIP generation.
    member _.Issue(artifact: DirectoryVersionZipCacheArtifact, issuedAt: DateTimeOffset) =
        ArtifactGrant.issue keyId signingKey (CacheArtifactGrantIssueRequest.Create(artifact, issuedAt))

    /// Creates one independent ephemeral signer for process hosting or focused tests.
    static member Create() =
        let keyId = Guid.NewGuid().ToString("N")
        let signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256)
        new CacheArtifactGrantSigner(keyId, signingKey)

    interface IDisposable with
        /// Releases the private process key material.
        member _.Dispose() = signingKey.Dispose()

/// Provides the single Cache artifact grant signer owned by this Server process.
[<RequireQualifiedAccess>]
module CacheArtifactGrantSigning =

    let private processSigner = lazy (CacheArtifactGrantSigner.Create())

    /// Returns the current Server process signer without creating durable key state.
    let signer () = processSigner.Value
