namespace Grace.Authorization.Tests

open Grace.Shared.Authorization
open Grace.Shared.Utilities
open Grace.Types.Authorization
open Grace.Types.Types
open NUnit.Framework
open System
open System.Collections.Generic

[<Parallelizable(ParallelScope.All)>]
type PathPermissionsTests() =

    let ownerId = Guid.Parse("11111111-1111-1111-1111-111111111111")
    let organizationId = Guid.Parse("22222222-2222-2222-2222-222222222222")
    let repositoryId = Guid.Parse("33333333-3333-3333-3333-333333333333")

    let principal = { PrincipalType = PrincipalType.User; PrincipalId = "user-1" }

    let createAssignment scope roleId =
        {
            Principal = principal
            Scope = scope
            RoleId = roleId
            Source = "test"
            SourceDetail = None
            CreatedAt = getCurrentInstant ()
        }

    [<Test>]
    member _.NormalizesPathSeparators() =
        let permissions = List<ClaimPermission>()
        permissions.Add({ Claim = "engineering"; DirectoryPermission = DirectoryPermission.Modify })

        let pathPermissions = [ { Path = "/images"; Permissions = permissions } ]
        let claims = Set.ofList [ "engineering" ]

        let result = checkPathPermission pathPermissions claims "\\images" PathWrite

        match result with
        | Some(Allowed _) -> ()
        | Some(Denied reason) -> Assert.Fail($"Expected Allowed but got Denied: {reason}")
        | None -> Assert.Fail("Expected path permission to match normalized path.")

    [<Test>]
    member _.WeirdPathsDoNotThrowAndDenyByDefault() =
        let weirdPaths =
            [
                ""
                " "
                "."
                ".."
                "../x"
                "./x"
                "//"
                "///"
                "/../x"
                "/./x"
            ]

        for path in weirdPaths do
            let resource = Resource.Path(ownerId, organizationId, repositoryId, path)

            let result =
                checkPermission
                    (RoleCatalog.getAll ())
                    []
                    []
                    [ principal ]
                    Set.empty
                    Operation.PathRead
                    resource

            match result with
            | Denied _ -> ()
            | Allowed reason -> Assert.Fail($"Expected Denied but got Allowed for '{path}': {reason}")

