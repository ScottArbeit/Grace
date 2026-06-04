namespace Grace.Server.Middleware

open Grace.Server.ApplicationContext
open Grace.Shared
open Grace.Shared.Utilities
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open System
open System.Threading.Tasks

/// Logs authorization/authentication failures with correlation context so 401/403 responses are diagnosable from server logs.
type LogAuthorizationFailureMiddleware(next: RequestDelegate) =

    let log = loggerFactory.CreateLogger($"{nameof LogAuthorizationFailureMiddleware}.Server")

    let tryGetCorrelationId (context: HttpContext) =
        match context.Items.TryGetValue(Constants.CorrelationId) with
        | true, value ->
            match value with
            | :? string as correlationId when not (String.IsNullOrWhiteSpace correlationId) -> correlationId
            | _ -> String.Empty
        | _ -> String.Empty

    member _.Invoke(context: HttpContext) =
#if DEBUG
        let middlewareTraceHeader = context.Request.Headers["X-MiddlewareTraceIn"]
        context.Request.Headers[ "X-MiddlewareTraceIn" ] <- $"{middlewareTraceHeader}{nameof LogAuthorizationFailureMiddleware} --> "
#endif
        task {
            do! next.Invoke(context)

            let statusCode = context.Response.StatusCode

            if statusCode = StatusCodes.Status401Unauthorized
               || statusCode = StatusCodes.Status403Forbidden then
                let correlationId = tryGetCorrelationId context
                let authorizationHeaderPresent = context.Request.Headers.ContainsKey("Authorization")

                let userAuthenticated =
                    not (isNull context.User)
                    && not (isNull context.User.Identity)
                    && context.User.Identity.IsAuthenticated

                let endpointDisplayName =
                    let endpoint = context.GetEndpoint()

                    if isNull endpoint
                       || String.IsNullOrWhiteSpace endpoint.DisplayName then
                        "<unknown>"
                    else
                        endpoint.DisplayName

                let authChallenge = context.Response.Headers.WWWAuthenticate.ToString()

                if log.IsEnabled(LogLevel.Warning) then
                    log.LogWarning(
                        "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Auth response {StatusCode} for {Method} {Path}. Endpoint: {Endpoint}. Authorization header present: {AuthorizationHeaderPresent}. User authenticated: {UserAuthenticated}. Challenge: {Challenge}.",
                        getCurrentInstantExtended (),
                        Environment.MachineName,
                        correlationId,
                        statusCode,
                        context.Request.Method,
                        context.Request.Path.ToString(),
                        endpointDisplayName,
                        authorizationHeaderPresent,
                        userAuthenticated,
                        authChallenge
                    )
        }
        :> Task
