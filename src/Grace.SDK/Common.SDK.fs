namespace Grace.SDK

open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Services
open Grace.Shared.Types
open Grace.Shared.Parameters.Common
open Grace.Shared.Utilities
open Grace.Shared.Validation
open NodaTime
open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Json
open System.Net.Security
open System.Text.Json

module Common =

    let rnd = Random()

    // We're limiting the SSL protocol to TLS 1.2+.
    let private sslClientAuthenticationOptions = SslClientAuthenticationOptions(
        EncryptionPolicy = EncryptionPolicy.RequireEncryption,
        EnabledSslProtocols = (Security.Authentication.SslProtocols.Tls12 ||| Security.Authentication.SslProtocols.Tls13)
    )

    // This construct is an equivalent to using IHttpClientFactory in the ASP.NET DI container, for code (like this) that isn't using GenericHost.
    // See https://docs.microsoft.com/en-us/aspnet/core/fundamentals/http-requests?view=aspnetcore-6.0#alternatives-to-ihttpclientfactory for more information.
    let private socketsHttpHandler = new SocketsHttpHandler(
        AllowAutoRedirect = true,                               // We expect to use Traffic Manager or equivalents, so there will be redirects.
        MaxAutomaticRedirections = 6,                           // Not sure of the exact right number, but definitely want a limit here.
        SslOptions = sslClientAuthenticationOptions,            // Require TLS 1.2 / 1.3
        AutomaticDecompression = DecompressionMethods.All,      // We'll store blobs using GZip, and we'll enable Brotli on the server
        EnableMultipleHttp2Connections = true,                  // I doubt this will ever happen, but don't mind making it possible
        PooledConnectionLifetime = TimeSpan.FromMinutes(2.0),   // Default is 2m
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2.0) // Default is 2m
    )

    /// Gets an HttpClient instance from a custom HttpClientFactory.
    let getHttpClient (correlationId: string) =
        let traceIdBytes: byte array = Array.zeroCreate 16
        let parentIdBytes: byte array = Array.zeroCreate 8
        rnd.NextBytes(traceIdBytes)
        rnd.NextBytes(parentIdBytes)
        let traceId = byteArrayAsString(traceIdBytes)
        let parentId = byteArrayAsString(parentIdBytes)

        let httpClient = new HttpClient(handler = socketsHttpHandler, disposeHandler = false)
        httpClient.DefaultRequestVersion <- HttpVersion.Version20   // We'll aggressively move to Version30 as soon as we can.
        httpClient.DefaultRequestHeaders.Add(Constants.Traceparent, $"00-{traceId}-{parentId}-01")
        httpClient.DefaultRequestHeaders.Add(Constants.Tracestate, $"graceserver-{parentId}")
        httpClient.DefaultRequestHeaders.Add(Constants.CorrelationIdHeaderKey, $"{correlationId}")
        httpClient.DefaultRequestHeaders.Add(Constants.ServerApiVersionHeaderKey, $"{Constants.ServerApiVersions.Edge}")
        //httpClient.DefaultVersionPolicy <- HttpVersionPolicy.RequestVersionOrHigher
#if DEBUG
        httpClient.Timeout <- TimeSpan.FromSeconds(1800.0)  // Keeps client commands open while debugging.
#endif
        httpClient
        
    let getServer<'T, 'U when 'T :> CommonParameters>(parameters: 'T, route: string) =
        task {
            try
                use httpClient = getHttpClient parameters.CorrelationId
                let startTime = getCurrentInstant()
                let daprServerUri = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DaprServerUri)
                let daprAppPort = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DaprAppPort)
                let serverUri = Uri($"{daprServerUri}:{daprAppPort}/{route}")
                let! response = Constants.DefaultAsyncRetryPolicy.ExecuteAsync(fun _ -> httpClient.GetAsync(new Uri($"{serverUri}")))
                let endTime = getCurrentInstant()
                if response.IsSuccessStatusCode then
                    let! graceReturn = response.Content.ReadFromJsonAsync<GraceReturnValue<'U>>(Constants.JsonSerializerOptions)
                    return Ok graceReturn |> enhance ("ServerElapsedTime", $"{(endTime - startTime).TotalMilliseconds:F3} ms")
                else
                    let! graceError = response.Content.ReadFromJsonAsync<GraceError>(Constants.JsonSerializerOptions)
                    return Error graceError |> enhance ("ServerElapsedTime", $"{(endTime - startTime).TotalMilliseconds:F3} ms")
            with ex ->
                let exceptionResponse = Utilities.createExceptionResponse ex
                return Error (GraceError.Create (JsonSerializer.Serialize(exceptionResponse)) parameters.CorrelationId)
        }

    let postServer<'T, 'U when 'T :> CommonParameters>(parameters: 'T, route: string) =
        task {
            try
                use httpClient = getHttpClient parameters.CorrelationId
                let jsonContent = JsonContent.Create(parameters, options = Constants.JsonSerializerOptions)
                let daprServerUri = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DaprServerUri)
                let daprAppPort = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DaprAppPort)
                let serverUri = Uri($"{daprServerUri}:{daprAppPort}/{route}")
                let startTime = getCurrentInstant()
                let! response = Constants.DefaultAsyncRetryPolicy.ExecuteAsync(fun _ -> httpClient.PostAsync(serverUri, jsonContent))
                let endTime = getCurrentInstant()
                if response.IsSuccessStatusCode then
                    let! graceReturnValue = response.Content.ReadFromJsonAsync<GraceReturnValue<'U>>(Constants.JsonSerializerOptions)
                    //let! blah = response.Content.ReadAsStringAsync()
                    //let graceReturnValue = JsonSerializer.Deserialize<GraceReturnValue<'U>>(blah, Constants.JsonSerializerOptions)
                    return Ok graceReturnValue |> enhance ("ServerElapsedTime", $"{(endTime - startTime).TotalMilliseconds:F3} ms")
                else
                    if response.StatusCode = HttpStatusCode.NotFound then
                        return Error (GraceError.Create $"Server endpoint {route} not found." parameters.CorrelationId)
                    else
                        let! graceError = response.Content.ReadFromJsonAsync<GraceError>(Constants.JsonSerializerOptions)
                        return Error graceError |> enhance ("ServerElapsedTime", $"{(endTime - startTime).TotalMilliseconds:F3} ms")
            with ex ->
                let exceptionResponse = Utilities.createExceptionResponse ex
                return Error (GraceError.Create ($"{exceptionResponse}") parameters.CorrelationId)
        }

    let ensureCorrelationIdIsSet<'T when 'T :> CommonParameters> (parameters: 'T)  =
        parameters.CorrelationId <- (Utilities.ensureNonEmptyCorrelationId parameters.CorrelationId)
        parameters

    let getObjectFileName (fileVersion: FileVersion) = 
        let file = FileInfo(Path.Combine(Current().RootDirectory, $"{fileVersion.RelativePath}"))
        $"{file.Name.Replace(file.Extension, String.Empty)}_{fileVersion.Sha256Hash}{file.Extension}"
