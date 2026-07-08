namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.Common
open Grace.CLI.Command
open Grace.CLI.Text
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Branch
open Grace.Types.MaterializationPlan
open Grace.Types.Common
open Grace.Types.DirectoryVersion
open Grace.Types.Reference
open MessagePack
open NUnit.Framework
open Spectre.Console
open System
open System.Collections.Generic
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks

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
            Grace.Shared.Client.Configuration.resetConfiguration ()

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
    let private unpromotedTipRootId = Guid.Parse("77777777-7777-7777-7777-777777777777")
    let private basedOnRootId = Guid.Parse("88888888-8888-8888-8888-888888888888")
    let private sha256Hash = Sha256Hash(String.replicate 64 "a")
    let private blake3Hash = Blake3Hash(String.replicate 64 "b")

    /// Builds a branch reference carrying the directory version id used by connect target selection tests.
    let private referenceDto referenceType directoryId = { ReferenceDto.Default with ReferenceType = referenceType; DirectoryId = directoryId }

    /// Computes the SHA-256 descriptor hash for artifact validation tests.
    let private computeSha256Hash (bytes: byte array) = Sha256Hash(byteArrayToString (SHA256.HashData(bytes).AsSpan()))

    /// Computes the BLAKE3 descriptor hash for artifact validation tests.
    let private computeBlake3Hash (bytes: byte array) = Blake3Hash(ContentAddress.computeBlake3Hex bytes)

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

    /// Builds a DirectoryVersionDto for Direct plan execution tests.
    let private directoryDto directoryVersionId (relativePath: string) (files: FileVersion array) =
        { DirectoryVersionDto.Default with
            DirectoryVersion =
                Grace.Types.Common.DirectoryVersion.CreateWithHashes
                    directoryVersionId
                    ownerId
                    organizationId
                    repositoryId
                    (RelativePath relativePath)
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

    /// Builds a Direct plan descriptor whose integrity fields match the supplied payload.
    let private zipArtifactForBytes source (bytes: byte array) =
        MaterializationArtifactDescriptor.DirectoryVersionZip(
            rootId,
            int64 bytes.LongLength,
            Some(computeSha256Hash bytes),
            Some(computeBlake3Hash bytes),
            source
        )

    /// Builds a Direct plan descriptor whose only integrity field is the supplied payload's BLAKE3 hash.
    let private zipArtifactForBlake3OnlyBytes source (bytes: byte array) =
        MaterializationArtifactDescriptor.DirectoryVersionZip(rootId, int64 bytes.LongLength, None, Some(computeBlake3Hash bytes), source)

    /// Builds a recursive metadata descriptor whose integrity fields match the supplied payload.
    let private metadataArtifactForBytes source (bytes: byte array) =
        MaterializationArtifactDescriptor.RecursiveDirectoryMetadata(
            rootId,
            int64 bytes.LongLength,
            Some(computeSha256Hash bytes),
            Some(computeBlake3Hash bytes),
            source
        )

    /// Builds a Direct Materialization Plan for connect execution tests.
    let private directPlan artifacts = MaterializationPlan.Create(rootId, MaterializationExecutionMode.Direct, MaterializationCacheSelection.Bypass, artifacts)

    /// Compresses test payload bytes with gzip for descriptor-versus-file integrity tests.
    let private gzipBytes (bytes: byte array) =
        use output = new MemoryStream()

        do
            use gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen = true)
            gzip.Write(bytes, 0, bytes.Length)

        output.ToArray()

    /// Builds a zip payload with one Grace text entry stored as gzip-compressed bytes.
    let private zipBytesForTextEntry relativePath (payload: byte array) =
        use output = new MemoryStream()

        do
            use zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen = true)
            let entry = zip.CreateEntry(relativePath)

            use entryStream = entry.Open()
            let compressedPayload = gzipBytes payload
            entryStream.Write(compressedPayload, 0, compressedPayload.Length)

        output.ToArray()

    /// Builds the relative-path lookup required by Direct zip extraction.
    let private fileVersionLookup (fileVersions: FileVersion seq) =
        let lookup = Dictionary<RelativePath, FileVersion>()

        fileVersions
        |> Seq.iter (fun fileVersion -> lookup.Add(fileVersion.RelativePath, fileVersion))

        lookup

    /// Keeps test streams inspectable after production code disposes the response stream wrapper.
    type private NonDisposingMemoryStream(bytes: byte array) =
        inherit MemoryStream(bytes)

        override _.Dispose(disposing: bool) = ()

    /// Verifies that Direct plan requests preserve a moving reference selector for final server resolution.
    [<Test>]
    let ``connect direct plan request preserves reference selector intent`` () =
        let referenceId = Guid.Parse("66666666-6666-6666-6666-666666666666")
        let branchDto = { BranchDto.Default with BranchName = BranchName "main" }

        let request =
            Connect.createDirectPlanRequest (
                Connect.createDirectPlanTargetSelector (Connect.UseReferenceId referenceId) (Connect.UsePlanBranchName branchDto.BranchName) rootId
            )

        request.TargetSelector.SelectorKind
        |> should equal MaterializationTargetSelectorKind.ReferenceId

        request.TargetSelector.ReferenceId
        |> should equal (Some referenceId)

        request.TargetSelector.DirectoryVersionId
        |> should equal None

    /// Verifies that Direct plan requests preserve reference type selector intent for server-side latest-reference resolution.
    [<Test>]
    let ``connect direct plan request preserves reference type selector intent`` () =
        let branchDto = { BranchDto.Default with BranchName = BranchName "main" }

        let request =
            Connect.createDirectPlanRequest (
                Connect.createDirectPlanTargetSelector
                    (Connect.UseReferenceType ReferenceType.Promotion)
                    (Connect.UsePlanBranchName branchDto.BranchName)
                    rootId
            )

        request.TargetSelector.SelectorKind
        |> should equal MaterializationTargetSelectorKind.ReferenceType

        request.TargetSelector.BranchName
        |> should equal (Some(BranchName "main"))

        request.TargetSelector.BranchId
        |> should equal None

        request.TargetSelector.ReferenceType
        |> should equal (Some ReferenceType.Promotion)

        request.TargetSelector.DirectoryVersionId
        |> should equal None

    /// Verifies that Direct plan requests preserve explicit branch id selector intent for server-side resolution.
    [<Test>]
    let ``connect direct plan request preserves branch id reference type selector intent`` () =
        let branchId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
        let branchDto = { BranchDto.Default with BranchId = branchId; BranchName = BranchName "renamed" }

        let parseResult =
            GraceCommand.rootCommand.Parse(
                [|
                    "connect"
                    OptionName.BranchId
                    $"{branchId}"
                    OptionName.ReferenceType
                    $"{ReferenceType.Promotion}"
                |]
            )

        let request =
            Connect.createDirectPlanRequest (
                Connect.createDirectPlanTargetSelector
                    (Connect.UseReferenceType ReferenceType.Promotion)
                    (Connect.getDirectPlanBranchIdentity parseResult branchDto)
                    rootId
            )

        request.TargetSelector.SelectorKind
        |> should equal MaterializationTargetSelectorKind.ReferenceType

        request.TargetSelector.BranchId
        |> should equal (Some branchId)

        request.TargetSelector.BranchName
        |> should equal None

        request.TargetSelector.ReferenceType
        |> should equal (Some ReferenceType.Promotion)

    /// Verifies that Direct plan requests still use branch-name identity when the user selected a branch name.
    [<Test>]
    let ``connect direct plan request preserves branch name reference type selector intent`` () =
        let branchId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
        let branchDto = { BranchDto.Default with BranchId = branchId; BranchName = BranchName "feature" }

        let parseResult =
            GraceCommand.rootCommand.Parse(
                [|
                    "connect"
                    OptionName.BranchName
                    "feature"
                    OptionName.ReferenceType
                    $"{ReferenceType.Promotion}"
                |]
            )

        let request =
            Connect.createDirectPlanRequest (
                Connect.createDirectPlanTargetSelector
                    (Connect.UseReferenceType ReferenceType.Promotion)
                    (Connect.getDirectPlanBranchIdentity parseResult branchDto)
                    rootId
            )

        request.TargetSelector.BranchId
        |> should equal None

        request.TargetSelector.BranchName
        |> should equal (Some(BranchName "feature"))

    /// Verifies that Direct plan requests still use default branch-name identity when no branch id is explicit.
    [<Test>]
    let ``connect direct plan request preserves default branch name reference type selector intent`` () =
        let branchDto = { BranchDto.Default with BranchId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"); BranchName = BranchName "main" }

        let parseResult =
            GraceCommand.rootCommand.Parse(
                [|
                    "connect"
                    OptionName.ReferenceType
                    $"{ReferenceType.Promotion}"
                |]
            )

        let request =
            Connect.createDirectPlanRequest (
                Connect.createDirectPlanTargetSelector
                    (Connect.UseReferenceType ReferenceType.Promotion)
                    (Connect.getDirectPlanBranchIdentity parseResult branchDto)
                    rootId
            )

        request.TargetSelector.BranchId
        |> should equal None

        request.TargetSelector.BranchName
        |> should equal (Some(BranchName "main"))

    /// Verifies that implicit default connect keeps promoted target semantics even when the branch tip has moved.
    [<Test>]
    let ``connect default resolver chooses latest promotion before unpromoted tip`` () =
        let branchDto =
            { BranchDto.Default with
                BranchName = BranchName "trunk"
                BasedOn = referenceDto ReferenceType.Commit basedOnRootId
                LatestReference = referenceDto ReferenceType.Commit unpromotedTipRootId
                LatestPromotion = referenceDto ReferenceType.Promotion rootId
                LatestCommit = referenceDto ReferenceType.Commit unpromotedTipRootId
            }

        Connect.resolveDefaultDirectoryVersionId branchDto
        |> should equal (Some rootId)

    /// Verifies that implicit default connect falls back to the branch base when no promotion exists.
    [<Test>]
    let ``connect default resolver falls back to based on when promotion is missing`` () =
        let branchDto =
            { BranchDto.Default with
                BranchName = BranchName "trunk"
                BasedOn = referenceDto ReferenceType.Commit basedOnRootId
                LatestReference = referenceDto ReferenceType.Commit unpromotedTipRootId
                LatestCommit = referenceDto ReferenceType.Commit unpromotedTipRootId
            }

        Connect.resolveDefaultDirectoryVersionId branchDto
        |> should equal (Some basedOnRootId)

    /// Verifies that Direct plan requests send the already-resolved implicit default target as an immutable selector.
    [<Test>]
    let ``connect direct plan request sends resolved default target as directory version selector`` () =
        let branchDto = { BranchDto.Default with BranchName = BranchName "trunk" }

        let request =
            Connect.createDirectPlanRequest (Connect.createDirectPlanTargetSelector Connect.UseDefault (Connect.UsePlanBranchName branchDto.BranchName) rootId)

        request.TargetSelector.SelectorKind
        |> should equal MaterializationTargetSelectorKind.DirectoryVersionId

        request.TargetSelector.DirectoryVersionId
        |> should equal (Some rootId)

        request.TargetSelector.BranchName
        |> should equal None

    /// Verifies that explicit DirectoryVersionId connects remain immutable selectors and can represent scoped directories.
    [<Test>]
    let ``connect direct plan request preserves explicit directory version selector`` () =
        let scopedDirectoryVersionId = Guid.Parse("99999999-9999-9999-9999-999999999999")
        let branchDto = { BranchDto.Default with BranchName = BranchName "trunk" }

        let request =
            Connect.createDirectPlanRequest (
                Connect.createDirectPlanTargetSelector
                    (Connect.UseDirectoryVersionId scopedDirectoryVersionId)
                    (Connect.UsePlanBranchName branchDto.BranchName)
                    rootId
            )

        request.TargetSelector.SelectorKind
        |> should equal MaterializationTargetSelectorKind.DirectoryVersionId

        request.TargetSelector.DirectoryVersionId
        |> should equal (Some scopedDirectoryVersionId)

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

            artifacts.ZipArtifact.ArtifactKind
            |> should equal MaterializationArtifactKind.DirectoryVersionZip

            artifacts.DirectoryVersionDtos
            |> should haveLength 1

            artifacts.FileVersions |> should haveLength 1

            artifacts.FileVersions[0].RelativePath
            |> should equal (RelativePath "src/app.fs")
        | Error error -> Assert.Fail($"Unexpected Direct plan preparation error: {error.Error}")

    /// Verifies that Direct plan execution preparation accepts non-root target directory versions.
    [<Test>]
    let ``connect direct plan preparation accepts non-root target directory metadata`` () =
        let source = Some(MaterializationArtifactSource.Direct("https://example.test/src.zip"))
        let fileVersion = FileVersion.CreateWithHashes (RelativePath "src/app.fs") sha256Hash blake3Hash String.Empty false 12L

        let plan =
            directPlan [ zipArtifact source
                         metadataArtifact source ]

        let directoryVersions =
            [|
                directoryDto rootId "src" [| fileVersion |]
            |]

        match Connect.prepareDirectPlanExecutionArtifacts "correlation-id" plan directoryVersions with
        | Ok artifacts ->
            artifacts.TargetRootDirectoryVersionId
            |> should equal rootId

            artifacts.FileVersions |> should haveLength 1
        | Error error -> Assert.Fail($"Unexpected non-root Direct plan preparation error: {error.Error}")

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

    /// Verifies that planned artifact validation rejects stale or truncated bytes before extraction.
    [<Test>]
    let ``connect direct plan artifact validation rejects size mismatch`` () =
        let bytes = [| 1uy; 2uy; 3uy |]

        let artifact =
            { zipArtifactForBytes (Some(MaterializationArtifactSource.Direct("https://example.test/root.zip"))) bytes with
                SizeInBytes = Some(int64 bytes.Length + 1L)
            }

        match Connect.validatePlannedArtifactBytes "correlation-id" "DirectoryVersionZip" artifact bytes with
        | Ok _ -> Assert.Fail("Expected artifact size mismatch to fail before extraction.")
        | Error error -> error.Error |> should contain "size mismatch"

    /// Verifies that planned artifact validation rejects bytes whose SHA-256 evidence no longer matches the plan.
    [<Test>]
    let ``connect direct plan artifact validation rejects sha mismatch`` () =
        let plannedBytes = [| 1uy; 2uy; 3uy |]
        let downloadedBytes = [| 1uy; 2uy; 4uy |]
        let artifact = zipArtifactForBytes (Some(MaterializationArtifactSource.Direct("https://example.test/root.zip"))) plannedBytes

        match Connect.validatePlannedArtifactBytes "correlation-id" "DirectoryVersionZip" artifact downloadedBytes with
        | Ok _ -> Assert.Fail("Expected artifact hash mismatch to fail before extraction.")
        | Error error -> error.Error |> should contain "SHA-256 mismatch"

    /// Verifies that planned artifact validation accepts descriptors that carry matching BLAKE3-only integrity evidence.
    [<Test>]
    let ``connect direct plan artifact validation accepts blake3 only descriptor`` () =
        let bytes = [| 1uy; 2uy; 3uy |]
        let artifact = zipArtifactForBlake3OnlyBytes (Some(MaterializationArtifactSource.Direct("https://example.test/root.zip"))) bytes

        match Connect.validatePlannedArtifactBytes "correlation-id" "DirectoryVersionZip" artifact bytes with
        | Error error -> Assert.Fail($"Unexpected BLAKE3-only artifact validation error: {error.Error}")
        | Ok () -> ()

    /// Verifies that planned artifact validation rejects descriptors whose BLAKE3-only integrity evidence does not match.
    [<Test>]
    let ``connect direct plan artifact validation rejects blake3 only mismatch`` () =
        let plannedBytes = [| 1uy; 2uy; 3uy |]
        let downloadedBytes = [| 1uy; 2uy; 4uy |]
        let artifact = zipArtifactForBlake3OnlyBytes (Some(MaterializationArtifactSource.Direct("https://example.test/root.zip"))) plannedBytes

        match Connect.validatePlannedArtifactBytes "correlation-id" "DirectoryVersionZip" artifact downloadedBytes with
        | Ok _ -> Assert.Fail("Expected BLAKE3-only artifact hash mismatch to fail before extraction.")
        | Error error -> error.Error |> should contain "BLAKE3 mismatch"

    /// Verifies that streamed DirectUri artifact validation accepts BLAKE3-only descriptors before extraction.
    [<Test>]
    let ``connect direct plan artifact stream validation accepts blake3 only descriptor`` () =
        let bytes = [| 1uy; 2uy; 3uy |]
        let artifact = zipArtifactForBlake3OnlyBytes (Some(MaterializationArtifactSource.Direct("https://example.test/root.zip"))) bytes

        use stream = new MemoryStream(bytes)

        match Connect.validatePlannedArtifactStream "correlation-id" "DirectoryVersionZip" artifact stream
              |> fun task -> task.GetAwaiter().GetResult()
            with
        | Error error -> Assert.Fail($"Unexpected streamed BLAKE3-only artifact validation error: {error.Error}")
        | Ok () -> stream.Position |> should equal 0L

    /// Verifies that streamed DirectUri artifact validation rejects mismatched BLAKE3-only descriptors before extraction.
    [<Test>]
    let ``connect direct plan artifact stream validation rejects blake3 only mismatch`` () =
        let plannedBytes = [| 1uy; 2uy; 3uy |]
        let downloadedBytes = [| 1uy; 2uy; 4uy |]
        let artifact = zipArtifactForBlake3OnlyBytes (Some(MaterializationArtifactSource.Direct("https://example.test/root.zip"))) plannedBytes

        use stream = new MemoryStream(downloadedBytes)

        match Connect.validatePlannedArtifactStream "correlation-id" "DirectoryVersionZip" artifact stream
              |> fun task -> task.GetAwaiter().GetResult()
            with
        | Ok _ -> Assert.Fail("Expected streamed BLAKE3-only artifact hash mismatch to fail before extraction.")
        | Error error -> error.Error |> should contain "BLAKE3 mismatch"

    /// Verifies that planned artifact validation still rejects descriptors with no supported integrity evidence.
    [<Test>]
    let ``connect direct plan artifact validation rejects missing integrity evidence`` () =
        let bytes = [| 1uy; 2uy; 3uy |]

        let artifact =
            MaterializationArtifactDescriptor.DirectoryVersionZip(
                rootId,
                int64 bytes.LongLength,
                None,
                None,
                Some(MaterializationArtifactSource.Direct("https://example.test/root.zip"))
            )

        match Connect.validatePlannedArtifactBytes "correlation-id" "DirectoryVersionZip" artifact bytes with
        | Ok _ -> Assert.Fail("Expected artifact validation to reject descriptors with no supported integrity evidence.")
        | Error error ->
            error.Error
            |> should contain "SHA-256 or BLAKE3 integrity evidence"

    /// Verifies that DirectUri transfer rejects oversized Content-Length without opening the response body.
    [<Test>]
    let ``connect direct uri copy rejects oversized content length before transfer`` () =
        let mutable opened = false

        let result =
            Connect.copyDirectUriArtifactToTempStream "correlation-id" "DirectoryVersionZip" (Some 3L) (Some 4L) (fun () ->
                opened <- true
                Task.FromResult<Stream>(new MemoryStream([| 1uy; 2uy; 3uy; 4uy |])))
            |> fun task -> task.GetAwaiter().GetResult()

        match result with
        | Ok stream ->
            stream.Dispose()
            Assert.Fail("Expected oversized Content-Length to fail before transfer.")
        | Error error ->
            opened |> should equal false

            error.Error
            |> should contain "Content-Length exceeds planned size"

    /// Verifies that DirectUri transfer stops after observing one byte beyond the planned delivered artifact size.
    [<Test>]
    let ``connect direct uri copy stops at planned size plus one when length is absent`` () =
        let payload = [| 0uy .. 9uy |]
        let source = new NonDisposingMemoryStream(payload)

        let result =
            Connect.copyDirectUriArtifactToTempStream "correlation-id" "DirectoryVersionZip" (Some 3L) None (fun () -> Task.FromResult<Stream>(source))
            |> fun task -> task.GetAwaiter().GetResult()

        match result with
        | Ok stream ->
            stream.Dispose()
            Assert.Fail("Expected over-limit DirectUri body to fail during transfer.")
        | Error error ->
            source.Position |> should equal 4L
            error.Error |> should contain "size mismatch"

    /// Verifies that descriptor integrity is evaluated against delivered compressed artifact bytes.
    [<Test>]
    let ``connect direct plan artifact validation hashes delivered compressed bytes`` () =
        let uncompressedBytes = Encoding.UTF8.GetBytes("uncompressed file content")
        let compressedBytes = gzipBytes uncompressedBytes
        let artifact = zipArtifactForBytes (Some(MaterializationArtifactSource.Direct("https://example.test/root.zip"))) compressedBytes

        match Connect.validatePlannedArtifactBytes "correlation-id" "DirectoryVersionZip" artifact compressedBytes with
        | Error error -> Assert.Fail($"Unexpected compressed artifact validation error: {error.Error}")
        | Ok () -> ()

        match Connect.validatePlannedArtifactBytes "correlation-id" "DirectoryVersionZip" artifact uncompressedBytes with
        | Ok () -> Assert.Fail("Expected descriptor validation to reject uncompressed bytes when descriptor hashes compressed delivery bytes.")
        | Error error -> error.Error |> should contain "size mismatch"

    /// Verifies that final materialized file validation uses uncompressed working-tree file bytes.
    [<Test>]
    let ``connect materialized file validation hashes uncompressed file bytes`` () =
        withTempDir (fun root ->
            Grace.Shared.Client.Configuration.resetConfiguration ()
            let payload = Encoding.UTF8.GetBytes("materialized text")
            let relativePath = RelativePath "src/app.fs"
            let filePath = Path.Combine(root, string relativePath)

            Directory.CreateDirectory(Path.GetDirectoryName(filePath))
            |> ignore

            File.WriteAllBytes(filePath, payload)

            let fileVersion =
                FileVersion.CreateWithHashes relativePath (computeSha256Hash payload) (computeBlake3Hash payload) String.Empty false (int64 payload.Length)

            match Connect.validateMaterializedFiles "correlation-id" [| fileVersion |]
                  |> fun task -> task.GetAwaiter().GetResult()
                with
            | Error error -> Assert.Fail($"Unexpected materialized file validation error: {error.Error}")
            | Ok () -> ())

    /// Verifies that staged Direct zip validation fails before replacing an existing working-tree file.
    [<Test>]
    let ``connect staged direct zip validation preserves existing worktree file on mismatch`` () =
        withTempDir (fun root ->
            Grace.Shared.Client.Configuration.resetConfiguration ()

            Grace.Shared.Client.Configuration.Current()
            |> ignore

            let expectedPayload = Encoding.UTF8.GetBytes("expected materialized text")
            let stalePayload = Encoding.UTF8.GetBytes("stale materialized text")
            let sentinel = "existing local sentinel"
            let relativePath = RelativePath "src/app.fs"
            let filePath = Path.Combine(root, string relativePath)

            Directory.CreateDirectory(Path.GetDirectoryName(filePath))
            |> ignore

            File.WriteAllText(filePath, sentinel)

            let fileVersion =
                FileVersion.CreateWithHashes
                    relativePath
                    (computeSha256Hash expectedPayload)
                    (computeBlake3Hash expectedPayload)
                    String.Empty
                    false
                    (int64 expectedPayload.Length)

            use zipStream = new MemoryStream(zipBytesForTextEntry (string relativePath) stalePayload)
            let parseResult = GraceCommand.rootCommand.Parse([| "connect" |])

            let result =
                Connect.extractValidatedZipEntries
                    parseResult
                    "correlation-id"
                    (fileVersionLookup [| fileVersion |])
                    (HashSet<RelativePath>())
                    [| fileVersion |]
                    zipStream
                |> fun task -> task.GetAwaiter().GetResult()

            match result with
            | Ok () -> Assert.Fail("Expected staged Direct zip validation to reject stale decompressed file bytes.")
            | Error error ->
                Assert.Multiple(
                    Action (fun () ->
                        error.Error |> should contain "mismatch"

                        File.ReadAllText(filePath)
                        |> should equal sentinel)
                ))

    /// Verifies that staged Direct zip validation still writes final files and object-cache bytes on success.
    [<Test>]
    let ``connect staged direct zip validation writes final worktree and object cache on success`` () =
        withTempDir (fun root ->
            Grace.Shared.Client.Configuration.resetConfiguration ()
            let configuration = Grace.Shared.Client.Configuration.Current()

            let payload = Encoding.UTF8.GetBytes("expected materialized text")
            let relativePath = RelativePath "src/app.fs"
            let filePath = Path.Combine(root, string relativePath)

            let fileVersion =
                FileVersion.CreateWithHashes relativePath (computeSha256Hash payload) (computeBlake3Hash payload) String.Empty false (int64 payload.Length)

            use zipStream = new MemoryStream(zipBytesForTextEntry (string relativePath) payload)
            let parseResult = GraceCommand.rootCommand.Parse([| "connect" |])

            let result =
                Connect.extractValidatedZipEntries
                    parseResult
                    "correlation-id"
                    (fileVersionLookup [| fileVersion |])
                    (HashSet<RelativePath>())
                    [| fileVersion |]
                    zipStream
                |> fun task -> task.GetAwaiter().GetResult()

            match result with
            | Error error -> Assert.Fail($"Unexpected staged Direct zip validation error: {error.Error}")
            | Ok () ->
                let objectFiles = Directory.GetFiles(string configuration.ObjectDirectory, "*", SearchOption.AllDirectories)

                Assert.Multiple(
                    Action (fun () ->
                        File.ReadAllBytes(filePath)
                        |> should equal payload

                        objectFiles |> should haveLength 1

                        File.ReadAllBytes(objectFiles[0])
                        |> should equal payload)
                ))

    /// Verifies that connect executes recursive metadata from the planned artifact payload.
    [<Test>]
    let ``connect direct plan decodes planned recursive metadata artifact`` () =
        let directoryVersions =
            [|
                rootDirectoryDto Array.empty<FileVersion>
            |]

        let bytes = MessagePackSerializer.Serialize(directoryVersions, Constants.messagePackSerializerOptions)
        let artifact = metadataArtifactForBytes (Some(MaterializationArtifactSource.Direct("https://example.test/root.msgpack"))) bytes

        match Connect.validatePlannedArtifactBytes "correlation-id" "RecursiveDirectoryMetadata" artifact bytes with
        | Error error -> Assert.Fail($"Unexpected metadata integrity error: {error.Error}")
        | Ok () ->
            match Connect.decodeRecursiveMetadataArtifact "correlation-id" bytes with
            | Error error -> Assert.Fail($"Unexpected metadata decode error: {error.Error}")
            | Ok decoded ->
                decoded |> should haveLength 1

                decoded[0].DirectoryVersion.DirectoryVersionId
                |> should equal rootId
