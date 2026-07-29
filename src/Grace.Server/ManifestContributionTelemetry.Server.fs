namespace Grace.Server

open Grace.Types.ManifestContributionAccounting
open System
open System.Collections.Generic
open System.Diagnostics
open System.Diagnostics.Metrics

/// Names the processing boundary that produced one manifest-accounting message outcome.
type ManifestContributionProcessingStage =
    | Parse
    | Handle
    | Settle

/// Names the truthful terminal observation for one Service Bus delivery.
type ManifestContributionMessageOutcome =
    | Completed
    | Abandoned
    | DeadLettered
    | SettlementFailed

/// Names one exact-relationship storage operation without exposing relationship identities.
type ManifestContributionRelationshipOperation =
    | EnsurePresent
    | EnsureAbsent
    | Verify

/// Names one bounded Redis accelerator operation.
type ManifestContributionRedisOperation =
    | Get
    | Set

/// Emits bounded metrics and high-cardinality activity context for manifest contribution accounting.
module ManifestContributionTelemetry =

    /// Identifies the one meter and activity source registered by Grace Server.
    [<Literal>]
    let InstrumentationName = "Grace.ManifestContributionAccounting"

    /// Lists every metric dimension permitted by the manifest-accounting telemetry contract.
    let internal AllowedMetricTagKeys =
        HashSet<string>(
            [|
                "stage"
                "outcome"
                "reference_type"
                "relationship_kind"
                "operation"
                "direction"
                "is_replay"
            |],
            StringComparer.Ordinal
        )

    let private meter = new Meter(InstrumentationName)
    let private activitySource = new ActivitySource(InstrumentationName)

    let private messages = meter.CreateCounter<int64>("grace.manifest_contribution.messages")

    let private processingDuration = meter.CreateHistogram<double>("grace.manifest_contribution.processing.duration", "ms")

    let private relationshipWrites = meter.CreateCounter<int64>("grace.manifest_contribution.relationship.writes")

    let private redisOperations = meter.CreateCounter<int64>("grace.manifest_contribution.redis.operations")

    let private repairActions = meter.CreateCounter<int64>("grace.manifest_contribution.repair.actions")

    /// Converts a processing stage to its stable bounded telemetry value.
    let private stageName =
        function
        | ManifestContributionProcessingStage.Parse -> "parse"
        | ManifestContributionProcessingStage.Handle -> "handle"
        | ManifestContributionProcessingStage.Settle -> "settle"

    /// Converts a message outcome to its stable bounded telemetry value.
    let private messageOutcomeName =
        function
        | ManifestContributionMessageOutcome.Completed -> "completed"
        | ManifestContributionMessageOutcome.Abandoned -> "abandoned"
        | ManifestContributionMessageOutcome.DeadLettered -> "dead_lettered"
        | ManifestContributionMessageOutcome.SettlementFailed -> "settlement_failed"

    /// Converts an exact relationship case to its stable bounded telemetry value.
    let private relationshipKind =
        function
        | ExactRelationship.ReferenceRoot _ -> "reference_root"
        | ExactRelationship.ParentChild _ -> "parent_child"
        | ExactRelationship.DirectoryVersionManifest _ -> "directory_version_manifest"

    /// Converts an exact relationship operation to its stable bounded telemetry value.
    let private relationshipOperationName =
        function
        | ManifestContributionRelationshipOperation.EnsurePresent -> "ensure_present"
        | ManifestContributionRelationshipOperation.EnsureAbsent -> "ensure_absent"
        | ManifestContributionRelationshipOperation.Verify -> "verify"

    /// Converts a Redis operation to its stable bounded telemetry value.
    let private redisOperationName =
        function
        | ManifestContributionRedisOperation.Get -> "get"
        | ManifestContributionRedisOperation.Set -> "set"

    /// Starts one delivery activity carrying identifiers that are forbidden from metric dimensions.
    let internal startMessageActivity messageId correlationId deliveryCount =
        let activity = activitySource.StartActivity("manifest-contribution.process-message", ActivityKind.Consumer)

        if not (isNull activity) then
            activity.SetTag("messaging.message.id", messageId)
            |> ignore

            activity.SetTag("grace.correlation_id", correlationId)
            |> ignore

            activity.SetTag("messaging.servicebus.delivery_count", deliveryCount)
            |> ignore

        activity

    /// Adds Reference accounting identities to the current activity without creating metric dimensions.
    let internal enrichReferenceActivity referenceId repositoryId directoryVersionId referenceType =
        let activity = Activity.Current

        if not (isNull activity) then
            activity.SetTag("grace.reference.id", referenceId)
            |> ignore

            activity.SetTag("grace.repository.id", repositoryId)
            |> ignore

            activity.SetTag("grace.directory_version.id", directoryVersionId)
            |> ignore

            activity.SetTag("grace.reference.type", referenceType)
            |> ignore

    /// Records the single truthful message outcome and its bounded processing duration.
    let internal recordMessage stage outcome durationMilliseconds =
        let tags =
            [|
                KeyValuePair<string, obj>("stage", stageName stage)
                KeyValuePair<string, obj>("outcome", messageOutcomeName outcome)
            |]

        messages.Add(1L, tags)
        processingDuration.Record(durationMilliseconds, tags)

    /// Records one exact-relationship storage observation without relationship identifiers.
    let internal recordRelationship operation relationship outcome =
        let tags =
            [|
                KeyValuePair<string, obj>("relationship_kind", relationshipKind relationship)
                KeyValuePair<string, obj>("operation", relationshipOperationName operation)
                KeyValuePair<string, obj>("outcome", outcome)
            |]

        relationshipWrites.Add(1L, tags)

    /// Records one Redis accelerator result without its key or manifest identity.
    let internal recordRedisOperation operation outcome =
        let tags =
            [|
                KeyValuePair<string, obj>("operation", redisOperationName operation)
                KeyValuePair<string, obj>("outcome", outcome)
            |]

        redisOperations.Add(1L, tags)

    /// Records each confirmed repair action with the bounded terminal repair outcome.
    let internal recordRepairActions (actionKinds: string array) terminalOutcome =
        actionKinds
        |> Array.iter (fun actionKind ->
            let tags =
                [|
                    KeyValuePair<string, obj>("operation", actionKind)
                    KeyValuePair<string, obj>("outcome", terminalOutcome)
                |]

            repairActions.Add(1L, tags))
