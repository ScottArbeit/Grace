namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI.Command
open Grace.Shared
open Grace.Types.Common
open NUnit.Framework
open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

module WorkingDirectoryUpdate = WorkingDirectoryUpdateContracts

/// Proves local-root lease and owned-marker behavior without invoking a caller workflow.
module WorkingDirectoryUpdateCoordinationTests =
    /// Extracts a successful private-contract construction or reports its rejection reason.
    let private required =
        function
        | Ok value -> value
        | Error error -> failwith error

    /// Creates and removes a unique temporary root around one coordination scenario.
    let private withTempRoot action =
        let root = Path.Combine(Path.GetTempPath(), "Grace", "tests", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore

        try
            action root
        finally
            if Directory.Exists(root) then Directory.Delete(root, true)

    /// Builds the complete selected target needed by marker scenarios.
    let private targetWith repositoryId branchId rootDirectoryVersionId (sha256Hash: string) (blake3Hash: string) =
        WorkingDirectoryUpdate.Target.create repositoryId branchId rootDirectoryVersionId (Sha256Hash sha256Hash) (Blake3Hash blake3Hash)
        |> required

    /// Builds the standard complete selected target needed by marker scenarios.
    let private target repositoryId branchId =
        targetWith
            repositoryId
            branchId
            (Guid.Parse("b1f5373a-7303-4dc1-b085-113e8efed444"))
            "40786b40bc5f3bc9070bf49f72bbf1f8b160bb952156e3c9894438c82d03dbd9"
            "dc938391649e1c587adcf4ddfe0b06b7a6c47df9e9812c4bea6d01a7c9eab836"

    /// Builds a deterministic Branch operation bound to the supplied target.
    let private branchOperation target =
        WorkingDirectoryUpdate.Operation.branchSwitch
            (Guid.Parse("2c461ab1-72a0-42c3-9c2e-ea9c0c3b83de"))
            (Guid.Parse("d9622ad2-552d-4ab1-996e-2d756af82047"))
            target
        |> required

    /// Returns the worker command built with this test assembly.
    let private workerCommand () =
        let baseDirectory = DirectoryInfo(AppContext.BaseDirectory)
        let configuration = baseDirectory.Parent.Name
        let targetFramework = baseDirectory.Name
        let srcDirectory = baseDirectory.Parent.Parent.Parent.Parent
        let workerDirectory = Path.Combine(srcDirectory.FullName, "Grace.CLI.LocalStateDb.Worker", "bin", configuration, targetFramework)
        let executable = Path.Combine(workerDirectory, "Grace.CLI.LocalStateDb.Worker.exe")
        let assembly = Path.Combine(workerDirectory, "Grace.CLI.LocalStateDb.Worker.dll")

        if File.Exists(executable) then
            executable, []
        elif File.Exists(assembly) then
            "dotnet", [ assembly ]
        else
            failwith "The local-state worker binary was not found. Build the test project before running coordination tests."

    /// Starts a separate process that holds the same real exclusive coordination handle until terminated.
    let private startLeaseWorker scope readyFile =
        let executable, prefix = workerCommand ()
        let startInfo = ProcessStartInfo()
        startInfo.FileName <- executable
        startInfo.UseShellExecute <- false
        startInfo.CreateNoWindow <- true

        for argument in prefix do
            startInfo.ArgumentList.Add(argument)

        startInfo.ArgumentList.Add("hold-file-lease")
        startInfo.ArgumentList.Add(WorkingDirectoryUpdateCoordination.Scope.leasePath scope)
        startInfo.ArgumentList.Add(readyFile)

        let childProcess = new Process()
        childProcess.StartInfo <- startInfo

        if not (childProcess.Start()) then
            failwith "The coordination lease worker did not start."

        childProcess

    /// Waits briefly for a worker's explicit lease-ready signal without hiding a stalled child process.
    let private waitForReady (childProcess: Process) readyFile =
        let deadline = DateTime.UtcNow.AddSeconds(10.0)

        while not (File.Exists(readyFile))
              && not childProcess.HasExited
              && DateTime.UtcNow < deadline do
            Thread.Sleep(25)

        if childProcess.HasExited then
            failwith $"The coordination lease worker exited with {childProcess.ExitCode}."
        elif not (File.Exists(readyFile)) then
            failwith "The coordination lease worker did not report a held lease."

    /// Proves repository/root scope is stable across branch changes but distinct for another local root.
    [<Test>]
    let ``scope excludes branch identity and separates local roots`` () =
        withTempRoot (fun root ->
            let repositoryId = Guid.Parse("5f48b9a7-5537-4d2d-aeda-16c6d66a1bbc")
            let branchA = Guid.Parse("f191d2d1-8194-4e48-b4e0-9f183dab177e")
            let branchB = Guid.Parse("3bd9490f-9b7f-4e89-bf85-56ae3ea07e1b")
            let secondRoot = Path.Combine(root, "second")
            Directory.CreateDirectory(secondRoot) |> ignore

            let first =
                WorkingDirectoryUpdateCoordination.Scope.create repositoryId root
                |> required

            let afterBranchChange =
                WorkingDirectoryUpdateCoordination.Scope.create repositoryId root
                |> required

            let separate =
                WorkingDirectoryUpdateCoordination.Scope.create repositoryId secondRoot
                |> required

            branchA |> should not' (equal branchB)

            WorkingDirectoryUpdateCoordination.Scope.value first
            |> should equal (WorkingDirectoryUpdateCoordination.Scope.value afterBranchChange)

            WorkingDirectoryUpdateCoordination.Scope.value first
            |> should not' (equal (WorkingDirectoryUpdateCoordination.Scope.value separate))

            use firstLease =
                WorkingDirectoryUpdateCoordination.Lease.acquire first CancellationToken.None
                |> fun task -> task.GetAwaiter().GetResult()

            use separateLease =
                WorkingDirectoryUpdateCoordination.Lease.acquire separate CancellationToken.None
                |> fun task -> task.GetAwaiter().GetResult()

            ())

    /// Proves case-only Windows spelling differences derive one local-root scope.
    [<Test>]
    let ``scope normalizes Windows root case`` () =
        if OperatingSystem.IsWindows() then
            withTempRoot (fun root ->
                let repositoryId = Guid.Parse("5f48b9a7-5537-4d2d-aeda-16c6d66a1bbc")
                let lower = root.ToLowerInvariant()
                let upper = root.ToUpperInvariant()

                WorkingDirectoryUpdateCoordination.Scope.create repositoryId lower
                |> required
                |> WorkingDirectoryUpdateCoordination.Scope.value
                |> should
                    equal
                    (WorkingDirectoryUpdateCoordination.Scope.create repositoryId upper
                     |> required
                     |> WorkingDirectoryUpdateCoordination.Scope.value))

    /// Proves a waiting same-scope lease observes cancellation rather than spinning indefinitely.
    [<Test>]
    let ``same scope lease waits cancellably`` () =
        withTempRoot (fun root ->
            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create (Guid.NewGuid()) root
                |> required

            use held =
                WorkingDirectoryUpdateCoordination.Lease.acquire scope CancellationToken.None
                |> fun task -> task.GetAwaiter().GetResult()

            use cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150.0))

            Assert.Catch<OperationCanceledException>(
                Action (fun () ->
                    WorkingDirectoryUpdateCoordination.Lease.acquire scope cancellation.Token
                    |> fun task -> task.GetAwaiter().GetResult()
                    |> ignore)
            )
            |> ignore)

    /// Proves a real second process blocks the same scope and abrupt termination releases its exclusive handle.
    [<Test>]
    let ``real second process serializes and abrupt exit releases lease`` () =
        withTempRoot (fun root ->
            let repositoryId = Guid.NewGuid()

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create repositoryId root
                |> required

            let readyFile = Path.Combine(root, "lease-ready")
            use childProcess = startLeaseWorker scope readyFile

            try
                waitForReady childProcess readyFile

                use cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150.0))

                Assert.Catch<OperationCanceledException>(
                    Action (fun () ->
                        WorkingDirectoryUpdateCoordination.Lease.acquire scope cancellation.Token
                        |> fun task -> task.GetAwaiter().GetResult()
                        |> ignore)
                )
                |> ignore

                childProcess.Kill(true)

                childProcess.WaitForExit(10000)
                |> should equal true

                use acquired =
                    WorkingDirectoryUpdateCoordination.Lease.acquire scope CancellationToken.None
                    |> fun task -> task.GetAwaiter().GetResult()

                acquired |> should not' (be Null)
            finally
                if not childProcess.HasExited then
                    childProcess.Kill(true)
                    childProcess.WaitForExit(10000) |> ignore

                ())

    /// Proves marker inspection requires the complete expected target as well as the exact Watch identity.
    [<Test>]
    let ``marker inspection adopts only the exact operation caller and target and rejects malformed schemas`` () =
        withTempRoot (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            let selectedTarget = target repositoryId branchId

            let operation =
                WorkingDirectoryUpdate.Operation.watchReplay repositoryId branchId "selected-cursor"
                |> required

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create repositoryId root
                |> required

            let marker =
                WorkingDirectoryUpdateCoordination.Marker.create scope (WorkingDirectoryUpdate.AttemptToken.create ()) selectedTarget operation
                |> required

            WorkingDirectoryUpdateCoordination.Marker.write scope marker
            |> fun task -> task.GetAwaiter().GetResult()

            WorkingDirectoryUpdateCoordination.Sidecar.write scope operation
            |> fun task -> task.GetAwaiter().GetResult()

            let sidecarPath = WorkingDirectoryUpdateCoordination.Scope.sidecarPath scope

            File.Exists(sidecarPath) |> should equal true

            use sidecarDocument = JsonDocument.Parse(File.ReadAllText(sidecarPath))
            let sidecar = sidecarDocument.RootElement

            sidecar.ValueKind
            |> should equal JsonValueKind.Object

            sidecar.EnumerateObject()
            |> Seq.map (fun property -> property.Name)
            |> Set.ofSeq
            |> should
                equal
                (Set.ofList [ "schemaVersion"
                              "operationId"
                              "completedUtc" ])

            let schemaVersion = sidecar.GetProperty("schemaVersion")

            schemaVersion.ValueKind
            |> should equal JsonValueKind.Number

            schemaVersion.GetInt32() |> should equal 1

            let operationId = sidecar.GetProperty("operationId")

            operationId.ValueKind
            |> should equal JsonValueKind.String

            operationId.GetString()
            |> should equal (WorkingDirectoryUpdate.Operation.value operation)

            let completedUtc = sidecar.GetProperty("completedUtc")

            completedUtc.ValueKind
            |> should equal JsonValueKind.String

            let completedValue = completedUtc.GetString()

            match DateTimeOffset.TryParse(completedValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with
            | true, completedAt ->
                completedAt.Offset |> should equal TimeSpan.Zero

                completedValue
                |> should
                    equal
                    (completedAt
                        .ToUniversalTime()
                        .ToString("O", CultureInfo.InvariantCulture))
            | false, _ -> Assert.Fail("The sidecar completion timestamp must be an ISO 8601 UTC value.")

            WorkingDirectoryUpdateCoordination.Marker.inspect scope selectedTarget operation
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal WorkingDirectoryUpdateCoordination.MarkerInspection.ExactMatch

            let differentRootDirectoryVersion =
                targetWith
                    repositoryId
                    branchId
                    (Guid.Parse("6bf7e6e5-33ac-450d-8e24-b07856a06cf0"))
                    "40786b40bc5f3bc9070bf49f72bbf1f8b160bb952156e3c9894438c82d03dbd9"
                    "dc938391649e1c587adcf4ddfe0b06b7a6c47df9e9812c4bea6d01a7c9eab836"

            let differentSha256 =
                targetWith
                    repositoryId
                    branchId
                    (Guid.Parse("b1f5373a-7303-4dc1-b085-113e8efed444"))
                    "50786b40bc5f3bc9070bf49f72bbf1f8b160bb952156e3c9894438c82d03dbd9"
                    "dc938391649e1c587adcf4ddfe0b06b7a6c47df9e9812c4bea6d01a7c9eab836"

            let differentBlake3 =
                targetWith
                    repositoryId
                    branchId
                    (Guid.Parse("b1f5373a-7303-4dc1-b085-113e8efed444"))
                    "40786b40bc5f3bc9070bf49f72bbf1f8b160bb952156e3c9894438c82d03dbd9"
                    "ec938391649e1c587adcf4ddfe0b06b7a6c47df9e9812c4bea6d01a7c9eab836"

            for differentTarget in
                [
                    differentRootDirectoryVersion
                    differentSha256
                    differentBlake3
                ] do
                WorkingDirectoryUpdateCoordination.Marker.inspect scope differentTarget operation
                |> fun task -> task.GetAwaiter().GetResult()
                |> should equal WorkingDirectoryUpdateCoordination.MarkerInspection.DifferentOperation

            let differentOperation =
                WorkingDirectoryUpdate.Operation.watchReplay repositoryId branchId "different-cursor"
                |> required

            WorkingDirectoryUpdateCoordination.Marker.inspect scope selectedTarget differentOperation
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal WorkingDirectoryUpdateCoordination.MarkerInspection.DifferentOperation

            let persistedMarker = File.ReadAllText(WorkingDirectoryUpdateCoordination.Scope.markerPath scope)

            let wrongCaller = persistedMarker.Replace("\"callerKind\":\"watch\"", "\"callerKind\":\"branch\"", StringComparison.Ordinal)

            wrongCaller |> should not' (equal persistedMarker)

            File.WriteAllText(WorkingDirectoryUpdateCoordination.Scope.markerPath scope, wrongCaller)

            WorkingDirectoryUpdateCoordination.Marker.inspect scope selectedTarget operation
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal WorkingDirectoryUpdateCoordination.MarkerInspection.DifferentOperation

            let unsupported = persistedMarker.Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal)

            unsupported |> should not' (equal persistedMarker)

            unsupported.Replace("\"schemaVersion\":2", "\"schemaVersion\":1", StringComparison.Ordinal)
            |> should equal persistedMarker

            File.WriteAllText(WorkingDirectoryUpdateCoordination.Scope.markerPath scope, unsupported)

            WorkingDirectoryUpdateCoordination.Marker.inspect scope selectedTarget operation
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal WorkingDirectoryUpdateCoordination.MarkerInspection.MalformedOrUnsupported

            File.WriteAllText(WorkingDirectoryUpdateCoordination.Scope.markerPath scope, "{not-json")

            WorkingDirectoryUpdateCoordination.Marker.inspect scope selectedTarget operation
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal WorkingDirectoryUpdateCoordination.MarkerInspection.MalformedOrUnsupported)

    /// Proves cleanup rereads marker ownership and never removes a replacement attempt token.
    [<Test>]
    let ``marker cleanup removes only its exact current token`` () =
        withTempRoot (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            let selectedTarget = target repositoryId branchId
            let operation = branchOperation selectedTarget

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create repositoryId root
                |> required

            let firstToken = WorkingDirectoryUpdate.AttemptToken.create ()

            let first =
                WorkingDirectoryUpdateCoordination.Marker.create scope firstToken selectedTarget operation
                |> required

            let secondToken = WorkingDirectoryUpdate.AttemptToken.create ()

            let second =
                WorkingDirectoryUpdateCoordination.Marker.create scope secondToken selectedTarget operation
                |> required

            WorkingDirectoryUpdateCoordination.Marker.write scope first
            |> fun task -> task.GetAwaiter().GetResult()

            WorkingDirectoryUpdateCoordination.Marker.write scope second
            |> fun task -> task.GetAwaiter().GetResult()

            WorkingDirectoryUpdateCoordination.Marker.tryRemoveOwned scope firstToken
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal WorkingDirectoryUpdateCoordination.MarkerCleanup.DifferentOperationEvidence

            File.Exists(WorkingDirectoryUpdateCoordination.Scope.markerPath scope)
            |> should equal true

            WorkingDirectoryUpdateCoordination.Marker.tryRemoveOwned scope secondToken
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal WorkingDirectoryUpdateCoordination.MarkerCleanup.ExactMatchCleaned

            File.Exists(WorkingDirectoryUpdateCoordination.Scope.markerPath scope)
            |> should equal false)

    /// Proves a failed first marker publication leaves fresh admission available and removes its temporary file.
    [<Test>]
    let ``failed fresh marker publication leaves no durable evidence`` () =
        withTempRoot (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            let selectedTarget = target repositoryId branchId
            let operation = branchOperation selectedTarget

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create repositoryId root
                |> required

            let attemptToken = WorkingDirectoryUpdate.AttemptToken.create ()

            let marker =
                WorkingDirectoryUpdateCoordination.Marker.create scope attemptToken selectedTarget operation
                |> required

            Assert.Throws<IOException>(
                Action (fun () ->
                    WorkingDirectoryUpdateCoordination.Marker.writeWithBeforePublish scope marker (fun () ->
                        raise (IOException("forced marker publication failure")))
                    |> fun task -> task.GetAwaiter().GetResult())
            )
            |> ignore

            WorkingDirectoryUpdateCoordination.Marker.inspect scope selectedTarget operation
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal WorkingDirectoryUpdateCoordination.MarkerInspection.Missing

            WorkingDirectoryUpdateCoordination.Marker.write scope marker
            |> fun task -> task.GetAwaiter().GetResult()

            WorkingDirectoryUpdateCoordination.Marker.inspect scope selectedTarget operation
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal WorkingDirectoryUpdateCoordination.MarkerInspection.ExactMatch

            WorkingDirectoryUpdateCoordination.Marker.tryRemoveOwned scope attemptToken
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal WorkingDirectoryUpdateCoordination.MarkerCleanup.ExactMatchCleaned

            Directory.GetFiles(WorkingDirectoryUpdateCoordination.Scope.directory scope, "*.tmp")
            |> should be Empty)

    /// Proves a failed replacement preserves the exact marker that an identical retry may adopt.
    [<Test>]
    let ``failed exact marker replacement preserves prior retry evidence`` () =
        withTempRoot (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            let selectedTarget = target repositoryId branchId
            let operation = branchOperation selectedTarget

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create repositoryId root
                |> required

            let firstToken = WorkingDirectoryUpdate.AttemptToken.create ()

            let first =
                WorkingDirectoryUpdateCoordination.Marker.create scope firstToken selectedTarget operation
                |> required

            let replacement =
                WorkingDirectoryUpdateCoordination.Marker.create scope (WorkingDirectoryUpdate.AttemptToken.create ()) selectedTarget operation
                |> required

            WorkingDirectoryUpdateCoordination.Marker.write scope first
            |> fun task -> task.GetAwaiter().GetResult()

            let markerPath = WorkingDirectoryUpdateCoordination.Scope.markerPath scope
            let priorEvidence = File.ReadAllText(markerPath)

            Assert.Throws<IOException>(
                Action (fun () ->
                    WorkingDirectoryUpdateCoordination.Marker.writeWithBeforePublish scope replacement (fun () ->
                        raise (IOException("forced marker replacement failure")))
                    |> fun task -> task.GetAwaiter().GetResult())
            )
            |> ignore

            File.ReadAllText(markerPath)
            |> should equal priorEvidence

            WorkingDirectoryUpdateCoordination.Marker.inspect scope selectedTarget operation
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal WorkingDirectoryUpdateCoordination.MarkerInspection.ExactMatch

            WorkingDirectoryUpdateCoordination.Marker.tryRemoveOwned scope firstToken
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal WorkingDirectoryUpdateCoordination.MarkerCleanup.ExactMatchCleaned

            Directory.GetFiles(WorkingDirectoryUpdateCoordination.Scope.directory scope, "*.tmp")
            |> should be Empty)

    /// Proves missing, damaged, and unreadable cleanup evidence is never treated as successful cleanup.
    [<Test>]
    let ``marker cleanup distinguishes every non-successful evidence disposition`` () =
        withTempRoot (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            let selectedTarget = target repositoryId branchId
            let operation = branchOperation selectedTarget

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create repositoryId root
                |> required

            let token = WorkingDirectoryUpdate.AttemptToken.create ()
            let markerPath = WorkingDirectoryUpdateCoordination.Scope.markerPath scope

            WorkingDirectoryUpdateCoordination.Marker.inspect scope selectedTarget operation
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal WorkingDirectoryUpdateCoordination.MarkerInspection.Missing

            WorkingDirectoryUpdateCoordination.Marker.tryRemoveOwned scope token
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal WorkingDirectoryUpdateCoordination.MarkerCleanup.NoMarker

            Directory.CreateDirectory(Path.GetDirectoryName(markerPath))
            |> ignore

            File.WriteAllText(markerPath, "{not-json")

            WorkingDirectoryUpdateCoordination.Marker.tryRemoveOwned scope token
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal WorkingDirectoryUpdateCoordination.MarkerCleanup.MalformedOrUnsupportedEvidence

            let marker =
                WorkingDirectoryUpdateCoordination.Marker.create scope token selectedTarget operation
                |> required

            WorkingDirectoryUpdateCoordination.Marker.write scope marker
            |> fun task -> task.GetAwaiter().GetResult()

            use markerHandle = new FileStream(markerPath, FileMode.Open, FileAccess.Read, FileShare.None)

            WorkingDirectoryUpdateCoordination.Marker.tryRemoveOwned scope token
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal WorkingDirectoryUpdateCoordination.MarkerCleanup.UnreadableEvidence)

    /// Proves an unreadable marker is preserved rather than treated as missing or adoptable evidence.
    [<Test>]
    let ``marker inspection distinguishes unreadable evidence`` () =
        withTempRoot (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            let selectedTarget = target repositoryId branchId
            let operation = branchOperation selectedTarget

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create repositoryId root
                |> required

            let marker =
                WorkingDirectoryUpdateCoordination.Marker.create scope (WorkingDirectoryUpdate.AttemptToken.create ()) selectedTarget operation
                |> required

            WorkingDirectoryUpdateCoordination.Marker.write scope marker
            |> fun task -> task.GetAwaiter().GetResult()

            use markerHandle = new FileStream(WorkingDirectoryUpdateCoordination.Scope.markerPath scope, FileMode.Open, FileAccess.Read, FileShare.None)

            WorkingDirectoryUpdateCoordination.Marker.inspect scope selectedTarget operation
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal WorkingDirectoryUpdateCoordination.MarkerInspection.Unreadable)

    /// Proves a matching marker remains visible when exact-token cleanup cannot delete its current file.
    [<Test>]
    let ``marker cleanup distinguishes exact cleanup failure`` () =
        withTempRoot (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            let selectedTarget = target repositoryId branchId
            let operation = branchOperation selectedTarget

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create repositoryId root
                |> required

            let token = WorkingDirectoryUpdate.AttemptToken.create ()

            let marker =
                WorkingDirectoryUpdateCoordination.Marker.create scope token selectedTarget operation
                |> required

            WorkingDirectoryUpdateCoordination.Marker.write scope marker
            |> fun task -> task.GetAwaiter().GetResult()

            WorkingDirectoryUpdateCoordination.Marker.tryRemoveOwnedWithDelete scope token (fun _ ->
                raise (IOException("forced portable marker deletion failure")))
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal WorkingDirectoryUpdateCoordination.MarkerCleanup.ExactCleanupFailed

            File.Exists(WorkingDirectoryUpdateCoordination.Scope.markerPath scope)
            |> should equal true)
