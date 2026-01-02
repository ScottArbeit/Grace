namespace Grace.Server.Security

open System
open System.Collections.Generic
open System.Security.Claims

module ClaimMapping =

    let private getClaimValues (principal: ClaimsPrincipal) (claimType: string) =
        principal.Claims
        |> Seq.filter (fun claim -> String.Equals(claim.Type, claimType, StringComparison.OrdinalIgnoreCase))
        |> Seq.map (fun claim -> claim.Value)
        |> Seq.filter (fun value -> not (String.IsNullOrWhiteSpace value))
        |> Seq.toList

    let private tryGetClaimValue (principal: ClaimsPrincipal) (claimTypes: string list) =
        claimTypes
        |> Seq.tryPick (fun claimType ->
            principal.Claims
            |> Seq.tryFind (fun claim -> String.Equals(claim.Type, claimType, StringComparison.OrdinalIgnoreCase))
            |> Option.map (fun claim -> claim.Value))
        |> Option.filter (fun value -> not (String.IsNullOrWhiteSpace value))

    let private splitScopes (value: string) =
        value.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries)
        |> Seq.map (fun scopeValue -> scopeValue.Trim())
        |> Seq.filter (fun scopeValue -> not (String.IsNullOrWhiteSpace scopeValue))
        |> Seq.toList

    let computeGraceUserId (principal: ClaimsPrincipal) =
        let existing =
            principal.Claims
            |> Seq.tryFind (fun claim -> claim.Type = PrincipalMapper.GraceUserIdClaim)
            |> Option.map (fun claim -> claim.Value)
            |> Option.filter (fun value -> not (String.IsNullOrWhiteSpace value))

        match existing with
        | Some _ -> None
        | None ->
            let tenantId = tryGetClaimValue principal [ "tid" ]
            let objectId = tryGetClaimValue principal [ "oid" ]

            match tenantId, objectId with
            | Some tid, Some oid -> Some($"{tid}:{oid}")
            | _ ->
                let issuer = tryGetClaimValue principal [ "iss" ]
                let subject = tryGetClaimValue principal [ "sub"; ClaimTypes.NameIdentifier ]

                match issuer, subject with
                | Some iss, Some sub -> Some($"{iss}|{sub}")
                | _ -> tryGetClaimValue principal [ "sub"; ClaimTypes.NameIdentifier; "preferred_username"; ClaimTypes.Email ]

    let mapClaims (principal: ClaimsPrincipal) =
        let claimsToAdd = ResizeArray<Claim>()

        match computeGraceUserId principal with
        | Some userId -> claimsToAdd.Add(Claim(PrincipalMapper.GraceUserIdClaim, userId))
        | None -> ()

        let existingGraceClaims = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        let existingGroupClaims = HashSet<string>(StringComparer.OrdinalIgnoreCase)

        for value in getClaimValues principal PrincipalMapper.GraceClaim do
            existingGraceClaims.Add(value) |> ignore

        for value in getClaimValues principal PrincipalMapper.GraceGroupIdClaim do
            existingGroupClaims.Add(value) |> ignore

        let roleClaims = (getClaimValues principal "roles") @ (getClaimValues principal ClaimTypes.Role)

        for value in roleClaims do
            if existingGraceClaims.Add(value) then
                claimsToAdd.Add(Claim(PrincipalMapper.GraceClaim, value))

        for value in getClaimValues principal "wids" do
            if existingGraceClaims.Add(value) then
                claimsToAdd.Add(Claim(PrincipalMapper.GraceClaim, value))

        for value in getClaimValues principal "scp" |> List.collect splitScopes do
            if existingGraceClaims.Add(value) then
                claimsToAdd.Add(Claim(PrincipalMapper.GraceClaim, value))

        for value in getClaimValues principal "groups" do
            if existingGroupClaims.Add(value) then
                claimsToAdd.Add(Claim(PrincipalMapper.GraceGroupIdClaim, value))

        claimsToAdd |> Seq.toList
