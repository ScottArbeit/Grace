namespace Grace.Actors

open Grace.Types.Common
open Grace.Types.ManifestContributionWorkflow
open System.Collections.Generic

/// Maps accepted Library content onto the existing counter and manifest-workflow protocol.
module LibraryTransfer =

    /// Returns each unique block range in a completed manifest.
    let workflowRanges (manifest: FileManifest) =
        let seen = HashSet<ContentBlockAddress>()

        manifest.Blocks
        |> Seq.choose (fun block ->
            if seen.Add block.Address then
                Some { StoragePoolId = manifest.StoragePoolId; ContentBlockAddress = block.Address }
            else
                None)
        |> Seq.toArray

    /// Builds the tracked counter identity shared by retry and acknowledgement.
    let counterOperationId operationId contentVersionId = $"library:{operationId:N}:content:{contentVersionId:N}"
