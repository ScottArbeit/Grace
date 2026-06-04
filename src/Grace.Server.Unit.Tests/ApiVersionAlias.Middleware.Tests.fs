namespace Grace.Server.Tests

open Grace.Server.Middleware
open Grace.Shared
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives
open NUnit.Framework
open System.Threading.Tasks

[<Parallelizable(ParallelScope.All)>]
type ApiVersionAliasMiddlewareTests() =

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

    [<Test>]
    member _.ReleasedApiVersionHeaderPassesThroughUnchanged() =
        task {
            let! mappedHeader, nextInvoked = invokeWithHeader (Some ApiContractVersion.CurrentReleased)
            let mappedHeader: string = mappedHeader

            Assert.That(nextInvoked, Is.True)
            Assert.That(mappedHeader, Is.EqualTo(ApiContractVersion.CurrentReleased))
        }

    [<TestCase("2022-01-01")>]
    [<TestCase("2023-10-01-preview")>]
    member _.UnsupportedApiVersionHeaderPassesThroughForVersioningRejection(headerValue: string) =
        task {
            let! mappedHeader, nextInvoked = invokeWithHeader (Some headerValue)
            let mappedHeader: string = mappedHeader

            Assert.That(nextInvoked, Is.True)
            Assert.That(mappedHeader, Is.EqualTo(headerValue))
        }

    [<Test>]
    member _.MissingApiVersionHeaderStaysMissingForServerDefaulting() =
        task {
            let! mappedHeader, nextInvoked = invokeWithHeader None

            Assert.That(nextInvoked, Is.True)
            Assert.That(mappedHeader, Is.EqualTo(""))
        }
