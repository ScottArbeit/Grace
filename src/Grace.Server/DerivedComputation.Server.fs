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
open Grace.Types.Review
open Grace.Types.Types
open Microsoft.Extensions.Logging
open System
open System.Threading.Tasks

module DerivedComputation =

    let log = loggerFactory.CreateLogger("DerivedComputation.Server")

    let handleReferenceEvent (referenceEvent: ReferenceEvent) =
        task {
            match referenceEvent.Event with
            | ReferenceEventType.Created(referenceId,
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
                | ReferenceType.Commit
                | ReferenceType.Checkpoint
                | ReferenceType.Promotion ->
                    let correlationId = referenceEvent.Metadata.CorrelationId
                    let policyActorProxy = Policy.CreateActorProxy branchId repositoryId correlationId

                    let! policySnapshot =
                        task {
                            match! policyActorProxy.GetCurrent correlationId with
                            | Some snapshot -> return snapshot.PolicySnapshotId
                            | None -> return PolicySnapshotId String.Empty
                        }

                    let now = getCurrentInstant ()

                    let riskProfile = { DeterministicRiskProfile.Default with ReferenceId = referenceId; PolicySnapshotId = policySnapshot; CreatedAt = now }

                    let stage0Analysis =
                        { Stage0Analysis.Default with
                            Stage0AnalysisId = Guid.NewGuid()
                            OwnerId = ownerId
                            OrganizationId = organizationId
                            RepositoryId = repositoryId
                            ReferenceId = referenceId
                            PolicySnapshotId = policySnapshot
                            RiskProfile = riskProfile
                            CreatedAt = now }

                    let stage0ActorProxy = Stage0.CreateActorProxy referenceId repositoryId correlationId
                    let metadata = EventMetadata.New correlationId Constants.GraceSystemUser

                    match! stage0ActorProxy.Handle (Stage0Command.Record stage0Analysis) metadata with
                    | Ok _ ->
                        log.LogInformation(
                            "{CurrentInstant}: Node: {hostName}; CorrelationId: {correlationId}; Stage 0 recorded for {referenceType} ReferenceId: {referenceId}.",
                            getCurrentInstantExtended (),
                            getMachineName,
                            correlationId,
                            getDiscriminatedUnionCaseName referenceType,
                            referenceId
                        )
                    | Error graceError ->
                        log.LogError(
                            "{CurrentInstant}: Node: {hostName}; CorrelationId: {correlationId}; Failed to record Stage 0 for ReferenceId {referenceId}: {error}.",
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
            | Acknowledged(policySnapshotId, _, _) ->
                log.LogInformation(
                    "{CurrentInstant}: Node: {hostName}; CorrelationId: {correlationId}; Policy snapshot acknowledged: {policySnapshotId}.",
                    getCurrentInstantExtended (),
                    getMachineName,
                    policyEvent.Metadata.CorrelationId,
                    policySnapshotId
                )
        }
