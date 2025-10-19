namespace Grace.SDK

open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Services
open Grace.Types.Types
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
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Extensions.Caching.Memory

// Supresses the warning for using AllowNoEncryption in Debug builds.
#nowarn "0044"

module Common =

    /// Checks to make sure the .NET MemoryCache is initialized. If not, it will create one.
    //let checkMemoryCache () =
    //    if isNull memoryCache then
    //        let memoryCacheOptions =
    //            MemoryCacheOptions(TrackStatistics = false, TrackLinkedCacheEntries = false, ExpirationScanFrequency = TimeSpan.FromSeconds(30.0))

    //        memoryCache <- new MemoryCache(memoryCacheOptions)

    /// <summary>
    /// Sends GET commands to Grace Server.
    /// </summary>
    /// <param name="parameters">Values to use when sending the GET command.</param>
    /// <param name="route">The route to use when sending the GET command.</param>
    /// <returns>A Task containing the result of the GET command.</returns>
    /// <typeparam name="'T">The type of the parameters to use when sending the GET command.</typeparam>
    /// <typeparam name="'U">The type of the result of the command.</typeparam>
    let getServer<'T, 'U when 'T :> CommonParameters> (parameters: 'T, route: string) =
        task {
            try
                //checkMemoryCache ()
                use httpClient = getHttpClient parameters.CorrelationId
                let startTime = getCurrentInstant ()

                let graceServerUri = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.GraceServerUri)

                let graceServerPort = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.GraceAppPort)

                let serverUri = Uri($"{graceServerUri}:{graceServerPort}/{route}")

                let! response = Constants.DefaultAsyncRetryPolicy.ExecuteAsync(fun _ -> httpClient.GetAsync(new Uri($"{serverUri}")))

                let endTime = getCurrentInstant ()

                if response.IsSuccessStatusCode then
                    let! graceReturn = response.Content.ReadFromJsonAsync<GraceReturnValue<'U>>(Constants.JsonSerializerOptions)

                    return
                        Ok graceReturn
                        |> enhance "ServerResponseTime" $"{(endTime - startTime).TotalMilliseconds:F3} ms"
                else
                    let! graceError = response.Content.ReadFromJsonAsync<GraceError>(Constants.JsonSerializerOptions)

                    return
                        Error graceError
                        |> enhance "ServerResponseTime" $"{(endTime - startTime).TotalMilliseconds:F3} ms"
            with ex ->
                let exceptionResponse = Utilities.ExceptionResponse.Create ex
                return Error(GraceError.Create (serialize exceptionResponse) parameters.CorrelationId)
        }

    /// <summary>
    /// Sends POST commands to Grace Server.
    /// </summary>
    /// <param name="parameters">Values to use when sending the POST command.</param>
    /// <param name="route">The route to use when sending the POST command.</param>
    /// <returns>A Task containing the result of the POST command.</returns>
    /// <typeparam name="'T">The type of the parameters to use when sending the POST command.</typeparam>
    /// <typeparam name="'U">The type of the result of the command.</typeparam>
    let postServer<'T, 'U when 'T :> CommonParameters> (parameters: 'T, route: string) : (Task<GraceResult<'U>>) =
        task {
            try
                //checkMemoryCache ()
                use httpClient = getHttpClient parameters.CorrelationId
                let serverUriWithRoute = Uri($"{Current().ServerUri}/{route}")
                let startTime = getCurrentInstant ()
                let! response = httpClient.PostAsync(serverUriWithRoute, createJsonContent parameters)

                let endTime = getCurrentInstant ()

                if response.IsSuccessStatusCode then
                    let! graceReturnValue = response.Content.ReadFromJsonAsync<GraceReturnValue<'U>>(Constants.JsonSerializerOptions)
                    //let! blah = response.Content.ReadAsStringAsync()
                    //logToConsole $"blah: {blah}"
                    //let graceReturnValue = JsonSerializer.Deserialize<GraceReturnValue<'U>>(blah, Constants.JsonSerializerOptions)

                    return
                        Ok graceReturnValue
                        |> enhance "ServerResponseTime" $"{(endTime - startTime).TotalMilliseconds:F3} ms"
                else if response.StatusCode = HttpStatusCode.NotFound then
                    return Error(GraceError.Create $"Server endpoint {route} not found." parameters.CorrelationId)
                elif response.StatusCode = HttpStatusCode.BadRequest then
                    let! errorMessage = response.Content.ReadAsStringAsync()

                    return
                        Error(GraceError.Create $"{errorMessage}" parameters.CorrelationId)
                        |> enhance "ServerResponseTime" $"{(endTime - startTime).TotalMilliseconds:F3} ms"
                        |> enhance "StatusCode" $"{response.StatusCode}"
                else
                    let! responseAsString = response.Content.ReadAsStringAsync()

                    try
                        let graceError = deserialize<GraceError> (responseAsString)

                        return
                            Error graceError
                            |> enhance "ServerResponseTime" $"{(endTime - startTime).TotalMilliseconds:F3} ms"
                            |> enhance "StatusCode" $"{response.StatusCode}"
                    with ex ->
                        return
                            Error(GraceError.Create $"{responseAsString}" parameters.CorrelationId)
                            |> enhance "ServerResponseTime" $"{(endTime - startTime).TotalMilliseconds:F3} ms"
                            |> enhance "StatusCode" $"{response.StatusCode}"
            with ex ->
                let exceptionResponse = Utilities.ExceptionResponse.Create ex
                return Error(GraceError.Create ($"{exceptionResponse}") parameters.CorrelationId)
        }

    /// Ensures that the CorrelationId is set in the parameters for calling Grace Server. If it hasn't already been set, one will be created.
    let ensureCorrelationIdIsSet<'T when 'T :> CommonParameters> (parameters: 'T) =
        parameters.CorrelationId <- (Utilities.ensureNonEmptyCorrelationId parameters.CorrelationId)
        parameters

    /// Returns the object file name for a given file version, including the SHA-256 hash.
    ///
    /// Example: foo.txt with a SHA-256 hash of "8e798...980c" -> "foo_8e798...980c.txt".
    let getObjectFileName (fileVersion: FileVersion) =
        let file = FileInfo(Path.Combine(Current().RootDirectory, $"{fileVersion.RelativePath}"))

        $"{file.Name.Replace(file.Extension, String.Empty)}_{fileVersion.Sha256Hash}{file.Extension}"
