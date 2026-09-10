namespace Grace.Actors

open Grace.Types.Common
open Grace.Types.Library
open Grace.Types.ManifestContributionWorkflow
open Grace.Types.UploadSession
open NodaTime
open System.Collections.Generic
open System.Threading.Tasks

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

    /// Validates the completed upload binding before a Library content change consumes it.
    let rec validatePreparedUpload (now: Instant) repositoryId operationId principalId (upload: UploadSessionDto) =
        if upload.LifecycleState = UploadSessionLifecycleState.StateDeleted
           || (upload.LifecycleState
               <> UploadSessionLifecycleState.NotStarted
               && (upload.RetryExpiresAt
                   |> Option.forall (fun deadline -> deadline <= now))) then
            Error RejectionReason.PreparedContentExpired
        else
            validatePreparedUploadOpen now repositoryId operationId principalId upload

    /// Checks a still-live preparation's identity and completed manifest before Library acceptance.
    and private validatePreparedUploadOpen (now: Instant) repositoryId operationId principalId (upload: UploadSessionDto) =
        let completed =
            match upload.LifecycleState with
            | UploadSessionLifecycleState.Finalized
            | UploadSessionLifecycleState.RetentionPending -> true
            | _ -> false

        let binding =
            upload.LibraryPreparation
            |> Option.defaultWith (fun () -> invalidOp "The upload session has no Library binding.")

        let manifest =
            upload.FinalizedManifest
            |> Option.defaultWith (fun () -> invalidOp "The upload session has no completed manifest.")

        if not completed
           || upload.RepositoryId <> repositoryId
           || binding.OperationId <> operationId
           || binding.PrincipalId <> principalId
           || manifest.FileContentHash <> upload.FileContentHash then
            invalidOp "The completed upload does not match this Library operation."
        elif upload.RetryExpiresAt
             |> Option.forall (fun deadline -> deadline <= now) then
            Error RejectionReason.PreparedContentExpired
        else
            Ok(binding, manifest)

    /// Reads a completed upload before observing the expiry clock used by the consuming Library decision.
    let readAndValidatePreparedUpload (readUpload: unit -> Task<UploadSessionDto>) (getCurrentInstant: unit -> Instant) repositoryId operationId principalId =
        task {
            let! upload = readUpload ()
            let now = getCurrentInstant ()
            return now, upload, validatePreparedUpload now repositoryId operationId principalId upload
        }

    /// Reports whether durable workflow state already proves the exact tracked Library contribution completed.
    let workflowCompletedForTrackedManifest repositoryId counterOperationId (manifest: FileManifest) ranges (workflow: ManifestContributionWorkflowDto) =
        workflow.RepositoryId = repositoryId
        && workflow.StoragePoolId = manifest.StoragePoolId
        && workflow.ManifestAddress = manifest.ManifestAddress
        && workflow.Direction = ManifestContributionDirection.Increment
        && workflow.StartOperationId = Some $"{counterOperationId}:fanout"
        && workflow.CounterRevision > 0L
        && workflow.Ranges = ranges
        && workflow.LifecycleState = ManifestContributionWorkflowLifecycleState.Completed
        && Array.isEmpty workflow.FailedRanges
        && workflow.CompletedRanges.Length = ranges.Length
        && (ranges
            |> Array.forall (fun expected ->
                workflow.CompletedRanges
                |> Array.exists (fun actual -> actual.Range = expected)))
