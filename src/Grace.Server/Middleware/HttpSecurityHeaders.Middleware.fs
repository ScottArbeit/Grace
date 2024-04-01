namespace Grace.Server.Middleware

open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives
open System.Collections.Generic
open Microsoft.Extensions.DependencyInjection

type HttpSecurityHeadersMiddleware(next: RequestDelegate) =

    let Access_Control_Allow_Credentials =
        new KeyValuePair<string, StringValues>("Access-Control-Allow-Credentials", new StringValues("true"))

    let Access_Control_Allow_Methods =
        new KeyValuePair<string, StringValues>("Access-Control-Allow-Methods", new StringValues("GET,POST,PUT"))

    let Access_Control_Allow_Headers =
        new KeyValuePair<string, StringValues>(
            "Access-Control-Allow-Headers",
            new StringValues("Origin, X-Correlation-Id")
        )

    let X_Content_Type_Options =
        new KeyValuePair<string, StringValues>("X-Content-Type-Options", new StringValues("nosniff"))

    let X_Frame_Options =
        new KeyValuePair<string, StringValues>("X-Frame-Options", new StringValues("SAMEORIGIN"))

    let X_Permitted_Cross_Domain_Policies =
        new KeyValuePair<string, StringValues>("X-Permitted-Cross-Domain-Policies", new StringValues("none"))

    let X_XSS_Protection =
        new KeyValuePair<string, StringValues>("X-XSS-Protection", new StringValues("1; mode=block"))

    let Referrer_Policy =
        new KeyValuePair<string, StringValues>("Referrer-Policy", new StringValues("strict-origin-when-cross-origin"))

    let Feature_Policy =
        new KeyValuePair<string, StringValues>(
            "Feature-Policy",
            new StringValues(
                "accelerometer 'none'; ambient-light-sensor 'none'; camera 'none'; geolocation 'none'; gyroscope 'none'; magnetometer 'none'; microphone 'none'; payment 'none'; usb 'none'"
            )
        )

    member this.Invoke(context: HttpContext) =

        // -----------------------------------------------------------------------------------------------------
        // On the way in...
#if DEBUG
        let middlewareTraceHeader = context.Request.Headers["X-MiddlewareTraceIn"]

        context.Request.Headers["X-MiddlewareTraceIn"] <-
            $"{middlewareTraceHeader}{nameof (HttpSecurityHeadersMiddleware)} --> "
#endif

        let headers = context.Response.Headers
        headers.Add(X_Content_Type_Options)
        headers.Add(X_XSS_Protection)
        headers.Add(X_Frame_Options)
        headers.Add(X_Permitted_Cross_Domain_Policies)
        headers.Add(Referrer_Policy)
        headers.Add(Feature_Policy)
        headers.Add(Access_Control_Allow_Credentials)
        headers.Add(Access_Control_Allow_Methods)
        headers.Add(Access_Control_Allow_Headers)

        // -----------------------------------------------------------------------------------------------------
        // Pass control to next middleware instance...
        let nextTask = next.Invoke(context)

        // -----------------------------------------------------------------------------------------------------
        // On the way out...

#if DEBUG
        let middlewareTraceOutHeader = context.Request.Headers["X-MiddlewareTraceOut"]

        context.Request.Headers["X-MiddlewareTraceOut"] <-
            $"{middlewareTraceOutHeader}{nameof (HttpSecurityHeadersMiddleware)} --> "
#endif

        nextTask
