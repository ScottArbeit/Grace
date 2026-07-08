namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.Command
open Grace.Shared
open Grace.Types.MaterializationPlan
open Grace.Types.Common
open Grace.Types.DirectoryVersion
open NUnit.Framework
open Spectre.Console
open System
open System.Collections.Generic
open System.IO

/// Groups connect coverage for the CLI test project.
[<NonParallelizable>]
module ConnectTests =
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

    /// Runs the supplied action with temp dir applied.
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

    /// Gets grace config path needed by the test scenario.
    let private getGraceConfigPath root = Path.Combine(root, ".grace", "graceconfig.json")

    /// Verifies that connect creates config when missing.
    [<Test>]
    let ``connect creates config when missing`` () =
        withTempDir (fun root ->
            /// Verifies that the CLI connect scenario exits with the expected process status.
            let exitCode, _ = runWithCapturedOutput [| "connect" |]
            exitCode |> should equal -1

            File.Exists(getGraceConfigPath root)
            |> should equal true)

    /// Verifies that connect skip decision requires matching blake3 when remote has one.
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

    /// Verifies that connect skip decision keeps legacy empty blake3 remote compatible.
    [<Test>]
    let ``connect skip decision keeps legacy empty blake3 remote compatible`` () =
        let remoteFile = FileVersion.Create (RelativePath "legacy-sha-only.txt") (Sha256Hash "legacy-sha") String.Empty false 10L

        Connect.existingFileMatchesRemoteVersion (Sha256Hash "legacy-sha") (Blake3Hash "different-local-blake3") remoteFile
        |> should equal true

    let private ownerId = Guid.Parse("11111111-1111-1111-1111-111111111111")
    let private organizationId = Guid.Parse("22222222-2222-2222-2222-222222222222")
    let private repositoryId = Guid.Parse("33333333-3333-3333-3333-333333333333")
    let private rootId = Guid.Parse("44444444-4444-4444-4444-444444444444")
    let private alternateRootId = Guid.Parse("55555555-5555-5555-5555-555555555555")
    let private sha256Hash = Sha256Hash(String.replicate 64 "a")
    let private blake3Hash = Blake3Hash(String.replicate 64 "b")

    /// Builds a root DirectoryVersionDto for Direct plan execution tests.
    let private rootDirectoryDto (files: FileVersion array) =
        { DirectoryVersionDto.Default with
            DirectoryVersion =
                Grace.Types.Common.DirectoryVersion.CreateWithHashes
                    rootId
                    ownerId
                    organizationId
                    repositoryId
                    Constants.RootDirectoryPath
                    sha256Hash
                    blake3Hash
                    (List<DirectoryVersionId>())
                    (List<FileVersion>(files :> seq<FileVersion>))
                    1L
        }

    /// Builds a Direct plan descriptor for the target-root zip artifact.
    let private zipArtifact source = MaterializationArtifactDescriptor.DirectoryVersionZip(rootId, 128L, Some sha256Hash, Some blake3Hash, source)

    /// Builds a Direct plan descriptor for the target-root recursive metadata artifact.
    let private metadataArtifact source = MaterializationArtifactDescriptor.RecursiveDirectoryMetadata(rootId, 64L, Some sha256Hash, Some blake3Hash, source)

    /// Builds a Direct Materialization Plan for connect execution tests.
    let private directPlan artifacts = MaterializationPlan.Create(rootId, MaterializationExecutionMode.Direct, MaterializationCacheSelection.Bypass, artifacts)

    /// Verifies that Direct plan execution preparation preserves recursive metadata file versions and planned zip source.
    [<Test>]
    let ``connect direct plan preparation returns metadata file versions and zip source`` () =
        let source = Some(MaterializationArtifactSource.Direct("https://example.test/root.zip"))

        let fileVersion = FileVersion.CreateWithHashes (RelativePath "src/app.fs") sha256Hash blake3Hash String.Empty false 12L

        let plan =
            directPlan [ zipArtifact source
                         metadataArtifact source ]

        let directoryVersions = [| rootDirectoryDto [| fileVersion |] |]

        match Connect.prepareDirectPlanExecutionArtifacts "correlation-id" plan directoryVersions with
        | Ok artifacts ->
            artifacts.TargetRootDirectoryVersionId
            |> should equal rootId

            artifacts.ZipUri
            |> should equal "https://example.test/root.zip"

            artifacts.DirectoryVersionDtos
            |> should haveLength 1

            artifacts.FileVersions |> should haveLength 1

            artifacts.FileVersions[0].RelativePath
            |> should equal (RelativePath "src/app.fs")
        | Error error -> Assert.Fail($"Unexpected Direct plan preparation error: {error.Error}")

    /// Verifies that Direct plan execution rejects mismatched root artifact descriptors before local writes.
    [<Test>]
    let ``connect direct plan preparation rejects mismatched artifact root`` () =
        let source = Some(MaterializationArtifactSource.Direct("https://example.test/root.zip"))
        let mismatchedZip = { zipArtifact source with RepresentedRootDirectoryVersionId = Some alternateRootId }

        let plan =
            directPlan [ mismatchedZip
                         metadataArtifact source ]

        let directoryVersions =
            [|
                rootDirectoryDto Array.empty<FileVersion>
            |]

        match Connect.prepareDirectPlanExecutionArtifacts "correlation-id" plan directoryVersions with
        | Ok _ -> Assert.Fail("Expected mismatched Direct plan roots to fail before extraction.")
        | Error error ->
            error.Error
            |> should contain "Materialization Plan"

    /// Verifies that Direct plan execution rejects missing artifact sources before status or object-cache updates.
    [<Test>]
    let ``connect direct plan preparation rejects missing zip source`` () =
        let source = Some(MaterializationArtifactSource.Direct("https://example.test/root.zip"))

        let plan =
            directPlan [ zipArtifact None
                         metadataArtifact source ]

        let directoryVersions =
            [|
                rootDirectoryDto Array.empty<FileVersion>
            |]

        match Connect.prepareDirectPlanExecutionArtifacts "correlation-id" plan directoryVersions with
        | Ok _ -> Assert.Fail("Expected missing zip artifact source to fail before extraction.")
        | Error error -> error.Error |> should contain "missing a source"

    /// Verifies that Direct plan execution rejects missing recursive metadata sources before status or object-cache updates.
    [<Test>]
    let ``connect direct plan preparation rejects missing recursive metadata source`` () =
        let source = Some(MaterializationArtifactSource.Direct("https://example.test/root.zip"))

        let plan =
            directPlan [ zipArtifact source
                         metadataArtifact None ]

        let directoryVersions =
            [|
                rootDirectoryDto Array.empty<FileVersion>
            |]

        match Connect.prepareDirectPlanExecutionArtifacts "correlation-id" plan directoryVersions with
        | Ok _ -> Assert.Fail("Expected missing recursive metadata artifact source to fail before extraction.")
        | Error error -> error.Error |> should contain "missing a source"
