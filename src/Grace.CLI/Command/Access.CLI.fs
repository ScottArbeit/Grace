namespace Grace.CLI.Command

open Grace.CLI.Common
open Grace.CLI.Text
open Grace.CLI.Services
open Grace.SDK.Access
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Parameters.Access
open Grace.Shared.Utilities
open Grace.Types.Access
open Grace.Types.Types
open Spectre.Console
open Spectre.Console.Json
open System
open System.CommandLine
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.Threading
open System.Threading.Tasks

module Access =

    module private Options =
        let user = new Option<string>("--user", Required = false, Description = "User id for the principal.")

        let group = new Option<string>("--group", Required = false, Description = "Group id for the principal.")

        let service = new Option<string>("--service", Required = false, Description = "Service id for the principal.")

        let owner =
            new Option<Guid>(
                "--owner",
                Required = false,
                Description = "Owner id <Guid>.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> Current().OwnerId)
            )

        let organization =
            new Option<Guid>(
                "--org",
                Required = false,
                Description = "Organization id <Guid>.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> Current().OrganizationId)
            )

        let repository =
            new Option<Guid>(
                "--repo",
                Required = false,
                Description = "Repository id <Guid>.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> Current().RepositoryId)
            )

        let branch =
            new Option<Guid>(
                "--branch",
                Required = false,
                Description = "Branch id <Guid>.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> Current().BranchId)
            )

        let roleId = new Option<string>("--role", Required = true, Description = "Role id.", Arity = ArgumentArity.ExactlyOne)

        let source = new Option<string>("--source", Required = false, Description = "Role assignment source.", Arity = ArgumentArity.ZeroOrOne)

        let path = new Option<string>("--path", Required = false, Description = "Repository path.", Arity = ArgumentArity.ZeroOrOne)

        let access =
            (new Option<string>("--access", Required = false, Description = "Access type: allow|deny.", Arity = ArgumentArity.ZeroOrOne))
                .AcceptOnlyFromAmong([| "allow"; "deny" |])

        let perm =
            (new Option<string>("--perm", Required = false, Description = "Permission: read|write|read-write.", Arity = ArgumentArity.ZeroOrOne))
                .AcceptOnlyFromAmong([| "read"; "write"; "read-write" |])

        let operation =
            (new Option<string>("--operation", Required = true, Description = "Operation to check.", Arity = ArgumentArity.ExactlyOne))
                .AcceptOnlyFromAmong(listCases<Operation> ())

        let json = new Option<bool>("--json", Required = false, Description = "Emit JSON output.")

        let dryRun = new Option<bool>("--dry-run", Required = false, Description = "Print the request payload without executing.")

        let effective = new Option<bool>("--effective", Required = false, Description = "Request effective roles (server computed).")

    let private renderJson (value: obj) = value |> serialize |> JsonText |> AnsiConsole.Write

    let private renderResult (parseResult: ParseResult) result =
        if parseResult.GetValue(Options.json) then
            match result with
            | Ok value -> renderJson value
            | Error error -> renderJson error
        else
            result |> renderOutput parseResult |> ignore

    let private principalFromOptions (parseResult: ParseResult) =
        let user = parseResult.GetValue(Options.user)
        let group = parseResult.GetValue(Options.group)
        let service = parseResult.GetValue(Options.service)

        let provided =
            [ ("user", user); ("group", group); ("service", service) ]
            |> List.filter (fun (_, value) -> not <| String.IsNullOrWhiteSpace value)

        match provided with
        | [ ("user", value) ] -> Ok("user", value)
        | [ ("group", value) ] -> Ok("group", value)
        | [ ("service", value) ] -> Ok("service", value)
        | _ -> Error "Specify exactly one of --user, --group, or --service."

    let private scopeFromOptions (parseResult: ParseResult) =
        let ownerId = parseResult.GetValue(Options.owner)
        let organizationId = parseResult.GetValue(Options.organization)
        let repositoryId = parseResult.GetValue(Options.repository)
        let branchId = parseResult.GetValue(Options.branch)

        let hasBranch = branchId <> Guid.Empty
        let hasRepo = repositoryId <> Guid.Empty
        let hasOrg = organizationId <> Guid.Empty
        let hasOwner = ownerId <> Guid.Empty

        if hasBranch then Ok("branch", ownerId, organizationId, repositoryId, branchId)
        elif hasRepo then Ok("repo", ownerId, organizationId, repositoryId, Guid.Empty)
        elif hasOrg then Ok("org", ownerId, organizationId, Guid.Empty, Guid.Empty)
        elif hasOwner then Ok("owner", ownerId, Guid.Empty, Guid.Empty, Guid.Empty)
        else Error "Specify --owner, --org, --repo, or --branch."

    let private buildPrincipalDto (principalType: string) (principalId: string) =
        let dto = PrincipalDto()
        dto.Type <- principalType
        dto.Id <- principalId
        dto

    let private buildScopeDto (scopeType: string) (ownerId: Guid) (organizationId: Guid) (repositoryId: Guid) (branchId: Guid) =
        let dto = ScopeDto()
        dto.Type <- scopeType
        dto.OwnerId <- $"{ownerId}"
        dto.OrganizationId <- $"{organizationId}"
        dto.RepositoryId <- $"{repositoryId}"
        dto.BranchId <- $"{branchId}"
        dto

    let private buildResourceDto (scopeType: string) (ownerId: Guid) (organizationId: Guid) (repositoryId: Guid) (branchId: Guid) (path: string) =
        let dto = ResourceDto()
        dto.Type <- scopeType
        dto.OwnerId <- $"{ownerId}"
        dto.OrganizationId <- $"{organizationId}"
        dto.RepositoryId <- $"{repositoryId}"
        dto.BranchId <- $"{branchId}"
        dto.Path <- path
        dto

    type GrantRole() =
        inherit AsynchronousCommandLineAction()

        override this.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                match principalFromOptions parseResult, scopeFromOptions parseResult with
                | Ok(principalType, principalId), Ok(scopeType, ownerId, organizationId, repositoryId, branchId) ->
                    let request = GrantRoleRequest()
                    request.Principal <- buildPrincipalDto principalType principalId
                    request.Scope <- buildScopeDto scopeType ownerId organizationId repositoryId branchId
                    request.RoleId <- parseResult.GetValue(Options.roleId)
                    request.Source <- parseResult.GetValue(Options.source)

                    if parseResult.GetValue(Options.dryRun) then
                        renderJson request
                        return 0
                    else
                        let! result = Role.GrantRoleAsync request
                        renderResult parseResult result
                        return 0
                | Error error, _ ->
                    renderJson error
                    return 1
                | _, Error error ->
                    renderJson error
                    return 1
            }

    type RevokeRole() =
        inherit AsynchronousCommandLineAction()

        override this.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                match principalFromOptions parseResult, scopeFromOptions parseResult with
                | Ok(principalType, principalId), Ok(scopeType, ownerId, organizationId, repositoryId, branchId) ->
                    let request = RevokeRoleRequest()
                    request.Principal <- buildPrincipalDto principalType principalId
                    request.Scope <- buildScopeDto scopeType ownerId organizationId repositoryId branchId
                    request.RoleId <- parseResult.GetValue(Options.roleId)

                    if parseResult.GetValue(Options.dryRun) then
                        renderJson request
                        return 0
                    else
                        let! result = Role.RevokeRoleAsync request
                        renderResult parseResult result
                        return 0
                | Error error, _ ->
                    renderJson error
                    return 1
                | _, Error error ->
                    renderJson error
                    return 1
            }

    type ListRoles() =
        inherit AsynchronousCommandLineAction()

        override this.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                match scopeFromOptions parseResult with
                | Ok(scopeType, ownerId, organizationId, repositoryId, branchId) ->
                    let request = ListRoleAssignmentsRequest()
                    request.Scope <- buildScopeDto scopeType ownerId organizationId repositoryId branchId

                    match principalFromOptions parseResult with
                    | Ok(principalType, principalId) -> request.Principal <- buildPrincipalDto principalType principalId
                    | Error _ -> ()

                    if parseResult.GetValue(Options.dryRun) then
                        renderJson request
                        return 0
                    else
                        let! result = Role.ListRoleAssignmentsAsync request
                        renderResult parseResult result
                        return 0
                | Error error ->
                    renderJson error
                    return 1
            }

    type PathAdd() =
        inherit AsynchronousCommandLineAction()

        override this.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                match principalFromOptions parseResult, scopeFromOptions parseResult with
                | Ok(principalType, principalId), Ok(_, ownerId, organizationId, repositoryId, _) ->
                    let request = AddPathAceRequest()
                    request.Principal <- buildPrincipalDto principalType principalId
                    request.OwnerId <- $"{ownerId}"
                    request.OrganizationId <- $"{organizationId}"
                    request.RepositoryId <- $"{repositoryId}"
                    request.Path <- parseResult.GetValue(Options.path)
                    request.Access <- parseResult.GetValue(Options.access)

                    let permission = parseResult.GetValue(Options.perm)

                    if not <| String.IsNullOrWhiteSpace permission then
                        request.Permissions.Add permission

                    if parseResult.GetValue(Options.dryRun) then
                        renderJson request
                        return 0
                    else
                        let! result = Path.AddPathAceAsync request
                        renderResult parseResult result
                        return 0
                | Error error, _ ->
                    renderJson error
                    return 1
                | _, Error error ->
                    renderJson error
                    return 1
            }

    type PathRemove() =
        inherit AsynchronousCommandLineAction()

        override this.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                match principalFromOptions parseResult, scopeFromOptions parseResult with
                | Ok(principalType, principalId), Ok(_, ownerId, organizationId, repositoryId, _) ->
                    let request = RemovePathAceRequest()
                    request.Principal <- buildPrincipalDto principalType principalId
                    request.OwnerId <- $"{ownerId}"
                    request.OrganizationId <- $"{organizationId}"
                    request.RepositoryId <- $"{repositoryId}"
                    request.Path <- parseResult.GetValue(Options.path)

                    let permission = parseResult.GetValue(Options.perm)

                    if not <| String.IsNullOrWhiteSpace permission then
                        request.Permissions.Add permission

                    if parseResult.GetValue(Options.dryRun) then
                        renderJson request
                        return 0
                    else
                        let! result = Path.RemovePathAceAsync request
                        renderResult parseResult result
                        return 0
                | Error error, _ ->
                    renderJson error
                    return 1
                | _, Error error ->
                    renderJson error
                    return 1
            }

    type PathList() =
        inherit AsynchronousCommandLineAction()

        override this.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                match scopeFromOptions parseResult with
                | Ok(_, ownerId, organizationId, repositoryId, _) ->
                    let request = ListPathAclsRequest()
                    request.OwnerId <- $"{ownerId}"
                    request.OrganizationId <- $"{organizationId}"
                    request.RepositoryId <- $"{repositoryId}"
                    request.PathPrefix <- parseResult.GetValue(Options.path)

                    match principalFromOptions parseResult with
                    | Ok(principalType, principalId) -> request.Principal <- buildPrincipalDto principalType principalId
                    | Error _ -> ()

                    if parseResult.GetValue(Options.dryRun) then
                        renderJson request
                        return 0
                    else
                        let! result = Path.ListPathAclsAsync request
                        renderResult parseResult result
                        return 0
                | Error error ->
                    renderJson error
                    return 1
            }

    type TestPermission() =
        inherit AsynchronousCommandLineAction()

        override this.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                match principalFromOptions parseResult, scopeFromOptions parseResult with
                | Ok(principalType, principalId), Ok(scopeType, ownerId, organizationId, repositoryId, branchId) ->
                    let request = CheckPermissionRequest()
                    request.Principal <- buildPrincipalDto principalType principalId
                    request.Operation <- parseResult.GetValue(Options.operation)
                    let pathValue = parseResult.GetValue(Options.path)
                    let resourceType = if String.IsNullOrWhiteSpace pathValue then scopeType else "path"

                    request.Resource <- buildResourceDto resourceType ownerId organizationId repositoryId branchId pathValue

                    if parseResult.GetValue(Options.dryRun) then
                        renderJson request
                        return 0
                    else
                        let! result = Permission.CheckPermissionAsync request
                        renderResult parseResult result
                        return 0
                | Error error, _ ->
                    renderJson error
                    return 1
                | _, Error error ->
                    renderJson error
                    return 1
            }

    let Build =
        let access = Command("access", "Manage access control and permissions.")

        let grant =
            Command("grant-role", "Grant a role assignment.")
            |> addOption Options.user
            |> addOption Options.group
            |> addOption Options.service
            |> addOption Options.owner
            |> addOption Options.organization
            |> addOption Options.repository
            |> addOption Options.branch
            |> addOption Options.roleId
            |> addOption Options.source
            |> addOption Options.json
            |> addOption Options.dryRun

        grant.Action <- new GrantRole()

        let revoke =
            Command("revoke-role", "Revoke a role assignment.")
            |> addOption Options.user
            |> addOption Options.group
            |> addOption Options.service
            |> addOption Options.owner
            |> addOption Options.organization
            |> addOption Options.repository
            |> addOption Options.branch
            |> addOption Options.roleId
            |> addOption Options.json
            |> addOption Options.dryRun

        revoke.Action <- new RevokeRole()

        let listRoles =
            Command("list-roles", "List direct role assignments.")
            |> addOption Options.owner
            |> addOption Options.organization
            |> addOption Options.repository
            |> addOption Options.branch
            |> addOption Options.user
            |> addOption Options.group
            |> addOption Options.service
            |> addOption Options.effective
            |> addOption Options.json
            |> addOption Options.dryRun

        listRoles.Action <- new ListRoles()

        let path = Command("path", "Manage path ACLs.")

        let pathAdd =
            Command("add", "Add a path ACL entry.")
            |> addOption Options.user
            |> addOption Options.group
            |> addOption Options.service
            |> addOption Options.owner
            |> addOption Options.organization
            |> addOption Options.repository
            |> addOption Options.path
            |> addOption Options.access
            |> addOption Options.perm
            |> addOption Options.json
            |> addOption Options.dryRun

        pathAdd.Action <- new PathAdd()

        let pathRemove =
            Command("remove", "Remove a path ACL entry.")
            |> addOption Options.user
            |> addOption Options.group
            |> addOption Options.service
            |> addOption Options.owner
            |> addOption Options.organization
            |> addOption Options.repository
            |> addOption Options.path
            |> addOption Options.perm
            |> addOption Options.json
            |> addOption Options.dryRun

        pathRemove.Action <- new PathRemove()

        let pathList =
            Command("list", "List path ACL entries.")
            |> addOption Options.owner
            |> addOption Options.organization
            |> addOption Options.repository
            |> addOption Options.path
            |> addOption Options.user
            |> addOption Options.group
            |> addOption Options.service
            |> addOption Options.json
            |> addOption Options.dryRun

        pathList.Action <- new PathList()

        path.Subcommands.Add(pathAdd)
        path.Subcommands.Add(pathRemove)
        path.Subcommands.Add(pathList)

        let testPermission =
            Command("test", "Check a permission for a principal.")
            |> addOption Options.user
            |> addOption Options.group
            |> addOption Options.service
            |> addOption Options.owner
            |> addOption Options.organization
            |> addOption Options.repository
            |> addOption Options.branch
            |> addOption Options.operation
            |> addOption Options.path
            |> addOption Options.json
            |> addOption Options.dryRun

        testPermission.Action <- new TestPermission()

        access.Subcommands.Add(grant)
        access.Subcommands.Add(revoke)
        access.Subcommands.Add(listRoles)
        access.Subcommands.Add(path)
        access.Subcommands.Add(testPermission)
        access
