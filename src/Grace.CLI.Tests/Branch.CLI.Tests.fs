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
open System.IO
open System.Text.Json
open System.Threading.Tasks

[<NonParallelizable>]
module BranchCommandTests =
    let private ownerId = Guid.NewGuid()
    let private organizationId = Guid.NewGuid()
    let private repositoryId = Guid.NewGuid()
    let private branchId = Guid.NewGuid()
    let private targetReferenceId = Guid.NewGuid()
    let private correlationId = "branch-annotate-tests"

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

    let private parse args = GraceCommand.rootCommand.Parse(withIds args)

    let private setAnsiConsoleOutput (writer: TextWriter) =
        let settings = AnsiConsoleSettings()
        settings.Out <- AnsiConsoleOutput(writer)
        AnsiConsole.Console <- AnsiConsole.Create(settings)

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
                { BoundaryId = "boundary-1"; LineRange = { StartLine = 9; EndLine = 9 }; SourceRowIds = [| "row-3" |] }
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

    [<Test>]
    let ``annotate handler maps options to parameters`` () =
        let explicitReferenceId = Guid.NewGuid()
        let mutable captured = Unchecked.defaultof<AnnotateParameters>
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

        let annotate (parameters: AnnotateParameters) : Task<GraceResult<BranchAnnotationDto>> =
            captured <- parameters
            Task.FromResult(Ok(GraceReturnValue.Create sampleAnnotation correlationId))

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

        let annotate (_: AnnotateParameters) : Task<GraceResult<BranchAnnotationDto>> =
            Task.FromResult(Ok(GraceReturnValue.Create sampleAnnotation correlationId))

        let resolveTargetReference (_: ReferenceId option) (_: CorrelationId) : Task<Result<CliCurrentStateCaptureResult, GraceError>> =
            Task.FromResult(Ok targetReferenceResult)

        let result =
            (Branch.annotateHandlerWith annotate resolveTargetReference parseResult)
                .Result

        match result with
        | Ok _ -> Assert.Fail("Expected invalid path failure.")
        | Error error -> error.Error |> should contain expectedError

    [<Test>]
    let ``annotate handler rejects invalid line range`` () =
        let parseResult =
            parse [| "branch"
                     "annotate"
                     "--path"
                     "src/App.fs"
                     "-L"
                     "9,7" |]

        let annotate (_: AnnotateParameters) : Task<GraceResult<BranchAnnotationDto>> =
            Task.FromResult(Ok(GraceReturnValue.Create sampleAnnotation correlationId))

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

    [<Test>]
    let ``annotate handler rejects reference type typo`` () =
        let parseResult =
            parse [| "branch"
                     "annotate"
                     "--path"
                     "src/App.fs"
                     "--reference-types"
                     "Commit,Typo" |]

        let annotate (_: AnnotateParameters) : Task<GraceResult<BranchAnnotationDto>> =
            Task.FromResult(Ok(GraceReturnValue.Create sampleAnnotation correlationId))

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

    [<Test>]
    let ``human output is span grouped with requested line numbers and text`` () =
        let parseResult =
            parse [| "branch"
                     "annotate"
                     "--path"
                     "src/App.fs"
                     "--show"
                     "both" |]

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

    [<Test>]
    let ``show introduced suppresses last changed human label`` () =
        let parseResult =
            parse [| "branch"
                     "annotate"
                     "--path"
                     "src/App.fs"
                     "--show"
                     "introduced" |]

        let _, output =
            captureOutput (fun () ->
                Branch.renderBranchAnnotationHumanOutput parseResult Branch.Introduced sampleAnnotation
                0)

        output |> should contain "Introduced Reference"

        output
        |> should not' (contain "Last changed Reference")

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
