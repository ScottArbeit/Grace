namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.Shared
open Grace.Shared.Client
open Grace.Shared.Utilities
open NodaTime
open NUnit.Framework
open System
open System.IO
open System.Text.Json

[<NonParallelizable>]
module HistoryStorageTests =

    let private withFileBackup (path: string) (action: unit -> unit) =
        let backupPath = path + ".testbackup"
        let hadExisting = File.Exists(path)

        if hadExisting then File.Copy(path, backupPath, true)

        try
            action ()
        finally
            if hadExisting then
                File.Copy(backupPath, path, true)
                File.Delete(backupPath)
            elif File.Exists(path) then
                File.Delete(path)

    let private randomString (random: Random) =
        let length = random.Next(8, 32)
        let chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"
        let buffer = Array.zeroCreate<char> length

        for i in 0 .. length - 1 do
            buffer[i] <- chars[random.Next(chars.Length)]

        String(buffer)

    [<Test>]
    let ``redacts --token values`` () =
        let config = UserConfiguration.UserConfiguration()
        let random = Random(1337)

        for _ in 0..49 do
            let value = randomString random
            let args = [| "--token"; value |]
            let redacted, _ = HistoryStorage.redactArguments args config.History
            redacted |> Array.exists (fun arg -> arg = value) |> should equal false

    [<Test>]
    let ``redacts --token=value values`` () =
        let config = UserConfiguration.UserConfiguration()
        let random = Random(7331)

        for _ in 0..49 do
            let value = randomString random
            let args = [| $"--token={value}" |]
            let redacted, _ = HistoryStorage.redactArguments args config.History

            redacted
            |> Array.exists (fun arg -> arg.Contains(value, StringComparison.Ordinal))
            |> should equal false

    [<TestCase("30s", 30.0)>]
    [<TestCase("10m", 600.0)>]
    [<TestCase("24h", 86400.0)>]
    [<TestCase("7d", 604800.0)>]
    let ``parses valid durations`` (input: string, expectedSeconds: float) =
        match HistoryStorage.tryParseDuration input with
        | Ok duration -> duration.TotalSeconds |> should equal expectedSeconds
        | Error error -> Assert.Fail($"Expected Ok, got Error: {error}")

    [<TestCase("")>]
    [<TestCase("10x")>]
    [<TestCase("abc")>]
    [<TestCase("10")>]
    let ``rejects invalid durations`` (input: string) =
        match HistoryStorage.tryParseDuration input with
        | Ok _ -> Assert.Fail("Expected Error, got Ok.")
        | Error _ -> Assert.Pass()

    [<Test>]
    let ``readHistoryEntries skips corrupt lines`` () =
        let historyPath = HistoryStorage.getHistoryFilePath ()
        let historyDir = Path.GetDirectoryName(historyPath)
        Directory.CreateDirectory(historyDir) |> ignore

        let options = JsonSerializerOptions(Constants.JsonSerializerOptions)
        options.WriteIndented <- false

        let entry: HistoryStorage.HistoryEntry =
            { id = Guid.NewGuid()
              timestampUtc = getCurrentInstant ()
              argvOriginal = [| "branch"; "status" |]
              argvNormalized = [| "branch"; "status" |]
              commandLine = "branch status"
              cwd = Environment.CurrentDirectory
              repoRoot = None
              repoName = None
              repoBranch = None
              graceVersion = "0.1"
              exitCode = 0
              durationMs = 10L
              parseSucceeded = true
              redactions = List.empty
              source = None }

        let json = JsonSerializer.Serialize(entry, options)

        withFileBackup historyPath (fun () ->
            File.WriteAllLines(historyPath, [| json; "not json" |])
            let result = HistoryStorage.readHistoryEntries ()
            result.Entries.Length |> should equal 1
            result.CorruptCount |> should equal 1)

    [<Test>]
    let ``prunes history to max entries`` () =
        let historyPath = HistoryStorage.getHistoryFilePath ()
        let configPath = UserConfiguration.getUserConfigurationPath ()
        let historyDir = Path.GetDirectoryName(historyPath)
        Directory.CreateDirectory(historyDir) |> ignore

        withFileBackup configPath (fun () ->
            withFileBackup historyPath (fun () ->
                let configuration = UserConfiguration.UserConfiguration()
                configuration.History.Enabled <- true
                configuration.History.MaxEntries <- 2
                configuration.History.MaxFileBytes <- 1024L * 1024L
                configuration.History.RetentionDays <- 365

                match UserConfiguration.saveUserConfiguration configuration with
                | Ok _ -> ()
                | Error error -> Assert.Fail(error)

                File.WriteAllText(historyPath, String.Empty)

                let start = getCurrentInstant ()

                for offset in [ 0..2 ] do
                    let timestamp = start.Plus(Duration.FromMinutes(float offset))

                    HistoryStorage.tryRecordInvocation
                        { argvOriginal = [| "branch"; "status" |]
                          argvNormalized = [| "branch"; "status" |]
                          cwd = Environment.CurrentDirectory
                          exitCode = 0
                          durationMs = 5L
                          parseSucceeded = true
                          timestampUtc = timestamp
                          source = None }

                let result = HistoryStorage.readHistoryEntries ()
                result.Entries.Length |> should equal 2))
