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

    /// Models route disposition values passed between the parser and command output contract handlers.
    type RouteDisposition =
        | Routed
        | SourceOnlyUnrouted of disposition: string

    /// Models current json behavior values passed between the parser and command output contract handlers.
    type CurrentJsonBehavior =
        | CommonRenderOutputEnvelope
        | ImmediateJsonErrorOnly
        | ConditionalCheckStatusEnvelope
        | HumanProgressOnlySuccess
        | PartialManualSuccess
        | ManualJsonUnenveloped
        | HumanProcOnly
        | HumanOnly
        | UnroutedSourceOnly

    /// Models command category values passed between the parser and command output contract handlers.
    type CommandCategory =
        | ProgressLocalWorkflow
        | MutatingStateTransition
        | ReadOrMutatingVerify
        | ReadListSearch
        | Mutating
        | FireAndForgetProgress
        | WorkflowAcceptedOperation
        | HelpIntrospection

    /// Models execution scope values passed between the parser and command output contract handlers.
    type ExecutionScope =
        | CompositeLocalAndServer
        | LocalClient
        | ServerViaSdk
        | Verify
        | ServerViaSdkDefinedButNotRootRouted

    /// Models output dto disposition values passed between the parser and command output contract handlers.
    type OutputDtoDisposition =
        | ReuseExistingApiOrSdkDto
        | RequiresCliDto
        | NoServerDto

    /// Models envelope contract values passed between the parser and command output contract handlers.
    type EnvelopeContract =
        | ExistingGraceResultEnvelope of dtoDisposition: OutputDtoDisposition
        | ConditionalGraceResultEnvelope of dtoDisposition: OutputDtoDisposition * condition: string
        | MigrationRequiredToGraceResultEnvelope of dtoDisposition: OutputDtoDisposition
        | JsonModeErrorOnly of reason: string
        | SourceOnlyUnsupported of disposition: string

    /// Models feature state values passed between the parser and command output contract handlers.
    type FeatureState =
        | ExistingBehavior
        | FutureInertIntrospection
        | FutureReturnValueProjection
        | UnsupportedUntilRouted
        | RequiresMigration

    /// Defines structured data exchanged by CLI helpers.
    type MachineReadableFeatures = { JsonMode: FeatureState; Schema: FeatureState; Examples: FeatureState; Select: FeatureState }

    /// Models return value metadata status values passed between the parser and command output contract handlers.
    type ReturnValueMetadataStatus =
        | SchemaReady
        | MetadataIncomplete
        | ContractUnsupported

    /// Defines structured data exchanged by CLI helpers.
    type ReturnValueContract = { Name: string; Provenance: string; Status: ReturnValueMetadataStatus; Schema: obj; Example: obj; Notes: string list }

    /// Models command contract entry values passed between the parser and command output contract handlers.
    type CommandContractEntry =
        {
            Identity: CommandIdentity
            RouteDisposition: RouteDisposition
            CurrentJsonBehavior: CurrentJsonBehavior
            Category: CommandCategory
            ExecutionScope: ExecutionScope
            Mutating: bool
            EnvelopeContract: EnvelopeContract
            Features: MachineReadableFeatures
            ReturnValueContract: ReturnValueContract
        }

    /// Models introspection kind values passed between the parser and command output contract handlers.
    type IntrospectionKind =
        | Schema
        | Examples

    /// Defines structured data exchanged by CLI helpers.
    type CommandIdentityDocument = { Id: string; Path: string list; GroupPath: string list; Name: string }

    /// Models command registry document values passed between the parser and command output contract handlers.
    type CommandRegistryDocument =
        {
            RouteDisposition: string
            CurrentJsonBehavior: string
            Category: string
            ExecutionScope: string
            Mutating: bool
            EnvelopeContract: string
            JsonMode: string
            Schema: string
            Examples: string
            Select: string
        }

    /// Models command schema document values passed between the parser and command output contract handlers.
    type CommandSchemaDocument =
        {
            Status: string
            Source: string
            Envelope: string
            ReturnValueDisposition: string
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
            Registry: CommandRegistryDocument
            Input: CommandInputDocument option
            Schema: CommandSchemaDocument option
            Examples: CommandExampleDocument list
        }

    /// Converts a union-case value into the stable case name emitted in command-output metadata.
    let private unionName value = $"{value}"

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

    /// Builds command-output contract metadata for unsupported return value schema so automation can rely on stable JSON shapes.
    let private unsupportedReturnValueSchema reason =
        schemaObject
            "Unsupported command output contract"
            [
                "Status", scalarSchema "string"
                "Reason", scalarSchema "string"
            ]
            [| "Status"; "Reason" |]

    /// Builds command-output contract metadata for unsupported return value example so automation can rely on stable JSON shapes.
    let private unsupportedReturnValueExample reason = box {| Status = "metadata-incomplete"; Reason = reason |}

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

    /// Builds command-output contract metadata for incomplete return value contract so automation can rely on stable JSON shapes.
    let private incompleteReturnValueContract name reason =
        {
            Name = name
            Provenance = "CommandOutputContract"
            Status = MetadataIncomplete
            Schema = unsupportedReturnValueSchema reason
            Example = unsupportedReturnValueExample reason
            Notes = [ reason ]
        }

    /// Builds command-output contract metadata for unsupported return value contract so automation can rely on stable JSON shapes.
    let private unsupportedReturnValueContract name reason =
        {
            Name = name
            Provenance = "CommandOutputContract"
            Status = ContractUnsupported
            Schema = unsupportedReturnValueSchema reason
            Example = unsupportedReturnValueExample reason
            Notes = [ reason ]
        }

    /// Builds command-output contract metadata for route disposition text so automation can rely on stable JSON shapes.
    let private routeDispositionText (disposition: RouteDisposition) =
        match disposition with
        | Routed -> "Routed"
        | SourceOnlyUnrouted reason -> $"SourceOnlyUnrouted: {reason}"

    /// Builds command-output contract metadata for output dto disposition text so automation can rely on stable JSON shapes.
    let private outputDtoDispositionText (disposition: OutputDtoDisposition) =
        match disposition with
        | ReuseExistingApiOrSdkDto -> "ReuseExistingApiOrSdkDto"
        | RequiresCliDto -> "RequiresCliDto"
        | NoServerDto -> "NoServerDto"

    /// Builds command-output contract metadata for envelope contract text so automation can rely on stable JSON shapes.
    let private envelopeContractText (contract: EnvelopeContract) =
        match contract with
        | ExistingGraceResultEnvelope disposition -> $"ExistingGraceResultEnvelope: {outputDtoDispositionText disposition}"
        | ConditionalGraceResultEnvelope (disposition, condition) -> $"ConditionalGraceResultEnvelope: {outputDtoDispositionText disposition}; {condition}"
        | MigrationRequiredToGraceResultEnvelope disposition -> $"MigrationRequiredToGraceResultEnvelope: {outputDtoDispositionText disposition}"
        | JsonModeErrorOnly reason -> $"JsonModeErrorOnly: {reason}"
        | SourceOnlyUnsupported reason -> $"SourceOnlyUnsupported: {reason}"

    /// Builds command-output contract metadata for return value disposition text so automation can rely on stable JSON shapes.
    let private returnValueDispositionText (contract: EnvelopeContract) =
        match contract with
        | ExistingGraceResultEnvelope disposition
        | ConditionalGraceResultEnvelope (disposition, _)
        | MigrationRequiredToGraceResultEnvelope disposition -> outputDtoDispositionText disposition
        | JsonModeErrorOnly reason -> $"Unsupported: {reason}"
        | SourceOnlyUnsupported reason -> $"Unsupported: {reason}"

    /// Maps each JSON-success command to the exact result type declared by its current handler or SDK call.
    let private declaredResultTypeFor commandId =
        match commandId with
        | "authorize.can"
        | "authorize.check" -> typeof<Grace.Types.Authorization.PermissionCheckResult>
        | "authorize.grant-role"
        | "authorize.list-role-assignments"
        | "authorize.revoke-role"
        | "authorize.show" -> typeof<Grace.Types.Authorization.RoleAssignment list>
        | "authorize.list-path-permissions"
        | "authorize.remove-path-permission"
        | "authorize.upsert-path-permission" -> typeof<Grace.Types.Common.PathPermission list>
        | "authorize.list-roles" -> typeof<Grace.Types.Authorization.RoleDefinition list>
        | "admin.reminder.list" -> typeof<IEnumerable<Grace.Types.Reminder.ReminderDto>>
        | "admin.reminder.get" -> typeof<Grace.Types.Reminder.ReminderDto>
        | "admin.reminder.create"
        | "admin.reminder.delete"
        | "admin.reminder.reschedule"
        | "admin.reminder.update-time" -> typeof<string>
        | "agent.add-summary" -> cliLocalResultType "Grace.CLI.Command.AgentCommand+AddSummaryResult"
        | "agent.bootstrap"
        | "agent.work.start"
        | "agent.work.status"
        | "agent.work.stop" -> typeof<Grace.Types.Automation.AgentSessionOperationResult>
        | "alias.list" -> typeof<Grace.CLI.Common.LocalOutputDto.AliasListDto>
        | "approval.policy.create"
        | "approval.policy.delete"
        | "approval.policy.disable"
        | "approval.policy.enable"
        | "approval.policy.show"
        | "approval.policy.update" -> typeof<Grace.Types.Webhooks.ApprovalPolicy>
        | "approval.policy.evaluate"
        | "approval.policy.list" -> typeof<IReadOnlyList<Grace.Types.Webhooks.ApprovalPolicy>>
        | "approval.request.approve"
        | "approval.request.reject"
        | "approval.request.show"
        | "approval.request.wait" -> typeof<Grace.Types.Webhooks.ApprovalRequest>
        | "approval.request.history"
        | "approval.request.list" -> typeof<IReadOnlyList<Grace.Types.Webhooks.ApprovalRequest>>
        | "authenticate.login"
        | "authenticate.logout"
        | "authenticate.token.clear"
        | "authenticate.token.set"
        | "authenticate.token.status" -> typeof<string>
        | "authenticate.status" -> typeof<Grace.CLI.Command.Auth.AuthStatusOutput>
        | "authenticate.token.create" -> typeof<Grace.Types.PersonalAccessToken.PersonalAccessTokenCreated>
        | "authenticate.token.list" -> typeof<Grace.Types.PersonalAccessToken.PersonalAccessTokenSummary list>
        | "authenticate.token.revoke" -> typeof<Grace.Types.PersonalAccessToken.PersonalAccessTokenSummary>
        | "authenticate.whoami" -> typeof<Grace.CLI.Command.Auth.AuthInfo>
        | "branch.annotate" -> typeof<Grace.Types.Annotation.BranchAnnotationDto>
        | "branch.assign"
        | "branch.checkpoint"
        | "branch.commit"
        | "branch.create"
        | "branch.create-external"
        | "branch.delete"
        | "branch.enable-assign"
        | "branch.enable-auto-rebase"
        | "branch.enable-checkpoints"
        | "branch.enable-commit"
        | "branch.enable-external"
        | "branch.enable-promotion"
        | "branch.enable-save"
        | "branch.enable-tag"
        | "branch.promote"
        | "branch.save"
        | "branch.set-name"
        | "branch.set-promotion-mode"
        | "branch.tag"
        | "branch.update-parent-branch" -> typeof<string>
        | "branch.get" -> typeof<Grace.Types.Branch.BranchDto>
        | "branch.get-checkpoints"
        | "branch.get-commits"
        | "branch.get-externals"
        | "branch.get-promotions"
        | "branch.get-references"
        | "branch.get-saves"
        | "branch.get-tags" -> typeof<Grace.Types.Branch.BranchDto * Grace.Types.Reference.ReferenceDto array>
        | "branch.get-recursive-size" -> typeof<int64>
        | "branch.list-contents" -> typeof<IEnumerable<Grace.Types.DirectoryVersion.DirectoryVersionDto>>
        | "candidate.attestations" -> typeof<Grace.Shared.Parameters.Review.CandidateAttestationsResult>
        | "candidate.cancel"
        | "candidate.gate.rerun"
        | "candidate.retry" -> typeof<Grace.Shared.Parameters.Review.CandidateActionResult>
        | "candidate.get" -> typeof<Grace.Shared.Parameters.Review.CandidateProjectionSnapshotResult>
        | "candidate.required-actions" -> typeof<Grace.Shared.Parameters.Review.CandidateRequiredActionsResult>
        | "config.write" -> typeof<unit>
        | "connect" -> typeof<Grace.CLI.Common.LocalOutputDto.ConnectDto>
        | "diff.blake3" -> typeof<Grace.Types.Diff.DiffDto>
        | "directory-version.get-zip-file" -> typeof<Uri>
        | "history.delete" -> typeof<Grace.CLI.Common.LocalOutputDto.HistoryDeleteDto>
        | "history.off"
        | "history.on" -> typeof<Grace.CLI.Common.LocalOutputDto.HistoryRecordingDto>
        | "history.search"
        | "history.show" -> typeof<Grace.CLI.Common.LocalOutputDto.HistoryEntriesDto>
        | "doctor" -> typeof<Grace.CLI.Common.LocalOutputDto.DoctorReportDto>
        | "maintenance.check-ignore-entries" -> typeof<Grace.CLI.Common.LocalOutputDto.MaintenanceIgnoreEntriesDto>
        | "maintenance.clear-journal" -> typeof<Grace.CLI.Common.LocalOutputDto.MaintenanceClearJournalDto>
        | "maintenance.list-contents" -> typeof<Grace.CLI.Common.LocalOutputDto.MaintenanceListContentsDto>
        | "maintenance.scan" -> typeof<Grace.CLI.Common.LocalOutputDto.MaintenanceScanDto>
        | "maintenance.show-journal" -> typeof<Grace.CLI.Common.LocalOutputDto.MaintenanceShowJournalDto>
        | "maintenance.stats"
        | "maintenance.update-index" -> typeof<Grace.CLI.Common.LocalOutputDto.MaintenanceStatsDto>
        | "organization.create"
        | "organization.delete"
        | "organization.set-description"
        | "organization.set-name"
        | "organization.set-search-visibility"
        | "organization.set-type"
        | "organization.undelete" -> typeof<string>
        | "organization.get" -> typeof<Grace.Types.Organization.OrganizationDto>
        | "owner.create"
        | "owner.delete"
        | "owner.set-description"
        | "owner.set-name"
        | "owner.set-search-visibility"
        | "owner.set-type"
        | "owner.undelete" -> typeof<string>
        | "owner.get" -> typeof<Grace.Types.Owner.OwnerDto>
        | "promotion-set.apply"
        | "promotion-set.conflicts.resolve"
        | "promotion-set.create"
        | "promotion-set.delete"
        | "promotion-set.recompute"
        | "promotion-set.request-approval"
        | "promotion-set.update-input-promotions" -> typeof<string>
        | "promotion-set.conflicts.show" -> cliLocalResultType "Grace.CLI.Command.PromotionSetCommand+ConflictShowResult"
        | "promotion-set.get"
        | "promotion-set.show" -> typeof<Grace.Types.PromotionSet.PromotionSetDto>
        | "promotion-set.get-events" -> typeof<IReadOnlyList<Grace.Types.PromotionSet.PromotionSetEvent>>
        | "promotion-set.list" -> typeof<(Grace.Types.PromotionSet.PromotionSetDto * Grace.Types.Webhooks.PromotionSetApprovalSummary option) list>
        | "queue.dequeue"
        | "queue.enqueue"
        | "queue.pause"
        | "queue.resume" -> typeof<string>
        | "queue.status" -> typeof<Grace.Types.Queue.PromotionQueue>
        | "repository.create"
        | "repository.delete"
        | "repository.set-allows-large-files"
        | "repository.set-anonymous-access"
        | "repository.set-checkpoint-days"
        | "repository.set-conflict-resolution-policy"
        | "repository.set-default-server-api-version"
        | "repository.set-description"
        | "repository.set-diff-cache-days"
        | "repository.set-directory-version-cache-days"
        | "repository.set-logical-delete-days"
        | "repository.set-name"
        | "repository.set-record-saves"
        | "repository.set-save-days"
        | "repository.set-status"
        | "repository.set-visibility"
        | "repository.undelete" -> typeof<string>
        | "repository.get" -> typeof<Grace.Types.Repository.RepositoryDto>
        | "repository.get-branches" -> typeof<IEnumerable<Grace.Types.Branch.BranchDto>>
        | "repository.init" -> typeof<Grace.CLI.Common.LocalOutputDto.RepositoryInitDto>
        | "review.checkpoint"
        | "review.deepen"
        | "review.resolve" -> typeof<string>
        | "review.inbox" -> typeof<obj>
        | "review.open" -> typeof<Grace.Types.Review.ReviewNotes option>
        | "review.report.export" -> typeof<Grace.CLI.Common.LocalOutputDto.ReviewReportExportDto>
        | "review.report.show" -> typeof<Grace.SDK.ReviewReportResult>
        | "watch" -> cliLocalResultType "Grace.CLI.Command.Watch+WatchCheckStatusDto"
        | "webhook.create"
        | "webhook.delete"
        | "webhook.disable"
        | "webhook.enable"
        | "webhook.show"
        | "webhook.update" -> typeof<Grace.Types.Webhooks.WebhookRule>
        | "webhook.deliveries" -> typeof<IReadOnlyList<Grace.Types.Webhooks.WebhookDelivery>>
        | "webhook.delivery.show"
        | "webhook.test" -> typeof<Grace.Types.Webhooks.WebhookDelivery>
        | "webhook.list" -> typeof<IReadOnlyList<Grace.Types.Webhooks.WebhookRule>>
        | "workitem.attachments.add" -> cliLocalResultType "Grace.CLI.Command.WorkItemCommand+AttachmentResult"
        | "workitem.attachments.download" -> cliLocalResultType "Grace.CLI.Command.WorkItemCommand+AttachmentDownloadResult"
        | "workitem.attachments.delete" -> typeof<Grace.Types.Artifact.ArtifactDeletionResult>
        | "workitem.attachments.list" -> typeof<Grace.Shared.Parameters.WorkItem.ListWorkItemAttachmentsResult>
        | "workitem.attachments.show" -> typeof<Grace.Shared.Parameters.WorkItem.ShowWorkItemAttachmentResult>
        | "workitem.attachments.undelete" -> typeof<string>
        | "workitem.description.clear"
        | "workitem.description.set" -> typeof<string>
        | "workitem.create"
        | "workitem.link.prset"
        | "workitem.link.ref"
        | "workitem.links.remove.prset"
        | "workitem.links.remove.ref"
        | "workitem.set-status" -> typeof<string>
        | "workitem.links.list" -> typeof<Grace.Types.WorkItem.WorkItemLinksDto>
        | "workitem.show" -> typeof<Grace.Types.WorkItem.WorkItemDto>
        | unknown -> invalidOp $"JSON-success command '{unknown}' does not declare a ReturnValue type in CommandOutputContract."

    /// Builds type-derived schema and examples for every command that already emits a Grace success envelope.
    let private typeDerivedReturnValueContract commandId =
        let resultType = declaredResultTypeFor commandId

        supportedReturnValueContract
            (contractTypeName resultType)
            $"Declared ReturnValue type: {resultType.FullName}"
            (schemaForType [] resultType)
            (representativeExample resultType)
            [
                "ReturnValue schema and example are derived from the declared result type and Grace's configured JSON serializer policy."
            ]

    /// Selects a derived contract only for commands with a real JSON success path.
    let private returnValueContractFor (identity: CommandIdentity) (envelopeContract: EnvelopeContract) =
        match envelopeContract with
        | ExistingGraceResultEnvelope _
        | ConditionalGraceResultEnvelope _ -> typeDerivedReturnValueContract identity.CommandId
        | SourceOnlyUnsupported reason -> unsupportedReturnValueContract "unsupported" reason
        | JsonModeErrorOnly reason -> unsupportedReturnValueContract "unsupported" reason
        | MigrationRequiredToGraceResultEnvelope disposition ->
            incompleteReturnValueContract
                (outputDtoDispositionText disposition)
                "This command is routed, but its JSON success path still requires migration before schema/examples can describe the emitted ReturnValue."

    /// Builds the command document section of the machine-readable command-output contract.
    let private commandDocument (identity: CommandIdentity) =
        { Id = identity.CommandId; Path = identity.CommandPath; GroupPath = identity.GroupPath; Name = identity.CommandName }

    /// Builds the registry document section of the machine-readable command-output contract.
    let private registryDocument (entry: CommandContractEntry) =
        {
            RouteDisposition = routeDispositionText entry.RouteDisposition
            CurrentJsonBehavior = unionName entry.CurrentJsonBehavior
            Category = unionName entry.Category
            ExecutionScope = unionName entry.ExecutionScope
            Mutating = entry.Mutating
            EnvelopeContract = envelopeContractText entry.EnvelopeContract
            JsonMode = unionName entry.Features.JsonMode
            Schema = unionName entry.Features.Schema
            Examples = unionName entry.Features.Examples
            Select = unionName entry.Features.Select
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
            | ContractUnsupported -> "unsupported"

        {
            Status = status
            Source = "CommandOutputContract"
            Envelope =
                match entry.EnvelopeContract with
                | JsonModeErrorOnly reason -> $"GraceError only in JSON mode for this release; no success ReturnValue envelope is emitted. {reason}"
                | ConditionalGraceResultEnvelope (_, condition) ->
                    $"GraceReturnValue<T> status envelope for status checks, including unavailable modes with nonzero exit codes. GraceError remains for parser and command execution errors outside that status-check path. {condition}"
                | _ -> "GraceReturnValue<T> on success; GraceError on error. CLI success Properties are emitted as Key/Value entries."
            ReturnValueDisposition = returnValueDispositionText entry.EnvelopeContract
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

    /// Builds command-output contract metadata for incomplete metadata example so automation can rely on stable JSON shapes.
    let private incompleteMetadataExample (entry: CommandContractEntry) =
        {
            Name = "metadata-incomplete"
            Description = "Explicit machine-readable metadata gap for a command that is not schema-ready."
            Document =
                box
                    {|
                        Status =
                            match entry.ReturnValueContract.Status with
                            | ContractUnsupported -> "unsupported"
                            | _ -> "metadata-incomplete"
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
            Registry = registryDocument entry
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
                    | MetadataIncomplete
                    | ContractUnsupported ->
                        [
                            incompleteMetadataExample entry
                            errorExample entry
                        ]
        }

    /// Builds command-output contract metadata for features for so automation can rely on stable JSON shapes.
    let private featuresFor behavior =
        match behavior with
        | UnroutedSourceOnly ->
            { JsonMode = UnsupportedUntilRouted; Schema = UnsupportedUntilRouted; Examples = UnsupportedUntilRouted; Select = UnsupportedUntilRouted }
        | ImmediateJsonErrorOnly ->
            { JsonMode = ExistingBehavior; Schema = FutureInertIntrospection; Examples = FutureInertIntrospection; Select = RequiresMigration }
        | ConditionalCheckStatusEnvelope -> { JsonMode = ExistingBehavior; Schema = ExistingBehavior; Examples = ExistingBehavior; Select = ExistingBehavior }
        | CommonRenderOutputEnvelope -> { JsonMode = ExistingBehavior; Schema = ExistingBehavior; Examples = ExistingBehavior; Select = ExistingBehavior }
        | _ -> { JsonMode = RequiresMigration; Schema = FutureInertIntrospection; Examples = FutureInertIntrospection; Select = FutureReturnValueProjection }

    /// Builds command-output contract metadata for envelope for so automation can rely on stable JSON shapes.
    let private envelopeFor routed behavior dtoDisposition =
        match routed, behavior with
        | false, _ -> SourceOnlyUnsupported "Defined in source but not root-routed for V1."
        | true, CommonRenderOutputEnvelope -> ExistingGraceResultEnvelope dtoDisposition
        | true, ConditionalCheckStatusEnvelope ->
            ConditionalGraceResultEnvelope(
                dtoDisposition,
                "`grace watch --check` supports JSON and --select status output; foreground `grace watch` still returns a JSON error because it is a continuous workflow."
            )
        | true, ImmediateJsonErrorOnly ->
            JsonModeErrorOnly
                "The command is routed, but --output Json is intentionally short-circuited before command execution because watch is a continuous foreground workflow."
        | true, _ -> MigrationRequiredToGraceResultEnvelope dtoDisposition

    /// Builds command-output contract metadata for command identity so automation can rely on stable JSON shapes.
    let internal commandIdentity groupPath commandName = { GroupPath = groupPath; CommandName = commandName }

    /// Builds command-output contract metadata for discover leaf commands so automation can rely on stable JSON shapes.
    let discoverLeafCommands (rootCommand: Command) =
        /// Builds command-output contract metadata for rec so automation can rely on stable JSON shapes.
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

    /// Builds command-output contract metadata for row so automation can rely on stable JSON shapes.
    let private row groupPath commandName routed mutating behavior category executionScope dtoDisposition =
        let identity = commandIdentity groupPath commandName

        let routeDisposition =
            if routed then
                Routed
            else
                SourceOnlyUnrouted "Defined-only reference command; not attached to GraceCommand.rootCommand."

        let envelopeContract = envelopeFor routed behavior dtoDisposition

        {
            Identity = identity
            RouteDisposition = routeDisposition
            CurrentJsonBehavior = behavior
            Category = category
            ExecutionScope = executionScope
            Mutating = mutating
            EnvelopeContract = envelopeContract
            Features = featuresFor behavior
            ReturnValueContract = returnValueContractFor identity envelopeContract
        }

    let private common_renderOutput_envelope = CommonRenderOutputEnvelope
    let private immediate_json_error_only = ImmediateJsonErrorOnly
    let private conditional_check_status_envelope = ConditionalCheckStatusEnvelope
    let private human_progress_only_success = HumanProgressOnlySuccess
    let private partial_manual_success = PartialManualSuccess
    let private manual_json_unenveloped = ManualJsonUnenveloped
    let private human_proc_only = HumanProcOnly
    let private human_only = HumanOnly
    let private unrouted_source_only = UnroutedSourceOnly

    let private progress_local_workflow = ProgressLocalWorkflow
    let private mutating_state_transition = MutatingStateTransition
    let private read_or_mutating_verify = ReadOrMutatingVerify
    let private read_list_search = ReadListSearch
    let private mutating = Mutating
    let private fire_and_forget_progress = FireAndForgetProgress
    let private workflow_accepted_operation = WorkflowAcceptedOperation
    let private help_introspection = HelpIntrospection

    let private composite_local_server = CompositeLocalAndServer
    let private local_client = LocalClient
    let private server_via_sdk = ServerViaSdk
    let private verify = Verify
    let private server_via_sdk_defined_but_not_root_routed = ServerViaSdkDefinedButNotRootRouted

    let entries =
        [
            row [ "authorize" ] "can" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "authorize" ] "check" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "authorize" ] "grant-role" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "authorize" ] "list-path-permissions" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "authorize" ] "list-role-assignments" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "authorize" ] "list-roles" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row
                [ "authorize" ]
                "remove-path-permission"
                true
                true
                common_renderOutput_envelope
                mutating_state_transition
                server_via_sdk
                ReuseExistingApiOrSdkDto
            row [ "authorize" ] "revoke-role" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "authorize" ] "show" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row
                [ "authorize" ]
                "upsert-path-permission"
                true
                true
                common_renderOutput_envelope
                mutating_state_transition
                server_via_sdk
                ReuseExistingApiOrSdkDto
            row [ "admin"; "reminder" ] "create" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "admin"; "reminder" ] "delete" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "admin"; "reminder" ] "get" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "admin"; "reminder" ] "list" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "admin"; "reminder" ] "reschedule" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "admin"; "reminder" ] "update-time" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "agent" ] "add-summary" true true common_renderOutput_envelope mutating_state_transition verify ReuseExistingApiOrSdkDto
            row [ "agent" ] "bootstrap" true true common_renderOutput_envelope mutating_state_transition composite_local_server ReuseExistingApiOrSdkDto
            row [ "agent"; "work" ] "start" true true common_renderOutput_envelope mutating_state_transition composite_local_server ReuseExistingApiOrSdkDto
            row [ "agent"; "work" ] "status" true false common_renderOutput_envelope read_list_search composite_local_server ReuseExistingApiOrSdkDto
            row [ "agent"; "work" ] "stop" true true common_renderOutput_envelope mutating_state_transition composite_local_server ReuseExistingApiOrSdkDto
            row [ "alias" ] "list" true false common_renderOutput_envelope help_introspection local_client RequiresCliDto
            row [ "approval"; "policy" ] "create" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "approval"; "policy" ] "delete" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "approval"; "policy" ] "disable" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "approval"; "policy" ] "enable" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "approval"; "policy" ] "evaluate" true false common_renderOutput_envelope read_or_mutating_verify server_via_sdk ReuseExistingApiOrSdkDto
            row [ "approval"; "policy" ] "list" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "approval"; "policy" ] "show" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "approval"; "policy" ] "update" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "approval"; "request" ] "approve" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "approval"; "request" ] "history" true true common_renderOutput_envelope workflow_accepted_operation server_via_sdk ReuseExistingApiOrSdkDto
            row [ "approval"; "request" ] "list" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "approval"; "request" ] "reject" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "approval"; "request" ] "show" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "approval"; "request" ] "wait" true true common_renderOutput_envelope workflow_accepted_operation server_via_sdk ReuseExistingApiOrSdkDto
            row [ "authenticate" ] "login" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "authenticate" ] "logout" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "authenticate" ] "status" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "authenticate"; "token" ] "clear" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "authenticate"; "token" ] "create" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "authenticate"; "token" ] "list" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "authenticate"; "token" ] "revoke" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "authenticate"; "token" ] "set" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "authenticate"; "token" ] "status" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "authenticate" ] "whoami" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "branch" ] "annotate" true true common_renderOutput_envelope mutating_state_transition composite_local_server ReuseExistingApiOrSdkDto
            row [ "branch" ] "assign" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "branch" ] "checkpoint" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "branch" ] "commit" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "branch" ] "create" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "branch" ] "create-external" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "branch" ] "delete" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "branch" ] "enable-assign" true false common_renderOutput_envelope read_or_mutating_verify server_via_sdk ReuseExistingApiOrSdkDto
            row [ "branch" ] "enable-auto-rebase" true false common_renderOutput_envelope read_or_mutating_verify server_via_sdk ReuseExistingApiOrSdkDto
            row [ "branch" ] "enable-checkpoints" true false common_renderOutput_envelope read_or_mutating_verify server_via_sdk ReuseExistingApiOrSdkDto
            row [ "branch" ] "enable-commit" true false common_renderOutput_envelope read_or_mutating_verify server_via_sdk ReuseExistingApiOrSdkDto
            row [ "branch" ] "enable-external" true false common_renderOutput_envelope read_or_mutating_verify server_via_sdk ReuseExistingApiOrSdkDto
            row [ "branch" ] "enable-promotion" true false common_renderOutput_envelope read_or_mutating_verify server_via_sdk ReuseExistingApiOrSdkDto
            row [ "branch" ] "enable-save" true false common_renderOutput_envelope read_or_mutating_verify server_via_sdk ReuseExistingApiOrSdkDto
            row [ "branch" ] "enable-tag" true false common_renderOutput_envelope read_or_mutating_verify server_via_sdk ReuseExistingApiOrSdkDto
            row [ "branch" ] "get" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "branch" ] "get-checkpoints" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "branch" ] "get-commits" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "branch" ] "get-externals" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "branch" ] "get-promotions" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "branch" ] "get-recursive-size" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "branch" ] "get-references" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "branch" ] "get-saves" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "branch" ] "get-tags" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "branch" ] "list-contents" true false common_renderOutput_envelope read_list_search composite_local_server ReuseExistingApiOrSdkDto
            row [ "branch" ] "promote" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "branch" ] "rebase" true true human_progress_only_success progress_local_workflow composite_local_server RequiresCliDto
            row [ "branch" ] "save" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "branch" ] "set-name" true false common_renderOutput_envelope read_or_mutating_verify server_via_sdk ReuseExistingApiOrSdkDto
            row [ "branch" ] "set-promotion-mode" true false common_renderOutput_envelope read_or_mutating_verify server_via_sdk ReuseExistingApiOrSdkDto
            row [ "branch" ] "status" true false human_progress_only_success read_list_search composite_local_server RequiresCliDto
            row [ "branch" ] "switch" true true human_progress_only_success progress_local_workflow composite_local_server RequiresCliDto
            row [ "branch" ] "tag" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "branch" ] "update-parent-branch" true false common_renderOutput_envelope read_or_mutating_verify server_via_sdk ReuseExistingApiOrSdkDto
            row [ "candidate" ] "attestations" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "candidate" ] "cancel" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "candidate"; "gate" ] "rerun" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "candidate" ] "get" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "candidate" ] "required-actions" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "candidate" ] "retry" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "config" ] "write" true false common_renderOutput_envelope read_or_mutating_verify local_client ReuseExistingApiOrSdkDto
            row [] "connect" true true common_renderOutput_envelope progress_local_workflow composite_local_server RequiresCliDto
            row [ "diff" ] "blake3" true true common_renderOutput_envelope progress_local_workflow composite_local_server ReuseExistingApiOrSdkDto
            row [ "diff" ] "checkpoint" true true human_progress_only_success progress_local_workflow composite_local_server RequiresCliDto
            row [ "diff" ] "commit" true true human_progress_only_success progress_local_workflow composite_local_server RequiresCliDto
            row [ "diff" ] "directoryid" true true human_progress_only_success progress_local_workflow composite_local_server RequiresCliDto
            row [ "diff" ] "promotion" true true human_progress_only_success progress_local_workflow composite_local_server RequiresCliDto
            row [ "diff" ] "save" true true human_progress_only_success progress_local_workflow composite_local_server RequiresCliDto
            row [ "diff" ] "sha" true true human_progress_only_success progress_local_workflow composite_local_server RequiresCliDto
            row [ "diff" ] "tag" true true human_progress_only_success progress_local_workflow composite_local_server RequiresCliDto
            row [ "directory-version" ] "get-zip-file" true true common_renderOutput_envelope progress_local_workflow composite_local_server RequiresCliDto
            row [ "history" ] "delete" true true common_renderOutput_envelope mutating local_client RequiresCliDto
            row [ "history" ] "off" true true common_renderOutput_envelope mutating local_client RequiresCliDto
            row [ "history" ] "on" true true common_renderOutput_envelope mutating local_client RequiresCliDto
            row [ "history" ] "run" true true human_proc_only fire_and_forget_progress local_client RequiresCliDto
            row [ "history" ] "search" true false common_renderOutput_envelope read_list_search local_client RequiresCliDto
            row [ "history" ] "show" true false common_renderOutput_envelope read_list_search local_client RequiresCliDto
            row [] "doctor" true false common_renderOutput_envelope read_list_search local_client RequiresCliDto
            row [ "maintenance" ] "check-ignore-entries" true false common_renderOutput_envelope read_list_search local_client RequiresCliDto
            row [ "maintenance" ] "clear-journal" true true common_renderOutput_envelope mutating local_client RequiresCliDto
            row [ "maintenance" ] "list-contents" true false common_renderOutput_envelope read_list_search local_client RequiresCliDto
            row [ "maintenance" ] "scan" true true common_renderOutput_envelope progress_local_workflow local_client RequiresCliDto
            row [ "maintenance" ] "show-journal" true false common_renderOutput_envelope read_list_search local_client RequiresCliDto
            row [ "maintenance" ] "stats" true false common_renderOutput_envelope read_list_search local_client RequiresCliDto
            row [ "maintenance" ] "update-index" true true common_renderOutput_envelope progress_local_workflow local_client RequiresCliDto
            row [ "organization" ] "create" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "organization" ] "delete" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "organization" ] "get" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "organization" ] "set-description" true false common_renderOutput_envelope read_or_mutating_verify server_via_sdk ReuseExistingApiOrSdkDto
            row [ "organization" ] "set-name" true false common_renderOutput_envelope read_or_mutating_verify server_via_sdk ReuseExistingApiOrSdkDto
            row
                [ "organization" ]
                "set-search-visibility"
                true
                false
                common_renderOutput_envelope
                read_or_mutating_verify
                server_via_sdk
                ReuseExistingApiOrSdkDto
            row [ "organization" ] "set-type" true false common_renderOutput_envelope read_or_mutating_verify server_via_sdk ReuseExistingApiOrSdkDto
            row [ "organization" ] "undelete" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "owner" ] "create" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "owner" ] "delete" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "owner" ] "get" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "owner" ] "set-description" true false common_renderOutput_envelope read_or_mutating_verify server_via_sdk ReuseExistingApiOrSdkDto
            row [ "owner" ] "set-name" true false common_renderOutput_envelope read_or_mutating_verify server_via_sdk ReuseExistingApiOrSdkDto
            row [ "owner" ] "set-search-visibility" true false common_renderOutput_envelope read_or_mutating_verify server_via_sdk ReuseExistingApiOrSdkDto
            row [ "owner" ] "set-type" true false common_renderOutput_envelope read_or_mutating_verify server_via_sdk ReuseExistingApiOrSdkDto
            row [ "owner" ] "undelete" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "promotion-set" ] "apply" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row
                [ "promotion-set"; "conflicts" ]
                "resolve"
                true
                true
                common_renderOutput_envelope
                mutating_state_transition
                server_via_sdk
                ReuseExistingApiOrSdkDto
            row [ "promotion-set"; "conflicts" ] "show" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "promotion-set" ] "create" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "promotion-set" ] "delete" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "promotion-set" ] "get" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "promotion-set" ] "get-events" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "promotion-set" ] "list" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "promotion-set" ] "recompute" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "promotion-set" ] "request-approval" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "promotion-set" ] "show" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row
                [ "promotion-set" ]
                "update-input-promotions"
                true
                false
                common_renderOutput_envelope
                read_or_mutating_verify
                server_via_sdk
                ReuseExistingApiOrSdkDto
            row [ "queue" ] "dequeue" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "queue" ] "enqueue" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "queue" ] "pause" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "queue" ] "resume" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "queue" ] "status" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "reference" ] "assign" false true unrouted_source_only mutating_state_transition server_via_sdk_defined_but_not_root_routed NoServerDto
            row [ "reference" ] "checkpoint" false true unrouted_source_only mutating_state_transition server_via_sdk_defined_but_not_root_routed NoServerDto
            row [ "reference" ] "commit" false true unrouted_source_only mutating_state_transition server_via_sdk_defined_but_not_root_routed NoServerDto
            row
                [ "reference" ]
                "create-external"
                false
                true
                unrouted_source_only
                mutating_state_transition
                server_via_sdk_defined_but_not_root_routed
                NoServerDto
            row [ "reference" ] "delete" false true unrouted_source_only mutating_state_transition server_via_sdk_defined_but_not_root_routed NoServerDto
            row [ "reference" ] "get" false false unrouted_source_only read_list_search server_via_sdk_defined_but_not_root_routed NoServerDto
            row [ "reference" ] "promote" false true unrouted_source_only mutating_state_transition server_via_sdk_defined_but_not_root_routed NoServerDto
            row [ "reference" ] "save" false true unrouted_source_only mutating_state_transition server_via_sdk_defined_but_not_root_routed NoServerDto
            row [ "reference" ] "tag" false true unrouted_source_only mutating_state_transition server_via_sdk_defined_but_not_root_routed NoServerDto
            row [ "repository" ] "create" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "repository" ] "delete" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "repository" ] "get" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "repository" ] "get-branches" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "repository" ] "init" true true common_renderOutput_envelope progress_local_workflow composite_local_server RequiresCliDto
            row
                [ "repository" ]
                "set-allows-large-files"
                true
                false
                common_renderOutput_envelope
                read_or_mutating_verify
                server_via_sdk
                ReuseExistingApiOrSdkDto
            row [ "repository" ] "set-anonymous-access" true false common_renderOutput_envelope read_or_mutating_verify server_via_sdk ReuseExistingApiOrSdkDto
            row [ "repository" ] "set-checkpoint-days" true false common_renderOutput_envelope read_or_mutating_verify server_via_sdk ReuseExistingApiOrSdkDto
            row
                [ "repository" ]
                "set-conflict-resolution-policy"
                true
                false
                common_renderOutput_envelope
                read_or_mutating_verify
                server_via_sdk
                ReuseExistingApiOrSdkDto
            row
                [ "repository" ]
                "set-default-server-api-version"
                true
                false
                common_renderOutput_envelope
                read_or_mutating_verify
                server_via_sdk
                ReuseExistingApiOrSdkDto
            row [ "repository" ] "set-description" true false common_renderOutput_envelope read_or_mutating_verify server_via_sdk ReuseExistingApiOrSdkDto
            row [ "repository" ] "set-diff-cache-days" true false common_renderOutput_envelope read_or_mutating_verify server_via_sdk ReuseExistingApiOrSdkDto
            row
                [ "repository" ]
                "set-directory-version-cache-days"
                true
                false
                common_renderOutput_envelope
                read_or_mutating_verify
                server_via_sdk
                ReuseExistingApiOrSdkDto
            row
                [ "repository" ]
                "set-logical-delete-days"
                true
                false
                common_renderOutput_envelope
                read_or_mutating_verify
                server_via_sdk
                ReuseExistingApiOrSdkDto
            row [ "repository" ] "set-name" true false common_renderOutput_envelope read_or_mutating_verify server_via_sdk ReuseExistingApiOrSdkDto
            row [ "repository" ] "set-record-saves" true false common_renderOutput_envelope read_or_mutating_verify server_via_sdk ReuseExistingApiOrSdkDto
            row [ "repository" ] "set-save-days" true false common_renderOutput_envelope read_or_mutating_verify server_via_sdk ReuseExistingApiOrSdkDto
            row [ "repository" ] "set-status" true false common_renderOutput_envelope read_or_mutating_verify server_via_sdk ReuseExistingApiOrSdkDto
            row [ "repository" ] "set-visibility" true false common_renderOutput_envelope read_or_mutating_verify server_via_sdk ReuseExistingApiOrSdkDto
            row [ "repository" ] "undelete" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "review" ] "checkpoint" true true common_renderOutput_envelope mutating_state_transition verify ReuseExistingApiOrSdkDto
            row [ "review" ] "deepen" true true common_renderOutput_envelope mutating_state_transition verify ReuseExistingApiOrSdkDto
            row [ "review" ] "inbox" true false common_renderOutput_envelope read_list_search verify ReuseExistingApiOrSdkDto
            row [ "review" ] "open" true true common_renderOutput_envelope mutating_state_transition verify ReuseExistingApiOrSdkDto
            row [ "review"; "report" ] "export" true false common_renderOutput_envelope read_list_search composite_local_server RequiresCliDto
            row [ "review"; "report" ] "show" true false common_renderOutput_envelope read_list_search composite_local_server RequiresCliDto
            row [ "review" ] "resolve" true true common_renderOutput_envelope mutating_state_transition verify ReuseExistingApiOrSdkDto
            row [] "watch" true true conditional_check_status_envelope progress_local_workflow local_client RequiresCliDto
            row [ "webhook" ] "create" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "webhook" ] "delete" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "webhook" ] "deliveries" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "webhook"; "delivery" ] "show" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "webhook" ] "disable" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "webhook" ] "enable" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "webhook" ] "list" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "webhook" ] "show" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "webhook" ] "test" true false common_renderOutput_envelope read_or_mutating_verify server_via_sdk ReuseExistingApiOrSdkDto
            row [ "webhook" ] "update" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "workitem"; "attachments" ] "add" true true common_renderOutput_envelope mutating_state_transition composite_local_server RequiresCliDto
            row [ "workitem"; "attachments" ] "delete" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "workitem"; "attachments" ] "download" true true common_renderOutput_envelope progress_local_workflow composite_local_server RequiresCliDto
            row [ "workitem"; "attachments" ] "list" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "workitem"; "attachments" ] "show" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row
                [ "workitem"; "attachments" ]
                "undelete"
                true
                true
                common_renderOutput_envelope
                mutating_state_transition
                server_via_sdk
                ReuseExistingApiOrSdkDto
            row [ "workitem" ] "create" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "workitem"; "description" ] "clear" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "workitem"; "description" ] "set" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "workitem"; "link" ] "prset" true true common_renderOutput_envelope read_or_mutating_verify server_via_sdk ReuseExistingApiOrSdkDto
            row [ "workitem"; "link" ] "ref" true true common_renderOutput_envelope read_or_mutating_verify server_via_sdk ReuseExistingApiOrSdkDto
            row [ "workitem"; "links" ] "list" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row
                [ "workitem"; "links"; "remove" ]
                "prset"
                true
                false
                common_renderOutput_envelope
                read_or_mutating_verify
                server_via_sdk
                ReuseExistingApiOrSdkDto
            row [ "workitem"; "links"; "remove" ] "ref" true false common_renderOutput_envelope read_or_mutating_verify server_via_sdk ReuseExistingApiOrSdkDto
            row [ "workitem" ] "show" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "workitem" ] "set-status" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
        ]

    /// Tries to map find and returns a GraceError instead of throwing on unsupported input.
    let tryFind identity =
        entries
        |> List.tryFind (fun entry -> entry.Identity = identity)

    let routedEntries =
        entries
        |> List.filter (fun entry ->
            match entry.RouteDisposition with
            | Routed -> true
            | SourceOnlyUnrouted _ -> false)

    let sourceOnlyEntries =
        entries
        |> List.filter (fun entry ->
            match entry.RouteDisposition with
            | Routed -> false
            | SourceOnlyUnrouted _ -> true)
