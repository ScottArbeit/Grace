namespace Grace.Server.Smoke.Tests

open Grace.Server.Tests
open NUnit.Framework
open System
open System.Net.Http

[<TestFixture>]
[<NonParallelizable>]
type Smoke() =
    [<Test; Category("Smoke")>]
    member _.HealthzReturnsSuccess() =
        task {
            let! state = AspireTestHost.startAsync ()

            try
                let! response = state.Client.GetAsync("/healthz")
                let! body = response.Content.ReadAsStringAsync()

                Assert.That(response.IsSuccessStatusCode, Is.True, "Expected /healthz to return success.")
                Assert.That(body, Does.Contain("healthy").IgnoreCase, "Expected /healthz body to indicate health.")
            finally
                state.Client.Dispose()
                do! AspireTestHost.stopAsync (Some state.App)
        }
