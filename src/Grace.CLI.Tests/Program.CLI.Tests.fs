namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open NUnit.Framework
open System

/// Groups command parsing coverage for the CLI test project.
[<TestFixture>]
[<NonParallelizable>]
module CommandParsingTests =
    /// Runs the supplied action with environment variable applied.
    let private withEnvironmentVariable (name: string) (value: string option) (action: unit -> unit) =
        let original = Environment.GetEnvironmentVariable(name)

        try
            Environment.SetEnvironmentVariable(name, value |> Option.toObj)
            action ()
        finally
            Environment.SetEnvironmentVariable(name, original)

    /// Verifies that resolve invocation source prefers explicit source over environment.
    [<Test>]
    let ``resolveInvocationSource prefers explicit source over environment`` () =
        withEnvironmentVariable Common.SourceEnvironmentVariableName (Some "env-source") (fun () ->
            let parseResult =
                GraceCommand.rootCommand.Parse(
                    [|
                        "--source"
                        "explicit-source"
                        "history"
                        "show"
                    |]
                )

            parseResult.Errors.Count |> should equal 0

            Common.resolveInvocationSource parseResult
            |> should equal (Some "explicit-source"))

    /// Verifies that resolve invocation source falls back to environment.
    [<Test>]
    let ``resolveInvocationSource falls back to environment`` () =
        withEnvironmentVariable Common.SourceEnvironmentVariableName (Some "env-source") (fun () ->
            let parseResult = GraceCommand.rootCommand.Parse([| "history"; "show" |])
            parseResult.Errors.Count |> should equal 0

            Common.resolveInvocationSource parseResult
            |> should equal (Some "env-source"))

    /// Verifies that history show accepts source filter option.
    [<Test>]
    let ``history show accepts source filter option`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                [|
                    "history"
                    "show"
                    "--source"
                    "codex"
                |]
            )

        parseResult.Errors.Count |> should equal 0

    /// Verifies that history search accepts source filter option.
    [<Test>]
    let ``history search accepts source filter option`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse [| "history"
                                              "search"
                                              "workitem"
                                              "--source"
                                              "codex" |]

        parseResult.Errors.Count |> should equal 0

    /// Verifies cache commands bypass repository presentation so the child process keeps its JSON stream clean in every directory.
    [<Test>]
    let ``cache command is identified as machine scoped before repository invocation`` () =
        let parseResult = GraceCommand.rootCommand.Parse([| "cache"; "status" |])
        parseResult.Errors.Count |> should equal 0

        GraceCommand.isGraceCache parseResult
        |> should equal true

    /// Builds grant role args test data used to exercise CLI program behavior.
    let private grantRoleArgs roleId =
        [|
            "authorize"
            "grant-role"
            "--principal-type"
            "User"
            "--principal-id"
            "user-1"
            "--role"
            roleId
        |]

    /// Builds revoke role args test data used to exercise CLI program behavior.
    let private revokeRoleArgs roleId =
        [|
            "authorize"
            "revoke-role"
            "--principal-type"
            "User"
            "--principal-id"
            "user-1"
            "--role"
            roleId
        |]

    /// Builds revoke role args with scope test data used to exercise CLI program behavior.
    let private revokeRoleArgsWithScope roleId scopeArgs = Array.append (revokeRoleArgs roleId) scopeArgs

    /// Verifies that authorize role commands accept canonical role ids.
    [<TestCase("SystemAdmin")>]
    [<TestCase("SystemOperator")>]
    [<TestCase("SystemReader")>]
    [<TestCase("OwnerAdmin")>]
    [<TestCase("OwnerContributor")>]
    [<TestCase("OwnerReader")>]
    [<TestCase("OrganizationAdmin")>]
    [<TestCase("OrganizationContributor")>]
    [<TestCase("OrganizationReader")>]
    [<TestCase("RepositoryAdmin")>]
    [<TestCase("RepositoryContributor")>]
    [<TestCase("RepositoryReader")>]
    [<TestCase("repositoryreader")>]
    [<TestCase("BranchAdmin")>]
    [<TestCase("BranchWriter")>]
    [<TestCase("BranchReader")>]
    [<TestCase("RepositoryApprovalResponder")>]
    [<TestCase("BranchApprovalResponder")>]
    let ``authorize role commands accept canonical role ids`` roleId =
        let grantParseResult = GraceCommand.rootCommand.Parse(grantRoleArgs roleId)
        grantParseResult.Errors.Count |> should equal 0

        let revokeParseResult = GraceCommand.rootCommand.Parse(revokeRoleArgs roleId)
        revokeParseResult.Errors.Count |> should equal 0

    /// Verifies that authorize grant role rejects stale short role ids.
    [<TestCase("OrgAdmin")>]
    [<TestCase("OrgContributor")>]
    [<TestCase("OrgReader")>]
    [<TestCase("RepoAdmin")>]
    [<TestCase("RepoContributor")>]
    [<TestCase("RepoReader")>]
    [<TestCase("rEpOaDmIn")>]
    [<TestCase("oRgReAdEr")>]
    [<TestCase("ApprovalResponder")>]
    let ``authorize grant role rejects stale short role ids`` roleId =
        let grantParseResult = GraceCommand.rootCommand.Parse(grantRoleArgs roleId)
        grantParseResult.Errors.Count |> should equal 1

    /// Verifies that authorize revoke role allows stale role ids when explicit scope is supplied.
    [<TestCase("OrgAdmin")>]
    [<TestCase("ApprovalResponder")>]
    let ``authorize revoke role allows stale role ids when explicit scope is supplied`` roleId =
        let repositoryId = Guid.NewGuid()

        let revokeParseResult =
            GraceCommand.rootCommand.Parse(
                revokeRoleArgsWithScope
                    roleId
                    [|
                        "--repository-id"
                        repositoryId.ToString()
                    |]
            )

        revokeParseResult.Errors.Count |> should equal 0

        match Command.Access.deriveScopeKindForRevoke revokeParseResult roleId with
        | Ok scopeKind -> scopeKind |> should equal "repository"
        | Error error -> Assert.Fail($"Expected stale role revoke scope derivation to succeed, but got {error}.")

    /// Verifies that authorize revoke role rejects stale role ids without explicit scope.
    [<TestCase("OrgAdmin")>]
    [<TestCase("ApprovalResponder")>]
    let ``authorize revoke role rejects stale role ids without explicit scope`` roleId =
        let revokeParseResult = GraceCommand.rootCommand.Parse(revokeRoleArgs roleId)
        revokeParseResult.Errors.Count |> should equal 0

        match Command.Access.deriveScopeKindForRevoke revokeParseResult roleId with
        | Ok scopeKind -> Assert.Fail($"Expected stale role revoke without explicit scope to fail, but got {scopeKind}.")
        | Error error -> error.Error |> should contain "explicit scope"

    /// Verifies that authorize grant and revoke derive scope from role without scope option.
    [<Test>]
    let ``authorize grant and revoke derive scope from role without scope option`` () =
        let grantParseResult = GraceCommand.rootCommand.Parse(grantRoleArgs "RepositoryReader")
        grantParseResult.Errors.Count |> should equal 0

        let revokeParseResult = GraceCommand.rootCommand.Parse(revokeRoleArgs "RepositoryReader")
        revokeParseResult.Errors.Count |> should equal 0

    /// Verifies that authorize grant no longer accepts explicit scope option.
    [<Test>]
    let ``authorize grant no longer accepts explicit scope option`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                [|
                    yield! grantRoleArgs "RepositoryReader"
                    "--scope"
                    "repo"
                |]
            )

        parseResult.Errors.Count
        |> should be (greaterThan 0)

    /// Verifies that authenticate command group accepts authn alias.
    [<Test>]
    let ``authenticate command group accepts authn alias`` () =
        let parseResult = GraceCommand.rootCommand.Parse([| "authn"; "status" |])
        parseResult.Errors.Count |> should equal 0

    /// Verifies that authorize command group accepts authz alias.
    [<Test>]
    let ``authorize command group accepts authz alias`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                grantRoleArgs "RepositoryReader"
                |> Array.updateAt 0 "authz"
            )

        parseResult.Errors.Count |> should equal 0

    /// Verifies that authorize show command parses through authz alias.
    [<Test>]
    let ``authorize show command parses through authz alias`` () =
        let parseResult = GraceCommand.rootCommand.Parse([| "authz"; "show" |])
        parseResult.Errors.Count |> should equal 0

    /// Verifies that authorize can command parses user oriented repo and path checks.
    [<Test>]
    let ``authorize can command parses user oriented repo and path checks`` () =
        let readRepo = GraceCommand.rootCommand.Parse([| "authz"; "can"; "read"; "repo" |])
        readRepo.Errors.Count |> should equal 0

        let pathRead =
            GraceCommand.rootCommand.Parse(
                [|
                    "authz"
                    "can"
                    "read"
                    "path"
                    "--path"
                    "src/Grace.CLI/Program.CLI.fs"
                |]
            )

        pathRead.Errors.Count |> should equal 0

    /// Verifies that old auth and access command groups are not accepted.
    [<TestCase("auth", "status")>]
    [<TestCase("access", "list-roles")>]
    let ``old auth and access command groups are not accepted`` commandGroup commandName =
        let parseResult = GraceCommand.rootCommand.Parse([| commandGroup; commandName |])

        parseResult.Errors.Count
        |> should be (greaterThan 0)


namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.Command
open Grace.SDK
open Grace.Shared
open Grace.Shared.Client
open Grace.Shared.Client.Configuration
open Grace.Shared.Utilities
open Grace.Types.Common
open NUnit.Framework
open Spectre.Console
open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Http
open System.Text.Json

/// Groups help does not read config coverage for the CLI test project.
[<NonParallelizable>]
module HelpDoesNotReadConfigTests =
    /// Sets ansi console output needed by the test scenario.
    let private setAnsiConsoleOutput (writer: TextWriter) =
        let settings = AnsiConsoleSettings()
        settings.Out <- AnsiConsoleOutput(writer)
        AnsiConsole.Console <- AnsiConsole.Create(settings)

    /// Runs with captured output for test scenarios.
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

    /// Runs with captured stdout and stderr for test scenarios.
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

    /// Parses json output for test assertions.
    let private parseJsonOutput (output: string) =
        output
            .TrimStart()
            .StartsWith("{", StringComparison.Ordinal)
        |> should equal true

        JsonDocument.Parse(output)

    /// Asserts that json error output matches the expected contract.
    let private assertJsonErrorOutput (standardOut: string) =
        use document = parseJsonOutput standardOut
        let rootElement = document.RootElement
        let error = rootElement.GetProperty("Error").GetString()

        error |> should not' (equal String.Empty)

        standardOut
        |> should not' (contain "Exception in isOutputFormat")

        standardOut
        |> should not' (contain "graceconfig.json is not found")

        standardOut |> should not' (contain "Elapsed:")

        error

    /// Asserts that grace envelope matches the expected contract.
    let private assertGraceEnvelope (output: string) =
        use document = parseJsonOutput output
        let rootElement = document.RootElement

        rootElement.GetProperty("ReturnValue").ValueKind
        |> should equal JsonValueKind.Object

        rootElement.GetProperty("EventTime").ValueKind
        |> should equal JsonValueKind.String

        rootElement.GetProperty("CorrelationId").ValueKind
        |> should equal JsonValueKind.String

        rootElement.GetProperty("Properties").ValueKind
        |> should equal JsonValueKind.Array

        rootElement.Clone()

    /// Asserts that grace error envelope on clean streams matches the expected contract.
    let private assertGraceErrorEnvelopeOnCleanStreams (standardOut: string) (standardError: string) =
        standardError |> should equal String.Empty
        let error = assertJsonErrorOutput standardOut

        standardOut
            .TrimStart()
            .StartsWith("{", StringComparison.Ordinal)
        |> should equal true

        error

    /// Runs the supplied action with file backup applied.
    let private withFileBackup (path: string) (action: unit -> unit) =
        let directory = Path.GetDirectoryName(path)

        if not (String.IsNullOrWhiteSpace(directory)) then
            Directory.CreateDirectory(directory) |> ignore

        let backupPath = Path.Combine(Path.GetTempPath(), $"grace-cli-test-backup-{Guid.NewGuid():N}")
        let existed = File.Exists(path)

        if existed then File.Copy(path, backupPath, true)

        try
            action ()
        finally
            if existed then
                File.Copy(backupPath, path, true)
                File.Delete(backupPath)
            elif File.Exists(path) then
                File.Delete(path)

    /// Captures stdout and stderr produced by the action.
    let private captureStdoutAndStderr (action: unit -> unit) =
        use standardOutWriter = new StringWriter()
        use standardErrorWriter = new StringWriter()
        let originalOut = Console.Out
        let originalError = Console.Error

        try
            Console.SetOut(standardOutWriter)
            Console.SetError(standardErrorWriter)
            setAnsiConsoleOutput standardOutWriter
            action ()
            standardOutWriter.ToString(), standardErrorWriter.ToString()
        finally
            Console.SetOut(originalOut)
            Console.SetError(originalError)
            setAnsiConsoleOutput originalOut

    /// Captures output produced by the action.
    let private captureOutput (action: unit -> unit) =
        /// Builds standard out test data used to exercise CLI program behavior.
        let standardOut, _ = captureStdoutAndStderr action
        standardOut


    /// Runs the supplied action with temp dir applied.
    let private withTempDir (action: string -> unit) =
        let tempDir = Path.Combine(Path.GetTempPath(), $"grace-cli-tests-{Guid.NewGuid():N}")
        Directory.CreateDirectory(tempDir) |> ignore
        let originalDir = Environment.CurrentDirectory

        try
            Environment.CurrentDirectory <- tempDir
            resetConfiguration ()
            action tempDir
        finally
            Environment.CurrentDirectory <- originalDir

            if Directory.Exists(tempDir) then
                try
                    Directory.Delete(tempDir, true)
                with
                | _ -> ()

    /// Writes invalid config needed by the test scenario.
    let private writeInvalidConfig (root: string) =
        let graceDir = Path.Combine(root, ".grace")
        Directory.CreateDirectory(graceDir) |> ignore
        File.WriteAllText(Path.Combine(graceDir, "graceconfig.json"), "not json")

    /// Writes valid config needed by the test scenario.
    let private writeValidConfig (root: string) (ownerId: Guid) (orgId: Guid) (repoId: Guid) (branchId: Guid) =
        let graceDir = Path.Combine(root, ".grace")
        Directory.CreateDirectory(graceDir) |> ignore
        let config = GraceConfiguration()
        config.OwnerId <- ownerId
        config.OrganizationId <- orgId
        config.RepositoryId <- repoId
        config.BranchId <- branchId
        let json = serialize config
        File.WriteAllText(Path.Combine(graceDir, "graceconfig.json"), json)

    /// Writes valid config with deterministic ids needed by the test scenario.
    let private writeValidConfigWithDeterministicIds (root: string) =
        writeValidConfig
            root
            (Guid.Parse("11111111-1111-1111-1111-111111111111"))
            (Guid.Parse("22222222-2222-2222-2222-222222222222"))
            (Guid.Parse("33333333-3333-3333-3333-333333333333"))
            (Guid.Parse("44444444-4444-4444-4444-444444444444"))

    /// Verifies that authz show explicit repository scope does not include configured branch.
    [<Test>]
    let ``authz show explicit repository scope does not include configured branch`` () =
        withTempDir (fun root ->
            let ownerId = Guid.Parse("11111111-1111-1111-1111-111111111111")
            let organizationId = Guid.Parse("22222222-2222-2222-2222-222222222222")
            let configuredRepositoryId = Guid.Parse("33333333-3333-3333-3333-333333333333")
            let configuredBranchId = Guid.Parse("44444444-4444-4444-4444-444444444444")
            let requestedRepositoryId = Guid.Parse("55555555-5555-5555-5555-555555555555")

            writeValidConfig root ownerId organizationId configuredRepositoryId configuredBranchId

            let parseResult =
                GraceCommand.rootCommand.Parse(
                    [|
                        "authz"
                        "show"
                        "--repository-id"
                        requestedRepositoryId.ToString()
                    |]
                )

            parseResult.Errors.Count |> should equal 0

            let graceIds = Access.normalizeShowRoleAssignmentIds parseResult

            graceIds.OwnerId |> should equal ownerId

            graceIds.OrganizationId
            |> should equal organizationId

            graceIds.RepositoryId
            |> should equal requestedRepositoryId

            graceIds.RepositoryIdString
            |> should equal (requestedRepositoryId.ToString())

            graceIds.BranchId |> should equal BranchId.Empty

            graceIds.BranchIdString
            |> should equal String.Empty

            graceIds.HasBranch |> should equal false)

    /// Verifies that authz show explicit owner scope does not include configured child ids.
    [<Test>]
    let ``authz show explicit owner scope does not include configured child ids`` () =
        withTempDir (fun root ->
            let configuredOwnerId = Guid.Parse("11111111-1111-1111-1111-111111111111")
            let configuredOrganizationId = Guid.Parse("22222222-2222-2222-2222-222222222222")
            let configuredRepositoryId = Guid.Parse("33333333-3333-3333-3333-333333333333")
            let configuredBranchId = Guid.Parse("44444444-4444-4444-4444-444444444444")
            let requestedOwnerId = Guid.Parse("66666666-6666-6666-6666-666666666666")

            writeValidConfig root configuredOwnerId configuredOrganizationId configuredRepositoryId configuredBranchId

            let parseResult =
                GraceCommand.rootCommand.Parse(
                    [|
                        "authz"
                        "show"
                        "--owner-id"
                        requestedOwnerId.ToString()
                    |]
                )

            parseResult.Errors.Count |> should equal 0

            let graceIds = Access.normalizeShowRoleAssignmentIds parseResult

            graceIds.OwnerId |> should equal requestedOwnerId

            graceIds.OrganizationId
            |> should equal OrganizationId.Empty

            graceIds.RepositoryId
            |> should equal RepositoryId.Empty

            graceIds.BranchId |> should equal BranchId.Empty
            graceIds.HasOrganization |> should equal false
            graceIds.HasRepository |> should equal false
            graceIds.HasBranch |> should equal false)

    /// Verifies that authorize revoke role rejects stale role ids with only configured scope.
    [<Test>]
    let ``authorize revoke role rejects stale role ids with only configured scope`` () =
        withTempDir (fun root ->
            writeValidConfigWithDeterministicIds root

            let roleId = "OrgAdmin"

            let revokeParseResult =
                GraceCommand.rootCommand.Parse(
                    [|
                        "authorize"
                        "revoke-role"
                        "--principal-type"
                        "User"
                        "--principal-id"
                        "user-1"
                        "--role"
                        roleId
                    |]
                )

            revokeParseResult.Errors.Count |> should equal 0

            match Command.Access.deriveScopeKindForRevoke revokeParseResult roleId with
            | Ok scopeKind -> Assert.Fail($"Expected stale role revoke with only configured scope to fail, but got {scopeKind}.")
            | Error error -> error.Error |> should contain "explicit scope")

    /// Verifies that authz can explicit owner scope does not include configured child ids.
    [<Test>]
    let ``authz can explicit owner scope does not include configured child ids`` () =
        withTempDir (fun root ->
            let configuredOwnerId = Guid.Parse("11111111-1111-1111-1111-111111111111")
            let configuredOrganizationId = Guid.Parse("22222222-2222-2222-2222-222222222222")
            let configuredRepositoryId = Guid.Parse("33333333-3333-3333-3333-333333333333")
            let configuredBranchId = Guid.Parse("44444444-4444-4444-4444-444444444444")
            let requestedOwnerId = Guid.Parse("66666666-6666-6666-6666-666666666666")

            writeValidConfig root configuredOwnerId configuredOrganizationId configuredRepositoryId configuredBranchId

            let parseResult =
                GraceCommand.rootCommand.Parse(
                    [|
                        "authz"
                        "can"
                        "read"
                        "branch"
                        "--owner-id"
                        requestedOwnerId.ToString()
                    |]
                )

            parseResult.Errors.Count |> should equal 0

            let graceIds = Command.Access.normalizeCanPermissionIds parseResult

            graceIds.OwnerId |> should equal requestedOwnerId

            graceIds.OrganizationId
            |> should equal OrganizationId.Empty

            graceIds.RepositoryId
            |> should equal RepositoryId.Empty

            graceIds.BranchId |> should equal BranchId.Empty
            graceIds.HasOrganization |> should equal false
            graceIds.HasRepository |> should equal false
            graceIds.HasBranch |> should equal false)

    /// Verifies that authz can explicit organization scope does not include configured repository or branch.
    [<Test>]
    let ``authz can explicit organization scope does not include configured repository or branch`` () =
        withTempDir (fun root ->
            let ownerId = Guid.Parse("11111111-1111-1111-1111-111111111111")
            let configuredOrganizationId = Guid.Parse("22222222-2222-2222-2222-222222222222")
            let configuredRepositoryId = Guid.Parse("33333333-3333-3333-3333-333333333333")
            let configuredBranchId = Guid.Parse("44444444-4444-4444-4444-444444444444")
            let requestedOrganizationId = Guid.Parse("77777777-7777-7777-7777-777777777777")

            writeValidConfig root ownerId configuredOrganizationId configuredRepositoryId configuredBranchId

            let parseResult =
                GraceCommand.rootCommand.Parse(
                    [|
                        "authz"
                        "can"
                        "read"
                        "branch"
                        "--organization-id"
                        requestedOrganizationId.ToString()
                    |]
                )

            parseResult.Errors.Count |> should equal 0

            let graceIds = Command.Access.normalizeCanPermissionIds parseResult

            graceIds.OwnerId |> should equal ownerId

            graceIds.OrganizationId
            |> should equal requestedOrganizationId

            graceIds.RepositoryId
            |> should equal RepositoryId.Empty

            graceIds.BranchId |> should equal BranchId.Empty
            graceIds.HasRepository |> should equal false
            graceIds.HasBranch |> should equal false)

    /// Verifies that authz can explicit repository scope does not include configured branch.
    [<Test>]
    let ``authz can explicit repository scope does not include configured branch`` () =
        withTempDir (fun root ->
            let ownerId = Guid.Parse("11111111-1111-1111-1111-111111111111")
            let organizationId = Guid.Parse("22222222-2222-2222-2222-222222222222")
            let configuredRepositoryId = Guid.Parse("33333333-3333-3333-3333-333333333333")
            let configuredBranchId = Guid.Parse("44444444-4444-4444-4444-444444444444")
            let requestedRepositoryId = Guid.Parse("55555555-5555-5555-5555-555555555555")

            writeValidConfig root ownerId organizationId configuredRepositoryId configuredBranchId

            let parseResult =
                GraceCommand.rootCommand.Parse(
                    [|
                        "authz"
                        "can"
                        "read"
                        "branch"
                        "--repository-id"
                        requestedRepositoryId.ToString()
                    |]
                )

            parseResult.Errors.Count |> should equal 0

            let graceIds = Command.Access.normalizeCanPermissionIds parseResult

            graceIds.OwnerId |> should equal ownerId

            graceIds.OrganizationId
            |> should equal organizationId

            graceIds.RepositoryId
            |> should equal requestedRepositoryId

            graceIds.BranchId |> should equal BranchId.Empty
            graceIds.HasBranch |> should equal false)

    /// Verifies that authz can explicit branch scope keeps configured parent ids.
    [<Test>]
    let ``authz can explicit branch scope keeps configured parent ids`` () =
        withTempDir (fun root ->
            let ownerId = Guid.Parse("11111111-1111-1111-1111-111111111111")
            let organizationId = Guid.Parse("22222222-2222-2222-2222-222222222222")
            let repositoryId = Guid.Parse("33333333-3333-3333-3333-333333333333")
            let configuredBranchId = Guid.Parse("44444444-4444-4444-4444-444444444444")
            let requestedBranchId = Guid.Parse("88888888-8888-8888-8888-888888888888")

            writeValidConfig root ownerId organizationId repositoryId configuredBranchId

            let parseResult =
                GraceCommand.rootCommand.Parse(
                    [|
                        "authz"
                        "can"
                        "read"
                        "branch"
                        "--branch-id"
                        requestedBranchId.ToString()
                    |]
                )

            parseResult.Errors.Count |> should equal 0

            let graceIds = Command.Access.normalizeCanPermissionIds parseResult

            graceIds.OwnerId |> should equal ownerId

            graceIds.OrganizationId
            |> should equal organizationId

            graceIds.RepositoryId |> should equal repositoryId

            graceIds.BranchId
            |> should equal requestedBranchId)

    /// Verifies that authz can config only branch scope keeps configured branch id.
    [<Test>]
    let ``authz can config-only branch scope keeps configured branch id`` () =
        withTempDir (fun root ->
            let ownerId = Guid.Parse("11111111-1111-1111-1111-111111111111")
            let organizationId = Guid.Parse("22222222-2222-2222-2222-222222222222")
            let repositoryId = Guid.Parse("33333333-3333-3333-3333-333333333333")
            let branchId = Guid.Parse("44444444-4444-4444-4444-444444444444")

            writeValidConfig root ownerId organizationId repositoryId branchId

            let parseResult = GraceCommand.rootCommand.Parse([| "authz"; "can"; "read"; "branch" |])

            parseResult.Errors.Count |> should equal 0

            let graceIds = Command.Access.normalizeCanPermissionIds parseResult

            graceIds.OwnerId |> should equal ownerId

            graceIds.OrganizationId
            |> should equal organizationId

            graceIds.RepositoryId |> should equal repositoryId
            graceIds.BranchId |> should equal branchId)

    /// Adds lifecycle headers to the test fixture.
    let private addLifecycleHeaders
        (response: HttpResponseMessage)
        (status: string)
        (unsupportedAfter: string)
        (minimumVersion: string)
        (recommendedVersion: string option)
        (updateUrl: string)
        =
        response.Headers.TryAddWithoutValidation(Constants.SdkLifecycleStatusHeaderKey, status)
        |> ignore

        response.Headers.TryAddWithoutValidation(Constants.SdkLifecycleUnsupportedAfterHeaderKey, unsupportedAfter)
        |> ignore

        response.Headers.TryAddWithoutValidation(Constants.SdkLifecycleMinimumVersionHeaderKey, minimumVersion)
        |> ignore

        match recommendedVersion with
        | Some value ->
            response.Headers.TryAddWithoutValidation(Constants.SdkLifecycleRecommendedVersionHeaderKey, value)
            |> ignore
        | None -> ()

        response.Headers.TryAddWithoutValidation(Constants.SdkLifecycleUpdateUrlHeaderKey, updateUrl)
        |> ignore

    /// Builds lifecycle properties test data used to exercise CLI program behavior.
    let private lifecycleProperties status =
        use response = new HttpResponseMessage(HttpStatusCode.OK)

        addLifecycleHeaders response status "2026-12-01" "0.1.0" (Some "0.2.0") "https://github.com/ScottArbeit/Grace/releases"

        match ClientIdentity.parseLifecycleDiagnostics response with
        | Some diagnostics -> ClientIdentity.lifecycleDiagnosticsToProperties diagnostics
        | None -> Dictionary<string, obj>()

    /// Verifies that help works with invalid config.
    [<Test>]
    let ``help works with invalid config`` () =
        withTempDir (fun root ->
            writeInvalidConfig root

            /// Verifies that the CLI program scenario exits with the expected process status.
            let exitCode, _ =
                runWithCapturedOutput [| "authorize"
                                         "grant-role"
                                         "-h" |]

            exitCode |> should equal 0)

    /// Verifies that help works without config.
    [<Test>]
    let ``help works without config`` () =
        withTempDir (fun _ ->
            /// Verifies that the CLI program scenario exits with the expected process status.
            let exitCode, _ =
                runWithCapturedOutput [| "authorize"
                                         "grant-role"
                                         "-h" |]

            exitCode |> should equal 0)

    /// Verifies that help shows symbolic defaults.
    [<Test>]
    let ``help shows symbolic defaults`` () =
        withTempDir (fun _ ->
            /// Verifies that the CLI program scenario exits with the expected process status.
            let exitCode, output =
                runWithCapturedOutput [| "authorize"
                                         "grant-role"
                                         "-h" |]

            exitCode |> should equal 0

            output
            |> should contain "[default: current OwnerId]"

            output
            |> should contain "[default: current OrganizationId]"

            output
            |> should contain "[default: current RepositoryId]"

            output
            |> should contain "[default: current BranchId]"

            output |> should contain "[default: new NanoId]")

    /// Verifies that create help rewrites empty guid defaults.
    [<Test>]
    let ``create help rewrites empty guid defaults`` () =
        withTempDir (fun _ ->
            /// Verifies that the CLI program scenario exits with the expected process status.
            let exitCode, output =
                runWithCapturedOutput [| "repository"
                                         "create"
                                         "-h" |]

            exitCode |> should equal 0

            output
            |> should contain "[default: current OwnerId]"

            output
            |> should contain "[default: current OrganizationId]"

            output |> should contain "[default: new Guid]"

            output
            |> should not' (contain "00000000-0000-0000-0000-0000000000000"))

    /// Verifies that schema emits registry derived json without requiring config.
    [<Test>]
    let ``schema emits registry-derived json without requiring config`` () =
        withTempDir (fun root ->
            /// Verifies that the CLI program scenario exits with the expected process status.
            let exitCode, output =
                runWithCapturedOutput [| "repository"
                                         "init"
                                         "--schema" |]

            exitCode |> should equal 0

            use document = JsonDocument.Parse(output)
            let rootElement = document.RootElement

            rootElement.GetProperty("Kind").GetString()
            |> should equal "schema"

            rootElement
                .GetProperty("ContractVersion")
                .GetString()
            |> should equal "cli-json-v1"

            rootElement
                .GetProperty("Command")
                .GetProperty("Id")
                .GetString()
            |> should equal "repository.init"

            rootElement
                .GetProperty("Registry")
                .GetProperty("CurrentJsonBehavior")
                .GetString()
            |> should equal "CommonRenderOutputEnvelope"

            rootElement
                .GetProperty("Schema")
                .GetProperty("Status")
                .GetString()
            |> should equal "metadata-incomplete"

            rootElement.GetProperty("Schema").GetProperty(
                "SuccessSchema"
            )
                .GetProperty(
                "properties"
            )
                .GetProperty(
                "ReturnValue"
            )
                .ValueKind
            |> should equal JsonValueKind.Object

            Directory.Exists(Path.Combine(root, ".grace"))
            |> should equal false)

    /// Verifies that examples emit registry derived json and ignore output mode.
    [<Test>]
    let ``examples emit registry-derived json and ignore output mode`` () =
        withTempDir (fun _ ->
            /// Verifies that the CLI program scenario exits with the expected process status.
            let exitCode, output =
                runWithCapturedOutput [| "--output"
                                         "Json"
                                         "authenticate"
                                         "logout"
                                         "--examples" |]

            exitCode |> should equal 0

            use document = parseJsonOutput output
            let rootElement = document.RootElement

            rootElement.GetProperty("Kind").GetString()
            |> should equal "examples"

            rootElement
                .GetProperty("Command")
                .GetProperty("Id")
                .GetString()
            |> should equal "authenticate.logout"

            let examples = rootElement.GetProperty("Examples")
            examples.GetArrayLength() |> should equal 2

            examples[0]
                .GetProperty("Document")
                .GetProperty("ReturnValue")
                .GetString()
            |> should equal "Signed out.")

    /// Verifies that root login alias emits schema introspection.
    [<Test>]
    let ``root login alias emits schema introspection`` () =
        withTempDir (fun _ ->
            /// Verifies that the CLI program scenario exits with the expected process status.
            let exitCode, output =
                runWithCapturedOutput [| "login"
                                         "--schema" |]

            exitCode |> should equal 0

            use document = parseJsonOutput output
            let rootElement = document.RootElement

            rootElement.GetProperty("Kind").GetString()
            |> should equal "schema"

            rootElement
                .GetProperty("Command")
                .GetProperty("Id")
                .GetString()
            |> should equal "authenticate.login")

    /// Verifies that root login alias preserves preceding global options for schema introspection.
    [<Test>]
    let ``root login alias preserves preceding global options for schema introspection`` () =
        withTempDir (fun _ ->
            /// Verifies that the CLI program scenario exits with the expected process status.
            let exitCode, output =
                runWithCapturedOutput [| "--output"
                                         "Json"
                                         "login"
                                         "--schema" |]

            exitCode |> should equal 0

            use document = parseJsonOutput output
            let rootElement = document.RootElement

            rootElement.GetProperty("Kind").GetString()
            |> should equal "schema"

            rootElement
                .GetProperty("Command")
                .GetProperty("Id")
                .GetString()
            |> should equal "authenticate.login")

    /// Verifies that root logout alias preserves preceding global options for examples introspection.
    [<Test>]
    let ``root logout alias preserves preceding global options for examples introspection`` () =
        withTempDir (fun _ ->
            /// Verifies that the CLI program scenario exits with the expected process status.
            let exitCode, output =
                runWithCapturedOutput [| "--output"
                                         "Json"
                                         "logout"
                                         "--examples" |]

            exitCode |> should equal 0

            use document = parseJsonOutput output
            let rootElement = document.RootElement

            rootElement.GetProperty("Kind").GetString()
            |> should equal "examples"

            rootElement
                .GetProperty("Command")
                .GetProperty("Id")
                .GetString()
            |> should equal "authenticate.logout")

    /// Verifies that examples for missing dto metadata emit explicit unsupported document.
    [<Test>]
    let ``examples for missing dto metadata emit explicit unsupported document`` () =
        withTempDir (fun _ ->
            /// Verifies that the CLI program scenario exits with the expected process status.
            let exitCode, output =
                runWithCapturedOutput [| "workitem"
                                         "show"
                                         "--examples" |]

            exitCode |> should equal 0

            use document = parseJsonOutput output
            let rootElement = document.RootElement

            rootElement.GetProperty("Kind").GetString()
            |> should equal "examples"

            let examples = rootElement.GetProperty("Examples")

            examples[ 0 ].GetProperty("Name").GetString()
            |> should equal "metadata-incomplete"

            examples[0]
                .GetProperty("Document")
                .GetProperty("Status")
                .GetString()
            |> should equal "metadata-incomplete"

            examples[0]
                .GetProperty("Document")
                .GetProperty("CommandId")
                .GetString()
            |> should equal "workitem.show")

    /// Verifies that nested command schema resolves full command id.
    [<Test>]
    let ``nested command schema resolves full command id`` () =
        withTempDir (fun _ ->
            /// Verifies that the CLI program scenario exits with the expected process status.
            let exitCode, output =
                runWithCapturedOutput [| "workitem"
                                         "attach"
                                         "summary"
                                         "--schema" |]

            exitCode |> should equal 0

            use document = parseJsonOutput output
            let rootElement = document.RootElement

            rootElement.GetProperty("Kind").GetString()
            |> should equal "schema"

            rootElement
                .GetProperty("Command")
                .GetProperty("Id")
                .GetString()
            |> should equal "workitem.attach.summary")

    /// Verifies that root output json is honored for nested commands before config errors.
    [<Test>]
    let ``root output json is honored for nested commands before config errors`` () =
        let cases =
            [
                [| "agent"; "work"; "status" |]
                [| "approval"; "policy"; "list" |]
                [|
                    "webhook"
                    "delivery"
                    "show"
                    "--delivery"
                    "11111111-1111-1111-1111-111111111111"
                |]
                [|
                    "workitem"
                    "attach"
                    "summary"
                    "123"
                    "--text"
                    "summary text"
                |]
            ]

        for commandArgs in cases do
            withTempDir (fun _ ->
                let args = Array.append [| "--output"; "Json" |] commandArgs
                /// Verifies that the CLI program scenario exits with the expected process status.
                let exitCode, standardOut, standardError = runWithCapturedStdoutAndStderr args

                exitCode |> should equal -1
                standardError |> should equal String.Empty

                use document = parseJsonOutput standardOut
                let rootElement = document.RootElement

                rootElement.GetProperty("Error").GetString()
                |> should contain "graceconfig.json"

                standardOut |> should not' (contain "Elapsed:")

                standardOut
                |> should not' (contain "Grace Version Control System"))

    /// Verifies that root schema emits json parse error envelope.
    [<Test>]
    let ``root schema emits json parse error envelope`` () =
        withTempDir (fun _ ->
            /// Verifies that the CLI program scenario exits with the expected process status.
            let exitCode, output = runWithCapturedOutput [| "--schema" |]

            exitCode |> should equal -1

            use document = parseJsonOutput output
            let rootElement = document.RootElement

            rootElement.GetProperty("Error").GetString()
            |> should equal "Required command was not provided."

            rootElement
                .GetProperty("CorrelationId")
                .GetString()
            |> should not' (equal String.Empty))

    /// Verifies that schema and examples together emit json error envelope.
    [<Test>]
    let ``schema and examples together emit json error envelope`` () =
        withTempDir (fun _ ->
            /// Verifies that the CLI program scenario exits with the expected process status.
            let exitCode, output =
                runWithCapturedOutput [| "repository"
                                         "init"
                                         "--schema"
                                         "--examples" |]

            exitCode |> should equal -1

            use document = parseJsonOutput output
            let rootElement = document.RootElement

            rootElement.GetProperty("Error").GetString()
            |> should equal "--schema and --examples cannot be used together.")

    /// Verifies that schema with unknown option emits json parse error envelope.
    [<Test>]
    let ``schema with unknown option emits json parse error envelope`` () =
        withTempDir (fun _ ->
            /// Verifies that the CLI program scenario exits with the expected process status.
            let exitCode, output =
                runWithCapturedOutput [| "repository"
                                         "init"
                                         "--schema"
                                         "--definitely-not-an-option" |]

            exitCode |> should equal -1

            use document = parseJsonOutput output
            let rootElement = document.RootElement

            rootElement.GetProperty("Error").GetString()
            |> should contain "Unrecognized command or argument")

    /// Verifies that schema with invalid output emits json parse error envelope.
    [<Test>]
    let ``schema with invalid output emits json parse error envelope`` () =
        withTempDir (fun _ ->
            /// Verifies that the CLI program scenario exits with the expected process status.
            let exitCode, output =
                runWithCapturedOutput [| "--output"
                                         "Bogus"
                                         "repository"
                                         "init"
                                         "--schema" |]

            exitCode |> should equal -1

            use document = parseJsonOutput output
            let rootElement = document.RootElement

            rootElement.GetProperty("Error").GetString()
            |> should contain "Bogus")

    /// Verifies that render output json success writes one stdout document and no stderr.
    [<Test>]
    let ``renderOutput json success writes one stdout document and no stderr`` () =
        let parseResult = GraceCommand.rootCommand.Parse([| "--output"; "Json" |])
        let returnValue = GraceReturnValue.Create {| Value = "ok" |} "corr-json-success"

        /// Builds standard out test data used to exercise CLI program behavior.
        let standardOut, standardError =
            captureStdoutAndStderr (fun () ->
                Common.renderOutput parseResult (Ok returnValue)
                |> should equal 0)

        standardError |> should equal String.Empty

        use document = parseJsonOutput standardOut
        let rootElement = document.RootElement

        rootElement
            .GetProperty("ReturnValue")
            .GetProperty("Value")
            .GetString()
        |> should equal "ok"

        rootElement
            .GetProperty("CorrelationId")
            .GetString()
        |> should equal "corr-json-success"

        standardOut |> should not' (contain "Elapsed:")

    /// Verifies that alias list json emits grace envelope.
    [<Test>]
    let ``alias list json emits Grace envelope`` () =
        /// Verifies that the CLI program scenario exits with the expected process status.
        let exitCode, output =
            runWithCapturedOutput [| "--output"
                                     "Json"
                                     "alias"
                                     "list" |]

        exitCode |> should equal 0

        let rootElement = assertGraceEnvelope output
        let returnValue = rootElement.GetProperty("ReturnValue")

        returnValue.GetProperty("Count").GetInt32()
        |> should be (greaterThan 0)

        returnValue.GetProperty("Aliases").ValueKind
        |> should equal JsonValueKind.Array

        output |> should not' (contain "Grace command")

    /// Verifies that history show json emits grace envelope.
    [<Test>]
    let ``history show json emits Grace envelope`` () =
        /// Verifies that the CLI program scenario exits with the expected process status.
        let exitCode, output =
            runWithCapturedOutput [| "--output"
                                     "Json"
                                     "history"
                                     "show"
                                     "--limit"
                                     "0" |]

        exitCode |> should equal 0

        let rootElement = assertGraceEnvelope output
        let returnValue = rootElement.GetProperty("ReturnValue")

        returnValue.GetProperty("Entries").ValueKind
        |> should equal JsonValueKind.Array

        returnValue.GetProperty("Count").GetInt32()
        |> should be (greaterThanOrEqualTo 0)

    /// Verifies that history show json select failure returns nonzero error envelope.
    [<Test>]
    let ``history show json select failure returns nonzero error envelope`` () =
        /// Verifies that the CLI program scenario exits with the expected process status.
        let exitCode, standardOut, standardError =
            runWithCapturedStdoutAndStderr [| "--output"
                                              "Json"
                                              "history"
                                              "show"
                                              "--limit"
                                              "0"
                                              "--select"
                                              "NonExistent" |]

        exitCode |> should equal -1

        let error = assertGraceErrorEnvelopeOnCleanStreams standardOut standardError

        error
        |> should contain "--select path 'NonExistent' was not found"

    /// Verifies that history search json emits grace envelope.
    [<Test>]
    let ``history search json emits Grace envelope`` () =
        /// Verifies that the CLI program scenario exits with the expected process status.
        let exitCode, output =
            runWithCapturedOutput [| "--output"
                                     "Json"
                                     "history"
                                     "search"
                                     "workitem"
                                     "--limit"
                                     "0" |]

        exitCode |> should equal 0

        let rootElement = assertGraceEnvelope output

        rootElement.GetProperty("ReturnValue").GetProperty(
            "Entries"
        )
            .ValueKind
        |> should equal JsonValueKind.Array

    /// Verifies that history search json select failure returns nonzero error envelope.
    [<Test>]
    let ``history search json select failure returns nonzero error envelope`` () =
        /// Verifies that the CLI program scenario exits with the expected process status.
        let exitCode, standardOut, standardError =
            runWithCapturedStdoutAndStderr [| "--output"
                                              "Json"
                                              "history"
                                              "search"
                                              "workitem"
                                              "--limit"
                                              "0"
                                              "--select"
                                              "NonExistent" |]

        exitCode |> should equal -1

        let error = assertGraceErrorEnvelopeOnCleanStreams standardOut standardError

        error
        |> should contain "--select path 'NonExistent' was not found"

    /// Verifies that history validation error json emits grace error envelope.
    [<Test>]
    let ``history validation error json emits Grace error envelope`` () =
        /// Verifies that the CLI program scenario exits with the expected process status.
        let exitCode, output =
            runWithCapturedOutput [| "--output"
                                     "Json"
                                     "history"
                                     "show"
                                     "--limit"
                                     "-1" |]

        exitCode |> should equal -1

        use document = parseJsonOutput output

        document
            .RootElement
            .GetProperty("Error")
            .GetString()
        |> should equal "Limit must be positive."

    /// Verifies that history on off and delete json emit grace envelopes.
    [<Test>]
    let ``history on off and delete json emit Grace envelopes`` () =
        let userConfigPath = UserConfiguration.getUserConfigurationPath ()
        let historyPath = HistoryStorage.getHistoryFilePath ()

        withFileBackup userConfigPath (fun () ->
            withFileBackup historyPath (fun () ->
                /// Builds on exit code test data used to exercise CLI program behavior.
                let onExitCode, onOutput =
                    runWithCapturedOutput [| "--output"
                                             "Json"
                                             "history"
                                             "on" |]

                onExitCode |> should equal 0
                assertGraceEnvelope onOutput |> ignore

                /// Builds off exit code test data used to exercise CLI program behavior.
                let offExitCode, offOutput =
                    runWithCapturedOutput [| "--output"
                                             "Json"
                                             "history"
                                             "off" |]

                offExitCode |> should equal 0
                assertGraceEnvelope offOutput |> ignore

                File.WriteAllText(historyPath, String.Empty)

                /// Builds delete exit code test data used to exercise CLI program behavior.
                let deleteExitCode, deleteOutput =
                    runWithCapturedOutput [| "--output"
                                             "Json"
                                             "history"
                                             "delete" |]

                deleteExitCode |> should equal 0
                assertGraceEnvelope deleteOutput |> ignore))

    /// Verifies that connect json validation error emits clean grace envelope.
    [<Test>]
    let ``connect json validation error emits clean Grace envelope`` () =
        withTempDir (fun root ->
            writeValidConfigWithDeterministicIds root

            /// Verifies that the CLI program scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "Json"
                                                  "connect"
                                                  "owner/repository"
                                                  "--owner-name"
                                                  "owner" |]

            exitCode |> should equal -1

            assertGraceErrorEnvelopeOnCleanStreams standardOut standardError
            |> should contain "Repository shortcut must be in the form")

    /// Verifies that directory version get zip file json validation error emits clean grace envelope.
    [<Test>]
    let ``directory version get zip file json validation error emits clean Grace envelope`` () =
        withTempDir (fun _ ->
            /// Verifies that the CLI program scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "Json"
                                                  "directory-version"
                                                  "get-zip-file"
                                                  "--directory-version-id"
                                                  $"{Guid.Empty}" |]

            exitCode |> should equal -1

            assertGraceErrorEnvelopeOnCleanStreams standardOut standardError
            |> should contain "graceconfig.json")

    /// Verifies that repository init json invalid directory emits clean grace envelope.
    [<Test>]
    let ``repository init json invalid directory emits clean Grace envelope`` () =
        withTempDir (fun root ->
            writeValidConfigWithDeterministicIds root
            let missingDirectory = Path.Combine(root, "missing")

            /// Verifies that the CLI program scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "Json"
                                                  "repository"
                                                  "init"
                                                  "--directory"
                                                  missingDirectory |]

            exitCode |> should equal -1

            assertGraceErrorEnvelopeOnCleanStreams standardOut standardError
            |> should contain "directory")

    /// Verifies that repository init no output dto renders absent observability as null.
    [<Test>]
    let ``repository init no output DTO renders absent observability as null`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                [|
                    "--output"
                    "Json"
                    "repository"
                    "init"
                    "--no-output"
                |]
            )

        let output: Common.LocalOutputDto.RepositoryInitDto =
            { Message = "Initialized repository."; DirectoryCount = None; FileCount = None; TotalFileSize = None; RootSha256Hash = None }

        /// Builds standard out test data used to exercise CLI program behavior.
        let standardOut, standardError =
            captureStdoutAndStderr (fun () ->
                Common.renderOutput parseResult (Ok(GraceReturnValue.Create output "corr-repository-init"))
                |> should equal 0)

        standardError |> should equal String.Empty

        let rootElement = assertGraceEnvelope standardOut
        let returnValue = rootElement.GetProperty("ReturnValue")

        returnValue.GetProperty("Message").GetString()
        |> should equal "Initialized repository."

        returnValue
            .GetProperty(
                "DirectoryCount"
            )
            .ValueKind
        |> should equal JsonValueKind.Null

        returnValue.GetProperty("FileCount").ValueKind
        |> should equal JsonValueKind.Null

        returnValue.GetProperty("TotalFileSize").ValueKind
        |> should equal JsonValueKind.Null

        returnValue
            .GetProperty(
                "RootSha256Hash"
            )
            .ValueKind
        |> should equal JsonValueKind.Null

    /// Verifies that review report show json validation error emits clean grace envelope.
    [<Test>]
    let ``review report show json validation error emits clean Grace envelope`` () =
        withTempDir (fun root ->
            writeValidConfigWithDeterministicIds root

            /// Verifies that the CLI program scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "Json"
                                                  "review"
                                                  "report"
                                                  "show"
                                                  "--candidate"
                                                  "not-a-guid" |]

            exitCode |> should equal -1

            assertGraceErrorEnvelopeOnCleanStreams standardOut standardError
            |> should contain "CandidateId must be a valid non-empty Guid")

    /// Verifies that review report export json validation error emits clean grace envelope.
    [<Test>]
    let ``review report export json validation error emits clean Grace envelope`` () =
        withTempDir (fun root ->
            writeValidConfigWithDeterministicIds root

            /// Verifies that the CLI program scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "Json"
                                                  "review"
                                                  "report"
                                                  "export"
                                                  "--candidate"
                                                  "not-a-guid"
                                                  "--format"
                                                  "markdown"
                                                  "--output-file"
                                                  "report.md" |]

            exitCode |> should equal -1

            assertGraceErrorEnvelopeOnCleanStreams standardOut standardError
            |> should contain "CandidateId must be a valid non-empty Guid")

    /// Verifies that workitem attachments download json validation error emits clean grace envelope.
    [<Test>]
    let ``workitem attachments download json validation error emits clean Grace envelope`` () =
        withTempDir (fun root ->
            writeValidConfigWithDeterministicIds root

            /// Verifies that the CLI program scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "Json"
                                                  "workitem"
                                                  "attachments"
                                                  "download"
                                                  "not-a-work-item"
                                                  "--artifact-id"
                                                  $"{Guid.Empty}"
                                                  "--output-file"
                                                  "attachment.bin" |]

            exitCode |> should equal -1

            assertGraceErrorEnvelopeOnCleanStreams standardOut standardError
            |> should contain "work item ID is invalid")

    /// Verifies that select option makes command json oriented.
    [<Test>]
    let ``select option makes command json-oriented`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                [|
                    "authenticate"
                    "logout"
                    "--select"
                    "Value"
                |]
            )

        parseResult.Errors.Count |> should equal 0

        Common.json parseResult |> should equal true

        Common.tryGetSelect parseResult
        |> should equal (Some "Value")

    /// Verifies that render output select writes selected scalar json only.
    [<Test>]
    let ``renderOutput select writes selected scalar json only`` () =
        let parseResult = GraceCommand.rootCommand.Parse([| "--select"; "Value" |])
        let returnValue = GraceReturnValue.Create {| Value = "ok"; Other = "hidden" |} "corr-select-success"

        /// Builds standard out test data used to exercise CLI program behavior.
        let standardOut, standardError =
            captureStdoutAndStderr (fun () ->
                Common.renderOutput parseResult (Ok returnValue)
                |> should equal 0)

        standardError |> should equal String.Empty
        standardOut.Trim() |> should equal "\"ok\""

    /// Verifies that render output select success suppresses lifecycle warnings.
    [<Test>]
    let ``renderOutput select success suppresses lifecycle warnings`` () =
        let parseResult = GraceCommand.rootCommand.Parse([| "--select"; "Value" |])
        let returnValue = GraceReturnValue.Create {| Value = "ok" |} "corr-select-lifecycle-success"

        returnValue.enhance (lifecycleProperties "deprecated")
        |> ignore

        /// Builds standard out test data used to exercise CLI program behavior.
        let standardOut, standardError =
            captureStdoutAndStderr (fun () ->
                Common.renderOutput parseResult (Ok returnValue)
                |> should equal 0)

        standardError |> should equal String.Empty
        standardOut.Trim() |> should equal "\"ok\""

        standardOut
        |> should not' (contain "This Grace client version is deprecated.")

    /// Verifies that render output select writes selected object and collection json.
    [<Test>]
    let ``renderOutput select writes selected object and collection json`` () =
        let parseResult = GraceCommand.rootCommand.Parse([| "--select"; "Container.Items" |])

        let returnValue =
            GraceReturnValue.Create
                {|
                    Container =
                        {|
                            Items =
                                [|
                                    {| Name = "one" |}
                                    {| Name = "two" |}
                                |]
                        |}
                |}
                "corr-select-collection"

        /// Builds standard out test data used to exercise CLI program behavior.
        let standardOut, standardError =
            captureStdoutAndStderr (fun () ->
                Common.renderOutput parseResult (Ok returnValue)
                |> should equal 0)

        standardError |> should equal String.Empty

        use document = JsonDocument.Parse(standardOut)
        let rootElement = document.RootElement

        rootElement.ValueKind
        |> should equal JsonValueKind.Array

        rootElement.GetArrayLength() |> should equal 2

        rootElement[ 1 ].GetProperty("Name").GetString()
        |> should equal "two"

    /// Verifies that render output select writes null json.
    [<Test>]
    let ``renderOutput select writes null json`` () =
        let parseResult = GraceCommand.rootCommand.Parse([| "--select"; "Value" |])
        let returnValue = GraceReturnValue.Create {| Value = (null: string) |} "corr-select-null"

        /// Builds standard out test data used to exercise CLI program behavior.
        let standardOut, standardError =
            captureStdoutAndStderr (fun () ->
                Common.renderOutput parseResult (Ok returnValue)
                |> should equal 0)

        standardError |> should equal String.Empty
        standardOut.Trim() |> should equal "null"

    /// Verifies that render output select missing property emits json error.
    [<Test>]
    let ``renderOutput select missing property emits json error`` () =
        let parseResult = GraceCommand.rootCommand.Parse([| "--select"; "Missing" |])
        let returnValue = GraceReturnValue.Create {| Value = "ok" |} "corr-select-missing"

        /// Builds standard out test data used to exercise CLI program behavior.
        let standardOut, standardError =
            captureStdoutAndStderr (fun () ->
                Common.renderOutput parseResult (Ok returnValue)
                |> should equal -1)

        standardError |> should equal String.Empty

        use document = parseJsonOutput standardOut
        let rootElement = document.RootElement

        rootElement.GetProperty("Error").GetString()
        |> should contain "was not found in ReturnValue"

        rootElement
            .GetProperty("CorrelationId")
            .GetString()
        |> should equal "corr-select-missing"

    /// Verifies that render output select scalar traversal emits json error.
    [<Test>]
    let ``renderOutput select scalar traversal emits json error`` () =
        let parseResult = GraceCommand.rootCommand.Parse([| "--select"; "Value.Name" |])
        let returnValue = GraceReturnValue.Create {| Value = "ok" |} "corr-select-scalar"

        /// Builds standard out test data used to exercise CLI program behavior.
        let standardOut, standardError =
            captureStdoutAndStderr (fun () ->
                Common.renderOutput parseResult (Ok returnValue)
                |> should equal -1)

        standardError |> should equal String.Empty

        assertJsonErrorOutput standardOut
        |> should contain "cannot read 'Name'"

    /// Verifies that render output select leaves error envelope unprojected.
    [<Test>]
    let ``renderOutput select leaves error envelope unprojected`` () =
        let parseResult = GraceCommand.rootCommand.Parse([| "--select"; "Value" |])
        let error = GraceError.Create "original failure" "corr-select-error"

        error.enhance (lifecycleProperties "unsupported")
        |> ignore

        /// Builds standard out test data used to exercise CLI program behavior.
        let standardOut, standardError =
            captureStdoutAndStderr (fun () ->
                Common.renderOutput parseResult (Error error)
                |> should equal -1)

        standardError |> should equal String.Empty

        use document = parseJsonOutput standardOut
        let rootElement = document.RootElement

        rootElement.GetProperty("Error").GetString()
        |> should equal "original failure"

        standardOut
        |> should not' (contain "This Grace client version is no longer supported.")

        /// Tracks return Value changes so this scenario can assert the resulting side effect explicitly.
        let mutable returnValue = Unchecked.defaultof<JsonElement>

        rootElement.TryGetProperty("ReturnValue", &returnValue)
        |> should equal false

    /// Verifies that invalid select grammar does not invoke command.
    [<Test>]
    let ``invalid select grammar does not invoke command`` () =
        withTempDir (fun root ->
            /// Verifies that the CLI program scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "connect"
                                                  "--select"
                                                  "Value[0]" |]

            exitCode |> should equal -1
            standardError |> should equal String.Empty

            assertJsonErrorOutput standardOut
            |> should contain "predicates, wildcards, functions, and expressions are not supported"

            File.Exists(Path.Combine(root, ".grace", "graceconfig.json"))
            |> should equal false)

    /// Verifies that valid select on unsupported command is json error without invoking command.
    [<Test>]
    let ``valid select on unsupported command is json error without invoking command`` () =
        withTempDir (fun root ->
            /// Verifies that the CLI program scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "history"
                                                  "run"
                                                  "--select"
                                                  "Value" |]

            exitCode |> should equal -1
            standardError |> should equal String.Empty

            assertJsonErrorOutput standardOut
            |> should contain "does not support --select in this release"

            File.Exists(Path.Combine(root, ".grace", "graceconfig.json"))
            |> should equal false)

    /// Verifies that render output json error writes grace error envelope with shared field names.
    [<Test>]
    let ``renderOutput json error writes GraceError envelope with shared field names`` () =
        let parseResult = GraceCommand.rootCommand.Parse([| "--output"; "Json" |])
        let error = GraceError.Create "central writer error" "corr-json-error"

        /// Builds standard out test data used to exercise CLI program behavior.
        let standardOut, standardError =
            captureStdoutAndStderr (fun () ->
                Common.renderOutput parseResult (Error error)
                |> should equal -1)

        standardError |> should equal String.Empty

        use document = parseJsonOutput standardOut
        let rootElement = document.RootElement

        rootElement.GetProperty("Error").GetString()
        |> should equal "central writer error"

        rootElement
            .GetProperty("CorrelationId")
            .GetString()
        |> should equal "corr-json-error"

        /// Tracks camel Case Correlation Id changes so this scenario can assert the resulting side effect explicitly.
        let mutable camelCaseCorrelationId = Unchecked.defaultof<JsonElement>

        rootElement.TryGetProperty("correlationId", &camelCaseCorrelationId)
        |> should equal false

        Constants.JsonSerializerOptions.PropertyNamingPolicy
        |> should equal null

    /// Verifies that missing config in json mode emits one error document on stdout.
    [<Test>]
    let ``missing config in json mode emits one error document on stdout`` () =
        withTempDir (fun _ ->
            /// Verifies that the CLI program scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "Json"
                                                  "branch"
                                                  "get" |]

            exitCode |> should equal -1
            standardError |> should equal String.Empty

            use document = parseJsonOutput standardOut
            let rootElement = document.RootElement

            rootElement.GetProperty("Error").GetString()
            |> should contain "graceconfig.json"

            standardOut |> should not' (contain "Elapsed:"))

    /// Verifies that missing config in json equals mode emits one error document on stdout.
    [<Test>]
    let ``missing config in json equals mode emits one error document on stdout`` () =
        withTempDir (fun _ ->
            /// Verifies that the CLI program scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output=Json"
                                                  "branch"
                                                  "get" |]

            exitCode |> should equal -1
            standardError |> should equal String.Empty

            assertJsonErrorOutput standardOut
            |> should contain "graceconfig.json")

    /// Verifies that missing config in mixed case json equals mode emits one error document on stdout.
    [<Test>]
    let ``missing config in mixed-case json equals mode emits one error document on stdout`` () =
        withTempDir (fun _ ->
            /// Verifies that the CLI program scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--OUTPUT=Json"
                                                  "branch"
                                                  "get" |]

            exitCode |> should equal -1
            standardError |> should equal String.Empty

            assertJsonErrorOutput standardOut
            |> should contain "graceconfig.json")

    /// Verifies that earlier non json output token does not mask later json intent.
    [<Test>]
    let ``earlier non-json output token does not mask later json intent`` () =
        withTempDir (fun _ ->
            /// Verifies that the CLI program scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "Normal"
                                                  "--output=Json"
                                                  "branch"
                                                  "get" |]

            exitCode |> should equal -1
            standardError |> should equal String.Empty

            assertJsonErrorOutput standardOut |> ignore)

    /// Verifies that malformed split output option does not mask later long json intent.
    [<Test>]
    let ``malformed split output option does not mask later long json intent`` () =
        withTempDir (fun _ ->
            /// Verifies that the CLI program scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "--output"
                                                  "Json"
                                                  "branch"
                                                  "get" |]

            exitCode |> should equal -1
            standardError |> should equal String.Empty

            assertJsonErrorOutput standardOut |> ignore)

    /// Verifies that malformed split output option does not mask later short json intent.
    [<Test>]
    let ``malformed split output option does not mask later short json intent`` () =
        withTempDir (fun _ ->
            /// Verifies that the CLI program scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "-o"
                                                  "-o"
                                                  "Json"
                                                  "branch"
                                                  "get" |]

            exitCode |> should equal -1
            standardError |> should equal String.Empty

            assertJsonErrorOutput standardOut |> ignore)

    /// Verifies that parse error in json mode emits one error document on stdout.
    [<Test>]
    let ``parse error in json mode emits one error document on stdout`` () =
        withTempDir (fun _ ->
            /// Verifies that the CLI program scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "Json"
                                                  "repository"
                                                  "init"
                                                  "--definitely-not-an-option" |]

            exitCode |> should equal -1
            standardError |> should equal String.Empty

            use document = parseJsonOutput standardOut
            let rootElement = document.RootElement

            rootElement.GetProperty("Error").GetString()
            |> should contain "Unrecognized command or argument")

    /// Verifies that parse error in json equals mode emits one error document on stdout.
    [<Test>]
    let ``parse error in json equals mode emits one error document on stdout`` () =
        withTempDir (fun _ ->
            /// Verifies that the CLI program scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output=Json"
                                                  "repository"
                                                  "init"
                                                  "--definitely-not-an-option" |]

            exitCode |> should equal -1
            standardError |> should equal String.Empty

            assertJsonErrorOutput standardOut
            |> should contain "Unrecognized command or argument")

    /// Verifies that short equals json output spelling emits json error envelope.
    [<Test>]
    let ``short equals json output spelling emits json error envelope`` () =
        withTempDir (fun _ ->
            /// Verifies that the CLI program scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "-o=Json"
                                                  "repository"
                                                  "init"
                                                  "--definitely-not-an-option" |]

            exitCode |> should equal -1
            standardError |> should equal String.Empty

            assertJsonErrorOutput standardOut |> ignore)

    /// Verifies that lowercase json value is rejected with json error envelope.
    [<Test>]
    let ``lowercase json value is rejected with json error envelope`` () =
        withTempDir (fun _ ->
            /// Verifies that the CLI program scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output=json"
                                                  "repository"
                                                  "init" |]

            exitCode |> should equal -1
            standardError |> should equal String.Empty

            assertJsonErrorOutput standardOut
            |> should contain "json")

    /// Verifies that catch all exception in json mode emits one error document on stdout.
    [<Test>]
    let ``catch all exception in json mode emits one error document on stdout`` () =
        withTempDir (fun root ->
            writeInvalidConfig root

            /// Verifies that the CLI program scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "Json"
                                                  "authorize"
                                                  "list-roles" |]

            exitCode |> should equal -1
            standardError |> should equal String.Empty

            use document = parseJsonOutput standardOut
            let rootElement = document.RootElement

            rootElement.GetProperty("Error").GetString()
            |> should not' (equal String.Empty)

            rootElement.GetProperty("Exception").ValueKind
            |> should equal JsonValueKind.Object

            rootElement
                .GetProperty("CorrelationId")
                .GetString()
            |> should not' (equal String.Empty)

            standardOut |> should not' (contain "Exception:"))

    /// Verifies that missing config in human mode remains human oriented.
    [<Test>]
    let ``missing config in human mode remains human oriented`` () =
        withTempDir (fun _ ->
            /// Verifies that the CLI program scenario exits with the expected process status.
            let exitCode, standardOut, _ =
                runWithCapturedStdoutAndStderr [| "branch"
                                                  "get" |]

            exitCode |> should equal -1

            standardOut
                .TrimStart()
                .StartsWith("{", StringComparison.Ordinal)
            |> should equal false

            standardOut |> should contain "graceconfig.json")

    /// Verifies that verbose parse result shows resolved ids.
    [<Test>]
    let ``verbose parse result shows resolved ids`` () =
        withTempDir (fun root ->
            let ownerId = Guid.NewGuid()
            let orgId = Guid.NewGuid()
            let repoId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            writeValidConfig root ownerId orgId repoId branchId

            let parseResult = GraceCommand.rootCommand.Parse([| "authorize"; "grant-role" |])

            let output = captureOutput (fun () -> Common.printParseResult parseResult)

            output |> should contain "Resolved values:"
            output |> should contain $"{ownerId}"
            output |> should contain $"{orgId}"
            output |> should contain $"{repoId}"
            output |> should contain $"{branchId}")

    /// Verifies that get normalized ids and names falls back to config ids.
    [<Test>]
    let ``getNormalizedIdsAndNames falls back to config ids`` () =
        withTempDir (fun root ->
            let ownerId = Guid.NewGuid()
            let orgId = Guid.NewGuid()
            let repoId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            writeValidConfig root ownerId orgId repoId branchId

            let parseResult = GraceCommand.rootCommand.Parse([| "authorize"; "grant-role" |])
            let graceIds = Services.getNormalizedIdsAndNames parseResult

            graceIds.OwnerId |> should equal ownerId
            graceIds.OrganizationId |> should equal orgId
            graceIds.RepositoryId |> should equal repoId
            graceIds.BranchId |> should equal branchId)


namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.Shared.Client
open NUnit.Framework
open Spectre.Console
open System
open System.IO

/// Groups root help grouping coverage for the CLI test project.
[<NonParallelizable>]
module RootHelpGroupingTests =
    /// Exercises private behavior.
    type private GroupedHelpExpectation = { Args: string array; Headings: string list }

    let private groupedHelpExpectations =
        [
            {
                Args = [| "repo"; "-h" |]
                Headings =
                    [
                        "Create and initialize:"
                        "Inspect:"
                        "Configuration:"
                        "Lifecycle:"
                    ]
            }
            {
                Args = [| "branch"; "-h" |]
                Headings =
                    [
                        "Create and contribute:"
                        "Promotion workflow:"
                        "Inspect:"
                        "Settings:"
                        "Lifecycle:"
                    ]
            }
            {
                Args = [| "owner"; "-h" |]
                Headings =
                    [
                        "Create and inspect:"
                        "Settings:"
                        "Lifecycle:"
                    ]
            }
            {
                Args = [| "org"; "-h" |]
                Headings =
                    [
                        "Create and inspect:"
                        "Settings:"
                        "Lifecycle:"
                    ]
            }
        ]

    /// Sets ansi console output needed by the test scenario.
    let private setAnsiConsoleOutput (writer: TextWriter) =
        let settings = AnsiConsoleSettings()
        settings.Out <- AnsiConsoleOutput(writer)
        AnsiConsole.Console <- AnsiConsole.Create(settings)

    /// Runs with captured output for test scenarios.
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

    /// Runs the supplied action with file backup applied.
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

    /// Runs the supplied action with grace user file backups applied.
    let private withGraceUserFileBackups (action: unit -> unit) =
        let configPath = UserConfiguration.getUserConfigurationPath ()
        let historyPath = HistoryStorage.getHistoryFilePath ()
        let lockPath = HistoryStorage.getHistoryLockPath ()

        withFileBackup configPath (fun () -> withFileBackup historyPath (fun () -> withFileBackup lockPath action))

    /// Builds slice between test data used to exercise CLI program behavior.
    let private sliceBetween (text: string) (startText: string) (endText: string) =
        let startIndex = text.IndexOf(startText, StringComparison.Ordinal)
        let endIndex = text.IndexOf(endText, StringComparison.Ordinal)

        if startIndex >= 0 && endIndex > startIndex then
            text.Substring(startIndex, endIndex - startIndex)
        else
            text

    /// Verifies that root help groups commands.
    [<Test>]
    let ``root help groups commands`` () =
        withGraceUserFileBackups (fun () ->
            /// Verifies that the CLI program scenario exits with the expected process status.
            let exitCode, output = runWithCapturedOutput [||]
            exitCode |> should equal 0

            output |> should contain "Getting started:"
            output |> should contain "Day-to-day development:"
            output |> should contain "Review and promotion:"

            output
            |> should contain "Administration and authorization:"

            output |> should contain "Local utilities:"

            let gettingStarted = sliceBetween output "Getting started:" "Day-to-day development:"

            gettingStarted |> should contain "authenticate"
            gettingStarted |> should contain "connect"
            gettingStarted |> should contain "config"

            gettingStarted
            |> should not' (contain $"{Environment.NewLine}    auth ")

            gettingStarted |> should not' (contain "branch")

            let dayToDay = sliceBetween output "Day-to-day development:" "Review and promotion:"

            dayToDay |> should contain "branch"
            dayToDay |> should contain "diff"
            dayToDay |> should contain "directory-version"
            dayToDay |> should contain "watch"
            dayToDay |> should not' (contain "work")

            let reviewAndPromotion = sliceBetween output "Review and promotion:" "Administration and authorization:"

            reviewAndPromotion |> should contain "workitem"
            reviewAndPromotion |> should contain "review"
            reviewAndPromotion |> should contain "candidate"
            reviewAndPromotion |> should contain "queue"
            reviewAndPromotion |> should contain "agent"
            reviewAndPromotion |> should contain "approval"
            reviewAndPromotion |> should contain "webhook"

            reviewAndPromotion
            |> should contain "promotion-set"

            let administration = sliceBetween output "Administration and authorization:" "Local utilities:"

            administration |> should contain "authorize"

            administration
            |> should not' (contain $"{Environment.NewLine}    access "))

    /// Verifies that subcommand help is not grouped.
    [<Test>]
    let ``subcommand help is not grouped`` () =
        withGraceUserFileBackups (fun () ->
            /// Verifies that the CLI program scenario exits with the expected process status.
            let exitCode, output =
                runWithCapturedOutput [| "branch"
                                         "-h" |]

            exitCode |> should equal 0
            output |> should not' (contain "Getting started:")

            output
            |> should not' (contain "Day-to-day development:")

            output
            |> should not' (contain "Review and promotion:")

            output
            |> should not' (contain "Administration and authorization:")

            output |> should not' (contain "Local utilities:"))

    /// Verifies that selected command helps are grouped.
    [<Test>]
    let ``selected command helps are grouped`` () =
        withGraceUserFileBackups (fun () ->
            for expectation in groupedHelpExpectations do
                /// Verifies that the CLI program scenario exits with the expected process status.
                let exitCode, output = runWithCapturedOutput expectation.Args
                exitCode |> should equal 0

                for heading in expectation.Headings do
                    output |> should contain heading)


namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.Types.Common
open NUnit.Framework
open System

/// Groups client identity coverage for the CLI test project.
[<NonParallelizable>]
module ClientIdentityTests =

    /// Verifies that configure sdk client identity stamps cli client type with assembly file version.
    [<Test>]
    let ``configureSdkClientIdentity stamps CLI client type with assembly file version`` () =
        Grace.SDK.ClientIdentity.clear ()

        try
            Services.configureSdkClientIdentity ()

            match Grace.SDK.ClientIdentity.tryGetConfiguredClientType () with
            | Some (ClientType.CLI version) ->
                String.IsNullOrWhiteSpace version
                |> should equal false

                version
                |> should equal (Services.getCliAssemblyFileVersion ())
            | other -> Assert.Fail($"Expected CLI client identity, got {other}.")
        finally
            Grace.SDK.ClientIdentity.clear ()

    /// Verifies that configured sdk client identity adds grace client headers.
    [<Test>]
    let ``configured SDK client identity adds Grace client headers`` () =
        Grace.SDK.ClientIdentity.clear ()

        try
            Grace.SDK.ClientIdentity.configure (ClientType.CLI "1.2.3")

            use httpClient = Grace.SDK.ClientIdentity.getHttpClient "corr-client"

            httpClient.DefaultRequestHeaders.Contains(Grace.Shared.Constants.ClientTypeHeaderKey)
            |> should equal true

            httpClient.DefaultRequestHeaders.GetValues(Grace.Shared.Constants.ClientTypeHeaderKey)
            |> Seq.exactlyOne
            |> should equal "CLI"

            httpClient.DefaultRequestHeaders.GetValues(Grace.Shared.Constants.ClientVersionHeaderKey)
            |> Seq.exactlyOne
            |> should equal "1.2.3"
        finally
            Grace.SDK.ClientIdentity.clear ()
