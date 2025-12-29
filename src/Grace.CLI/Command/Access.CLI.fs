namespace Grace.CLI.Command

open FSharpPlus
open Grace.CLI.Common
open Grace.CLI.Common.Validations
open Grace.CLI.Services
open Grace.CLI.Text
open Grace.SDK
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Parameters.Access
open Grace.Shared.Parameters.Common
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Grace.Types.Authorization
open Grace.Types.Types
open Spectre.Console
open Spectre.Console.Json
open System
open System.Collections.Generic
open System.CommandLine
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.Threading
open System.Threading.Tasks

module Access =

    module private Options =
        let ownerId =
            new Option<OwnerId>(
                OptionName.OwnerId,
                Required = false,
                Description = "The owner ID <Guid>.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> if Current().OwnerId = Guid.Empty then Guid.Empty else Current().OwnerId)
            )

        let organizationId =
            new Option<OrganizationId>(
                OptionName.OrganizationId,
                Required = false,
                Description = "The organization ID <Guid>.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory =
                    (fun _ ->
                        if Current().OrganizationId = Guid.Empty then
                            Guid.Empty
                        else
                            Current().OrganizationId)
            )

        let repositoryId =
            new Option<RepositoryId>(
                OptionName.RepositoryId,
                [| "-r" |],
                Required = false,
                Description = "The repository ID <Guid>.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory =
                    (fun _ ->
                        if Current().RepositoryId = Guid.Empty then
                            Guid.Empty
                        else
                            Current().RepositoryId)
            )

        let branchId =
            new Option<BranchId>(
                OptionName.BranchId,
                Required = false,
                Description = "The branch ID <Guid>.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> if Current().BranchId = Guid.Empty then Guid.Empty else Current().BranchId)
            )

        let principalTypeRequired =
            (new Option<string>(
                OptionName.PrincipalType,
                Required = true,
                Description = "The principal type (User, Group, Service).",
                Arity = ArgumentArity.ExactlyOne
            ))
                .AcceptOnlyFromAmong(listCases<PrincipalType> ())

        let principalIdRequired =
            new Option<string>(
                OptionName.PrincipalId,
                Required = true,
                Description = "The principal identifier.",
                Arity = ArgumentArity.ExactlyOne
            )

        let principalTypeOptional =
            (new Option<string>(
                OptionName.PrincipalType,
                Required = false,
                Description = "Optional principal type filter (User, Group, Service).",
                Arity = ArgumentArity.ZeroOrOne
            ))
                .AcceptOnlyFromAmong(listCases<PrincipalType> ())

        let principalIdOptional =
            new Option<string>(
                OptionName.PrincipalId,
                Required = false,
                Description = "Optional principal identifier filter.",
                Arity = ArgumentArity.ZeroOrOne
            )

        let scopeKindRequired =
            (new Option<string>(
                OptionName.ScopeKind,
                Required = true,
                Description = "Scope kind (system, owner, org, repo, branch).",
                Arity = ArgumentArity.ExactlyOne
            ))
                .AcceptOnlyFromAmong([| "system"; "owner"; "org"; "organization"; "repo"; "repository"; "branch" |])

        let roleId =
            new Option<string>(OptionName.RoleId, Required = true, Description = "Role identifier.", Arity = ArgumentArity.ExactlyOne)

        let source =
            new Option<string>(
                OptionName.Source,
                Required = false,
                Description = "Optional role assignment source.",
                Arity = ArgumentArity.ZeroOrOne
            )

        let sourceDetail =
            new Option<string>(
                OptionName.SourceDetail,
                Required = false,
                Description = "Optional role assignment source detail.",
                Arity = ArgumentArity.ZeroOrOne
            )

        let pathRequired = new Option<string>(OptionName.Path, Required = true, Description = "Repository relative path.", Arity = ArgumentArity.ExactlyOne)

        let pathOptional =
            new Option<string>(
                OptionName.Path,
                Required = false,
                Description = "Optional repository relative path filter.",
                Arity = ArgumentArity.ZeroOrOne
            )

        let claim =
            new Option<string[]>(
                OptionName.Claim,
                Required = true,
                Description = "Claim to grant permissions for (repeatable).",
                Arity = ArgumentArity.OneOrMore
            )

        let directoryPermission =
            new Option<string[]>(
                OptionName.DirectoryPermission,
                Required = true,
                Description = "Directory permission to apply (repeatable; match --claim order).",
                Arity = ArgumentArity.OneOrMore
            )

        let operationRequired =
            (new Option<string>(
                OptionName.Operation,
                Required = true,
                Description = "Operation to check.",
                Arity = ArgumentArity.ExactlyOne
            ))
                .AcceptOnlyFromAmong(listCases<Operation> ())

        let resourceKindRequired =
            (new Option<string>(
                OptionName.ResourceKind,
                Required = true,
                Description = "Resource kind (system, owner, org, repo, branch, path).",
                Arity = ArgumentArity.ExactlyOne
            ))
                .AcceptOnlyFromAmong([| "system"; "owner"; "org"; "organization"; "repo"; "repository"; "branch"; "path" |])

    let private formatScope (scope: Scope) =
        match scope with
        | Scope.System -> "System"
        | Scope.Owner ownerId -> $"Owner:{ownerId}"
        | Scope.Organization(ownerId, organizationId) -> $"Org:{ownerId}/{organizationId}"
        | Scope.Repository(ownerId, organizationId, repositoryId) -> $"Repo:{ownerId}/{organizationId}/{repositoryId}"
        | Scope.Branch(ownerId, organizationId, repositoryId, branchId) -> $"Branch:{ownerId}/{organizationId}/{repositoryId}/{branchId}"

    let private formatClaimPermissions (permissions: IEnumerable<ClaimPermission>) =
        permissions
        |> Seq.map (fun permission -> $"{permission.Claim}:{permission.DirectoryPermission}")
        |> String.concat ", "

    let private renderAssignments (parseResult: ParseResult) (assignments: RoleAssignment list) =
        if parseResult |> hasOutput then
            if Seq.isEmpty assignments then
                logToAnsiConsole Colors.Highlighted "No role assignments found."
            else
                let table = Table(Border = TableBorder.DoubleEdge)

                table.AddColumns(
                    [| TableColumn($"[{Colors.Important}]Principal[/]")
                       TableColumn($"[{Colors.Important}]Scope[/]")
                       TableColumn($"[{Colors.Important}]Role[/]")
                       TableColumn($"[{Colors.Important}]Source[/]")
                       TableColumn($"[{Colors.Important}]Created[/]") |]
                )
                |> ignore

                for assignment in assignments do
                    let principalText = $"{assignment.Principal.PrincipalType}:{assignment.Principal.PrincipalId}"
                    let sourceDetail = assignment.SourceDetail |> Option.defaultValue ""
                    let sourceText =
                        if String.IsNullOrWhiteSpace sourceDetail then
                            assignment.Source
                        else
                            $"{assignment.Source} ({sourceDetail})"

                    table.AddRow(
                        $"[{Colors.Deemphasized}]{principalText}[/]",
                        formatScope assignment.Scope,
                        assignment.RoleId,
                        sourceText,
                        formatInstantExtended assignment.CreatedAt
                    )
                    |> ignore

                AnsiConsole.Write(table)

    let private renderRoles (parseResult: ParseResult) (roles: RoleDefinition list) =
        if parseResult |> hasOutput then
            if Seq.isEmpty roles then
                logToAnsiConsole Colors.Highlighted "No roles found."
            else
                let table = Table(Border = TableBorder.DoubleEdge)

                table.AddColumns(
                    [| TableColumn($"[{Colors.Important}]Role[/]")
                       TableColumn($"[{Colors.Important}]Applies To[/]")
                       TableColumn($"[{Colors.Important}]Operations[/]") |]
                )
                |> ignore

                for role in roles do
                    let appliesTo = role.AppliesTo |> Seq.sort |> String.concat ", "
                    let operations = role.AllowedOperations |> Seq.map string |> Seq.sort |> String.concat ", "

                    table.AddRow($"[{Colors.Deemphasized}]{role.RoleId}[/]", appliesTo, operations) |> ignore

                AnsiConsole.Write(table)

    let private renderPathPermissions (parseResult: ParseResult) (pathPermissions: PathPermission list) =
        if parseResult |> hasOutput then
            if Seq.isEmpty pathPermissions then
                logToAnsiConsole Colors.Highlighted "No path permissions found."
            else
                let table = Table(Border = TableBorder.DoubleEdge)

                table.AddColumns(
                    [| TableColumn($"[{Colors.Important}]Path[/]")
                       TableColumn($"[{Colors.Important}]Claims[/]") |]
                )
                |> ignore

                for pathPermission in pathPermissions do
                    let claims = formatClaimPermissions pathPermission.Permissions
                    table.AddRow($"[{Colors.Deemphasized}]{pathPermission.Path}[/]", claims) |> ignore

                AnsiConsole.Write(table)

    let private renderPermissionCheck (parseResult: ParseResult) (result: PermissionCheckResult) =
        if parseResult |> hasOutput then
            match result with
            | Allowed reason ->
                AnsiConsole.MarkupLine($"[{Colors.Highlighted}]Allowed[/]: {Markup.Escape(reason)}")
            | Denied reason ->
                AnsiConsole.MarkupLine($"[{Colors.Error}]Denied[/]: {Markup.Escape(reason)}")

    let private validateClaimPermissions (parseResult: ParseResult) =
        let correlationId = getCorrelationId parseResult
        let claims = parseResult.GetValue(Options.claim)
        let permissions = parseResult.GetValue(Options.directoryPermission)

        if isNull claims || claims.Length = 0 then
            Error(GraceError.Create "At least one --claim value is required." correlationId)
        elif isNull permissions || permissions.Length = 0 then
            Error(GraceError.Create "At least one --dir-perm value is required." correlationId)
        elif claims.Length <> permissions.Length then
            Error(GraceError.Create "--claim and --dir-perm counts must match." correlationId)
        else
            let invalid =
                permissions
                |> Array.tryFind (fun value -> discriminatedUnionFromString<DirectoryPermission> value |> Option.isNone)

            match invalid with
            | Some value -> Error(GraceError.Create $"Invalid DirectoryPermission '{value}'." correlationId)
            | None -> Ok parseResult

    let private validatePrincipalFilter (parseResult: ParseResult) (principalType: string option) (principalId: string option) =
        let correlationId = getCorrelationId parseResult
        let principalTypeValue = principalType |> Option.defaultValue String.Empty
        let principalIdValue = principalId |> Option.defaultValue String.Empty

        if String.IsNullOrWhiteSpace principalTypeValue && String.IsNullOrWhiteSpace principalIdValue then
            Ok parseResult
        elif String.IsNullOrWhiteSpace principalTypeValue || String.IsNullOrWhiteSpace principalIdValue then
            Error(GraceError.Create "PrincipalType and PrincipalId must be provided together." correlationId)
        else
            Ok parseResult

    let private validatePathResource (parseResult: ParseResult) (resourceKind: string) (pathValue: string) =
        if resourceKind.Equals("path", StringComparison.InvariantCultureIgnoreCase) && String.IsNullOrWhiteSpace pathValue then
            Error(GraceError.Create "Path is required for Path resources." (getCorrelationId parseResult))
        else
            Ok parseResult

    type GrantRole() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult

                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> CommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let parameters =
                            GrantRoleParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OrganizationId = graceIds.OrganizationIdString,
                                RepositoryId = graceIds.RepositoryIdString,
                                BranchId = graceIds.BranchIdString,
                                PrincipalType = parseResult.GetValue(Options.principalTypeRequired),
                                PrincipalId = parseResult.GetValue(Options.principalIdRequired),
                                ScopeKind = parseResult.GetValue(Options.scopeKindRequired),
                                RoleId = parseResult.GetValue(Options.roleId),
                                Source = parseResult.GetValue(Options.source),
                                SourceDetail = parseResult.GetValue(Options.sourceDetail),
                                CorrelationId = getCorrelationId parseResult
                            )

                        let! result =
                            if parseResult |> hasOutput then
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                            let! response = Access.GrantRole(parameters)
                                            t0.Increment(100.0)
                                            return response
                                        })
                            else
                                Access.GrantRole(parameters)

                        match result with
                        | Ok graceReturnValue ->
                            renderAssignments parseResult graceReturnValue.ReturnValue
                            return result |> renderOutput parseResult
                        | Error error ->
                            logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                            return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult
                with ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    type RevokeRole() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult

                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> CommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let parameters =
                            RevokeRoleParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OrganizationId = graceIds.OrganizationIdString,
                                RepositoryId = graceIds.RepositoryIdString,
                                BranchId = graceIds.BranchIdString,
                                PrincipalType = parseResult.GetValue(Options.principalTypeRequired),
                                PrincipalId = parseResult.GetValue(Options.principalIdRequired),
                                ScopeKind = parseResult.GetValue(Options.scopeKindRequired),
                                RoleId = parseResult.GetValue(Options.roleId),
                                CorrelationId = getCorrelationId parseResult
                            )

                        let! result =
                            if parseResult |> hasOutput then
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                            let! response = Access.RevokeRole(parameters)
                                            t0.Increment(100.0)
                                            return response
                                        })
                            else
                                Access.RevokeRole(parameters)

                        match result with
                        | Ok graceReturnValue ->
                            renderAssignments parseResult graceReturnValue.ReturnValue
                            return result |> renderOutput parseResult
                        | Error error ->
                            logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                            return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult
                with ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    type ListRoleAssignments() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult

                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let principalType = parseResult.GetValue(Options.principalTypeOptional) |> Option.ofObj
                    let principalId = parseResult.GetValue(Options.principalIdOptional) |> Option.ofObj

                    let validateIncomingParameters =
                        parseResult
                        |> CommonValidations
                        >>= (fun _ -> validatePrincipalFilter parseResult principalType principalId)

                    match validateIncomingParameters with
                    | Ok _ ->
                        let parameters =
                            ListRoleAssignmentsParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OrganizationId = graceIds.OrganizationIdString,
                                RepositoryId = graceIds.RepositoryIdString,
                                BranchId = graceIds.BranchIdString,
                                PrincipalType = (principalType |> Option.defaultValue ""),
                                PrincipalId = (principalId |> Option.defaultValue ""),
                                ScopeKind = parseResult.GetValue(Options.scopeKindRequired),
                                CorrelationId = getCorrelationId parseResult
                            )

                        let! result =
                            if parseResult |> hasOutput then
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                            let! response = Access.ListRoleAssignments(parameters)
                                            t0.Increment(100.0)
                                            return response
                                        })
                            else
                                Access.ListRoleAssignments(parameters)

                        match result with
                        | Ok graceReturnValue ->
                            renderAssignments parseResult graceReturnValue.ReturnValue
                            return result |> renderOutput parseResult
                        | Error error ->
                            logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                            return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult
                with ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    type UpsertPathPermission() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult

                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters =
                        parseResult
                        |> CommonValidations
                        >>= validateClaimPermissions

                    match validateIncomingParameters with
                    | Ok _ ->
                        let claims = parseResult.GetValue(Options.claim)
                        let permissions = parseResult.GetValue(Options.directoryPermission)
                        let claimPermissions = List<ClaimPermissionParameters>()

                        for index in 0 .. claims.Length - 1 do
                            claimPermissions.Add(ClaimPermissionParameters(Claim = claims[index], DirectoryPermission = permissions[index]))

                        let parameters =
                            UpsertPathPermissionParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OrganizationId = graceIds.OrganizationIdString,
                                RepositoryId = graceIds.RepositoryIdString,
                                Path = parseResult.GetValue(Options.pathRequired),
                                ClaimPermissions = claimPermissions,
                                CorrelationId = getCorrelationId parseResult
                            )

                        let! result =
                            if parseResult |> hasOutput then
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                            let! response = Access.UpsertPathPermission(parameters)
                                            t0.Increment(100.0)
                                            return response
                                        })
                            else
                                Access.UpsertPathPermission(parameters)

                        match result with
                        | Ok graceReturnValue ->
                            renderPathPermissions parseResult graceReturnValue.ReturnValue
                            return result |> renderOutput parseResult
                        | Error error ->
                            logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                            return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult
                with ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    type RemovePathPermission() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult

                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> CommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let parameters =
                            RemovePathPermissionParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OrganizationId = graceIds.OrganizationIdString,
                                RepositoryId = graceIds.RepositoryIdString,
                                Path = parseResult.GetValue(Options.pathRequired),
                                CorrelationId = getCorrelationId parseResult
                            )

                        let! result =
                            if parseResult |> hasOutput then
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                            let! response = Access.RemovePathPermission(parameters)
                                            t0.Increment(100.0)
                                            return response
                                        })
                            else
                                Access.RemovePathPermission(parameters)

                        match result with
                        | Ok graceReturnValue ->
                            renderPathPermissions parseResult graceReturnValue.ReturnValue
                            return result |> renderOutput parseResult
                        | Error error ->
                            logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                            return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult
                with ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    type ListPathPermissions() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult

                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> CommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let parameters =
                            ListPathPermissionsParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OrganizationId = graceIds.OrganizationIdString,
                                RepositoryId = graceIds.RepositoryIdString,
                                Path = (parseResult.GetValue(Options.pathOptional) |> Option.ofObj |> Option.defaultValue ""),
                                CorrelationId = getCorrelationId parseResult
                            )

                        let! result =
                            if parseResult |> hasOutput then
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                            let! response = Access.ListPathPermissions(parameters)
                                            t0.Increment(100.0)
                                            return response
                                        })
                            else
                                Access.ListPathPermissions(parameters)

                        match result with
                        | Ok graceReturnValue ->
                            renderPathPermissions parseResult graceReturnValue.ReturnValue
                            return result |> renderOutput parseResult
                        | Error error ->
                            logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                            return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult
                with ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    type CheckPermission() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult

                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let resourceKind = parseResult.GetValue(Options.resourceKindRequired)
                    let pathValue = parseResult.GetValue(Options.pathOptional) |> Option.ofObj |> Option.defaultValue ""
                    let principalType = parseResult.GetValue(Options.principalTypeOptional) |> Option.ofObj
                    let principalId = parseResult.GetValue(Options.principalIdOptional) |> Option.ofObj

                    let validateIncomingParameters =
                        parseResult
                        |> CommonValidations
                        >>= (fun _ -> validatePrincipalFilter parseResult principalType principalId)
                        >>= (fun _ -> validatePathResource parseResult resourceKind pathValue)

                    match validateIncomingParameters with
                    | Ok _ ->
                        let parameters =
                            CheckPermissionParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OrganizationId = graceIds.OrganizationIdString,
                                RepositoryId = graceIds.RepositoryIdString,
                                BranchId = graceIds.BranchIdString,
                                Operation = parseResult.GetValue(Options.operationRequired),
                                ResourceKind = resourceKind,
                                Path = pathValue,
                                PrincipalType = (principalType |> Option.defaultValue ""),
                                PrincipalId = (principalId |> Option.defaultValue ""),
                                CorrelationId = getCorrelationId parseResult
                            )

                        let! result =
                            if parseResult |> hasOutput then
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Checking permission.[/]")
                                            let! response = Access.CheckPermission(parameters)
                                            t0.Increment(100.0)
                                            return response
                                        })
                            else
                                Access.CheckPermission(parameters)

                        match result with
                        | Ok graceReturnValue ->
                            renderPermissionCheck parseResult graceReturnValue.ReturnValue
                            return result |> renderOutput parseResult
                        | Error error ->
                            logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                            return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult
                with ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    type ListRoles() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult

                    let parameters = CommonParameters(CorrelationId = getCorrelationId parseResult)

                    let! result =
                        if parseResult |> hasOutput then
                            progress
                                .Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                    task {
                                        let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                        let! response = Access.ListRoles(parameters)
                                        t0.Increment(100.0)
                                        return response
                                    })
                        else
                            Access.ListRoles(parameters)

                    match result with
                    | Ok graceReturnValue ->
                        renderRoles parseResult graceReturnValue.ReturnValue
                        return result |> renderOutput parseResult
                    | Error error ->
                        logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                        return result |> renderOutput parseResult
                with ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    let Build =
        let addScopeOptions (command: Command) =
            command
            |> addOption Options.ownerId
            |> addOption Options.organizationId
            |> addOption Options.repositoryId
            |> addOption Options.branchId

        let accessCommand = new Command("access", Description = "Manages access control and permissions.")

        let grantRoleCommand =
            new Command("grant-role", Description = "Grants a role to a principal at a scope.")
            |> addScopeOptions
            |> addOption Options.scopeKindRequired
            |> addOption Options.principalTypeRequired
            |> addOption Options.principalIdRequired
            |> addOption Options.roleId
            |> addOption Options.source
            |> addOption Options.sourceDetail

        grantRoleCommand.Action <- new GrantRole()
        accessCommand.Subcommands.Add(grantRoleCommand)

        let revokeRoleCommand =
            new Command("revoke-role", Description = "Revokes a role from a principal at a scope.")
            |> addScopeOptions
            |> addOption Options.scopeKindRequired
            |> addOption Options.principalTypeRequired
            |> addOption Options.principalIdRequired
            |> addOption Options.roleId

        revokeRoleCommand.Action <- new RevokeRole()
        accessCommand.Subcommands.Add(revokeRoleCommand)

        let listRoleAssignmentsCommand =
            new Command("list-role-assignments", Description = "Lists role assignments at a scope.")
            |> addScopeOptions
            |> addOption Options.scopeKindRequired
            |> addOption Options.principalTypeOptional
            |> addOption Options.principalIdOptional

        listRoleAssignmentsCommand.Action <- new ListRoleAssignments()
        accessCommand.Subcommands.Add(listRoleAssignmentsCommand)

        let upsertPathPermissionCommand =
            new Command("upsert-path-permission", Description = "Upserts repository path permissions.")
            |> addScopeOptions
            |> addOption Options.pathRequired
            |> addOption Options.claim
            |> addOption Options.directoryPermission

        upsertPathPermissionCommand.Action <- new UpsertPathPermission()
        accessCommand.Subcommands.Add(upsertPathPermissionCommand)

        let removePathPermissionCommand =
            new Command("remove-path-permission", Description = "Removes repository path permissions.")
            |> addScopeOptions
            |> addOption Options.pathRequired

        removePathPermissionCommand.Action <- new RemovePathPermission()
        accessCommand.Subcommands.Add(removePathPermissionCommand)

        let listPathPermissionsCommand =
            new Command("list-path-permissions", Description = "Lists repository path permissions.")
            |> addScopeOptions
            |> addOption Options.pathOptional

        listPathPermissionsCommand.Action <- new ListPathPermissions()
        accessCommand.Subcommands.Add(listPathPermissionsCommand)

        let checkPermissionCommand =
            new Command("check", Description = "Checks a permission for the current or specified principal.")
            |> addScopeOptions
            |> addOption Options.operationRequired
            |> addOption Options.resourceKindRequired
            |> addOption Options.pathOptional
            |> addOption Options.principalTypeOptional
            |> addOption Options.principalIdOptional

        checkPermissionCommand.Action <- new CheckPermission()
        accessCommand.Subcommands.Add(checkPermissionCommand)

        let listRolesCommand = new Command("list-roles", Description = "Lists available roles.")

        listRolesCommand.Action <- new ListRoles()
        accessCommand.Subcommands.Add(listRolesCommand)

        accessCommand
