namespace Grace.Server.Tests

open Grace.Shared
open Grace.Shared.AnnotationLineCore
open Grace.Types
open Grace.Types.Annotation
open Grace.Types.Common
open Grace.Types.Reference
open Microsoft.AspNetCore.Http
open NodaTime
open NUnit.Framework
open System.Security.Claims

/// Covers metadata Creation behavior in no-Aspire server unit tests.
[<TestFixture>]
type MetadataCreationTests() =

    /// Verifies that create Context.
    member private _.CreateContext() =
        let context = DefaultHttpContext()
        context.Items[ Constants.CorrelationId ] <- "corr-server"
        context.User <- ClaimsPrincipal(ClaimsIdentity([| Claim(ClaimTypes.Name, "tester") |], "test"))
        context

    /// Verifies that create Metadata maps Grace client headers to CLI client type.
    [<Test>]
    member this.``createMetadata maps Grace client headers to CLI client type``() =
        let context = this.CreateContext()
        context.Request.Headers[ Constants.ClientTypeHeaderKey ] <- "CLI"
        context.Request.Headers[ Constants.ClientVersionHeaderKey ] <- "0.1.2.3"

        let metadata = Grace.Server.Services.createMetadata context

        match metadata.ClientType with
        | Some (ClientType.CLI version) -> Assert.That(version, Is.EqualTo("0.1.2.3"))
        | other -> Assert.Fail($"Expected CLI client metadata, got {other}.")

    /// Verifies that create Metadata leaves client type unset when headers are absent.
    [<Test>]
    member this.``createMetadata leaves client type unset when headers are absent``() =
        let context = this.CreateContext()

        let metadata = Grace.Server.Services.createMetadata context

        Assert.That(metadata.ClientType, Is.EqualTo(Microsoft.FSharp.Core.Option.None))

    /// Verifies that create Metadata leaves client type unset when CLI version header is missing.
    [<Test>]
    member this.``createMetadata leaves client type unset when CLI version header is missing``() =
        let context = this.CreateContext()
        context.Request.Headers[ Constants.ClientTypeHeaderKey ] <- "CLI"

        let metadata = Grace.Server.Services.createMetadata context

        Assert.That(metadata.ClientType, Is.EqualTo(Microsoft.FSharp.Core.Option.None))

    /// Verifies that create Metadata uses http principal when identity name is absent.
    [<Test>]
    member this.``createMetadata uses http principal when identity name is absent``() =
        let context = this.CreateContext()
        context.User <- ClaimsPrincipal(ClaimsIdentity([||], "test"))

        let metadata = Grace.Server.Services.createMetadata context

        Assert.That(metadata.Principal, Is.EqualTo("http"))

    /// Verifies that storage upload session metadata preserves client type headers.
    [<Test>]
    member this.``storage upload session metadata preserves client type headers``() =
        let context = this.CreateContext()
        context.Request.Path <- PathString("/storage/startManifestUploadSession")
        context.Request.Headers[ Constants.ClientTypeHeaderKey ] <- "CLI"
        context.Request.Headers[ Constants.ClientVersionHeaderKey ] <- "0.1.2.3"

        let metadata = Grace.Server.Storage.createEventMetadata context "corr-upload-session"

        Assert.That(metadata.CorrelationId, Is.EqualTo("corr-upload-session"))
        Assert.That(metadata.Properties["Path"], Is.EqualTo("/storage/startManifestUploadSession"))

        match metadata.ClientType with
        | Some (ClientType.CLI version) -> Assert.That(version, Is.EqualTo("0.1.2.3"))
        | other -> Assert.Fail($"Expected CLI client metadata, got {other}.")


/// Covers branch Annotation Server behavior in no-Aspire server unit tests.
[<TestFixture>]
type BranchAnnotationServerTests() =

    let branchId = System.Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")

    /// Builds reference test data for the server unit general scenarios in this file.
    let reference order referenceId =
        { ReferenceDto.Default with
            BranchId = branchId
            CreatedAt = Instant.FromUnixTimeSeconds(int64 order)
            Links = Array.empty
            ReferenceId = referenceId
            ReferenceType = ReferenceType.Save
        }

    /// Verifies that try Get Based On Reference Id follows stored Based On links.
    [<Test>]
    member _.``tryGetBasedOnReferenceId follows stored BasedOn links``() =
        let basedOnReferenceId = System.Guid.Parse("11111111-1111-1111-1111-111111111111")

        let referenceDto =
            { ReferenceDto.Default with
                Links =
                    [|
                        ReferenceLinkType.IncludedInPromotionSet(System.Guid.Parse("22222222-2222-2222-2222-222222222222"))
                        ReferenceLinkType.BasedOn basedOnReferenceId
                    |]
            }

        Assert.That(Grace.Server.Branch.tryGetBasedOnReferenceId referenceDto, Is.EqualTo(Some basedOnReferenceId))

    /// Verifies that terminal PromotionSet references are revealable while generic review metadata remains protected.
    [<Test>]
    member _.``PromotionSet terminal reveal allows terminal metadata but protects generic links``() =
        let promotionSetId = System.Guid.Parse("22222222-2222-2222-2222-222222222222")
        let otherPromotionSetId = System.Guid.Parse("33333333-3333-3333-3333-333333333333")

        let terminalReference =
            { ReferenceDto.Default with
                Links =
                    [|
                        ReferenceLinkType.IncludedInPromotionSet promotionSetId
                        ReferenceLinkType.PromotionSetTerminal promotionSetId
                    |]
            }

        let nonTerminalReference =
            { ReferenceDto.Default with
                Links =
                    [|
                        ReferenceLinkType.IncludedInPromotionSet promotionSetId
                    |]
            }

        Assert.That(Grace.Server.Branch.isRevealablePromotionSetMetadataLink terminalReference promotionSetId, Is.True)
        Assert.That(Grace.Server.Branch.tryGetTerminalPromotionSetForReveal terminalReference, Is.EqualTo(Some promotionSetId))
        Assert.That(Grace.Server.Branch.isRevealablePromotionSetMetadataLink nonTerminalReference promotionSetId, Is.False)
        Assert.That(Grace.Server.Branch.isRevealablePromotionSetMetadataLink terminalReference otherPromotionSetId, Is.False)

    /// Verifies that ordered History Window preserves boundary link when local history is truncated.
    [<Test>]
    member _.``orderedHistoryWindow preserves boundary link when local history is truncated``() =
        let firstReferenceId = System.Guid.Parse("11111111-1111-1111-1111-111111111111")
        let secondReferenceId = System.Guid.Parse("22222222-2222-2222-2222-222222222222")
        let thirdReferenceId = System.Guid.Parse("33333333-3333-3333-3333-333333333333")

        let historyWindow =
            Grace.Server.Branch.orderedHistoryWindowWithSyntheticBoundaries
                thirdReferenceId
                2
                [|
                    reference 1 firstReferenceId
                    reference 2 secondReferenceId
                    reference 3 thirdReferenceId
                |]

        let window = historyWindow.References

        Assert.Multiple(
            System.Action (fun () ->
                Assert.That(window, Has.Length.EqualTo(2))
                Assert.That(window[0].ReferenceId, Is.EqualTo(secondReferenceId))
                Assert.That(window[1].ReferenceId, Is.EqualTo(thirdReferenceId))

                Assert.That(Grace.Server.Branch.tryGetBasedOnReferenceId window[0], Is.EqualTo(Some firstReferenceId))
                Assert.That(Grace.Server.Branch.tryGetBasedOnReferenceId window[1], Is.EqualTo(Microsoft.FSharp.Core.Option.None))
                Assert.That(historyWindow.SyntheticBasedOnByReferenceId[secondReferenceId], Is.EqualTo(firstReferenceId)))
        )

    /// Verifies that effective History From Materialization Result turns ancestor materialization errors into boundaries.
    [<Test>]
    member _.``effectiveHistoryFromMaterializationResult turns ancestor materialization errors into boundaries``() =
        let targetReferenceId = System.Guid.Parse("11111111-1111-1111-1111-111111111111")
        let ancestorReferenceId = System.Guid.Parse("22222222-2222-2222-2222-222222222222")
        let ancestor = reference 1 ancestorReferenceId
        let materializationError = GraceError.Create "ancestor cannot be materialized" "corr-ancestor"

        let result =
            Grace.Server.Branch.effectiveHistoryFromMaterializationResult
                "src/App.fs"
                targetReferenceId
                ancestor
                (Some targetReferenceId)
                (Error materializationError)

        match result with
        | Error error -> Assert.Fail($"Ancestor materialization error should become a boundary, got {error.Error}.")
        | Ok document ->
            Assert.That(document.BoundaryKind, Is.EqualTo(Some Grace.Server.Branch.unreadableAncestorBoundaryKind))
            Assert.That(document.Document.Content, Is.Empty)
            Assert.That(document.BasedOnReferenceId, Is.EqualTo(Some targetReferenceId))

    /// Verifies that effective History From Materialization Result preserves target materialization errors.
    [<Test>]
    member _.``effectiveHistoryFromMaterializationResult preserves target materialization errors``() =
        let targetReferenceId = System.Guid.Parse("11111111-1111-1111-1111-111111111111")
        let target = reference 1 targetReferenceId
        let materializationError = GraceError.Create "target cannot be materialized" "corr-target"

        let result = Grace.Server.Branch.effectiveHistoryFromMaterializationResult "src/App.fs" targetReferenceId target None (Error materializationError)

        match result with
        | Ok _ -> Assert.Fail("Target materialization error should stop annotation.")
        | Error error -> Assert.That(error.Error, Is.EqualTo(materializationError.Error))

    /// Verifies that try Reserve Retained Annotation Bytes rejects content past total materialization budget.
    [<Test>]
    member _.``tryReserveRetainedAnnotationBytes rejects content past total materialization budget``() =
        let document = { SourceReference = AnnotationSourceReference.Default; Path = "src/App.fs"; Content = [| 1uy |] }

        let accepted =
            Grace.Server.Branch.tryReserveRetainedAnnotationBytes
                (Grace.Server.Branch.MaxRetainedAnnotationMaterializationBytes
                 - 1L)
                document

        let rejected = Grace.Server.Branch.tryReserveRetainedAnnotationBytes Grace.Server.Branch.MaxRetainedAnnotationMaterializationBytes document

        Assert.Multiple(
            System.Action (fun () ->
                match accepted with
                | Ok retainedBytes -> Assert.That(retainedBytes, Is.EqualTo(Grace.Server.Branch.MaxRetainedAnnotationMaterializationBytes))
                | Error boundaryKind -> Assert.Fail($"Expected accepted byte reservation, got boundary {boundaryKind}.")

                match rejected with
                | Ok retainedBytes -> Assert.Fail($"Expected materialization budget boundary, got retained bytes {retainedBytes}.")
                | Error boundaryKind -> Assert.That(boundaryKind, Is.EqualTo(Grace.Server.Branch.annotationMaterializationBudgetBoundaryKind)))
        )
