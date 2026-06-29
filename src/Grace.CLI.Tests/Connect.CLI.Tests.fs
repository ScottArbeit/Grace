namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.Command
open Grace.Types.Common
open NUnit.Framework
open Spectre.Console
open System
open System.IO

[<NonParallelizable>]
module ConnectTests =
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
            let exitCode = GraceCommand.main args
            exitCode, writer.ToString()
        finally
            Console.SetOut(originalOut)
            setAnsiConsoleOutput originalOut

    let private withTempDir (action: string -> unit) =
        let tempDir = Path.Combine(Path.GetTempPath(), $"grace-cli-tests-{Guid.NewGuid():N}")
        Directory.CreateDirectory(tempDir) |> ignore
        let originalDir = Environment.CurrentDirectory

        try
            Environment.CurrentDirectory <- tempDir
            action tempDir
        finally
            Environment.CurrentDirectory <- originalDir

            if Directory.Exists(tempDir) then
                try
                    Directory.Delete(tempDir, true)
                with
                | _ -> ()

    let private getGraceConfigPath root = Path.Combine(root, ".grace", "graceconfig.json")

    [<Test>]
    let ``connect creates config when missing`` () =
        withTempDir (fun root ->
            let exitCode, _ = runWithCapturedOutput [| "connect" |]
            exitCode |> should equal -1

            File.Exists(getGraceConfigPath root)
            |> should equal true)

    [<Test>]
    let ``connect skip decision requires matching blake3 when remote has one`` () =
        let remoteFile =
            FileVersion.CreateWithHashes
                (RelativePath "same-sha-different-blake3.txt")
                (Sha256Hash "shared-sha")
                (Blake3Hash "remote-blake3")
                String.Empty
                false
                10L

        Connect.existingFileMatchesRemoteVersion (Sha256Hash "shared-sha") (Blake3Hash "local-blake3") remoteFile
        |> should equal false

        Connect.existingFileMatchesRemoteVersion (Sha256Hash "shared-sha") (Blake3Hash "remote-blake3") remoteFile
        |> should equal true

    [<Test>]
    let ``connect skip decision keeps legacy empty blake3 remote compatible`` () =
        let remoteFile = FileVersion.Create (RelativePath "legacy-sha-only.txt") (Sha256Hash "legacy-sha") String.Empty false 10L

        Connect.existingFileMatchesRemoteVersion (Sha256Hash "legacy-sha") (Blake3Hash "different-local-blake3") remoteFile
        |> should equal true
