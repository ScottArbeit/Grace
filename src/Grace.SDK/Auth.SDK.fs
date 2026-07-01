namespace Grace.SDK

open Grace.Shared
open Grace.Shared.Services
open Grace.Shared.Utilities
open Grace.Types.Auth
open Grace.Types.Common
open System
open System.Net.Http
open System.Net.Http.Headers
open System.Net.Http.Json
open System.Threading.Tasks

/// Converts non-success HTTP responses into Grace errors while preserving route and correlation context.
module ResponseErrors =

    /// Formats the HTTP status code and reason phrase used when a server error body is empty.
    let private statusDescription (response: HttpResponseMessage) =
        let reason =
            if String.IsNullOrWhiteSpace response.ReasonPhrase then
                response.StatusCode.ToString()
            else
                response.ReasonPhrase

        $"{int response.StatusCode} {reason}"

    /// Reads a failed response body as a serialized GraceError, falling back to a plain-text GraceError.
    let fromResponse (correlationId: string) (route: string) (response: HttpResponseMessage) =
        task {
            let! responseBody = response.Content.ReadAsStringAsync()

            if String.IsNullOrWhiteSpace responseBody then
                return GraceError.Create $"Server returned {statusDescription response} for {route} without an error body." correlationId
            else
                try
                    return deserialize<GraceError> responseBody
                with
                | _ -> return GraceError.Create responseBody correlationId
        }

/// Manages optional bearer-token injection for SDK HTTP clients and authentication discovery calls.
module Auth =

    /// Callback used by host applications to supply an access token just before an SDK HTTP call.
    type TokenProvider = unit -> Task<string option>

    let mutable private tokenProvider: TokenProvider option = None

    /// Installs the process-wide token callback used by subsequent SDK requests.
    let setTokenProvider (provider: TokenProvider) = tokenProvider <- Some provider

    /// Removes the process-wide token callback so SDK requests are sent without bearer auth.
    let clearTokenProvider () = tokenProvider <- None

    /// Asks the configured token callback for a bearer token and suppresses provider failures.
    let tryGetAccessToken () =
        task {
            match tokenProvider with
            | None -> return None
            | Some provider ->
                try
                    return! provider ()
                with
                | _ -> return None
        }

    /// Adds a bearer Authorization header when the token callback returns a non-empty token.
    let addAuthorizationHeader (httpClient: HttpClient) =
        task {
            let! tokenOpt = tryGetAccessToken ()

            match tokenOpt with
            | Some token when not (String.IsNullOrWhiteSpace token) ->
                httpClient.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("Bearer", token)
            | _ -> ()
        }

    /// Fetches the server-advertised OIDC client configuration from the authentication endpoint.
    let getOidcClientConfig (parameters: Grace.Shared.Parameters.Common.CommonParameters) =
        task {
            let correlationId = ensureNonEmptyCorrelationId parameters.CorrelationId
            parameters.CorrelationId <- correlationId

            try
                use httpClient = ClientIdentity.getHttpClient correlationId
                let startTime = getCurrentInstant ()

                let graceServerUri = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.GraceServerUri)
                let serverUri = Uri($"{graceServerUri}/authenticate/oidc/config")

                let! response = Constants.DefaultAsyncRetryPolicy.ExecuteAsync(fun _ -> httpClient.GetAsync(serverUri))
                let endTime = getCurrentInstant ()

                if response.IsSuccessStatusCode then
                    let! graceReturnValue = response.Content.ReadFromJsonAsync<GraceReturnValue<OidcClientConfig>>(Constants.JsonSerializerOptions)

                    return
                        Ok graceReturnValue
                        |> enhance "ServerResponseTime" $"{(endTime - startTime).TotalMilliseconds:F3} ms"
                        |> ClientIdentity.enhanceWithLifecycleDiagnostics response
                else
                    let! graceError = ResponseErrors.fromResponse correlationId "authenticate/oidc/config" response

                    return
                        Error graceError
                        |> enhance "ServerResponseTime" $"{(endTime - startTime).TotalMilliseconds:F3} ms"
                        |> ClientIdentity.enhanceWithLifecycleDiagnostics response
            with
            | ex ->
                let exceptionResponse = Utilities.ExceptionResponse.Create ex
                return Error(GraceError.Create (serialize exceptionResponse) parameters.CorrelationId)
        }
