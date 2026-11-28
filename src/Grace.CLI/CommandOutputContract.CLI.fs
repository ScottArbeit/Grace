namespace Grace.CLI

open System
open System.Collections
open System.Collections.Generic
open System.CommandLine
open System.Reflection
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.Json.Schema
open System.Text.Json.Serialization.Metadata
open Microsoft.FSharp.Reflection

/// Groups the command output contract command parser, handlers, and output helpers.
module CommandOutputContract =

    /// Models command identity values passed between the parser and command output contract handlers.
    type CommandIdentity =
        {
            GroupPath: string list
            CommandName: string
        }

        /// Gets the full command path including its group segments and command name.
        member this.CommandPath = this.GroupPath @ [ this.CommandName ]
        /// Gets the stable dotted identifier for this command.
        member this.CommandId = String.Join(".", this.CommandPath)
        /// Runs the asynchronous command identity action when System.CommandLine dispatches the parsed command.
        override this.ToString() = String.Join(" ", this.CommandPath)

    /// Describes how much of the Grace JSON contract a live leaf command honors under `--output Json`.
    type JsonOutputSupport =
        /// Emits `GraceReturnValue<T>` on success and `GraceError` on failure.
        | Supported
        /// Emits `GraceReturnValue<T>` only on the path the condition names; other paths return `GraceError`.
        | Conditional of condition: string
        /// Success output is not yet a stable `GraceReturnValue<T>`, so `--schema` cannot describe it and `--select` is rejected.
        | MigrationRequired

    /// Reports whether `--schema` and `--examples` can describe a command's `ReturnValue`.
    type ReturnValueMetadataStatus =
        | SchemaReady
        | MetadataIncomplete

    /// Describes the `ReturnValue` payload that `--schema` and `--examples` publish for one command.
    type ReturnValueContract = { Name: string; Provenance: string; Status: ReturnValueMetadataStatus; Schema: obj; Example: obj; Notes: string list }

    /// Holds the facts `--schema`, `--examples`, and `--select` rely on for one live leaf command.
    type CommandContractEntry =
        {
            Identity: CommandIdentity
            JsonOutput: JsonOutputSupport
            ReturnValueContract: ReturnValueContract
        }

        /// Gets whether `--select` may project this command's `ReturnValue`; only stable success envelopes qualify.
        member this.SupportsSelect =
            match this.JsonOutput with
            | Supported
            | Conditional _ -> true
            | MigrationRequired -> false

    /// Models introspection kind values passed between the parser and command output contract handlers.
    type IntrospectionKind =
        | Schema
        | Examples

    /// Defines structured data exchanged by CLI helpers.
    type CommandIdentityDocument = { Id: string; Path: string list; GroupPath: string list; Name: string }

    /// Tells callers whether a command emits the Grace JSON envelope and whether it accepts `--select`.
    type CommandSupportDocument = { JsonOutput: string; Select: bool }

    /// Models command schema document values passed between the parser and command output contract handlers.
    type CommandSchemaDocument =
        {
            Status: string
            Source: string
            Envelope: string
            ReturnValueContract: string
            SuccessSchema: obj
            ErrorSchema: obj
            Notes: string list
        }

    /// Defines structured data exchanged by CLI helpers.
    type CommandExampleDocument = { Name: string; Description: string; Document: obj }

    /// Describes one command-line input accepted by a command-specific machine-readable contract.
    type CommandInputOptionDocument = { Name: string; ValueKind: string; Description: string }

    /// Describes a command-specific input-selection rule for callers that cannot infer it from an output envelope.
    type CommandInputDocument = { Selection: string; Description: string; Options: CommandInputOptionDocument list }

    /// Models command introspection document values passed between the parser and command output contract handlers.
    type CommandIntrospectionDocument =
        {
            Kind: string
            ContractVersion: string
            Command: CommandIdentityDocument
            Support: CommandSupportDocument
            Input: CommandInputDocument option
            Schema: CommandSchemaDocument option
            Examples: CommandExampleDocument list
        }

    /// Constructs a draft 2020-12 object schema with the supplied title, properties, and required field list.
    let private schemaObject (title: string) (properties: (string * obj) list) (required: string array) =
        let schema = Dictionary<string, obj>(StringComparer.Ordinal)
        schema["$schema"] <- "https://json-schema.org/draft/2020-12/schema"
        schema["title"] <- title
        schema["type"] <- "object"
        schema["required"] <- required
        schema["properties"] <- Dictionary<string, obj>(properties |> Seq.map KeyValuePair)
        box schema

    /// Constructs a JSON schema for scalar command-output fields such as strings, booleans, or numbers.
    let private scalarSchema (typeName: string) =
        let schema = Dictionary<string, obj>(StringComparer.Ordinal)
        schema["type"] <- typeName
        box schema

    /// Builds command-output contract metadata for any schema so automation can rely on stable JSON shapes.
    let private anySchema description =
        let schema = Dictionary<string, obj>(StringComparer.Ordinal)
        schema["description"] <- description
        box schema

    /// Constructs the nullable nullable object schema used in generated command-output metadata.
    let private nullableObjectSchema description =
        let schema = Dictionary<string, obj>(StringComparer.Ordinal)
        schema["type"] <- [| "object"; "null" |]
        schema["description"] <- description
        box schema

    let private propertyBagSchema =
        let propertyEntry =
            schemaObject
                "Grace CLI property entry"
                [
                    "Key", scalarSchema "string"
                    "Value", anySchema "Safe machine-readable metadata value."
                ]
                [| "Key"; "Value" |]

        let schema = Dictionary<string, obj>(StringComparer.Ordinal)
        schema["type"] <- "array"
        schema["items"] <- propertyEntry
        schema["description"] <- "CLI stdout representation of Grace Properties metadata."
        box schema

    /// Builds command-output contract metadata for cli properties so automation can rely on stable JSON shapes.
    let private cliProperties commandId provenance =
        [|
            {| Key = "cli.contractVersion"; Value = "cli-json-v1" |}
            {| Key = "cli.commandId"; Value = commandId |}
            {| Key = "cli.introspectionSource"; Value = provenance |}
        |]

    /// Describes the placeholder `ReturnValue` published for a command whose success output still needs migration.
    let private incompleteReturnValueSchema reason =
        schemaObject
            "Incomplete command output contract"
            [
                "Status", scalarSchema "string"
                "Reason", scalarSchema "string"
            ]
            [| "Status"; "Reason" |]

    /// Builds the placeholder example that explains why a migration-required command has no `ReturnValue` example.
    let private incompleteReturnValueExample reason = box {| Status = "metadata-incomplete"; Reason = reason |}

    let private reflectionFlags = BindingFlags.Public ||| BindingFlags.NonPublic

    let private schemaSerializerOptions =
        let options = JsonSerializerOptions(Grace.Shared.Constants.JsonSerializerOptions)

        if isNull options.TypeInfoResolver then
            options.TypeInfoResolver <- DefaultJsonTypeInfoResolver()

        options

    let private schemaExporterOptions = JsonSchemaExporterOptions(TreatNullObliviousAsNonNullable = true)

    /// Identifies a closed generic type without relying on its display name.
    let private isGenericTypeOf genericTypeDefinition (candidate: Type) =
        candidate.IsGenericType
        && candidate.GetGenericTypeDefinition() = genericTypeDefinition

    /// Finds the JSON array item type for Grace result collections.
    let private tryCollectionElementType (candidate: Type) =
        if candidate = typeof<string> then
            None
        elif candidate.IsArray then
            Some(candidate.GetElementType())
        elif isGenericTypeOf typedefof<list<_>> candidate then
            Some(candidate.GetGenericArguments()[0])
        else
            candidate.GetInterfaces()
            |> Array.append [| candidate |]
            |> Array.tryPick (fun implemented ->
                if implemented.IsGenericType
                   && implemented.GetGenericTypeDefinition() = typedefof<IEnumerable<_>> then
                    Some(implemented.GetGenericArguments()[0])
                else
                    None)

    /// Finds string-keyed dictionary value types whose JSON shape is an object property bag.
    let private tryStringDictionaryValueType (candidate: Type) =
        candidate.GetInterfaces()
        |> Array.append [| candidate |]
        |> Array.tryPick (fun implemented ->
            if implemented.IsGenericType then
                let definition = implemented.GetGenericTypeDefinition()
                let arguments = implemented.GetGenericArguments()

                if (definition = typedefof<IDictionary<_, _>>
                    || definition = typedefof<IReadOnlyDictionary<_, _>>)
                   && arguments[0] = typeof<string> then
                    Some arguments[1]
                else
                    None
            else
                None)

    /// Applies Grace's configured union-tag naming policy to a discriminated-union case.
    let private unionCaseName (caseInfo: UnionCaseInfo) =
        Grace.Shared.Constants.JsonSerializerOptions.Converters
        |> Seq.tryPick (fun converter ->
            match converter with
            | :? System.Text.Json.Serialization.JsonFSharpConverter as fsharpConverter ->
                let namingPolicy = fsharpConverter.Options.UnionTagNamingPolicy

                if isNull namingPolicy then
                    Some caseInfo.Name
                else
                    Some(namingPolicy.ConvertName(caseInfo.Name))
            | _ -> None)
        |> Option.defaultValue caseInfo.Name

    /// Derives JSON Schema recursively from a declared result type and Grace's serializer configuration.
    let rec private schemaForType (visiting: Type list) (resultType: Type) : obj =
        let nestedSchema nextType = schemaForType (resultType :: visiting) nextType

        if visiting |> List.exists ((=) resultType) then
            box true
        elif resultType = typeof<unit> then
            scalarSchema "null"
        elif isGenericTypeOf typedefof<option<_>> resultType then
            let innerSchema = nestedSchema (resultType.GetGenericArguments()[0])
            let schema = Dictionary<string, obj>(StringComparer.Ordinal)
            schema["anyOf"] <- [| innerSchema; scalarSchema "null" |]
            box schema
        elif isGenericTypeOf typedefof<list<_>> resultType then
            let schema = Dictionary<string, obj>(StringComparer.Ordinal)
            schema["type"] <- "array"
            schema["items"] <- nestedSchema (resultType.GetGenericArguments()[0])
            box schema
        elif FSharpType.IsTuple resultType then
            let itemSchemas =
                FSharpType.GetTupleElements resultType
                |> Array.map nestedSchema

            let schema = Dictionary<string, obj>(StringComparer.Ordinal)
            schema["type"] <- "array"
            schema["prefixItems"] <- itemSchemas
            schema["minItems"] <- itemSchemas.Length
            schema["maxItems"] <- itemSchemas.Length
            box schema
        elif FSharpType.IsRecord(resultType, reflectionFlags) then
            let fields = FSharpType.GetRecordFields(resultType, reflectionFlags)

            let properties =
                fields
                |> Array.map (fun field -> KeyValuePair(field.Name, nestedSchema field.PropertyType))

            let required =
                fields
                |> Array.filter (fun field -> not (isGenericTypeOf typedefof<option<_>> field.PropertyType))
                |> Array.map (fun field -> field.Name)

            let schema = Dictionary<string, obj>(StringComparer.Ordinal)
            schema["title"] <- resultType.Name
            schema["type"] <- "object"
            schema["properties"] <- Dictionary<string, obj>(properties)
            schema["required"] <- required
            box schema
        elif FSharpType.IsUnion(resultType, reflectionFlags) then
            let cases = FSharpType.GetUnionCases(resultType, reflectionFlags)
            let onlyCaseFields = if cases.Length = 1 then cases[ 0 ].GetFields() else [||]

            if cases.Length = 1 && onlyCaseFields.Length = 1 then
                nestedSchema onlyCaseFields[0].PropertyType
            elif cases
                 |> Array.forall (fun caseInfo -> caseInfo.GetFields().Length = 0) then
                let schema = Dictionary<string, obj>(StringComparer.Ordinal)
                schema["type"] <- "string"
                schema["enum"] <- cases |> Array.map unionCaseName
                box schema
            else
                let caseSchemas =
                    cases
                    |> Array.map (fun caseInfo ->
                        let fields = caseInfo.GetFields()

                        if fields.Length = 0 then
                            let schema = Dictionary<string, obj>(StringComparer.Ordinal)
                            schema["const"] <- unionCaseName caseInfo
                            box schema
                        else
                            let caseValueSchema =
                                if fields.Length = 1 then
                                    nestedSchema fields[0].PropertyType
                                else
                                    let properties =
                                        fields
                                        |> Array.map (fun field -> KeyValuePair(field.Name, nestedSchema field.PropertyType))

                                    let schema = Dictionary<string, obj>(StringComparer.Ordinal)
                                    schema["type"] <- "object"
                                    schema["properties"] <- Dictionary<string, obj>(properties)
                                    schema["required"] <- fields |> Array.map (fun field -> field.Name)
                                    box schema

                            let schema = Dictionary<string, obj>(StringComparer.Ordinal)
                            schema["type"] <- "object"

                            schema["properties"] <- Dictionary<string, obj>(
                                [
                                    KeyValuePair(unionCaseName caseInfo, caseValueSchema)
                                ]
                            )

                            schema["required"] <- [| unionCaseName caseInfo |]
                            box schema)

                let schema = Dictionary<string, obj>(StringComparer.Ordinal)
                schema["anyOf"] <- caseSchemas
                box schema
        else
            match tryStringDictionaryValueType resultType, tryCollectionElementType resultType with
            | Some valueType, _ ->
                let schema = Dictionary<string, obj>(StringComparer.Ordinal)
                schema["type"] <- "object"
                schema["additionalProperties"] <- nestedSchema valueType
                box schema
            | None, Some elementType ->
                let schema = Dictionary<string, obj>(StringComparer.Ordinal)
                schema["type"] <- "array"
                schema["items"] <- nestedSchema elementType
                box schema
            | None, None ->
                try
                    box (JsonSchemaExporter.GetJsonSchemaAsNode(schemaSerializerOptions, resultType, schemaExporterOptions))
                with
                | _ -> box true

    /// Constructs a deterministic value that the unchanged Grace serializer can turn into an example document.
    let rec private representativeValue (visiting: Type list) depth (resultType: Type) : obj =
        let nestedValue nextType = representativeValue (resultType :: visiting) (depth + 1) nextType

        if depth >= 16
           || visiting |> List.exists ((=) resultType) then
            null
        elif resultType = typeof<string> then
            box "example"
        elif resultType = typeof<Guid> then
            box (Guid.Parse("11111111-1111-1111-1111-111111111111"))
        elif resultType = typeof<Uri> then
            box (Uri("https://example.invalid/grace"))
        elif resultType = typeof<DateTime> then
            box (DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc))
        elif resultType = typeof<DateTimeOffset> then
            box (DateTimeOffset(2026, 6, 5, 0, 0, 0, TimeSpan.Zero))
        elif resultType = typeof<unit> then
            box ()
        elif isGenericTypeOf typedefof<option<_>> resultType then
            null
        elif resultType.IsArray then
            box (Array.CreateInstance(resultType.GetElementType(), 0))
        elif isGenericTypeOf typedefof<list<_>> resultType then
            let emptyCase =
                FSharpType.GetUnionCases(resultType, reflectionFlags)
                |> Array.find (fun caseInfo -> caseInfo.GetFields().Length = 0)

            FSharpValue.MakeUnion(emptyCase, [||], reflectionFlags)
        elif FSharpType.IsTuple resultType then
            let values =
                FSharpType.GetTupleElements resultType
                |> Array.map nestedValue

            FSharpValue.MakeTuple(values, resultType)
        elif FSharpType.IsRecord(resultType, reflectionFlags) then
            let values =
                FSharpType.GetRecordFields(resultType, reflectionFlags)
                |> Array.map (fun field -> nestedValue field.PropertyType)

            FSharpValue.MakeRecord(resultType, values, reflectionFlags)
        elif FSharpType.IsUnion(resultType, reflectionFlags) then
            let caseInfo = FSharpType.GetUnionCases(resultType, reflectionFlags)[0]

            let values =
                caseInfo.GetFields()
                |> Array.map (fun field -> nestedValue field.PropertyType)

            FSharpValue.MakeUnion(caseInfo, values, reflectionFlags)
        elif resultType.IsEnum then
            Enum.GetValues(resultType).GetValue(0)
        elif resultType = typeof<obj> then
            box (Dictionary<string, obj>())
        else
            match tryStringDictionaryValueType resultType, tryCollectionElementType resultType with
            | Some valueType, _ ->
                let dictionaryType = typedefof<Dictionary<_, _>>.MakeGenericType (typeof<string>, valueType)
                Activator.CreateInstance(dictionaryType)
            | None, Some elementType ->
                if resultType.IsInterface then
                    box (Array.CreateInstance(elementType, 0))
                else
                    try
                        Activator.CreateInstance(resultType)
                    with
                    | _ -> box (Array.CreateInstance(elementType, 0))
            | None, None ->
                try
                    Activator.CreateInstance(resultType)
                with
                | _ -> null

    /// Serializes a deterministic representative through the same options used by runtime JSON output.
    let private representativeExample resultType =
        try
            let value = representativeValue [] 0 resultType
            let json = JsonSerializer.Serialize(value, resultType, Grace.Shared.Constants.JsonSerializerOptions)
            box (JsonNode.Parse(json))
        with
        | _ -> null

    /// Formats a declared CLR/F# type as concise CLI contract metadata.
    let rec private contractTypeName (resultType: Type) =
        if resultType = typeof<unit> then
            "unit"
        elif resultType = typeof<string> then
            "string"
        elif resultType = typeof<bool> then
            "bool"
        elif resultType = typeof<int> then
            "int"
        elif resultType = typeof<int64> then
            "int64"
        elif resultType.IsArray then
            $"{contractTypeName (resultType.GetElementType())} array"
        elif FSharpType.IsTuple resultType then
            FSharpType.GetTupleElements resultType
            |> Array.map contractTypeName
            |> String.concat " * "
        elif isGenericTypeOf typedefof<option<_>> resultType then
            $"{contractTypeName (resultType.GetGenericArguments()[0])} option"
        elif isGenericTypeOf typedefof<list<_>> resultType then
            $"{contractTypeName (resultType.GetGenericArguments()[0])} list"
        elif resultType.IsGenericType then
            let genericName = resultType.Name.Split('`')[0]

            resultType.GetGenericArguments()
            |> Array.map contractTypeName
            |> String.concat ", "
            |> fun arguments -> $"{genericName}<{arguments}>"
        else
            resultType.Name

    /// Resolves private CLI-local result records without making their implementation types public.
    let private cliLocalResultType fullName =
        match Assembly
                  .GetExecutingAssembly()
                  .GetType(fullName, false)
            with
        | null -> invalidOp $"CLI output result type '{fullName}' was not found."
        | resultType -> resultType

    /// Builds command-output contract metadata for supported return value contract so automation can rely on stable JSON shapes.
    let private supportedReturnValueContract name provenance schema example notes =
        { Name = name; Provenance = provenance; Status = SchemaReady; Schema = schema; Example = example; Notes = notes }

    /// Builds the explicit metadata gap that `--schema` reports for a command whose success output still needs migration.
    let private incompleteReturnValueContract name reason =
        {
            Name = name
            Provenance = "CommandOutputContract"
            Status = MetadataIncomplete
            Schema = incompleteReturnValueSchema reason
            Example = incompleteReturnValueExample reason
            Notes = [ reason ]
        }

    /// Declares the exact `ReturnValue` type each envelope-emitting command produces, keyed by dotted command ID.
    /// `--schema` and `--examples` derive their documents from these types. The registry tests require every live leaf
    /// command to appear exactly once across this list, `migrationRequiredCommandIds`, and the owner observation entry.
    let private declaredResultTypes: (string * Type) list =
        [
            "authorize.can", typeof<Grace.Types.Authorization.PermissionCheckResult>
            "authorize.check", typeof<Grace.Types.Authorization.PermissionCheckResult>
            "authorize.grant-role", typeof<Grace.Types.Authorization.RoleAssignment list>
            "authorize.list-role-assignments", typeof<Grace.Types.Authorization.RoleAssignment list>
            "authorize.revoke-role", typeof<Grace.Types.Authorization.RoleAssignment list>
            "authorize.show", typeof<Grace.Types.Authorization.RoleAssignment list>
            "authorize.list-path-permissions", typeof<Grace.Types.Common.PathPermission list>
            "authorize.remove-path-permission", typeof<Grace.Types.Common.PathPermission list>
            "authorize.upsert-path-permission", typeof<Grace.Types.Common.PathPermission list>
            "authorize.list-roles", typeof<Grace.Types.Authorization.RoleDefinition list>
            "admin.reminder.list", typeof<IEnumerable<Grace.Types.Reminder.ReminderDto>>
            "admin.reminder.get", typeof<Grace.Types.Reminder.ReminderDto>
            "admin.reminder.create", typeof<string>
            "admin.reminder.delete", typeof<string>
            "admin.reminder.reschedule", typeof<string>
            "admin.reminder.update-time", typeof<string>
            "agent.add-summary", cliLocalResultType "Grace.CLI.Command.AgentCommand+AddSummaryResult"
            "agent.bootstrap", typeof<Grace.Types.Automation.AgentSessionOperationResult>
            "agent.work.start", typeof<Grace.Types.Automation.AgentSessionOperationResult>
            "agent.work.status", typeof<Grace.Types.Automation.AgentSessionOperationResult>
            "agent.work.stop", typeof<Grace.Types.Automation.AgentSessionOperationResult>
            "alias.list", typeof<Grace.CLI.Common.LocalOutputDto.AliasListDto>
            "approval.policy.create", typeof<Grace.Types.Webhooks.ApprovalPolicy>
            "approval.policy.delete", typeof<Grace.Types.Webhooks.ApprovalPolicy>
            "approval.policy.disable", typeof<Grace.Types.Webhooks.ApprovalPolicy>
            "approval.policy.enable", typeof<Grace.Types.Webhooks.ApprovalPolicy>
            "approval.policy.show", typeof<Grace.Types.Webhooks.ApprovalPolicy>
            "approval.policy.update", typeof<Grace.Types.Webhooks.ApprovalPolicy>
            "approval.policy.evaluate", typeof<IReadOnlyList<Grace.Types.Webhooks.ApprovalPolicy>>
            "approval.policy.list", typeof<IReadOnlyList<Grace.Types.Webhooks.ApprovalPolicy>>
            "approval.request.approve", typeof<Grace.Types.Webhooks.ApprovalRequest>
            "approval.request.reject", typeof<Grace.Types.Webhooks.ApprovalRequest>
            "approval.request.show", typeof<Grace.Types.Webhooks.ApprovalRequest>
            "approval.request.wait", typeof<Grace.Types.Webhooks.ApprovalRequest>
            "approval.request.history", typeof<IReadOnlyList<Grace.Types.Webhooks.ApprovalRequest>>
            "approval.request.list", typeof<IReadOnlyList<Grace.Types.Webhooks.ApprovalRequest>>
            "authenticate.login", typeof<string>
            "authenticate.logout", typeof<string>
            "authenticate.token.clear", typeof<string>
            "authenticate.token.set", typeof<string>
            "authenticate.token.status", typeof<string>
            "authenticate.status", typeof<Grace.CLI.Command.Auth.AuthStatusOutput>
            "authenticate.token.create", typeof<Grace.Types.PersonalAccessToken.PersonalAccessTokenCreated>
            "authenticate.token.list", typeof<Grace.Types.PersonalAccessToken.PersonalAccessTokenSummary list>
            "authenticate.token.revoke", typeof<Grace.Types.PersonalAccessToken.PersonalAccessTokenSummary>
            "authenticate.whoami", typeof<Grace.CLI.Command.Auth.AuthInfo>
            "branch.annotate", typeof<Grace.Types.Annotation.BranchAnnotationDto>
            "branch.assign", typeof<string>
            "branch.checkpoint", typeof<string>
            "branch.commit", typeof<string>
            "branch.create", typeof<string>
            "branch.create-external", typeof<string>
            "branch.delete", typeof<string>
            "branch.enable-assign", typeof<string>
            "branch.enable-auto-rebase", typeof<string>
            "branch.enable-checkpoints", typeof<string>
            "branch.enable-commit", typeof<string>
            "branch.enable-external", typeof<string>
            "branch.enable-promotion", typeof<string>
            "branch.enable-save", typeof<string>
            "branch.enable-tag", typeof<string>
            "branch.promote", typeof<string>
            "branch.save", typeof<string>
            "branch.set-name", typeof<string>
            "branch.set-promotion-mode", typeof<string>
            "branch.tag", typeof<string>
            "branch.update-parent-branch", typeof<string>
            "branch.get", typeof<Grace.Types.Branch.BranchDto>
            "branch.get-checkpoints", typeof<Grace.Types.Branch.BranchDto * Grace.Types.Reference.ReferenceDto array>
            "branch.get-commits", typeof<Grace.Types.Branch.BranchDto * Grace.Types.Reference.ReferenceDto array>
            "branch.get-externals", typeof<Grace.Types.Branch.BranchDto * Grace.Types.Reference.ReferenceDto array>
            "branch.get-promotions", typeof<Grace.Types.Branch.BranchDto * Grace.Types.Reference.ReferenceDto array>
            "branch.get-references", typeof<Grace.Types.Branch.BranchDto * Grace.Types.Reference.ReferenceDto array>
            "branch.get-saves", typeof<Grace.Types.Branch.BranchDto * Grace.Types.Reference.ReferenceDto array>
            "branch.get-tags", typeof<Grace.Types.Branch.BranchDto * Grace.Types.Reference.ReferenceDto array>
            "branch.get-recursive-size", typeof<int64>
            "branch.list-contents", typeof<IEnumerable<Grace.Types.DirectoryVersion.DirectoryVersionDto>>
            "candidate.attestations", typeof<Grace.Shared.Parameters.Review.CandidateAttestationsResult>
            "candidate.cancel", typeof<Grace.Shared.Parameters.Review.CandidateActionResult>
            "candidate.gate.rerun", typeof<Grace.Shared.Parameters.Review.CandidateActionResult>
            "candidate.retry", typeof<Grace.Shared.Parameters.Review.CandidateActionResult>
            "candidate.get", typeof<Grace.Shared.Parameters.Review.CandidateProjectionSnapshotResult>
            "candidate.required-actions", typeof<Grace.Shared.Parameters.Review.CandidateRequiredActionsResult>
            "config.write", typeof<unit>
            "connect", typeof<Grace.CLI.Common.LocalOutputDto.ConnectDto>
            "diff.blake3", typeof<Grace.Types.Diff.DiffDto>
            "directory-version.get-zip-file", typeof<Uri>
            "history.delete", typeof<Grace.CLI.Common.LocalOutputDto.HistoryDeleteDto>
            "history.off", typeof<Grace.CLI.Common.LocalOutputDto.HistoryRecordingDto>
            "history.on", typeof<Grace.CLI.Common.LocalOutputDto.HistoryRecordingDto>
            "history.search", typeof<Grace.CLI.Common.LocalOutputDto.HistoryEntriesDto>
            "history.show", typeof<Grace.CLI.Common.LocalOutputDto.HistoryEntriesDto>
            "doctor", typeof<Grace.CLI.Common.LocalOutputDto.DoctorReportDto>
            "maintenance.check-ignore-entries", typeof<Grace.CLI.Common.LocalOutputDto.MaintenanceIgnoreEntriesDto>
            "maintenance.clear-journal", typeof<Grace.CLI.Common.LocalOutputDto.MaintenanceClearJournalDto>
            "maintenance.list-contents", typeof<Grace.CLI.Common.LocalOutputDto.MaintenanceListContentsDto>
            "maintenance.scan", typeof<Grace.CLI.Common.LocalOutputDto.MaintenanceScanDto>
            "maintenance.show-journal", typeof<Grace.CLI.Common.LocalOutputDto.MaintenanceShowJournalDto>
            "maintenance.stats", typeof<Grace.CLI.Common.LocalOutputDto.MaintenanceStatsDto>
            "maintenance.update-index", typeof<Grace.CLI.Common.LocalOutputDto.MaintenanceStatsDto>
            "organization.create", typeof<string>
            "organization.delete", typeof<string>
            "organization.set-description", typeof<string>
            "organization.set-name", typeof<string>
            "organization.set-search-visibility", typeof<string>
            "organization.set-type", typeof<string>
            "organization.undelete", typeof<string>
            "organization.get", typeof<Grace.Types.Organization.OrganizationDto>
            "owner.create", typeof<string>
            "owner.delete", typeof<string>
            "owner.set-description", typeof<string>
            "owner.set-name", typeof<string>
            "owner.set-search-visibility", typeof<string>
            "owner.set-type", typeof<string>
            "owner.undelete", typeof<string>
            "owner.get", typeof<Grace.Types.Owner.OwnerDto>
            "promotion-set.apply", typeof<string>
            "promotion-set.conflicts.resolve", typeof<string>
            "promotion-set.create", typeof<string>
            "promotion-set.delete", typeof<string>
            "promotion-set.recompute", typeof<string>
            "promotion-set.request-approval", typeof<string>
            "promotion-set.update-input-promotions", typeof<string>
            "promotion-set.conflicts.show", cliLocalResultType "Grace.CLI.Command.PromotionSetCommand+ConflictShowResult"
            "promotion-set.get", typeof<Grace.Types.PromotionSet.PromotionSetDto>
            "promotion-set.show", typeof<Grace.Types.PromotionSet.PromotionSetDto>
            "promotion-set.get-events", typeof<IReadOnlyList<Grace.Types.PromotionSet.PromotionSetEvent>>
            "promotion-set.list", typeof<(Grace.Types.PromotionSet.PromotionSetDto * Grace.Types.Webhooks.PromotionSetApprovalSummary option) list>
            "queue.dequeue", typeof<string>
            "queue.enqueue", typeof<string>
            "queue.pause", typeof<string>
            "queue.resume", typeof<string>
            "queue.status", typeof<Grace.Types.Queue.PromotionQueue>
            "repository.create", typeof<string>
            "repository.delete", typeof<string>
            "repository.set-allows-large-files", typeof<string>
            "repository.set-anonymous-access", typeof<string>
            "repository.set-checkpoint-days", typeof<string>
            "repository.set-conflict-resolution-policy", typeof<string>
            "repository.set-default-server-api-version", typeof<string>
            "repository.set-description", typeof<string>
            "repository.set-diff-cache-days", typeof<string>
            "repository.set-directory-version-cache-days", typeof<string>
            "repository.set-logical-delete-days", typeof<string>
            "repository.set-name", typeof<string>
            "repository.set-record-saves", typeof<string>
            "repository.set-save-days", typeof<string>
            "repository.set-status", typeof<string>
            "repository.set-visibility", typeof<string>
            "repository.undelete", typeof<string>
            "repository.get", typeof<Grace.Types.Repository.RepositoryDto>
            "repository.get-branches", typeof<IEnumerable<Grace.Types.Branch.BranchDto>>
            "repository.init", typeof<Grace.CLI.Common.LocalOutputDto.RepositoryInitDto>
            "library.get", typeof<Grace.Types.Library.LibraryCatalogDto>
            "library.list", typeof<Grace.Types.Library.LibraryCatalogDto>
            "library.add", typeof<Grace.Types.Library.LibraryCatalogChangeResultDto>
            "library.remove", typeof<Grace.Types.Library.LibraryCatalogChangeResultDto>
            "library.rename", typeof<Grace.CLI.Command.LibraryCommand.RenameOutput>
            "library.sync.enable", typeof<Grace.CLI.Command.LibrarySynchronization.LibrarySynchronizationStatus>
            "library.sync.run", typeof<Grace.CLI.Command.LibrarySynchronization.LibrarySynchronizationStatus>
            "library.sync.pause", typeof<Grace.CLI.Command.LibrarySynchronization.LibrarySynchronizationStatus>
            "library.sync.resume", typeof<Grace.CLI.Command.LibrarySynchronization.LibrarySynchronizationStatus>
            "library.sync.status", typeof<Grace.CLI.Command.LibrarySynchronization.LibrarySynchronizationStatus>
            "review.checkpoint", typeof<string>
            "review.deepen", typeof<string>
            "review.resolve", typeof<string>
            "review.inbox", typeof<obj>
            "review.open", typeof<Grace.Types.Review.ReviewNotes option>
            "review.report.export", typeof<Grace.CLI.Common.LocalOutputDto.ReviewReportExportDto>
            "review.report.show", typeof<Grace.SDK.ReviewReportResult>
            "watch", cliLocalResultType "Grace.CLI.Command.Watch+WatchCheckStatusDto"
            "webhook.create", typeof<Grace.Types.Webhooks.WebhookRule>
            "webhook.delete", typeof<Grace.Types.Webhooks.WebhookRule>
            "webhook.disable", typeof<Grace.Types.Webhooks.WebhookRule>
            "webhook.enable", typeof<Grace.Types.Webhooks.WebhookRule>
            "webhook.show", typeof<Grace.Types.Webhooks.WebhookRule>
            "webhook.update", typeof<Grace.Types.Webhooks.WebhookRule>
            "webhook.deliveries", typeof<IReadOnlyList<Grace.Types.Webhooks.WebhookDelivery>>
            "webhook.delivery.show", typeof<Grace.Types.Webhooks.WebhookDelivery>
            "webhook.test", typeof<Grace.Types.Webhooks.WebhookDelivery>
            "webhook.list", typeof<IReadOnlyList<Grace.Types.Webhooks.WebhookRule>>
            "workitem.attachments.add", cliLocalResultType "Grace.CLI.Command.WorkItemCommand+AttachmentResult"
            "workitem.attachments.download", cliLocalResultType "Grace.CLI.Command.WorkItemCommand+AttachmentDownloadResult"
            "workitem.attachments.delete", typeof<Grace.Types.Artifact.ArtifactDeletionResult>
            "workitem.attachments.list", typeof<Grace.Shared.Parameters.WorkItem.ListWorkItemAttachmentsResult>
            "workitem.attachments.show", typeof<Grace.Shared.Parameters.WorkItem.ShowWorkItemAttachmentResult>
            "workitem.attachments.undelete", typeof<string>
            "workitem.description.clear", typeof<string>
            "workitem.description.set", typeof<string>
            "workitem.create", typeof<string>
            "workitem.link.prset", typeof<string>
            "workitem.link.ref", typeof<string>
            "workitem.links.remove.prset", typeof<string>
            "workitem.links.remove.ref", typeof<string>
            "workitem.set-status", typeof<string>
            "workitem.links.list", typeof<Grace.Types.WorkItem.WorkItemLinksDto>
            "workitem.show", typeof<Grace.Types.WorkItem.WorkItemDto>
        ]

    /// Names the routed commands whose success output is not yet a stable `GraceReturnValue<T>` envelope.
    /// Adding a command here is a deliberate, reviewable choice to ship it without a describable JSON success contract.
    let private migrationRequiredCommandIds =
        [
            "branch.rebase"
            "branch.status"
            "branch.switch"
            "diff.checkpoint"
            "diff.commit"
            "diff.directoryid"
            "diff.promotion"
            "diff.save"
            "diff.sha"
            "diff.tag"
            "history.run"
        ]

    /// Names the condition under which `grace watch` emits its status envelope instead of a JSON error.
    let private watchCheckCondition =
        "`grace watch --check` supports JSON and --select status output; foreground `grace watch` still returns a JSON error because it is a continuous workflow."

    /// Selects the JSON-output support level for a command that declares a result type.
    let private jsonOutputForDeclared commandId =
        match commandId with
        | "watch" -> Conditional watchCheckCondition
        | _ -> Supported

    /// Builds type-derived schema and examples for a command that emits a Grace success envelope.
    let private typeDerivedReturnValueContract (resultType: Type) =
        supportedReturnValueContract
            (contractTypeName resultType)
            $"Declared ReturnValue type: {resultType.FullName}"
            (schemaForType [] resultType)
            (representativeExample resultType)
            [
                "ReturnValue schema and example are derived from the declared result type and Grace's configured JSON serializer policy."
            ]

    /// Explains why a migration-required command cannot publish a `ReturnValue` schema or example yet.
    let private migrationRequiredContract =
        incompleteReturnValueContract
            "migration-required"
            "This command's JSON success path still requires migration before schema/examples can describe the emitted ReturnValue."

    /// Builds the command document section of the machine-readable command-output contract.
    let private commandDocument (identity: CommandIdentity) =
        { Id = identity.CommandId; Path = identity.CommandPath; GroupPath = identity.GroupPath; Name = identity.CommandName }

    /// Builds the support section that tells callers whether JSON output and `--select` are available.
    let private supportDocument (entry: CommandContractEntry) =
        {
            JsonOutput =
                match entry.JsonOutput with
                | Supported -> "Supported"
                | Conditional _ -> "Conditional"
                | MigrationRequired -> "MigrationRequired"
            Select = entry.SupportsSelect
        }

    /// Builds command-output contract metadata for success envelope schema so automation can rely on stable JSON shapes.
    let private successEnvelopeSchema (entry: CommandContractEntry) =
        schemaObject
            $"GraceReturnValue<{entry.ReturnValueContract.Name}>"
            [
                "ReturnValue", entry.ReturnValueContract.Schema
                "EventTime", scalarSchema "string"
                "CorrelationId", scalarSchema "string"
                "Properties", propertyBagSchema
            ]
            [|
                "ReturnValue"
                "EventTime"
                "CorrelationId"
                "Properties"
            |]

    let private errorEnvelopeSchema =
        schemaObject
            "GraceError"
            [
                "Exception", nullableObjectSchema "Serialized Grace exception details, or null/default when no exception object is available."
                "Error", scalarSchema "string"
                "EventTime", scalarSchema "string"
                "CorrelationId", scalarSchema "string"
                "Properties", anySchema "GraceError serializes Properties with the shared serializer policy."
            ]
            [|
                "Exception"
                "Error"
                "EventTime"
                "CorrelationId"
                "Properties"
            |]

    /// Builds the schema document section of the machine-readable command-output contract.
    let private schemaDocument (entry: CommandContractEntry) =
        let status =
            match entry.ReturnValueContract.Status with
            | SchemaReady -> "schema-ready"
            | MetadataIncomplete -> "metadata-incomplete"

        {
            Status = status
            Source = "CommandOutputContract"
            Envelope =
                match entry.JsonOutput with
                | Supported -> "GraceReturnValue<T> on success; GraceError on error. CLI success Properties are emitted as Key/Value entries."
                | Conditional condition ->
                    $"GraceReturnValue<T> status envelope for status checks, including unavailable modes with nonzero exit codes. GraceError remains for parser and command execution errors outside that status-check path. {condition}"
                | MigrationRequired -> "Success output is not yet a stable GraceReturnValue<T> for this command; GraceError on error."
            ReturnValueContract = entry.ReturnValueContract.Name
            SuccessSchema = successEnvelopeSchema entry
            ErrorSchema = errorEnvelopeSchema
            Notes =
                [
                    "Schema is derived from CommandOutputContract registry metadata."
                    "This path is inert and does not execute the command action."
                    yield! entry.ReturnValueContract.Notes
                ]
        }

    /// Builds command-output contract metadata for success example so automation can rely on stable JSON shapes.
    let private successExample (entry: CommandContractEntry) =
        {
            Name = "success-envelope-shape"
            Description = $"Representative GraceReturnValue<{entry.ReturnValueContract.Name}> envelope shape from registry metadata."
            Document =
                box
                    {|
                        ReturnValue = entry.ReturnValueContract.Example
                        EventTime = "2026-06-05T00:00:00Z"
                        CorrelationId = "correlation-id"
                        Properties = cliProperties entry.Identity.CommandId entry.ReturnValueContract.Provenance
                    |}
        }

    /// Builds the example that names the metadata gap for a command that is not schema-ready.
    let private incompleteMetadataExample (entry: CommandContractEntry) =
        {
            Name = "metadata-incomplete"
            Description = "Explicit machine-readable metadata gap for a command that is not schema-ready."
            Document =
                box
                    {|
                        Status = "metadata-incomplete"
                        CommandId = entry.Identity.CommandId
                        ReturnValueContract = entry.ReturnValueContract.Name
                        Reason =
                            entry.ReturnValueContract.Notes
                            |> String.concat " "
                        Properties = cliProperties entry.Identity.CommandId entry.ReturnValueContract.Provenance
                    |}
        }

    /// Builds command-output contract metadata for error example so automation can rely on stable JSON shapes.
    let private errorExample (entry: CommandContractEntry) =
        {
            Name = "error-envelope-shape"
            Description = "Representative GraceError envelope shape."
            Document =
                box
                    {|
                        Exception = null
                        Error = "error message"
                        EventTime = "2026-06-05T00:00:00Z"
                        CorrelationId = "correlation-id"
                        Properties = cliProperties entry.Identity.CommandId "CommandOutputContract"
                    |}
        }

    /// Returns command-specific source metadata where output-envelope metadata alone cannot express input exclusivity.
    let private commandInputDocument (identity: CommandIdentity) =
        match identity.CommandId with
        | "workitem.description.set" ->
            Some
                {
                    Selection = "ExactlyOne"
                    Description = "Supply exactly one complete Markdown source; the selected text is sent unchanged."
                    Options =
                        [
                            { Name = "--text"; ValueKind = "string"; Description = "Use inline Markdown text." }
                            { Name = "--file"; ValueKind = "path"; Description = "Read complete Markdown text from a file." }
                            { Name = "--stdin"; ValueKind = "flag"; Description = "Read complete Markdown text from standard input." }
                        ]
                }
        | _ -> None

    /// Builds the introspection document section of the machine-readable command-output contract.
    let introspectionDocument (kind: IntrospectionKind) (entry: CommandContractEntry) =
        {
            Kind =
                match kind with
                | Schema -> "schema"
                | Examples -> "examples"
            ContractVersion = "cli-json-v1"
            Command = commandDocument entry.Identity
            Support = supportDocument entry
            Input = commandInputDocument entry.Identity
            Schema =
                match kind with
                | Schema -> Some(schemaDocument entry)
                | Examples -> None
            Examples =
                match kind with
                | Schema -> []
                | Examples ->
                    match entry.ReturnValueContract.Status with
                    | SchemaReady ->
                        [
                            successExample entry
                            errorExample entry
                        ]
                    | MetadataIncomplete ->
                        [
                            incompleteMetadataExample entry
                            errorExample entry
                        ]
        }

    /// Builds the registry identity for a command from its group path and leaf name.
    let internal commandIdentity groupPath commandName = { GroupPath = groupPath; CommandName = commandName }

    /// Parses a dotted command ID such as `workitem.attachments.add` into its group path and leaf name.
    let private identityOfCommandId (commandId: string) =
        let segments = commandId.Split('.') |> List.ofArray
        commandIdentity (segments |> List.take (segments.Length - 1)) (List.last segments)

    /// Walks the live System.CommandLine tree and returns the identity of every leaf command a user can invoke.
    let discoverLeafCommands (rootCommand: Command) =
        /// Descends one command level, accumulating the group path until a leaf command is reached.
        let rec loop path (command: Command) =
            let subcommands =
                command.Subcommands
                |> Seq.cast<Command>
                |> Seq.toList

            if subcommands.IsEmpty then
                [
                    { GroupPath = path; CommandName = command.Name }
                ]
            else
                subcommands
                |> List.collect (fun child -> loop (path @ [ command.Name ]) child)

        rootCommand.Subcommands
        |> Seq.cast<Command>
        |> Seq.toList
        |> List.collect (loop [])

    /// Lists the output contract of every live leaf command.
    let entries =
        [
            for commandId, resultType in declaredResultTypes do
                {
                    Identity = identityOfCommandId commandId
                    JsonOutput = jsonOutputForDeclared commandId
                    ReturnValueContract = typeDerivedReturnValueContract resultType
                }

            for commandId in migrationRequiredCommandIds do
                { Identity = identityOfCommandId commandId; JsonOutput = MigrationRequired; ReturnValueContract = migrationRequiredContract }
        ]

    /// Looks up the output contract for one command identity, or `None` when the command has no registry entry.
    let tryFind identity =
        entries
        |> List.tryFind (fun entry -> entry.Identity = identity)
