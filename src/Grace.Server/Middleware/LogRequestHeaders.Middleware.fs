namespace Grace.Server.Middleware

open Grace.Server
open Grace.Server.ApplicationContext
open Grace.Shared
open Grace.Shared.Resources.Text
open Grace.Shared.Utilities
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Microsoft.Extensions.ObjectPool
open System
open System.Text

/// Contains Grace Server request header redaction behavior and supporting helpers.
module RequestHeaderRedaction =

    /// Determines whether sensitive header.
    let isSensitiveHeader (name: string) =
        let normalizedName =
            name
                .Replace("-", String.Empty)
                .Replace("_", String.Empty)

        name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
        || normalizedName.Contains("token", StringComparison.OrdinalIgnoreCase)
        || normalizedName.Contains("apikey", StringComparison.OrdinalIgnoreCase)
        || normalizedName.Contains("clientsecret", StringComparison.OrdinalIgnoreCase)
        || normalizedName.Contains("signingsecret", StringComparison.OrdinalIgnoreCase)
        || normalizedName.Contains("signature", StringComparison.OrdinalIgnoreCase)
        || normalizedName.Contains("credential", StringComparison.OrdinalIgnoreCase)

    /// Computes redact header value data used by Grace Server.
    let redactHeaderValue name value = if isSensitiveHeader name then "[REDACTED]" else value

/// Checks the incoming request for an X-Correlation-Id header. If there's no CorrelationId header, it generates one and adds it to the response headers.
type LogRequestHeadersMiddleware(next: RequestDelegate) =

    let log = loggerFactory.CreateLogger($"{nameof LogRequestHeadersMiddleware}.Server")

    /// Logs request headers while redacting authorization-sensitive values.
    member this.Invoke(context: HttpContext) =

        // -----------------------------------------------------------------------------------------------------
        // On the way in...
#if DEBUG
        let middlewareTraceHeader = context.Request.Headers["X-MiddlewareTraceIn"]

        context.Request.Headers[ "X-MiddlewareTraceIn" ] <- $"{middlewareTraceHeader}{nameof LogRequestHeadersMiddleware} --> "
#endif
        //let path = context.Request.Path.ToString()

        //if path = "/healthz" then
        //    logToConsole $"In LogRequestHeadersMiddleware.Middleware.fs: Path: {path}."

        if log.IsEnabled(LogLevel.Debug) then
            let sb = stringBuilderPool.Get()

            try
                context.Request.Headers
                |> Seq.iter (fun kv ->
                    let value = RequestHeaderRedaction.redactHeaderValue kv.Key (kv.Value.ToString())
                    sb.AppendLine($"{kv.Key} = {value}") |> ignore)

                log.LogDebug("Request headers: {headers}", sb.ToString())
            finally
                stringBuilderPool.Return(sb)

        // -----------------------------------------------------------------------------------------------------
        // Pass control to next middleware instance...
        let nextTask = next.Invoke(context)

        // -----------------------------------------------------------------------------------------------------
        // On the way out...
#if DEBUG
        let middlewareTraceOutHeader = context.Request.Headers["X-MiddlewareTraceOut"]

        context.Request.Headers[ "X-MiddlewareTraceOut" ] <- $"{middlewareTraceOutHeader}{nameof LogRequestHeadersMiddleware} --> "
#endif
        nextTask
