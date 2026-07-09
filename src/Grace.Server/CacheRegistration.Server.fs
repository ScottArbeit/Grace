namespace Grace.Server

open Giraffe
open Grace.Actors.Extensions
open Grace.Server.Security
open Grace.Server.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.CacheRegistration
open Grace.Types.Common
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.JwtBearer
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open System
open System.Text.Json

/// Contains Grace Cache registration HTTP handlers.
module CacheRegistration =

    /// Builds a GraceError for Cache registration route validation failures.
    let private cacheRegistrationError correlationId message = GraceError.Create message correlationId

    /// Binds a Cache registration route body while keeping malformed JSON in the 400 path.
    let private bindJson<'T> (context: HttpContext) correlationId =
        task {
            try
                let! value = context.BindJsonAsync<'T>()
                return Ok value
            with
            | :? JsonException -> return Error(cacheRegistrationError correlationId "Cache registration request body is invalid.")
            | :? BadHttpRequestException -> return Error(cacheRegistrationError correlationId "Cache registration request body is invalid.")
            | :? NullReferenceException -> return Error(cacheRegistrationError correlationId "Cache registration request body is required.")
        }

    /// Determines whether the current request authenticated through the OIDC JWT bearer scheme.
    let private authenticatedSchemeForCache (context: HttpContext) =
        task {
            try
                let! jwtResult = context.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme)

                if jwtResult.Succeeded then return JwtBearerDefaults.AuthenticationScheme
                else if isNull context.User.Identity then return String.Empty
                else return context.User.Identity.AuthenticationType
            with
            | :? InvalidOperationException ->
                if isNull context.User.Identity then
                    return String.Empty
                else
                    return context.User.Identity.AuthenticationType
        }

    /// Loads the server-approved Cache registration boundary from configuration.
    let private getRegistrationConfig (context: HttpContext) =
        let configuration = context.RequestServices.GetRequiredService<IConfiguration>()
        CacheServiceIdentity.requireValidRegistrationConfig configuration

    /// Authorizes a Cache registration operation with the server-owned identity and approval boundary from #617.
    let private authorizeRegistration context requestedScopes requestedCapabilities correlationId =
        task {
            let config = getRegistrationConfig context
            let! authenticatedScheme = authenticatedSchemeForCache context

            match CacheServiceIdentity.decideRegistration config authenticatedScheme context.User requestedScopes requestedCapabilities with
            | CacheServiceIdentity.Allowed servicePrincipalId -> return Ok(servicePrincipalId, config)
            | CacheServiceIdentity.Denied reason -> return Error(cacheRegistrationError correlationId reason)
        }

    /// Handles POST /cache/register.
    let Register: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let correlationId = getCorrelationId context

                try
                    match! bindJson<CacheRegistrationRequest> context correlationId with
                    | Error error -> return! context |> result400BadRequest error
                    | Ok request ->
                        match Lifecycle.validateRequest request with
                        | Error errors ->
                            return!
                                context
                                |> result400BadRequest (cacheRegistrationError correlationId (String.concat " " errors))
                        | Ok () ->
                            let requestedScopes = request.RequestedScopes |> Seq.toList
                            let requestedCapabilities = request.RequestedCapabilities |> Seq.toList

                            match! authorizeRegistration context requestedScopes requestedCapabilities correlationId with
                            | Error error ->
                                return!
                                    context
                                    |> returnResult StatusCodes.Status401Unauthorized error
                            | Ok (servicePrincipalId, _) ->
                                let actorProxy = ActorProxy.CacheRegistration.CreateActorProxy correlationId

                                match!
                                    actorProxy.Register
                                        (
                                            servicePrincipalId,
                                            request,
                                            requestedScopes |> List.toArray,
                                            requestedCapabilities |> List.toArray,
                                            getCurrentInstant (),
                                            correlationId
                                        )
                                    with
                                | Ok result -> return! context |> result200Ok result
                                | Error error -> return! context |> result400BadRequest error
                with
                | ex ->
                    return!
                        context
                        |> result500ServerError (GraceError.CreateWithException ex "Cache registration request failed." correlationId)
            }

    /// Handles POST /cache/refresh.
    let Refresh: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let correlationId = getCorrelationId context

                try
                    match! bindJson<CacheRegistrationRefreshRequest> context correlationId with
                    | Error error -> return! context |> result400BadRequest error
                    | Ok refreshRequest when
                        not (isNull (box refreshRequest))
                        && refreshRequest.Class
                           <> nameof CacheRegistrationRefreshRequest
                        ->
                        return!
                            context
                            |> result400BadRequest (cacheRegistrationError correlationId "Class must be CacheRegistrationRefreshRequest.")
                    | Ok _ ->
                        let config = getRegistrationConfig context

                        match! authorizeRegistration context config.AllowedScopes config.AllowedCapabilities correlationId with
                        | Error error ->
                            return!
                                context
                                |> returnResult StatusCodes.Status401Unauthorized error
                        | Ok (servicePrincipalId, _) ->
                            let actorProxy = ActorProxy.CacheRegistration.CreateActorProxy correlationId

                            match! actorProxy.Refresh(servicePrincipalId, getCurrentInstant (), correlationId) with
                            | Ok result -> return! context |> result200Ok result
                            | Error error -> return! context |> result400BadRequest error
                with
                | ex ->
                    return!
                        context
                        |> result500ServerError (GraceError.CreateWithException ex "Cache registration refresh failed." correlationId)
            }
