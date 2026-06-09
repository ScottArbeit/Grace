namespace Grace.Types.Tests

open Grace.Shared.Utilities
open Grace.Shared.Parameters.Branch
open Grace.Types.Annotation
open Grace.Types.Common
open NUnit.Framework
open System
open System.Text.Json

[<Parallelizable(ParallelScope.All)>]
type AnnotationContractTests() =

    let targetReferenceId = Guid.Parse("11111111-1111-1111-1111-111111111111")
    let sourceReferenceId = Guid.Parse("22222222-2222-2222-2222-222222222222")
    let directoryVersionId = Guid.Parse("33333333-3333-3333-3333-333333333333")

    let assertOk (result: Result<unit, string list>) =
        match result with
        | Ok () -> ()
        | Error errors -> Assert.Fail(String.Join(Environment.NewLine, errors))

    let validAnnotation includeLineText =
        BranchAnnotationDto.Create(
            { StartLine = 10; EndLine = 12 },
            targetReferenceId,
            "src/App.fs",
            [|
                ReferenceType.Commit
                ReferenceType.Checkpoint
            |],
            DefaultMaxReferences,
            includeLineText,
            [|
                { LineNumber = 10; Text = "let value = 1" }
                { LineNumber = 11; Text = "value + 1" }
            |],
            [|
                { BoundaryId = "boundary-1"; LineRange = { StartLine = 10; EndLine = 12 }; SourceRowIds = [| "source-row-1" |] }
            |],
            [|
                { SpanId = "span-1"; BoundaryId = "boundary-1"; LineRange = { StartLine = 10; EndLine = 11 }; SourceRowIds = [| "source-row-1" |] }
            |],
            [|
                { SourceRowId = "source-row-1"; SourceReferenceId = "source-reference-1"; Path = "src/App.fs"; LineRange = { StartLine = 3; EndLine = 4 } }
            |],
            [|
                {
                    SourceReferenceId = "source-reference-1"
                    ReferenceId = sourceReferenceId
                    ReferenceType = ReferenceType.Commit
                    ReferenceText = "previous commit"
                    DirectoryVersionId = directoryVersionId
                    CreatedAt = None
                }
            |]
        )

    [<Test>]
    member _.AnnotationDtoSerializationKeepsSourceReferencesAsArray() =
        let annotation = validAnnotation true
        let json = serialize annotation

        use document = JsonDocument.Parse(json)
        let root = document.RootElement

        Assert.Multiple(
            Action (fun () ->
                let mutable algorithmVersion = Unchecked.defaultof<JsonElement>

                Assert.That(root.GetProperty("Class").GetString(), Is.EqualTo(nameof BranchAnnotationDto))

                Assert.That(
                    root
                        .GetProperty("RequestedLineRange")
                        .GetProperty("StartLine")
                        .GetInt32(),
                    Is.EqualTo(10)
                )

                Assert.That(root.GetProperty("Path").GetString(), Is.EqualTo("src/App.fs"))
                Assert.That(root.GetProperty("ReferenceTypeFilter").ValueKind, Is.EqualTo(JsonValueKind.Array))
                Assert.That(root.GetProperty("SourceReferences").ValueKind, Is.EqualTo(JsonValueKind.Array))

                Assert.That(
                    root
                        .GetProperty("SourceReferences")
                        .GetArrayLength(),
                    Is.EqualTo(1)
                )

                Assert.That(root.TryGetProperty("AlgorithmVersion", &algorithmVersion), Is.False))
        )

        let roundTrip = deserialize<BranchAnnotationDto> json
        Assert.That(roundTrip, Is.EqualTo(annotation))

    [<Test>]
    member _.IncludeLineTextFalseYieldsEmptyLines() =
        let annotation = validAnnotation false

        Assert.Multiple(
            Action (fun () ->
                Assert.That(annotation.IncludeLineText, Is.False)
                Assert.That(annotation.Lines, Is.Empty)
                assertOk (validate annotation))
        )

    [<Test>]
    member _.AnnotateParametersDefaultReferenceBudgetIsOneThousand() =
        let parameters = AnnotateParameters()

        Assert.Multiple(
            Action (fun () ->
                Assert.That(parameters.MaxReferences, Is.EqualTo(1000))
                Assert.That(parameters.IncludeLineText, Is.False)
                assertOk (parameters.Validate()))
        )

    [<TestCase(0)>]
    [<TestCase(5001)>]
    member _.AnnotateParametersRejectsReferenceBudgetOutsideSupportedRange(maxReferences: int) =
        let parameters = AnnotateParameters()
        parameters.MaxReferences <- maxReferences

        Assert.That(parameters.Validate().IsError, Is.True)

    [<Test>]
    member _.AnnotateParametersAcceptsMaximumReferenceBudget() =
        let parameters = AnnotateParameters()
        parameters.MaxReferences <- 5000

        assertOk (parameters.Validate())

    [<TestCase(0, 1)>]
    [<TestCase(-1, 1)>]
    [<TestCase(5, 4)>]
    member _.AnnotateParametersRejectsInvalidLineRanges(startLine: int, endLine: int) =
        let parameters = AnnotateParameters()
        parameters.StartLine <- startLine
        parameters.EndLine <- endLine

        Assert.That(parameters.Validate().IsError, Is.True)

    [<Test>]
    member _.AnnotationValidationChecksSpanSourceAndBoundaryLinks() =
        let annotation = validAnnotation true

        assertOk (validate annotation)

        let broken =
            { annotation with
                Spans =
                    [|
                        { annotation.Spans[0] with BoundaryId = "missing-boundary"; SourceRowIds = [| "missing-source-row" |] }
                    |]
                SourceRows =
                    [|
                        { annotation.SourceRows[0] with SourceReferenceId = "missing-source-reference" }
                    |]
            }

        match validate broken with
        | Ok () -> Assert.Fail("Broken annotation links should be rejected.")
        | Error errors ->
            Assert.Multiple(
                Action (fun () ->
                    Assert.That(errors, Has.Some.Contains("missing Boundary"))
                    Assert.That(errors, Has.Some.Contains("missing SourceRow"))
                    Assert.That(errors, Has.Some.Contains("missing SourceReference")))
            )

    [<Test>]
    member _.AnnotationValidationRejectsDuplicateSourceRows() =
        let annotation = validAnnotation true

        let duplicate =
            { annotation with
                SourceRows =
                    [|
                        annotation.SourceRows[0]
                        annotation.SourceRows[0]
                    |]
            }

        match validate duplicate with
        | Ok () -> Assert.Fail("Duplicate source rows should be rejected.")
        | Error errors -> Assert.That(errors, Has.Some.Contains("duplicate SourceRowId"))
