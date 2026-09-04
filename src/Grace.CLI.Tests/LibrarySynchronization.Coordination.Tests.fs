namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI.Command
open NUnit.Framework
open System
open System.IO
open System.Threading
open System.Threading.Tasks

/// Verifies Library publication shares the repository-root exclusion with Branch and Watch WDU work.
[<NonParallelizable>]
module LibrarySynchronizationCoordinationTests =

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
