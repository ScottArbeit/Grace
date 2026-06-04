namespace Grace.Server.Tests

open Grace.Server.Middleware
open Grace.Shared
open Grace.Types.Common
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives
open NUnit.Framework
open System.IO
open System.Text.Json
open System.Threading.Tasks

[<Parallelizable(ParallelScope.All)>]
type SdkLifecyclePolicyTests() =

    let identity (clientType: string option) (clientVersion: string option) : SdkLifecyclePolicy.ClientIdentity =
        { ClientType = clientType; ClientVersion = clientVersion }

    [<Test>]
    member _.SupportedCliVersionContinuesWithoutLifecycleHeaders() =
        let decision = SdkLifecyclePolicy.decide (identity (Some "CLI") (Some "0.2.0"))

        Assert.That(decision, Is.EqualTo(SdkLifecyclePolicy.Continue))

    [<Test>]
    member _.DeprecatedCliVersionContinuesWithLifecycleHeaders() =
        let decision = SdkLifecyclePolicy.decide (identity (Some "CLI") (Some "0.1.2.3"))

        match decision with
        | SdkLifecyclePolicy.ContinueWithHeaders headers ->
            Assert.That(headers.Status, Is.EqualTo(SdkLifecyclePolicy.DeprecatedStatus))
            Assert.That(headers.MinimumSupportedVersion, Is.EqualTo(SdkLifecyclePolicy.MinimumSupportedCliVersion))
            Assert.That(headers.RecommendedVersion, Is.EqualTo(SdkLifecyclePolicy.RecommendedCliVersion))
            Assert.That(headers.Message, Does.Contain("deprecated"))
        | other -> Assert.Fail($"Expected deprecated lifecycle headers, got {other}.")

    [<Test>]
    member _.UnsupportedCliVersionRejectsWithStructuredClientDetails() =
        let decision = SdkLifecyclePolicy.decide (identity (Some "cli") (Some "0.0.9"))

        match decision with
        | SdkLifecyclePolicy.RejectUnsupported unsupportedClient ->
            Assert.That(unsupportedClient.ClientType, Is.EqualTo(SdkLifecyclePolicy.KnownClientTypeCli))
            Assert.That(unsupportedClient.ClientVersion, Is.EqualTo("0.0.9"))
            Assert.That(unsupportedClient.MinimumSupportedVersion, Is.EqualTo(SdkLifecyclePolicy.MinimumSupportedCliVersion))
            Assert.That(unsupportedClient.RecommendedVersion, Is.EqualTo(SdkLifecyclePolicy.RecommendedCliVersion))
            Assert.That(unsupportedClient.Message, Does.Contain("no longer supported"))
        | other -> Assert.Fail($"Expected unsupported lifecycle rejection, got {other}.")

    [<Test>]
    member _.MissingUnknownAndMalformedClientIdentityContinues() =
        let cases =
            [
                identity None None
                identity (Some "CLI") None
                identity None (Some "0.0.9")
                identity (Some "Custom") (Some "0.0.9")
                identity (Some "CLI") (Some "not-a-version")
            ]

        for case in cases do
            Assert.That(SdkLifecyclePolicy.decide case, Is.EqualTo(SdkLifecyclePolicy.Continue))

[<Parallelizable(ParallelScope.All)>]
type SdkLifecycleMiddlewareTests() =

    let getHeader (context: DefaultHttpContext) headerName = context.Response.Headers[ headerName ].ToString()

    let createContext (clientType: string option) (clientVersion: string option) =
        let context = DefaultHttpContext()
        context.Response.Body <- new MemoryStream()
        context.Items.Add(Constants.CorrelationId, "corr-sdk-lifecycle")

        match clientType with
        | Some value -> context.Request.Headers[ Constants.ClientTypeHeaderKey ] <- StringValues(value)
        | None -> ()

        match clientVersion with
        | Some value -> context.Request.Headers[ Constants.ClientVersionHeaderKey ] <- StringValues(value)
        | None -> ()

        context

    let responseBody (context: DefaultHttpContext) =
        context.Response.Body.Seek(0L, SeekOrigin.Begin)
        |> ignore

        use reader = new StreamReader(context.Response.Body, leaveOpen = true)
        reader.ReadToEnd()

    [<Test>]
    member _.DeprecatedCliVersionAddsLifecycleHeadersAndInvokesNext() =
        task {
            let context = createContext (Some "CLI") (Some "0.1.2.3")
            let mutable nextInvoked = false

            let middleware =
                SdkLifecycleMiddleware(
                    RequestDelegate (fun ctx ->
                        nextInvoked <- true
                        ctx.Response.StatusCode <- StatusCodes.Status200OK
                        Task.CompletedTask)
                )

            do! middleware.Invoke(context)
            do! context.Response.StartAsync()

            Assert.That(nextInvoked, Is.True)
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK))
            Assert.That(getHeader context Constants.SdkLifecycleStatusHeaderKey, Is.EqualTo(SdkLifecyclePolicy.DeprecatedStatus))
            Assert.That(getHeader context Constants.SdkLifecycleMinimumVersionHeaderKey, Is.EqualTo(SdkLifecyclePolicy.MinimumSupportedCliVersion))
            Assert.That(getHeader context Constants.SdkLifecycleRecommendedVersionHeaderKey, Is.EqualTo(SdkLifecyclePolicy.RecommendedCliVersion))
            Assert.That(getHeader context Constants.SdkLifecycleMessageHeaderKey, Does.Contain("deprecated"))
        }

    [<Test>]
    member _.DeprecatedCliVersionAddsLifecycleHeadersToFailingResponse() =
        task {
            let context = createContext (Some "CLI") (Some "0.1.2.3")

            let middleware =
                SdkLifecycleMiddleware(
                    RequestDelegate (fun ctx ->
                        task {
                            ctx.Response.StatusCode <- StatusCodes.Status400BadRequest
                            do! ctx.Response.WriteAsync("downstream validation failed")
                        }
                        :> Task)
                )

            do! middleware.Invoke(context)

            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest))
            Assert.That(getHeader context Constants.SdkLifecycleStatusHeaderKey, Is.EqualTo(SdkLifecyclePolicy.DeprecatedStatus))
            Assert.That(getHeader context Constants.SdkLifecycleMessageHeaderKey, Does.Contain("deprecated"))
            Assert.That(responseBody context, Is.EqualTo("downstream validation failed"))
        }

    [<Test>]
    member _.UnsupportedCliVersionReturnsUpgradeRequiredGraceErrorWithoutInvokingNext() =
        task {
            let context = createContext (Some "CLI") (Some "0.0.9")
            let mutable nextInvoked = false

            let middleware =
                SdkLifecycleMiddleware(
                    RequestDelegate (fun _ ->
                        nextInvoked <- true
                        Task.CompletedTask)
                )

            do! middleware.Invoke(context)

            let body = responseBody context
            let error = JsonSerializer.Deserialize<GraceError>(body, Constants.JsonSerializerOptions)

            Assert.That(nextInvoked, Is.False)
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status426UpgradeRequired))
            Assert.That(context.Response.ContentType, Is.EqualTo("application/json; charset=utf-8"))
            Assert.That(getHeader context Constants.SdkLifecycleStatusHeaderKey, Is.EqualTo(SdkLifecyclePolicy.UnsupportedStatus))
            Assert.That(error.Error, Is.EqualTo("UnsupportedClientVersion"))
            Assert.That(error.CorrelationId, Is.EqualTo("corr-sdk-lifecycle"))
            Assert.That(error.Properties[ "clientType" ].ToString(), Is.EqualTo("CLI"))
            Assert.That(error.Properties[ "clientVersion" ].ToString(), Is.EqualTo("0.0.9"))

            Assert.That(
                error
                    .Properties[ "minimumSupportedVersion" ]
                    .ToString(),
                Is.EqualTo(SdkLifecyclePolicy.MinimumSupportedCliVersion)
            )

            Assert.That(
                error
                    .Properties[ "recommendedVersion" ]
                    .ToString(),
                Is.EqualTo(SdkLifecyclePolicy.RecommendedCliVersion)
            )
        }

    [<Test>]
    member _.UnknownMissingAndMalformedClientIdentityDoesNotBlockRawClients() =
        task {
            let cases =
                [
                    createContext None None
                    createContext (Some "CLI") None
                    createContext (Some "Custom") (Some "0.0.9")
                    createContext (Some "CLI") (Some "not-a-version")
                ]

            for context in cases do
                let mutable nextInvoked = false

                let middleware =
                    SdkLifecycleMiddleware(
                        RequestDelegate (fun ctx ->
                            nextInvoked <- true
                            ctx.Response.StatusCode <- StatusCodes.Status204NoContent
                            Task.CompletedTask)
                    )

                do! middleware.Invoke(context)

                Assert.That(nextInvoked, Is.True)
                Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status204NoContent))
                Assert.That(getHeader context Constants.SdkLifecycleStatusHeaderKey, Is.EqualTo(""))
        }
