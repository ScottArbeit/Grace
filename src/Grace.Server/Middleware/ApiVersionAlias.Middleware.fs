namespace Grace.Server.Middleware

open Grace.Shared
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives
open System.Threading.Tasks

/// Contains Grace Server api version alias behavior and supporting helpers.
module ApiVersionAlias =

    /// Attempts to map to supported version and returns an option or result instead of throwing.
    let tryMapToSupportedVersion value =
        match ApiContractVersion.normalize value with
        | Ok normalized when ApiContractVersion.isExplicitOverrideAlias normalized -> Some ApiContractVersion.CurrentReleased
        | _ -> None

/// Represents api version alias middleware used by Grace Server APIs and background services.
type ApiVersionAliasMiddleware(next: RequestDelegate) =

    /// Rewrites legacy API-version URL segments before the request reaches endpoint routing.
    member _.Invoke(context: HttpContext) =
        match context.Request.Headers.TryGetValue(Constants.ServerApiVersionHeaderKey) with
        | true, values ->
            match ApiVersionAlias.tryMapToSupportedVersion (values.ToString()) with
            | Some mappedVersion -> context.Request.Headers[ Constants.ServerApiVersionHeaderKey ] <- StringValues(mappedVersion)
            | None -> ()
        | false, _ -> ()

        next.Invoke(context)
