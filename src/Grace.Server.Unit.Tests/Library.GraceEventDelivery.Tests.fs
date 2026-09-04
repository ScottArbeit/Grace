namespace Grace.Server.Tests

open Grace.Actors
open Grace.Actors.Services
open Grace.Shared
open Grace.Types.Common
open Grace.Types.Events
open Grace.Types.Library
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic
open System.Threading.Tasks

/// Proves the failure-only Library GraceEvent envelope lifecycle without requiring a Service Bus namespace.
[<Parallelizable(ParallelScope.All)>]
type LibraryGraceEventDeliveryTests() =

    let repositoryId = Guid.Parse("11111111-1111-1111-1111-111111111111")
    let cursor = 42L
    let messageId = RepositoryLibrary.stableMessageId repositoryId cursor

    /// Creates one deterministic Library availability event and metadata pair.
    let graceEventAndMetadata () =
        let payload =
            LibraryContentAvailable.Create(
                repositoryId,
                "epoch-cursor",
                "available-cursor",
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Instant.FromUtc(2026, 9, 4, 12, 0),
                "corr-library-event"
            )

        let properties = Dictionary<string, string>()
        properties["zeta"] <- "last"
        properties["alpha"] <- "first"

        let metadata =
            { EventMetadata.New "corr-library-event" "RepositoryLibraryActor" with Timestamp = Instant.FromUtc(2026, 9, 4, 12, 0); Properties = properties }

        GraceEvent.LibraryContentAvailableEvent payload, metadata

    /// Creates the stable transport envelope used by every failure-path test.
    let envelope () =
        let graceEvent, metadata = graceEventAndMetadata ()

        createGraceEventEnvelope
            "grace-events"
            repositoryId
            RepositoryLibrary.FailedGraceEventRecordKind
            (RepositoryLibrary.fallbackRecordKey cursor)
            messageId
            graceEvent
            metadata

    /// Verifies retries rebuild the same stable identity, bytes, and ordered application properties.
    [<Test>]
    member _.EnvelopeIsStableAcrossAcceptedOperationRetries() =
        let first = envelope ()
        let second = envelope ()
        let message = createServiceBusMessage first

        Assert.That(first, Is.EqualTo(second))
        Assert.That(message.MessageId, Is.EqualTo(messageId))
        Assert.That(message.Body.ToArray() = first.Body, Is.True)
        Assert.That((message.ApplicationProperties.Keys |> Seq.toArray) = [| "alpha"; "graceEventType"; "zeta" |], Is.True)

    /// Verifies a successful first send performs no fallback persistence or clear.
    [<Test>]
    member _.SuccessfulInitialSendCreatesNoFallbackState() =
        task {
            let effects = ResizeArray<string>()

            let! result =
                GraceEventDelivery.attempt
                    (fun _ ->
                        effects.Add("send")
                        Task.CompletedTask)
                    (fun _ ->
                        effects.Add("persist")
                        Task.CompletedTask)
                    (fun () ->
                        effects.Add("clear")
                        Task.CompletedTask)
                    false
                    (envelope ())

            Assert.That(result, Is.EqualTo GraceEventDelivery.Delivered)
            Assert.That((effects |> Seq.toArray) = [| "send" |], Is.True)
        }

    /// Verifies a terminal send failure persists the exact envelope only after the failed send.
    [<Test>]
    member _.TerminalSendFailurePersistsExactEnvelopeAfterSend() =
        task {
            let effects = ResizeArray<string>()
            let mutable persisted = None
            let candidate = envelope ()

            let! result =
                GraceEventDelivery.attempt
                    (fun _ ->
                        effects.Add("send")
                        Task.FromException(InvalidOperationException("terminal send failure")))
                    (fun retained ->
                        effects.Add("persist")
                        persisted <- Some retained
                        Task.CompletedTask)
                    (fun () ->
                        effects.Add("clear")
                        Task.CompletedTask)
                    false
                    candidate

            Assert.That(result, Is.EqualTo GraceEventDelivery.Deferred)
            Assert.That((effects |> Seq.toArray) = [| "send"; "persist" |], Is.True)
            Assert.That(persisted, Is.EqualTo(Some candidate))
        }

    /// Verifies retry sends the retained envelope and clears it only after successful delivery.
    [<Test>]
    member _.SuccessfulFallbackRetryUsesSameMessageIdThenClears() =
        task {
            let effects = ResizeArray<string>()
            let mutable observedMessageId = String.Empty
            let candidate = envelope ()

            let! result =
                GraceEventDelivery.attempt
                    (fun retained ->
                        effects.Add("send")
                        observedMessageId <- retained.MessageId
                        Task.CompletedTask)
                    (fun _ ->
                        effects.Add("persist")
                        Task.CompletedTask)
                    (fun () ->
                        effects.Add("clear")
                        Task.CompletedTask)
                    true
                    candidate

            Assert.That(result, Is.EqualTo GraceEventDelivery.Delivered)
            Assert.That(observedMessageId, Is.EqualTo(messageId))
            Assert.That((effects |> Seq.toArray) = [| "send"; "clear" |], Is.True)
        }

    /// Verifies a failed retry leaves existing fallback state untouched for another activation.
    [<Test>]
    member _.FailedFallbackRetryDoesNotRewriteOrClearState() =
        task {
            let effects = ResizeArray<string>()

            let! result =
                GraceEventDelivery.attempt
                    (fun _ ->
                        effects.Add("send")
                        Task.FromException(InvalidOperationException("ambiguous accepted-then-throw")))
                    (fun _ ->
                        effects.Add("persist")
                        Task.CompletedTask)
                    (fun () ->
                        effects.Add("clear")
                        Task.CompletedTask)
                    true
                    (envelope ())

            Assert.That(result, Is.EqualTo GraceEventDelivery.Deferred)
            Assert.That((effects |> Seq.toArray) = [| "send" |], Is.True)
        }

    /// Verifies a stop after resend success but before clear safely resends the same identity on restart.
    [<Test>]
    member _.ClearInterruptionLeavesExactEnvelopeSafeForRestart() =
        task {
            let effects = ResizeArray<string>()
            let candidate = envelope ()

            let firstAttempt () =
                GraceEventDelivery.attempt
                    (fun retained ->
                        effects.Add($"send:{retained.MessageId}")
                        Task.CompletedTask)
                    (fun _ -> Task.CompletedTask)
                    (fun () ->
                        effects.Add("clear:interrupted")
                        Task.FromException(InvalidOperationException("process stopped before clear")))
                    true
                    candidate

            let mutable interrupted = false

            try
                let! _ = firstAttempt ()
                Assert.Fail("The simulated process stop must interrupt fallback clear.")
            with
            | :? InvalidOperationException -> interrupted <- true

            Assert.That(interrupted, Is.True)

            let! result =
                GraceEventDelivery.attempt
                    (fun retained ->
                        effects.Add($"send:{retained.MessageId}")
                        Task.CompletedTask)
                    (fun _ -> Task.CompletedTask)
                    (fun () ->
                        effects.Add("clear:succeeded")
                        Task.CompletedTask)
                    true
                    candidate

            Assert.That(result, Is.EqualTo GraceEventDelivery.Delivered)

            let expectedEffects =
                [|
                    $"send:{messageId}"
                    "clear:interrupted"
                    $"send:{messageId}"
                    "clear:succeeded"
                |]

            Assert.That((effects |> Seq.toArray) = expectedEffects, Is.True)
        }
