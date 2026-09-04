namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI.Command
open Grace.CLI
open NUnit.Framework
open System
open System.IO
open System.Threading
open System.Threading.Tasks

/// Verifies Library publication shares the repository-root exclusion with Branch and Watch WDU work.
[<NonParallelizable>]
module LibrarySynchronizationCoordinationTests =

    /// Builds enabled local cursor authority for one deterministic publication revalidation.
    let private localState repositoryId workingCopyId catalogVersion : LibraryLocalState.RepositoryState =
        {
            RepositoryId = repositoryId
            WorkingCopyId = workingCopyId
            LibraryCatalogVersion = catalogVersion
            CursorEpoch = Some "epoch-1"
            AppliedCursor = Some "cursor-0"
            PredecessorCursor = None
            ParticipationEnabled = true
            LifecycleState = "enabled"
        }

    /// Builds exact stable-byte evidence without performing a filesystem read.
    let private stableContent blake3 sha256 size : LibraryFilesystem.StableContent =
        { Bytes = Array.zeroCreate (int size); Blake3Hash = blake3; Sha256Hash = sha256; Size = size }

    /// Builds current publication evidence whose catalog, cursor, and target all remain admissible.
    let private currentEvidence catalogVersion : LibraryCommand.RemotePublicationRevalidation =
        {
            ExpectedCatalogVersion = catalogVersion
            AcceptedCatalogVersion = catalogVersion
            ObservedCatalogVersion = catalogVersion
            DurableCatalogVersion = catalogVersion
            ExpectedCursor = Some "cursor-0"
            DurableCursor = Some "cursor-0"
            TargetMatchesAccepted = false
            TargetExists = true
            TargetBlake3Hash = Some "ancestry-hash"
            AncestryBlake3Hash = Some "ancestry-hash"
        }

    /// Verifies a Library claimant cannot enter while the shared WDU lease is held.
    [<Test>]
    let ``Library and WDU serialize on the same repository root lease`` () =
        let root = Path.Combine(Path.GetTempPath(), $"grace-library-lease-{Guid.NewGuid():N}")
        Directory.CreateDirectory(root) |> ignore

        try
            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create (Guid.NewGuid()) root
                |> Result.defaultWith invalidOp

            let firstTask = WorkingDirectoryUpdateCoordination.Lease.acquire scope CancellationToken.None
            let first = firstTask.GetAwaiter().GetResult()
            let second = WorkingDirectoryUpdateCoordination.Lease.acquire scope CancellationToken.None
            Task.Delay(100).GetAwaiter().GetResult()
            second.IsCompleted |> should equal false
            (first :> IDisposable).Dispose()
            use acquired = second.GetAwaiter().GetResult()
            acquired |> should not' (be Null)
        finally
            if Directory.Exists(root) then Directory.Delete(root, true)

    /// Verifies actual target bytes that no longer match durable ancestry abort before remote publication.
    [<Test>]
    let ``target mutation is rejected by under-lease publication revalidation`` () =
        let evidence = { currentEvidence (Guid.NewGuid()) with TargetBlake3Hash = Some "locally-mutated-hash" }

        (fun () -> LibraryCommand.validateRemotePublication evidence)
        |> should throw typeof<InvalidOperationException>

    /// Verifies a Branch WDU winner can stale the catalog while Library waits and the later Library effect is rejected.
    [<Test>]
    let ``Library revalidates catalog after waiting for Branch WDU exclusion`` () =
        let root = Path.Combine(Path.GetTempPath(), $"grace-library-revalidation-{Guid.NewGuid():N}")
        Directory.CreateDirectory(root) |> ignore

        try
            let catalogVersion = Guid.NewGuid()
            let mutable observedCatalogVersion = catalogVersion
            let mutable effectCount = 0

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create (Guid.NewGuid()) root
                |> Result.defaultWith invalidOp

            let branchLeaseTask = WorkingDirectoryUpdateCoordination.Lease.acquire scope CancellationToken.None
            let branchLease = branchLeaseTask.GetAwaiter().GetResult()

            let libraryPublication =
                task {
                    use! _libraryLease = WorkingDirectoryUpdateCoordination.Lease.acquire scope CancellationToken.None

                    LibraryCommand.validateRemotePublication { currentEvidence catalogVersion with ObservedCatalogVersion = observedCatalogVersion }

                    effectCount <- effectCount + 1
                }

            Task.Delay(100).GetAwaiter().GetResult()

            libraryPublication.IsCompleted
            |> should equal false

            observedCatalogVersion <- Guid.NewGuid()
            (branchLease :> IDisposable).Dispose()

            (fun () -> libraryPublication.GetAwaiter().GetResult())
            |> should throw typeof<InvalidOperationException>

            effectCount |> should equal 0
        finally
            if Directory.Exists(root) then Directory.Delete(root, true)

    /// Verifies an editor mutation after upload preparation aborts local submit admission under the shared lease.
    [<Test>]
    let ``local publication rejects target mutation after prepared upload`` () =
        let repositoryId = Guid.NewGuid()
        let catalogVersion = Guid.NewGuid()
        let state = localState repositoryId (Guid.NewGuid()) catalogVersion
        let expected = stableContent (String.replicate 64 "a") (String.replicate 64 "b") 1L
        let observed = stableContent (String.replicate 64 "c") (String.replicate 64 "d") 1L

        (fun () -> LibraryCommand.validateLocalPublication state catalogVersion state None None expected observed)
        |> should throw typeof<InvalidOperationException>

    /// Verifies a Branch WDU lease prevents local submit admission until the exact repository-root exclusion is released.
    [<Test>]
    let ``Branch WDU lease blocks local publication admission`` () =
        let root = Path.Combine(Path.GetTempPath(), $"grace-library-local-lease-{Guid.NewGuid():N}")
        Directory.CreateDirectory(root) |> ignore

        try
            let repositoryId = Guid.NewGuid()
            let catalogVersion = Guid.NewGuid()
            let state = localState repositoryId (Guid.NewGuid()) catalogVersion
            let content = stableContent (String.replicate 64 "a") (String.replicate 64 "b") 1L
            let mutable submitAdmissionCount = 0

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create repositoryId root
                |> Result.defaultWith invalidOp

            let branchLease =
                WorkingDirectoryUpdateCoordination.Lease.acquire scope CancellationToken.None
                |> fun operation -> operation.GetAwaiter().GetResult()

            let localPublication =
                task {
                    use! _libraryLease = WorkingDirectoryUpdateCoordination.Lease.acquire scope CancellationToken.None
                    LibraryCommand.validateLocalPublication state catalogVersion state None None content content
                    submitAdmissionCount <- submitAdmissionCount + 1
                }

            Task.Delay(100).GetAwaiter().GetResult()
            localPublication.IsCompleted |> should equal false
            submitAdmissionCount |> should equal 0

            (branchLease :> IDisposable).Dispose()
            localPublication.GetAwaiter().GetResult()
            submitAdmissionCount |> should equal 1
        finally
            if Directory.Exists(root) then Directory.Delete(root, true)
