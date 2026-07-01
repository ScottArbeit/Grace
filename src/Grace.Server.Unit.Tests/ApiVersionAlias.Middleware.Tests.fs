namespace Grace.Server.Tests

open Grace.Server.Middleware
open Grace.Shared
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives
open NUnit.Framework
open System.Threading.Tasks

/// Covers api Version Alias Middleware behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type ApiVersionAliasMiddlewareTests() =

    /// Builds invoke With Header test data for the server unit api Version Alias Middleware scenarios in this file.
    let invokeWithHeader (headerValue: string option) =
        task {
            let context = DefaultHttpContext()

            match headerValue with
            | Some value -> context.Request.Headers[ Constants.ServerApiVersionHeaderKey ] <- StringValues(value)
            | None -> ()

            let mutable nextInvoked = false

            let middleware =
                ApiVersionAliasMiddleware(
                    RequestDelegate (fun _ ->
                        nextInvoked <- true
                        Task.CompletedTask)
                )

            do! middleware.Invoke(context)

            return
                context
                    .Request
                    .Headers[ Constants.ServerApiVersionHeaderKey ]
                    .ToString(),
                nextInvoked
        }

    /// Verifies that alias Api Version Header Maps To Current Released Version Before Next.
    [<TestCase("edge")>]
    [<TestCase("latest")>]
    [<TestCase("EDGE")>]
    member _.AliasApiVersionHeaderMapsToCurrentReleasedVersionBeforeNext(headerValue: string) =
        task {
            let! mappedHeader, nextInvoked = invokeWithHeader (Some headerValue)
            let mappedHeader: string = mappedHeader

            Assert.That(nextInvoked, Is.True)
            Assert.That(mappedHeader, Is.EqualTo(ApiContractVersion.CurrentReleased))
        }

    /// Verifies that released Api Version Header Passes Through Unchanged.
    [<Test>]
    member _.ReleasedApiVersionHeaderPassesThroughUnchanged() =
        task {
            let! mappedHeader, nextInvoked = invokeWithHeader (Some ApiContractVersion.CurrentReleased)
            let mappedHeader: string = mappedHeader

            Assert.That(nextInvoked, Is.True)
            Assert.That(mappedHeader, Is.EqualTo(ApiContractVersion.CurrentReleased))
        }

    /// Verifies that unsupported Api Version Header Passes Through For Versioning Rejection.
    [<TestCase("2022-01-01")>]
    [<TestCase("2023-10-01-preview")>]
    member _.UnsupportedApiVersionHeaderPassesThroughForVersioningRejection(headerValue: string) =
        task {
            let! mappedHeader, nextInvoked = invokeWithHeader (Some headerValue)
            let mappedHeader: string = mappedHeader

            Assert.That(nextInvoked, Is.True)
            Assert.That(mappedHeader, Is.EqualTo(headerValue))
        }

    /// Verifies that missing Api Version Header Stays Missing For Server Defaulting.
    [<Test>]
    member _.MissingApiVersionHeaderStaysMissingForServerDefaulting() =
        task {
            let! mappedHeader, nextInvoked = invokeWithHeader None

            Assert.That(nextInvoked, Is.True)
            Assert.That(mappedHeader, Is.EqualTo(""))
        }
