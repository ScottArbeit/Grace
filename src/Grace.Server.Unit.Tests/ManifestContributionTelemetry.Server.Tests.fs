namespace Grace.Server.Tests

open Grace.Server
open Grace.Server.Notification
open Grace.Types.Common
open Grace.Types.Events
open Grace.Types.ManifestContributionAccounting
open Grace.Types.Reference
open Grace.Types.Repository
open NUnit.Framework
open System
open System.Collections.Generic
open System.Diagnostics
open System.Diagnostics.Metrics
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions
open System.Threading
open System.Threading.Tasks

/// Proves bounded manifest-accounting telemetry and single-intent Service Bus settlement behavior.
[<NonParallelizable>]
type ManifestContributionTelemetryServerTests() =

    /// Creates settlement dependencies whose calls are retained for exact-path assertions.
    let dependencies parse handle complete abandon deadLetter : Subscriber.GraceEventSettlementDependencies =
        { Parse = parse; Handle = handle; Complete = complete; Abandon = abandon; DeadLetter = deadLetter }

    /// Supplies stable high-cardinality message context without exposing it as metric dimensions.
    let metadata: Subscriber.GraceEventMessageMetadata = { MessageId = "message-123"; CorrelationId = "correlation-456"; DeliveryCount = 3 }

    /// Represents a valid Reference-created delivery that enters manifest contribution accounting.
    let validEvent =
        GraceEvent.ReferenceEvent
            {
                Event =
                    ReferenceEventType.Created(
                        Guid.Parse("11111111-1111-1111-1111-111111111111"),
                        Guid.Parse("22222222-2222-2222-2222-222222222222"),
                        Guid.Parse("33333333-3333-3333-3333-333333333333"),
                        Guid.Parse("44444444-4444-4444-4444-444444444444"),
                        Guid.Parse("55555555-5555-5555-5555-555555555555"),
                        Guid.Parse("66666666-6666-6666-6666-666666666666"),
                        Sha256Hash String.Empty,
                        Blake3Hash String.Empty,
                        ReferenceType.Commit,
                        "reference-created",
                        []
                    )
                Metadata = EventMetadata.New "reference-created-correlation" "unit-test"
            }

    /// Represents a valid general Grace delivery that never enters manifest contribution accounting.
    let unrelatedEvent =
        GraceEvent.RepositoryEvent { Event = RepositoryEventType.Initialized; Metadata = EventMetadata.New "repository-initialized-correlation" "unit-test" }

    /// Executes one successful delivery and proves handler and completion are each invoked once.
    [<Test>]
    member _.ValidMessageHandlesAndCompletesExactlyOnce() =
        task {
            let calls = ResizeArray<string>()

            let seam =
                dependencies
                    (fun _ -> Ok validEvent)
                    (fun _ _ ->
                        calls.Add "handle"
                        Task.CompletedTask)
                    (fun _ ->
                        calls.Add "complete"
                        Task.CompletedTask)
                    (fun _ ->
                        calls.Add "abandon"
                        Task.CompletedTask)
                    (fun _ _ _ ->
                        calls.Add "dead-letter"
                        Task.CompletedTask)

            do! Subscriber.processGraceEventWith seam metadata (BinaryData.FromString("{}")) CancellationToken.None

            Assert.That(calls.ToArray(), Is.EqualTo([| "handle"; "complete" |] :> obj))
        }

    /// Proves valid general Grace deliveries do not inflate manifest-accounting message or duration instruments.
    [<Test>]
    member _.UnrelatedGraceEventDoesNotEmitManifestMessageMeasurements() =
        task {
            let measurements = ResizeArray<string>()
            use listener = new MeterListener()

            listener.InstrumentPublished <-
                fun instrument meterListener ->
                    if instrument.Meter.Name = ManifestContributionTelemetry.InstrumentationName
                       && (instrument.Name = "grace.manifest_contribution.messages"
                           || instrument.Name = "grace.manifest_contribution.processing.duration") then
                        meterListener.EnableMeasurementEvents instrument

            listener.SetMeasurementEventCallback<int64>(fun instrument _ _ _ -> measurements.Add instrument.Name)
            listener.SetMeasurementEventCallback<double>(fun instrument _ _ _ -> measurements.Add instrument.Name)
            listener.Start()

            let seam =
                dependencies
                    (fun _ -> Ok unrelatedEvent)
                    (fun _ _ -> Task.CompletedTask)
                    (fun _ -> Task.CompletedTask)
                    (fun _ -> Task.CompletedTask)
                    (fun _ _ _ -> Task.CompletedTask)

            do! Subscriber.processGraceEventWith seam metadata (BinaryData.FromString("{}")) CancellationToken.None

            Assert.That(measurements, Is.Empty)
        }

    /// Proves Reference-created deliveries emit both manifest message instruments with bounded terminal tags.
    [<Test>]
    member _.ReferenceCreatedDeliveryEmitsManifestMessageAndDurationMeasurements() =
        task {
            let measurements = ResizeArray<string * string * string>()
            use listener = new MeterListener()

            listener.InstrumentPublished <-
                fun instrument meterListener ->
                    if instrument.Meter.Name = ManifestContributionTelemetry.InstrumentationName
                       && (instrument.Name = "grace.manifest_contribution.messages"
                           || instrument.Name = "grace.manifest_contribution.processing.duration") then
                        meterListener.EnableMeasurementEvents instrument

            listener.SetMeasurementEventCallback<int64> (fun instrument _ tags _ ->
                let mutable stage = String.Empty
                let mutable outcome = String.Empty

                for tag in tags do
                    if tag.Key = "stage" then stage <- string tag.Value
                    elif tag.Key = "outcome" then outcome <- string tag.Value

                measurements.Add(instrument.Name, stage, outcome))

            listener.SetMeasurementEventCallback<double> (fun instrument _ tags _ ->
                let mutable stage = String.Empty
                let mutable outcome = String.Empty

                for tag in tags do
                    if tag.Key = "stage" then stage <- string tag.Value
                    elif tag.Key = "outcome" then outcome <- string tag.Value

                measurements.Add(instrument.Name, stage, outcome))

            listener.Start()

            let seam =
                dependencies
                    (fun _ -> Ok validEvent)
                    (fun _ _ -> Task.CompletedTask)
                    (fun _ -> Task.CompletedTask)
                    (fun _ -> Task.CompletedTask)
                    (fun _ _ _ -> Task.CompletedTask)

            do! Subscriber.processGraceEventWith seam metadata (BinaryData.FromString("{}")) CancellationToken.None

            Assert.That(
                measurements
                |> Seq.map (fun (instrumentName, _, _) -> instrumentName),
                Is.EquivalentTo(
                    [|
                        "grace.manifest_contribution.messages"
                        "grace.manifest_contribution.processing.duration"
                    |]
                )
            )

            Assert.That(measurements, Has.All.Matches<string * string * string>(fun (_, stage, outcome) -> stage = "settle" && outcome = "completed"))
        }

    /// Proves malformed and JSON-null payloads dead-letter once with fixed redacted evidence.
    [<TestCase("{not-json")>]
    [<TestCase("null")>]
    member _.MalformedPayloadDeadLettersOnceWithoutPayloadEvidence(payload: string) =
        task {
            let calls = ResizeArray<string>()
            let measurements = ResizeArray<string>()
            let mutable reason = String.Empty
            let mutable description = String.Empty
            use listener = new MeterListener()

            listener.InstrumentPublished <-
                fun instrument meterListener ->
                    if instrument.Meter.Name = ManifestContributionTelemetry.InstrumentationName
                       && (instrument.Name = "grace.manifest_contribution.messages"
                           || instrument.Name = "grace.manifest_contribution.processing.duration") then
                        meterListener.EnableMeasurementEvents instrument

            listener.SetMeasurementEventCallback<int64>(fun instrument _ _ _ -> measurements.Add instrument.Name)
            listener.SetMeasurementEventCallback<double>(fun instrument _ _ _ -> measurements.Add instrument.Name)
            listener.Start()

            let seam =
                dependencies
                    (fun _ -> Error "parser detail containing payload: secret-value")
                    (fun _ _ ->
                        calls.Add "handle"
                        Task.CompletedTask)
                    (fun _ ->
                        calls.Add "complete"
                        Task.CompletedTask)
                    (fun _ ->
                        calls.Add "abandon"
                        Task.CompletedTask)
                    (fun actualReason actualDescription _ ->
                        calls.Add "dead-letter"
                        reason <- actualReason
                        description <- actualDescription
                        Task.CompletedTask)

            do! Subscriber.processGraceEventWith seam metadata (BinaryData.FromString(payload)) CancellationToken.None

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(calls.ToArray(), Is.EqualTo([| "dead-letter" |] :> obj))
                    Assert.That(reason, Is.EqualTo("MalformedGraceEvent"))
                    Assert.That(description, Is.EqualTo(Subscriber.MalformedGraceEventDescription))
                    Assert.That(description.Length, Is.LessThanOrEqualTo(128))
                    Assert.That(description, Does.Not.Contain(payload))
                    Assert.That(description, Does.Not.Contain("secret-value"))
                    Assert.That(measurements, Is.Empty))
            )
        }

    /// Proves handler failure abandons exactly once and never attempts another settlement.
    [<Test>]
    member _.HandlerFailureAbandonsExactlyOnce() =
        task {
            let calls = ResizeArray<string>()

            let seam =
                dependencies
                    (fun _ -> Ok validEvent)
                    (fun _ _ ->
                        calls.Add "handle"
                        Task.FromException(InvalidOperationException("handler failed")))
                    (fun _ ->
                        calls.Add "complete"
                        Task.CompletedTask)
                    (fun _ ->
                        calls.Add "abandon"
                        Task.CompletedTask)
                    (fun _ _ _ ->
                        calls.Add "dead-letter"
                        Task.CompletedTask)

            do! Subscriber.processGraceEventWith seam metadata (BinaryData.FromString("{}")) CancellationToken.None

            Assert.That(calls.ToArray(), Is.EqualTo([| "handle"; "abandon" |] :> obj))
        }

    /// Proves Reference-created activity identity is available at the point where handling fails.
    [<Test>]
    member _.ReferenceCreatedActivityIsEnrichedBeforeHandlerFailure() =
        task {
            let calls = ResizeArray<string>()
            let observedTags = Dictionary<string, obj>(StringComparer.Ordinal)
            use listener = new ActivityListener()
            listener.ShouldListenTo <- fun source -> source.Name = ManifestContributionTelemetry.InstrumentationName
            listener.Sample <- fun _ -> ActivitySamplingResult.AllDataAndRecorded
            ActivitySource.AddActivityListener listener

            let seam =
                dependencies
                    (fun _ -> Ok validEvent)
                    (fun _ _ ->
                        calls.Add "handle"

                        if not (isNull Activity.Current) then
                            for tag in Activity.Current.TagObjects do
                                observedTags[tag.Key] <- tag.Value

                        Task.FromException(InvalidOperationException("handler failed")))
                    (fun _ ->
                        calls.Add "complete"
                        Task.CompletedTask)
                    (fun _ ->
                        calls.Add "abandon"
                        Task.CompletedTask)
                    (fun _ _ _ ->
                        calls.Add "dead-letter"
                        Task.CompletedTask)

            do! Subscriber.processGraceEventWith seam metadata (BinaryData.FromString("{}")) CancellationToken.None

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(calls.ToArray(), Is.EqualTo([| "handle"; "abandon" |] :> obj))
                    Assert.That(observedTags["grace.reference.id"], Is.EqualTo("11111111-1111-1111-1111-111111111111"))
                    Assert.That(observedTags["grace.repository.id"], Is.EqualTo("44444444-4444-4444-4444-444444444444"))
                    Assert.That(observedTags["grace.directory_version.id"], Is.EqualTo("66666666-6666-6666-6666-666666666666"))
                    Assert.That(observedTags["grace.reference.type"], Is.EqualTo("ReferenceType.Commit")))
            )
        }

    /// Proves cancellation during handling propagates without a replacement settlement.
    [<Test>]
    member _.CancellationDuringHandlingPropagatesWithoutSettlement() =
        task {
            let calls = ResizeArray<string>()

            let seam =
                dependencies
                    (fun _ -> Ok validEvent)
                    (fun _ _ ->
                        calls.Add "handle"
                        Task.FromCanceled(CancellationToken(true)))
                    (fun _ ->
                        calls.Add "complete"
                        Task.CompletedTask)
                    (fun _ ->
                        calls.Add "abandon"
                        Task.CompletedTask)
                    (fun _ _ _ ->
                        calls.Add "dead-letter"
                        Task.CompletedTask)

            let mutable cancelled = false

            try
                do! Subscriber.processGraceEventWith seam metadata (BinaryData.FromString("{}")) CancellationToken.None
            with
            | :? OperationCanceledException -> cancelled <- true

            Assert.That(cancelled, Is.True)
            Assert.That(calls.ToArray(), Is.EqualTo([| "handle" |] :> obj))
        }

    /// Proves cancellation observed after handling but before settlement is not relabeled as an unknown settlement.
    [<Test>]
    member _.PreSettlementCancellationPropagatesWithoutSettlementFailureOutcome() =
        task {
            let calls = ResizeArray<string>()
            let outcomes = ResizeArray<string>()
            use cancellation = new CancellationTokenSource()
            use listener = new MeterListener()

            listener.InstrumentPublished <-
                fun instrument meterListener ->
                    if instrument.Meter.Name = ManifestContributionTelemetry.InstrumentationName
                       && instrument.Name = "grace.manifest_contribution.messages" then
                        meterListener.EnableMeasurementEvents instrument

            listener.SetMeasurementEventCallback<int64> (fun _ _ tags _ ->
                for tag in tags do
                    if tag.Key = "outcome" then outcomes.Add(string tag.Value))

            listener.Start()

            let seam =
                dependencies
                    (fun _ -> Ok validEvent)
                    (fun _ _ ->
                        calls.Add "handle"
                        cancellation.Cancel()
                        Task.CompletedTask)
                    (fun _ ->
                        calls.Add "complete"
                        Task.CompletedTask)
                    (fun _ ->
                        calls.Add "abandon"
                        Task.CompletedTask)
                    (fun _ _ _ ->
                        calls.Add "dead-letter"
                        Task.CompletedTask)

            let mutable cancellationPropagated = false

            try
                do! Subscriber.processGraceEventWith seam metadata (BinaryData.FromString("{}")) cancellation.Token
            with
            | :? OperationCanceledException -> cancellationPropagated <- true

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(cancellationPropagated, Is.True)
                    Assert.That(calls.ToArray(), Is.EqualTo([| "handle" |] :> obj))
                    Assert.That(outcomes, Is.Empty))
            )
        }

    /// Proves cancellation before parsing propagates without any settlement attempt.
    [<Test>]
    member _.CancellationBeforeParsingPropagatesWithoutSettlement() =
        task {
            let calls = ResizeArray<string>()
            use cancellation = new CancellationTokenSource()
            cancellation.Cancel()

            let seam =
                dependencies
                    (fun _ ->
                        calls.Add "parse"
                        Ok validEvent)
                    (fun _ _ ->
                        calls.Add "handle"
                        Task.CompletedTask)
                    (fun _ ->
                        calls.Add "complete"
                        Task.CompletedTask)
                    (fun _ ->
                        calls.Add "abandon"
                        Task.CompletedTask)
                    (fun _ _ _ ->
                        calls.Add "dead-letter"
                        Task.CompletedTask)

            let mutable cancelled = false

            try
                do! Subscriber.processGraceEventWith seam metadata (BinaryData.FromString("{}")) cancellation.Token
            with
            | :? OperationCanceledException -> cancelled <- true

            Assert.That(cancelled, Is.True)
            Assert.That(calls, Is.Empty)
        }

    /// Proves an unknown complete result surfaces one typed failure without abandon or dead-letter fallback.
    [<Test>]
    member _.CompleteFailureDoesNotAttemptFallbackSettlement() =
        task {
            let calls = ResizeArray<string>()
            let outcomes = ResizeArray<string>()
            use listener = new MeterListener()

            listener.InstrumentPublished <-
                fun instrument meterListener ->
                    if instrument.Meter.Name = ManifestContributionTelemetry.InstrumentationName
                       && instrument.Name = "grace.manifest_contribution.messages" then
                        meterListener.EnableMeasurementEvents instrument

            listener.SetMeasurementEventCallback<int64> (fun _ _ tags _ ->
                for tag in tags do
                    if tag.Key = "outcome" then outcomes.Add(string tag.Value))

            listener.Start()

            let seam =
                dependencies
                    (fun _ -> Ok validEvent)
                    (fun _ _ ->
                        calls.Add "handle"
                        Task.CompletedTask)
                    (fun _ ->
                        calls.Add "complete"
                        Task.FromException(InvalidOperationException("complete unknown")))
                    (fun _ ->
                        calls.Add "abandon"
                        Task.CompletedTask)
                    (fun _ _ _ ->
                        calls.Add "dead-letter"
                        Task.CompletedTask)

            let mutable failed = false

            try
                do! Subscriber.processGraceEventWith seam metadata (BinaryData.FromString("{}")) CancellationToken.None
            with
            | :? Subscriber.GraceEventSettlementFailureException -> failed <- true

            Assert.That(failed, Is.True)
            Assert.That(calls.ToArray(), Is.EqualTo([| "handle"; "complete" |] :> obj))
            Assert.That(outcomes.ToArray(), Is.EqualTo([| "settlement_failed" |] :> obj))
        }

    /// Proves the production processor boundary suppresses only typed settlement failures, not cancellation.
    [<Test>]
    member _.ProcessorBoundaryPreventsAutomaticAbandonmentAndPreservesCancellation() =
        task {
            let settlementFailure = Subscriber.GraceEventSettlementFailureException("complete", InvalidOperationException("complete outcome unknown"))

            do! Subscriber.runProcessorMessageCallbackWith NullLogger.Instance metadata (fun () -> Task.FromException(settlementFailure))

            let mutable cancellationPropagated = false

            try
                do! Subscriber.runProcessorMessageCallbackWith NullLogger.Instance metadata (fun () -> Task.FromCanceled(CancellationToken(true)))
            with
            | :? OperationCanceledException -> cancellationPropagated <- true

            Assert.That(cancellationPropagated, Is.True)
        }

    /// Proves an unknown abandon result propagates without complete or dead-letter fallback.
    [<Test>]
    member _.AbandonFailureDoesNotAttemptFallbackSettlement() =
        task {
            let calls = ResizeArray<string>()

            let seam =
                dependencies
                    (fun _ -> Ok validEvent)
                    (fun _ _ ->
                        calls.Add "handle"
                        Task.FromException(InvalidOperationException("handler failed")))
                    (fun _ ->
                        calls.Add "complete"
                        Task.CompletedTask)
                    (fun _ ->
                        calls.Add "abandon"
                        Task.FromException(InvalidOperationException("abandon unknown")))
                    (fun _ _ _ ->
                        calls.Add "dead-letter"
                        Task.CompletedTask)

            let mutable failed = false

            try
                do! Subscriber.processGraceEventWith seam metadata (BinaryData.FromString("{}")) CancellationToken.None
            with
            | :? Subscriber.GraceEventSettlementFailureException -> failed <- true

            Assert.That(failed, Is.True)
            Assert.That(calls.ToArray(), Is.EqualTo([| "handle"; "abandon" |] :> obj))
        }

    /// Proves an unknown dead-letter result propagates without complete or abandon fallback.
    [<Test>]
    member _.DeadLetterFailureDoesNotAttemptFallbackSettlement() =
        task {
            let calls = ResizeArray<string>()

            let seam =
                dependencies
                    (fun _ -> Error "malformed")
                    (fun _ _ ->
                        calls.Add "handle"
                        Task.CompletedTask)
                    (fun _ ->
                        calls.Add "complete"
                        Task.CompletedTask)
                    (fun _ ->
                        calls.Add "abandon"
                        Task.CompletedTask)
                    (fun _ _ _ ->
                        calls.Add "dead-letter"
                        Task.FromException(InvalidOperationException("dead-letter unknown")))

            let mutable failed = false

            try
                do! Subscriber.processGraceEventWith seam metadata (BinaryData.FromString("bad")) CancellationToken.None
            with
            | :? Subscriber.GraceEventSettlementFailureException -> failed <- true

            Assert.That(failed, Is.True)
            Assert.That(calls.ToArray(), Is.EqualTo([| "dead-letter" |] :> obj))
        }

    /// Proves emitted metrics use only the issue-approved bounded tag keys.
    [<Test>]
    member _.MetricTagKeysAreBounded() =
        let observed = ResizeArray<string>()
        use listener = new MeterListener()

        listener.InstrumentPublished <-
            fun instrument meterListener ->
                if instrument.Meter.Name = ManifestContributionTelemetry.InstrumentationName then
                    meterListener.EnableMeasurementEvents instrument

        listener.SetMeasurementEventCallback<int64> (fun _ _ tags _ ->
            for tag in tags do
                observed.Add tag.Key)

        listener.SetMeasurementEventCallback<double> (fun _ _ tags _ ->
            for tag in tags do
                observed.Add tag.Key)

        listener.Start()

        ManifestContributionTelemetry.recordMessage ManifestContributionProcessingStage.Parse ManifestContributionMessageOutcome.Completed 1.0

        let relationship =
            ExactRelationship.ReferenceRoot
                {
                    RepositoryId = Guid.Parse("11111111-1111-1111-1111-111111111111")
                    RootDirectoryVersionId = Guid.Parse("22222222-2222-2222-2222-222222222222")
                    ReferenceId = Guid.Parse("33333333-3333-3333-3333-333333333333")
                }

        ManifestContributionTelemetry.recordRelationship ManifestContributionRelationshipOperation.EnsurePresent relationship "changed"
        ManifestContributionTelemetry.recordRedisOperation ManifestContributionRedisOperation.Get "hit"
        ManifestContributionTelemetry.recordRepairActions [| "GetOrAddExactRelationship" |] "verified_complete"

        Assert.That(observed, Is.Not.Empty)
        Assert.That(observed, Has.All.Matches<string>(fun key -> ManifestContributionTelemetry.AllowedMetricTagKeys.Contains key))

    /// Proves high-cardinality identifiers are carried by activities, not metric tags.
    [<Test>]
    member _.MessageActivityCarriesIdentifiers() =
        let observed = ResizeArray<Activity>()
        use listener = new ActivityListener()
        listener.ShouldListenTo <- fun source -> source.Name = ManifestContributionTelemetry.InstrumentationName
        listener.Sample <- fun _ -> ActivitySamplingResult.AllDataAndRecorded
        listener.ActivityStopped <- fun activity -> observed.Add activity
        ActivitySource.AddActivityListener listener

        use activity = ManifestContributionTelemetry.startMessageActivity metadata.MessageId metadata.CorrelationId metadata.DeliveryCount

        Assert.That(activity, Is.Not.Null)
        ManifestContributionTelemetry.enrichReferenceActivity "reference-789" "repository-012" "directory-version-345" "Commit"
        activity.Dispose()

        let completed = observed |> Seq.exactlyOne

        let tags =
            completed.TagObjects
            |> Seq.map (fun tag -> tag.Key, tag.Value)
            |> dict

        Assert.Multiple(
            Action (fun () ->
                Assert.That(tags["messaging.message.id"], Is.EqualTo(metadata.MessageId))
                Assert.That(tags["grace.correlation_id"], Is.EqualTo(metadata.CorrelationId))
                Assert.That(tags["messaging.servicebus.delivery_count"], Is.EqualTo(metadata.DeliveryCount))
                Assert.That(tags["grace.reference.id"], Is.EqualTo("reference-789"))
                Assert.That(tags["grace.repository.id"], Is.EqualTo("repository-012"))
                Assert.That(tags["grace.directory_version.id"], Is.EqualTo("directory-version-345"))
                Assert.That(tags["grace.reference.type"], Is.EqualTo("Commit")))
        )

    /// Proves all required bounded instruments are published by the exact application meter.
    [<Test>]
    member _.RequiredInstrumentNamesArePublished() =
        Assert.That(ManifestContributionTelemetry.InstrumentationName, Is.EqualTo("Grace.ManifestContributionAccounting"))

        let observed = HashSet<string>(StringComparer.Ordinal)
        use listener = new MeterListener()

        listener.InstrumentPublished <-
            fun instrument _ ->
                if instrument.Meter.Name = ManifestContributionTelemetry.InstrumentationName then
                    observed.Add instrument.Name |> ignore

        listener.Start()

        Assert.That(
            observed,
            Is.SupersetOf(
                [|
                    "grace.manifest_contribution.messages"
                    "grace.manifest_contribution.processing.duration"
                    "grace.manifest_contribution.relationship.writes"
                    "grace.manifest_contribution.redis.operations"
                    "grace.manifest_contribution.repair.actions"
                |]
            )
        )

    /// Proves processor SDK errors log structured broker fields and complete without a custom delay.
    [<Test>]
    member _.ProcessorErrorCallbackIsStructuredAndImmediate() =
        let observed = Dictionary<string, obj>(StringComparer.Ordinal)

        let logger =
            { new ILogger with
                member _.BeginScope<'TState>(_: 'TState) = null
                member _.IsEnabled _ = true

                member _.Log<'TState>(_, _, state: 'TState, _, _) =
                    match box state with
                    | :? IEnumerable<KeyValuePair<string, obj>> as fields ->
                        for field in fields do
                            observed[field.Key] <- field.Value
                    | _ -> ()
            }

        let callback =
            Subscriber.handleProcessorErrorWith
                logger
                (InvalidOperationException("processor failed"))
                "Receive"
                "grace-events/subscriptions/server"
                "servicebus.example"
                "processor-1"

        Assert.Multiple(
            Action (fun () ->
                Assert.That(callback.IsCompletedSuccessfully, Is.True)
                Assert.That(observed["ErrorSource"], Is.EqualTo("Receive"))
                Assert.That(observed["EntityPath"], Is.EqualTo("grace-events/subscriptions/server"))
                Assert.That(observed["FullyQualifiedNamespace"], Is.EqualTo("servicebus.example"))
                Assert.That(observed["Identifier"], Is.EqualTo("processor-1")))
        )
