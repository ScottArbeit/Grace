namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.Command
open Grace.CLI.Services
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Parameters.Branch
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Grace.Types.Annotation
open Grace.Types.Branch
open Grace.Types.Common
open Grace.Types.DirectoryVersion
open Grace.Types.Reference
open NodaTime
open NUnit.Framework
open Spectre.Console
open System
open System.Collections.Generic
open System.IO
open System.IO.Compression
open System.Net
open System.Net.Sockets
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
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

    /// Proves both supported Reference-only admissions enter the shared WDU transaction.
    [<Test>]
    let ``reference-only route selects WDU admission for both Save modes`` () =
        Branch.referenceOnlySwitchRoute false
        |> should equal Branch.ReferenceWithoutSave

        Branch.referenceOnlySwitchRoute true
        |> should equal Branch.ReferenceWithSave

    /// Proves initial and resumed Reference output share one stable JSON field set.
    [<Test>]
    let ``reference switch output serializes one stable shape`` () =
        let directoryVersionId = Guid.Parse("11111111-1111-4111-8111-111111111111")
        let output = Branch.referenceSwitchOutput "FinalizationIncomplete" "repair" branchId directoryVersionId
        use document = JsonDocument.Parse(serialize output)

        let properties =
            document.RootElement.EnumerateObject()
            |> Seq.map (fun property -> property.Name)
            |> Set.ofSeq

        properties
        |> should
            equal
            (set [ "Outcome"
                   "Message"
                   "BranchId"
                   "DirectoryVersionId" ])

        document
            .RootElement
            .GetProperty("Outcome")
            .GetString()
        |> should equal "FinalizationIncomplete"

        document
            .RootElement
            .GetProperty("Message")
            .GetString()
        |> should equal "repair"

        document
            .RootElement
            .GetProperty("BranchId")
            .GetGuid()
        |> should equal branchId

        document
            .RootElement
            .GetProperty("DirectoryVersionId")
            .GetGuid()
        |> should equal directoryVersionId

    /// Proves every public switch outcome preserves its exit classification and classified message.
    [<Test>]
    let ``hash switch outcome projects all public cases`` () =
        let target =
            WorkingDirectoryUpdateContracts.Target.create
                repositoryId
                branchId
                (Guid.NewGuid())
                (Sha256Hash(String.replicate 64 "a"))
                (Blake3Hash(String.replicate 64 "b"))
            |> Result.defaultWith failwith

        let operation =
            WorkingDirectoryUpdateContracts.Operation.branchSwitchWithSelection branchId WorkingDirectoryUpdateContracts.BranchSelection.DirectoryVersion target
            |> Result.defaultWith failwith

        let updated =
            WorkingDirectoryUpdateContracts.Receipt.create target operation true
            |> Result.defaultWith failwith

        let unchanged =
            WorkingDirectoryUpdateContracts.Receipt.create target operation false
            |> Result.defaultWith failwith

        let failure =
            WorkingDirectoryUpdateContracts.Failure.create "classified failure"
            |> Result.defaultWith failwith

        [
            WorkingDirectoryUpdateContracts.Outcome.Updated updated, "Updated", 0
            WorkingDirectoryUpdateContracts.Outcome.Unchanged unchanged, "Unchanged", 0
            WorkingDirectoryUpdateContracts.Outcome.Rejected failure, "Rejected", -1
            WorkingDirectoryUpdateContracts.Outcome.UpdateIncomplete failure, "UpdateIncomplete", -1
            WorkingDirectoryUpdateContracts.Outcome.FinalizationIncomplete(updated, failure), "FinalizationIncomplete", -1
        ]
        |> List.iter (fun (outcome, name, exitCode) ->
            let actualName, message, actualExitCode = Branch.projectHashSwitchOutcome outcome
            actualName |> should equal name
            actualExitCode |> should equal exitCode

            if exitCode <> 0 then message |> should equal "classified failure")

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

    /// Verifies an accepted-but-response-lost Promote retry reuses one distinct child Rebase identity.
    [<Test>]
    let ``promote retry derives the same child rebase identity`` () =
        let promotionReferenceId = Guid.Parse("11111111-7301-4000-8000-111111111111")
        let firstAttempt = Branch.buildPromotionRebaseReferenceId promotionReferenceId
        let responseLostRetry = Branch.buildPromotionRebaseReferenceId promotionReferenceId
        let otherPromotion = Branch.buildPromotionRebaseReferenceId (Guid.Parse("22222222-7301-4000-8000-222222222222"))

        firstAttempt |> should equal responseLostRetry

        firstAttempt
        |> should not' (equal promotionReferenceId)

        firstAttempt |> should not' (equal otherPromotion)

    /// Verifies status accepts the specific parentless result without weakening unrelated SDK failures.
    [<Test>]
    let ``branch status maps only parentless result to no-parent rendering input`` () =
        let realParent = { Grace.Types.Branch.BranchDto.Default with BranchId = Guid.NewGuid(); BranchName = BranchName "main" }

        let parentlessError = GraceError.Create (BranchError.getErrorMessage BranchError.ParentBranchDoesNotExist) correlationId

        let unrelatedError = GraceError.Create "Parent branch lookup failed." correlationId

        match Branch.classifyParentBranchForStatus (Ok(GraceReturnValue.Create realParent correlationId)) with
        | Ok actualParent -> actualParent |> should equal realParent
        | Error error -> Assert.Fail($"Expected real parent branch status input, got: {error.Error}")

        match Branch.classifyParentBranchForStatus (Error parentlessError) with
        | Ok parentBranch ->
            parentBranch
            |> should equal Grace.Types.Branch.BranchDto.Default
        | Error error -> Assert.Fail($"Expected parentless branch status input, got: {error.Error}")

        match Branch.classifyParentBranchForStatus (Error unrelatedError) with
        | Ok _ -> Assert.Fail("Expected unrelated parent branch SDK error to remain fatal.")
        | Error error -> error |> should equal unrelatedError

    /// Verifies canonical typed-slot sentinels remain absent after JSON round-tripping while concrete References stay eligible for lookup.
    [<Test>]
    let ``concrete reference ids exclude JSON round-tripped typed sentinels`` () =
        let concreteReferenceId = Guid.NewGuid()
        let roundTrippedSentinel = deserialize<ReferenceDto> (serialize ReferenceDto.Default)
        let concreteReference = { ReferenceDto.Default with ReferenceId = concreteReferenceId }

        Common.concreteReferenceIds [ roundTrippedSentinel
                                      concreteReference ]
        |> should equal [ concreteReferenceId ]

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
            saveConfigFile (Path.Combine(configuration.ConfigurationDirectory, Constants.GraceConfigFileName)) configuration
            resetConfiguration ()

            action ()
        finally
            resetConfiguration ()
            Services.parseResult <- originalParseResult
            Environment.CurrentDirectory <- originalDir
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()

            if Directory.Exists(tempDir) then Directory.Delete(tempDir, true)

    /// Computes stable content hashes for the binary files served by the Save switch loopback fixture.
    let private switchFixtureHashes (bytes: byte array) =
        Sha256Hash(
            Convert
                .ToHexString(SHA256.HashData(bytes))
                .ToLowerInvariant()
        ),
        Blake3Hash(ContentAddress.computeBlake3Hex bytes)

    /// Creates one complete root-only local status used before the Save switch updates its tracked file.
    let private switchFixtureStatus (configuration: GraceConfiguration) (directoryId: DirectoryVersionId) (relativePath: string) (bytes: byte array) =
        let sha256, blake3 = switchFixtureHashes bytes

        let file =
            LocalFileVersion.CreateWithHashes (RelativePath relativePath) sha256 blake3 true (int64 bytes.Length) (getCurrentInstant ()) true DateTime.UtcNow

        let entry = Services.DirectoryVersionPreimageEntry.File file.RelativePath file.Size file.Blake3Hash file.Sha256Hash

        let root =
            LocalDirectoryVersion.CreateWithHashes
                directoryId
                configuration.OwnerId
                configuration.OrganizationId
                configuration.RepositoryId
                (RelativePath Constants.RootDirectoryPath)
                (Services.computeSha256ForDirectoryEntries (RelativePath Constants.RootDirectoryPath) [| entry |])
                (Services.computeBlake3ForDirectory (RelativePath Constants.RootDirectoryPath) [| entry |])
                (List<DirectoryVersionId>())
                (List<LocalFileVersion>([| file |]))
                file.Size
                DateTime.UtcNow

        let index = GraceIndex()
        index[root.DirectoryVersionId] <- root

        { GraceStatus.Default with
            Index = index
            RootDirectoryId = root.DirectoryVersionId
            RootDirectorySha256Hash = root.Sha256Hash
            RootDirectoryBlake3Hash = root.Blake3Hash
        },
        root

    /// Serves the Save switch control-plane and direct binary Azure Blob exchanges through one loopback listener.
    let private withSaveSwitchLoopback
        (currentBranch: BranchDto)
        (selectedBranch: BranchDto)
        (selectedReference: ReferenceDto)
        (selectedDirectory: LocalDirectoryVersion)
        (editedBytes: byte array)
        (targetBytes: byte array)
        (beforeTargetBlobRead: unit -> unit)
        (action: string -> ResizeArray<string> -> ResizeArray<string> -> ResizeArray<byte array> -> unit)
        =
        use listener = new TcpListener(IPAddress.Loopback, 0)
        use cancellation = new CancellationTokenSource()
        listener.Start()
        let port = (listener.LocalEndpoint :?> IPEndPoint).Port
        let baseUri = $"http://127.0.0.1:{port}"
        let requests = ResizeArray<string>()
        let headers = ResizeArray<string>()
        let bodies = ResizeArray<byte array>()
        let mutable branchGetCount = 0

        /// Decodes one complete HTTP chunked body so binary Blob uploads can be asserted as their original bytes.
        let decodeChunkedBody (bytes: byte array) =
            use decoded = new MemoryStream()
            let mutable offset = 0
            let mutable complete = false

            while not complete do
                let lineEnd =
                    bytes
                    |> Array.skip offset
                    |> Array.pairwise
                    |> Array.tryFindIndex (fun (first, second) -> first = byte '\r' && second = byte '\n')
                    |> Option.map (fun index -> offset + index)
                    |> Option.defaultWith (fun () -> invalidOp "Save switch loopback chunk header ended early.")

                let size =
                    Encoding.ASCII.GetString(bytes, offset, lineEnd - offset)
                    |> fun value -> value.Split(';')[0]
                    |> fun value -> Convert.ToInt32(value, 16)

                offset <- lineEnd + 2

                if size = 0 then
                    complete <- true
                else
                    if bytes.Length < offset + size + 2 then
                        invalidOp "Save switch loopback chunk payload ended early."

                    decoded.Write(bytes, offset, size)
                    offset <- offset + size + 2

            decoded.ToArray()

        /// Decompresses the direct Blob upload payload so the fixture verifies the original staged file bytes.
        let decodeGzipBody (bytes: byte array) =
            use compressed = new MemoryStream(bytes)
            use gzip = new GZipStream(compressed, CompressionMode.Decompress)
            use decoded = new MemoryStream()
            gzip.CopyTo(decoded)
            decoded.ToArray()

        /// Reads one complete HTTP request, including Content-Length or chunked binary bodies.
        let readRequest (stream: NetworkStream) =
            use buffer = new MemoryStream()
            let scratch = Array.zeroCreate<byte> 8192
            let mutable headerEnd = -1
            let mutable contentLength = 0
            let mutable chunked = false

            let completeBody () =
                if headerEnd < 0 then
                    false
                elif chunked then
                    let bytes = buffer.ToArray()

                    try
                        decodeChunkedBody bytes[headerEnd..] |> ignore
                        true
                    with
                    | _ -> false
                else
                    buffer.Length >= int64 (headerEnd + contentLength)

            while not (completeBody ()) do
                let read = stream.Read(scratch, 0, scratch.Length)
                if read = 0 then invalidOp "Save switch loopback request ended early."
                buffer.Write(scratch, 0, read)
                let bytes = buffer.ToArray()
                let text = Encoding.ASCII.GetString(bytes)
                let terminator = text.IndexOf("\r\n\r\n", StringComparison.Ordinal)

                if terminator >= 0 then
                    headerEnd <- terminator + 4

                    contentLength <-
                        text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                        |> Array.tryPick (fun header ->
                            if header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) then
                                Some(Int32.Parse(header.Substring(15).Trim()))
                            else
                                None)
                        |> Option.defaultValue 0

                    chunked <- text.Contains("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase)

            let bytes = buffer.ToArray()
            let header = Encoding.ASCII.GetString(bytes, 0, headerEnd)
            let line = header.Substring(0, header.IndexOf("\r\n", StringComparison.Ordinal))

            let body =
                if contentLength = 0 then
                    if chunked then decodeChunkedBody bytes[headerEnd..] else Array.empty<byte>
                else
                    bytes[headerEnd .. (headerEnd + contentLength - 1)]

            line, header, body

        /// Writes one close-delimited loopback response with the Azure headers required by direct blob clients.
        let writeResponse (stream: NetworkStream) status contentType (body: byte array) =
            let headers =
                $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nx-ms-request-id: fixture\r\nx-ms-version: 2023-11-03\r\nx-ms-request-server-encrypted: true\r\nETag: \"fixture\"\r\nLast-Modified: Tue, 01 Jan 2030 00:00:00 GMT\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"

            let headerBytes = Encoding.ASCII.GetBytes(headers)
            stream.Write(headerBytes, 0, headerBytes.Length)
            if body.Length > 0 then stream.Write(body, 0, body.Length)

        /// Produces a typed Grace return envelope for one SDK route response.
        let envelope value =
            serialize (GraceReturnValue.Create value correlationId)
            |> Encoding.UTF8.GetBytes

        let server =
            Task.Run (fun () ->
                try
                    while not cancellation.IsCancellationRequested do
                        use client = listener.AcceptTcpClient()
                        use stream = client.GetStream()
                        let line, header, requestBody = readRequest stream

                        let body =
                            if line.Contains(" /fixture-container/save-object?sig=fake ") then
                                decodeGzipBody requestBody
                            else
                                requestBody

                        requests.Add(line)
                        headers.Add(header)
                        bodies.Add(body)

                        let response =
                            if line.Contains(" /fixture-container/save-object?sig=fake ") then
                                201, "application/octet-stream", Array.empty<byte>
                            elif line.Contains(" /fixture-container/target-object?sig=fake ") then
                                beforeTargetBlobRead ()
                                200, "application/octet-stream", targetBytes
                            elif line.Contains("/branch/Get ") then
                                branchGetCount <- branchGetCount + 1
                                200, "application/json", envelope (if branchGetCount < 3 then currentBranch else selectedBranch)
                            elif line.Contains("/directory/GetDirectoryVersionsRecursive ") then
                                200, "application/json", envelope (Array.empty<DirectoryVersion>)
                            elif line.Contains("/storage/getUploadMetadataForFiles ") then
                                let sha256, blake3 = switchFixtureHashes editedBytes

                                let uploadMetadata: Grace.Shared.Parameters.Storage.UploadMetadata =
                                    {
                                        RelativePath = RelativePath "tracked.bin"
                                        BlobUriWithSasToken = Uri($"{baseUri}/fixture-container/save-object?sig=fake")
                                        Sha256Hash = sha256
                                        Blake3Hash = blake3
                                        ContentReference = FileContentReference.WholeFileContent
                                    }

                                200, "application/json", envelope (List<Grace.Shared.Parameters.Storage.UploadMetadata>([| uploadMetadata |]))
                            elif line.Contains("/directory/SaveDirectoryVersions ") then
                                200, "application/json", envelope "saved"
                            elif line.Contains("/branch/Save ") then
                                200, "application/json", envelope "saved-reference"
                            elif line.Contains("/branch/GetReference ") then
                                200, "application/json", envelope selectedReference
                            elif line.Contains("/branch/GetVersion ") then
                                200, "application/json", envelope [| selectedDirectory.DirectoryVersionId |]
                            elif line.Contains("/directory/GetByDirectoryIds ") then
                                200,
                                "application/json",
                                envelope [| { DirectoryVersionDto.Default with DirectoryVersion = selectedDirectory.ToDirectoryVersion } |]
                            elif line.Contains("/storage/getDownloadUri ") then
                                200, "text/plain", Encoding.UTF8.GetBytes($"{baseUri}/fixture-container/target-object?sig=fake")
                            else
                                404, "text/plain", Encoding.UTF8.GetBytes(line)

                        let status, contentType, payload = response
                        writeResponse stream status contentType payload
                with
                | :? SocketException -> ())

        try
            action baseUri requests headers bodies
        finally
            cancellation.Cancel()
            listener.Stop()
            server.Wait(TimeSpan.FromSeconds(5.0)) |> ignore

    /// Identifies the platform-specific OIDC discovery request that precedes SDK route calls on Linux.
    let private oidcDiscoveryRequest = "GET /authenticate/oidc/config HTTP/1.1"

    /// Names the complete Save, selection, and direct Blob operation sequence required from the public switch command.
    let private saveSwitchOperationSequence =
        [
            "POST /branch/Get HTTP/1.1"
            "POST /branch/Get HTTP/1.1"
            "POST /storage/getUploadMetadataForFiles HTTP/1.1"
            "PUT /fixture-container/save-object?sig=fake HTTP/1.1"
            "POST /directory/GetDirectoryVersionsRecursive HTTP/1.1"
            "POST /directory/SaveDirectoryVersions HTTP/1.1"
            "POST /branch/Save HTTP/1.1"
            "POST /branch/GetReference HTTP/1.1"
            "POST /branch/Get HTTP/1.1"
            "POST /branch/GetVersion HTTP/1.1"
            "POST /directory/GetByDirectoryIds HTTP/1.1"
            "POST /storage/getDownloadUri HTTP/1.1"
            "GET /fixture-container/target-object?sig=fake HTTP/1.1"
        ]

    /// Separates supported OIDC discovery traffic while retaining source indices for operation-specific header and body assertions.
    let private saveSwitchOperationEntries (requests: ResizeArray<string>) =
        requests
        |> Seq.filter (fun request -> request.StartsWith("GET /authenticate/oidc/", StringComparison.Ordinal))
        |> Seq.iter (fun request -> request |> should equal oidcDiscoveryRequest)

        requests
        |> Seq.indexed
        |> Seq.filter (fun (_, request) -> request <> oidcDiscoveryRequest)
        |> Seq.toArray

    /// Runs a Reference-only switch through the built action and checks which route receives control.
    let private runReferenceOnlySwitchRoute route expectedHandler sentinelExitCode =
        withTempBranchSwitchRepo (fun () ->
            let calls = ResizeArray<string>()
            let mutable resolvedParameters: GetBranchParameters option = None

            let operations: Branch.SwitchTestOperations =
                {
                    ResumePending =
                        fun _ ->
                            calls.Add("resume")
                            Task.FromResult None
                    ResolveReferenceRoute =
                        fun parameters ->
                            calls.Add("resolve")
                            resolvedParameters <- Some parameters
                            Task.FromResult(Ok(Some route))
                    ResolveBranchSelector = fun _ -> Task.FromResult(Ok { ReferenceId = targetReferenceId; SelectedBranchId = Some branchId })
                    RunReferenceWithoutSave =
                        fun _ _ _ ->
                            calls.Add("reference-without-save")
                            Task.FromResult sentinelExitCode
                    RunReferenceWithSave =
                        fun _ _ _ ->
                            calls.Add("reference-with-save")
                            Task.FromResult sentinelExitCode
                    RunLegacy =
                        fun _ _ ->
                            calls.Add("legacy")
                            Task.FromResult sentinelExitCode
                }

            let action = Branch.Switch.CreateForTests operations

            let parseResult =
                parse [| "branch"
                         "switch"
                         "--reference-id"
                         (Guid.NewGuid()).ToString() |]

            let actualExitCode =
                action
                    .InvokeAsync(parseResult, System.Threading.CancellationToken.None)
                    .GetAwaiter()
                    .GetResult()

            actualExitCode |> should equal sentinelExitCode

            match resolvedParameters with
            | Some parameters ->
                parameters.RepositoryId
                |> should equal (repositoryId.ToString())

                parameters.BranchId
                |> should equal (branchId.ToString())
            | None -> Assert.Fail("Expected a Reference-only switch to resolve the current Branch.")

            calls
            |> Seq.toList
            |> should equal [ "resume"; "resolve"; expectedHandler ])

    /// Verifies a no-Save current Branch resumes finalization before using the WDU Reference route.
    [<Test>]
    let ``Reference-only switch resumes before routing to WDU when Save is disabled`` () =
        runReferenceOnlySwitchRoute Branch.ReferenceWithoutSave "reference-without-save" 8711

    /// Verifies a Save-enabled current Branch resumes finalization before using the WDU Save admission route.
    [<Test>]
    let ``Reference-only switch resumes before routing to WDU when Save is enabled`` () =
        runReferenceOnlySwitchRoute Branch.ReferenceWithSave "reference-with-save" 8712

    /// Runs one Branch selector through the built action and verifies its immutable Reference-selected handoff.
    let private runBranchSelectorSwitch args route selectedBranchId expectedRequestBranchId expectedBranchName expectedHandler =
        withTempBranchSwitchRepo (fun () ->
            let calls = ResizeArray<string>()
            let exactReferenceId = ReferenceId.NewGuid()

            let operations: Branch.SwitchTestOperations =
                {
                    ResumePending =
                        fun _ ->
                            calls.Add("resume")
                            Task.FromResult None
                    ResolveReferenceRoute =
                        fun _ ->
                            calls.Add("resolve")
                            Task.FromResult(Ok(Some route))
                    ResolveBranchSelector =
                        fun parameters ->
                            calls.Add("branch-selector")

                            parameters.BranchId
                            |> should equal expectedRequestBranchId

                            parameters.BranchName
                            |> should equal expectedBranchName

                            Task.FromResult(Ok { ReferenceId = exactReferenceId; SelectedBranchId = Some selectedBranchId })
                    RunReferenceWithoutSave =
                        fun target _ _ ->
                            calls.Add("reference-without-save")

                            target.ReferenceId
                            |> should equal exactReferenceId

                            Task.FromResult 1025
                    RunReferenceWithSave =
                        fun target _ _ ->
                            calls.Add("reference-with-save")

                            target.ReferenceId
                            |> should equal exactReferenceId

                            Task.FromResult 1025
                    RunLegacy =
                        fun _ _ ->
                            calls.Add("legacy")
                            Task.FromResult 1025
                }

            let action = Branch.Switch.CreateForTests operations

            let exitCode =
                action
                    .InvokeAsync(parse (Array.append [| "branch"; "switch" |] args), CancellationToken.None)
                    .GetAwaiter()
                    .GetResult()

            exitCode |> should equal 1025

            calls
            |> Seq.toList
            |> should
                equal
                [
                    "resume"
                    "branch-selector"
                    "resolve"
                    expectedHandler
                ])

    /// Verifies that a Branch ID selector reaches the no-Save Reference-selected WDU route.
    [<Test; Category("Issue1025")>]
    let ``Branch ID switch routes through no-Save WDU Reference path`` () =
        let requestedBranchId = BranchId.NewGuid()

        runBranchSelectorSwitch
            [|
                "--to-branch-id"
                requestedBranchId.ToString()
            |]
            Branch.ReferenceWithoutSave
            requestedBranchId
            (requestedBranchId.ToString())
            String.Empty
            "reference-without-save"

    /// Verifies that a Branch name selector reaches the Save-enabled Reference-selected WDU route.
    [<Test; Category("Issue1025")>]
    let ``Branch name switch routes through Save-enabled WDU Reference path`` () =
        let selectedBranchId = BranchId.NewGuid()
        let branchName = "selected-by-name"

        runBranchSelectorSwitch [| "--to-branch-name"; branchName |] Branch.ReferenceWithSave selectedBranchId String.Empty branchName "reference-with-save"

    /// Verifies that Branch ID suppresses Branch name when both public selectors are supplied.
    [<Test; Category("Issue1025")>]
    let ``Branch switch gives ID precedence over name`` () =
        let requestedBranchId = BranchId.NewGuid()

        runBranchSelectorSwitch
            [|
                "--to-branch-id"
                requestedBranchId.ToString()
                "--to-branch-name"
                "ignored-name"
            |]
            Branch.ReferenceWithoutSave
            requestedBranchId
            (requestedBranchId.ToString())
            String.Empty
            "reference-without-save"

    /// Creates a valid selected Branch response for exact latest-Reference validation tests.
    let private branchSelectorDto selectedBranchId (selectedBranchName: string) referenceId rootDirectoryId =
        let latestReference =
            { ReferenceDto.Default with
                ReferenceId = referenceId
                OwnerId = ownerId
                OrganizationId = organizationId
                RepositoryId = repositoryId
                BranchId = selectedBranchId
                DirectoryId = rootDirectoryId
            }

        { BranchDto.Default with
            OwnerId = ownerId
            OrganizationId = organizationId
            RepositoryId = repositoryId
            BranchId = selectedBranchId
            BranchName = BranchName selectedBranchName
            LatestReference = latestReference
        }

    /// Verifies that post-resolution Branch-name validation accepts the server's case-insensitive match.
    [<Test; Category("Issue1025")>]
    let ``Branch selector accepts a stored name that differs only by case`` () =
        let selectedBranchId = BranchId.NewGuid()
        let referenceId = ReferenceId.NewGuid()
        let selectedBranch = branchSelectorDto selectedBranchId "Feature" referenceId (DirectoryVersionId.NewGuid())

        match Branch.tryCreateBranchSelectorReferenceTarget ownerId organizationId repositoryId BranchId.Empty "feature" selectedBranch with
        | Ok target ->
            target.ReferenceId |> should equal referenceId

            target.SelectedBranchId
            |> should equal (Some selectedBranchId)
        | Error error -> Assert.Fail($"Expected a case-insensitive Branch-name match, but received: {error}")

    /// Verifies that post-resolution Branch-name validation still rejects a genuinely different name.
    [<Test; Category("Issue1025")>]
    let ``Branch selector rejects a genuinely different stored name`` () =
        let selectedBranch = branchSelectorDto (BranchId.NewGuid()) "Feature" (ReferenceId.NewGuid()) (DirectoryVersionId.NewGuid())

        match Branch.tryCreateBranchSelectorReferenceTarget ownerId organizationId repositoryId BranchId.Empty "different" selectedBranch with
        | Error error ->
            error
            |> should equal "The selected Branch response does not match the requested Branch name."
        | Ok _ -> Assert.Fail("Expected a genuinely different Branch name to be rejected.")

    /// Verifies that an absent latest Reference is rejected before target retrieval can begin.
    [<Test; Category("Issue1025")>]
    let ``Branch selector rejects missing latest Reference`` () =
        let selectedBranchId = BranchId.NewGuid()

        match
            Branch.tryCreateBranchSelectorReferenceTarget
                ownerId
                organizationId
                repositoryId
                selectedBranchId
                String.Empty
                { BranchDto.Default with OwnerId = ownerId; OrganizationId = organizationId; RepositoryId = repositoryId; BranchId = selectedBranchId }
            with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Expected an empty latest Reference to be rejected.")

    /// Verifies that latest Reference ownership cannot differ from its selected Branch.
    [<Test; Category("Issue1025")>]
    let ``Branch selector rejects mismatched latest Reference`` () =
        let selectedBranchId = BranchId.NewGuid()
        let selectedBranch = branchSelectorDto selectedBranchId "selected" (ReferenceId.NewGuid()) (DirectoryVersionId.NewGuid())

        match
            Branch.tryCreateBranchSelectorReferenceTarget
                ownerId
                organizationId
                repositoryId
                selectedBranchId
                String.Empty
                { selectedBranch with LatestReference = { selectedBranch.LatestReference with RepositoryId = RepositoryId.NewGuid() } }
            with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Expected a mismatched latest Reference to be rejected.")

    /// Verifies that empty or wrong-root target graphs are rejected before content preparation.
    [<Test; Category("Issue1025")>]
    let ``Exact Reference rejects incomplete target graph root`` () =
        let expectedRoot = DirectoryVersionId.NewGuid()
        let reference = { ReferenceDto.Default with DirectoryId = expectedRoot }

        match Branch.tryValidateReferenceTargetGraphRoot reference Array.empty with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Expected an empty graph to be rejected.")

        match Branch.tryValidateReferenceTargetGraphRoot reference [| DirectoryVersionId.NewGuid() |] with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Expected a mismatched graph root to be rejected.")

    /// Verifies that selector resolution failure cannot reach either WDU route or legacy mutation.
    [<Test; Category("Issue1025")>]
    let ``Branch selector resolution failure has no mutation or publication`` () =
        withTempBranchSwitchRepo (fun () ->
            let calls = ResizeArray<string>()

            let operations: Branch.SwitchTestOperations =
                {
                    ResumePending =
                        fun _ ->
                            calls.Add("resume")
                            Task.FromResult None
                    ResolveReferenceRoute =
                        fun _ ->
                            calls.Add("resolve")
                            Task.FromResult(Ok(Some Branch.ReferenceWithoutSave))
                    ResolveBranchSelector =
                        fun _ ->
                            calls.Add("branch-selector")
                            Task.FromResult(Error(GraceError.Create "invalid latest Reference" correlationId))
                    RunReferenceWithoutSave =
                        fun _ _ _ ->
                            calls.Add("reference-without-save")
                            Task.FromResult 0
                    RunReferenceWithSave =
                        fun _ _ _ ->
                            calls.Add("reference-with-save")
                            Task.FromResult 0
                    RunLegacy =
                        fun _ _ ->
                            calls.Add("legacy")
                            Task.FromResult 0
                }

            let exitCode =
                Branch
                    .Switch
                    .CreateForTests(operations)
                    .InvokeAsync(
                        parse [| "branch"
                                 "switch"
                                 "--to-branch-name"
                                 "invalid" |],
                        CancellationToken.None
                    )
                    .GetAwaiter()
                    .GetResult()

            exitCode |> should equal -1

            calls
            |> Seq.toList
            |> should equal [ "resume"; "branch-selector" ])

    /// Verifies that an exact replay completes before selectors, mutation, or publication are evaluated.
    [<Test; Category("Issue1025")>]
    let ``Branch selector exact replay bypasses all routing`` () =
        withTempBranchSwitchRepo (fun () ->
            let target =
                WorkingDirectoryUpdateContracts.Target.create
                    repositoryId
                    branchId
                    (DirectoryVersionId.NewGuid())
                    (Sha256Hash(String.replicate 64 "a"))
                    (Blake3Hash(String.replicate 64 "b"))
                |> Result.defaultWith failwith

            let operation =
                WorkingDirectoryUpdateContracts.Operation.branchSwitchWithSelection
                    branchId
                    (WorkingDirectoryUpdateContracts.BranchSelection.Reference targetReferenceId)
                    target
                |> Result.defaultWith failwith

            let receipt =
                WorkingDirectoryUpdateContracts.Receipt.create target operation false
                |> Result.defaultWith failwith

            let calls = ResizeArray<string>()

            let operations: Branch.SwitchTestOperations =
                {
                    ResumePending =
                        fun _ ->
                            calls.Add("resume")
                            Task.FromResult(Some(WorkingDirectoryUpdateContracts.Outcome.Unchanged receipt))
                    ResolveReferenceRoute =
                        fun _ ->
                            calls.Add("resolve")
                            Task.FromResult(Ok None)
                    ResolveBranchSelector =
                        fun _ ->
                            calls.Add("branch-selector")
                            Task.FromResult(Error(GraceError.Create "unexpected" correlationId))
                    RunReferenceWithoutSave =
                        fun _ _ _ ->
                            calls.Add("reference-without-save")
                            Task.FromResult -1
                    RunReferenceWithSave =
                        fun _ _ _ ->
                            calls.Add("reference-with-save")
                            Task.FromResult -1
                    RunLegacy =
                        fun _ _ ->
                            calls.Add("legacy")
                            Task.FromResult -1
                }

            let exitCode =
                Branch
                    .Switch
                    .CreateForTests(operations)
                    .InvokeAsync(
                        parse [| "branch"
                                 "switch"
                                 "--to-branch-id"
                                 (BranchId.NewGuid()).ToString() |],
                        CancellationToken.None
                    )
                    .GetAwaiter()
                    .GetResult()

            exitCode |> should equal 0
            calls |> Seq.toList |> should equal [ "resume" ])

    /// Proves the public Save-enabled Reference command persists its edit, prepares the selected graph, and completes through WDU.
    [<Test>]
    let ``Save Reference switch command persists rereads and applies selected target`` () =
        withTempBranchSwitchRepo (fun () ->
            let configuration = Current()
            let originalServerUri = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.GraceServerUri)
            let initialBytes = [| 1uy; 2uy; 3uy |]
            let editedBytes = [| 4uy; 5uy; 6uy |]
            let targetBytes = [| 7uy; 8uy; 9uy |]
            let initialStatus, _ = switchFixtureStatus configuration (DirectoryVersionId.NewGuid()) "tracked.bin" initialBytes
            let selectedStatus, selectedRoot = switchFixtureStatus configuration (DirectoryVersionId.NewGuid()) "selected.bin" targetBytes

            let selectedBranch =
                { BranchDto.Default with BranchId = BranchId.NewGuid(); BranchName = BranchName "selected"; RepositoryId = configuration.RepositoryId }

            let selectedReference =
                { ReferenceDto.Default with ReferenceId = targetReferenceId; BranchId = selectedBranch.BranchId; DirectoryId = selectedRoot.DirectoryVersionId }

            try
                File.WriteAllBytes(Path.Combine(configuration.RootDirectory, "tracked.bin"), editedBytes)

                LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile initialStatus
                |> fun task -> task.GetAwaiter().GetResult() |> ignore

                let watchStatus =
                    {
                        UpdatedAt = getCurrentInstant ()
                        IsStartupClaim = false
                        RepositoryId = configuration.RepositoryId
                        RepositoryName = RepositoryName configuration.RepositoryName
                        BranchId = configuration.BranchId
                        BranchName = BranchName configuration.BranchName
                        RootDirectory = configuration.RootDirectory
                        HasPendingWatchWork = false
                        IsWorkingTreeClean = true
                        RootDirectoryId = initialStatus.RootDirectoryId
                        RootDirectorySha256Hash = initialStatus.RootDirectorySha256Hash
                        RootDirectoryBlake3Hash = initialStatus.RootDirectoryBlake3Hash
                        LastFileUploadInstant = Instant.MinValue
                        LastDirectoryVersionInstant = Instant.MinValue
                        DirectoryIds = HashSet<DirectoryVersionId>(initialStatus.Index.Keys)
                    }

                let watchStatusFile = Services.IpcFileName()

                Directory.CreateDirectory(Path.GetDirectoryName(watchStatusFile))
                |> ignore

                File.WriteAllText(watchStatusFile, serialize watchStatus)
                configuration.ObjectStorageProvider <- ObjectStorageProvider.AzureBlobStorage

                let editedSha256, editedBlake3 = switchFixtureHashes editedBytes

                let editedFile =
                    LocalFileVersion.CreateWithHashes
                        (RelativePath "tracked.bin")
                        editedSha256
                        editedBlake3
                        true
                        (int64 editedBytes.Length)
                        (getCurrentInstant ())
                        true
                        DateTime.UtcNow

                let editedCachePath = Services.getLocalObjectCachePathForFileVersion editedFile.ToFileVersion

                Directory.CreateDirectory(Path.GetDirectoryName(editedCachePath))
                |> ignore

                File.WriteAllBytes(editedCachePath, editedBytes)
                File.Exists(editedCachePath) |> should equal true

                withSaveSwitchLoopback
                    { BranchDto.Default with
                        BranchId = configuration.BranchId
                        BranchName = BranchName configuration.BranchName
                        RepositoryId = configuration.RepositoryId
                        SaveEnabled = true
                    }
                    selectedBranch
                    selectedReference
                    selectedRoot
                    editedBytes
                    targetBytes
                    (fun () -> ())
                    (fun serverUri requests headers bodies ->
                        configuration.ServerUri <- serverUri
                        Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.GraceServerUri, serverUri)

                        let action = new Branch.Switch()

                        let exitCode, output =
                            captureOutput (fun () ->
                                action
                                    .InvokeAsync(
                                        parse [| "branch"
                                                 "switch"
                                                 "--reference-id"
                                                 targetReferenceId.ToString() |],
                                        CancellationToken.None
                                    )
                                    .GetAwaiter()
                                    .GetResult())

                        if exitCode <> 0 then
                            Assert.Fail($"{output}{Environment.NewLine}{String.Join(Environment.NewLine, requests)}")

                        output |> should contain "Uploading 1 file(s)"

                        File.ReadAllBytes(Path.Combine(configuration.RootDirectory, "selected.bin"))
                        |> should equal targetBytes

                        File.Exists(Path.Combine(configuration.RootDirectory, "tracked.bin"))
                        |> should equal false

                        Current().BranchId
                        |> should equal selectedBranch.BranchId

                        let committedStatus =
                            LocalStateDb.readStatusSnapshot configuration.GraceStatusFile
                            |> fun task -> task.GetAwaiter().GetResult()

                        committedStatus.RootDirectoryId
                        |> should equal selectedRoot.DirectoryVersionId

                        committedStatus.Index.Keys
                        |> Set.ofSeq
                        |> should equal (set [ selectedRoot.DirectoryVersionId ])

                        LocalStateDb.readLocalStatusRevision configuration.GraceStatusFile
                        |> fun task -> task.GetAwaiter().GetResult()
                        |> should be (greaterThan 0L)

                        let operationEntries = saveSwitchOperationEntries requests

                        let branchGetIndex =
                            operationEntries
                            |> Array.find (fun (_, request) -> request = "POST /branch/Get HTTP/1.1")
                            |> fst

                        let uploadIndex =
                            operationEntries
                            |> Array.find (fun (_, request) -> request = "PUT /fixture-container/save-object?sig=fake HTTP/1.1")
                            |> fst

                        let saveDirectoryVersionsIndex =
                            operationEntries
                            |> Array.find (fun (_, request) -> request = "POST /directory/SaveDirectoryVersions HTTP/1.1")
                            |> fst

                        headers[branchGetIndex]
                        |> should contain "X-Correlation-Id: branch-annotate-tests"

                        headers[branchGetIndex]
                        |> should contain "X-Api-Version: 2023-10-01"

                        headers[uploadIndex]
                        |> should contain $"x-ms-meta-Sha256Hash: {editedSha256}"

                        Encoding.UTF8.GetString(bodies[saveDirectoryVersionsIndex])
                        |> should contain $"{editedSha256}"

                        bodies[uploadIndex] |> should equal editedBytes

                        operationEntries
                        |> Array.map snd
                        |> Array.toList
                        |> should equal saveSwitchOperationSequence)
            finally
                Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.GraceServerUri, originalServerUri))

    /// Proves a post-Save local-status drift aborts the public Reference command before it mutates the selected target.
    [<Test>]
    let ``Save Reference switch command rejects drift before target mutation`` () =
        withTempBranchSwitchRepo (fun () ->
            let configuration = Current()
            let originalServerUri = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.GraceServerUri)
            let initialBytes = [| 1uy; 2uy; 3uy |]
            let editedBytes = [| 4uy; 5uy; 6uy |]
            let targetBytes = [| 7uy; 8uy; 9uy |]
            let initialStatus, _ = switchFixtureStatus configuration (DirectoryVersionId.NewGuid()) "tracked.bin" initialBytes
            let driftStatus, _ = switchFixtureStatus configuration (DirectoryVersionId.NewGuid()) "drift.bin" [| 10uy; 11uy; 12uy |]
            let _, selectedRoot = switchFixtureStatus configuration (DirectoryVersionId.NewGuid()) "selected.bin" targetBytes

            let selectedBranch =
                { BranchDto.Default with BranchId = BranchId.NewGuid(); BranchName = BranchName "selected"; RepositoryId = configuration.RepositoryId }

            let selectedReference =
                { ReferenceDto.Default with ReferenceId = targetReferenceId; BranchId = selectedBranch.BranchId; DirectoryId = selectedRoot.DirectoryVersionId }

            try
                File.WriteAllBytes(Path.Combine(configuration.RootDirectory, "tracked.bin"), editedBytes)

                LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile initialStatus
                |> fun task -> task.GetAwaiter().GetResult() |> ignore

                let watchStatus =
                    {
                        UpdatedAt = getCurrentInstant ()
                        IsStartupClaim = false
                        RepositoryId = configuration.RepositoryId
                        RepositoryName = RepositoryName configuration.RepositoryName
                        BranchId = configuration.BranchId
                        BranchName = BranchName configuration.BranchName
                        RootDirectory = configuration.RootDirectory
                        HasPendingWatchWork = false
                        IsWorkingTreeClean = true
                        RootDirectoryId = initialStatus.RootDirectoryId
                        RootDirectorySha256Hash = initialStatus.RootDirectorySha256Hash
                        RootDirectoryBlake3Hash = initialStatus.RootDirectoryBlake3Hash
                        LastFileUploadInstant = Instant.MinValue
                        LastDirectoryVersionInstant = Instant.MinValue
                        DirectoryIds = HashSet<DirectoryVersionId>(initialStatus.Index.Keys)
                    }

                let watchStatusFile = Services.IpcFileName()

                Directory.CreateDirectory(Path.GetDirectoryName(watchStatusFile))
                |> ignore

                File.WriteAllText(watchStatusFile, serialize watchStatus)
                configuration.ObjectStorageProvider <- ObjectStorageProvider.AzureBlobStorage

                let editedSha256, editedBlake3 = switchFixtureHashes editedBytes

                let editedFile =
                    LocalFileVersion.CreateWithHashes
                        (RelativePath "tracked.bin")
                        editedSha256
                        editedBlake3
                        true
                        (int64 editedBytes.Length)
                        (getCurrentInstant ())
                        true
                        DateTime.UtcNow

                let editedCachePath = Services.getLocalObjectCachePathForFileVersion editedFile.ToFileVersion

                Directory.CreateDirectory(Path.GetDirectoryName(editedCachePath))
                |> ignore

                File.WriteAllBytes(editedCachePath, editedBytes)

                withSaveSwitchLoopback
                    { BranchDto.Default with
                        BranchId = configuration.BranchId
                        BranchName = BranchName configuration.BranchName
                        RepositoryId = configuration.RepositoryId
                        SaveEnabled = true
                    }
                    selectedBranch
                    selectedReference
                    selectedRoot
                    editedBytes
                    targetBytes
                    (fun () ->
                        LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile driftStatus
                        |> fun task -> task.GetAwaiter().GetResult() |> ignore)
                    (fun serverUri requests _ bodies ->
                        configuration.ServerUri <- serverUri
                        Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.GraceServerUri, serverUri)

                        let exitCode, output =
                            captureOutput (fun () ->
                                (new Branch.Switch())
                                    .InvokeAsync(
                                        parse [| "branch"
                                                 "switch"
                                                 "--reference-id"
                                                 targetReferenceId.ToString() |],
                                        CancellationToken.None
                                    )
                                    .GetAwaiter()
                                    .GetResult())

                        exitCode |> should not' (equal 0)

                        output
                        |> should contain "Local status changed while the selected Reference was being prepared."

                        File.Exists(Path.Combine(configuration.RootDirectory, "selected.bin"))
                        |> should equal false

                        File.ReadAllBytes(Path.Combine(configuration.RootDirectory, "tracked.bin"))
                        |> should equal editedBytes

                        Current().BranchId
                        |> should equal configuration.BranchId

                        LocalStateDb.readPendingWorkingDirectoryUpdateFinalization configuration.GraceStatusFile
                        |> fun task -> task.GetAwaiter().GetResult()
                        |> should equal None

                        let operationEntries = saveSwitchOperationEntries requests

                        let uploadIndex =
                            operationEntries
                            |> Array.find (fun (_, request) -> request = "PUT /fixture-container/save-object?sig=fake HTTP/1.1")
                            |> fst

                        operationEntries
                        |> Array.map snd
                        |> Array.toList
                        |> should equal saveSwitchOperationSequence

                        bodies[uploadIndex] |> should equal editedBytes)
            finally
                Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.GraceServerUri, originalServerUri))

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
            let mutable inspectionCount = 0
            let mutable markerSeenByPostMarkerInspection = false
            let mutable operationRan = false
            let mutable markerSeenByOperation = false
            let mutationFile = Path.Combine(Current().RootDirectory, "grace-owned-mutation.txt")

            let operations: Branch.BranchSwitchWatchCleanPreflightOperations =
                {
                    UpdateMarkerExists = fun () -> File.Exists(updateMarkerFile)
                    InspectWatchStatus =
                        fun () ->
                            inspected <- true
                            inspectionCount <- inspectionCount + 1

                            if inspectionCount = 1 then
                                File.Exists(updateMarkerFile)
                                |> should equal false
                            else
                                markerSeenByPostMarkerInspection <- File.Exists(updateMarkerFile)

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
            inspectionCount |> should equal 2

            markerSeenByPostMarkerInspection
            |> should equal true

            operationRan |> should equal true
            markerSeenByOperation |> should equal true

            File.Exists(mutationFile) |> should equal true

            File.Exists(updateMarkerFile)
            |> should equal false)

    /// Verifies that durable rows appended after clean preflight still block mutation once the update marker exists.
    [<Test>]
    let ``branch switch working tree update rechecks durable rows after marker creation`` () =
        withTempBranchSwitchRepo (fun () ->
            let updateMarkerFile = Services.updateInProgressFileName ()
            let markerText = $"`grace switch` is in progress. Lease: {Guid.NewGuid():N}"
            let mutationFile = Path.Combine(Current().RootDirectory, "must-not-be-written-after-marker.txt")
            let mutable inspected = false
            let mutable inspectionCount = 0
            let mutable journalReadCount = 0
            let mutable markerSeenByPostMarkerJournalRead = false
            let mutable markerSeenByPostMarkerInspection = false
            let mutable operationRan = false

            let operations: Branch.BranchSwitchWatchCleanPreflightOperations =
                {
                    UpdateMarkerExists = fun () -> File.Exists(updateMarkerFile)
                    InspectWatchStatus =
                        fun () ->
                            inspected <- true
                            inspectionCount <- inspectionCount + 1

                            if inspectionCount = 1 then
                                File.Exists(updateMarkerFile)
                                |> should equal false
                            else
                                markerSeenByPostMarkerInspection <- File.Exists(updateMarkerFile)

                            Task.FromResult(branchSwitchWatchInspection (Some GraceWatchRuntimeMode.HealthyIncremental) (branchSwitchWatchStatus ()))
                    ReadPendingJournalSummary =
                        fun () ->
                            journalReadCount <- journalReadCount + 1

                            if journalReadCount = 1 then
                                Task.FromResult(cleanPendingJournalSummary ())
                            else
                                markerSeenByPostMarkerJournalRead <- File.Exists(updateMarkerFile)

                                Task.FromResult(
                                    { DbPath = Current().GraceStatusFile; AppliedThroughSequence = 7L; PendingRowCount = 1L }: LocalStateDb.WatchJournalPendingWorkSummary
                                )
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

                error.Error
                |> should contain "after update marker creation"

                error.Error
                |> should contain "unresolved durable journal rows"
            | Ok _ -> Assert.Fail("Expected post-marker durable journal evidence to refuse branch switch.")

            inspected |> should equal true
            inspectionCount |> should equal 2

            markerSeenByPostMarkerInspection
            |> should equal true

            journalReadCount |> should equal 2

            markerSeenByPostMarkerJournalRead
            |> should equal true

            operationRan |> should equal false

            File.Exists(mutationFile) |> should equal false

            File.Exists(updateMarkerFile)
            |> should equal false)

    /// Verifies a local Watch queue or dirty IPC state that appears after the first preflight blocks mutation under this invocation's marker.
    [<Test>]
    let ``branch switch working tree update rechecks full Watch status after owned marker creation`` () =
        withTempBranchSwitchRepo (fun () ->
            let updateMarkerFile = Services.updateInProgressFileName ()
            let markerText = $"`grace switch` is in progress. Lease: {Guid.NewGuid():N}"
            let mutationFile = Path.Combine(Current().RootDirectory, "must-not-be-written-after-dirty-ipc.txt")
            let mutable inspectionCount = 0
            let mutable markerSeenByPostMarkerInspection = false
            let mutable operationRan = false
            let dirtyStatus = { branchSwitchWatchStatus () with HasPendingWatchWork = true; IsWorkingTreeClean = false }

            let operations: Branch.BranchSwitchWatchCleanPreflightOperations =
                {
                    UpdateMarkerExists = fun () -> File.Exists(updateMarkerFile)
                    InspectWatchStatus =
                        fun () ->
                            inspectionCount <- inspectionCount + 1

                            if inspectionCount = 2 then
                                markerSeenByPostMarkerInspection <- File.Exists(updateMarkerFile)
                                Task.FromResult(branchSwitchWatchInspection (Some GraceWatchRuntimeMode.HealthyIncremental) dirtyStatus)
                            else
                                Task.FromResult(branchSwitchWatchInspection (Some GraceWatchRuntimeMode.HealthyIncremental) (branchSwitchWatchStatus ()))
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
            | Error error -> error.Error |> should contain "dirty working tree"
            | Ok _ -> Assert.Fail("Expected post-marker dirty Watch status to refuse branch switch.")

            inspectionCount |> should equal 2

            markerSeenByPostMarkerInspection
            |> should equal true

            operationRan |> should equal false
            File.Exists(mutationFile) |> should equal false

            File.Exists(updateMarkerFile)
            |> should equal false)

    /// Verifies a marker replaced by another Grace command after creation is rejected before the second Watch-clean inspection.
    [<Test>]
    let ``branch switch working tree update refuses foreign marker after owned marker creation`` () =
        withTempBranchSwitchRepo (fun () ->
            let updateMarkerFile = Services.updateInProgressFileName ()
            let markerText = $"`grace switch` is in progress. Lease: {Guid.NewGuid():N}"
            let mutable markerChecks = 0
            let mutable inspected = false
            let mutable operationRan = false

            let operations: Branch.BranchSwitchWatchCleanPreflightOperations =
                {
                    UpdateMarkerExists =
                        fun () ->
                            markerChecks <- markerChecks + 1

                            if markerChecks = 2 then
                                File.WriteAllText(updateMarkerFile, "foreign Grace update marker")

                            File.Exists(updateMarkerFile)
                    InspectWatchStatus =
                        fun () ->
                            inspected <- true
                            Task.FromResult(branchSwitchWatchInspection (Some GraceWatchRuntimeMode.HealthyIncremental) (branchSwitchWatchStatus ()))
                    ReadPendingJournalSummary = fun () -> Task.FromResult(cleanPendingJournalSummary ())
                }

            let result =
                (Branch.runBranchSwitchWorkingTreeUpdateWithMarker operations correlationId updateMarkerFile markerText (fun () -> task { operationRan <- true }))
                    .GetAwaiter()
                    .GetResult()

            match result with
            | Error error ->
                error.Error
                |> should contain "update marker already exists"
            | Ok _ -> Assert.Fail("Expected replaced foreign marker to refuse branch switch.")

            markerChecks |> should equal 2
            inspected |> should equal true
            operationRan |> should equal false

            File.ReadAllText(updateMarkerFile)
            |> should equal "foreign Grace update marker"

            File.Delete(updateMarkerFile))

    /// Verifies that the switch workflow lease serializes precomputation without creating the Watch suppression marker.
    [<Test>]
    let ``branch switch workflow lease serializes before precompute without update marker`` () =
        withTempBranchSwitchRepo (fun () ->
            let updateMarkerFile = Services.updateInProgressFileName ()
            let switchLeaseFile = Branch.branchSwitchWorkflowLeaseFileName updateMarkerFile
            let switchLeaseText = $"`grace switch` workflow lease. Lease: {Guid.NewGuid():N}"
            let mutable inspectionCount = 0
            let mutable workflowRan = false
            let mutable leaseSeenByWorkflow = false
            let mutable materializationLeaseHeldByWorkflow = false
            let mutable markerSeenByWorkflow = false

            let operations: Branch.BranchSwitchWatchCleanPreflightOperations =
                {
                    UpdateMarkerExists = fun () -> File.Exists(updateMarkerFile)
                    InspectWatchStatus =
                        fun () ->
                            inspectionCount <- inspectionCount + 1

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
                        let materializationLeaseFile = WorkingDirectoryMaterialization.leaseFileName ()

                        materializationLeaseHeldByWorkflow <-
                            try
                                use _probe = new FileStream(materializationLeaseFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
                                false
                            with
                            | :? IOException -> true

                        markerSeenByWorkflow <- File.Exists(updateMarkerFile)
                        return "computed"
                    }))
                    .GetAwaiter()
                    .GetResult()

            match result with
            | Ok value -> value |> should equal "computed"
            | Error error -> Assert.Fail($"Expected branch switch lease to allow workflow, got: {error.Error}")

            inspectionCount |> should equal 2
            workflowRan |> should equal true
            leaseSeenByWorkflow |> should equal true

            materializationLeaseHeldByWorkflow
            |> should equal true

            markerSeenByWorkflow |> should equal false

            File.Exists(switchLeaseFile) |> should equal false

            File.Exists(updateMarkerFile)
            |> should equal false)

    /// Verifies that stale Watch-clean evidence cannot start switch work after the materialization lease wait.
    [<Test>]
    let ``branch switch workflow lease rechecks Watch preflight after materialization lease wait`` () =
        withTempBranchSwitchRepo (fun () ->
            let updateMarkerFile = Services.updateInProgressFileName ()
            let switchLeaseFile = Branch.branchSwitchWorkflowLeaseFileName updateMarkerFile
            let switchLeaseText = $"`grace switch` workflow lease. Lease: {Guid.NewGuid():N}"
            let dirtyStatus = { branchSwitchWatchStatus () with HasPendingWatchWork = true; IsWorkingTreeClean = false }
            let materializationLeaseFile = WorkingDirectoryMaterialization.leaseFileName ()
            let mutable inspectionCount = 0
            let mutable postLeasePreflightHeldMaterializationLease = false
            let mutable workflowRan = false

            Directory.CreateDirectory(Path.GetDirectoryName(materializationLeaseFile))
            |> ignore

            use blockingLease = new FileStream(materializationLeaseFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)

            let operations: Branch.BranchSwitchWatchCleanPreflightOperations =
                {
                    UpdateMarkerExists = fun () -> File.Exists(updateMarkerFile)
                    InspectWatchStatus =
                        fun () ->
                            inspectionCount <- inspectionCount + 1

                            if inspectionCount = 2 then
                                postLeasePreflightHeldMaterializationLease <-
                                    try
                                        use _probe = new FileStream(materializationLeaseFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
                                        false
                                    with
                                    | :? IOException -> true

                                Task.FromResult(branchSwitchWatchInspection (Some GraceWatchRuntimeMode.HealthyIncremental) dirtyStatus)
                            else
                                Task.FromResult(branchSwitchWatchInspection (Some GraceWatchRuntimeMode.HealthyIncremental) (branchSwitchWatchStatus ()))
                    ReadPendingJournalSummary = fun () -> Task.FromResult(cleanPendingJournalSummary ())
                }

            let switchTask =
                Task.Run (fun () ->
                    (Branch.runBranchSwitchWorkflowWithLease operations correlationId switchLeaseFile switchLeaseText (fun () ->
                        task {
                            workflowRan <- true
                            return "must-not-run"
                        }))
                        .GetAwaiter()
                        .GetResult())

            Task.Delay(150).Wait()

            switchTask.IsCompleted |> should equal false

            blockingLease.Dispose()

            Task.WaitAll([| switchTask :> Task |], 5000)
            |> should equal true

            match switchTask.Result with
            | Error error ->
                error.Error
                |> should contain "Branch switch refused before mutation"

                error.Error |> should contain "dirty working tree"
            | Ok _ -> Assert.Fail("Expected post-lease dirty Watch preflight to refuse branch switch.")

            inspectionCount |> should equal 2

            postLeasePreflightHeldMaterializationLease
            |> should equal true

            workflowRan |> should equal false

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

    /// Verifies that a Branch switch configuration write failure leaves the persisted branch identity current in this process.
    [<Test>]
    let ``branch switch configuration update failure preserves current cache`` () =
        if Environment.OSVersion.Platform
           <> PlatformID.Win32NT then
            Assert.Ignore("This focused file-sharing failure injection is specific to the supported Windows filesystem.")

        withTempBranchSwitchRepo (fun () ->
            let persistedConfiguration = Current()

            updateConfiguration persistedConfiguration
            resetConfiguration ()

            let targetBranchId = Guid.NewGuid()
            let targetBranchName = "branch-switch-proposed"
            let configurationPath = Path.Combine(Current().ConfigurationDirectory, Constants.GraceConfigFileName)
            use readLock = new FileStream(configurationPath, FileMode.Open, FileAccess.Read, FileShare.Read)

            let operation =
                Func<Task> (fun () ->
                    Branch.applyBranchSwitchLocalState
                        (fun () -> Task.CompletedTask)
                        (fun () ->
                            let configuration = Current()
                            configuration.BranchId <- targetBranchId
                            configuration.BranchName <- targetBranchName
                            updateConfiguration configuration)
                        (fun () -> Task.CompletedTask)
                    :> Task)

            Assert.ThrowsAsync<IOException>(operation)
            |> ignore

            resetConfiguration ()

            let currentConfiguration = Current()

            currentConfiguration.BranchId
            |> should equal branchId

            currentConfiguration.BranchName
            |> should equal "branch-switch-current")

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

    /// Verifies branch switch treats a missing local-state DB as uninspectable durable evidence, not zero pending rows.
    [<Test>]
    let ``branch switch Watch preflight refuses missing local-state database after clean IPC`` () =
        withTempBranchSwitchRepo (fun () ->
            let mutable inspected = false
            let mutable inspectedJournal = false
            let localStatePath = Current().GraceStatusFile

            if File.Exists(localStatePath) then File.Delete(localStatePath)

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
                            LocalStateDb.readWatchJournalPendingWorkSummaryForTransitionCheck localStatePath
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
                |> should contain "durable journal pending-work evidence could not be inspected"

                error.Error
                |> should contain "local-state database is missing"
            | Ok _ -> Assert.Fail("Expected missing durable journal evidence to refuse branch switch.")

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
