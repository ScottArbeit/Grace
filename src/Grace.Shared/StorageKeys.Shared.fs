namespace Grace.Shared

open Grace.Types.Common

module StorageKeys =
    [<Literal>]
    let private CasPrefix = "cas"

    [<Literal>]
    let private ContentBlocksPrefix = "content-blocks"

    [<Literal>]
    let private FileManifestsPrefix = "file-manifests"

    [<Literal>]
    let private ContentBlockMetadataPrefix = "content-block-metadata"

    /// Builds the current repository-scoped object key for whole-file content.
    let wholeFileContentObjectKey (fileVersion: FileVersion) = $"{fileVersion.RelativePath}/{fileVersion.GetObjectFileName}"

    /// Builds the StoragePool-scoped object key for a content-addressed ContentBlock payload.
    let contentBlockObjectKey (contentBlockAddress: ContentBlockAddress) = $"{CasPrefix}/{ContentBlocksPrefix}/{contentBlockAddress}"

    /// Builds the StoragePool-scoped object key for a content-addressed FileManifest record.
    let fileManifestObjectKey (manifestAddress: ManifestAddress) = $"{CasPrefix}/{FileManifestsPrefix}/{manifestAddress}"

    /// Builds the StoragePool-scoped object key for mutable ContentBlock metadata.
    let contentBlockMetadataObjectKey (contentBlockAddress: ContentBlockAddress) = $"{CasPrefix}/{ContentBlockMetadataPrefix}/{contentBlockAddress}.json"
