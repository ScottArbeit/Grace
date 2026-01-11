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

/// Checks the incoming request for an X-Correlation-Id header. If there's no CorrelationId header, it generates one and adds it to the response headers.
type FakeMiddleware(next: RequestDelegate) =

    let pooledObjectPolicy = StringBuilderPooledObjectPolicy()
    let stringBuilderPool = ObjectPool.Create<StringBuilder>(pooledObjectPolicy)
    let log = ApplicationContext.loggerFactory.CreateLogger(nameof FakeMiddleware)

    member this.Invoke(context: HttpContext) =

        // -----------------------------------------------------------------------------------------------------
        // On the way in...
#if DEBUG
        let middlewareTraceHeader = context.Request.Headers["X-MiddlewareTraceIn"]

        context.Request.Headers[ "X-MiddlewareTraceIn" ] <- $"{middlewareTraceHeader}{nameof FakeMiddleware} --> "
#endif

        let path = context.Request.Path.ToString()
        logToConsole $"****In FakeMiddleware; Path: {path}."

        // -----------------------------------------------------------------------------------------------------
        // Pass control to next middleware instance...
        let nextTask = next.Invoke(context)

        // -----------------------------------------------------------------------------------------------------
        // On the way out...
#if DEBUG
        let middlewareTraceOutHeader = context.Request.Headers["X-MiddlewareTraceOut"]

        context.Request.Headers[ "X-MiddlewareTraceOut" ] <- $"{middlewareTraceOutHeader}{nameof FakeMiddleware} --> "
#endif
        nextTask
