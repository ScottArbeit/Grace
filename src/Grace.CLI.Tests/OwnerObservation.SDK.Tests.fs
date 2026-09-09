namespace Grace.CLI.Tests

open System
open System.Net
open System.Net.Sockets
open System.Text.Json
open System.Threading.Tasks
open Grace.SDK
open Grace.Shared
open Grace.Shared.Parameters.Repository
open Grace.Types.Common
open Grace.Types.UsageObservation
open NodaTime.Text
open NUnit.Framework

/// Shares only ordinary fixture functions for the two owner client boundaries.
module internal OwnerObservationClientFixture =
    /// Creates a retained observation with exact boundary quantities and nanosecond timestamps.
    let observation quantity : DirectoryVersionSizeObservation =
        {
            ObservationId = Guid.Parse "11111111-1111-1111-1111-111111111111"
            Scope =
                {
                    OwnerId = Guid.Parse "22222222-2222-2222-2222-222222222222"
                    OrganizationId = Guid.Parse "33333333-3333-3333-3333-333333333333"
                    RepositoryId = Guid.Parse "44444444-4444-4444-4444-444444444444"
                }
            DeclaredLogicalBytes = quantity
            DistinctContentCount = quantity
            EnumerationStartedAt =
                InstantPattern
                    .ExtendedIso
                    .Parse(
                        "2026-09-07T01:02:03.123456789Z"
                    )
                    .Value
            EnumerationFinishedAt =
                InstantPattern
                    .ExtendedIso
                    .Parse(
                        "2026-09-07T01:02:04.987654321Z"
                    )
                    .Value
        }

    /// Builds the exact public response using the existing record and local number policy.
    let body quantity =
        let options = JsonSerializerOptions(Constants.JsonSerializerOptions)

        options.NumberHandling <-
            options.NumberHandling
            ||| Serialization.JsonNumberHandling.WriteAsString

        JsonSerializer.Serialize(GraceReturnValue.Create (observation quantity) "owner-read-test", options)

    /// Carries three explicit historical selectors to the SDK.
    let parameters () =
        let scope = (observation 0L).Scope

        GetRepositoryParameters(
            OwnerId = string scope.OwnerId,
            OrganizationId = string scope.OrganizationId,
            RepositoryId = string scope.RepositoryId,
            CorrelationId = "owner-read-test"
        )

    /// Sends a real loopback HTTP response while capturing the SDK's method, path, query and headers.
    let withServer status responseBody (action: unit -> Task<'T>) =
        task {
            use port = new TcpListener(IPAddress.Loopback, 0)
            port.Start()
            let number = (port.LocalEndpoint :?> IPEndPoint).Port
            port.Stop()
            use listener = new HttpListener()
            let url = $"http://localhost:{number}/"
            listener.Prefixes.Add url
            listener.Start()
            let oldUri = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.GraceServerUri)
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.GraceServerUri, url.TrimEnd('/'))

            let serve =
                task {
                    let! context =
                        listener
                            .GetContextAsync()
                            .WaitAsync(TimeSpan.FromSeconds 15.)

                    let captured = context.Request.HttpMethod, context.Request.RawUrl, context.Request.Headers["X-Correlation-Id"]
                    context.Response.StatusCode <- status
                    context.Response.ContentType <- "application/json"
                    let bytes = Text.Encoding.UTF8.GetBytes(responseBody: string)
                    context.Response.ContentLength64 <- int64 bytes.Length
                    do! context.Response.OutputStream.WriteAsync bytes
                    context.Response.Close()
                    return captured
                }

            try
                let! result = action ()
                let! captured = serve
                return result, captured
            finally
                Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.GraceServerUri, oldUri)
                listener.Stop()
        }

/// Executes the F# SDK GET and deserializer rather than comparing a route string alone.
[<NonParallelizable>]
type OwnerObservationSdkTests() =
    /// Quantities and Instants survive the actual request and Grace envelope reader.
    [<TestCase(0L)>]
    [<TestCase(9007199254740993L)>]
    [<TestCase(Int64.MaxValue)>]
    member _.``GET preserves complete observation``(quantity) =
        task {
            let expected = OwnerObservationClientFixture.observation quantity

            let! result, (method, url, _) =
                OwnerObservationClientFixture.withServer 200 (OwnerObservationClientFixture.body quantity) (fun () ->
                    Owner.GetDirectoryVersionObservation(expected.ObservationId, OwnerObservationClientFixture.parameters ()))

            Assert.That(method, Is.EqualTo "GET")

            Assert.That(
                url,
                Is.EqualTo
                    $"/owner/usage/directory-version-observations/{expected.ObservationId}?OwnerId={expected.Scope.OwnerId}&OrganizationId={expected.Scope.OrganizationId}&RepositoryId={expected.Scope.RepositoryId}"
            )

            match result with
            | Ok value -> Assert.That(value.ReturnValue, Is.EqualTo expected)
            | Error error -> Assert.Fail error.Error
        }

    /// Invalid explicit selectors return before HTTP or current-configuration fallback.
    [<Test>]
    member _.``invalid selectors cannot use configured defaults``() =
        task {
            let setters: (GetRepositoryParameters -> unit) list =
                [
                    (fun p -> p.OwnerId <- "")
                    (fun p -> p.OrganizationId <- "bad")
                    (fun p -> p.RepositoryId <- string Guid.Empty)
                    (fun p -> p.OwnerName <- "name")
                    (fun p -> p.OrganizationName <- "name")
                    (fun p -> p.RepositoryName <- "name")
                ]

            let mutable pending = setters

            while not pending.IsEmpty do
                let parameters = OwnerObservationClientFixture.parameters ()
                pending.Head parameters
                pending <- pending.Tail

                let! result =
                    Owner.GetDirectoryVersionObservation(
                        (OwnerObservationClientFixture.observation 0L)
                            .ObservationId,
                        parameters
                    )

                Assert.That(Result.isError result, Is.True)
        }

    /// The SDK keeps server errors as errors with no observation.
    [<Test>]
    member _.``GET retains server error conventions``() =
        task {
            let response = JsonSerializer.Serialize(GraceError.Create "Observation was not found." "owner-read-test", Constants.JsonSerializerOptions)

            let! result, _ =
                OwnerObservationClientFixture.withServer 404 response (fun () ->
                    Owner.GetDirectoryVersionObservation(Guid.NewGuid(), OwnerObservationClientFixture.parameters ()))

            match result with
            | Error error -> Assert.That(error.Error, Is.EqualTo "Observation was not found.")
            | Ok _ -> Assert.Fail "Unexpected observation."
        }
