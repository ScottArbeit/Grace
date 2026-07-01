namespace Grace.Shared

open System
open Grace.Types.Common

/// Contains storage keys helpers.
module StorageKeys =
    [<Literal>]
    let private CasPrefix = "cas"

    [<Literal>]
    let private ContentPrefix = "content"

    [<Literal>]
    let private FileManifestsPrefix = "file-manifests"

    [<Literal>]
    let private ContentBlockMetadataPrefix = "content-block-metadata"

    /// Builds the storage object key for whole-file content using the current hash-addressed layout.
    let private wholeFileContentObjectFileName (fileVersion: FileVersion) =
        if String.IsNullOrWhiteSpace(string fileVersion.Blake3Hash) then
            fileVersion.GetObjectFileName
        else
            Utilities.getObjectFileName fileVersion.RelativePath $"{fileVersion.Sha256Hash}_{fileVersion.Blake3Hash}"

    /// Builds the current repository-scoped object key for whole-file content.
    let wholeFileContentObjectKey (fileVersion: FileVersion) = $"{fileVersion.RelativePath}/{wholeFileContentObjectFileName fileVersion}"

    /// Builds the legacy SHA-only repository-scoped object key for whole-file content.
    let legacyWholeFileContentObjectKey (fileVersion: FileVersion) = $"{fileVersion.RelativePath}/{fileVersion.GetObjectFileName}"

    /// Returns true when the current whole-file key differs from the legacy SHA-only key.
    let hasBlake3SpecificWholeFileContentObjectKey (fileVersion: FileVersion) =
        wholeFileContentObjectKey fileVersion
        <> legacyWholeFileContentObjectKey fileVersion

    /// Builds the StoragePool-scoped object key for a content-addressed ContentBlock payload.
    let contentBlockObjectKey (contentBlockAddress: ContentBlockAddress) =
        let digest = ContentAddress.requireBlake3Address (nameof contentBlockAddress) contentBlockAddress
        $"{CasPrefix}/{ContentPrefix}/{digest[0..1]}/{digest[2..3]}/{digest[4..5]}/{digest[6..7]}/{digest}"

    /// Builds the StoragePool-scoped object key for a content-addressed FileManifest record.
    let fileManifestObjectKey (manifestAddress: ManifestAddress) = $"{CasPrefix}/{FileManifestsPrefix}/{manifestAddress}"

    /// Builds the StoragePool-scoped object key for mutable ContentBlock metadata.
    let contentBlockMetadataObjectKey (contentBlockAddress: ContentBlockAddress) = $"{CasPrefix}/{ContentBlockMetadataPrefix}/{contentBlockAddress}.json"
