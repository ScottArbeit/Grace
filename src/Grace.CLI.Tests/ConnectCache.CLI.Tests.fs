namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.Command
open Grace.CLI.Text
open Grace.Shared
open Grace.Shared.Parameters.Cache
open Grace.Types.Common
open NUnit.Framework
open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Json
open System.Threading
open System.Threading.Tasks

/// Covers Cache-required Connect selection and loopback endpoint validation.
module ConnectCacheTests =

    /// Parses Connect arguments through the public root command.
    let private parse arguments = GraceCommand.rootCommand.Parse(Array.append [| "connect" |] arguments)

    /// Represents the deterministic public key returned by the fake Cache process.
    let private cachePublicKey = { Kty = "EC"; Crv = "P-256"; X = "cache-x"; Y = "cache-y" }

    /// Creates one deterministic Server preparation bound to the requested artifact and permit.
    let private createPreparation repositoryId directoryVersionId permit hashCharacter size =
        {
            Descriptor =
                {
                    RepositoryId = string repositoryId
                    DirectoryVersionId = string directoryVersionId
                    Kind = "DirectoryVersionZip"
                    Sha256 = String.replicate 64 hashCharacter
                    Size = size
                }
            Permit = permit
            PermitExpiresAt = DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)
            RedemptionBytes = "unused-by-cli"
        }

    /// Creates one HTTP response with JSON content serialized by Grace's shared settings.
    let private jsonResponse status value =
        let response = new HttpResponseMessage(status)
        response.Content <- JsonContent.Create(value, options = Constants.JsonSerializerOptions)
        response

    /// Creates one successful Cache artifact response containing the supplied ZIP bytes.
    let private zipResponse bytes =
        let response = new HttpResponseMessage(HttpStatusCode.OK)
        response.Content <- new ByteArrayContent(bytes)
        response

    /// Reads a supplied Cache ZIP stream into exact test bytes.
    let private readBytes (stream: Stream) =
        task {
            use target = new MemoryStream()
            do! stream.CopyToAsync(target)
            return target.ToArray()
        }

    /// Direct Connect does not inspect Cache environment configuration.
    [<Test>]
    let ``direct connect never reads the Cache environment`` () =
        let mutable environmentRead = false

        let result =
            ConnectCache.selectRetrieval (parse [||]) (fun _ ->
                environmentRead <- true
                invalidOp "Direct Connect must not inspect GRACE_CACHE_URI.")

        match result with
        | Ok ConnectCache.Direct -> ()
        | other -> Assert.Fail($"Unexpected Direct selection: {other}.")

        environmentRead |> should equal false

    /// An explicit Cache endpoint is meaningful only when Cache-required retrieval is selected.
    [<Test>]
    let ``cache uri without cache required is rejected without reading the environment`` () =
        let mutable environmentRead = false

        let result =
            ConnectCache.selectRetrieval
                (parse [| OptionName.CacheUri
                          "http://localhost:5341/" |])
                (fun _ ->
                    environmentRead <- true
                    "http://localhost:6341/")

        match result with
        | Ok selection -> Assert.Fail($"Expected Cache selection failure, got {selection}.")
        | Error error ->
            error
            |> should contain "requires --cache-required"

        environmentRead |> should equal false

    /// Explicit CLI configuration wins without consulting the environment.
    [<Test>]
    let ``cache required prefers the explicit Cache URI`` () =
        let mutable environmentRead = false

        let result =
            ConnectCache.selectRetrieval
                (parse [| OptionName.CacheRequired
                          OptionName.CacheUri
                          "http://127.0.0.1:5341/" |])
                (fun _ ->
                    environmentRead <- true
                    "http://localhost:6341/")

        match result with
        | Ok (ConnectCache.Required uri) ->
            uri.AbsoluteUri
            |> should equal "http://127.0.0.1:5341/"
        | other -> Assert.Fail($"Unexpected Cache selection: {other}.")

        environmentRead |> should equal false

    /// Invalid explicit input is terminal and cannot fall back to a valid environment endpoint.
    [<Test>]
    let ``cache required rejects invalid explicit Cache URI without environment fallback`` () =
        let mutable environmentRead = false

        let result =
            ConnectCache.selectRetrieval
                (parse [| OptionName.CacheRequired
                          OptionName.CacheUri
                          "https://localhost:5341/" |])
                (fun _ ->
                    environmentRead <- true
                    "http://localhost:6341/")

        match result with
        | Ok selection -> Assert.Fail($"Expected invalid explicit Cache URI, got {selection}.")
        | Error error ->
            error
            |> should contain "absolute loopback HTTP URI"

        environmentRead |> should equal false

    /// Cache-required Connect uses GRACE_CACHE_URI only when no explicit URI was supplied.
    [<Test>]
    let ``cache required uses the Cache environment URI when CLI URI is absent`` () =
        let mutable requestedName = String.Empty

        let result =
            ConnectCache.selectRetrieval (parse [| OptionName.CacheRequired |]) (fun name ->
                requestedName <- name
                "http://[::1]:5341/")

        match result with
        | Ok (ConnectCache.Required uri) ->
            uri.AbsoluteUri
            |> should equal "http://[::1]:5341/"
        | other -> Assert.Fail($"Unexpected Cache selection: {other}.")

        requestedName |> should equal "GRACE_CACHE_URI"

    /// Cache-required Connect accepts only the three explicit-port loopback HTTP forms.
    [<Test>]
    let ``cache URI validation accepts exact loopback HTTP endpoints`` () =
        let accepted =
            [|
                "http://127.0.0.1:5341"
                "http://localhost:5341/"
                "http://LOCALHOST:80/"
                "http://[::1]:5341/"
            |]

        for value in accepted do
            match ConnectCache.validateCacheUri value with
            | Ok _ -> ()
            | Error error -> Assert.Fail($"Expected '{value}' to be accepted: {error}")

    /// Cache URI validation rejects every endpoint shape outside the frozen loopback contract.
    [<Test>]
    let ``cache URI validation rejects unsupported endpoint shapes`` () =
        let rejected =
            [|
                "https://localhost:5341/"
                "http://localhost/"
                "http://example.com:5341/"
                "http://127.0.0.2:5341/"
                "http://127.1:5341/"
                "http://[0:0:0:0:0:0:0:1]:5341/"
                "http://user@localhost:5341/"
                "http://localhost:5341/cache"
                "http://localhost:5341/."
                "http://localhost:5341/cache/.."
                "http://localhost:5341/?query=true"
                "http://localhost:5341/#fragment"
                "not-a-uri"
            |]

        for value in rejected do
            match ConnectCache.validateCacheUri value with
            | Error _ -> ()
            | Ok uri -> Assert.Fail($"Expected '{value}' to be rejected, got {uri}.")

    /// A Cache hit returns the exact response stream without entering preparation or fill.
    [<Test>]
    let ``cache hit consumes one exact verified GET without fill`` () =
        task {
            let repositoryId = Guid.NewGuid()
            let directoryVersionId = Guid.NewGuid()
            let expectedBytes = [| 1uy; 3uy; 5uy; 7uy |]
            let requests = ResizeArray<string>()
            let mutable preparationCalled = false
            let mutable consumed = 0

            let dependencies: ConnectCache.Dependencies =
                {
                    Send =
                        fun request _ ->
                            task {
                                requests.Add($"{request.Method} {request.RequestUri.AbsolutePath}")
                                return zipResponse expectedBytes
                            }
                    Prepare =
                        fun _ ->
                            preparationCalled <- true
                            Task.FromResult(Error(GraceError.Create "unexpected preparation" "cache-hit"))
                    StartTimer = fun () -> fun () -> TimeSpan.Zero
                    Delay = fun _ _ -> Task.CompletedTask
                }

            let! result =
                ConnectCache.useVerifiedZipWith
                    dependencies
                    (Uri("http://localhost:5341/"))
                    (string repositoryId)
                    (string directoryVersionId)
                    "cache-hit"
                    CancellationToken.None
                    (fun stream ->
                        consumed <- consumed + 1
                        readBytes stream)

            match result with
            | Error error -> Assert.Fail($"Unexpected Cache hit failure: {error.Error}")
            | Ok actualBytes -> actualBytes |> should equal expectedBytes

            requests
            |> Seq.toArray
            |> should
                equal
                [|
                    $"GET /repositories/{repositoryId}/directory-version-zips/{directoryVersionId}"
                |]

            preparationCalled |> should equal false
            consumed |> should equal 1
        }

    /// A Cache miss binds one Server permit to the Cache key, fills with only that permit, then performs an independent GET.
    [<Test>]
    let ``cache miss prepares fills and independently gets the verified artifact`` () =
        task {
            let repositoryId = Guid.NewGuid()
            let directoryVersionId = Guid.NewGuid()
            let expectedBytes = [| 2uy; 4uy; 6uy; 8uy |]
            let requests = ResizeArray<string>()
            let mutable requestNumber = 0
            let mutable preparationCount = 0

            let preparation = createPreparation repositoryId directoryVersionId "permit-1" "a" (int64 expectedBytes.Length)

            let dependencies: ConnectCache.Dependencies =
                {
                    Send =
                        fun request cancellationToken ->
                            task {
                                requestNumber <- requestNumber + 1
                                requests.Add($"{request.Method} {request.RequestUri.AbsolutePath}")

                                match requestNumber with
                                | 1 -> return new HttpResponseMessage(HttpStatusCode.NotFound)
                                | 2 -> return jsonResponse HttpStatusCode.OK cachePublicKey
                                | 3 ->
                                    let! body = request.Content.ReadAsStringAsync(cancellationToken)
                                    body |> should contain "permit-1"
                                    body |> should not' (contain "Descriptor")
                                    body |> should not' (contain "CachePublicKey")
                                    return new HttpResponseMessage(HttpStatusCode.NoContent)
                                | 4 -> return zipResponse expectedBytes
                                | other -> return raise (InvalidOperationException($"Unexpected Cache request {other}."))
                            }
                    Prepare =
                        fun parameters ->
                            task {
                                preparationCount <- preparationCount + 1

                                parameters.RepositoryId
                                |> should equal (string repositoryId)

                                parameters.DirectoryVersionId
                                |> should equal (string directoryVersionId)

                                parameters.CachePublicKey
                                |> should equal cachePublicKey

                                parameters.CorrelationId
                                |> should equal "cache-miss"

                                return Ok(GraceReturnValue.Create preparation "cache-miss")
                            }
                    StartTimer = fun () -> fun () -> TimeSpan.Zero
                    Delay = fun _ _ -> Task.CompletedTask
                }

            let! result =
                ConnectCache.useVerifiedZipWith
                    dependencies
                    (Uri("http://localhost:5341/"))
                    (string repositoryId)
                    (string directoryVersionId)
                    "cache-miss"
                    CancellationToken.None
                    readBytes

            match result with
            | Error error -> Assert.Fail($"Unexpected Cache miss-to-hit failure: {error.Error}")
            | Ok actualBytes -> actualBytes |> should equal expectedBytes

            requests
            |> Seq.toArray
            |> should
                equal
                [|
                    $"GET /repositories/{repositoryId}/directory-version-zips/{directoryVersionId}"
                    "GET /fill-public-key"
                    $"POST /repositories/{repositoryId}/directory-version-zips/{directoryVersionId}/fill"
                    $"GET /repositories/{repositoryId}/directory-version-zips/{directoryVersionId}"
                |]

            preparationCount |> should equal 1
        }

    /// Capacity rejection retries with a fresh Server permit and bounded monotonic delay before the final GET.
    [<Test>]
    let ``cache fill capacity retries with a fresh Server permit`` () =
        task {
            let repositoryId = Guid.NewGuid()
            let directoryVersionId = Guid.NewGuid()
            let expectedBytes = [| 9uy; 8uy; 7uy |]
            let postedPermits = ResizeArray<string>()
            let delays = ResizeArray<TimeSpan>()
            let mutable requestNumber = 0
            let mutable preparationCount = 0
            let mutable elapsed = TimeSpan.Zero

            let preparationForPermit permit = createPreparation repositoryId directoryVersionId permit "b" (int64 expectedBytes.Length)

            let dependencies: ConnectCache.Dependencies =
                {
                    Send =
                        fun request cancellationToken ->
                            task {
                                requestNumber <- requestNumber + 1

                                match requestNumber with
                                | 1 -> return new HttpResponseMessage(HttpStatusCode.NotFound)
                                | 2 -> return jsonResponse HttpStatusCode.OK cachePublicKey
                                | capacityResponse when capacityResponse >= 3 && capacityResponse <= 9 ->
                                    let! body = request.Content.ReadAsStringAsync(cancellationToken)
                                    postedPermits.Add(body)

                                    return
                                        jsonResponse
                                            HttpStatusCode.TooManyRequests
                                            { Code = "CacheFillCapacityExceeded"; Detail = "Distinct fill capacity is full." }
                                | 10 ->
                                    let! body = request.Content.ReadAsStringAsync(cancellationToken)
                                    postedPermits.Add(body)
                                    return new HttpResponseMessage(HttpStatusCode.NoContent)
                                | 11 -> return zipResponse expectedBytes
                                | other -> return raise (InvalidOperationException($"Unexpected Cache request {other}."))
                            }
                    Prepare =
                        fun _ ->
                            preparationCount <- preparationCount + 1
                            Task.FromResult(Ok(GraceReturnValue.Create (preparationForPermit $"permit-{preparationCount}") "cache-retry"))
                    StartTimer = fun () -> fun () -> elapsed
                    Delay =
                        fun delay _ ->
                            delays.Add(delay)
                            elapsed <- elapsed + delay
                            Task.CompletedTask
                }

            let! result =
                ConnectCache.useVerifiedZipWith
                    dependencies
                    (Uri("http://localhost:5341/"))
                    (string repositoryId)
                    (string directoryVersionId)
                    "cache-retry"
                    CancellationToken.None
                    readBytes

            match result with
            | Error error -> Assert.Fail($"Unexpected Cache retry failure: {error.Error}")
            | Ok actualBytes -> actualBytes |> should equal expectedBytes

            preparationCount |> should equal 8
            postedPermits.Count |> should equal 8
            postedPermits[0] |> should contain "permit-1"
            postedPermits[7] |> should contain "permit-8"

            delays
            |> Seq.toArray
            |> should
                equal
                [|
                    TimeSpan.FromMilliseconds(100.0)
                    TimeSpan.FromMilliseconds(200.0)
                    TimeSpan.FromMilliseconds(400.0)
                    TimeSpan.FromMilliseconds(800.0)
                    TimeSpan.FromMilliseconds(1600.0)
                    TimeSpan.FromSeconds(2.0)
                    TimeSpan.FromSeconds(2.0)
                |]
        }

    /// A 429 with any other typed Cache problem is terminal and never consumes retry time.
    [<Test>]
    let ``cache fill does not retry another 429 problem code`` () =
        task {
            let repositoryId = Guid.NewGuid()
            let directoryVersionId = Guid.NewGuid()
            let mutable requestNumber = 0
            let mutable preparationCount = 0
            let mutable delayCount = 0

            let preparation = createPreparation repositoryId directoryVersionId "permit-terminal" "c" 1L

            let dependencies: ConnectCache.Dependencies =
                {
                    Send =
                        fun _ _ ->
                            task {
                                requestNumber <- requestNumber + 1

                                match requestNumber with
                                | 1 -> return new HttpResponseMessage(HttpStatusCode.NotFound)
                                | 2 -> return jsonResponse HttpStatusCode.OK cachePublicKey
                                | 3 -> return jsonResponse HttpStatusCode.TooManyRequests { Code = "CacheRecoveryRequired"; Detail = "Reset is required." }
                                | other -> return raise (InvalidOperationException($"Unexpected Cache request {other}."))
                            }
                    Prepare =
                        fun _ ->
                            preparationCount <- preparationCount + 1
                            Task.FromResult(Ok(GraceReturnValue.Create preparation "cache-terminal"))
                    StartTimer = fun () -> fun () -> TimeSpan.Zero
                    Delay =
                        fun _ _ ->
                            delayCount <- delayCount + 1
                            Task.CompletedTask
                }

            let! result =
                ConnectCache.useVerifiedZipWith
                    dependencies
                    (Uri("http://localhost:5341/"))
                    (string repositoryId)
                    (string directoryVersionId)
                    "cache-terminal"
                    CancellationToken.None
                    (fun _ -> Task.FromResult())

            match result with
            | Ok _ -> Assert.Fail("Expected the non-capacity 429 to be terminal.")
            | Error error ->
                error.Error
                |> should contain "CacheRecoveryRequired"

            preparationCount |> should equal 1
            delayCount |> should equal 0
            requestNumber |> should equal 3
        }

    /// Capacity retry never starts another permit attempt after the monotonic 60-second budget expires.
    [<Test>]
    let ``cache fill capacity stops at the monotonic retry budget`` () =
        task {
            let repositoryId = Guid.NewGuid()
            let directoryVersionId = Guid.NewGuid()
            let mutable requestNumber = 0
            let mutable preparationCount = 0
            let mutable elapsed = TimeSpan.FromMilliseconds(59950.0)
            let delays = ResizeArray<TimeSpan>()

            let preparation = createPreparation repositoryId directoryVersionId "permit-budget" "d" 1L

            let dependencies: ConnectCache.Dependencies =
                {
                    Send =
                        fun _ _ ->
                            task {
                                requestNumber <- requestNumber + 1

                                match requestNumber with
                                | 1 -> return new HttpResponseMessage(HttpStatusCode.NotFound)
                                | 2 -> return jsonResponse HttpStatusCode.OK cachePublicKey
                                | 3 ->
                                    return
                                        jsonResponse
                                            HttpStatusCode.TooManyRequests
                                            { Code = "CacheFillCapacityExceeded"; Detail = "Distinct fill capacity is full." }
                                | other -> return raise (InvalidOperationException($"Unexpected Cache request {other}."))
                            }
                    Prepare =
                        fun _ ->
                            preparationCount <- preparationCount + 1
                            Task.FromResult(Ok(GraceReturnValue.Create preparation "cache-budget"))
                    StartTimer = fun () -> fun () -> elapsed
                    Delay =
                        fun delay _ ->
                            delays.Add(delay)
                            elapsed <- elapsed + delay
                            Task.CompletedTask
                }

            let! result =
                ConnectCache.useVerifiedZipWith
                    dependencies
                    (Uri("http://localhost:5341/"))
                    (string repositoryId)
                    (string directoryVersionId)
                    "cache-budget"
                    CancellationToken.None
                    (fun _ -> Task.FromResult())

            match result with
            | Ok _ -> Assert.Fail("Expected Cache fill retry budget failure.")
            | Error error ->
                error.Error
                |> should contain "expired after 60 seconds"

            preparationCount |> should equal 1
            requestNumber |> should equal 3

            delays
            |> Seq.toArray
            |> should equal [| TimeSpan.FromMilliseconds(50.0) |]
        }

    /// A retry whose Server preparation consumes the remaining budget never reaches another Cache fill.
    [<Test>]
    let ``cache fill does not post when retry preparation crosses the deadline`` () =
        task {
            let repositoryId = Guid.NewGuid()
            let directoryVersionId = Guid.NewGuid()
            let mutable requestNumber = 0
            let mutable preparationCount = 0
            let mutable elapsed = TimeSpan.Zero
            let delays = ResizeArray<TimeSpan>()

            let preparationForPermit permit = createPreparation repositoryId directoryVersionId permit "f" 1L

            let dependencies: ConnectCache.Dependencies =
                {
                    Send =
                        fun _ _ ->
                            task {
                                requestNumber <- requestNumber + 1

                                match requestNumber with
                                | 1 -> return new HttpResponseMessage(HttpStatusCode.NotFound)
                                | 2 -> return jsonResponse HttpStatusCode.OK cachePublicKey
                                | 3 ->
                                    return
                                        jsonResponse
                                            HttpStatusCode.TooManyRequests
                                            { Code = "CacheFillCapacityExceeded"; Detail = "Distinct fill capacity is full." }
                                | other -> return raise (InvalidOperationException($"Unexpected Cache request {other}."))
                            }
                    Prepare =
                        fun _ ->
                            preparationCount <- preparationCount + 1

                            if preparationCount = 2 then elapsed <- TimeSpan.FromSeconds(60.0)

                            Task.FromResult(Ok(GraceReturnValue.Create (preparationForPermit $"permit-{preparationCount}") "cache-late-preparation"))
                    StartTimer = fun () -> fun () -> elapsed
                    Delay =
                        fun delay _ ->
                            delays.Add(delay)
                            elapsed <- elapsed + delay
                            Task.CompletedTask
                }

            let! result =
                ConnectCache.useVerifiedZipWith
                    dependencies
                    (Uri("http://localhost:5341/"))
                    (string repositoryId)
                    (string directoryVersionId)
                    "cache-late-preparation"
                    CancellationToken.None
                    (fun _ -> Task.FromResult())

            match result with
            | Ok _ -> Assert.Fail("Expected Cache fill retry budget failure.")
            | Error error ->
                error.Error
                |> should contain "expired after 60 seconds"

            preparationCount |> should equal 2
            requestNumber |> should equal 3

            delays
            |> Seq.toArray
            |> should equal [| TimeSpan.FromMilliseconds(100.0) |]
        }

    /// Cancellation promptly detaches Connect from an unfinished Server preparation without starting later effects.
    [<Test>]
    let ``cache fill cancellation detaches from in-flight preparation`` () =
        task {
            let repositoryId = Guid.NewGuid()
            let directoryVersionId = Guid.NewGuid()
            let mutable requestNumber = 0
            let mutable zipStagingCount = 0
            let mutable workingDirectoryUpdateCount = 0
            use cancellation = new CancellationTokenSource()

            let preparationStarted = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

            let preparationCompletion =
                TaskCompletionSource<Result<GraceReturnValue<DirectoryVersionZipPreparation>, GraceError>>(TaskCreationOptions.RunContinuationsAsynchronously)

            let preparation = createPreparation repositoryId directoryVersionId "permit-cancelled" "g" 1L

            let dependencies: ConnectCache.Dependencies =
                {
                    Send =
                        fun _ _ ->
                            task {
                                requestNumber <- requestNumber + 1

                                match requestNumber with
                                | 1 -> return new HttpResponseMessage(HttpStatusCode.NotFound)
                                | 2 -> return jsonResponse HttpStatusCode.OK cachePublicKey
                                | other -> return raise (InvalidOperationException($"Unexpected Cache request {other}."))
                            }
                    Prepare =
                        fun _ ->
                            preparationStarted.SetResult()
                            preparationCompletion.Task
                    StartTimer = fun () -> fun () -> TimeSpan.Zero
                    Delay = fun _ _ -> Task.CompletedTask
                }

            let operation =
                ConnectCache.useVerifiedZipWith
                    dependencies
                    (Uri("http://localhost:5341/"))
                    (string repositoryId)
                    (string directoryVersionId)
                    "cache-cancelled-preparation"
                    cancellation.Token
                    (fun _ ->
                        zipStagingCount <- zipStagingCount + 1
                        workingDirectoryUpdateCount <- workingDirectoryUpdateCount + 1
                        Task.FromResult())

            do! preparationStarted.Task.WaitAsync(TimeSpan.FromSeconds(1.0))
            cancellation.Cancel()

            let! completion =
                task {
                    try
                        let! _ = operation.WaitAsync(TimeSpan.FromSeconds(1.0))
                        return Ok()
                    with
                    | ex -> return Error ex
                }

            match completion with
            | Error (:? OperationCanceledException) -> ()
            | Error ex -> Assert.Fail($"Expected prompt cancellation, got {ex.GetType().Name}: {ex.Message}")
            | Ok () -> Assert.Fail("Expected Cache preparation cancellation.")

            preparationCompletion.SetResult(Ok(GraceReturnValue.Create preparation "cache-cancelled-preparation"))
            do! Task.Yield()

            requestNumber |> should equal 2
            zipStagingCount |> should equal 0
            workingDirectoryUpdateCount |> should equal 0
        }

    /// A successful fill never substitutes for the required independent verified GET.
    [<Test>]
    let ``post-fill Cache GET failure is terminal without consuming ZIP bytes`` () =
        task {
            let repositoryId = Guid.NewGuid()
            let directoryVersionId = Guid.NewGuid()
            let mutable requestNumber = 0
            let mutable consumed = false

            let preparation = createPreparation repositoryId directoryVersionId "permit-post-fill" "e" 1L

            let dependencies: ConnectCache.Dependencies =
                {
                    Send =
                        fun _ _ ->
                            task {
                                requestNumber <- requestNumber + 1

                                match requestNumber with
                                | 1 -> return new HttpResponseMessage(HttpStatusCode.NotFound)
                                | 2 -> return jsonResponse HttpStatusCode.OK cachePublicKey
                                | 3 -> return new HttpResponseMessage(HttpStatusCode.NoContent)
                                | 4 -> return new HttpResponseMessage(HttpStatusCode.NotFound)
                                | other -> return raise (InvalidOperationException($"Unexpected Cache request {other}."))
                            }
                    Prepare = fun _ -> Task.FromResult(Ok(GraceReturnValue.Create preparation "cache-post-fill"))
                    StartTimer = fun () -> fun () -> TimeSpan.Zero
                    Delay = fun _ _ -> Task.CompletedTask
                }

            let! result =
                ConnectCache.useVerifiedZipWith
                    dependencies
                    (Uri("http://localhost:5341/"))
                    (string repositoryId)
                    (string directoryVersionId)
                    "cache-post-fill"
                    CancellationToken.None
                    (fun _ ->
                        consumed <- true
                        Task.FromResult())

            match result with
            | Ok _ -> Assert.Fail("Expected the failed post-fill Cache GET to be terminal.")
            | Error error ->
                error.Error
                |> should contain "Post-fill Cache GET"

            requestNumber |> should equal 4
            consumed |> should equal false
        }
