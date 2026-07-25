namespace Grace.Server.Unit.Tests

open Azure.Messaging.ServiceBus
open Grace.Actors.Reference
open Grace.Actors.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Common
open Grace.Types.Events
open Grace.Types.Reference
open NUnit.Framework
open System
open System.Collections.Generic
open System.Diagnostics
open System.Text.Json
open System.Threading.Tasks

/// Covers reference Actor Hash Validation behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type ReferenceActorHashValidationTests() =

    let correlationId = "reference-root-hash-validation-tests"
    let ownerId = Guid.Parse("11111111-bbbb-4444-8888-111111111111")
    let organizationId = Guid.Parse("22222222-bbbb-4444-8888-222222222222")
    let repositoryId = Guid.Parse("33333333-bbbb-4444-8888-333333333333")
    let directoryVersionId = Guid.Parse("44444444-bbbb-4444-8888-444444444444")
    let sha256Hash = Sha256Hash "root-sha256"
    let blake3Hash = Blake3Hash "root-blake3"

    let branchId = Guid.Parse("55555555-bbbb-4444-8888-555555555555")
    let referenceId = Guid.Parse("66666666-bbbb-4444-8888-666666666666")
    let referenceText = ReferenceText "matching replay"

    /// Builds directory Version With Hashes test data for the server unit reference Actor scenarios in this file.
    let directoryVersionWithHashes sha blake3 =
        DirectoryVersion.CreateWithHashes
            directoryVersionId
            ownerId
            organizationId
            repositoryId
            (RelativePath ".")
            sha
            blake3
            (List<DirectoryVersionId>())
            (List<FileVersion>())
            0L

    /// Builds child Directory Version With Hashes test data for the server unit reference Actor scenarios in this file.
    let childDirectoryVersionWithHashes sha blake3 =
        DirectoryVersion.CreateWithHashes
            directoryVersionId
            ownerId
            organizationId
            repositoryId
            (RelativePath $"child/{Guid.NewGuid():N}")
            sha
            blake3
            (List<DirectoryVersionId>())
            (List<FileVersion>())
            0L

    /// Verifies that missing Root Blake3 Fails Before Reference Creation.
    [<Test>]
    member _.MissingRootBlake3FailsBeforeReferenceCreation() =
        let directoryVersion = directoryVersionWithHashes sha256Hash (Blake3Hash String.Empty)

        let result = validateReferenceRootDirectoryVersionHashes correlationId repositoryId directoryVersionId sha256Hash blake3Hash directoryVersion

        match result with
        | Ok _ -> Assert.Fail("Expected missing root Blake3Hash to fail.")
        | Error error ->
            Assert.That(error.Error, Does.Contain("must include Blake3Hash"))
            Assert.That(error.Properties[nameof DirectoryVersionId], Is.EqualTo(string directoryVersionId))

    /// Verifies that empty Command Blake3 Fails Before Reference Creation.
    [<Test>]
    member _.EmptyCommandBlake3FailsBeforeReferenceCreation() =
        let directoryVersion = directoryVersionWithHashes sha256Hash blake3Hash

        let result =
            validateReferenceRootDirectoryVersionHashes correlationId repositoryId directoryVersionId sha256Hash (Blake3Hash String.Empty) directoryVersion

        match result with
        | Ok _ -> Assert.Fail("Expected empty command Blake3Hash to fail.")
        | Error error -> Assert.That(error.Error, Does.Contain("command must include"))

    /// Verifies that missing BLAKE3 in both the root and command still fails closed.
    [<Test>]
    member _.MissingRootAndCommandBlake3FailsBeforeReferenceCreation() =
        let directoryVersion = directoryVersionWithHashes sha256Hash (Blake3Hash String.Empty)

        let result =
            validateReferenceRootDirectoryVersionHashes correlationId repositoryId directoryVersionId sha256Hash (Blake3Hash String.Empty) directoryVersion

        match result with
        | Ok _ -> Assert.Fail("Expected missing root and command Blake3Hash values to fail.")
        | Error error -> Assert.That(error.Error, Does.Contain("must include Blake3Hash"))

    /// Verifies that non Root Directory Version Fails Before Reference Creation.
    [<Test>]
    member _.NonRootDirectoryVersionFailsBeforeReferenceCreation() =
        let directoryVersion = childDirectoryVersionWithHashes sha256Hash blake3Hash

        let result = validateReferenceRootDirectoryVersionHashes correlationId repositoryId directoryVersionId sha256Hash blake3Hash directoryVersion

        match result with
        | Ok _ -> Assert.Fail("Expected non-root DirectoryVersion to fail.")
        | Error error -> Assert.That(error.Error, Does.Contain("repository root path"))

    /// Verifies that mismatched Root Hashes Fail Before Reference Creation.
    [<Test>]
    member _.MismatchedRootHashesFailBeforeReferenceCreation() =
        let directoryVersion = directoryVersionWithHashes sha256Hash blake3Hash

        let shaResult =
            validateReferenceRootDirectoryVersionHashes correlationId repositoryId directoryVersionId (Sha256Hash "wrong-sha") blake3Hash directoryVersion

        let blakeResult =
            validateReferenceRootDirectoryVersionHashes correlationId repositoryId directoryVersionId sha256Hash (Blake3Hash "wrong-blake3") directoryVersion

        match shaResult, blakeResult with
        | Error shaError, Error blakeError ->
            Assert.That(shaError.Error, Does.Contain("Sha256Hash does not match"))
            Assert.That(blakeError.Error, Does.Contain("Blake3Hash does not match"))
        | _ -> Assert.Fail("Expected both mismatched hash validations to fail.")

    /// Verifies that create Command Replay Matches Durable Created Reference.
    [<Test>]
    member _.CreateCommandReplayMatchesDurableCreatedReference() =
        let links =
            [
                ReferenceLinkType.BasedOn(Guid.Parse("77777777-bbbb-4444-8888-777777777777"))
            ]

        let referenceDto =
            { ReferenceDto.Default with
                ReferenceId = referenceId
                OwnerId = ownerId
                OrganizationId = organizationId
                RepositoryId = repositoryId
                BranchId = branchId
                DirectoryId = directoryVersionId
                Sha256Hash = sha256Hash
                Blake3Hash = blake3Hash
                ReferenceType = ReferenceType.Commit
                ReferenceText = referenceText
                Links = links
                UpdatedAt = Some(getCurrentInstant ())
            }

        let matchingCommand =
            ReferenceCommand.Create(
                referenceId,
                ownerId,
                organizationId,
                repositoryId,
                branchId,
                directoryVersionId,
                sha256Hash,
                blake3Hash,
                ReferenceType.Commit,
                referenceText,
                links
            )

        let mismatchedCommand =
            ReferenceCommand.Create(
                referenceId,
                ownerId,
                organizationId,
                repositoryId,
                branchId,
                directoryVersionId,
                sha256Hash,
                Blake3Hash "different-blake3",
                ReferenceType.Commit,
                referenceText,
                links
            )

        Assert.That(createCommandMatchesReference referenceDto matchingCommand, Is.True)
        Assert.That(createCommandMatchesReference referenceDto mismatchedCommand, Is.False)
        Assert.That(createCommandMatchesReference ReferenceDto.Default matchingCommand, Is.False)

    /// Verifies that an empty BLAKE3 hash in a persisted Created event fails projection.
    [<Test>]
    member _.CreatedEventWithoutBlake3FailsProjection() =
        let createdEvent =
            {
                Event =
                    ReferenceEventType.Created(
                        referenceId,
                        ownerId,
                        organizationId,
                        repositoryId,
                        branchId,
                        directoryVersionId,
                        sha256Hash,
                        Blake3Hash String.Empty,
                        ReferenceType.Commit,
                        ReferenceText "invalid commit",
                        Seq.empty
                    )
                Metadata =
                    {
                        Timestamp = getCurrentInstant ()
                        CorrelationId = correlationId
                        Principal = "projection-test"
                        ClientType = None
                        Properties = Dictionary<string, string>()
                    }
            }

        Assert.Throws<ArgumentException>(
            Action (fun () ->
                ReferenceDto.UpdateDto createdEvent ReferenceDto.Default
                |> ignore)
        )
        |> ignore

    /// Verifies that Reference creation persists durable state before strict publication begins.
    [<Test>]
    member _.ReferenceCreatedPersistencePrecedesStrictPublication() =
        task {
            let calls = ResizeArray<string>()

            do!
                persistReferenceCreatedThenPublish
                    (fun () ->
                        calls.Add("persist")
                        Task.CompletedTask)
                    (fun () ->
                        calls.Add("publish")
                        Task.CompletedTask)

            Assert.That(calls.Count, Is.EqualTo(2))
            Assert.That(calls[0], Is.EqualTo("persist"))
            Assert.That(calls[1], Is.EqualTo("publish"))
        }

    /// Measures the fixed durable-save and broker-send foreground work against small and large manifest graphs.
    [<Test>]
    member _.ReferenceCreatedForegroundWorkIsIndependentOfManifestGraphSize() =
        task {
            let iterations = 10_000

            let measure manifestGraphSize =
                task {
                    let manifestGraph = Array.zeroCreate<byte> manifestGraphSize
                    let mutable persistenceCalls = 0
                    let mutable publicationCalls = 0
                    let stopwatch = Stopwatch.StartNew()

                    for _ in 1..iterations do
                        do!
                            persistReferenceCreatedThenPublish
                                (fun () ->
                                    persistenceCalls <- persistenceCalls + 1
                                    Task.CompletedTask)
                                (fun () ->
                                    publicationCalls <- publicationCalls + 1
                                    Task.CompletedTask)

                    stopwatch.Stop()
                    GC.KeepAlive(manifestGraph)
                    return struct (persistenceCalls, publicationCalls, stopwatch.Elapsed)
                }

            let! struct (smallPersistenceCalls, smallPublicationCalls, smallElapsed) = measure 1
            let! struct (largePersistenceCalls, largePublicationCalls, largeElapsed) = measure 10_000

            TestContext.Progress.WriteLine(
                $"MCA foreground measurement: iterations={iterations}; smallGraph=1; "
                + $"calls={smallPersistenceCalls + smallPublicationCalls}; elapsedMs={smallElapsed.TotalMilliseconds:F3}; "
                + $"largeGraph=10000; calls={largePersistenceCalls + largePublicationCalls}; elapsedMs={largeElapsed.TotalMilliseconds:F3}"
            )

            Assert.That(smallPersistenceCalls, Is.EqualTo(iterations))
            Assert.That(smallPublicationCalls, Is.EqualTo(iterations))
            Assert.That(largePersistenceCalls, Is.EqualTo(iterations))
            Assert.That(largePublicationCalls, Is.EqualTo(iterations))
            Assert.That(smallElapsed, Is.LessThan(TimeSpan.FromSeconds(1.0)))
            Assert.That(largeElapsed, Is.LessThan(TimeSpan.FromSeconds(1.0)))
        }

    /// Verifies that an unknown publication outcome preserves the completed persistence step and propagates failure.
    [<Test>]
    member _.ReferenceCreatedPublicationFailureDoesNotRepeatPersistence() =
        task {
            let mutable persistenceCount = 0
            let mutable publicationCount = 0

            let action () =
                persistReferenceCreatedThenPublish
                    (fun () ->
                        persistenceCount <- persistenceCount + 1
                        Task.CompletedTask)
                    (fun () ->
                        publicationCount <- publicationCount + 1
                        Task.FromException(InvalidOperationException("broker outcome unknown")))

            let error = Assert.ThrowsAsync<InvalidOperationException>(Func<Task>(fun () -> action () :> Task))

            Assert.That(error.Message, Is.EqualTo("broker outcome unknown"))
            Assert.That(persistenceCount, Is.EqualTo(1))
            Assert.That(publicationCount, Is.EqualTo(1))
        }

    /// Verifies that the strict Reference Created envelope uses the deterministic broker identity and real GraceEvent body.
    [<Test>]
    member _.ReferenceCreatedEnvelopeUsesDeterministicMessageIdentity() =
        let metadata = EventMetadata.New correlationId "strict-reference-publisher-test"
        metadata.Properties[ nameof RepositoryId ] <- string repositoryId

        let referenceEvent =
            {
                Event =
                    ReferenceEventType.Created(
                        referenceId,
                        ownerId,
                        organizationId,
                        repositoryId,
                        branchId,
                        directoryVersionId,
                        sha256Hash,
                        blake3Hash,
                        ReferenceType.Commit,
                        referenceText,
                        []
                    )
                Metadata = metadata
            }

        let message: ServiceBusMessage = createReferenceCreatedServiceBusMessage referenceEvent
        let body = JsonSerializer.Deserialize<GraceEvent>(message.Body.ToArray(), Grace.Shared.Constants.JsonSerializerOptions)

        Assert.That(message.MessageId, Is.EqualTo($"Reference/{referenceId}/Created"))
        Assert.That(message.CorrelationId, Is.EqualTo(correlationId))
        Assert.That(message.Subject, Is.EqualTo("GraceEvent"))
        Assert.That(message.ApplicationProperties["graceEventType"], Is.EqualTo("GraceEvent.ReferenceEvent"))

        match body with
        | GraceEvent.ReferenceEvent bodyEvent -> Assert.That(bodyEvent.Event, Is.EqualTo(referenceEvent.Event))
        | _ -> Assert.Fail("Expected a ReferenceEvent GraceEvent body.")
