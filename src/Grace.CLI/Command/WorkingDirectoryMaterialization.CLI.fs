namespace Grace.CLI.Command

open Grace.CLI.Services
open System
open System.IO
open System.Threading
open System.Threading.Tasks

/// Coordinates Grace-owned working-directory materialization operations.
module internal WorkingDirectoryMaterialization =
    let private lane = new SemaphoreSlim(1, 1)

    /// Gets the repository/worktree-scoped file lease that joins Watch and branch-switch materialization.
    let internal leaseFileName () =
        let markerDirectory = DirectoryInfo(Path.GetDirectoryName(updateInProgressFileName ()))

        let repositoryScopeDirectory =
            if isNull markerDirectory.Parent
               || isNull markerDirectory.Parent.Parent then
                markerDirectory
            else
                markerDirectory.Parent.Parent

        Path.Combine(repositoryScopeDirectory.FullName, "working-directory-materialization.lease")

    /// Opens the repository/worktree-scoped lease file when no other materialization operation owns it.
    let private tryOpenLeaseFile (leaseFileName: string) =
        Directory.CreateDirectory(Path.GetDirectoryName(leaseFileName))
        |> ignore

        try
            Some(new FileStream(leaseFileName, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        with
        | :? IOException -> None

    /// Waits until this CLI process owns the repository/worktree-scoped materialization lease.
    let private acquireLeaseFile () =
        task {
            let leaseFileName = leaseFileName ()
            let mutable leaseFile: FileStream = null

            while isNull leaseFile do
                match tryOpenLeaseFile leaseFileName with
                | Some openedLeaseFile -> leaseFile <- openedLeaseFile
                | None -> do! Task.Delay(TimeSpan.FromMilliseconds(50.0))

            return leaseFile
        }

    /// Runs a Grace-owned working-directory materialization action one at a time for the current repository/worktree.
    let runSerialized (operation: unit -> Task<'T>) =
        task {
            do! lane.WaitAsync()
            let mutable leaseFile: FileStream = null

            try
                let! acquiredLeaseFile = acquireLeaseFile ()
                leaseFile <- acquiredLeaseFile
                return! operation ()
            finally
                if not (isNull leaseFile) then leaseFile.Dispose()

                lane.Release() |> ignore
        }
