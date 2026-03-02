namespace Grace.Server

open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Server.ApplicationContext
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types.Events
open Grace.Types.Policy
open Grace.Types.Reference
open Grace.Types.Types
open Grace.Types.Validation
open Microsoft.Extensions.Logging
open System
open System.Threading.Tasks

module DerivedComputation =

    let log = loggerFactory.CreateLogger("DerivedComputation.Server")


    let internal shouldRecordQuickScan referenceType =
        match referenceType with
        | ReferenceType.Commit
        | ReferenceType.Checkpoint
        | ReferenceType.Promotion -> true
        | _ -> false

    let handleReferenceEvent (referenceEvent: ReferenceEvent) =
        task {
            match referenceEvent.Event with
            | ReferenceEventType.Created (referenceId,
                                          ownerId,
                                          organizationId,
                                          repositoryId,
                                          branchId,
                                          directoryId,
                                          sha256Hash,
                                          referenceType,
                                          referenceText,
                                          links) ->
                match referenceType with
                | _ when shouldRecordQuickScan referenceType ->
                    let correlationId = referenceEvent.Metadata.CorrelationId
                    let policyActorProxy = Policy.CreateActorProxy branchId repositoryId correlationId

                    let! policySnapshot =
                        task {
                            match! policyActorProxy.GetCurrent correlationId with
                            | Some snapshot -> return snapshot.PolicySnapshotId
                            | None -> return PolicySnapshotId String.Empty
                        }

                    let now = getCurrentInstant ()

                    let validationResult =
                        { ValidationResultDto.Default with
                            ValidationResultId = Guid.NewGuid()
                            OwnerId = ownerId
                            OrganizationId = organizationId
                            RepositoryId = repositoryId
                            ValidationName = "quick-scan"
                            ValidationVersion = "1.0"
                            Output =
                                {
                                    Status = ValidationStatus.Pass
                                    Summary =
                                        $"quick-scan recorded for {getDiscriminatedUnionCaseName referenceType}; referenceId={referenceId}; policySnapshotId={policySnapshot}."
                                    ArtifactIds = []
                                }
                            OnBehalfOf = [ UserId Constants.GraceSystemUser ]
                            CreatedAt = now
                        }

                    let validationResultActorProxy = ValidationResult.CreateActorProxy validationResult.ValidationResultId repositoryId correlationId

                    let metadata = EventMetadata.New correlationId Constants.GraceSystemUser

                    match! validationResultActorProxy.Handle (ValidationResultCommand.Record validationResult) metadata with
                    | Ok _ ->
                        log.LogInformation(
                            "{CurrentInstant}: Node: {hostName}; CorrelationId: {correlationId}; quick-scan validation recorded for {referenceType} ReferenceId: {referenceId}.",
                            getCurrentInstantExtended (),
                            getMachineName,
                            correlationId,
                            getDiscriminatedUnionCaseName referenceType,
                            referenceId
                        )
                    | Error graceError ->
                        log.LogError(
                            "{CurrentInstant}: Node: {hostName}; CorrelationId: {correlationId}; Failed to record quick-scan validation for ReferenceId {referenceId}: {error}.",
                            getCurrentInstantExtended (),
                            getMachineName,
                            correlationId,
                            referenceId,
                            graceError
                        )
                | _ -> ()
            | _ -> ()
        }

    let handlePolicyEvent (policyEvent: PolicyEvent) =
        task {
            match policyEvent.Event with
            | SnapshotCreated snapshot ->
                log.LogInformation(
                    "{CurrentInstant}: Node: {hostName}; CorrelationId: {correlationId}; Policy snapshot created: {policySnapshotId}.",
                    getCurrentInstantExtended (),
                    getMachineName,
                    policyEvent.Metadata.CorrelationId,
                    snapshot.PolicySnapshotId
                )
            | Acknowledged (policySnapshotId, _, _) ->
                log.LogInformation(
                    "{CurrentInstant}: Node: {hostName}; CorrelationId: {correlationId}; Policy snapshot acknowledged: {policySnapshotId}.",
                    getCurrentInstantExtended (),
                    getMachineName,
                    policyEvent.Metadata.CorrelationId,
                    policySnapshotId
                )
        }
