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

/// Checks the incoming request for an X-Correlation-Id header. If there's no CorrelationId header, it generates one and adds it to the response headers.
type LogRequestHeadersMiddleware(next: RequestDelegate) =

    let log = loggerFactory.CreateLogger($"{nameof LogRequestHeadersMiddleware}.Server")

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
                let isSensitiveHeader (name: string) =
                    name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("token", StringComparison.OrdinalIgnoreCase)

                context.Request.Headers
                |> Seq.iter (fun kv ->
                    let value = if isSensitiveHeader kv.Key then "[REDACTED]" else kv.Value.ToString()
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
