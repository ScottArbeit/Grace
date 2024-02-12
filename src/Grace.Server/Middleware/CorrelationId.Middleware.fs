namespace Grace.Server.Middleware

open Grace.Shared
open Grace.Shared.Resources.Text
open Grace.Shared.Utilities
open Microsoft.AspNetCore.Http
open System

/// Checks the incoming request for an X-Correlation-Id header. If there's no CorrelationId header, it generates one and adds it to the response headers.
type CorrelationIdMiddleware(next: RequestDelegate) =

    member this.Invoke(context: HttpContext) =

// -----------------------------------------------------------------------------------------------------
// On the way in...
#if DEBUG
        let middlewareTraceHeader = context.Request.Headers["X-MiddlewareTraceIn"];
        context.Request.Headers["X-MiddlewareTraceIn"] <- $"{middlewareTraceHeader}{nameof(CorrelationIdMiddleware)} --> ";
#endif

        let correlationId = 
            if context.Request.Headers.ContainsKey(Constants.CorrelationIdHeaderKey) then
                context.Request.Headers[Constants.CorrelationIdHeaderKey].ToString()
            else
                generateCorrelationId()
        context.Items.Add(Constants.CorrelationId, correlationId)
        context.Response.Headers.Add(Constants.CorrelationIdHeaderKey, correlationId)

// -----------------------------------------------------------------------------------------------------
// Pass control to next middleware instance...
        let nextTask = next.Invoke(context);

// -----------------------------------------------------------------------------------------------------
// On the way out...
#if DEBUG
        let middlewareTraceOutHeader = context.Request.Headers["X-MiddlewareTraceOut"];
        context.Request.Headers["X-MiddlewareTraceOut"] <- $"{middlewareTraceOutHeader}{nameof(CorrelationIdMiddleware)} --> ";
#endif
        nextTask
