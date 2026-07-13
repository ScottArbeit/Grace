namespace Grace.Server.Unit.Tests

open Grace.Actors.ArtifactGrantSigningKeyActor
open Grace.Shared.ArtifactGrant
open Grace.Types.ArtifactGrant
open Grace.Types.Common
open Grace.Types.MaterializationPlan
open Microsoft.Extensions.Logging.Abstractions
open NodaTime
open NUnit.Framework
open Orleans.Runtime
open System
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks

/// Provides controllable actor persistence for key lifecycle and failure-boundary tests.
type private FakePersistentState
    (
        initialState: ArtifactGrantSigningKeyRingState,
        recordExists: bool,
        ?failWrites: bool,
        ?failReads: bool,
        ?initiallyLoaded: bool
    ) =
    let mutable value = initialState
    let mutable exists = recordExists
    let mutable fail = defaultArg failWrites false
    let failRead = defaultArg failReads false
    let mutable loaded = defaultArg initiallyLoaded true
    let mutable writes = 0

    /// Returns how many durable writes were attempted.
    member _.WriteCount = writes

    /// Returns the state currently visible through the actor persistence facet.
    member _.State = value

    /// Makes the persistence facet readable after grain construction, matching Orleans activation order.
    member _.MarkLoaded() = loaded <- true

    /// Controls whether the next state write fails like the configured actor store.
    member _.FailWrites
        with get () = fail
        and set value = fail <- value

    interface IPersistentState<ArtifactGrantSigningKeyRingState> with
        member _.State
            with get () = value
            and set next = value <- next

        member _.Etag = null

        member _.RecordExists =
            if not loaded then invalidOp "State has not yet been loaded"
            if failRead then invalidOp "actor storage read failed"

            exists

        member _.ReadStateAsync() = Task.CompletedTask

        member _.WriteStateAsync() =
            writes <- writes + 1

            if fail then
                Task.FromException(InvalidOperationException("actor storage write failed"))
            else
                exists <- true
                Task.CompletedTask

        member _.ClearStateAsync() =
            value <- ArtifactGrantSigningKeyRingState.Empty
            exists <- false
            Task.CompletedTask

        member this.ReadStateAsync(_cancellationToken: CancellationToken) = (this :> IPersistentState<_>).ReadStateAsync()
        member this.WriteStateAsync(_cancellationToken: CancellationToken) = (this :> IPersistentState<_>).WriteStateAsync()
        member this.ClearStateAsync(_cancellationToken: CancellationToken) = (this :> IPersistentState<_>).ClearStateAsync()

/// Covers durable deployment-wide artifact signing-key ownership and recovery.
[<Parallelizable(ParallelScope.All)>]
type ArtifactGrantSigningKeyActorTests() =

    let now = Instant.FromUtc(2026, 7, 9, 12, 0)
    let cacheId = "cache-service-client"
    let targetRoot = DirectoryVersionId.Parse "11111111-1111-1111-1111-111111111111"
    let artifactIdentity = "GraceZipFiles/11111111-1111-1111-1111-111111111111.zip"

    /// Creates one canonical ephemeral holder public key for issuance tests.
    let holderPublicKey () =
        use key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256)
        exportHolderPublicKey key

    /// Builds a valid actor request containing only authenticated user identity, not caller Principal text.
    let issueRequest () : ArtifactGrantSigningRequest =
        {
            RequesterPrincipalType = ArtifactGrantRequesterPrincipalType.User
            RequesterPrincipalId = "user-11111111-1111-1111-1111-111111111111"
            HolderPublicKey = holderPublicKey ()
            CacheId = cacheId
            TargetRootDirectoryVersionId = targetRoot
            ExecutionMode = MaterializationExecutionMode.CacheRequired
            ArtifactIdentities = [| artifactIdentity |]
            RequestedTtl = None
        }

    /// Creates a store over controllable persisted actor state.
    let store (state: FakePersistentState) = ArtifactGrantSigningKeyStore(state, NullLogger.Instance)

    /// Creates one persisted signing-key entry for durable-state validation tests.
    let persistedKey (curve: ECCurve) createdAt activeUntil =
        use key = ECDsa.Create(curve)

        {
            KeyId = $"persisted-{Guid.NewGuid():N}"
            CreatedAt = createdAt
            ActiveUntil = activeUntil
            ExpiresAt = activeUntil.Plus ArtifactGrantContract.MaximumAcceptedGrantTtl
            PrivateKeyPkcs8 = key.ExportPkcs8PrivateKey()
        }

    /// Wraps retained key entries in the unchanged durable ring shape.
    let persistedRing keys = { Class = nameof ArtifactGrantSigningKeyRingState; Keys = keys }

    /// Unwraps successful issuance while preserving a useful test failure.
    let issuedGrant result =
        match result with
        | Ok grant -> grant
        | Error error -> failwith (ArtifactGrantIssueError.toMessage error)

    /// Builds the grant-only validation request used to prove recovered key publication.
    let validationRequest () = ArtifactGrantValidationRequest.Create(cacheId, targetRoot, MaterializationExecutionMode.CacheRequired, artifactIdentity)

    [<Test>]
    member _.``store construction waits for Orleans persistent state loading``() =
        task {
            let state = FakePersistentState(ArtifactGrantSigningKeyRingState.Empty, false, initiallyLoaded = false)
            let keyStore = store state
            state.MarkLoaded()

            let! keys = keyStore.PublishValidationKeys now
            Assert.That(keys.Keys, Has.Count.EqualTo 1)
            Assert.That(state.WriteCount, Is.EqualTo 1)
        }

    [<Test>]
    member _.``first key is durably written before a grant or key set is returned``() =
        task {
            let state = FakePersistentState(ArtifactGrantSigningKeyRingState.Empty, false)
            let keyStore = store state
            let request = issueRequest ()
            let! grant = keyStore.IssueGrant(request, now)
            let! keys = keyStore.PublishValidationKeys now
            let grant = issuedGrant grant

            Assert.That(state.WriteCount, Is.EqualTo 1)
            Assert.That(state.State.Keys, Has.Length.EqualTo 1)
            Assert.That(keys.Keys |> Seq.map (fun key -> key.KeyId), Does.Contain grant.Header.KeyId)
            Assert.That(grant.Payload.RequesterPrincipalId, Is.EqualTo request.RequesterPrincipalId)
            Assert.That(grant.Payload.HolderKeyThumbprint, Is.EqualTo(holderKeyThumbprint request.HolderPublicKey))
            Assert.That(grant.Payload.NotBefore, Is.EqualTo grant.Payload.IssuedAt)
        }

    [<Test>]
    member _.``publication rotates only after the signer cache horizon boundary and persists before return``() =
        task {
            let activeUntil = now.Plus ArtifactGrantContract.SigningKeyActiveLifetime
            let original = persistedKey ECCurve.NamedCurves.nistP256 now activeUntil
            let state = FakePersistentState(persistedRing [| original |], true)
            let keyStore = store state
            let boundary = activeUntil.Minus ArtifactGrantContract.ValidationKeyCacheTtl

            let! immediatelyBefore = keyStore.PublishValidationKeys(boundary.Minus(Duration.FromMilliseconds 1L))
            let! exactlyAt = keyStore.PublishValidationKeys boundary

            Assert.That(state.WriteCount, Is.EqualTo 0)

            Assert.That(
                immediatelyBefore.Keys
                |> Seq.map (fun key -> key.KeyId),
                Is.EquivalentTo [| original.KeyId |]
            )

            Assert.That(exactlyAt.Keys |> Seq.map (fun key -> key.KeyId), Is.EquivalentTo [| original.KeyId |])

            let! immediatelyAfter = keyStore.PublishValidationKeys(boundary.Plus(Duration.FromMilliseconds 1L))

            let persistedIds =
                state.State.Keys
                |> Seq.map (fun key -> key.KeyId)
                |> Set.ofSeq

            let publishedIds =
                immediatelyAfter.Keys
                |> Seq.map (fun key -> key.KeyId)
                |> Set.ofSeq

            Assert.That(state.WriteCount, Is.EqualTo 1)
            Assert.That(state.State.Keys, Has.Length.EqualTo 2)
            Assert.That((publishedIds = persistedIds), Is.True)
            Assert.That(publishedIds, Does.Contain original.KeyId)
        }

    [<Test>]
    member _.``failed horizon persistence exposes neither a publication nor a usable replacement signer``() =
        task {
            let activeUntil = now.Plus ArtifactGrantContract.SigningKeyActiveLifetime
            let original = persistedKey ECCurve.NamedCurves.nistP256 now activeUntil
            let durable = persistedRing [| original |]
            let state = FakePersistentState(durable, true, failWrites = true)
            let keyStore = store state

            let publicationTime =
                activeUntil
                    .Minus(ArtifactGrantContract.ValidationKeyCacheTtl)
                    .Plus(Duration.FromMilliseconds 1L)

            let failure = Assert.ThrowsAsync<InvalidOperationException>(Func<Task>(fun () -> keyStore.PublishValidationKeys(publicationTime) :> Task))

            Assert.That(failure.Message, Is.EqualTo "actor storage write failed")
            Assert.That(state.State, Is.SameAs durable)
            Assert.That(state.WriteCount, Is.EqualTo 1)

            state.FailWrites <- false
            let! issuedAfterExpiry = keyStore.IssueGrant(issueRequest (), activeUntil)
            let replacementGrant = issuedGrant issuedAfterExpiry

            Assert.That(state.WriteCount, Is.EqualTo 2)
            Assert.That(replacementGrant.Header.KeyId, Is.EqualTo state.State.Keys[0].KeyId)
            Assert.That(replacementGrant.Header.KeyId, Is.Not.EqualTo original.KeyId)
        }

    [<Test>]
    member _.``cache publication spanning rotation contains the later strict signer``() =
        task {
            let activeUntil = now.Plus ArtifactGrantContract.SigningKeyActiveLifetime
            let original = persistedKey ECCurve.NamedCurves.nistP256 now activeUntil
            let state = FakePersistentState(persistedRing [| original |], true)
            let keyStore = store state

            let publishedAt =
                activeUntil
                    .Minus(ArtifactGrantContract.ValidationKeyCacheTtl)
                    .Plus(Duration.FromMilliseconds 1L)

            let! cachedPublication = keyStore.PublishValidationKeys publishedAt
            let! beforeExpiryResult = keyStore.IssueGrant(issueRequest (), activeUntil.Minus(Duration.FromMilliseconds 1L))
            let! atExpiryResult = keyStore.IssueGrant(issueRequest (), activeUntil)
            let beforeExpiry = issuedGrant beforeExpiryResult
            let atExpiry = issuedGrant atExpiryResult

            Assert.That(atExpiry.Header.KeyId, Is.Not.EqualTo original.KeyId)

            Assert.That(
                cachedPublication.Keys
                |> Seq.map (fun key -> key.KeyId),
                Does.Contain original.KeyId
            )

            Assert.That(
                cachedPublication.Keys
                |> Seq.map (fun key -> key.KeyId),
                Does.Contain beforeExpiry.Header.KeyId
            )

            Assert.That(
                cachedPublication.Keys
                |> Seq.map (fun key -> key.KeyId),
                Does.Contain atExpiry.Header.KeyId
            )

            match validateWithKeySet activeUntil cachedPublication (validationRequest ()) atExpiry with
            | Ok () -> ()
            | Error error -> Assert.Fail(ArtifactGrantValidationError.toMessage error)
        }

    [<Test>]
    member _.``new activation recovers the same live key set and validates an unexpired grant``() =
        task {
            let firstState = FakePersistentState(ArtifactGrantSigningKeyRingState.Empty, false)
            let firstStore = store firstState
            let! grantResult = firstStore.IssueGrant(issueRequest (), now)
            let grant = issuedGrant grantResult
            let persisted = firstState.State

            let movedState = FakePersistentState(persisted, true)
            let movedStore = store movedState
            let! recoveredKeys = movedStore.PublishValidationKeys now

            Assert.That(movedState.WriteCount, Is.EqualTo 0)

            Assert.That(
                recoveredKeys.Keys
                |> Seq.map (fun key -> key.KeyId),
                Does.Contain grant.Header.KeyId
            )

            match validateWithKeySet now recoveredKeys (validationRequest ()) grant with
            | Ok () -> ()
            | Error error -> Assert.Fail(ArtifactGrantValidationError.toMessage error)
        }

    [<Test>]
    member _.``storage write failure returns no grant and leaves no process-local replacement``() =
        task {
            let state = FakePersistentState(ArtifactGrantSigningKeyRingState.Empty, false, failWrites = true)
            let keyStore = store state

            let caught = Assert.ThrowsAsync<InvalidOperationException>(Func<Task>(fun () -> keyStore.IssueGrant(issueRequest (), now) :> Task))
            Assert.That(caught.Message, Is.EqualTo "actor storage write failed")
            Assert.That(state.State.Keys, Is.Empty)

            state.FailWrites <- false
            let! recoveredGrant = keyStore.IssueGrant(issueRequest (), now)
            let recoveredGrant = issuedGrant recoveredGrant
            Assert.That(state.State.Keys, Has.Length.EqualTo 1)
            Assert.That(recoveredGrant.Header.KeyId, Is.EqualTo state.State.Keys[0].KeyId)
        }

    [<Test>]
    member _.``storage read and malformed persisted state failures escape without a default key set``() =
        task {
            let readFailure = FakePersistentState(ArtifactGrantSigningKeyRingState.Empty, false, failReads = true)
            let readStore = store readFailure

            let readException = Assert.ThrowsAsync<InvalidOperationException>(Func<Task>(fun () -> readStore.PublishValidationKeys(now) :> Task))

            Assert.That(readException.Message, Is.EqualTo "actor storage read failed")

            let malformedState = { ArtifactGrantSigningKeyRingState.Empty with Class = "malformed" }
            let malformedStore = store (FakePersistentState(malformedState, true))

            let malformedException = Assert.ThrowsAsync<InvalidOperationException>(Func<Task>(fun () -> malformedStore.PublishValidationKeys(now) :> Task))

            Assert.That(malformedException.Message, Is.EqualTo "Artifact grant signing-key actor state is malformed.")
        }

    [<Test>]
    member _.``valid persisted P-256 private material survives activation and remains usable``() =
        task {
            let retained = persistedKey ECCurve.NamedCurves.nistP256 now (now.Plus ArtifactGrantContract.SigningKeyActiveLifetime)
            let state = FakePersistentState(persistedRing [| retained |], true)
            let keyStore = store state

            let! grantResult = keyStore.IssueGrant(issueRequest (), now)
            let grant = issuedGrant grantResult
            let! publication = keyStore.PublishValidationKeys now

            Assert.That(grant.Header.KeyId, Is.EqualTo retained.KeyId)
            Assert.That(publication.Keys |> Seq.map (fun key -> key.KeyId), Does.Contain retained.KeyId)
            Assert.That(state.WriteCount, Is.EqualTo 0)
        }

    [<Test>]
    member _.``adjacent and non-adjacent duplicate key ids fail the whole ring without writes or results``() =
        task {
            let activeUntil = now.Plus ArtifactGrantContract.SigningKeyActiveLifetime
            let first = persistedKey ECCurve.NamedCurves.nistP256 now activeUntil
            let second = persistedKey ECCurve.NamedCurves.nistP256 now activeUntil
            let duplicate = { persistedKey ECCurve.NamedCurves.nistP256 now activeUntil with KeyId = first.KeyId }
            let uniqueState = FakePersistentState(persistedRing [| first; second |], true)
            let uniqueStore = store uniqueState

            let! uniqueGrantResult = uniqueStore.IssueGrant(issueRequest (), now)
            let! uniquePublication = uniqueStore.PublishValidationKeys now
            let uniqueGrant = issuedGrant uniqueGrantResult

            Assert.That(
                uniquePublication.Keys
                |> Seq.map (fun key -> key.KeyId),
                Does.Contain uniqueGrant.Header.KeyId
            )

            Assert.That(
                uniquePublication.Keys
                |> Seq.map (fun key -> key.KeyId),
                Is.EquivalentTo [| first.KeyId
                                   second.KeyId |]
            )

            Assert.That(uniqueState.WriteCount, Is.EqualTo 0)

            let rings =
                [|
                    persistedRing [| first; duplicate |]
                    persistedRing [| first
                                     second
                                     duplicate |]
                    persistedRing [| second
                                     first
                                     duplicate
                                     persistedKey ECCurve.NamedCurves.nistP256 now activeUntil |]
                |]

            for durable in rings do
                let publicationState = FakePersistentState(durable, true)
                let issuanceState = FakePersistentState(durable, true)

                Assert.ThrowsAsync<InvalidOperationException>(
                    Func<Task> (fun () ->
                        store publicationState
                        |> fun keyStore -> keyStore.PublishValidationKeys(now) :> Task)
                )
                |> ignore

                Assert.ThrowsAsync<InvalidOperationException>(
                    Func<Task> (fun () ->
                        store issuanceState
                        |> fun keyStore -> keyStore.IssueGrant(issueRequest (), now) :> Task)
                )
                |> ignore

                Assert.That(publicationState.WriteCount, Is.EqualTo 0)
                Assert.That(issuanceState.WriteCount, Is.EqualTo 0)
                Assert.That(publicationState.State, Is.SameAs durable)
                Assert.That(issuanceState.State, Is.SameAs durable)
        }

    [<Test>]
    member _.``canonical key lifetimes recover while every mismatched duration fails the whole ring``() =
        task {
            let precisionStep = Duration.FromMilliseconds 1L
            let activeUntil = now.Plus ArtifactGrantContract.SigningKeyActiveLifetime
            let valid = persistedKey ECCurve.NamedCurves.nistP256 now activeUntil
            let validState = FakePersistentState(persistedRing [| valid |], true)
            let validStore = store validState

            let! grantResult = validStore.IssueGrant(issueRequest (), now)
            let! publication = validStore.PublishValidationKeys now

            Assert.That((issuedGrant grantResult).Header.KeyId, Is.EqualTo valid.KeyId)
            Assert.That(publication.Keys |> Seq.map (fun key -> key.KeyId), Does.Contain valid.KeyId)
            Assert.That(validState.WriteCount, Is.EqualTo 0)

            let invalidKeys =
                [|
                    { valid with KeyId = "short-active"; ActiveUntil = valid.ActiveUntil.Minus precisionStep }
                    { valid with KeyId = "long-active"; ActiveUntil = valid.ActiveUntil.Plus precisionStep }
                    { valid with KeyId = "short-overlap"; ExpiresAt = valid.ExpiresAt.Minus precisionStep }
                    { valid with KeyId = "long-overlap"; ExpiresAt = valid.ExpiresAt.Plus precisionStep }
                    { valid with KeyId = "shifted-created"; CreatedAt = valid.CreatedAt.Plus precisionStep }
                |]

            for invalid in invalidKeys do
                let durable = persistedRing [| valid; invalid |]
                let publicationState = FakePersistentState(durable, true)
                let issuanceState = FakePersistentState(durable, true)

                Assert.ThrowsAsync<InvalidOperationException>(
                    Func<Task> (fun () ->
                        store publicationState
                        |> fun keyStore -> keyStore.PublishValidationKeys(now) :> Task)
                )
                |> ignore

                Assert.ThrowsAsync<InvalidOperationException>(
                    Func<Task> (fun () ->
                        store issuanceState
                        |> fun keyStore -> keyStore.IssueGrant(issueRequest (), now) :> Task)
                )
                |> ignore

                Assert.That(publicationState.WriteCount, Is.EqualTo 0)
                Assert.That(issuanceState.WriteCount, Is.EqualTo 0)
                Assert.That(publicationState.State, Is.SameAs durable)
                Assert.That(issuanceState.State, Is.SameAs durable)
        }

    [<Test>]
    member _.``wrong curve malformed PKCS8 and non-32-byte coordinates fail the whole durable state without repair``() =
        task {
            let activeUntil = now.Plus ArtifactGrantContract.SigningKeyActiveLifetime
            let valid = persistedKey ECCurve.NamedCurves.nistP256 now activeUntil
            let p384 = persistedKey ECCurve.NamedCurves.nistP384 now activeUntil
            let p521 = persistedKey ECCurve.NamedCurves.nistP521 now activeUntil
            let malformed = { valid with KeyId = "malformed-pkcs8"; PrivateKeyPkcs8 = [| 1uy; 2uy; 3uy |] }

            for invalid in [| p384; p521; malformed |] do
                let durable = persistedRing [| valid; invalid |]
                let publicationState = FakePersistentState(durable, true)
                let issuanceState = FakePersistentState(durable, true)
                let publicationStore = store publicationState
                let issuanceStore = store issuanceState

                Assert.ThrowsAsync<InvalidOperationException>(Func<Task>(fun () -> publicationStore.PublishValidationKeys(now) :> Task))
                |> ignore

                Assert.ThrowsAsync<InvalidOperationException>(Func<Task>(fun () -> issuanceStore.IssueGrant(issueRequest (), now) :> Task))
                |> ignore

                Assert.That(publicationState.WriteCount, Is.EqualTo 0)
                Assert.That(issuanceState.WriteCount, Is.EqualTo 0)
                Assert.That(publicationState.State, Is.SameAs durable)
                Assert.That(issuanceState.State, Is.SameAs durable)
        }

    [<Test>]
    member _.``rotation preserves overlap keys until the final accepted grant window ends``() =
        task {
            let state = FakePersistentState(ArtifactGrantSigningKeyRingState.Empty, false)
            let keyStore = store state
            let! firstGrantResult = keyStore.IssueGrant(issueRequest (), now)
            let firstGrant = issuedGrant firstGrantResult

            let rotatedAt =
                now
                    .Plus(ArtifactGrantContract.SigningKeyActiveLifetime)
                    .Plus(Duration.FromTicks 1L)

            let! secondGrantResult = keyStore.IssueGrant(issueRequest (), rotatedAt)
            let secondGrant = issuedGrant secondGrantResult
            let! overlap = keyStore.PublishValidationKeys rotatedAt

            Assert.That(secondGrant.Header.KeyId, Is.Not.EqualTo firstGrant.Header.KeyId)
            Assert.That(overlap.Keys |> Seq.map (fun key -> key.KeyId), Does.Contain firstGrant.Header.KeyId)
            Assert.That(overlap.Keys |> Seq.map (fun key -> key.KeyId), Does.Contain secondGrant.Header.KeyId)

            let afterOverlap =
                now
                    .Plus(ArtifactGrantContract.SigningKeyActiveLifetime)
                    .Plus(ArtifactGrantContract.MaximumAcceptedGrantTtl)
                    .Plus(ArtifactGrantContract.MaximumProofClockSkew)
                    .Plus(Duration.FromMilliseconds 1L)

            let! retired = keyStore.PublishValidationKeys afterOverlap
            Assert.That(retired.Keys |> Seq.map (fun key -> key.KeyId), Does.Not.Contain firstGrant.Header.KeyId)
        }

    [<Test>]
    member _.``validation key remains published through final proof skew boundary but cannot sign after expiry``() =
        task {
            let state = FakePersistentState(ArtifactGrantSigningKeyRingState.Empty, false)
            let keyStore = store state
            let! firstGrantResult = keyStore.IssueGrant(issueRequest (), now)
            let firstGrant = issuedGrant firstGrantResult

            let firstKey =
                state.State.Keys
                |> Array.find (fun key -> key.KeyId = firstGrant.Header.KeyId)

            let finalAcceptedInstant = firstKey.ExpiresAt.Plus ArtifactGrantContract.MaximumProofClockSkew

            let! atBoundary = keyStore.PublishValidationKeys finalAcceptedInstant
            Assert.That(atBoundary.Keys |> Seq.map (fun key -> key.KeyId), Does.Contain firstKey.KeyId)

            let! afterBoundary = keyStore.PublishValidationKeys(finalAcceptedInstant.Plus(Duration.FromMilliseconds 1L))

            Assert.That(
                afterBoundary.Keys
                |> Seq.map (fun key -> key.KeyId),
                Does.Not.Contain firstKey.KeyId
            )

            let! issuedAfterExpiry = keyStore.IssueGrant(issueRequest (), firstKey.ExpiresAt)
            Assert.That((issuedGrant issuedAfterExpiry).Header.KeyId, Is.Not.EqualTo firstKey.KeyId)
        }

    [<Test>]
    member _.``clock rollback retains but does not select a future-dated persisted key``() =
        task {
            let future = now.Plus(Duration.FromHours 1)
            let seedState = FakePersistentState(ArtifactGrantSigningKeyRingState.Empty, false)
            let seedStore = store seedState
            let! futureGrantResult = seedStore.IssueGrant(issueRequest (), future)
            let futureGrant = issuedGrant futureGrantResult
            let state = FakePersistentState(seedState.State, true)
            let keyStore = store state

            let! currentGrantResult = keyStore.IssueGrant(issueRequest (), now)
            let currentGrant = issuedGrant currentGrantResult
            let! published = keyStore.PublishValidationKeys now

            Assert.That(state.WriteCount, Is.EqualTo 1)
            Assert.That(currentGrant.Header.KeyId, Is.Not.EqualTo futureGrant.Header.KeyId)
            Assert.That(state.State.Keys, Has.Length.EqualTo 2)
            Assert.That(published.Keys |> Seq.map (fun key -> key.KeyId), Does.Contain futureGrant.Header.KeyId)
            Assert.That(published.Keys |> Seq.map (fun key -> key.KeyId), Does.Contain currentGrant.Header.KeyId)
        }

    [<Test>]
    member _.``concurrent signing and publication use isolated crypto objects and produce valid signatures``() =
        task {
            let state = FakePersistentState(ArtifactGrantSigningKeyRingState.Empty, false)
            let keyStore = store state
            let! _ = keyStore.PublishValidationKeys now

            let issueTasks =
                [|
                    for _ in 1..32 -> keyStore.IssueGrant(issueRequest (), now)
                |]

            let publicationTasks =
                [|
                    for _ in 1..32 -> keyStore.PublishValidationKeys now
                |]

            let! grants = Task.WhenAll issueTasks
            let! publications = Task.WhenAll publicationTasks
            let keySet = publications[0]

            for result in grants do
                let grant = issuedGrant result

                match validateWithKeySet now keySet (validationRequest ()) grant with
                | Ok () -> ()
                | Error error -> Assert.Fail(ArtifactGrantValidationError.toMessage error)
        }

    [<Test>]
    member _.``missing authenticated user malformed holder direct mode and overlong ttl fail before key creation``() =
        task {
            let state = FakePersistentState(ArtifactGrantSigningKeyRingState.Empty, false)
            let keyStore = store state
            let valid = issueRequest ()

            let cases =
                [|
                    { valid with RequesterPrincipalId = String.Empty }, InvalidRequesterPrincipal
                    { valid with HolderPublicKey = ArtifactGrantHolderPublicKey.Create("bad", "key") }, InvalidHolderKeyThumbprint
                    { valid with ExecutionMode = MaterializationExecutionMode.Direct }, InvalidExecutionMode
                    { valid with RequestedTtl = Some(ArtifactGrantContract.MaximumAcceptedGrantTtl.Plus(Duration.FromTicks 1L)) }, RequestedTtlTooLong
                |]

            for request, expected in cases do
                let! result = keyStore.IssueGrant(request, now)

                match result with
                | Error actual -> Assert.That(actual, Is.EqualTo expected)
                | Ok _ -> Assert.Fail($"Expected issuance failure {expected}.")

            Assert.That(state.WriteCount, Is.EqualTo 0)
            Assert.That(state.State.Keys, Is.Empty)
        }
