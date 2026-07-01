namespace Grace.Server.Tests

open NUnit.Framework
open System.Net

/// Covers smoke scenarios.
[<TestFixture>]
[<NonParallelizable>]
type Smoke() =
    /// Verifies the healthz returns success scenario.
    [<Test; Category("Smoke")>]
    member _.HealthzReturnsSuccess() =
        task {
            let! response = Services.Client.GetAsync("/healthz")
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "Expected /healthz to return success.")
            Assert.That(response.Content.Headers.ContentType.MediaType, Is.EqualTo("text/html"))
            Assert.That(body, Does.Contain("healthy").IgnoreCase, "Expected /healthz body to indicate health.")
        }
