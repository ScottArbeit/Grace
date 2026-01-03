namespace Grace.SDK

open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Parameters.Common
open Grace.Shared.Parameters.Auth
open Grace.Shared.Services
open Grace.Shared.Utilities
open Grace.Types.PersonalAccessToken
open Grace.Types.Types
open System
open System.Net
open System.Net.Http
open System.Net.Http.Json
open System.Threading.Tasks

module PersonalAccessToken =

    let private postServerWithEnv<'T, 'U when 'T :> CommonParameters> (parameters: 'T, route: string) =
        task {
            try
                let parameters = Common.ensureCorrelationIdIsSet parameters
                use httpClient = getHttpClient parameters.CorrelationId
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
                        let graceError = deserialize<GraceError> responseAsString

                        return
                            Error graceError
                            |> enhance "ServerResponseTime" $"{(endTime - startTime).TotalMilliseconds:F3} ms"
                            |> enhance "StatusCode" $"{response.StatusCode}"
                    with _ ->
                        return
                            Error(GraceError.Create $"{responseAsString}" parameters.CorrelationId)
                            |> enhance "ServerResponseTime" $"{(endTime - startTime).TotalMilliseconds:F3} ms"
                            |> enhance "StatusCode" $"{response.StatusCode}"
            with ex ->
                let exceptionResponse = Utilities.ExceptionResponse.Create ex
                return Error(GraceError.Create ($"{exceptionResponse}") parameters.CorrelationId)
        }

    let Create (parameters: CreatePersonalAccessTokenParameters) =
        postServerWithEnv<CreatePersonalAccessTokenParameters, PersonalAccessTokenCreated> (parameters, "auth/token/create")

    let List (parameters: ListPersonalAccessTokensParameters) =
        postServerWithEnv<ListPersonalAccessTokensParameters, PersonalAccessTokenSummary list> (parameters, "auth/token/list")

    let Revoke (parameters: RevokePersonalAccessTokenParameters) =
        postServerWithEnv<RevokePersonalAccessTokenParameters, PersonalAccessTokenSummary> (parameters, "auth/token/revoke")
