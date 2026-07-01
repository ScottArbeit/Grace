namespace Grace.Server.Tests

open Grace.Server.Security
open NUnit.Framework
open System.Security.Claims

/// Covers claim mapping behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type ClaimMappingTests() =
    /// Constructs principal fixtures used by the server unit auth Mapping assertions.
    let createPrincipal (claims: Claim list) =
        let identity = ClaimsIdentity(claims, "Bearer")
        ClaimsPrincipal(identity)

    /// Builds find Values test data for the server unit auth Mapping scenarios in this file.
    let findValues (claimType: string) (claims: Claim list) =
        claims
        |> List.filter (fun claim -> claim.Type = claimType)
        |> List.map (fun claim -> claim.Value)
        |> Set.ofList

    /// Verifies that maps Grace User Id From Subject.
    [<Test>]
    member _.MapsGraceUserIdFromSubject() =
        let principal = createPrincipal [ Claim("sub", "subject-1") ]

        let mapped = ClaimMapping.mapClaims principal
        let userIds = findValues PrincipalMapper.GraceUserIdClaim mapped
        Assert.That(userIds, Is.EquivalentTo([ "subject-1" ]))

    /// Verifies that does Not Override Existing Grace User Id.
    [<Test>]
    member _.DoesNotOverrideExistingGraceUserId() =
        let principal =
            createPrincipal [ Claim(PrincipalMapper.GraceUserIdClaim, "existing-user")
                              Claim("tid", "tenant-2")
                              Claim("oid", "object-2") ]

        let mapped = ClaimMapping.mapClaims principal
        let userIds = findValues PrincipalMapper.GraceUserIdClaim mapped
        Assert.That(userIds.Count, Is.EqualTo(0))

    /// Verifies that maps Roles Scopes Permissions And Groups.
    [<Test>]
    member _.MapsRolesScopesPermissionsAndGroups() =
        let principal =
            createPrincipal [ Claim("roles", "Admin")
                              Claim("scp", "repo.write repo.read")
                              Claim("scope", "repo.list")
                              Claim("permissions", "repo.delete")
                              Claim("groups", "group-1") ]

        let mapped = ClaimMapping.mapClaims principal
        let graceClaims = findValues PrincipalMapper.GraceClaim mapped
        let graceGroups = findValues PrincipalMapper.GraceGroupIdClaim mapped

        Assert.That(
            graceClaims,
            Is.EquivalentTo(
                [
                    "Admin"
                    "repo.write"
                    "repo.read"
                    "repo.list"
                    "repo.delete"
                ]
            )
        )

        Assert.That(graceGroups, Is.EquivalentTo([ "group-1" ]))
