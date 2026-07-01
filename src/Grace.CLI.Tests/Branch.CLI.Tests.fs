namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.Command
open Grace.CLI.Services
open Grace.Shared
open Grace.Shared.Parameters.Branch
open Grace.Shared.Utilities
open Grace.Types.Annotation
open Grace.Types.Common
open Grace.Types.Reference
open NUnit.Framework
open Spectre.Console
open System
open System.Collections.Generic
open System.IO
open System.Text.Json
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
