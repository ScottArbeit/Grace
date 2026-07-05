namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.Command
open Grace.CLI.Services
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Parameters.Branch
open Grace.Shared.Utilities
open Grace.Types.Annotation
open Grace.Types.Common
open Grace.Types.Reference
open NodaTime
open NUnit.Framework
open Spectre.Console
open System
open System.Collections.Generic
open System.IO
open System.Text.Json
open System.Threading.Tasks

/// Groups branch command coverage for the CLI test project.
[<NonParallelizable>]
module BranchCommandTests =
    let private ownerId = Guid.NewGuid()
    let private organizationId = Guid.NewGuid()
    let private repositoryId = Guid.NewGuid()
    let private branchId = Guid.NewGuid()
    let private targetReferenceId = Guid.NewGuid()
    let private correlationId = "branch-annotate-tests"

    /// Runs the supplied action with ids applied.
    let private withIds (args: string array) =
        Array.append
            args
            [|
                "--owner-id"
                ownerId.ToString()
                "--organization-id"
                organizationId.ToString()
                "--repository-id"
                repositoryId.ToString()
                "--branch-id"
                branchId.ToString()
                "--correlation-id"
                correlationId
            |]

    /// Parses representative arguments through the production CLI parser for CLI branch assertions.
    let private parse args = GraceCommand.rootCommand.Parse(withIds args)

    /// Sets ansi console output needed by the test scenario.
    let private setAnsiConsoleOutput (writer: TextWriter) =
        let settings = AnsiConsoleSettings()
        settings.Out <- AnsiConsoleOutput(writer)
        AnsiConsole.Console <- AnsiConsole.Create(settings)

    /// Captures output produced by the action.
    let private captureOutput (action: unit -> int) =
        use writer = new StringWriter()
        let originalOut = Console.Out

        try
            Console.SetOut(writer)
            setAnsiConsoleOutput writer
            let exitCode = action ()
            exitCode, writer.ToString()
        finally
            Console.SetOut(originalOut)
            setAnsiConsoleOutput originalOut

    let private targetReferenceResult =
        {
            TargetReferenceId = targetReferenceId
            RootDirectoryId = Guid.NewGuid()
            RootDirectorySha256Hash = Sha256Hash "root-sha"
            RootDirectoryBlake3Hash = Blake3Hash "root-blake3"
            Source = ExplicitReference
            CreatedSaveMessage = None
        }

    /// Runs the supplied action with a temporary current repository identity for branch switch preflight tests.
    let private withTempBranchSwitchRepo (action: unit -> unit) =
        let tempDir = Path.Combine(Path.GetTempPath(), $"grace-branch-switch-tests-{Guid.NewGuid():N}")
        let graceDir = Path.Combine(tempDir, Constants.GraceConfigDirectory)
        let originalDir = Environment.CurrentDirectory
        let originalParseResult = Services.parseResult

        try
            Directory.CreateDirectory(graceDir) |> ignore
            File.WriteAllText(Path.Combine(graceDir, Constants.GraceConfigFileName), "{}")
            Environment.CurrentDirectory <- tempDir
            Services.parseResult <- GraceCommand.rootCommand.Parse(Array.empty<string>)
            resetConfiguration ()

            let configuration = Current()
            configuration.RepositoryId <- repositoryId
            configuration.RepositoryName <- "branch-switch-repository"
            configuration.BranchId <- branchId
            configuration.BranchName <- "branch-switch-current"
            configuration.RootDirectory <- tempDir

            action ()
        finally
            resetConfiguration ()
            Services.parseResult <- originalParseResult
            Environment.CurrentDirectory <- originalDir

            if Directory.Exists(tempDir) then Directory.Delete(tempDir, true)

    /// Builds a trusted Watch IPC inspection snapshot for branch switch preflight tests.
    let private branchSwitchWatchStatus () : GraceWatchStatus =
        let current = Current()
        let rootDirectoryId = Guid.NewGuid()

        {
            UpdatedAt = getCurrentInstant ()
            IsStartupClaim = false
            RepositoryId = current.RepositoryId
            RepositoryName = RepositoryName current.RepositoryName
            BranchId = current.BranchId
            BranchName = BranchName current.BranchName
            RootDirectory = current.RootDirectory
            HasPendingWatchWork = false
            IsWorkingTreeClean = true
            RootDirectoryId = rootDirectoryId
            RootDirectorySha256Hash = Sha256Hash "branch-switch-watch-root"
            RootDirectoryBlake3Hash = Blake3Hash "branch-switch-watch-root-blake3"
            LastFileUploadInstant = Instant.MinValue
            LastDirectoryVersionInstant = Instant.MinValue
            DirectoryIds = HashSet<DirectoryVersionId>([| rootDirectoryId |])
        }

    /// Wraps a status snapshot in the inspection shape consumed by branch switch preflight.
    let private branchSwitchWatchInspection persistedMode status =
        { Exists = true; Status = Some status; PersistedMode = persistedMode; SafetyFlags = status.SafetyFlags; ReadError = None }

    /// Builds clean durable journal evidence for branch switch preflight tests.
    let private cleanPendingJournalSummary () : LocalStateDb.WatchJournalPendingWorkSummary =
        { DbPath = Current().GraceStatusFile; AppliedThroughSequence = 0L; PendingRowCount = 0L }

    /// Writes a Watch IPC status snapshot for branch switch preflight tests.
    let private writeBranchSwitchWatchStatus (fileName: string) status =
        Directory.CreateDirectory(Path.GetDirectoryName(fileName))
        |> ignore

        File.WriteAllText(fileName, serialize status)

    /// Runs branch switch preflight with injected side effects and reports which effects occurred.
    let private runBranchSwitchPreflight markerExists inspection =
        let mutable inspected = false

        let operations: Branch.BranchSwitchWatchCleanPreflightOperations =
            {
                UpdateMarkerExists = fun () -> markerExists
                InspectWatchStatus =
                    fun () ->
                        inspected <- true
                        Task.FromResult inspection
                ReadPendingJournalSummary = fun () -> Task.FromResult(cleanPendingJournalSummary ())
            }

        let result =
            (Branch.runBranchSwitchWatchCleanPreflight operations correlationId)
                .GetAwaiter()
                .GetResult()

        result, inspected

    /// Verifies that update marker names include repository and root identity before branch-name scoping.
    [<Test>]
    let ``branch switch update marker path is scoped by repository root and branch identity`` () =
        withTempBranchSwitchRepo (fun () ->
            let current = Current()
            let originalRepositoryName = current.RepositoryName
            let originalBranchName = current.BranchName
            let currentMarkerFile = Services.updateInProgressFileName ()
            let legacyBranchOnlyMarkerFile = Path.Combine(Path.GetTempPath(), "Grace", originalBranchName, Constants.UpdateInProgressFileName)
            let foreignRoot = Path.Combine(Path.GetTempPath(), $"grace-branch-switch-foreign-{Guid.NewGuid():N}")

            let foreignMarkerFile =
                Services.updateInProgressFileNameForIdentity (Guid.NewGuid()) originalRepositoryName foreignRoot (Guid.NewGuid()) originalBranchName

            try
                Directory.CreateDirectory(Path.GetDirectoryName(foreignMarkerFile))
                |> ignore

                File.WriteAllText(foreignMarkerFile, "foreign repository marker")

                foreignMarkerFile
                |> should not' (equal currentMarkerFile)

                currentMarkerFile
                |> should not' (equal legacyBranchOnlyMarkerFile)

                File.Exists(foreignMarkerFile)
                |> should equal true

                File.Exists(currentMarkerFile)
                |> should equal false

                let result, inspected =
                    branchSwitchWatchInspection (Some GraceWatchRuntimeMode.HealthyIncremental) (branchSwitchWatchStatus ())
                    |> runBranchSwitchPreflight (File.Exists(currentMarkerFile))

                match result with
                | Ok _ -> ()
                | Error error -> Assert.Fail($"Expected unrelated repository marker to allow branch switch preflight, got: {error.Error}")

                inspected |> should equal true

                File.Exists(currentMarkerFile)
                |> should equal false

            finally
                if File.Exists(foreignMarkerFile) then File.Delete(foreignMarkerFile)

                if Directory.Exists(foreignRoot) then Directory.Delete(foreignRoot, true))

    /// Verifies that branch switch accepts matching IDs even when display names in Watch status are stale.
    [<Test>]
    let ``branch switch Watch preflight allows healthy clean status with stale display names`` () =
        withTempBranchSwitchRepo (fun () ->
            let status =
                { branchSwitchWatchStatus () with
                    RepositoryName = RepositoryName "stale-repository-display-name"
                    BranchName = BranchName "stale-branch-display-name"
                }

            let updateMarkerFile = Services.updateInProgressFileName ()

            let result, inspected =
                branchSwitchWatchInspection (Some GraceWatchRuntimeMode.HealthyIncremental) status
                |> runBranchSwitchPreflight false

            match result with
            | Ok _ -> ()
            | Error error -> Assert.Fail($"Expected healthy Watch status to allow branch switch, got: {error.Error}")

            inspected |> should equal true

            File.Exists(updateMarkerFile)
            |> should equal false)

    /// Verifies that successful branch switch mutation creates the marker only after the second Watch-clean preflight.
    [<Test>]
    let ``branch switch working tree update creates marker after mutation boundary preflight`` () =
        withTempBranchSwitchRepo (fun () ->
            let updateMarkerFile = Services.updateInProgressFileName ()
            let markerText = $"`grace switch` is in progress. Lease: {Guid.NewGuid():N}"
            let mutable inspected = false
            let mutable operationRan = false
            let mutable markerSeenByOperation = false
            let mutationFile = Path.Combine(Current().RootDirectory, "grace-owned-mutation.txt")

            let operations: Branch.BranchSwitchWatchCleanPreflightOperations =
                {
                    UpdateMarkerExists = fun () -> File.Exists(updateMarkerFile)
                    InspectWatchStatus =
                        fun () ->
                            inspected <- true

                            File.Exists(updateMarkerFile)
                            |> should equal false

                            Task.FromResult(branchSwitchWatchInspection (Some GraceWatchRuntimeMode.HealthyIncremental) (branchSwitchWatchStatus ()))
                    ReadPendingJournalSummary = fun () -> Task.FromResult(cleanPendingJournalSummary ())
                }

            let result =
                (Branch.runBranchSwitchWorkingTreeUpdateWithMarker operations correlationId updateMarkerFile markerText (fun () ->
                    task {
                        operationRan <- true
                        markerSeenByOperation <- File.Exists(updateMarkerFile)
                        File.WriteAllText(mutationFile, "Grace-owned branch switch write")
                    }))
                    .GetAwaiter()
                    .GetResult()

            match result with
            | Ok _ -> ()
            | Error error -> Assert.Fail($"Expected mutation-boundary preflight to allow branch switch, got: {error.Error}")

            inspected |> should equal true
            operationRan |> should equal true
            markerSeenByOperation |> should equal true

            File.Exists(mutationFile) |> should equal true

            File.Exists(updateMarkerFile)
            |> should equal false)

    /// Verifies that the switch workflow lease serializes precomputation without creating the Watch suppression marker.
    [<Test>]
    let ``branch switch workflow lease serializes before precompute without update marker`` () =
        withTempBranchSwitchRepo (fun () ->
            let updateMarkerFile = Services.updateInProgressFileName ()
            let switchLeaseFile = Branch.branchSwitchWorkflowLeaseFileName updateMarkerFile
            let switchLeaseText = $"`grace switch` workflow lease. Lease: {Guid.NewGuid():N}"
            let mutable inspected = false
            let mutable workflowRan = false
            let mutable leaseSeenByWorkflow = false
            let mutable markerSeenByWorkflow = false

            let operations: Branch.BranchSwitchWatchCleanPreflightOperations =
                {
                    UpdateMarkerExists = fun () -> File.Exists(updateMarkerFile)
                    InspectWatchStatus =
                        fun () ->
                            inspected <- true

                            File.Exists(updateMarkerFile)
                            |> should equal false

                            File.Exists(switchLeaseFile) |> should equal true

                            Task.FromResult(branchSwitchWatchInspection (Some GraceWatchRuntimeMode.HealthyIncremental) (branchSwitchWatchStatus ()))
                    ReadPendingJournalSummary = fun () -> Task.FromResult(cleanPendingJournalSummary ())
                }

            let result =
                (Branch.runBranchSwitchWorkflowWithLease operations correlationId switchLeaseFile switchLeaseText (fun () ->
                    task {
                        workflowRan <- true
                        leaseSeenByWorkflow <- File.Exists(switchLeaseFile)
                        markerSeenByWorkflow <- File.Exists(updateMarkerFile)
                        return "computed"
                    }))
                    .GetAwaiter()
                    .GetResult()

            match result with
            | Ok value -> value |> should equal "computed"
            | Error error -> Assert.Fail($"Expected branch switch lease to allow workflow, got: {error.Error}")

            inspected |> should equal true
            workflowRan |> should equal true
            leaseSeenByWorkflow |> should equal true
            markerSeenByWorkflow |> should equal false

            File.Exists(switchLeaseFile) |> should equal false

            File.Exists(updateMarkerFile)
            |> should equal false)

    /// Verifies that an existing switch workflow lease refuses before Watch inspection and state precomputation.
    [<Test>]
    let ``branch switch workflow lease refuses competing switch before precompute`` () =
        withTempBranchSwitchRepo (fun () ->
            let updateMarkerFile = Services.updateInProgressFileName ()
            let switchLeaseFile = Branch.branchSwitchWorkflowLeaseFileName updateMarkerFile
            let switchLeaseText = $"`grace switch` workflow lease. Lease: {Guid.NewGuid():N}"
            let mutable inspected = false
            let mutable workflowRan = false

            Directory.CreateDirectory(Path.GetDirectoryName(switchLeaseFile))
            |> ignore

            File.WriteAllText(switchLeaseFile, "competing switch workflow")

            let operations: Branch.BranchSwitchWatchCleanPreflightOperations =
                {
                    UpdateMarkerExists = fun () -> File.Exists(updateMarkerFile)
                    InspectWatchStatus =
                        fun () ->
                            inspected <- true
                            Task.FromResult(branchSwitchWatchInspection (Some GraceWatchRuntimeMode.HealthyIncremental) (branchSwitchWatchStatus ()))
                    ReadPendingJournalSummary = fun () -> Task.FromResult(cleanPendingJournalSummary ())
                }

            let result =
                (Branch.runBranchSwitchWorkflowWithLease operations correlationId switchLeaseFile switchLeaseText (fun () ->
                    task {
                        workflowRan <- true
                        return ()
                    }))
                    .GetAwaiter()
                    .GetResult()

            match result with
            | Error error ->
                error.Error
                |> should contain "Branch switch refused before state precomputation"
            | Ok _ -> Assert.Fail("Expected competing switch workflow lease to refuse before precompute.")

            inspected |> should equal false
            workflowRan |> should equal false

            File.ReadAllText(switchLeaseFile)
            |> should equal "competing switch workflow"

            File.Delete(switchLeaseFile))

    /// Verifies that switch workflow leases serialize the same worktree even after branch identity changes.
    [<Test>]
    let ``branch switch workflow lease is independent of mutable branch identity`` () =
        withTempBranchSwitchRepo (fun () ->
            let current = Current()
            let sourceUpdateMarkerFile = Services.updateInProgressFileName ()
            let targetBranchId = Guid.NewGuid()
            let targetBranchName = "branch-switch-target"

            let targetUpdateMarkerFile =
                Services.updateInProgressFileNameForIdentity current.RepositoryId current.RepositoryName current.RootDirectory targetBranchId targetBranchName

            let sourceSwitchLeaseFile = Branch.branchSwitchWorkflowLeaseFileName sourceUpdateMarkerFile
            let targetSwitchLeaseFile = Branch.branchSwitchWorkflowLeaseFileName targetUpdateMarkerFile

            targetSwitchLeaseFile
            |> should equal sourceSwitchLeaseFile

            let sourceSwitchLeaseText = $"`grace switch` workflow lease. Lease: {Guid.NewGuid():N}"
            let targetSwitchLeaseText = $"`grace switch` workflow lease. Lease: {Guid.NewGuid():N}"
            let mutable inspected = false
            let mutable workflowRan = false

            Directory.CreateDirectory(Path.GetDirectoryName(sourceSwitchLeaseFile))
            |> ignore

            File.WriteAllText(sourceSwitchLeaseFile, sourceSwitchLeaseText)

            current.BranchId <- targetBranchId
            current.BranchName <- targetBranchName

            let operations: Branch.BranchSwitchWatchCleanPreflightOperations =
                {
                    UpdateMarkerExists = fun () -> File.Exists(targetUpdateMarkerFile)
                    InspectWatchStatus =
                        fun () ->
                            inspected <- true
                            Task.FromResult(branchSwitchWatchInspection (Some GraceWatchRuntimeMode.HealthyIncremental) (branchSwitchWatchStatus ()))
                    ReadPendingJournalSummary = fun () -> Task.FromResult(cleanPendingJournalSummary ())
                }

            let result =
                (Branch.runBranchSwitchWorkflowWithLease operations correlationId targetSwitchLeaseFile targetSwitchLeaseText (fun () ->
                    task {
                        workflowRan <- true
                        return ()
                    }))
                    .GetAwaiter()
                    .GetResult()

            match result with
            | Error error ->
                error.Error
                |> should contain "Branch switch refused before state precomputation"
            | Ok _ -> Assert.Fail("Expected same-worktree switch workflow lease to refuse after branch identity changes.")

            inspected |> should equal false
            workflowRan |> should equal false

            File.ReadAllText(sourceSwitchLeaseFile)
            |> should equal sourceSwitchLeaseText

            File.Delete(sourceSwitchLeaseFile))

    /// Verifies that branch identity is moved before object-cache refresh can fail.
    [<Test>]
    let ``branch switch local state updates config before cache refresh failure`` () =
        withTempBranchSwitchRepo (fun () ->
            let targetBranchId = Guid.NewGuid()
            let targetBranchName = "branch-switch-target"

            let operation =
                Func<Task> (fun () ->
                    Branch.applyBranchSwitchLocalState
                        (fun () -> Task.CompletedTask)
                        (fun () ->
                            let configuration = Current()
                            configuration.BranchId <- targetBranchId
                            configuration.BranchName <- targetBranchName
                            updateConfiguration configuration)
                        (fun () -> task { raise (IOException("simulated object cache refresh failure")) })
                    :> Task)

            Assert.ThrowsAsync<IOException>(operation)
            |> ignore

            resetConfiguration ()
            let configuration = Current()

            configuration.BranchId
            |> should equal targetBranchId

            configuration.BranchName
            |> should equal targetBranchName)

    /// Verifies that a second Watch-clean preflight failure prevents marker creation and working tree mutation.
    [<Test>]
    let ``branch switch working tree update refuses dirty mutation boundary before marker or rewrite`` () =
        withTempBranchSwitchRepo (fun () ->
            let updateMarkerFile = Services.updateInProgressFileName ()
            let markerText = $"`grace switch` is in progress. Lease: {Guid.NewGuid():N}"
            let dirtyStatus = { branchSwitchWatchStatus () with HasPendingWatchWork = true; IsWorkingTreeClean = false }
            let mutationFile = Path.Combine(Current().RootDirectory, "must-not-be-written.txt")
            let mutable inspected = false
            let mutable operationRan = false

            let operations: Branch.BranchSwitchWatchCleanPreflightOperations =
                {
                    UpdateMarkerExists = fun () -> File.Exists(updateMarkerFile)
                    InspectWatchStatus =
                        fun () ->
                            inspected <- true

                            File.Exists(updateMarkerFile)
                            |> should equal false

                            Task.FromResult(branchSwitchWatchInspection (Some GraceWatchRuntimeMode.HealthyIncremental) dirtyStatus)
                    ReadPendingJournalSummary = fun () -> Task.FromResult(cleanPendingJournalSummary ())
                }

            let result =
                (Branch.runBranchSwitchWorkingTreeUpdateWithMarker operations correlationId updateMarkerFile markerText (fun () ->
                    task {
                        operationRan <- true
                        File.WriteAllText(mutationFile, "must not be written")
                    }))
                    .GetAwaiter()
                    .GetResult()

            match result with
            | Error error ->
                error.Error
                |> should contain "Branch switch refused before mutation"

                error.Error |> should contain "dirty working tree"
            | Ok _ -> Assert.Fail("Expected dirty mutation-boundary Watch status to refuse branch switch.")

            inspected |> should equal true
            operationRan |> should equal false

            File.Exists(mutationFile) |> should equal false

            File.Exists(updateMarkerFile)
            |> should equal false)

    /// Verifies that partial mutation failure removes the working-tree marker created by this invocation.
    [<Test>]
    let ``branch switch working tree update removes owned marker when operation fails`` () =
        withTempBranchSwitchRepo (fun () ->
            let updateMarkerFile = Services.updateInProgressFileName ()
            let markerText = $"`grace switch` is in progress. Lease: {Guid.NewGuid():N}"

            let operations: Branch.BranchSwitchWatchCleanPreflightOperations =
                {
                    UpdateMarkerExists = fun () -> File.Exists(updateMarkerFile)
                    InspectWatchStatus =
                        fun () -> Task.FromResult(branchSwitchWatchInspection (Some GraceWatchRuntimeMode.HealthyIncremental) (branchSwitchWatchStatus ()))
                    ReadPendingJournalSummary = fun () -> Task.FromResult(cleanPendingJournalSummary ())
                }

            let operation =
                Func<Task> (fun () ->
                    task {
                        let! _ =
                            Branch.runBranchSwitchWorkingTreeUpdateWithMarker operations correlationId updateMarkerFile markerText (fun () ->
                                task { raise (InvalidOperationException("simulated partial mutation failure")) })

                        return ()
                    }
                    :> Task)

            Assert.ThrowsAsync<InvalidOperationException>(operation)
            |> ignore

            File.Exists(updateMarkerFile)
            |> should equal false

            File.Exists(updateMarkerFile + ".completed")
            |> should equal false)

    /// Verifies that a raced marker creation failure does not run mutation work.
    [<Test>]
    let ``branch switch working tree update refuses raced marker before rewrite`` () =
        withTempBranchSwitchRepo (fun () ->
            let updateMarkerFile = Services.updateInProgressFileName ()
            let markerText = $"`grace switch` is in progress. Lease: {Guid.NewGuid():N}"
            let mutable operationRan = false

            let operations: Branch.BranchSwitchWatchCleanPreflightOperations =
                {
                    UpdateMarkerExists =
                        fun () ->
                            if not (File.Exists(updateMarkerFile)) then
                                Directory.CreateDirectory(Path.GetDirectoryName(updateMarkerFile))
                                |> ignore

                                File.WriteAllText(updateMarkerFile, "competing marker")

                            false
                    InspectWatchStatus =
                        fun () -> Task.FromResult(branchSwitchWatchInspection (Some GraceWatchRuntimeMode.HealthyIncremental) (branchSwitchWatchStatus ()))
                    ReadPendingJournalSummary = fun () -> Task.FromResult(cleanPendingJournalSummary ())
                }

            let updateTask =
                Branch.runBranchSwitchWorkingTreeUpdateWithMarker operations correlationId updateMarkerFile markerText (fun () -> task { operationRan <- true })

            let result = updateTask.GetAwaiter().GetResult()

            match result with
            | Error error ->
                error.Error
                |> should contain "update marker appeared while preflight was running"
            | Ok _ -> Assert.Fail("Expected raced marker creation to refuse branch switch.")

            operationRan |> should equal false

            File.Exists(updateMarkerFile) |> should equal true

            File.ReadAllText(updateMarkerFile)
            |> should equal "competing marker"

            File.Delete(updateMarkerFile))

    /// Verifies that cancellation removes the working-tree marker when this invocation created it.
    [<Test>]
    let ``branch switch working tree update removes owned marker when operation cancels`` () =
        withTempBranchSwitchRepo (fun () ->
            let updateMarkerFile = Services.updateInProgressFileName ()
            let markerText = "`grace switch` is in progress."

            let operations: Branch.BranchSwitchWatchCleanPreflightOperations =
                {
                    UpdateMarkerExists = fun () -> File.Exists(updateMarkerFile)
                    InspectWatchStatus =
                        fun () -> Task.FromResult(branchSwitchWatchInspection (Some GraceWatchRuntimeMode.HealthyIncremental) (branchSwitchWatchStatus ()))
                    ReadPendingJournalSummary = fun () -> Task.FromResult(cleanPendingJournalSummary ())
                }

            let operation =
                Func<Task> (fun () ->
                    task {
                        let! _ =
                            Branch.runBranchSwitchWorkingTreeUpdateWithMarker operations correlationId updateMarkerFile markerText (fun () ->
                                task { raise (OperationCanceledException("simulated cancellation")) })

                        return ()
                    }
                    :> Task)

            Assert.ThrowsAsync<OperationCanceledException>(operation)
            |> ignore

            File.Exists(updateMarkerFile)
            |> should equal false)

    /// Verifies that same-branch Watch IPC from another checkout does not override the current repository snapshot.
    [<Test>]
    let ``branch switch Watch preflight reads repository scoped IPC for current checkout`` () =
        withTempBranchSwitchRepo (fun () ->
            let current = Current()
            let currentIpcFile = Services.IpcFileName()
            let foreignRoot = Path.Combine(Path.GetTempPath(), $"grace-branch-switch-foreign-ipc-{Guid.NewGuid():N}")

            let foreignIpcFile = Services.IpcFileNameForIdentity (Guid.NewGuid()) current.RepositoryName foreignRoot (Guid.NewGuid()) current.BranchName

            try
                let foreignStatus =
                    { branchSwitchWatchStatus () with
                        RepositoryId = Guid.NewGuid()
                        BranchId = Guid.NewGuid()
                        RootDirectory = foreignRoot
                        HasPendingWatchWork = true
                        IsWorkingTreeClean = false
                    }

                writeBranchSwitchWatchStatus foreignIpcFile foreignStatus
                writeBranchSwitchWatchStatus currentIpcFile (branchSwitchWatchStatus ())

                let inspection =
                    Services
                        .inspectGraceWatchStatus()
                        .GetAwaiter()
                        .GetResult()

                inspection.HasCurrentRepositoryIdentity
                |> should equal true

                inspection.IsUsable |> should equal true

                let result, inspected = runBranchSwitchPreflight false inspection

                match result with
                | Ok _ -> ()
                | Error error -> Assert.Fail($"Expected current repository Watch IPC to allow branch switch, got: {error.Error}")

                inspected |> should equal true

                File.Exists(Services.updateInProgressFileName ())
                |> should equal false
            finally
                if File.Exists(currentIpcFile) then File.Delete(currentIpcFile)

                if File.Exists(foreignIpcFile) then File.Delete(foreignIpcFile))

    /// Verifies that a pre-existing same-repository update marker refuses before Watch inspection or marker mutation.
    [<Test>]
    let ``branch switch Watch preflight refuses existing same repository update marker before inspection`` () =
        withTempBranchSwitchRepo (fun () ->
            let updateMarkerFile = Services.updateInProgressFileName ()

            Directory.CreateDirectory(Path.GetDirectoryName(updateMarkerFile))
            |> ignore

            File.WriteAllText(updateMarkerFile, "same repository marker")

            let result, inspected =
                branchSwitchWatchInspection (Some GraceWatchRuntimeMode.HealthyIncremental) (branchSwitchWatchStatus ())
                |> runBranchSwitchPreflight (File.Exists(updateMarkerFile))

            match result with
            | Error error ->
                error.Error
                |> should contain "Branch switch refused before mutation"

                error.Error
                |> should contain "update marker already exists"
            | Ok _ -> Assert.Fail("Expected existing update marker to refuse branch switch.")

            inspected |> should equal false)

    /// Verifies that marker write failures remove the partial file created by this preflight.
    [<Test>]
    let ``branch switch update marker creation removes partial marker after write failure`` () =
        withTempBranchSwitchRepo (fun () ->
            let updateMarkerFile = Services.updateInProgressFileName ()
            let markerText = $"`grace switch` is in progress. Lease: {Guid.NewGuid():N}"
            let partialText = markerText.Substring(0, 16)

            let operation =
                Func<Task> (fun () ->
                    Branch.createBranchSwitchUpdateMarkerWithWriter
                        (fun writer _ ->
                            task {
                                do! writer.WriteAsync(partialText)
                                do! writer.FlushAsync()
                                raise (IOException("simulated marker flush failure"))
                            })
                        updateMarkerFile
                        markerText
                    :> Task)

            Assert.ThrowsAsync<IOException>(operation)
            |> ignore

            File.Exists(updateMarkerFile)
            |> should equal false)

    /// Verifies that current-repository untrusted Watch IPC still refuses before marker creation.
    [<Test>]
    let ``branch switch Watch preflight refuses dirty current repository IPC before marker creation`` () =
        withTempBranchSwitchRepo (fun () ->
            let currentIpcFile = Services.IpcFileName()

            try
                let dirtyStatus = { branchSwitchWatchStatus () with HasPendingWatchWork = true; IsWorkingTreeClean = false }

                writeBranchSwitchWatchStatus currentIpcFile dirtyStatus

                let inspection =
                    Services
                        .inspectGraceWatchStatus()
                        .GetAwaiter()
                        .GetResult()

                let result, inspected = runBranchSwitchPreflight false inspection

                match result with
                | Error error ->
                    error.Error
                    |> should contain "Branch switch refused before mutation"

                    error.Error |> should contain "dirty working tree"
                | Ok _ -> Assert.Fail("Expected dirty current-repository Watch IPC to refuse branch switch.")

                inspected |> should equal true
            finally
                if File.Exists(currentIpcFile) then File.Delete(currentIpcFile))

    /// Verifies durable journal evidence blocks branch switch even before Watch republishes dirty IPC.
    [<Test>]
    let ``branch switch Watch preflight refuses durable pending rows after clean IPC`` () =
        withTempBranchSwitchRepo (fun () ->
            let mutable inspected = false
            let mutable inspectedJournal = false

            let operations: Branch.BranchSwitchWatchCleanPreflightOperations =
                {
                    UpdateMarkerExists = fun () -> false
                    InspectWatchStatus =
                        fun () ->
                            inspected <- true
                            Task.FromResult(branchSwitchWatchInspection (Some GraceWatchRuntimeMode.HealthyIncremental) (branchSwitchWatchStatus ()))
                    ReadPendingJournalSummary =
                        fun () ->
                            inspectedJournal <- true

                            Task.FromResult(
                                { DbPath = Current().GraceStatusFile; AppliedThroughSequence = 7L; PendingRowCount = 2L }: LocalStateDb.WatchJournalPendingWorkSummary
                            )
                }

            let result =
                (Branch.runBranchSwitchWatchCleanPreflight operations correlationId)
                    .GetAwaiter()
                    .GetResult()

            match result with
            | Error error ->
                error.Error
                |> should contain "Branch switch refused before mutation"

                error.Error
                |> should contain "unresolved durable journal rows"
            | Ok _ -> Assert.Fail("Expected durable pending journal evidence to refuse branch switch.")

            inspected |> should equal true
            inspectedJournal |> should equal true)

    /// Verifies that untrusted Watch states refuse before branch switch marker creation.
    [<Test>]
    let ``branch switch Watch preflight refuses untrusted Watch states before marker creation`` () =
        withTempBranchSwitchRepo (fun () ->
            let current = Current()
            let healthyStatus = branchSwitchWatchStatus ()

            let missingStatus: GraceWatchStatusInspection =
                {
                    Exists = false
                    Status = None
                    PersistedMode = None
                    SafetyFlags =
                        [|
                            "missingStatus"
                            "requiresExplicitResync"
                        |]
                    ReadError = None
                }

            let unreadableStatus: GraceWatchStatusInspection =
                {
                    Exists = true
                    Status = None
                    PersistedMode = None
                    SafetyFlags =
                        [|
                            "unreadableStatus"
                            "requiresExplicitResync"
                        |]
                    ReadError = Some "The Grace Watch status file exists but could not be read."
                }

            let staleStatus =
                { healthyStatus with
                    UpdatedAt =
                        getCurrentInstant()
                            .Minus(Duration.FromMinutes(10.0))
                }

            let crossRootStatus = { healthyStatus with RootDirectory = Path.Combine(Path.GetTempPath(), $"other-root-{Guid.NewGuid():N}") }

            let crossRepositoryStatus = { healthyStatus with RepositoryId = Guid.NewGuid(); RepositoryName = RepositoryName current.RepositoryName }

            let crossBranchStatus = { healthyStatus with BranchId = Guid.NewGuid(); BranchName = BranchName current.BranchName }

            let legacyNonAuthoritativeStatus =
                { healthyStatus with
                    RepositoryId = RepositoryId.Empty
                    RepositoryName = RepositoryName String.Empty
                    BranchId = BranchId.Empty
                    BranchName = BranchName String.Empty
                    RootDirectory = String.Empty
                }

            let resynchronizingStatus = { healthyStatus with DirectoryIds = HashSet<DirectoryVersionId>() }

            let pendingStatus = { healthyStatus with HasPendingWatchWork = true }

            let dirtyStatus = { healthyStatus with IsWorkingTreeClean = false }

            let startupStatus = { healthyStatus with IsStartupClaim = true }

            let cases =
                [|
                    "missing Watch status", missingStatus, "status file is missing"
                    "unreadable Watch status", unreadableStatus, "could not be read"
                    "stale Watch heartbeat", branchSwitchWatchInspection (Some GraceWatchRuntimeMode.HealthyIncremental) staleStatus, "heartbeat is stale"
                    "cross-root Watch status",
                    branchSwitchWatchInspection (Some GraceWatchRuntimeMode.HealthyIncremental) crossRootStatus,
                    "does not match the current repository root"
                    "cross-repository Watch status",
                    branchSwitchWatchInspection (Some GraceWatchRuntimeMode.HealthyIncremental) crossRepositoryStatus,
                    "persisted IDs are authoritative"
                    "cross-branch Watch status",
                    branchSwitchWatchInspection (Some GraceWatchRuntimeMode.HealthyIncremental) crossBranchStatus,
                    "persisted IDs are authoritative"
                    "legacy non-authoritative Watch status",
                    branchSwitchWatchInspection (Some GraceWatchRuntimeMode.HealthyIncremental) legacyNonAuthoritativeStatus,
                    "legacy non-authoritative identity"
                    "suspended Watch mode",
                    branchSwitchWatchInspection (Some GraceWatchRuntimeMode.Suspended) healthyStatus,
                    "requires healthy/current incremental mode"
                    "resynchronizing Watch status",
                    branchSwitchWatchInspection (Some GraceWatchRuntimeMode.HealthyIncremental) resynchronizingStatus,
                    "resynchronizing"
                    "pending local observations",
                    branchSwitchWatchInspection (Some GraceWatchRuntimeMode.HealthyIncremental) pendingStatus,
                    "pending local observations"
                    "dirty Watch status", branchSwitchWatchInspection (Some GraceWatchRuntimeMode.HealthyIncremental) dirtyStatus, "dirty working tree"
                    "startup Watch claim", branchSwitchWatchInspection (Some GraceWatchRuntimeMode.StartingUp) startupStatus, "still starting"
                |]

            for name, inspection, expectedReason in cases do
                let result, inspected = runBranchSwitchPreflight false inspection

                match result with
                | Error error ->
                    error.Error
                    |> should contain "Branch switch refused before mutation"

                    error.Error |> should contain expectedReason
                | Ok _ -> Assert.Fail($"Expected {name} to refuse branch switch.")

                inspected |> should equal true)

    let private sampleAnnotation =
        let lastChangedReferenceId = Guid.NewGuid()
        let introducedReferenceId = Guid.NewGuid()

        BranchAnnotationDto.Create(
            { StartLine = 7; EndLine = 9 },
            targetReferenceId,
            "src/App.fs",
            [|
                ReferenceType.Commit
                ReferenceType.Promotion
            |],
            250,
            true,
            [|
                { LineNumber = 7; Text = "let one = 1" }
                { LineNumber = 8; Text = "let two = 2" }
                { LineNumber = 9; Text = "let three = 3" }
            |],
            [|
                { BoundaryId = "boundary-1"; LineRange = { StartLine = 9; EndLine = 9 }; SourceRowIds = [| "row-3" |]; BoundaryKind = "TraversalBudgetReached" }
            |],
            [|
                { SpanId = "span-1"; BoundaryId = String.Empty; LineRange = { StartLine = 7; EndLine = 8 }; SourceRowIds = [| "row-1"; "row-2" |] }
                { SpanId = "span-2"; BoundaryId = "boundary-1"; LineRange = { StartLine = 9; EndLine = 9 }; SourceRowIds = [| "row-3" |] }
            |],
            [|
                { SourceRowId = "row-1"; SourceReferenceId = "source-last"; Path = "src/App.fs"; LineRange = { StartLine = 5; EndLine = 6 } }
                { SourceRowId = "row-2"; SourceReferenceId = "source-introduced"; Path = "src/App.fs"; LineRange = { StartLine = 1; EndLine = 2 } }
                { SourceRowId = "row-3"; SourceReferenceId = "source-boundary"; Path = "src/App.fs"; LineRange = { StartLine = 9; EndLine = 9 } }
            |],
            [|
                { AnnotationSourceReference.Default with
                    SourceReferenceId = "source-last"
                    ReferenceId = lastChangedReferenceId
                    ReferenceType = "Commit"
                    ReferenceText = "Change line constants"
                    CreatedBy = Some "alice"
                }
                { AnnotationSourceReference.Default with
                    SourceReferenceId = "source-introduced"
                    ReferenceId = introducedReferenceId
                    ReferenceType = "Promotion"
                    ReferenceText = "Introduce file"
                    CreatedBy = Some "bob"
                }
                { AnnotationSourceReference.Default with
                    SourceReferenceId = "source-boundary"
                    ReferenceId = lastChangedReferenceId
                    ReferenceType = "Commit"
                    ReferenceText = "Boundary row"
                    CreatedBy = None
                }
            |]
        )

    let private directorySha256Hash = Sha256Hash "111122223333444455556666777788889999aaaabbbbccccddddeeeeffff0000"

    let private directoryBlake3Hash = Blake3Hash "9999888877776666555544443333222211110000ffffeeeeddddccccbbbbaaaa"

    let private fileSha256Hash = Sha256Hash "aaaabbbbccccddddeeeeffff0000111122223333444455556666777788889999"

    let private fileBlake3Hash = Blake3Hash "bbbbccccddddeeeeffff0000111122223333444455556666777788889999aaaa"

    /// Builds branch directory with file test data used to exercise CLI branch behavior.
    let private branchDirectoryWithFile () =
        let file = FileVersion.CreateWithHashes "src/App.fs" fileSha256Hash fileBlake3Hash String.Empty false 123L

        let files = List<FileVersion>()
        files.Add(file)

        DirectoryVersion.CreateWithHashes
            (Guid.NewGuid())
            ownerId
            organizationId
            repositoryId
            Constants.RootDirectoryPath
            directorySha256Hash
            directoryBlake3Hash
            (List<DirectoryVersionId>())
            files
            123L

    /// Verifies that list contents formatter uses short blake3 hashes by default.
    [<Test>]
    let ``list contents formatter uses short BLAKE3 hashes by default`` () =
        let parseResult = parse [| "branch"; "list-contents" |]
        let displayMode = Common.HashOptions.bindVersionHashDisplayMode parseResult

        Branch.formatListContentsVersionHash displayMode directoryBlake3Hash directorySha256Hash
        |> should equal "99998888"

        /// Defines a test helper for used by the CLI branch scenario.
        let _, output =
            captureOutput (fun () ->
                Branch.printContents parseResult [| branchDirectoryWithFile () |]
                0)

        output |> should contain "BLAKE3 version hash"
        output |> should contain "99998888"
        output |> should contain "bbbbcccc"

        output
        |> should not' (contain $"{directorySha256Hash}")

        output
        |> should not' (contain $"{fileSha256Hash}")

    /// Verifies that list contents formatter honors full hash display options.
    [<Test>]
    let ``list contents formatter honors full hash display options`` () =
        let parseResult =
            parse [| "branch"
                     "list-contents"
                     "--full-hashes" |]

        let displayMode = Common.HashOptions.bindVersionHashDisplayMode parseResult

        Branch.formatListContentsVersionHash displayMode directoryBlake3Hash directorySha256Hash
        |> should equal $"{directoryBlake3Hash}"

        /// Defines a test helper for used by the CLI branch scenario.
        let _, output =
            captureOutput (fun () ->
                Branch.printContents parseResult [| branchDirectoryWithFile () |]
                0)

        output |> should contain $"{directoryBlake3Hash}"
        output |> should contain $"{fileBlake3Hash}"

    /// Verifies that list contents formatter adds sha 256 when requested.
    [<Test>]
    let ``list contents formatter adds SHA-256 when requested`` () =
        let parseResult =
            parse [| "branch"
                     "list-contents"
                     "--show-sha256" |]

        let displayMode = Common.HashOptions.bindVersionHashDisplayMode parseResult

        Branch.formatListContentsVersionHash displayMode directoryBlake3Hash directorySha256Hash
        |> should equal "99998888 (SHA-256 11112222)"

    /// Verifies that list contents formatter honors deprecated full sha display option.
    [<Test>]
    let ``list contents formatter honors deprecated full sha display option`` () =
        let parseResult =
            parse [| "branch"
                     "list-contents"
                     "--full-sha" |]

        let displayMode = Common.HashOptions.bindVersionHashDisplayMode parseResult

        Branch.formatListContentsVersionHash displayMode directoryBlake3Hash directorySha256Hash
        |> should equal $"{directoryBlake3Hash} (SHA-256 {directorySha256Hash})"

    /// Verifies that list contents formatter shows unavailable for missing blake3.
    [<Test>]
    let ``list contents formatter shows unavailable for missing BLAKE3`` () =
        let parseResult = parse [| "branch"; "list-contents" |]
        let displayMode = Common.HashOptions.bindVersionHashDisplayMode parseResult

        Branch.formatListContentsVersionHash displayMode (Blake3Hash String.Empty) directorySha256Hash
        |> should equal Common.HashOptions.MissingVersionHashText

    /// Verifies that annotate handler maps options to parameters.
    [<Test>]
    let ``annotate handler maps options to parameters`` () =
        let explicitReferenceId = Guid.NewGuid()
        /// Tracks captured changes so this scenario can assert the resulting side effect explicitly.
        let mutable captured = Unchecked.defaultof<AnnotateParameters>
        /// Tracks resolver Reference Id changes so this scenario can assert the resulting side effect explicitly.
        let mutable resolverReferenceId = None

        let parseResult =
            parse [| "branch"
                     "annotate"
                     "--path"
                     "src\\App.fs"
                     "--reference-id"
                     explicitReferenceId.ToString()
                     "-L"
                     "7,9"
                     "--reference-types"
                     "commit, Promotion"
                     "--show"
                     "introduced"
                     "--max-references"
                     "250" |]

        /// Applies annotation command options so branch command assertions can inspect the resulting request.
        let annotate (parameters: AnnotateParameters) : Task<GraceResult<BranchAnnotationDto>> =
            captured <- parameters
            Task.FromResult(Ok(GraceReturnValue.Create sampleAnnotation correlationId))

        /// Resolves the branch target reference supplied by the CLI scenario under test.
        let resolveTargetReference (referenceId: ReferenceId option) (_: CorrelationId) : Task<Result<CliCurrentStateCaptureResult, GraceError>> =
            resolverReferenceId <- referenceId
            Task.FromResult(Ok targetReferenceResult)

        let result =
            (Branch.annotateHandlerWith annotate resolveTargetReference parseResult)
                .Result

        match result with
        | Ok returnValue ->
            returnValue.ReturnValue.Path
            |> should equal "src/App.fs"

            captured.Path |> should equal "src/App.fs"

            captured.TargetReferenceId
            |> should equal targetReferenceId

            captured.StartLine |> should equal 7
            captured.EndLine |> should equal 9
            captured.MaxReferences |> should equal 250
            captured.IncludeLineText |> should equal true

            captured.ReferenceTypes
            |> should
                equal
                [|
                    ReferenceType.Commit
                    ReferenceType.Promotion
                |]

            resolverReferenceId
            |> should equal (Some explicitReferenceId)
        | Error error -> Assert.Fail($"Expected annotate handler success, got: {error.Error}")

    /// Verifies that annotate handler rejects invalid repository relative paths.
    [<TestCase("C:\\repo\\src\\App.fs", "--path must be repository-relative")>]
    [<TestCase("D:/repo/src/App.fs", "--path must be repository-relative")>]
    [<TestCase("../src/App.fs", "--path must not contain traversal")>]
    [<TestCase("src/../App.fs", "--path must not contain traversal")>]
    let ``annotate handler rejects invalid repository relative paths`` path expectedError =
        let parseResult =
            parse [| "branch"
                     "annotate"
                     "--path"
                     path |]

        /// Applies annotation command options so branch command assertions can inspect the resulting request.
        let annotate (_: AnnotateParameters) : Task<GraceResult<BranchAnnotationDto>> =
            Task.FromResult(Ok(GraceReturnValue.Create sampleAnnotation correlationId))

        /// Resolves the branch target reference supplied by the CLI scenario under test.
        let resolveTargetReference (_: ReferenceId option) (_: CorrelationId) : Task<Result<CliCurrentStateCaptureResult, GraceError>> =
            Task.FromResult(Ok targetReferenceResult)

        let result =
            (Branch.annotateHandlerWith annotate resolveTargetReference parseResult)
                .Result

        match result with
        | Ok _ -> Assert.Fail("Expected invalid path failure.")
        | Error error -> error.Error |> should contain expectedError

    /// Verifies that annotate handler rejects invalid line range.
    [<Test>]
    let ``annotate handler rejects invalid line range`` () =
        let parseResult =
            parse [| "branch"
                     "annotate"
                     "--path"
                     "src/App.fs"
                     "-L"
                     "9,7" |]

        /// Applies annotation command options so branch command assertions can inspect the resulting request.
        let annotate (_: AnnotateParameters) : Task<GraceResult<BranchAnnotationDto>> =
            Task.FromResult(Ok(GraceReturnValue.Create sampleAnnotation correlationId))

        /// Resolves the branch target reference supplied by the CLI scenario under test.
        let resolveTargetReference (_: ReferenceId option) (_: CorrelationId) : Task<Result<CliCurrentStateCaptureResult, GraceError>> =
            Task.FromResult(Ok targetReferenceResult)

        let result =
            (Branch.annotateHandlerWith annotate resolveTargetReference parseResult)
                .Result

        match result with
        | Ok _ -> Assert.Fail("Expected invalid line range failure.")
        | Error error ->
            error.Error
            |> should contain "Invalid annotation line range"

    /// Verifies that annotate handler rejects explicit zero end line.
    [<Test>]
    let ``annotate handler rejects explicit zero end line`` () =
        let parseResult =
            parse [| "branch"
                     "annotate"
                     "--path"
                     "src/App.fs"
                     "--start-line"
                     "5"
                     "--end-line"
                     "0" |]

        /// Applies annotation command options so branch command assertions can inspect the resulting request.
        let annotate (_: AnnotateParameters) : Task<GraceResult<BranchAnnotationDto>> =
            Task.FromResult(Ok(GraceReturnValue.Create sampleAnnotation correlationId))

        /// Resolves the branch target reference supplied by the CLI scenario under test.
        let resolveTargetReference (_: ReferenceId option) (_: CorrelationId) : Task<Result<CliCurrentStateCaptureResult, GraceError>> =
            Task.FromResult(Ok targetReferenceResult)

        let result =
            (Branch.annotateHandlerWith annotate resolveTargetReference parseResult)
                .Result

        match result with
        | Ok _ -> Assert.Fail("Expected explicit zero end line failure.")
        | Error error ->
            error.Error
            |> should contain "Invalid annotation line range"

    /// Verifies that annotate handler defaults omitted end line to start line.
    [<Test>]
    let ``annotate handler defaults omitted end line to start line`` () =
        /// Tracks captured changes so this scenario can assert the resulting side effect explicitly.
        let mutable captured = Unchecked.defaultof<AnnotateParameters>

        let parseResult =
            parse [| "branch"
                     "annotate"
                     "--path"
                     "src/App.fs"
                     "--start-line"
                     "5" |]

        /// Applies annotation command options so branch command assertions can inspect the resulting request.
        let annotate (parameters: AnnotateParameters) : Task<GraceResult<BranchAnnotationDto>> =
            captured <- parameters
            Task.FromResult(Ok(GraceReturnValue.Create sampleAnnotation correlationId))

        /// Resolves the branch target reference supplied by the CLI scenario under test.
        let resolveTargetReference (_: ReferenceId option) (_: CorrelationId) : Task<Result<CliCurrentStateCaptureResult, GraceError>> =
            Task.FromResult(Ok targetReferenceResult)

        let result =
            (Branch.annotateHandlerWith annotate resolveTargetReference parseResult)
                .Result

        match result with
        | Ok _ ->
            captured.StartLine |> should equal 5
            captured.EndLine |> should equal 5
        | Error error -> Assert.Fail($"Expected omitted end line success, got: {error.Error}")

    /// Verifies that annotate handler rejects reference type typo.
    [<Test>]
    let ``annotate handler rejects reference type typo`` () =
        let parseResult =
            parse [| "branch"
                     "annotate"
                     "--path"
                     "src/App.fs"
                     "--reference-types"
                     "Commit,Typo" |]

        /// Applies annotation command options so branch command assertions can inspect the resulting request.
        let annotate (_: AnnotateParameters) : Task<GraceResult<BranchAnnotationDto>> =
            Task.FromResult(Ok(GraceReturnValue.Create sampleAnnotation correlationId))

        /// Resolves the branch target reference supplied by the CLI scenario under test.
        let resolveTargetReference (_: ReferenceId option) (_: CorrelationId) : Task<Result<CliCurrentStateCaptureResult, GraceError>> =
            Task.FromResult(Ok targetReferenceResult)

        let result =
            (Branch.annotateHandlerWith annotate resolveTargetReference parseResult)
                .Result

        match result with
        | Ok _ -> Assert.Fail("Expected invalid Reference type failure.")
        | Error error ->
            error.Error
            |> should contain "Unknown Reference type"

    /// Verifies that human output is span grouped with requested line numbers and text.
    [<Test>]
    let ``human output is span grouped with requested line numbers and text`` () =
        let parseResult =
            parse [| "branch"
                     "annotate"
                     "--path"
                     "src/App.fs"
                     "--show"
                     "both" |]

        /// Defines a test helper for used by the CLI branch scenario.
        let _, output =
            captureOutput (fun () ->
                Branch.renderBranchAnnotationHumanOutput parseResult Branch.Both sampleAnnotation
                0)

        output |> should contain "Lines 7-8"
        output |> should contain "Line 9"
        output |> should contain "Last changed Reference"
        output |> should contain "Introduced Reference"
        output |> should contain "boundary-1"
        output |> should contain "7"
        output |> should contain "let one = 1"
        output |> should contain "9"
        output |> should contain "let three = 3"

    /// Verifies that show introduced suppresses last changed human label.
    [<Test>]
    let ``show introduced suppresses last changed human label`` () =
        let parseResult =
            parse [| "branch"
                     "annotate"
                     "--path"
                     "src/App.fs"
                     "--show"
                     "introduced" |]

        /// Defines a test helper for used by the CLI branch scenario.
        let _, output =
            captureOutput (fun () ->
                Branch.renderBranchAnnotationHumanOutput parseResult Branch.Introduced sampleAnnotation
                0)

        output |> should contain "Introduced Reference"

        output
        |> should not' (contain "Last changed Reference")

    /// Verifies that json output remains a single grace result document and skips human spans.
    [<Test>]
    let ``json output remains a single Grace result document and skips human spans`` () =
        let parseResult =
            parse [| "--output"
                     "Json"
                     "branch"
                     "annotate"
                     "--path"
                     "src/App.fs"
                     "--show"
                     "introduced" |]

        /// Verifies that the CLI branch scenario exits with the expected process status.
        let exitCode, output =
            captureOutput (fun () ->
                let result = Ok(GraceReturnValue.Create sampleAnnotation correlationId)
                let rendered = Common.renderOutput parseResult result
                Branch.renderBranchAnnotationHumanOutput parseResult Branch.Introduced sampleAnnotation
                rendered)

        exitCode |> should equal 0

        output
        |> should not' (contain "Introduced Reference")

        use document = JsonDocument.Parse(output)

        document.RootElement.ValueKind
        |> should equal JsonValueKind.Object

        document
            .RootElement
            .GetProperty("ReturnValue")
            .GetProperty("Path")
            .GetString()
        |> should equal "src/App.fs"

    /// Verifies that list contents json includes full dual hashes regardless of human display flags.
    [<Test>]
    let ``list contents json includes full dual hashes regardless of human display flags`` () =
        /// Asserts that json contains full dual hashes matches the expected contract.
        let assertJsonContainsFullDualHashes (parseResult: System.CommandLine.ParseResult) =
            parseResult.Errors.Count |> should equal 0

            /// Verifies that the CLI branch scenario exits with the expected process status.
            let exitCode, output =
                captureOutput (fun () ->
                    let result = Ok(GraceReturnValue.Create [| branchDirectoryWithFile () |] correlationId)
                    Common.renderOutput parseResult result)

            exitCode |> should equal 0

            use document = JsonDocument.Parse(output)
            let directory = document.RootElement.GetProperty("ReturnValue")[0]

            directory.GetProperty("Sha256Hash").GetString()
            |> should equal $"{directorySha256Hash}"

            directory.GetProperty("Blake3Hash").GetString()
            |> should equal $"{directoryBlake3Hash}"

            let file = directory.GetProperty("Files")[0]

            file.GetProperty("Sha256Hash").GetString()
            |> should equal $"{fileSha256Hash}"

            file.GetProperty("Blake3Hash").GetString()
            |> should equal $"{fileBlake3Hash}"

        let fullHashesParseResult =
            parse [| "--output"
                     "Json"
                     "branch"
                     "list-contents"
                     "--full-hashes"
                     "--show-sha256" |]

        assertJsonContainsFullDualHashes fullHashesParseResult

        let deprecatedFullShaParseResult =
            parse [| "--output"
                     "Json"
                     "branch"
                     "list-contents"
                     "--full-sha" |]

        assertJsonContainsFullDualHashes deprecatedFullShaParseResult

    /// Verifies that select output remains one machine readable document and skips human spans.
    [<Test>]
    let ``select output remains one machine readable document and skips human spans`` () =
        let parseResult =
            parse [| "branch"
                     "annotate"
                     "--path"
                     "src/App.fs"
                     "--show"
                     "introduced"
                     "--select"
                     "Path" |]

        /// Verifies that the CLI branch scenario exits with the expected process status.
        let exitCode, output =
            captureOutput (fun () ->
                let result = Ok(GraceReturnValue.Create sampleAnnotation correlationId)
                let rendered = Common.renderOutput parseResult result
                Branch.renderBranchAnnotationHumanOutput parseResult Branch.Introduced sampleAnnotation
                rendered)

        exitCode |> should equal 0

        output
        |> should not' (contain "Introduced Reference")

        output
        |> should not' (contain "Branch annotation for")

        use document = JsonDocument.Parse(output)

        document.RootElement.ValueKind
        |> should equal JsonValueKind.String

        document.RootElement.GetString()
        |> should equal "src/App.fs"
