namespace Grace.CLI.Command

open Grace.CLI.Text
open Grace.Shared
open Grace.Shared.Parameters.Cache
open Grace.Types.Common
open System
open System.CommandLine
open System.CommandLine.Parsing
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Json
open System.Threading
open System.Threading.Tasks

/// Owns Cache-required Connect option parsing and retrieval orchestration.
module internal ConnectCache =

    /// Selects either unchanged Direct retrieval or one validated Cache-required endpoint.
    type Retrieval =
        | Direct
        | Required of Uri

    /// Supplies the Cache transport, Server preparation, monotonic timer, and retry delay seams.
    type Dependencies =
        {
            Send: HttpRequestMessage -> CancellationToken -> Task<HttpResponseMessage>
            Prepare: PrepareDirectoryVersionZipParameters -> Task<Result<GraceReturnValue<DirectoryVersionZipPreparation>, GraceError>>
            StartTimer: unit -> (unit -> TimeSpan)
            Delay: TimeSpan -> CancellationToken -> Task
        }

    /// Carries only the opaque Server permit accepted by the Cache fill route.
    [<CLIMutable>]
    type private CacheFillRequest = { Permit: string }

    /// Defines the options that explicitly select and locate Cache-required retrieval.
    module internal Options =
        let cacheRequired =
            new Option<bool>(
                OptionName.CacheRequired,
                Required = false,
                Description = "Require Connect retrieval through a verified loopback Grace Cache.",
                Arity = ArgumentArity.Zero,
                DefaultValueFactory = (fun _ -> false)
            )

        let cacheUri =
            new Option<string>(
                OptionName.CacheUri,
                Required = false,
                Description = "The explicit loopback Grace Cache HTTP endpoint.",
                Arity = ArgumentArity.ExactlyOne
            )

    /// Requires the original URI authority to name one exact loopback host and an explicit numeric port.
    let private hasExactLoopbackHostAndPort (value: string) =
        let schemeSeparator = value.IndexOf("://", StringComparison.Ordinal)

        if schemeSeparator < 0 then
            false
        else
            let authorityStart = schemeSeparator + 3
            let remainder = value.Substring(authorityStart)
            let authorityEnd = remainder.IndexOfAny([| '/'; '?'; '#' |])

            let authority = if authorityEnd < 0 then remainder else remainder.Substring(0, authorityEnd)

            let hostText, portText =
                if authority.StartsWith("[", StringComparison.Ordinal) then
                    let closingBracket = authority.IndexOf(']')

                    if closingBracket >= 0
                       && closingBracket + 1 < authority.Length
                       && authority[closingBracket + 1] = ':' then
                        authority.Substring(0, closingBracket + 1), authority.Substring(closingBracket + 2)
                    else
                        String.Empty, String.Empty
                else
                    let separator = authority.LastIndexOf(':')

                    if separator > 0 then
                        authority.Substring(0, separator), authority.Substring(separator + 1)
                    else
                        String.Empty, String.Empty

            let hostAllowed =
                hostText.Equals("127.0.0.1", StringComparison.Ordinal)
                || hostText.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || hostText.Equals("[::1]", StringComparison.OrdinalIgnoreCase)

            match Int32.TryParse(portText) with
            | true, port -> hostAllowed && port >= 0 && port <= 65535
            | _ -> false

    /// Requires the original endpoint text to contain no path or exactly one root slash.
    let private hasAllowedPathText (value: string) =
        let schemeSeparator = value.IndexOf("://", StringComparison.Ordinal)

        if schemeSeparator < 0 then
            false
        else
            let suffixStart = value.IndexOfAny([| '/'; '?'; '#' |], schemeSeparator + 3)

            if suffixStart < 0 then true else value.Substring(suffixStart) = "/"

    /// Accepts only one absolute explicit-port HTTP endpoint on the calibrated loopback hosts.
    let internal validateCacheUri (value: string) =
        let invalid () =
            Error
                "Cache URI must be an absolute loopback HTTP URI with host 127.0.0.1, localhost, or [::1], an explicit port, an empty or / path, and no user info, query, or fragment."

        if String.IsNullOrWhiteSpace(value) then
            invalid ()
        else
            match Uri.TryCreate(value, UriKind.Absolute) with
            | false, _ -> invalid ()
            | true, uri ->
                if
                    not (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
                    || not (hasExactLoopbackHostAndPort value)
                    || not (hasAllowedPathText value)
                    || not (String.IsNullOrEmpty(uri.UserInfo))
                    || not (String.IsNullOrEmpty(uri.Query))
                    || not (String.IsNullOrEmpty(uri.Fragment))
                then
                    invalid ()
                else
                    Ok uri

    /// Reads one option only when it was supplied explicitly on the command line.
    let private tryGetExplicitValue<'T> (parseResult: ParseResult) (option: Option<'T>) =
        let result = parseResult.GetResult(option)

        if isNull result || result.Implicit then
            None
        else
            Some(parseResult.GetValue(option))

    /// Resolves Cache-required selection without inspecting environment state for Direct or explicit CLI input.
    let internal selectRetrieval (parseResult: ParseResult) (getEnvironmentVariable: string -> string) =
        let cacheRequired = parseResult.GetValue(Options.cacheRequired)
        let explicitCacheUri = tryGetExplicitValue parseResult Options.cacheUri

        if not cacheRequired then
            match explicitCacheUri with
            | Some _ -> Error "--cache-uri requires --cache-required."
            | None -> Ok Direct
        else
            match explicitCacheUri with
            | Some value -> validateCacheUri value |> Result.map Required
            | None ->
                match getEnvironmentVariable "GRACE_CACHE_URI" with
                | value when String.IsNullOrWhiteSpace(value) -> Error "--cache-required needs --cache-uri or GRACE_CACHE_URI."
                | value -> validateCacheUri value |> Result.map Required

    /// Builds the exact Cache route for one repository and DirectoryVersion ZIP identity.
    let private artifactUri (cacheUri: Uri) (repositoryId: string) (directoryVersionId: string) =
        Uri(cacheUri, $"repositories/{Uri.EscapeDataString(repositoryId)}/directory-version-zips/{Uri.EscapeDataString(directoryVersionId)}")

    /// Builds the Cache route that exposes the current process public key.
    let private publicKeyUri (cacheUri: Uri) = Uri(cacheUri, "fill-public-key")

    /// Builds the permit-only fill route for one exact artifact identity.
    let private fillUri (cacheUri: Uri) (repositoryId: string) (directoryVersionId: string) =
        Uri(cacheUri, $"repositories/{Uri.EscapeDataString(repositoryId)}/directory-version-zips/{Uri.EscapeDataString(directoryVersionId)}/fill")

    /// Runs one Cache request with response streaming enabled.
    let private send (dependencies: Dependencies) (method': HttpMethod) (uri: Uri) cancellationToken =
        task {
            use request = new HttpRequestMessage(method', uri)
            return! dependencies.Send request cancellationToken
        }

    /// Sends one Cache fill request containing no data beyond the opaque permit.
    let private sendFill (dependencies: Dependencies) (uri: Uri) (permit: string) cancellationToken =
        task {
            use request = new HttpRequestMessage(HttpMethod.Post, uri)
            request.Content <- JsonContent.Create({ Permit = permit }, options = Constants.JsonSerializerOptions)
            return! dependencies.Send request cancellationToken
        }

    /// Returns a correlated terminal Cache error for an unexpected HTTP status.
    let private unexpectedStatus correlationId operation (statusCode: HttpStatusCode) =
        GraceError.Create $"{operation} failed with HTTP status {int statusCode}." correlationId

    /// Reads one typed Cache problem without treating malformed error content as retryable.
    let private readProblem (response: HttpResponseMessage) correlationId cancellationToken =
        task {
            try
                let! problem = response.Content.ReadFromJsonAsync<CacheProblem>(Constants.JsonSerializerOptions, cancellationToken)

                if
                    isNull (box problem)
                    || String.IsNullOrWhiteSpace(problem.Code)
                then
                    return Error(GraceError.Create "Cache returned an invalid problem response." correlationId)
                else
                    return Ok problem
            with
            | :? OperationCanceledException as ex -> return raise ex
            | _ -> return Error(GraceError.Create "Cache returned an invalid problem response." correlationId)
        }

    /// Projects a typed Cache problem as one correlated terminal CLI error.
    let private problemError correlationId operation (problem: CacheProblem) =
        GraceError.Create $"{operation} failed: {problem.Code}: {problem.Detail}" correlationId

    /// Caps exponential capacity backoff at two seconds.
    let private retryDelay attempt =
        let milliseconds = min 2000 (100 * (1 <<< min attempt 5))
        TimeSpan.FromMilliseconds(float milliseconds)

    /// Creates the distinct terminal result for exhaustion of typed Cache backpressure retry.
    let private retryBudgetError correlationId = GraceError.Create "CacheFillCapacityExceeded retry budget expired after 60 seconds." correlationId

    /// Reads the current Cache process public key from its loopback endpoint.
    let private readPublicKey dependencies cacheUri correlationId cancellationToken =
        task {
            use! response = send dependencies HttpMethod.Get (publicKeyUri cacheUri) cancellationToken

            if response.StatusCode <> HttpStatusCode.OK then
                return Error(unexpectedStatus correlationId "Cache public-key GET" response.StatusCode)
            else
                let! publicKey = response.Content.ReadFromJsonAsync<CachePublicJwk>(Constants.JsonSerializerOptions, cancellationToken)

                if isNull (box publicKey) then
                    return Error(GraceError.Create "Cache public-key GET returned no key." correlationId)
                else
                    return Ok publicKey
        }

    /// Requests one fresh Server permit bound to the current Cache process key and artifact identity.
    let private prepareFill dependencies repositoryId directoryVersionId publicKey correlationId =
        let parameters =
            PrepareDirectoryVersionZipParameters(
                RepositoryId = repositoryId,
                DirectoryVersionId = directoryVersionId,
                CachePublicKey = publicKey,
                CorrelationId = correlationId
            )

        dependencies.Prepare parameters

    /// Waits cancellably for Server preparation and applies the remaining retry budget when capacity retry is active.
    let private awaitPreparation
        dependencies
        repositoryId
        directoryVersionId
        publicKey
        correlationId
        (remaining: TimeSpan option)
        (cancellationToken: CancellationToken)
        =
        task {
            let preparation = prepareFill dependencies repositoryId directoryVersionId publicKey correlationId

            match remaining with
            | None ->
                let! result = preparation.WaitAsync(cancellationToken)
                return Ok result
            | Some remaining ->
                try
                    let! result = preparation.WaitAsync(remaining, cancellationToken)
                    return Ok result
                with
                | :? TimeoutException -> return Error()
        }

    /// Retries only Cache fill-capacity rejection and obtains a fresh Server permit for every attempt.
    let rec private fillUntilAvailable
        (dependencies: Dependencies)
        (cacheUri: Uri)
        (repositoryId: string)
        (directoryVersionId: string)
        (publicKey: CachePublicJwk)
        (correlationId: string)
        (cancellationToken: CancellationToken)
        (elapsed: (unit -> TimeSpan) option)
        (attempt: int)
        =
        task {
            cancellationToken.ThrowIfCancellationRequested()
            let budget = TimeSpan.FromSeconds(60.0)

            let remaining =
                elapsed
                |> Option.map (fun readElapsed -> budget - readElapsed ())

            if remaining
               |> Option.exists (fun value -> value <= TimeSpan.Zero) then
                return Error(retryBudgetError correlationId)
            else
                let! awaitedPreparation = awaitPreparation dependencies repositoryId directoryVersionId publicKey correlationId remaining cancellationToken

                match awaitedPreparation with
                | Error () -> return Error(retryBudgetError correlationId)
                | Ok _ when
                    elapsed
                    |> Option.exists (fun readElapsed -> readElapsed () >= budget)
                    ->
                    return Error(retryBudgetError correlationId)
                | Ok (Error error) -> return Error error
                | Ok (Ok preparation) ->
                    use! fillResponse =
                        sendFill dependencies (fillUri cacheUri repositoryId directoryVersionId) preparation.ReturnValue.Permit cancellationToken

                    if fillResponse.StatusCode = HttpStatusCode.NoContent then
                        return Ok()
                    elif fillResponse.StatusCode = HttpStatusCode.TooManyRequests then
                        let! problemResult = readProblem fillResponse correlationId cancellationToken

                        match problemResult with
                        | Error error -> return Error error
                        | Ok problem when problem.Code = "CacheFillCapacityExceeded" ->
                            let readElapsed =
                                elapsed
                                |> Option.defaultWith dependencies.StartTimer

                            let remaining = budget - readElapsed ()

                            if remaining <= TimeSpan.Zero then
                                return Error(retryBudgetError correlationId)
                            else
                                let requestedDelay = retryDelay attempt
                                let delay = if requestedDelay < remaining then requestedDelay else remaining
                                do! dependencies.Delay delay cancellationToken

                                return!
                                    fillUntilAvailable
                                        dependencies
                                        cacheUri
                                        repositoryId
                                        directoryVersionId
                                        publicKey
                                        correlationId
                                        cancellationToken
                                        (Some readElapsed)
                                        (attempt + 1)
                        | Ok problem -> return Error(problemError correlationId "Cache fill" problem)
                    else
                        return Error(unexpectedStatus correlationId "Cache fill" fillResponse.StatusCode)
        }

    /// Supplies a successful Cache GET stream to the caller before disposing the response.
    let private consumeResponse (response: HttpResponseMessage) (cancellationToken: CancellationToken) consume =
        task {
            use! stream = response.Content.ReadAsStreamAsync(cancellationToken)
            return! consume stream
        }

    /// Supplies one exact verified Cache GET stream to the caller while its HTTP response remains alive.
    let internal useVerifiedZipWith (dependencies: Dependencies) cacheUri repositoryId directoryVersionId correlationId cancellationToken consume =
        task {
            let uri = artifactUri cacheUri repositoryId directoryVersionId
            use! response = send dependencies HttpMethod.Get uri cancellationToken

            if response.StatusCode = HttpStatusCode.OK then
                let! result = consumeResponse response cancellationToken consume
                return Ok result
            elif response.StatusCode = HttpStatusCode.NotFound then
                let! publicKeyResult = readPublicKey dependencies cacheUri correlationId cancellationToken

                match publicKeyResult with
                | Error error -> return Error error
                | Ok publicKey ->
                    let! fillResult = fillUntilAvailable dependencies cacheUri repositoryId directoryVersionId publicKey correlationId cancellationToken None 0

                    match fillResult with
                    | Error error -> return Error error
                    | Ok () ->
                        use! verifiedResponse = send dependencies HttpMethod.Get uri cancellationToken

                        if verifiedResponse.StatusCode <> HttpStatusCode.OK then
                            return Error(unexpectedStatus correlationId "Post-fill Cache GET" verifiedResponse.StatusCode)
                        else
                            let! result = consumeResponse verifiedResponse cancellationToken consume
                            return Ok result
            else
                return Error(unexpectedStatus correlationId "Cache GET" response.StatusCode)
        }

    let private httpClient = new HttpClient()

    /// Uses the process-wide HTTP client, current authenticated SDK identity, monotonic timer, and cancellable delay.
    let private liveDependencies =
        {
            Send = fun request cancellationToken -> httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            Prepare = fun parameters -> Grace.SDK.Cache.PrepareDirectoryVersionZip(parameters)
            StartTimer =
                fun () ->
                    let stopwatch = Stopwatch.StartNew()
                    fun () -> stopwatch.Elapsed
            Delay = fun delay cancellationToken -> Task.Delay(delay, cancellationToken)
        }

    /// Retrieves one exact DirectoryVersion ZIP through the selected loopback Cache and supplies its verified GET stream.
    let internal useVerifiedZip cacheUri repositoryId directoryVersionId correlationId cancellationToken consume =
        useVerifiedZipWith liveDependencies cacheUri repositoryId directoryVersionId correlationId cancellationToken consume
