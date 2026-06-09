namespace Grace.Server.Tests

open Grace.Shared
open Grace.Types
open Grace.Types.Common
open Grace.Types.Reference
open Microsoft.AspNetCore.Http
open NUnit.Framework
open System.Security.Claims

[<TestFixture>]
type MetadataCreationTests() =

    member private _.CreateContext() =
        let context = DefaultHttpContext()
        context.Items[ Constants.CorrelationId ] <- "corr-server"
        context.User <- ClaimsPrincipal(ClaimsIdentity([| Claim(ClaimTypes.Name, "tester") |], "test"))
        context

    [<Test>]
    member this.``createMetadata maps Grace client headers to CLI client type``() =
        let context = this.CreateContext()
        context.Request.Headers[ Constants.ClientTypeHeaderKey ] <- "CLI"
        context.Request.Headers[ Constants.ClientVersionHeaderKey ] <- "0.1.2.3"

        let metadata = Grace.Server.Services.createMetadata context

        match metadata.ClientType with
        | Some (ClientType.CLI version) -> Assert.That(version, Is.EqualTo("0.1.2.3"))
        | other -> Assert.Fail($"Expected CLI client metadata, got {other}.")

    [<Test>]
    member this.``createMetadata leaves client type unset when headers are absent``() =
        let context = this.CreateContext()

        let metadata = Grace.Server.Services.createMetadata context

        Assert.That(metadata.ClientType, Is.EqualTo(Microsoft.FSharp.Core.Option.None))

    [<Test>]
    member this.``createMetadata leaves client type unset when CLI version header is missing``() =
        let context = this.CreateContext()
        context.Request.Headers[ Constants.ClientTypeHeaderKey ] <- "CLI"

        let metadata = Grace.Server.Services.createMetadata context

        Assert.That(metadata.ClientType, Is.EqualTo(Microsoft.FSharp.Core.Option.None))

    [<Test>]
    member this.``createMetadata uses http principal when identity name is absent``() =
        let context = this.CreateContext()
        context.User <- ClaimsPrincipal(ClaimsIdentity([||], "test"))

        let metadata = Grace.Server.Services.createMetadata context

        Assert.That(metadata.Principal, Is.EqualTo("http"))

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


[<TestFixture>]
type BranchAnnotationServerTests() =

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
