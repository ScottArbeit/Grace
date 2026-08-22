namespace Grace.Shared.Parameters

open System
open Grace.Shared.Parameters.Common

/// Contains the fixed Product V1 contracts for one Server-approved DirectoryVersion ZIP fill.
module Cache =

    /// Describes the immutable ZIP bytes that Grace Server approved and Grace Cache must verify independently.
    [<CLIMutable>]
    type CacheArtifactDescriptor = { RepositoryId: string; DirectoryVersionId: string; Kind: string; Sha256: string; Size: int64 }

    /// Carries only the public coordinates of the Cache process P-256 key.
    [<CLIMutable>]
    type CachePublicJwk = { Kty: string; Crv: string; X: string; Y: string }

    /// Requests Server preparation for one immutable DirectoryVersion ZIP and one running Cache process key.
    type PrepareDirectoryVersionZipParameters() =
        inherit CommonParameters()
        member val RepositoryId = String.Empty with get, set
        member val DirectoryVersionId = String.Empty with get, set
        member val CachePublicKey = { Kty = String.Empty; Crv = String.Empty; X = String.Empty; Y = String.Empty } with get, set

    /// Returns the opaque short-lived permit and the exact bytes that Cache must sign to redeem it.
    [<CLIMutable>]
    type DirectoryVersionZipPreparation = { Descriptor: CacheArtifactDescriptor; Permit: string; PermitExpiresAt: DateTimeOffset; RedemptionBytes: string }

    /// Redeems one opaque permit using the bound Cache process signature.
    type RedeemDirectoryVersionZipFillParameters() =
        inherit CommonParameters()
        member val Permit = String.Empty with get, set
        member val Signature = String.Empty with get, set

    /// Returns one fresh read-only source to Cache after authorization and binding revalidation.
    [<CLIMutable>]
    type DirectoryVersionZipFillSource = { Descriptor: CacheArtifactDescriptor; SourceUri: string; SourceExpiresAt: DateTimeOffset }

    /// Identifies a typed Cache failure without returning secrets or managed filesystem paths.
    [<CLIMutable>]
    type CacheProblem = { Code: string; Detail: string }
