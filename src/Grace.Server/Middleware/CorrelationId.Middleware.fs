namespace Grace.Server.Middleware

open Grace.Shared
open Grace.Shared.Resources.Text
open Grace.Shared.Utilities
open Microsoft.AspNetCore.Http
open System

type CorrelationIdMiddleware(next: RequestDelegate) =

    member this.Invoke(context: HttpContext) =

// -----------------------------------------------------------------------------------------------------
// On the way in...
#if DEBUG
        let middlewareTraceHeader = context.Request.Headers["X-MiddlewareTraceIn"];
        context.Request.Headers["X-MiddlewareTraceIn"] <- $"{middlewareTraceHeader}{this.GetType().Name} --> ";
#endif

        let correlationId = 
            if context.Request.Headers.ContainsKey(Constants.CorrelationIdHeaderKey) then
                context.Request.Headers[Constants.CorrelationIdHeaderKey].ToString()
            else
                Guid.NewGuid().ToString()
        context.Items.Add(Constants.CorrelationId, correlationId)
        context.Response.Headers.Add(Constants.CorrelationIdHeaderKey, correlationId)

// -----------------------------------------------------------------------------------------------------
// Pass control to next middleware instance...
        let nextTask = next.Invoke(context);
// -----------------------------------------------------------------------------------------------------
// On the way out...

#if DEBUG
        let middlewareTraceOutHeader = context.Request.Headers["X-MiddlewareTraceOut"];
        context.Request.Headers["X-MiddlewareTraceOut"] <- $"{middlewareTraceOutHeader}{this.GetType().Name} --> ";
#endif
        nextTask
