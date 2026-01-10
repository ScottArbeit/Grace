namespace Grace.Server.Tests

open NUnit.Framework

[<TestFixture>]
[<NonParallelizable>]
type Smoke() =
    [<Test; Category("Smoke")>]
    member _.HealthzReturnsSuccess() =
        task {
            let! response = Services.Client.GetAsync("/healthz")
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.IsSuccessStatusCode, Is.True, "Expected /healthz to return success.")
            Assert.That(body, Does.Contain("healthy").IgnoreCase, "Expected /healthz body to indicate health.")
        }
