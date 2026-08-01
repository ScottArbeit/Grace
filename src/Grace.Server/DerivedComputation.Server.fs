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
open Grace.Types.Common
open Grace.Types.Validation
open Microsoft.Extensions.Logging
open System
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks

/// Contains Grace Server derived computation behavior and supporting helpers.
module DerivedComputation =

    let log = loggerFactory.CreateLogger("DerivedComputation.Server")


    /// Determines whether record quick scan.
    let internal shouldRecordQuickScan referenceType =
        match referenceType with
        | ReferenceType.Commit
        | ReferenceType.Checkpoint
        | ReferenceType.Promotion -> true
        | _ -> false

    /// Derives the versioned durable identity for one Reference quick-scan result.
    let internal buildQuickScanValidationResultId (repositoryId: RepositoryId) (referenceId: ReferenceId) =
        let repositorySegment = repositoryId.ToString("D").ToLowerInvariant()
        let referenceSegment = referenceId.ToString("D").ToLowerInvariant()

        let seed = $"grace.validation.quick-scan.v1|{repositorySegment}|{referenceSegment}"

        let hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed))
        let guidBytes = hash[0..15]
        guidBytes[7] <- (guidBytes[7] &&& 0x0Fuy) ||| 0x50uy
        guidBytes[8] <- (guidBytes[8] &&& 0x3Fuy) ||| 0x80uy
        ValidationResultId(guidBytes)

    /// Builds the replay-safe quick-scan result from the persisted Reference Created event.
    let internal buildQuickScanValidationResult (referenceEvent: ReferenceEvent) =
        match referenceEvent.Event with
        | ReferenceEventType.Created (referenceId, ownerId, organizationId, repositoryId, _, _, _, _, referenceType, _, _) ->
            { ValidationResultDto.Default with
                ValidationResultId = buildQuickScanValidationResultId repositoryId referenceId
                OwnerId = ownerId
                OrganizationId = organizationId
                RepositoryId = repositoryId
                ValidationName = "quick-scan"
                ValidationVersion = "1.0"
                Output =
                    {
                        Status = ValidationStatus.Pass
                        Summary = $"quick-scan recorded for {getDiscriminatedUnionCaseName referenceType}; referenceId={referenceId}."
                        ArtifactIds = []
                    }
                OnBehalfOf = [ UserId Constants.GraceSystemUser ]
                CreatedAt = referenceEvent.Metadata.Timestamp
            }
        | _ -> invalidArg (nameof referenceEvent) "Quick-scan results require a persisted Reference Created event."

    /// Coordinates handle reference event processing for Grace Server.
    let handleReferenceEvent (referenceEvent: ReferenceEvent) =
        task {
            match referenceEvent.Event with
            | ReferenceEventType.Created (referenceId,
                                          ownerId,
                                          organizationId,
                                          repositoryId,
                                          _,
                                          directoryId,
                                          sha256Hash,
                                          blake3Hash,
                                          referenceType,
                                          referenceText,
                                          links) ->
                match referenceType with
                | _ when shouldRecordQuickScan referenceType ->
                    let correlationId = referenceEvent.Metadata.CorrelationId
                    let validationResult = buildQuickScanValidationResult referenceEvent

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

    /// Coordinates handle policy event processing for Grace Server.
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
