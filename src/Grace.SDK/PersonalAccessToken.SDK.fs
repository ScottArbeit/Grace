namespace Grace.SDK

open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Parameters.Common
open Grace.Shared.Parameters.Auth
open Grace.Shared.Services
open Grace.Shared.Utilities
open Grace.Types.PersonalAccessToken
open Grace.Types.Common
open System
open System.Net
open System.Net.Http
open System.Net.Http.Json
open System.Threading.Tasks

/// SDK wrappers for personal access token creation, listing, and revocation.
module PersonalAccessToken =

    /// Posts token requests to the server URI from the environment and attaches SDK auth and lifecycle handling.
    let private postServerWithEnv<'T, 'U when 'T :> CommonParameters> (parameters: 'T, route: string) =
        task {
            try
                let parameters = Common.ensureCorrelationIdIsSet parameters
                use httpClient = ClientIdentity.getHttpClient parameters.CorrelationId
                do! Auth.addAuthorizationHeader httpClient
                let startTime = getCurrentInstant ()

                let graceServerUri = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.GraceServerUri)
                let serverUri = Uri($"{graceServerUri}/{route}")

                let! response = httpClient.PostAsync(serverUri, createJsonContent parameters)
                let endTime = getCurrentInstant ()

                if response.IsSuccessStatusCode then
                    let! graceReturnValue = response.Content.ReadFromJsonAsync<GraceReturnValue<'U>>(Constants.JsonSerializerOptions)

                    return
                        Ok graceReturnValue
                        |> enhance "ServerResponseTime" $"{(endTime - startTime).TotalMilliseconds:F3} ms"
                        |> ClientIdentity.enhanceWithLifecycleDiagnostics response
                else if response.StatusCode = HttpStatusCode.NotFound then
                    return
                        Error(GraceError.Create $"Server endpoint {route} not found." parameters.CorrelationId)
                        |> ClientIdentity.enhanceWithLifecycleDiagnostics response
                elif response.StatusCode = HttpStatusCode.BadRequest then
                    let! errorMessage = response.Content.ReadAsStringAsync()

                    return
                        Error(GraceError.Create $"{errorMessage}" parameters.CorrelationId)
                        |> enhance "ServerResponseTime" $"{(endTime - startTime).TotalMilliseconds:F3} ms"
                        |> enhance "StatusCode" $"{response.StatusCode}"
                        |> ClientIdentity.enhanceWithLifecycleDiagnostics response
                else
                    let! graceError = ResponseErrors.fromResponse parameters.CorrelationId route response

                    return
                        Error graceError
                        |> enhance "ServerResponseTime" $"{(endTime - startTime).TotalMilliseconds:F3} ms"
                        |> enhance "StatusCode" $"{response.StatusCode}"
                        |> ClientIdentity.enhanceWithLifecycleDiagnostics response
            with
            | ex ->
                let exceptionResponse = Utilities.ExceptionResponse.Create ex
                return Error(GraceError.Create ($"{exceptionResponse}") parameters.CorrelationId)
        }

    /// Requests a new personal access token and returns the one-time token material.
    let Create (parameters: CreatePersonalAccessTokenParameters) =
        postServerWithEnv<CreatePersonalAccessTokenParameters, PersonalAccessTokenCreated> (parameters, "authenticate/token/create")

    /// Lists personal access token summaries without exposing token secrets.
    let List (parameters: ListPersonalAccessTokensParameters) =
        postServerWithEnv<ListPersonalAccessTokensParameters, PersonalAccessTokenSummary list> (parameters, "authenticate/token/list")

    /// Revokes one personal access token and returns the updated token summary.
    let Revoke (parameters: RevokePersonalAccessTokenParameters) =
        postServerWithEnv<RevokePersonalAccessTokenParameters, PersonalAccessTokenSummary> (parameters, "authenticate/token/revoke")
