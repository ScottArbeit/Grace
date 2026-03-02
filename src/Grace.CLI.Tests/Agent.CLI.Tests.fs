namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.Shared.Client.Configuration
open NUnit.Framework
open System
open System.IO

[<NonParallelizable>]
module AgentCommandTests =
    let private ownerId = Guid.NewGuid()
    let private organizationId = Guid.NewGuid()
    let private repositoryId = Guid.NewGuid()

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
            |]

    let private withIdsAndSilent (args: string array) =
        args
        |> Array.append [| "--output"; "Silent" |]
        |> withIds

    let private invoke (args: string array) =
        let parseResult = GraceCommand.rootCommand.Parse(args)
        parseResult.Invoke()

    let private withTempDir (action: string -> unit) =
        let tempDir = Path.Combine(Path.GetTempPath(), $"grace-agent-tests-{Guid.NewGuid():N}")
        Directory.CreateDirectory(tempDir) |> ignore
        let originalDir = Environment.CurrentDirectory

        try
            Environment.CurrentDirectory <- tempDir
            resetConfiguration ()
            action tempDir
        finally
            Environment.CurrentDirectory <- originalDir
            resetConfiguration ()

            if Directory.Exists(tempDir) then
                try
                    Directory.Delete(tempDir, true)
                with
                | _ -> ()

    let private writeLocalState (root: string) (agentId: Guid) (sessionId: string) (workItemId: string) =
        let graceDirectory = Path.Combine(root, ".grace")

        Directory.CreateDirectory(graceDirectory)
        |> ignore

        let json =
            sprintf
                """{
  "AgentId": "%s",
  "AgentDisplayName": "Codex",
  "Source": "codex",
  "ActiveSessionId": "%s",
  "ActiveWorkItemIdOrNumber": "%s",
  "ActivePromotionSetId": "",
  "LastOperationId": "op-1",
  "LastCorrelationId": "corr-1",
  "LastUpdatedAtUtc": "2026-02-27T00:00:00Z"
}"""
                (agentId.ToString())
                sessionId
                workItemId

        File.WriteAllText(Path.Combine(graceDirectory, "agent-session-state.json"), json)

    [<Test>]
    let ``agent add-summary rejects invalid work item id`` () =
        let missingSummary = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.md")

        let exitCode =
            invoke (
                withIdsAndSilent [| "agent"
                                    "add-summary"
                                    "--work-item-id"
                                    "not-a-guid"
                                    "--summary-file"
                                    missingSummary |]
            )

        exitCode |> should equal -1

    [<Test>]
    let ``agent add-summary rejects missing summary file`` () =
        let missingSummary = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.md")

        let exitCode =
            invoke (
                withIdsAndSilent [| "agent"
                                    "add-summary"
                                    "--work-item-id"
                                    Guid.NewGuid().ToString()
                                    "--summary-file"
                                    missingSummary |]
            )

        exitCode |> should equal -1

    [<Test>]
    let ``agent add-summary accepts numeric work item identifier`` () =
        let missingSummary = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.md")

        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIds [| "agent"
                           "add-summary"
                           "--work-item-id"
                           "123"
                           "--summary-file"
                           missingSummary |]
            )

        parseResult.Errors.Count |> should equal 0

    [<Test>]
    let ``agent add-summary accepts guid work item identifier`` () =
        let missingSummary = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.md")

        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIds [| "agent"
                           "add-summary"
                           "--work-item-id"
                           (Guid.NewGuid().ToString())
                           "--summary-file"
                           missingSummary |]
            )

        parseResult.Errors.Count |> should equal 0

    [<Test>]
    let ``agent add-summary accepts prompt and promotion-set options with numeric identifier`` () =
        let missingSummary = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.md")
        let missingPrompt = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.md")
        let promotionSetId = Guid.NewGuid().ToString()

        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIds [| "agent"
                           "add-summary"
                           "--work-item-id"
                           "42"
                           "--summary-file"
                           missingSummary
                           "--prompt-file"
                           missingPrompt
                           "--prompt-origin"
                           "agent://codex"
                           "--promotion-set-id"
                           promotionSetId |]
            )

        parseResult.Errors.Count |> should equal 0

    [<Test>]
    let ``agent add-summary rejects prompt-origin without prompt file`` () =
        let summaryPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.md")

        try
            File.WriteAllText(summaryPath, "summary")

            let exitCode =
                invoke (
                    withIdsAndSilent [| "agent"
                                        "add-summary"
                                        "--work-item-id"
                                        "42"
                                        "--summary-file"
                                        summaryPath
                                        "--prompt-origin"
                                        "agent://codex" |]
                )

            exitCode |> should equal -1
        finally
            if File.Exists(summaryPath) then File.Delete(summaryPath)

    [<Test>]
    let ``agent bootstrap succeeds without repository config`` () =
        withTempDir (fun root ->
            let agentId = Guid.NewGuid()

            let exitCode =
                invoke [| "agent"
                          "bootstrap"
                          "--agent-id"
                          agentId.ToString()
                          "--display-name"
                          "Codex"
                          "--output"
                          "Silent" |]

            exitCode |> should equal 0

            File.Exists(Path.Combine(root, ".grace", "agent-session-state.json"))
            |> should equal true)

    [<Test>]
    let ``agent work start reports actionable missing config`` () =
        withTempDir (fun _ ->
            let exitCode =
                invoke [| "agent"
                          "work"
                          "start"
                          "--work-item-id"
                          "42"
                          "--output"
                          "Silent" |]

            exitCode |> should equal -1)

    [<Test>]
    let ``agent work start rejects stale local state mismatch`` () =
        withTempDir (fun root ->
            writeLocalState root (Guid.NewGuid()) "session-1" "41"

            let exitCode =
                invoke (
                    withIdsAndSilent [| "agent"
                                        "work"
                                        "start"
                                        "--work-item-id"
                                        "42" |]
                )

            exitCode |> should equal -1)

    [<Test>]
    let ``agent work start handles idempotent local replay`` () =
        withTempDir (fun root ->
            writeLocalState root (Guid.NewGuid()) "session-1" "42"

            let exitCode =
                invoke (
                    withIdsAndSilent [| "agent"
                                        "work"
                                        "start"
                                        "--work-item-id"
                                        "42" |]
                )

            exitCode |> should equal 0)

    [<Test>]
    let ``agent work stop is idempotent when no active local session exists`` () =
        withTempDir (fun _ ->
            let bootstrapExitCode =
                invoke [| "agent"
                          "bootstrap"
                          "--agent-id"
                          Guid.NewGuid().ToString()
                          "--display-name"
                          "Codex"
                          "--output"
                          "Silent" |]

            bootstrapExitCode |> should equal 0

            let stopExitCode =
                invoke (
                    withIdsAndSilent [| "agent"
                                        "work"
                                        "stop" |]
                )

            stopExitCode |> should equal 0)

    [<Test>]
    let ``agent work status rejects stale session override`` () =
        withTempDir (fun root ->
            writeLocalState root (Guid.NewGuid()) "session-1" "42"

            let exitCode =
                invoke (
                    withIdsAndSilent [| "agent"
                                        "work"
                                        "status"
                                        "--session-id"
                                        "session-2" |]
                )

            exitCode |> should equal -1)
