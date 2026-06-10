namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.Command
open Grace.CLI.Services
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Utilities
open Grace.Types.Common
open NodaTime
open NUnit.Framework
open Spectre.Console
open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Threading.Tasks

[<NonParallelizable>]
module WatchTests =
    let private setAnsiConsoleOutput (writer: TextWriter) =
        let settings = AnsiConsoleSettings()
        settings.Out <- AnsiConsoleOutput(writer)
        AnsiConsole.Console <- AnsiConsole.Create(settings)

    let private runWithCapturedOutput (args: string array) =
        use writer = new StringWriter()
        let originalOut = Console.Out

        try
            Console.SetOut(writer)
            setAnsiConsoleOutput writer
            let parseResult = GraceCommand.rootCommand.Parse(args)
            let exitCode = parseResult.InvokeAsync().Result
            exitCode, writer.ToString()
        finally
            Console.SetOut(originalOut)
            setAnsiConsoleOutput originalOut

    let private runWithCapturedStdoutAndStderr (args: string array) =
        use standardOutWriter = new StringWriter()
        use standardErrorWriter = new StringWriter()
        let originalOut = Console.Out
        let originalError = Console.Error

        try
            Console.SetOut(standardOutWriter)
            Console.SetError(standardErrorWriter)
            setAnsiConsoleOutput standardOutWriter
            let exitCode = GraceCommand.main args
            exitCode, standardOutWriter.ToString(), standardErrorWriter.ToString()
        finally
            Console.SetOut(originalOut)
            Console.SetError(originalError)
            setAnsiConsoleOutput originalOut

    let private runGraceProcessWithCapturedStdoutAndStderr workingDirectory (args: string array) =
        let cliDllPath = Path.Combine(AppContext.BaseDirectory, "grace.dll")

        File.Exists(cliDllPath) |> should equal true

        let startInfo = ProcessStartInfo()
        startInfo.FileName <- "dotnet"
        startInfo.WorkingDirectory <- workingDirectory
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.UseShellExecute <- false
        startInfo.CreateNoWindow <- true
        startInfo.ArgumentList.Add(cliDllPath)

        for arg in args do
            startInfo.ArgumentList.Add(arg)

        use proc = new Process()
        proc.StartInfo <- startInfo

        if not (proc.Start()) then failwith "Failed to start grace process."

        let standardOut = proc.StandardOutput.ReadToEnd()
        let standardError = proc.StandardError.ReadToEnd()

        if not (proc.WaitForExit(30000)) then
            try
                proc.Kill(true)
            with
            | _ -> ()

            Assert.Fail("Timed out waiting for grace process to exit.")

        proc.ExitCode, standardOut, standardError

    let private withEnv (name: string) (value: string option) (action: unit -> unit) =
        let original = Environment.GetEnvironmentVariable(name)

        match value with
        | Some v -> Environment.SetEnvironmentVariable(name, v)
        | None -> Environment.SetEnvironmentVariable(name, null)

        try
            action ()
        finally
            Environment.SetEnvironmentVariable(name, original)

    let private withClearedEnvVars (names: string list) (action: unit -> unit) =
        let rec run remaining =
            match remaining with
            | [] -> action ()
            | head :: tail -> withEnv head None (fun () -> run tail)

        run names

    let private clearWatchAuthEnv (action: unit -> unit) =
        withClearedEnvVars
            [
                Constants.EnvironmentVariables.GraceToken
                Constants.EnvironmentVariables.GraceTokenFile
                Constants.EnvironmentVariables.GraceAuthOidcAuthority
                Constants.EnvironmentVariables.GraceAuthOidcAudience
                Constants.EnvironmentVariables.GraceAuthOidcCliClientId
                Constants.EnvironmentVariables.GraceAuthOidcCliRedirectPort
                Constants.EnvironmentVariables.GraceAuthOidcCliScopes
                Constants.EnvironmentVariables.GraceAuthOidcM2mClientId
                Constants.EnvironmentVariables.GraceAuthOidcM2mClientSecret
                Constants.EnvironmentVariables.GraceAuthOidcM2mScopes
                Constants.EnvironmentVariables.GraceServerUri
            ]
            action

    let private parseJsonOutput (output: string) =
        output.StartsWith("{", StringComparison.Ordinal)
        |> should equal true

        JsonDocument.Parse(output)

    let private writeLiveWatchStatusFile () =
        let rootDirectoryId = Guid.NewGuid()

        let status: Services.GraceWatchStatus =
            {
                UpdatedAt = getCurrentInstant ()
                IsStartupClaim = false
                RootDirectoryId = rootDirectoryId
                RootDirectorySha256Hash = Sha256Hash "live-watch-root"
                LastFileUploadInstant = Instant.MinValue
                LastDirectoryVersionInstant = Instant.MinValue
                DirectoryIds = HashSet<DirectoryVersionId>([| rootDirectoryId |])
            }

        let ipcFileName = Services.IpcFileName()

        Directory.CreateDirectory(Path.GetDirectoryName(ipcFileName))
        |> ignore

        File.WriteAllText(ipcFileName, serialize status)
        ipcFileName

    let private readFileIfExists path = if File.Exists(path) then Some(File.ReadAllText(path)) else None

    let private deleteWatchStatusFileIfExists () =
        let ipcFileName = Services.IpcFileName()

        if File.Exists(ipcFileName) then File.Delete(ipcFileName)

    let private finalizedManifest () =
        let bytes = Encoding.UTF8.GetBytes("watch manifest-backed file")

        let block =
            match ContentBlockFormat.encode [ { PhysicalOffset = 0L; Bytes = bytes } ] with
            | Ok block -> block
            | Error error ->
                Assert.Fail($"Expected content block encoding to succeed, got {error}.")
                Unchecked.defaultof<ContentBlockFormat.EncodedContentBlock>

        let manifest =
            FileManifest.Create(
                ManifestAddress String.Empty,
                RabinChunking.SuiteName,
                FileContentHash(ContentAddress.computeBlake3Hex bytes),
                int64 bytes.Length,
                [
                    ContentBlock.Create(block.Address, 0L, int64 bytes.Length)
                ]
            )

        { manifest with ManifestAddress = ContentAddress.computeManifestAddressForManifest manifest }

    let private withTempRepo (action: string -> unit) =
        let tempDir = Path.Combine(Path.GetTempPath(), $"grace-watch-tests-{Guid.NewGuid():N}")
        let graceDir = Path.Combine(tempDir, Constants.GraceConfigDirectory)
        let configPath = Path.Combine(graceDir, Constants.GraceConfigFileName)
        Directory.CreateDirectory(graceDir) |> ignore
        File.WriteAllText(configPath, "{}")

        let originalDir = Environment.CurrentDirectory

        try
            Environment.CurrentDirectory <- tempDir
            resetConfiguration ()
            Services.graceWatchStatusUpdateTime <- Instant.MinValue
            deleteWatchStatusFileIfExists ()
            action tempDir
        finally
            deleteWatchStatusFileIfExists ()
            Services.graceWatchStatusUpdateTime <- Instant.MinValue
            resetConfiguration ()
            Environment.CurrentDirectory <- originalDir

            if Directory.Exists(tempDir) then
                try
                    Directory.Delete(tempDir, true)
                with
                | _ -> ()

    [<Test>]
    let ``resolveSignalRAccessTokenResult returns token when present`` () =
        let result = Watch.resolveSignalRAccessTokenResult (Ok(Some "token-value"))

        match result with
        | Ok token -> token |> should equal "token-value"
        | Error error -> Assert.Fail($"Expected token result, got error: {error}")

    [<Test>]
    let ``resolveSignalRAccessTokenResult errors when token is missing`` () =
        let result = Watch.resolveSignalRAccessTokenResult (Ok None)

        match result with
        | Ok token -> Assert.Fail($"Expected missing token error, got token: {token}")
        | Error error ->
            error
            |> should contain "No access token is available."

    [<Test>]
    let ``resolveSignalRAccessTokenResult includes underlying auth error`` () =
        let result = Watch.resolveSignalRAccessTokenResult (Error "test error")

        match result with
        | Ok token -> Assert.Fail($"Expected auth error, got token: {token}")
        | Error error ->
            error
            |> should contain "Unable to acquire an access token for SignalR notifications:"

            error |> should contain "test error"

    [<Test>]
    let ``watch save overlay preserves prior manifest backed unchanged file version`` () =
        let manifest = finalizedManifest ()

        let localFileVersion =
            LocalFileVersion.CreateWithHashes
                (RelativePath "large.bin")
                (Sha256Hash "watch-manifest-sha")
                (Blake3Hash $"{manifest.FileContentHash}")
                true
                manifest.Size
                (getCurrentInstant ())
                true
                DateTime.UtcNow

        let priorFileVersion = localFileVersion.ToFileVersion
        priorFileVersion.ContentReference <- FileContentReference.FileManifest manifest

        let previousDirectoryVersion =
            DirectoryVersion.CreateWithHashes
                (Guid.NewGuid())
                OwnerId.Empty
                OrganizationId.Empty
                RepositoryId.Empty
                Constants.RootDirectoryPath
                (Sha256Hash "watch-previous-directory-sha")
                (Blake3Hash "watch-previous-directory-blake3")
                (List<DirectoryVersionId>())
                (List<FileVersion>([| priorFileVersion |]))
                localFileVersion.Size

        let changedLocalDirectoryVersion =
            LocalDirectoryVersion.CreateWithHashes
                (Guid.NewGuid())
                OwnerId.Empty
                OrganizationId.Empty
                RepositoryId.Empty
                Constants.RootDirectoryPath
                (Sha256Hash "watch-new-directory-sha")
                (Blake3Hash "watch-new-directory-blake3")
                (List<DirectoryVersionId>())
                (List<LocalFileVersion>([| localFileVersion |]))
                localFileVersion.Size
                DateTime.UtcNow

        let directoryVersion = toDirectoryVersionWithUploadedFiles Seq.empty<FileVersion> [ previousDirectoryVersion ] changedLocalDirectoryVersion

        directoryVersion.Files.Count |> should equal 1

        directoryVersion.Files[0]
            .ContentReference
            .ReferenceType
        |> should equal FileContentReferenceType.FileManifest

        directoryVersion.Files[0]
            .ContentReference
            .Manifest
        |> should equal (Some manifest)

    [<Test>]
    let ``watch save overlay preserves newly uploaded manifest backed file version`` () =
        let manifest = finalizedManifest ()

        let localFileVersion =
            LocalFileVersion.CreateWithHashes
                (RelativePath "large.bin")
                (Sha256Hash "watch-uploaded-manifest-sha")
                (Blake3Hash $"{manifest.FileContentHash}")
                true
                manifest.Size
                (getCurrentInstant ())
                true
                DateTime.UtcNow

        localFileVersion.ToFileVersion.ContentReference.ReferenceType
        |> should equal FileContentReferenceType.WholeFileContent

        let uploadedFileVersion = localFileVersion.ToFileVersion
        uploadedFileVersion.ContentReference <- FileContentReference.FileManifest manifest

        let changedLocalDirectoryVersion =
            LocalDirectoryVersion.CreateWithHashes
                (Guid.NewGuid())
                OwnerId.Empty
                OrganizationId.Empty
                RepositoryId.Empty
                Constants.RootDirectoryPath
                (Sha256Hash "watch-uploaded-directory-sha")
                (Blake3Hash "watch-uploaded-directory-blake3")
                (List<DirectoryVersionId>())
                (List<LocalFileVersion>([| localFileVersion |]))
                localFileVersion.Size
                DateTime.UtcNow

        let directoryVersion = toDirectoryVersionWithUploadedFiles [ uploadedFileVersion ] Seq.empty<DirectoryVersion> changedLocalDirectoryVersion

        directoryVersion.Files.Count |> should equal 1

        directoryVersion.Files[0]
            .ContentReference
            .ReferenceType
        |> should equal FileContentReferenceType.FileManifest

        directoryVersion.Files[0]
            .ContentReference
            .Manifest
        |> should equal (Some manifest)

    [<Test>]
    let ``watch exits with auth guidance when no token is configured`` () =
        withTempRepo (fun _ ->
            clearWatchAuthEnv (fun () ->
                let exitCode, output = runWithCapturedOutput [| "watch" |]

                if exitCode <> -1 then
                    Assert.Fail(
                        $"Expected watch to exit with -1 when auth is missing. Actual: {exitCode}.{Environment.NewLine}Output:{Environment.NewLine}{output}"
                    )

                output
                |> should contain "Unable to acquire an access token for SignalR"

                output
                |> should contain "Authentication is not configured."))

    [<Test>]
    let ``watch exits nonzero when live watcher status already exists`` () =
        withTempRepo (fun _ ->
            clearWatchAuthEnv (fun () ->
                let ipcFileName = writeLiveWatchStatusFile ()
                let originalContents = readFileIfExists ipcFileName
                let exitCode, output = runWithCapturedOutput [| "watch" |]

                exitCode |> should equal -1

                output
                |> should contain "GraceWatch is already running"

                output
                |> should not' (contain "Unable to acquire an access token for SignalR")

                readFileIfExists ipcFileName
                |> should equal originalContents))

    [<Test>]
    let ``watch startup claim is atomic for simultaneous ordinary starts`` () =
        withTempRepo (fun _ ->
            let claimTasks =
                Array.init 8 (fun _ ->
                    Task.Run (fun () ->
                        Services
                            .tryClaimGraceWatchInterprocessFile()
                            .Result))

            claimTasks
            |> Array.map (fun claimTask -> claimTask :> Task)
            |> Task.WaitAll

            let successfulClaims =
                claimTasks
                |> Array.filter (fun claimTask -> claimTask.Result)

            successfulClaims.Length |> should equal 1

            let status = Services.getGraceWatchStatus().Result
            status |> should equal None)

    [<Test>]
    let ``watch startup claim blocks second ordinary start without usable status`` () =
        withTempRepo (fun _ ->
            clearWatchAuthEnv (fun () ->
                let claimed =
                    Services
                        .tryClaimGraceWatchInterprocessFile()
                        .Result

                claimed |> should equal true

                let status = Services.getGraceWatchStatus().Result
                status |> should equal None

                let exitCode, output = runWithCapturedOutput [| "watch" |]

                exitCode |> should equal -1

                output
                |> should contain "GraceWatch is already running"

                output
                |> should not' (contain "Unable to acquire an access token for SignalR")))

    [<Test>]
    let ``watch startup claim replaces malformed stale ipc file`` () =
        withTempRepo (fun _ ->
            let ipcFileName = Services.IpcFileName()

            Directory.CreateDirectory(Path.GetDirectoryName(ipcFileName))
            |> ignore

            File.WriteAllText(ipcFileName, "not-json")

            let claimed =
                Services
                    .tryClaimGraceWatchInterprocessFile()
                    .Result

            claimed |> should equal true

            let status = Services.getGraceWatchStatus().Result
            status |> should equal None)

    [<Test>]
    let ``watch check exits zero when live watcher status exists`` () =
        withTempRepo (fun _ ->
            let ipcFileName = writeLiveWatchStatusFile ()
            let originalContents = readFileIfExists ipcFileName

            let exitCode, output =
                runWithCapturedOutput [| "watch"
                                         "--check" |]

            exitCode |> should equal 0

            output |> should contain "GraceWatch is running"

            readFileIfExists ipcFileName
            |> should equal originalContents)

    [<Test>]
    let ``watch check through main exits zero and preserves live watcher status`` () =
        withTempRepo (fun _ ->
            let ipcFileName = writeLiveWatchStatusFile ()
            let originalContents = readFileIfExists ipcFileName

            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "watch"
                                                  "--check" |]

            exitCode |> should equal 0
            standardError |> should equal String.Empty

            standardOut
            |> should contain "GraceWatch is running"

            readFileIfExists ipcFileName
            |> should equal originalContents)

    [<Test>]
    let ``watch check json mode emits error envelope and preserves live watcher status`` () =
        withTempRepo (fun _ ->
            let ipcFileName = writeLiveWatchStatusFile ()
            let originalContents = readFileIfExists ipcFileName

            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "Json"
                                                  "watch"
                                                  "--check" |]

            exitCode |> should equal -1
            standardError |> should equal String.Empty

            standardOut
            |> should not' (contain "GraceWatch is running")

            use document = parseJsonOutput standardOut
            let root = document.RootElement

            root.GetProperty("Error").GetString()
            |> should contain "watch is a continuous foreground workflow"

            let mutable returnValue = Unchecked.defaultof<JsonElement>

            root.TryGetProperty("ReturnValue", &returnValue)
            |> should equal false

            readFileIfExists ipcFileName
            |> should equal originalContents)

    [<Test>]
    let ``watch check select mode emits error envelope and preserves live watcher status`` () =
        withTempRepo (fun _ ->
            let ipcFileName = writeLiveWatchStatusFile ()
            let originalContents = readFileIfExists ipcFileName

            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "watch"
                                                  "--check"
                                                  "--select"
                                                  "RootDirectoryId" |]

            exitCode |> should equal -1
            standardError |> should equal String.Empty

            standardOut
            |> should not' (contain "GraceWatch is running")

            use document = parseJsonOutput standardOut
            let root = document.RootElement

            root.GetProperty("Error").GetString()
            |> should contain "does not support --select in this release"

            let mutable returnValue = Unchecked.defaultof<JsonElement>

            root.TryGetProperty("ReturnValue", &returnValue)
            |> should equal false

            readFileIfExists ipcFileName
            |> should equal originalContents)

    [<Test>]
    let ``watch check exits nonzero when live watcher status is missing`` () =
        withTempRepo (fun _ ->
            clearWatchAuthEnv (fun () ->
                let exitCode, output =
                    runWithCapturedOutput [| "watch"
                                             "--check" |]

                exitCode |> should equal -1

                output
                |> should contain "GraceWatch is not running"

                output
                |> should not' (contain "Unable to acquire an access token for SignalR")

                Services.IpcFileName()
                |> File.Exists
                |> should equal false))

    [<Test>]
    let ``watch check exits nonzero and preserves startup claim`` () =
        withTempRepo (fun _ ->
            clearWatchAuthEnv (fun () ->
                let claimed =
                    Services
                        .tryClaimGraceWatchInterprocessFile()
                        .Result

                claimed |> should equal true

                let ipcFileName = Services.IpcFileName()
                let originalContents = readFileIfExists ipcFileName

                let exitCode, output =
                    runWithCapturedOutput [| "watch"
                                             "--check" |]

                exitCode |> should equal -1

                output
                |> should contain "GraceWatch is not running"

                output
                |> should not' (contain "Unable to acquire an access token for SignalR")

                readFileIfExists ipcFileName
                |> should equal originalContents))

    [<Test>]
    let ``watch json auth failure emits one clean error envelope`` () =
        withTempRepo (fun _ ->
            clearWatchAuthEnv (fun () ->
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Json"
                                                      "watch" |]

                exitCode |> should equal -1
                standardError |> should equal String.Empty

                standardOut
                |> should not' (contain "SignalR Hub connection state")

                standardOut |> should not' (contain "Elapsed:")

                use document = parseJsonOutput standardOut
                let root = document.RootElement

                root.GetProperty("Error").GetString()
                |> should contain "watch is a continuous foreground workflow"

                let mutable returnValue = Unchecked.defaultof<JsonElement>

                root.TryGetProperty("ReturnValue", &returnValue)
                |> should equal false))

    [<Test>]
    let ``watch json error in separate process does not delete live watch ipc file`` () =
        withTempRepo (fun root ->
            clearWatchAuthEnv (fun () ->
                let ipcFileName = writeLiveWatchStatusFile ()

                let exitCode, standardOut, standardError = runGraceProcessWithCapturedStdoutAndStderr root [| "--output"; "Json"; "watch" |]

                Assert.That(exitCode, Is.EqualTo(-1).Or.EqualTo(255))
                standardError |> should equal String.Empty

                use document = parseJsonOutput standardOut

                document
                    .RootElement
                    .GetProperty("Error")
                    .GetString()
                |> should contain "watch is a continuous foreground workflow"

                File.Exists(ipcFileName) |> should equal true))
