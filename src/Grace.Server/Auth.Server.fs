namespace Grace.Server

open Giraffe
open Grace.Server.Security
open Grace.Server.Services
open Grace.Shared.Utilities
open Grace.Types.Types
open System

module Auth =

    type AuthInfo =
        { GraceUserId: string
          Claims: string list }

    let Me: HttpHandler =
        fun next context ->
            task {
                match PrincipalMapper.tryGetUserId context.User with
                | None ->
                    return! RequestErrors.UNAUTHORIZED "Grace" "Auth" "Authentication required." next context
                | Some userId ->
                    let claims = PrincipalMapper.getEffectiveClaims context.User |> Set.toList
                    let info =
                        { GraceUserId = userId
                          Claims = claims }

                    let correlationId = getCorrelationId context
                    let returnValue = GraceReturnValue.Create info correlationId
                    return! context |> result200Ok returnValue
            }
