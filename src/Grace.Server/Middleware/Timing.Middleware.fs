namespace Grace.Server.Middleware

open Grace.Actors.Services
open Grace.Actors.Timing
open Grace.Actors.Types
open Grace.Server
open Grace.Server.ApplicationContext
open Grace.Shared
open Grace.Shared.Resources.Text
open Grace.Shared.Utilities
open Microsoft.AspNetCore.Http
open System
open System.Collections.Generic

/// Records how long the request took to process.
///
/// This middleware should be the second in order, after CorrelationIdMiddleware. This will ensure that the CorrelationId is set before we start recording timings.
type TimingMiddleware(next: RequestDelegate) =

    member this.Invoke(context: HttpContext) =
        task {
            let isInteresting path =
                match path with
                | "/metrics"
                | "/healthz" -> false
                | path when path.StartsWith "/actors" -> false
                | _ -> true

            // -----------------------------------------------------------------------------------------------------
            // On the way in...
#if DEBUG
            let middlewareTraceHeader = context.Request.Headers["X-MiddlewareTraceIn"]

            context.Request.Headers["X-MiddlewareTraceIn"] <- $"{middlewareTraceHeader}{nameof TimingMiddleware} --> "
#endif

            // We expect the CorrelationId to be set at this point.
            let correlationId = Services.getCorrelationId context

            if isInteresting context.Request.Path.Value then
                // Mark this as the start of timing this request.
                addTiming TimingFlag.Initial String.Empty correlationId

            // -----------------------------------------------------------------------------------------------------
            // Pass control to next middleware instance...
            do! next.Invoke(context)

            if isInteresting context.Request.Path.Value then
                // Mark this as the end of timing this request.
                addTiming TimingFlag.Final String.Empty correlationId

                // Write the timings to the timing log.
                reportTimings context.Request.Path correlationId

                // Remove this set of timings from the dictionary.
                removeTiming correlationId

        // -----------------------------------------------------------------------------------------------------
        // On the way out...
#if DEBUG
            let middlewareTraceOutHeader = context.Request.Headers["X-MiddlewareTraceOut"]
            context.Request.Headers["X-MiddlewareTraceOut"] <- $"{middlewareTraceOutHeader}{nameof TimingMiddleware} --> "
#endif
        }
