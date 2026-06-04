namespace Grace.Server.Middleware

open Grace.Shared
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives
open System.Threading.Tasks

module ApiVersionAlias =

    let tryMapToSupportedVersion value =
        match ApiContractVersion.normalize value with
        | Ok normalized when ApiContractVersion.isExplicitOverrideAlias normalized -> Some ApiContractVersion.CurrentReleased
        | _ -> None

type ApiVersionAliasMiddleware(next: RequestDelegate) =

    member _.Invoke(context: HttpContext) =
        match context.Request.Headers.TryGetValue(Constants.ServerApiVersionHeaderKey) with
        | true, values ->
            match ApiVersionAlias.tryMapToSupportedVersion (values.ToString()) with
            | Some mappedVersion -> context.Request.Headers[ Constants.ServerApiVersionHeaderKey ] <- StringValues(mappedVersion)
            | None -> ()
        | false, _ -> ()

        next.Invoke(context)
