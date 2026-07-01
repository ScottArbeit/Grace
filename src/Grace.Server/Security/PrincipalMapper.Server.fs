namespace Grace.Server.Security

open Grace.Types.Authorization
open System
open System.Security.Claims

/// Contains Grace Server principal mapper behavior and supporting helpers.
module PrincipalMapper =

    [<Literal>]
    let GraceUserIdClaim = "grace_user_id"

    [<Literal>]
    let GraceGroupIdClaim = "grace_group_id"

    [<Literal>]
    let GraceClaim = "grace_claim"

    /// Gets try get user id data needed by the server flow.
    let tryGetUserId (principal: ClaimsPrincipal) =
        principal.Claims
        |> Seq.tryFind (fun claim -> claim.Type = GraceUserIdClaim)
        |> Option.map (fun claim -> claim.Value)
        |> Option.filter (fun value -> not (String.IsNullOrWhiteSpace value))

    /// Returns claim values for a principal while tolerating unauthenticated identities.
    let private getClaims (principal: ClaimsPrincipal) (claimType: string) =
        principal.Claims
        |> Seq.filter (fun claim -> claim.Type = claimType)
        |> Seq.map (fun claim -> claim.Value)
        |> Seq.filter (fun value -> not (String.IsNullOrWhiteSpace value))
        |> Seq.toList

    /// Maps a claims principal into the user and claim principals considered by RBAC.
    let getPrincipals (principal: ClaimsPrincipal) =
        let userId = tryGetUserId principal
        let groupIds = getClaims principal GraceGroupIdClaim

        let principals =
            [
                if userId.IsSome then
                    { PrincipalType = PrincipalType.User; PrincipalId = userId.Value }
                for groupId in groupIds do
                    { PrincipalType = PrincipalType.Group; PrincipalId = groupId }
            ]

        principals

    /// Produces the claim dictionary exposed to callers after principal mapping.
    let getEffectiveClaims (principal: ClaimsPrincipal) =
        let claimValues = getClaims principal GraceClaim |> Set.ofList

        let groupClaims =
            getClaims principal GraceGroupIdClaim
            |> Set.ofList

        Set.union claimValues groupClaims
