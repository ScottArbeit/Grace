namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.Command
open Grace.CLI.Text
open Grace.Shared.Client.Configuration
open Grace.Types.Branch
open Grace.Types.Reference
open Grace.Types.Types
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
                with _ ->
                    ()

    let private getGraceConfigPath root = Path.Combine(root, ".grace", "graceconfig.json")

    [<Test>]
    let ``connect creates config when missing`` () =
        withTempDir (fun root ->
            let exitCode, _ = runWithCapturedOutput [| "connect" |]
            exitCode |> should equal -1
            File.Exists(getGraceConfigPath root) |> should equal true)

    [<Test>]
    let ``connect retrieve default branch defaults to true`` () =
        let parseResult = GraceCommand.rootCommand.Parse([| "connect" |])

        parseResult.GetValue<bool>(OptionName.RetrieveDefaultBranch)
        |> should equal true

    [<Test>]
    let ``connect retrieve default branch parses explicit false`` () =
        let parseResult = GraceCommand.rootCommand.Parse([| "connect"; OptionName.RetrieveDefaultBranch; "false" |])

        parseResult.GetValue<bool>(OptionName.RetrieveDefaultBranch)
        |> should equal false

    [<Test>]
    let ``connect directory version selection precedence uses directory version id`` () =
        let directoryVersionId = Guid.NewGuid()
        let referenceId = Guid.NewGuid()

        let parseResult =
            GraceCommand.rootCommand.Parse(
                [| "connect"
                   OptionName.DirectoryVersionId
                   $"{directoryVersionId}"
                   OptionName.ReferenceId
                   $"{referenceId}"
                   OptionName.ReferenceType
                   "Commit" |]
            )

        match Connect.getDirectoryVersionSelection parseResult with
        | Connect.UseDirectoryVersionId selected -> selected |> should equal directoryVersionId
        | other -> Assert.Fail($"Unexpected selection: {other}")

    [<Test>]
    let ``connect default directory version falls back to based-on`` () =
        let basedOnId = Guid.NewGuid()
        let branchDto = { BranchDto.Default with LatestPromotion = ReferenceDto.Default; BasedOn = { ReferenceDto.Default with DirectoryId = basedOnId } }

        Connect.resolveDefaultDirectoryVersionId branchDto
        |> should equal (Some basedOnId)

    [<Test>]
    let ``connect repository shortcut populates owner organization repository`` () =
        let parseResult = GraceCommand.rootCommand.Parse([| "connect"; "owner/org/repo" |])
        let graceIds = GraceIds.Default

        match Connect.applyRepositoryShortcut parseResult graceIds with
        | Ok updated ->
            updated.OwnerName |> should equal "owner"
            updated.OrganizationName |> should equal "org"
            updated.RepositoryName |> should equal "repo"
            updated.OwnerId |> should equal Guid.Empty
            updated.OrganizationId |> should equal Guid.Empty
            updated.RepositoryId |> should equal Guid.Empty
            updated.HasOwner |> should equal true
            updated.HasOrganization |> should equal true
            updated.HasRepository |> should equal true
        | Error error -> Assert.Fail($"Unexpected error: {error.Error}")

    [<Test>]
    let ``connect repository shortcut rejects missing segments`` () =
        let parseResult = GraceCommand.rootCommand.Parse([| "connect"; "owner/repo" |])
        let graceIds = GraceIds.Default

        match Connect.applyRepositoryShortcut parseResult graceIds with
        | Ok _ -> Assert.Fail("Expected error when repository shortcut is missing segments.")
        | Error error -> error.Error |> should contain "owner/organization/repository"

    [<Test>]
    let ``connect repository shortcut conflicts with explicit options`` () =
        let parseResult = GraceCommand.rootCommand.Parse([| "connect"; "owner/org/repo"; OptionName.OwnerName; "explicit-owner" |])

        let graceIds = GraceIds.Default

        match Connect.applyRepositoryShortcut parseResult graceIds with
        | Ok _ -> Assert.Fail("Expected error when shortcut is combined with explicit options.")
        | Error error -> error.Error |> should contain "Provide either the repository shortcut"
