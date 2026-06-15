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

module ResponseErrors =

    let private statusDescription (response: HttpResponseMessage) =
        let reason =
            if String.IsNullOrWhiteSpace response.ReasonPhrase then
                response.StatusCode.ToString()
            else
                response.ReasonPhrase

        $"{int response.StatusCode} {reason}"

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

module Auth =

    type TokenProvider = unit -> Task<string option>

    let mutable private tokenProvider: TokenProvider option = None

    let setTokenProvider (provider: TokenProvider) = tokenProvider <- Some provider

    let clearTokenProvider () = tokenProvider <- None

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

    let addAuthorizationHeader (httpClient: HttpClient) =
        task {
            let! tokenOpt = tryGetAccessToken ()

            match tokenOpt with
            | Some token when not (String.IsNullOrWhiteSpace token) ->
                httpClient.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("Bearer", token)
            | _ -> ()
        }

    let getOidcClientConfig (parameters: Grace.Shared.Parameters.Common.CommonParameters) =
        task {
            let correlationId = ensureNonEmptyCorrelationId parameters.CorrelationId
            parameters.CorrelationId <- correlationId

            try
                use httpClient = ClientIdentity.getHttpClient correlationId
                let startTime = getCurrentInstant ()

                let graceServerUri = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.GraceServerUri)
                let serverUri = Uri($"{graceServerUri}/auth/oidc/config")

                let! response = Constants.DefaultAsyncRetryPolicy.ExecuteAsync(fun _ -> httpClient.GetAsync(serverUri))
                let endTime = getCurrentInstant ()

                if response.IsSuccessStatusCode then
                    let! graceReturnValue = response.Content.ReadFromJsonAsync<GraceReturnValue<OidcClientConfig>>(Constants.JsonSerializerOptions)

                    return
                        Ok graceReturnValue
                        |> enhance "ServerResponseTime" $"{(endTime - startTime).TotalMilliseconds:F3} ms"
                        |> ClientIdentity.enhanceWithLifecycleDiagnostics response
                else
                    let! graceError = ResponseErrors.fromResponse correlationId "auth/oidc/config" response

                    return
                        Error graceError
                        |> enhance "ServerResponseTime" $"{(endTime - startTime).TotalMilliseconds:F3} ms"
                        |> ClientIdentity.enhanceWithLifecycleDiagnostics response
            with
            | ex ->
                let exceptionResponse = Utilities.ExceptionResponse.Create ex
                return Error(GraceError.Create (serialize exceptionResponse) parameters.CorrelationId)
        }
