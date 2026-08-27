namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Parameters.Cache
open Grace.Shared.Parameters.Common
open Grace.Types.ArtifactGrant

/// Provides typed access to the two Server operations used by the Cache HTTP tracer.
type Cache() =

    /// Gets the current Server process public key for local Cache artifact grant validation.
    static member GetArtifactGrantValidationKey(parameters: CommonParameters) =
        getServer<CommonParameters, CacheArtifactGrantValidationKey> (parameters |> ensureCorrelationIdIsSet, "cache/artifact-grant-validation-key")

    /// Prepares one Server-approved DirectoryVersion ZIP fill for the authenticated caller and Cache process key.
    static member PrepareDirectoryVersionZip(parameters: PrepareDirectoryVersionZipParameters) =
        postServer<PrepareDirectoryVersionZipParameters, DirectoryVersionZipPreparation> (
            parameters |> ensureCorrelationIdIsSet,
            "cache/prepareDirectoryVersionZip"
        )

    /// Redeems one permit and Cache signature for the exact read-only ZIP source.
    static member RedeemDirectoryVersionZipFill(parameters: RedeemDirectoryVersionZipFillParameters) =
        postServer<RedeemDirectoryVersionZipFillParameters, DirectoryVersionZipFillSource> (
            parameters |> ensureCorrelationIdIsSet,
            "cache/redeemDirectoryVersionZipFill"
        )
