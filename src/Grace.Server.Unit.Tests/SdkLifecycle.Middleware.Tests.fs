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

/// Covers sdk Lifecycle Policy behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type SdkLifecyclePolicyTests() =

    /// Builds identity test data for the server unit sdk Lifecycle Middleware scenarios in this file.
    let identity (clientType: string option) (clientVersion: string option) : SdkLifecyclePolicy.ClientIdentity =
        { ClientType = clientType; ClientVersion = clientVersion }

    /// Verifies that supported Cli Version Continues Without Lifecycle Headers.
    [<Test>]
    member _.SupportedCliVersionContinuesWithoutLifecycleHeaders() =
        let decision = SdkLifecyclePolicy.decide (identity (Some "CLI") (Some "0.2.0"))

        Assert.That(decision, Is.EqualTo(SdkLifecyclePolicy.Continue))

    /// Verifies that deprecated Cli Version Continues With Lifecycle Headers.
    [<Test>]
    member _.DeprecatedCliVersionContinuesWithLifecycleHeaders() =
        let decision = SdkLifecyclePolicy.decide (identity (Some "CLI") (Some "0.1.2.3"))

        match decision with
        | SdkLifecyclePolicy.ContinueWithHeaders headers ->
            Assert.That(headers.Status, Is.EqualTo(SdkLifecyclePolicy.DeprecatedStatus))
            Assert.That(headers.UnsupportedAfter, Is.EqualTo(SdkLifecyclePolicy.CliUnsupportedAfter))
            Assert.That(headers.MinimumSupportedVersion, Is.EqualTo(SdkLifecyclePolicy.MinimumSupportedCliVersion))
            Assert.That(headers.RecommendedVersion, Is.EqualTo(SdkLifecyclePolicy.RecommendedCliVersion))
            Assert.That(headers.UpdateUrl, Is.EqualTo(SdkLifecyclePolicy.CliUpdateUrl))
        | other -> Assert.Fail($"Expected deprecated lifecycle headers, got {other}.")

    /// Verifies that unsupported Cli Version Rejects With Structured Client Details.
    [<Test>]
    member _.UnsupportedCliVersionRejectsWithStructuredClientDetails() =
        let decision = SdkLifecyclePolicy.decide (identity (Some "cli") (Some "0.0.9"))

        match decision with
        | SdkLifecyclePolicy.RejectUnsupported unsupportedClient ->
            Assert.That(unsupportedClient.ClientType, Is.EqualTo(SdkLifecyclePolicy.KnownClientTypeCli))
            Assert.That(unsupportedClient.ClientVersion, Is.EqualTo("0.0.9"))
            Assert.That(unsupportedClient.UnsupportedAfter, Is.EqualTo(SdkLifecyclePolicy.CliUnsupportedAfter))
            Assert.That(unsupportedClient.MinimumSupportedVersion, Is.EqualTo(SdkLifecyclePolicy.MinimumSupportedCliVersion))
            Assert.That(unsupportedClient.RecommendedVersion, Is.EqualTo(SdkLifecyclePolicy.RecommendedCliVersion))
            Assert.That(unsupportedClient.UpdateUrl, Is.EqualTo(SdkLifecyclePolicy.CliUpdateUrl))
            Assert.That(unsupportedClient.Message, Does.Contain("no longer supported"))
        | other -> Assert.Fail($"Expected unsupported lifecycle rejection, got {other}.")

    /// Verifies that missing Unknown And Malformed Client Identity Continues.
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

/// Covers sdk Lifecycle Middleware behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type SdkLifecycleMiddlewareTests() =

    let getHeader (context: DefaultHttpContext) headerName = context.Response.Headers[ headerName ].ToString()

    /// Constructs context fixtures used by the server unit sdk Lifecycle Middleware assertions.
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

    /// Builds response Body test data for the server unit sdk Lifecycle Middleware scenarios in this file.
    let responseBody (context: DefaultHttpContext) =
        context.Response.Body.Seek(0L, SeekOrigin.Begin)
        |> ignore

        use reader = new StreamReader(context.Response.Body, leaveOpen = true)
        reader.ReadToEnd()

    /// Verifies that deprecated Cli Version Adds Lifecycle Headers And Invokes Next.
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
            Assert.That(getHeader context Constants.SdkLifecycleUnsupportedAfterHeaderKey, Is.EqualTo(SdkLifecyclePolicy.CliUnsupportedAfter))
            Assert.That(getHeader context Constants.SdkLifecycleMinimumVersionHeaderKey, Is.EqualTo(SdkLifecyclePolicy.MinimumSupportedCliVersion))
            Assert.That(getHeader context Constants.SdkLifecycleRecommendedVersionHeaderKey, Is.EqualTo(SdkLifecyclePolicy.RecommendedCliVersion))
            Assert.That(getHeader context Constants.SdkLifecycleUpdateUrlHeaderKey, Is.EqualTo(SdkLifecyclePolicy.CliUpdateUrl))
        }

    /// Verifies that deprecated Cli Version Adds Lifecycle Headers To Failing Response.
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
            Assert.That(getHeader context Constants.SdkLifecycleUnsupportedAfterHeaderKey, Is.EqualTo(SdkLifecyclePolicy.CliUnsupportedAfter))
            Assert.That(getHeader context Constants.SdkLifecycleUpdateUrlHeaderKey, Is.EqualTo(SdkLifecyclePolicy.CliUpdateUrl))
            Assert.That(responseBody context, Is.EqualTo("downstream validation failed"))
        }

    /// Verifies that unsupported Cli Version Returns Upgrade Required Grace Error Without Invoking Next.
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
            Assert.That(getHeader context Constants.SdkLifecycleUnsupportedAfterHeaderKey, Is.EqualTo(SdkLifecyclePolicy.CliUnsupportedAfter))
            Assert.That(getHeader context Constants.SdkLifecycleMinimumVersionHeaderKey, Is.EqualTo(SdkLifecyclePolicy.MinimumSupportedCliVersion))
            Assert.That(getHeader context Constants.SdkLifecycleRecommendedVersionHeaderKey, Is.EqualTo(SdkLifecyclePolicy.RecommendedCliVersion))
            Assert.That(getHeader context Constants.SdkLifecycleUpdateUrlHeaderKey, Is.EqualTo(SdkLifecyclePolicy.CliUpdateUrl))
            Assert.That(error.Error, Is.EqualTo("UnsupportedClientVersion"))
            Assert.That(error.CorrelationId, Is.EqualTo("corr-sdk-lifecycle"))
            Assert.That(error.Properties[ "clientType" ].ToString(), Is.EqualTo("CLI"))
            Assert.That(error.Properties[ "clientVersion" ].ToString(), Is.EqualTo("0.0.9"))
            Assert.That(error.Properties[ "unsupportedAfter" ].ToString(), Is.EqualTo(SdkLifecyclePolicy.CliUnsupportedAfter))

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

            Assert.That(error.Properties[ "updateUrl" ].ToString(), Is.EqualTo(SdkLifecyclePolicy.CliUpdateUrl))
        }

    /// Verifies that unknown Missing And Malformed Client Identity Does Not Block Raw Clients.
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
