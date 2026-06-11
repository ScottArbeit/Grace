namespace Grace.Shared

open System
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

    let private wholeFileContentObjectFileName (fileVersion: FileVersion) =
        if String.IsNullOrWhiteSpace(string fileVersion.Blake3Hash) then
            fileVersion.GetObjectFileName
        else
            Utilities.getObjectFileName fileVersion.RelativePath $"{fileVersion.Sha256Hash}_{fileVersion.Blake3Hash}"

    /// Builds the current repository-scoped object key for whole-file content.
    let wholeFileContentObjectKey (fileVersion: FileVersion) = $"{fileVersion.RelativePath}/{wholeFileContentObjectFileName fileVersion}"

    /// Builds the StoragePool-scoped object key for a content-addressed ContentBlock payload.
    let contentBlockObjectKey (contentBlockAddress: ContentBlockAddress) = $"{CasPrefix}/{ContentBlocksPrefix}/{contentBlockAddress}"

    /// Builds the StoragePool-scoped object key for a content-addressed FileManifest record.
    let fileManifestObjectKey (manifestAddress: ManifestAddress) = $"{CasPrefix}/{FileManifestsPrefix}/{manifestAddress}"

    /// Builds the StoragePool-scoped object key for mutable ContentBlock metadata.
    let contentBlockMetadataObjectKey (contentBlockAddress: ContentBlockAddress) = $"{CasPrefix}/{ContentBlockMetadataPrefix}/{contentBlockAddress}.json"
