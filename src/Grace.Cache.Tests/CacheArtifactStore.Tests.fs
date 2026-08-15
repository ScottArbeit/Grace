namespace Grace.Cache.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open Grace.Cache
open NUnit.Framework

/// Shares exact tuple construction and isolated durable restart setup for artifact-store tests.
module CacheArtifactStoreTestSupport =

    /// Returns fixed ZIP-shaped bytes used to verify size and digest independently on every test write.
    let payload = Encoding.UTF8.GetBytes("grace-cache-directory-version-zip\n")

    /// Returns the lowercase SHA-256 digest required by the calibrated artifact tuple.
    let digest (bytes: byte array) =
        Convert
            .ToHexString(SHA256.HashData(bytes))
            .ToLowerInvariant()

    /// Creates the one supported local tuple for a test identity and exact byte sequence.
    let createTuple canonicalIdentity (bytes: byte array) =
        {
            Kind = "DirectoryVersionZip"
            CanonicalIdentity = canonicalIdentity
            DirectoryVersionId = "directory-version-001"
            ExpectedSha256 = digest bytes
            ExpectedSize = int64 bytes.LongLength
        }

    /// Opens an isolated private database and managed artifact root for a single test case.
    let createSession () =
        let databasePath = CacheStoreTestSupport.createDatabasePath ()
        let store = CacheStoreTestSupport.openStore databasePath
        let managedRoot = Path.Combine(Path.GetDirectoryName(databasePath), "managed-artifacts")
        let artifacts = CacheArtifactStore.create store managedRoot
        databasePath, managedRoot, store, artifacts

    /// Reopens the isolated database so tests classify only durable SQLite and filesystem residue.
    let reopen databasePath managedRoot =
        let store = CacheStoreTestSupport.openStore databasePath
        store, CacheArtifactStore.create store managedRoot

    /// Releases the store before deleting all isolated database and managed-root residue.
    let closeSession databasePath store =
        CacheStore.disposeStore store
        CacheStoreTestSupport.deleteDatabasePath databasePath

    /// Supplies a new readable source stream because every commit consumes its input exactly once.
    let source bytes = new MemoryStream(bytes, false)

    /// Requires the expected artifact result while preserving a useful diagnostic for a changed state transition.
    let requireOutcome (expected: CacheArtifactOutcome) (actual: CacheArtifactOutcome) = Assert.That(actual, Is.EqualTo(expected))

    /// Maps test-case names to the internal effect seam without making that seam part of the test fixture API.
    let internal failurePoint effectName momentName =
        let effect =
            match effectName with
            | "staging-allocation" -> StagingAllocation
            | "staging-file-creation" -> StagingFileCreation
            | "byte-write-and-close" -> ByteWriteAndClose
            | "size-and-sha256-verification" -> SizeAndSha256Verification
            | "staging-state-transaction" -> StagingStateTransaction
            | "final-file-publication" -> FinalFilePublication
            | "complete-state-transaction" -> CompleteStateTransaction
            | "terminal-success-publication" -> TerminalSuccessPublication
            | _ -> invalidArg (nameof effectName) "Unknown calibrated effect name."

        let moment =
            match momentName with
            | "before" -> Before
            | "after" -> After
            | _ -> invalidArg (nameof momentName) "Unknown calibrated failure moment."

        { Effect = effect; Moment = moment }

/// Distinguishes the three finite expected results for conflict-control inputs.
type private ConflictControlExpectation =
    | ShouldConflict
    | ShouldReject
    | ShouldFill

/// Verifies the one-ZIP artifact state machine and finite restart classification table.
[<TestFixture>]
type CacheArtifactStoreTests() =

    /// Enumerates each before-and-after interruption at the eight calibrated durable effect boundaries.
    static member FailureCases =
        let effects =
            [
                "staging-allocation"
                "staging-file-creation"
                "byte-write-and-close"
                "size-and-sha256-verification"
                "staging-state-transaction"
                "final-file-publication"
                "complete-state-transaction"
                "terminal-success-publication"
            ]

        let moments = [ "before"; "after" ]

        [
            for effect in effects do
                for moment in moments do
                    TestCaseData(effect, moment)
                        .SetName($"{effect}-{moment}")
        ]

    /// Confirms success publishes verified final bytes and then exposes only an exact Complete hit.
    [<Test>]
    member _.``success publishes verified bytes before the exact Complete state is visible``() =
        let databasePath, _, store, artifacts = CacheArtifactStoreTestSupport.createSession ()
        let tuple = CacheArtifactStoreTestSupport.createTuple "artifact://grace/root/zip/001" CacheArtifactStoreTestSupport.payload

        try
            use input = CacheArtifactStoreTestSupport.source CacheArtifactStoreTestSupport.payload

            CacheArtifactStore.commit artifacts tuple input
            |> CacheArtifactStoreTestSupport.requireOutcome Filled

            match CacheArtifactStore.inspect artifacts tuple with
            | Hit finalPath ->
                Assert.That(File.Exists(finalPath), Is.True)
                Assert.That(File.ReadAllBytes(finalPath), Is.EqualTo<byte>(CacheArtifactStoreTestSupport.payload))
            | outcome -> Assert.Fail($"Expected a complete local hit, got {outcome}.")
        finally
            CacheArtifactStoreTestSupport.closeSession databasePath store

    /// Proves all sixteen effect interruptions converge through durable restart and one fresh retry.
    [<TestCaseSource(nameof CacheArtifactStoreTests.FailureCases)>]
    member _.``each injected boundary converges after durable restart and one retry``(effectName: string, momentName: string) =
        let databasePath, managedRoot, initialStore, initialArtifacts = CacheArtifactStoreTestSupport.createSession ()
        let tuple = CacheArtifactStoreTestSupport.createTuple $"artifact://grace/root/zip/{effectName}-{momentName}" CacheArtifactStoreTestSupport.payload

        try
            let inject () =
                use input = CacheArtifactStoreTestSupport.source CacheArtifactStoreTestSupport.payload

                CacheArtifactStore.commitWithFailure initialArtifacts tuple input (CacheArtifactStoreTestSupport.failurePoint effectName momentName)
                |> ignore

            Assert.Throws<CacheArtifactInjectedFailure>(Action inject)
            |> ignore

            CacheStore.disposeStore initialStore

            let restartedStore, restartedArtifacts = CacheArtifactStoreTestSupport.reopen databasePath managedRoot

            try
                let classification = CacheArtifactStore.inspect restartedArtifacts tuple

                match classification with
                | Absent
                | Hit _ -> ()
                | outcome -> Assert.Fail($"Restart classification was not finite for {effectName}/{momentName}: {outcome}.")

                use retryInput = CacheArtifactStoreTestSupport.source CacheArtifactStoreTestSupport.payload

                match CacheArtifactStore.commit restartedArtifacts tuple retryInput with
                | Filled
                | Hit _ -> ()
                | outcome -> Assert.Fail($"Retry did not converge for {effectName}/{momentName}: {outcome}.")

                match CacheArtifactStore.inspect restartedArtifacts tuple with
                | Hit _ -> ()
                | outcome -> Assert.Fail($"Retry did not end Complete for {effectName}/{momentName}: {outcome}.")
            finally
                CacheStore.disposeStore restartedStore
        finally
            CacheStore.disposeStore initialStore
            CacheStoreTestSupport.deleteDatabasePath databasePath

    /// Confirms immutable tuple differences cannot replace the already completed baseline bytes.
    [<Test>]
    member _.``digest size root kind and canonical identity controls preserve completed bytes``() =
        let databasePath, _, store, artifacts = CacheArtifactStoreTestSupport.createSession ()
        let baseline = CacheArtifactStoreTestSupport.createTuple "artifact://grace/root/zip/001" CacheArtifactStoreTestSupport.payload

        try
            use seed = CacheArtifactStoreTestSupport.source CacheArtifactStoreTestSupport.payload

            CacheArtifactStore.commit artifacts baseline seed
            |> CacheArtifactStoreTestSupport.requireOutcome Filled

            let originalPath = CacheArtifactStore.finalPathForTest artifacts baseline
            let originalBytes = File.ReadAllBytes(originalPath)

            let controls =
                [
                    "digest", { baseline with ExpectedSha256 = String.replicate 64 "0" }, ShouldConflict
                    "size", { baseline with ExpectedSize = baseline.ExpectedSize + 1L }, ShouldConflict
                    "root", { baseline with DirectoryVersionId = "directory-version-002" }, ShouldConflict
                    "kind", { baseline with Kind = "OtherKind" }, ShouldReject
                    "canonical-identity", { baseline with CanonicalIdentity = "artifact://grace/root/zip/002" }, ShouldFill
                ]

            for name, candidate, expectedKind in controls do
                use input = CacheArtifactStoreTestSupport.source CacheArtifactStoreTestSupport.payload
                let outcome = CacheArtifactStore.commit artifacts candidate input

                match expectedKind, outcome with
                | ShouldConflict, Conflict _
                | ShouldReject, Rejected _
                | ShouldFill, Filled -> ()
                | _ -> Assert.Fail($"{name} control returned {outcome}.")

                Assert.That(File.ReadAllBytes(originalPath), Is.EqualTo<byte>(originalBytes), $"{name} replaced the original complete bytes.")
        finally
            CacheArtifactStoreTestSupport.closeSession databasePath store

    /// Confirms traversal-shaped identity remains data under the opaque managed artifact root.
    [<Test>]
    member _.``traversal-shaped canonical identity remains an opaque managed filename``() =
        let databasePath, managedRoot, store, artifacts = CacheArtifactStoreTestSupport.createSession ()
        let tuple = CacheArtifactStoreTestSupport.createTuple "../../outside/evil.zip" CacheArtifactStoreTestSupport.payload

        try
            use input = CacheArtifactStoreTestSupport.source CacheArtifactStoreTestSupport.payload

            CacheArtifactStore.commit artifacts tuple input
            |> CacheArtifactStoreTestSupport.requireOutcome Filled

            let derived = Path.GetFullPath(CacheArtifactStore.finalPathForTest artifacts tuple)

            let expectedRoot =
                Path.GetFullPath(Path.Combine(managedRoot, "artifacts"))
                + string Path.DirectorySeparatorChar

            Assert.That(derived.StartsWith(expectedRoot, StringComparison.Ordinal), Is.True)
        finally
            CacheArtifactStoreTestSupport.closeSession databasePath store

    /// Confirms unrecorded staging residue is deleted rather than becoming a completed artifact.
    [<Test>]
    member _.``unknown partial staging residue is removed without promotion``() =
        let databasePath, _, store, artifacts = CacheArtifactStoreTestSupport.createSession ()
        let tuple = CacheArtifactStoreTestSupport.createTuple "artifact://grace/root/zip/unknown" CacheArtifactStoreTestSupport.payload

        try
            let unknownPath = CacheArtifactStore.unknownStagingPathForTest artifacts tuple
            File.WriteAllBytes(unknownPath, CacheArtifactStoreTestSupport.payload)

            CacheArtifactStore.inspect artifacts tuple
            |> CacheArtifactStoreTestSupport.requireOutcome Absent

            Assert.That(File.Exists(unknownPath), Is.False)
        finally
            CacheArtifactStoreTestSupport.closeSession databasePath store

    /// Confirms a missing Complete file fails closed and cannot be silently replaced.
    [<Test>]
    member _.``complete file disagreement requires local reset and preserves the durable record``() =
        let databasePath, _, store, artifacts = CacheArtifactStoreTestSupport.createSession ()
        let tuple = CacheArtifactStoreTestSupport.createTuple "artifact://grace/root/zip/disagreement" CacheArtifactStoreTestSupport.payload

        try
            use input = CacheArtifactStoreTestSupport.source CacheArtifactStoreTestSupport.payload

            CacheArtifactStore.commit artifacts tuple input
            |> CacheArtifactStoreTestSupport.requireOutcome Filled

            let finalPath = CacheArtifactStore.finalPathForTest artifacts tuple
            File.Delete(finalPath)

            match CacheArtifactStore.inspect artifacts tuple with
            | RecoveryRequired _ -> ()
            | outcome -> Assert.Fail($"Complete disagreement did not require local reset: {outcome}.")

            use retry = CacheArtifactStoreTestSupport.source CacheArtifactStoreTestSupport.payload

            match CacheArtifactStore.commit artifacts tuple retry with
            | RecoveryRequired _ -> ()
            | outcome -> Assert.Fail($"Complete disagreement was replaced instead of preserved: {outcome}.")
        finally
            CacheArtifactStoreTestSupport.closeSession databasePath store
