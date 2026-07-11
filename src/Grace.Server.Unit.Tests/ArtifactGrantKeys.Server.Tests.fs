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
    let cacheServicePrincipalId = "cache-service-client"
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
            CacheServicePrincipalId = cacheServicePrincipalId
            TargetRootDirectoryVersionId = targetRoot
            ExecutionMode = MaterializationExecutionMode.CacheRequired
            ArtifactIdentities = [| artifactIdentity |]
            RequestedTtl = None
        }

    /// Creates a store over controllable persisted actor state.
    let store (state: FakePersistentState) = ArtifactGrantSigningKeyStore(state, NullLogger.Instance)

    /// Unwraps successful issuance while preserving a useful test failure.
    let issuedGrant result =
        match result with
        | Ok grant -> grant
        | Error error -> failwith (ArtifactGrantIssueError.toMessage error)

    /// Builds the grant-only validation request used to prove recovered key publication.
    let validationRequest () =
        ArtifactGrantValidationRequest.Create(cacheServicePrincipalId, targetRoot, MaterializationExecutionMode.CacheRequired, artifactIdentity)

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
