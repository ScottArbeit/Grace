namespace Grace.Shared.Parameters

open System
open Grace.Shared.Parameters.Common
open Grace.Types.ArtifactGrant

/// Contains the fixed Product V1 contracts for one Server-approved DirectoryVersion ZIP fill.
module Cache =

    /// Requests Server preparation for one immutable DirectoryVersion ZIP and one running Cache process key.
    type PrepareDirectoryVersionZipParameters() =
        inherit CommonParameters()
        member val RepositoryId = String.Empty with get, set
        member val DirectoryVersionId = String.Empty with get, set
        member val CachePublicKey = P256PublicJwk.Create(String.Empty, String.Empty) with get, set

    /// Returns the read grant plus the separate fill permit bound to the current Cache process.
    [<CLIMutable>]
    type DirectoryVersionZipPreparation =
        {
            Artifact: DirectoryVersionZipCacheArtifact
            ArtifactGrant: string
            ArtifactGrantExpiresAt: DateTimeOffset
            Permit: string
            PermitExpiresAt: DateTimeOffset
            RedemptionBytes: string
        }

    /// Redeems one opaque permit using the bound Cache process signature.
    type RedeemDirectoryVersionZipFillParameters() =
        inherit CommonParameters()
        member val Permit = String.Empty with get, set
        member val Signature = String.Empty with get, set

    /// Returns one fresh read-only source to Cache after authorization and binding revalidation.
    [<CLIMutable>]
    type DirectoryVersionZipFillSource = { Artifact: DirectoryVersionZipCacheArtifact; SourceUri: string; SourceExpiresAt: DateTimeOffset }

    /// Identifies a typed Cache failure without returning secrets or managed filesystem paths.
    [<CLIMutable>]
    type CacheProblem = { Code: string; Detail: string }
