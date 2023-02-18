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
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Json
open System.Net.Security
open System.Text.Json
open System.Threading.Tasks

// Supresses the warning for using AllowNoEncryption in Debug builds.
#nowarn "0044"

module Common =

    let private sslClientAuthenticationOptions = 
#if DEBUG
        // In debug mode, we'll accept only TLS 1.2 and allow no encryption to enable access to the CosmosDB emulator without having to deal with certificates.
        // TLS 1.3 requires a non-null cipher, so limiting ourselves to TLS 1.2 lets us get away with using this.
        // AllowNoEncryption means: Prefer secure connections, but if there's a null cipher (which we'll get from the CosmosDB emulator because it has a self-signed certificate), we'll allow it.
        SslClientAuthenticationOptions(
            EncryptionPolicy = EncryptionPolicy.AllowNoEncryption,
            EnabledSslProtocols = Security.Authentication.SslProtocols.Tls12
        )
    #else
        // In release mode, we'll accept TLS 1.2 and TLS 1.3.
        SslClientAuthenticationOptions(
            EncryptionPolicy = EncryptionPolicy.RequireEncryption,
            EnabledSslProtocols = (Security.Authentication.SslProtocols.Tls12 ||| Security.Authentication.SslProtocols.Tls13)
        )
    #endif
    
    // This construct is equivalent to using IHttpClientFactory in the ASP.NET DI container, for code (like this) that isn't using GenericHost.
    // See https://docs.microsoft.com/en-us/aspnet/core/fundamentals/http-requests?view=aspnetcore-7.0#alternatives-to-ihttpclientfactory for more information.
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
        let traceIdBytes = stackalloc<byte> 16
        let parentIdBytes = stackalloc<byte> 8
        Random.Shared.NextBytes(traceIdBytes)
        Random.Shared.NextBytes(parentIdBytes)
        let traceId = byteArrayToString(traceIdBytes)
        let parentId = byteArrayToString(parentIdBytes)

        let httpClient = new HttpClient(handler = socketsHttpHandler, disposeHandler = false)
        httpClient.DefaultRequestVersion <- HttpVersion.Version20   // We'll aggressively move to Version30 as soon as we can.
        httpClient.DefaultRequestHeaders.Add(Constants.Traceparent, $"00-{traceId}-{parentId}-01")
        httpClient.DefaultRequestHeaders.Add(Constants.Tracestate, $"graceserver-{parentId}")
        httpClient.DefaultRequestHeaders.Add(Constants.CorrelationIdHeaderKey, $"{correlationId}")
        httpClient.DefaultRequestHeaders.Add(Constants.ServerApiVersionHeaderKey, $"{Constants.ServerApiVersions.Edge}")
        //httpClient.DefaultVersionPolicy <- HttpVersionPolicy.RequestVersionOrHigher
#if DEBUG
        //httpClient.Timeout <- TimeSpan.FromSeconds(1800.0)  // Keeps client commands open while debugging.
        httpClient.Timeout <- TimeSpan.FromSeconds(15.0)  // Fast fail for testing network connectivity.
#endif
        httpClient

    /// Sends GET commands to Grace Server.        
    let getServer<'T, 'U when 'T :> CommonParameters>(parameters: 'T, route: string) =
        task {
            try
                use httpClient = getHttpClient parameters.CorrelationId
                let startTime = getCurrentInstant()
                let daprServerUri = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DaprServerUri)
                let graceServerPort = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.GraceAppPort)
                let serverUri = Uri($"{daprServerUri}:{graceServerPort}/{route}")
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
                return Error (GraceError.Create (serialize exceptionResponse) parameters.CorrelationId)
        }

    /// Sends POST commands to Grace Server.
    let postServer<'T, 'U when 'T :> CommonParameters>(parameters: 'T, route: string) =
        task {
            try
                use httpClient = getHttpClient parameters.CorrelationId
                let jsonContent = JsonContent.Create(parameters, options = Constants.JsonSerializerOptions)
                let serverUriWithRoute = Uri($"{Current().ServerUri}/{route}")
                //logToConsole $"serverUriWithRoute: {serverUriWithRoute}"
                let startTime = getCurrentInstant()
                let! response = Constants.DefaultAsyncRetryPolicy.ExecuteAsync(fun _ -> httpClient.PostAsync(serverUriWithRoute, jsonContent))
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

    /// Ensures that the CorrelationId is set in the parameters for calling Grace Server. If it hasn't already been set, one will be created.
    let ensureCorrelationIdIsSet<'T when 'T :> CommonParameters> (parameters: 'T)  =
        parameters.CorrelationId <- (Utilities.ensureNonEmptyCorrelationId parameters.CorrelationId)
        parameters

    /// Returns the object file name for a given file version, including the SHA-256 hash.
    /// Example: foo.txt with a SHA-256 hash of "8e798...980c" -> "foo_8e798...980c.txt".
    let getObjectFileName (fileVersion: FileVersion) = 
        let file = FileInfo(Path.Combine(Current().RootDirectory, $"{fileVersion.RelativePath}"))
        $"{file.Name.Replace(file.Extension, String.Empty)}_{fileVersion.Sha256Hash}{file.Extension}"
