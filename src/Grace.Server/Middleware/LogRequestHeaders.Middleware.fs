namespace Grace.Server.Middleware

open Grace.Server
open Grace.Shared
open Grace.Shared.Resources.Text
open Grace.Shared.Utilities
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Microsoft.Extensions.ObjectPool
open System
open System.Text

// Define a StringBuilder policy for the pool
type StringBuilderPooledObjectPolicy() =
    inherit PooledObjectPolicy<StringBuilder>()

    override _.Create() = new StringBuilder()

    override _.Return(sb: StringBuilder) =
        sb.Clear() |> ignore
        true

/// Checks the incoming request for an X-Correlation-Id header. If there's no CorrelationId header, it generates one and adds it to the response headers.
type LogRequestHeadersMiddleware(next: RequestDelegate) =

    let pooledObjectPolicy = StringBuilderPooledObjectPolicy()
    let stringBuilderPool = ObjectPool.Create<StringBuilder>(pooledObjectPolicy)
    let log = ApplicationContext.loggerFactory.CreateLogger(nameof (LogRequestHeadersMiddleware))

    member this.Invoke(context: HttpContext) =

        // -----------------------------------------------------------------------------------------------------
        // On the way in...
#if DEBUG
        let middlewareTraceHeader = context.Request.Headers["X-MiddlewareTraceIn"]

        context.Request.Headers["X-MiddlewareTraceIn"] <- $"{middlewareTraceHeader}{nameof (LogRequestHeadersMiddleware)} --> "
#endif
        //let path = context.Request.Path.ToString()

        //if path <> "/healthz" then
        //    logToConsole $"In LogRequestHeadersMiddleware.Middleware.fs: Path: {path}."

        if log.IsEnabled(LogLevel.Debug) then
            let sb = stringBuilderPool.Get()

            try
                context.Request.Headers
                |> Seq.iter (fun kv -> sb.AppendLine($"{kv.Key} = {kv.Value}") |> ignore)

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

        context.Request.Headers["X-MiddlewareTraceOut"] <- $"{middlewareTraceOutHeader}{nameof (LogRequestHeadersMiddleware)} --> "
#endif
        nextTask
