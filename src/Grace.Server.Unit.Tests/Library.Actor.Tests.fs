namespace Grace.Server.Tests

open Grace.Actors
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Shared.Validation
open Grace.Types.Authorization
open Grace.Types.Common
open Grace.Types.Library
open Grace.Types.ManifestContributionWorkflow
open Grace.Types.UploadSession
open NodaTime
open Microsoft.Extensions.DependencyInjection
open NUnit.Framework
open Orleans
open Orleans.Runtime
open Orleans.Storage
open System
open System.Collections.Generic
open System.Reflection
open System.Text
open System.Threading
open System.Threading.Tasks

/// Supplies the one real grain identity needed to invoke RepositoryLibraryActor methods in a focused harness.
type LibraryTestGrainContextProxy() =
    inherit DispatchProxy()

    member val GrainId = GrainId.Create(GrainType.Create("Grace.RepositoryLibraryActor"), GrainIdKeyExtensions.CreateGuidKey(Guid.Empty)) with get, set

    override this.Invoke(methodInfo, _arguments) =
        match methodInfo.Name with
        | "get_GrainId" -> box this.GrainId
        | _ when methodInfo.ReturnType = typeof<Void> -> null
        | _ when methodInfo.ReturnType.IsValueType -> Activator.CreateInstance(methodInfo.ReturnType)
        | _ -> null

/// Stores exact Library records so authorization ordering can be exercised through the real actor methods.
type LibraryReplayTestStorage() =
    let records = Dictionary<string, obj>(StringComparer.Ordinal)
    let mutable readCount = 0

    let storageKey (grainType: string) (grainId: GrainId) = $"{grainType}|{grainId}"

    member _.ReadCount = readCount

    member _.Store<'T>(grainType, recordKey, value: 'T) = records[storageKey grainType (GrainId.Create(grainType, recordKey))] <- box value

    interface IGrainStorage with
        member _.ReadStateAsync<'T>(grainType, grainId, state: IGrainState<'T>) =
            readCount <- readCount + 1

            match records.TryGetValue(storageKey grainType grainId) with
            | true, value ->
                state.State <- unbox<'T> value
                state.ETag <- "library-replay-test"
                state.RecordExists <- true
            | _ -> state.RecordExists <- false

            Task.CompletedTask

        member _.WriteStateAsync<'T>(_, _, _: IGrainState<'T>) =
            Task.FromException(InvalidOperationException("The authorization replay harness must not write Library records."))

        member _.ClearStateAsync<'T>(_, _, _: IGrainState<'T>) =
            Task.FromException(InvalidOperationException("The authorization replay harness must not clear Library records."))

/// Verifies deterministic Library decisions, tokens, and notification effect ordering.
[<Parallelizable(ParallelScope.All)>]
type LibraryActorTests() =

    let repositoryId = Guid.Parse("a819b112-d97c-4813-b647-a02686a0eb33")
    let operationId = Guid.Parse("6aa16ee4-8df0-4aa0-a463-389e36f8e119")
    let itemId = Guid.Parse("06596842-c919-4437-951f-5a9ed9b79465")
    let contentVersionId = Guid.Parse("80faf399-08f5-4768-a0be-2c607f041162")
    let catalogVersion = Guid.Parse("e014a230-2557-45ba-95fa-eb0191dfb657")
    let timestamp = Instant.FromUtc(2026, 9, 7, 8, 9, 10)

    let envelope () =
        let properties = Dictionary<string, string>()
        properties["eventName"] <- "LibraryContentAvailable.v1"

        {
            TopicName = "grace-events"
            MessageId = $"LibraryContentAvailable/{repositoryId:D}/00000000000000017"
            Body = [| 10uy; 20uy; 30uy |]
            ContentType = "application/json"
            Subject = "LibraryContentAvailable.v1"
            CorrelationId = "library-test-correlation"
            ApplicationProperties = properties
        }

    let notificationAttempt sendFails advanceFails clearFails hasRetained =
        let effects = ResizeArray<string>()

        let send _ =
            task {
                effects.Add "send"

                if sendFails then return raise (InvalidOperationException "send failed")
            }

        let advance () =
            task {
                effects.Add "advance"

                if advanceFails then return raise (InvalidOperationException "advance failed")
            }

        let persist _ = task { effects.Add "persist" }

        let clear () =
            task {
                effects.Add "clear"

                if clearFails then return raise (InvalidOperationException "clear failed")
            }

        let run () = LibraryNotifications.attempt send advance persist clear hasRetained (envelope ())
        effects, run

    let acceptedRecord cursor =
        {
            SchemaVersion = 1
            Cursor = cursor
            RequestHash = $"request-{cursor}"
            CorrelationId = $"correlation-{cursor}"
            Change = Unchecked.defaultof<LibraryChangeDto>
            PriorNamespace = None
            PriorContentVersionId = None
            ConsumedNamespaceVersion = None
            ConsumedContentVersionId = None
            ConsumedContentRevision = None
            ConsumedSlotVersion = None
            AddedItemRecord = false
            AddedSlotRecord = false
        }

    let preparedManifest () =
        let bytes = Encoding.UTF8.GetBytes("prepared Library content")
        let blockAddress = ContentBlockAddress(ContentAddress.computeBlake3Hex bytes)

        let manifest =
            FileManifest.Create(
                ManifestAddress String.Empty,
                RabinChunking.SuiteName,
                FileContentHash(ContentAddress.computeBlake3Hex bytes),
                int64 bytes.Length,
                StoragePoolId "pool-library",
                [
                    ContentBlock.Create(blockAddress, 0L, int64 bytes.Length)
                ]
            )

        { manifest with ManifestAddress = ContentAddress.computeManifestAddressForManifest manifest }

    let preparedUpload expiresAt =
        let manifest = preparedManifest ()

        { UploadSessionDto.Default with
            UploadSessionId = Guid.Parse("918d5491-fc38-4010-886c-17541171670f")
            RepositoryId = repositoryId
            StoragePoolId = manifest.StoragePoolId
            AuthorizedScope = RelativePath "Library/918d5491-fc38-4010-886c-17541171670f"
            FileContentHash = manifest.FileContentHash
            ExpectedSize = manifest.Size
            LifecycleState = UploadSessionLifecycleState.RetentionPending
            FinalizedManifestAddress = Some manifest.ManifestAddress
            FinalizedManifest = Some manifest
            LibraryPreparation = Some { OperationId = operationId; PrincipalId = "user:test"; ExpectedSha256 = String.replicate 64 "a"; ExpiresAt = expiresAt }
        }

    /// Builds a real RepositoryLibraryActor with exact keyed records and a controllable current permission result.
    let replayActor permission (receipt: LibraryReceiptDocument) =
        let catalog = LibraryCatalogDto.CreateInitial(repositoryId, timestamp, "user:test")

        let control =
            {
                SchemaVersion = 1
                Catalog = catalog
                Epoch = Guid.Parse("d805308a-a0cc-4e9f-8050-95170e60aefd")
                CommittedCursor = 0L
                ReplayFloor = 1L
                Pending = None
                ItemRecordCount = 0
                SlotRecordCount = 0
                HistoryThrough = 0L
                NotifyThrough = 0L
                AdditiveCatalogVersions = [||]
            }

        let storage = LibraryReplayTestStorage()
        storage.Store("Grace.Library.Control.v2", repositoryId.ToString("D"), control)

        storage.Store(
            "Grace.Library.Receipt.v2",
            LibraryRecords.key [ repositoryId.ToString("D")
                                 "operation"
                                 receipt.OperationId.ToString("D") ],
            receipt
        )

        let services = ServiceCollection()

        for storageName in
            [|
                LibraryRecords.ControlStorageName
                LibraryRecords.ChangesStorageName
                LibraryRecords.CurrentStorageName
                LibraryRecords.ReceiptsStorageName
                LibraryRecords.HistoryStorageName
                LibraryRecords.BaselinesStorageName
            |] do
            services.AddKeyedSingleton<IGrainStorage>(storageName, storage)
            |> ignore

        let provider = services.BuildServiceProvider()
        let mutable authorizationCalls = 0

        let authorize =
            Func<RepositoryId, LibraryWriteAuthorization, CancellationToken, Task<PermissionCheckResult>> (fun _ _ _ ->
                authorizationCalls <- authorizationCalls + 1
                Task.FromResult permission)

        let actor = RepositoryLibraryActor(provider, null, authorize, Array.create 32 7uy)
        let context = DispatchProxy.Create<IGrainContext, LibraryTestGrainContextProxy>()

        (context :?> LibraryTestGrainContextProxy).GrainId <- GrainId.Create(
            GrainType.Create("Grace.RepositoryLibraryActor"),
            GrainIdKeyExtensions.CreateGuidKey(repositoryId)
        )

        typeof<Grain>
            .GetProperty(
                "GrainContext",
                BindingFlags.Instance
                ||| BindingFlags.Public
                ||| BindingFlags.NonPublic
            )
            .SetValue(actor, context)

        actor :> Grace.Actors.Interfaces.IRepositoryLibraryActor, storage, provider, (fun () -> authorizationCalls), catalog

    let authorization = { OwnerId = Guid.Empty; OrganizationId = Guid.Empty; Principals = Array.empty; EffectiveClaims = Array.empty }

    /// Verifies current denial prevents both permanent catalog and item receipt disclosure inside the actor turn.
    [<Test>]
    member _.CurrentPermissionDenialPrecedesStoredResultReplay() =
        task {
            let catalogResult =
                {
                    OperationId = operationId
                    Outcome = OutcomeKind.Accepted
                    LibraryCatalog = LibraryCatalogDto.CreateInitial(repositoryId, timestamp, "user:test")
                    ReasonCode = None
                    RecordedAt = timestamp
                }

            let catalogReceipt =
                { SchemaVersion = 1; OperationId = operationId; RequestHash = "catalog-request"; Outcome = LibraryOperationOutcome.CatalogResult catalogResult }

            let deniedReason = "Library permission was revoked before the actor turn."
            let catalogActor, catalogStorage, catalogServices, catalogAuthorizationCalls, catalog = replayActor (Denied deniedReason) catalogReceipt
            use _catalogServices = catalogServices

            let! catalogReplay =
                catalogActor.ChangeCatalog
                    true
                    catalog.Version
                    "Library"
                    operationId
                    catalogReceipt.RequestHash
                    "user:test"
                    authorization
                    true
                    "corr-catalog-denied"

            match catalogReplay with
            | Error reason -> Assert.That(reason, Is.EqualTo(deniedReason))
            | Ok _ -> Assert.Fail("Denied catalog replay disclosed its stored result.")

            Assert.That(catalogAuthorizationCalls (), Is.EqualTo(1))
            Assert.That(catalogStorage.ReadCount, Is.EqualTo(0))

            let rejectedReceipt =
                {
                    OperationId = operationId
                    RequestHash = "submit-request"
                    Outcome = OutcomeKind.Rejected
                    Change = None
                    ReasonCode = Some "preparedContentExpired"
                    CurrentLibraryCatalog = Some catalog
                    Rebaseline = None
                }

            let submitReceipt =
                {
                    SchemaVersion = 1
                    OperationId = operationId
                    RequestHash = rejectedReceipt.RequestHash
                    Outcome = LibraryOperationOutcome.RejectedChange rejectedReceipt
                }

            let command =
                LibraryChangeCommand.CreateDirectory(
                    operationId,
                    rejectedReceipt.RequestHash,
                    catalog.Version,
                    {
                        Parent = { Kind = "root"; LibraryPath = Some "Library"; ItemId = None }
                        Name = "denied"
                        ExpectedSlotVersion = Guid.Empty
                        ExpectedState = "vacant"
                    }
                )

            let submitActor, submitStorage, submitServices, submitAuthorizationCalls, _ = replayActor (Denied deniedReason) submitReceipt
            use _submitServices = submitServices
            let! submitReplay = submitActor.Submit command "user:test" authorization "corr-submit-denied"

            match submitReplay with
            | Error reason -> Assert.That(reason, Is.EqualTo(deniedReason))
            | Ok _ -> Assert.Fail("Denied item replay disclosed its stored receipt.")

            Assert.That(submitAuthorizationCalls (), Is.EqualTo(1))
            Assert.That(submitStorage.ReadCount, Is.EqualTo(0))
        }

    /// Verifies current permission allows exact catalog and item receipt replay through the same actor boundary.
    [<Test>]
    member _.CurrentPermissionAllowsExactStoredResultReplay() =
        task {
            let catalogResult =
                {
                    OperationId = operationId
                    Outcome = OutcomeKind.Accepted
                    LibraryCatalog = LibraryCatalogDto.CreateInitial(repositoryId, timestamp, "user:test")
                    ReasonCode = None
                    RecordedAt = timestamp
                }

            let catalogReceipt =
                { SchemaVersion = 1; OperationId = operationId; RequestHash = "catalog-request"; Outcome = LibraryOperationOutcome.CatalogResult catalogResult }

            let catalogActor, catalogStorage, catalogServices, catalogAuthorizationCalls, catalog = replayActor (Allowed "current") catalogReceipt
            use _catalogServices = catalogServices

            let! catalogReplay =
                catalogActor.ChangeCatalog
                    true
                    catalog.Version
                    "Library"
                    operationId
                    catalogReceipt.RequestHash
                    "user:test"
                    authorization
                    true
                    "corr-catalog-allowed"

            match catalogReplay with
            | Ok result -> Assert.That(result, Is.EqualTo(catalogResult))
            | Error reason -> Assert.Fail($"Allowed catalog replay failed: {reason}")

            Assert.That(catalogAuthorizationCalls (), Is.EqualTo(1))
            Assert.That(catalogStorage.ReadCount, Is.GreaterThan(0))

            let rejectedReceipt =
                {
                    OperationId = operationId
                    RequestHash = "submit-request"
                    Outcome = OutcomeKind.Rejected
                    Change = None
                    ReasonCode = Some "preparedContentExpired"
                    CurrentLibraryCatalog = Some catalog
                    Rebaseline = None
                }

            let submitReceipt =
                {
                    SchemaVersion = 1
                    OperationId = operationId
                    RequestHash = rejectedReceipt.RequestHash
                    Outcome = LibraryOperationOutcome.RejectedChange rejectedReceipt
                }

            let command =
                LibraryChangeCommand.CreateDirectory(
                    operationId,
                    rejectedReceipt.RequestHash,
                    catalog.Version,
                    {
                        Parent = { Kind = "root"; LibraryPath = Some "Library"; ItemId = None }
                        Name = "allowed"
                        ExpectedSlotVersion = Guid.Empty
                        ExpectedState = "vacant"
                    }
                )

            let submitActor, submitStorage, submitServices, submitAuthorizationCalls, _ = replayActor (Allowed "current") submitReceipt
            use _submitServices = submitServices
            let! submitReplay = submitActor.Submit command "user:test" authorization "corr-submit-allowed"

            match submitReplay with
            | Ok receipt -> Assert.That(receipt, Is.EqualTo(rejectedReceipt))
            | Error reason -> Assert.Fail($"Allowed item replay failed: {reason}")

            Assert.That(submitAuthorizationCalls (), Is.EqualTo(1))
            Assert.That(submitStorage.ReadCount, Is.GreaterThan(0))
        }

    /// A first successful send advances the durable cursor without creating failure state.
    [<Test>]
    member _.FirstNotificationSuccessAdvancesWithoutRetainingEnvelope() =
        let effects, run = notificationAttempt false false false false
        let sent = run().GetAwaiter().GetResult()

        Assert.Multiple(
            Action (fun () ->
                Assert.That(sent, Is.True)
                Assert.That(effects.ToArray(), Is.EqualTo(box [| "send"; "advance" |])))
        )

    /// A first terminal send failure stores the exact envelope and leaves progress unchanged.
    [<Test>]
    member _.FirstNotificationFailureRetainsEnvelopeWithoutAdvancing() =
        let effects, run = notificationAttempt true false false false
        let sent = run().GetAwaiter().GetResult()

        Assert.Multiple(
            Action (fun () ->
                Assert.That(sent, Is.False)
                Assert.That(effects.ToArray(), Is.EqualTo(box [| "send"; "persist" |])))
        )

    /// A retained envelope is cleared only after its retry sends and advances successfully.
    [<Test>]
    member _.RetainedNotificationSuccessAdvancesBeforeClearingEnvelope() =
        let effects, run = notificationAttempt false false false true
        let sent = run().GetAwaiter().GetResult()

        Assert.Multiple(
            Action (fun () ->
                Assert.That(sent, Is.True)
                Assert.That(effects.ToArray(), Is.EqualTo(box [| "send"; "advance"; "clear" |])))
        )

    /// A retained-envelope retry failure neither rewrites the envelope nor advances progress.
    [<Test>]
    member _.RetainedNotificationFailureLeavesExistingEnvelopeUntouched() =
        let effects, run = notificationAttempt true false false true
        let sent = run().GetAwaiter().GetResult()

        Assert.Multiple(
            Action (fun () ->
                Assert.That(sent, Is.False)
                Assert.That(effects.ToArray(), Is.EqualTo(box [| "send" |])))
        )

    /// Failure to persist progress after sending leaves retained state available for an exact retry.
    [<Test>]
    member _.NotificationAdvanceFailureDoesNotClearRetainedEnvelope() =
        let effects, run = notificationAttempt false true false true

        Assert.That(Action(fun () -> run().GetAwaiter().GetResult() |> ignore), Throws.TypeOf<InvalidOperationException>())
        Assert.That(effects.ToArray(), Is.EqualTo(box [| "send"; "advance" |]))

    /// Conflict names stay portable for Unicode input, huge extensions, and numbered retries.
    [<Test>]
    member _.ConflictNamesRemainBoundedAndDeterministicAcrossRetries() =
        let original =
            String.replicate 100 "😀"
            + "."
            + String.replicate 300 "x"

        let first = LibraryDecision.conflictName original operationId 0
        let second = LibraryDecision.conflictName original operationId 12

        Assert.Multiple(
            Action (fun () ->
                Assert.That(Encoding.UTF8.GetByteCount first, Is.LessThanOrEqualTo(Library.MaximumSegmentBytes))
                Assert.That(Encoding.UTF8.GetByteCount second, Is.LessThanOrEqualTo(Library.MaximumSegmentBytes))
                Assert.That(first, Does.EndWith(".conflict-6aa16ee48"))
                Assert.That(second, Does.EndWith(".conflict-6aa16ee48.12"))
                Assert.That(first, Is.Not.EqualTo(second))
                Assert.That(first.Contains("\uFFFD", StringComparison.Ordinal), Is.False))
        )

    /// Directory moves reject a descendant path that exceeds the repository-relative UTF-8 limit.
    [<Test>]
    member _.MovedDescendantsMustStayWithinOwnedPortablePaths() =
        let root = { Kind = "root"; LibraryPath = Some "Media"; ItemId = None }
        let movingId = Guid.Parse("66d071fb-435f-4d67-ad42-67a602691868")

        let catalog =
            {
                RepositoryId = repositoryId
                Version = catalogVersion
                Libraries = [| "Media" |]
                CreatedAt = timestamp
                CreatedBy = "user:test"
                PreviousVersion = None
            }

        let makeDirectory id parent name =
            {
                SchemaVersion = 2
                LastCursor = 1L
                HistoryTailSegment = None
                Item =
                    {
                        ItemId = id
                        ItemKind = ItemKind.Directory
                        LastChangeCursor = "cursor"
                        Namespace = Some { Parent = parent; Name = name; NamespaceVersion = Guid.NewGuid() }
                        Content = None
                        ContentRevision = None
                        Tombstone = None
                    }
            }

        let child1 = Guid.Parse("6d6f23dc-87f8-4f60-9ed6-49fd1f6f5371")
        let child2 = Guid.Parse("8d4f627a-e209-4b59-82f8-103932c95017")
        let child3 = Guid.Parse("14cd1da7-2020-4dc2-9995-517c71c4ec02")
        let child4 = Guid.Parse("ed7465d8-a3e8-419b-b7dc-716f4d9fccd7")
        let longName = String.replicate 250 "x"

        let validDocuments =
            [|
                makeDirectory child1 { Kind = "item"; LibraryPath = None; ItemId = Some movingId } "child"
                makeDirectory child2 { Kind = "item"; LibraryPath = None; ItemId = Some child1 } "grandchild"
            |]

        let oversizedDocuments =
            [|
                makeDirectory child1 { Kind = "item"; LibraryPath = None; ItemId = Some movingId } longName
                makeDirectory child2 { Kind = "item"; LibraryPath = None; ItemId = Some child1 } longName
                makeDirectory child3 { Kind = "item"; LibraryPath = None; ItemId = Some child2 } longName
                makeDirectory child4 { Kind = "item"; LibraryPath = None; ItemId = Some child3 } longName
                makeDirectory (Guid.NewGuid()) { Kind = "item"; LibraryPath = None; ItemId = Some child4 } longName
            |]

        Assert.Multiple(
            Action (fun () ->
                Assert.That(LibraryDecision.movedDescendantPathsAreValid catalog "Media" movingId "moved" validDocuments, Is.True)
                Assert.That(LibraryDecision.movedDescendantPathsAreValid catalog "Media" movingId "moved" oversizedDocuments, Is.False))
        )

    /// A temporarily visible later segment cannot move a change page beyond a missing committed cursor.
    [<Test>]
    member _.ChangePagesStopAtVisibilityGapAndResumeContiguously() =
        let first = ResizeArray<LibraryAcceptedChangeRecord>()
        let firstCursor, firstGap = LibraryQueries.appendContiguousChanges 200L 10 first [| acceptedRecord 201L |]
        let initialPage, initialPosition, initialHasMore = LibraryQueries.changePageWindow 199L 201L 10 (first.ToArray())
        let retry = ResizeArray<LibraryAcceptedChangeRecord>()

        let retryCursor, retryGap =
            LibraryQueries.appendContiguousChanges
                200L
                10
                retry
                [|
                    acceptedRecord 200L
                    acceptedRecord 201L
                |]

        let retryPage, retryPosition, retryHasMore = LibraryQueries.changePageWindow 199L 201L 10 (retry.ToArray())

        Assert.Multiple(
            Action (fun () ->
                Assert.That(first, Is.Empty)
                Assert.That(firstCursor, Is.EqualTo(200L))
                Assert.That(firstGap, Is.True)
                Assert.That(initialPage, Is.Empty)
                Assert.That(initialPosition, Is.EqualTo(199L))
                Assert.That(initialHasMore, Is.True)
                Assert.That(retry |> Seq.map (fun value -> value.Cursor), Is.EqualTo(box [| 200L; 201L |]))
                Assert.That(retryCursor, Is.EqualTo(202L))
                Assert.That(retryGap, Is.False)
                Assert.That(retryPage |> Array.map (fun value -> value.Cursor), Is.EqualTo(box [| 200L; 201L |]))
                Assert.That(retryPosition, Is.EqualTo(201L))
                Assert.That(retryHasMore, Is.False))
        )

    /// A valid preparation stops being consumable at its declared expiry boundary.
    [<Test>]
    member _.PreparedUploadExpiresAtThePersistedBoundary() =
        let upload = preparedUpload timestamp

        let before = LibraryTransfer.validatePreparedUpload (timestamp - Duration.FromTicks(1L)) repositoryId operationId "user:test" upload

        let atBoundary = LibraryTransfer.validatePreparedUpload timestamp repositoryId operationId "user:test" upload

        Assert.Multiple(
            Action (fun () ->
                match before with
                | Error reason -> Assert.Fail($"Expected valid preparation before expiry, got {reason}.")
                | Ok (binding, manifest) ->
                    Assert.That(binding.ExpiresAt, Is.EqualTo(timestamp))
                    Assert.That(manifest, Is.EqualTo(upload.FinalizedManifest.Value))

                match atBoundary with
                | Error reason -> Assert.That(reason, Is.EqualTo(RejectionReason.PreparedContentExpired))
                | Ok _ -> Assert.Fail("Expected the preparation to expire at its persisted boundary."))
        )

    /// An upload that expires during its awaited actor read is validated against the later observation time.
    [<Test>]
    member _.PreparedUploadExpiryIsObservedAfterAwaitedRead() =
        task {
            let upload = preparedUpload timestamp
            let readStarted = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let releaseRead = TaskCompletionSource<UploadSessionDto>(TaskCreationOptions.RunContinuationsAsynchronously)
            let mutable currentInstant = timestamp - Duration.FromTicks(1L)
            let mutable clockCalls = 0

            let validation =
                LibraryTransfer.readAndValidatePreparedUpload
                    (fun () ->
                        task {
                            readStarted.TrySetResult() |> ignore
                            return! releaseRead.Task
                        })
                    (fun () ->
                        clockCalls <- clockCalls + 1
                        currentInstant)
                    repositoryId
                    operationId
                    "user:test"

            do! readStarted.Task
            Assert.That(clockCalls, Is.Zero)
            currentInstant <- timestamp
            releaseRead.SetResult upload
            let! observedAt, returnedUpload, result = validation

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(clockCalls, Is.EqualTo(1))
                    Assert.That(observedAt, Is.EqualTo(timestamp))
                    Assert.That(returnedUpload, Is.EqualTo(upload))

                    match result with
                    | Error reason -> Assert.That(reason, Is.EqualTo(RejectionReason.PreparedContentExpired))
                    | Ok _ -> Assert.Fail("Expected the upload to expire while its actor read was awaiting."))
            )
        }

    /// Only the exact completed workflow can suppress a repeated tracked-manifest activation.
    [<Test>]
    member _.CompletedTrackedWorkflowMatchesExactOperationAndCounterRevision() =
        let manifest = preparedManifest ()
        let ranges = LibraryTransfer.workflowRanges manifest
        let counterOperationId = LibraryTransfer.counterOperationId operationId contentVersionId

        let completedRanges =
            ranges
            |> Array.mapi (fun index range ->
                {
                    OperationId = $"{counterOperationId}:fanout:revision:7:range:{index}:completed"
                    RepositoryId = repositoryId
                    StoragePoolId = manifest.StoragePoolId
                    ManifestAddress = manifest.ManifestAddress
                    Range = range
                })

        let completed =
            { ManifestContributionWorkflowDto.Default with
                RepositoryId = repositoryId
                StoragePoolId = manifest.StoragePoolId
                ManifestAddress = manifest.ManifestAddress
                Direction = ManifestContributionDirection.Increment
                Ranges = ranges
                CompletedRanges = completedRanges
                LifecycleState = ManifestContributionWorkflowLifecycleState.Completed
                StartOperationId = Some $"{counterOperationId}:fanout"
                LastOperationId =
                    completedRanges
                    |> Array.tryLast
                    |> Option.map (fun value -> value.OperationId)
                CounterRevision = 7L
                Revision = 2L
            }

        Assert.Multiple(
            Action (fun () ->
                Assert.That(LibraryTransfer.workflowCompletedForTrackedManifest repositoryId counterOperationId manifest ranges completed, Is.True)

                Assert.That(LibraryTransfer.workflowCompletedForTrackedManifest repositoryId "different-operation" manifest ranges completed, Is.False)

                Assert.That(
                    LibraryTransfer.workflowCompletedForTrackedManifest repositoryId counterOperationId manifest ranges { completed with CounterRevision = 0L },
                    Is.False
                )

                Assert.That(
                    LibraryTransfer.workflowCompletedForTrackedManifest
                        repositoryId
                        counterOperationId
                        manifest
                        ranges
                        { completed with LifecycleState = ManifestContributionWorkflowLifecycleState.InProgress },
                    Is.False
                ))
        )

    /// Signed Library tokens reject a changed purpose, repository, payload, and expiry boundary.
    [<Test>]
    member _.LibraryTokensBindPurposeRepositoryPayloadAndExpiry() =
        let key = Array.init 32 (fun index -> byte (index + 1))
        let epoch = Guid.Parse("c9c8f144-2700-44bd-925a-34325f38572c")
        let cursor = LibraryTokens.cursor key repositoryId epoch 17L
        let page = LibraryTokens.page key "changes" repositoryId "baseline-id" 3 200L
        let content = LibraryTokens.contentRead key repositoryId itemId contentVersionId "cursor-17" 200L

        let tamperedContent =
            content.Substring(0, content.Length - 1)
            + (if content.EndsWith("A", StringComparison.Ordinal) then "B" else "A")

        Assert.Multiple(
            Action (fun () ->
                Assert.That(LibraryTokens.tryCursor key repositoryId cursor, Is.EqualTo(Some(epoch, 17L)))

                Assert.That(
                    (LibraryTokens.tryCursor key (Guid.NewGuid()) cursor)
                        .IsNone,
                    Is.True
                )

                Assert.That(LibraryTokens.tryPage key "changes" repositoryId 199L page, Is.EqualTo(Some("baseline-id", 3)))

                Assert.That(
                    (LibraryTokens.tryPage key "bootstrap" repositoryId 199L page)
                        .IsNone,
                    Is.True
                )

                Assert.That(
                    (LibraryTokens.tryPage key "changes" repositoryId 200L page)
                        .IsNone,
                    Is.True
                )

                Assert.That(LibraryTokens.tryContentRead key 199L content, Is.EqualTo(Some(repositoryId, itemId, contentVersionId, "cursor-17")))

                Assert.That(
                    (LibraryTokens.tryContentRead key 200L content)
                        .IsNone,
                    Is.True
                )

                Assert.That(
                    (LibraryTokens.tryContentRead key 199L tamperedContent)
                        .IsNone,
                    Is.True
                ))
        )

    /// The production baseline packer streams the full item limit into deterministic byte-bounded shards, including tombstones.
    [<Test>]
    member _.BaselinePackerBoundsOneHundredThousandItemsAndReplaysExactly() =
        let namespaceVersion = Guid.Parse("1a591698-8f6f-40c1-9b6c-c2c7763b6df7")
        let parent = { Kind = "root"; LibraryPath = Some "Media"; ItemId = None }

        let item index =
            let idBytes = Array.zeroCreate<byte> 16

            BitConverter
                .GetBytes(index + 1)
                .CopyTo(idBytes, 0)

            let id = Guid idBytes

            let ns = { Parent = parent; Name = $"file-{index:D6}.bin"; NamespaceVersion = namespaceVersion }

            if index % 1000 = 0 then
                {
                    ItemId = id
                    ItemKind = ItemKind.File
                    LastChangeCursor = $"cursor-{index:D6}"
                    Namespace = None
                    Content = None
                    ContentRevision = None
                    Tombstone =
                        Some
                            {
                                DeletedAt = timestamp
                                DeletedBy = "user:baseline"
                                DeleteCursor = $"cursor-{index:D6}"
                                LastNamespace = ns
                                LastContentVersionId = Some contentVersionId
                            }
                }
            else
                {
                    ItemId = id
                    ItemKind = ItemKind.Directory
                    LastChangeCursor = $"cursor-{index:D6}"
                    Namespace = Some ns
                    Content = None
                    ContentRevision = None
                    Tombstone = None
                }

        let pack () =
            let current = ResizeArray<LibraryItemDto>()
            let fingerprints = ResizeArray<int * int * string>()
            let mutable itemCount = 0
            let mutable tombstoneCount = 0
            let mutable currentBytes = LibraryQueries.emptyBaselineShardBytes
            let mutable maximumPendingBytes = currentBytes

            let recordShard shard =
                let bytes = LibraryQueries.serializeBaselineShard shard
                fingerprints.Add((shard.Items.Length, bytes.Length, ContentAddress.computeBlake3Hex bytes))

                Assert.That(bytes.Length, Is.LessThanOrEqualTo(LibraryQueries.BaselineShardMaximumBytes))

            for index in 0..99_999 do
                let value = item index
                itemCount <- itemCount + 1
                if value.Tombstone.IsSome then tombstoneCount <- tombstoneCount + 1

                let nextBytes, completed = LibraryQueries.appendBaselineItem current currentBytes value
                currentBytes <- nextBytes
                maximumPendingBytes <- Math.Max(maximumPendingBytes, currentBytes)

                match completed with
                | Some shard -> recordShard shard
                | None -> ()

            match LibraryQueries.finishBaselineShard current currentBytes with
            | Some shard -> recordShard shard
            | None -> ()

            itemCount, tombstoneCount, maximumPendingBytes, fingerprints.ToArray()

        let firstCount, firstTombstones, firstMaximumPendingBytes, first = pack ()
        let secondCount, secondTombstones, secondMaximumPendingBytes, second = pack ()
        let totalSerializedBytes = first |> Array.sumBy (fun (_, bytes, _) -> bytes)

        TestContext.Out.WriteLine(
            $"Library baseline packer: items={firstCount}; tombstones={firstTombstones}; shards={first.Length}; largestPendingShardBytes={firstMaximumPendingBytes}; totalSerializedShardBytes={totalSerializedBytes}"
        )

        Assert.Multiple(
            Action (fun () ->
                Assert.That(firstCount, Is.EqualTo(100_000))
                Assert.That(firstTombstones, Is.EqualTo(100))
                Assert.That(first.Length, Is.GreaterThan(1))
                Assert.That(first |> Array.sumBy (fun (count, _, _) -> count), Is.EqualTo(100_000))
                Assert.That(firstMaximumPendingBytes, Is.LessThanOrEqualTo(LibraryQueries.BaselineShardMaximumBytes))
                Assert.That(secondCount, Is.EqualTo(firstCount))
                Assert.That(secondTombstones, Is.EqualTo(firstTombstones))
                Assert.That(secondMaximumPendingBytes, Is.EqualTo(firstMaximumPendingBytes))
                Assert.That(second, Is.EqualTo(box first)))
        )

/// Retains exact records across fresh actor instances and injects catalog write response failures.
type LibraryCatalogRecoveryStorage() =
    let records = Dictionary<string, obj>()
    let mutable fault = ""
    let mutable number = 0
    let key grainType grainId = string grainType + "|" + string grainId

    member _.Fault
        with set value = fault <- value

    member _.Store<'T>(kind: string, recordKey: string, value: 'T) = records[key kind (GrainId.Create(kind, recordKey))] <- box value

    member _.Control(repositoryId: Guid) =
        unbox<LibraryControlDocument> records[key "Grace.Library.Control.v2" (GrainId.Create("Grace.Library.Control.v2", repositoryId.ToString("D")))]

    interface IGrainStorage with
        member _.ReadStateAsync<'T>(kind, id, state: IGrainState<'T>) =
            match records.TryGetValue(key kind id) with
            | true, value ->
                state.State <- unbox value
                state.RecordExists <- true
                state.ETag <- "stored"
            | _ -> state.RecordExists <- false

            Task.CompletedTask

        member _.WriteStateAsync<'T>(kind, id, state: IGrainState<'T>) =
            let effect =
                if kind = "Grace.Library.Receipt.v2" then
                    "receipt"
                elif kind = "Grace.Library.Control.v2"
                     && (unbox<LibraryControlDocument> (box state.State))
                         .Pending
                         .IsNone then
                    "control"
                else
                    "other"

            if fault = "before-" + effect then
                fault <- ""
                Task.FromException(InvalidOperationException("injected before " + effect))
            else
                records[key kind id] <- box state.State
                number <- number + 1
                state.ETag <- string number

                if fault = "after-" + effect then
                    fault <- ""
                    Task.FromException(InvalidOperationException("injected after " + effect))
                else
                    Task.CompletedTask

        member _.ClearStateAsync<'T>(kind, id, _state: IGrainState<'T>) =
            records.Remove(key kind id) |> ignore
            Task.CompletedTask

/// Runs the real actor's additive-catalog admission and saved decision completion through persisted records.
[<NonParallelizable>]
type LibraryCatalogRecoveryTests() =
    let authorization = { OwnerId = Guid.Empty; OrganizationId = Guid.Empty; Principals = [||]; EffectiveClaims = [||] }

    /// Reopens the actor with current permission and the previously captured record store.
    let actor repositoryId permission (storage: LibraryCatalogRecoveryStorage) =
        let services = ServiceCollection()

        for name in
            [
                LibraryRecords.ControlStorageName
                LibraryRecords.ChangesStorageName
                LibraryRecords.CurrentStorageName
                LibraryRecords.ReceiptsStorageName
                LibraryRecords.HistoryStorageName
                LibraryRecords.BaselinesStorageName
            ] do
            services.AddKeyedSingleton<IGrainStorage>(name, storage)
            |> ignore

        let provider = services.BuildServiceProvider()
        let check = Func<RepositoryId, LibraryWriteAuthorization, CancellationToken, Task<PermissionCheckResult>>(fun _ _ _ -> Task.FromResult permission)
        let instance = RepositoryLibraryActor(provider, null, check, Array.create 32 7uy)
        let context = DispatchProxy.Create<IGrainContext, LibraryTestGrainContextProxy>()

        (context :?> LibraryTestGrainContextProxy).GrainId <- GrainId.Create(
            GrainType.Create("Grace.RepositoryLibraryActor"),
            GrainIdKeyExtensions.CreateGuidKey(repositoryId)
        )

        typeof<Grain>
            .GetProperty(
                "GrainContext",
                BindingFlags.Instance
                ||| BindingFlags.Public
                ||| BindingFlags.NonPublic
            )
            .SetValue(instance, context)

        instance :> Grace.Actors.Interfaces.IRepositoryLibraryActor

    /// Seeds the existing control record without introducing another state machine.
    let initial () =
        let repositoryId = Guid.NewGuid()
        let catalog = { LibraryCatalogDto.CreateInitial(repositoryId, getCurrentInstant (), "catalog-test") with Libraries = [| "Library" |] }

        let control =
            {
                SchemaVersion = 1
                Catalog = catalog
                Epoch = Guid.NewGuid()
                CommittedCursor = 0L
                ReplayFloor = 1L
                Pending = None
                ItemRecordCount = 0
                SlotRecordCount = 0
                HistoryThrough = 0L
                NotifyThrough = 0L
                AdditiveCatalogVersions = [||]
            }

        let storage = LibraryCatalogRecoveryStorage()
        storage.Store("Grace.Library.Control.v2", repositoryId.ToString("D"), control)
        repositoryId, storage, control

    /// Saves the exact existing catalog decision to isolate its real restart protocol from Cosmos enumeration.
    let pending repositoryId (storage: LibraryCatalogRecoveryStorage) add path =
        let current = storage.Control repositoryId
        let operationId = Guid.NewGuid()
        let version = LibraryDecision.deterministicGuid repositoryId operationId "catalog"

        let decision =
            LibraryPendingDecision.CatalogChange(operationId, "catalog-hash", current.Catalog.Version, version, add, path, getCurrentInstant (), "catalog-test")

        storage.Store("Grace.Library.Control.v2", repositoryId.ToString("D"), { current with Pending = Some decision })
        version

    /// Constructs one unchanged directory command that avoids file-upload dependencies.
    let command repositoryId version name =
        let parent = { Kind = "root"; LibraryPath = Some "Library"; ItemId = None }

        LibraryChangeCommand.CreateDirectory(
            Guid.NewGuid(),
            "request-" + name,
            version,
            { Parent = parent; Name = name; ExpectedSlotVersion = LibraryDecision.initialSlotVersion repositoryId parent name; ExpectedState = "vacant" }
        )

    /// Requires the real actor to return an ordinary receipt.
    let receipt =
        function
        | Ok value -> value
        | Error error -> failwith error

    /// Replays catalog writes before and after persisted effects, reopening the actor from its saved records.
    [<TestCase("none");
      TestCase("before-receipt");
      TestCase("after-receipt");
      TestCase("before-control");
      TestCase("after-control");
      Category("LibraryCatalogRecoveryTests")>]
    member _.CatalogRestartMaintainsExactBoundedVersions(fault) =
        task {
            let repositoryId, storage, original = initial ()
            let selected = pending repositoryId storage true "Added"
            storage.Fault <- fault

            try
                let! _ =
                    (actor repositoryId (Allowed "yes") storage)
                        .GetCatalog "first"

                ()
            with
            | :? InvalidOperationException as ex when ex.Message.StartsWith("injected") -> ()

            let reopened = actor repositoryId (Allowed "yes") storage
            let! catalog = reopened.GetCatalog "restart"
            let! duplicate = reopened.GetCatalog "duplicate"
            let current = storage.Control repositoryId
            Assert.That(catalog.Version, Is.EqualTo(selected))
            Assert.That(duplicate, Is.EqualTo(catalog))
            Assert.That(current.AdditiveCatalogVersions, Is.EqualTo<Guid>([| original.Catalog.Version |]))
            Assert.That(current.Pending.IsNone, Is.True)
            let json = serialize current
            Assert.That(deserialize<LibraryControlDocument> json, Is.EqualTo(current))
        }

    /// Validates current and multiple known old commands, unknown identities, slot checks and exact result replay.
    [<Test; Category("LibraryCatalogRecoveryTests")>]
    member _.KnownAdditiveRequestsKeepImmutableIdentityAndCurrentChecks() =
        task {
            let repositoryId, storage, original = initial ()
            let first = pending repositoryId storage true "Added"

            let! _ =
                (actor repositoryId (Allowed "yes") storage)
                    .GetCatalog "first"

            let latest = pending repositoryId storage true "Later"

            let! _ =
                (actor repositoryId (Allowed "yes") storage)
                    .GetCatalog "second"

            let current = storage.Control repositoryId
            Assert.That(current.AdditiveCatalogVersions, Is.EqualTo<Guid>([| original.Catalog.Version; first |]))
            let oldCommand = command repositoryId original.Catalog.Version "oldest"
            let oldBytes = serialize oldCommand

            let! oldResult =
                (actor repositoryId (Allowed "yes") storage)
                    .Submit
                    oldCommand
                    "catalog-test"
                    authorization
                    "oldest"

            let accepted = receipt oldResult
            Assert.That(accepted.Outcome, Is.EqualTo(OutcomeKind.Accepted), serialize accepted)
            Assert.That(accepted.OperationId, Is.EqualTo(LibraryDecision.operationId oldCommand))
            Assert.That(accepted.RequestHash, Is.EqualTo(LibraryDecision.requestHash oldCommand))
            Assert.That(serialize oldCommand, Is.EqualTo(oldBytes))

            let! replay =
                (actor repositoryId (Allowed "yes") storage)
                    .Submit
                    oldCommand
                    "catalog-test"
                    authorization
                    "replay"

            Assert.That(receipt replay, Is.EqualTo(accepted))

            let! denied =
                (actor repositoryId (Denied "revoked") storage)
                    .Submit
                    oldCommand
                    "catalog-test"
                    authorization
                    "denied"

            Assert.That((denied = Error "revoked"), Is.True)

            let! predecessor =
                (actor repositoryId (Allowed "yes") storage)
                    .Submit
                    (command repositoryId first "predecessor")
                    "catalog-test"
                    authorization
                    "predecessor"

            Assert.That((receipt predecessor).Outcome, Is.EqualTo(OutcomeKind.Accepted), serialize accepted)

            let! currentResult =
                (actor repositoryId (Allowed "yes") storage)
                    .Submit
                    (command repositoryId latest "current")
                    "catalog-test"
                    authorization
                    "current"

            Assert.That((receipt currentResult).Outcome, Is.EqualTo(OutcomeKind.Accepted), serialize accepted)

            let! occupied =
                (actor repositoryId (Allowed "yes") storage)
                    .Submit
                    (command repositoryId original.Catalog.Version "oldest")
                    "catalog-test"
                    authorization
                    "occupied"

            Assert.That((receipt occupied).ReasonCode, Is.EqualTo(Some RejectionReason.SlotOccupied))
            let unknownCommand = command repositoryId (Guid.NewGuid()) "unknown"

            let! unknown =
                (actor repositoryId (Allowed "yes") storage)
                    .Submit
                    unknownCommand
                    "catalog-test"
                    authorization
                    "unknown"

            Assert.That((receipt unknown).ReasonCode, Is.EqualTo(Some OutcomeKind.StalePolicy))

            let! again =
                (actor repositoryId (Allowed "yes") storage)
                    .Submit
                    unknownCommand
                    "catalog-test"
                    authorization
                    "unknown-replay"

            Assert.That(receipt again, Is.EqualTo(receipt unknown))
            let item = accepted.Change.Value.Item
            let staleNamespace = { ItemId = item.ItemId; ExpectedNamespaceVersion = Guid.NewGuid() }

            let! namespaceFailure =
                (actor repositoryId (Allowed "yes") storage)
                    .Submit
                    (LibraryChangeCommand.Rename(Guid.NewGuid(), "bad-namespace", original.Catalog.Version, item.ItemId, staleNamespace, "renamed"))
                    "catalog-test"
                    authorization
                    "namespace"

            Assert.That((receipt namespaceFailure).ReasonCode, Is.EqualTo(Some RejectionReason.NamespaceChanged))

            let fileItem =
                { item with
                    ItemId = Guid.NewGuid()
                    ItemKind = ItemKind.File
                    Content =
                        Some
                            {
                                ContentVersionId = Guid.NewGuid()
                                Blake3Hash = String.replicate 64 "a"
                                Sha256Hash = String.replicate 64 "b"
                                Size = 3L
                                CreatedAt = getCurrentInstant ()
                            }
                    ContentRevision = Some "unchanged"
                }

            storage.Store(
                "Grace.Library.Item.v2",
                LibraryRecords.key [ repositoryId.ToString("D")
                                     "item"
                                     fileItem.ItemId.ToString("D") ],
                { SchemaVersion = 1; Item = fileItem; LastCursor = 1L; HistoryTailSegment = None }
            )

            let namespaceCheck = { ItemId = fileItem.ItemId; ExpectedNamespaceVersion = fileItem.Namespace.Value.NamespaceVersion }

            let! contentFailure =
                (actor repositoryId (Allowed "yes") storage)
                    .Submit
                    (LibraryChangeCommand.Delete(Guid.NewGuid(), "bad-content", original.Catalog.Version, fileItem.ItemId, namespaceCheck, None))
                    "catalog-test"
                    authorization
                    "content"

            Assert.That((receipt contentFailure).ReasonCode, Is.EqualTo(Some RejectionReason.ContentChanged))

            let outside =
                LibraryChangeCommand.CreateDirectory(
                    Guid.NewGuid(),
                    "outside",
                    original.Catalog.Version,
                    {
                        Parent = { Kind = "root"; LibraryPath = Some "Outside"; ItemId = None }
                        Name = "directory"
                        ExpectedSlotVersion = Guid.Empty
                        ExpectedState = "vacant"
                    }
                )

            let mutable outsideRejected = false

            try
                let! _ =
                    (actor repositoryId (Allowed "yes") storage)
                        .Submit
                        outside
                        "catalog-test"
                        authorization
                        "path"

                ()
            with
            | :? InvalidOperationException as error -> outsideRejected <- error.Message.Contains("configured root")

            Assert.That(outsideRejected, Is.True)
        }

    /// A saved removal decision clears all older catalog allowance without changing permanent receipts.
    [<Test; Category("LibraryCatalogRecoveryTests")>]
    member _.RemovalResetsAllowanceAndRootLimitBoundsHistory() =
        task {
            let repositoryId, storage, original = initial ()
            let mutable count = 1

            while count < 128 do
                pending repositoryId storage true ("Root" + string count)
                |> ignore

                let! _ =
                    (actor repositoryId (Allowed "yes") storage)
                        .GetCatalog "add"

                count <- count + 1

            let atLimit = storage.Control repositoryId
            Assert.That(atLimit.Catalog.Libraries.Length, Is.EqualTo(128))
            Assert.That(atLimit.AdditiveCatalogVersions.Length, Is.EqualTo(127))

            let! over =
                (actor repositoryId (Allowed "yes") storage)
                    .ChangeCatalog
                    true
                    atLimit.Catalog.Version
                    "Over"
                    (Guid.NewGuid())
                    "over"
                    "catalog-test"
                    authorization
                    true
                    "over"

            Assert.That((receipt over).ReasonCode, Is.EqualTo(Some CatalogRejectionReason.LibraryLimitExceeded))

            pending repositoryId storage false "Root1"
            |> ignore

            let! _ =
                (actor repositoryId (Allowed "yes") storage)
                    .GetCatalog "remove"

            Assert.That(
                (storage.Control repositoryId)
                    .AdditiveCatalogVersions,
                Is.Empty
            )

            let! stale =
                (actor repositoryId (Allowed "yes") storage)
                    .Submit
                    (command repositoryId original.Catalog.Version "after-removal")
                    "catalog-test"
                    authorization
                    "stale"

            Assert.That((receipt stale).ReasonCode, Is.EqualTo(Some OutcomeKind.StalePolicy))
        }
