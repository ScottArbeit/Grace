namespace Grace.CLI

open System
open System.Collections.Generic
open System.CommandLine

module CommandOutputContract =

    type CommandIdentity =
        {
            GroupPath: string list
            CommandName: string
        }

        member this.CommandPath = this.GroupPath @ [ this.CommandName ]
        member this.CommandId = String.Join(".", this.CommandPath)
        override this.ToString() = String.Join(" ", this.CommandPath)

    type RouteDisposition =
        | Routed
        | SourceOnlyUnrouted of disposition: string

    type CurrentJsonBehavior =
        | CommonRenderOutputEnvelope
        | HumanProgressOnlySuccess
        | PartialManualSuccess
        | ManualJsonUnenveloped
        | HumanProcOnly
        | HumanOnly
        | UnroutedSourceOnly

    type CommandCategory =
        | ProgressLocalWorkflow
        | MutatingStateTransition
        | ReadOrMutatingVerify
        | ReadListSearch
        | Mutating
        | FireAndForgetProgress
        | WorkflowAcceptedOperation
        | HelpIntrospection

    type ExecutionScope =
        | CompositeLocalAndServer
        | LocalClient
        | ServerViaSdk
        | Verify
        | ServerViaSdkDefinedButNotRootRouted

    type OutputDtoDisposition =
        | ReuseExistingApiOrSdkDto
        | RequiresCliDto
        | NoServerDto

    type EnvelopeContract =
        | ExistingGraceResultEnvelope of dtoDisposition: OutputDtoDisposition
        | MigrationRequiredToGraceResultEnvelope of dtoDisposition: OutputDtoDisposition
        | SourceOnlyUnsupported of disposition: string

    type FeatureState =
        | ExistingBehavior
        | FutureInertIntrospection
        | FutureReturnValueProjection
        | UnsupportedUntilRouted
        | RequiresMigration

    type MachineReadableFeatures = { JsonMode: FeatureState; Schema: FeatureState; Examples: FeatureState; Select: FeatureState }

    type ReturnValueMetadataStatus =
        | SchemaReady
        | MetadataIncomplete
        | ContractUnsupported

    type ReturnValueContract = { Name: string; Provenance: string; Status: ReturnValueMetadataStatus; Schema: obj; Example: obj; Notes: string list }

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

    type IntrospectionKind =
        | Schema
        | Examples

    type CommandIdentityDocument = { Id: string; Path: string list; GroupPath: string list; Name: string }

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

    type CommandExampleDocument = { Name: string; Description: string; Document: obj }

    type CommandIntrospectionDocument =
        {
            Kind: string
            ContractVersion: string
            Command: CommandIdentityDocument
            Registry: CommandRegistryDocument
            Schema: CommandSchemaDocument option
            Examples: CommandExampleDocument list
        }

    let private unionName value = $"{value}"

    let private schemaObject (title: string) (properties: (string * obj) list) (required: string array) =
        let schema = Dictionary<string, obj>(StringComparer.Ordinal)
        schema["$schema"] <- "https://json-schema.org/draft/2020-12/schema"
        schema["title"] <- title
        schema["type"] <- "object"
        schema["required"] <- required
        schema["properties"] <- Dictionary<string, obj>(properties |> Seq.map KeyValuePair)
        box schema

    let private scalarSchema (typeName: string) =
        let schema = Dictionary<string, obj>(StringComparer.Ordinal)
        schema["type"] <- typeName
        box schema

    let private anySchema description =
        let schema = Dictionary<string, obj>(StringComparer.Ordinal)
        schema["description"] <- description
        box schema

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

    let private cliProperties commandId provenance =
        [|
            {| Key = "cli.contractVersion"; Value = "cli-json-v1" |}
            {| Key = "cli.commandId"; Value = commandId |}
            {| Key = "cli.introspectionSource"; Value = provenance |}
        |]

    let private unsupportedReturnValueSchema reason =
        schemaObject
            "Unsupported command output contract"
            [
                "Status", scalarSchema "string"
                "Reason", scalarSchema "string"
            ]
            [| "Status"; "Reason" |]

    let private unsupportedReturnValueExample reason = box {| Status = "metadata-incomplete"; Reason = reason |}

    let private stringReturnValueSchema = scalarSchema "string"

    let private supportedReturnValueContract name provenance schema example notes =
        { Name = name; Provenance = provenance; Status = SchemaReady; Schema = schema; Example = example; Notes = notes }

    let private incompleteReturnValueContract name reason =
        {
            Name = name
            Provenance = "CommandOutputContract"
            Status = MetadataIncomplete
            Schema = unsupportedReturnValueSchema reason
            Example = unsupportedReturnValueExample reason
            Notes = [ reason ]
        }

    let private unsupportedReturnValueContract name reason =
        {
            Name = name
            Provenance = "CommandOutputContract"
            Status = ContractUnsupported
            Schema = unsupportedReturnValueSchema reason
            Example = unsupportedReturnValueExample reason
            Notes = [ reason ]
        }

    let private routeDispositionText (disposition: RouteDisposition) =
        match disposition with
        | Routed -> "Routed"
        | SourceOnlyUnrouted reason -> $"SourceOnlyUnrouted: {reason}"

    let private outputDtoDispositionText (disposition: OutputDtoDisposition) =
        match disposition with
        | ReuseExistingApiOrSdkDto -> "ReuseExistingApiOrSdkDto"
        | RequiresCliDto -> "RequiresCliDto"
        | NoServerDto -> "NoServerDto"

    let private envelopeContractText (contract: EnvelopeContract) =
        match contract with
        | ExistingGraceResultEnvelope disposition -> $"ExistingGraceResultEnvelope: {outputDtoDispositionText disposition}"
        | MigrationRequiredToGraceResultEnvelope disposition -> $"MigrationRequiredToGraceResultEnvelope: {outputDtoDispositionText disposition}"
        | SourceOnlyUnsupported reason -> $"SourceOnlyUnsupported: {reason}"

    let private returnValueDispositionText (contract: EnvelopeContract) =
        match contract with
        | ExistingGraceResultEnvelope disposition
        | MigrationRequiredToGraceResultEnvelope disposition -> outputDtoDispositionText disposition
        | SourceOnlyUnsupported reason -> $"Unsupported: {reason}"

    let private returnValueContractFor (identity: CommandIdentity) (envelopeContract: EnvelopeContract) =
        match identity.CommandId, envelopeContract with
        | "repository.get", ExistingGraceResultEnvelope ReuseExistingApiOrSdkDto ->
            incompleteReturnValueContract
                "RepositoryDto"
                "RepositoryDto metadata is incomplete: src/Grace.Types/Repository.Types.fs declares additional emitted fields that are not yet represented in the CLI contract registry."
        | "workitem.show", ExistingGraceResultEnvelope ReuseExistingApiOrSdkDto ->
            incompleteReturnValueContract
                "WorkItemDto"
                "WorkItemDto metadata is incomplete: src/Grace.Types/WorkItem.Types.fs declares additional emitted fields that are not yet represented in the CLI contract registry."
        | "access.check", ExistingGraceResultEnvelope ReuseExistingApiOrSdkDto ->
            incompleteReturnValueContract
                "PermissionCheckResult"
                "PermissionCheckResult metadata is incomplete: src/Grace.Types/Authorization.Types.fs emits the Allowed/Denied discriminated union, not an object with Allowed and Reason fields."
        | "auth.logout", ExistingGraceResultEnvelope ReuseExistingApiOrSdkDto ->
            supportedReturnValueContract
                "string"
                "GraceReturnValue<string>"
                stringReturnValueSchema
                (box "Signed out.")
                [
                    "Representative scalar ReturnValue schema for a common Grace result envelope command."
                ]
        | _, SourceOnlyUnsupported reason -> unsupportedReturnValueContract "unsupported" reason
        | _, MigrationRequiredToGraceResultEnvelope disposition ->
            incompleteReturnValueContract
                (outputDtoDispositionText disposition)
                "This command is routed, but its JSON success path still requires migration before schema/examples can describe the emitted ReturnValue."
        | _, ExistingGraceResultEnvelope disposition ->
            incompleteReturnValueContract
                (outputDtoDispositionText disposition)
                "The registry has envelope metadata for this command, but command-specific ReturnValue schema/example metadata has not been declared yet."

    let private commandDocument (identity: CommandIdentity) =
        { Id = identity.CommandId; Path = identity.CommandPath; GroupPath = identity.GroupPath; Name = identity.CommandName }

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

    let private schemaDocument (entry: CommandContractEntry) =
        let status =
            match entry.ReturnValueContract.Status with
            | SchemaReady -> "schema-ready"
            | MetadataIncomplete -> "metadata-incomplete"
            | ContractUnsupported -> "unsupported"

        {
            Status = status
            Source = "CommandOutputContract"
            Envelope = "GraceReturnValue<T> on success; GraceError on error. CLI success Properties are emitted as Key/Value entries."
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

    let introspectionDocument (kind: IntrospectionKind) (entry: CommandContractEntry) =
        {
            Kind =
                match kind with
                | Schema -> "schema"
                | Examples -> "examples"
            ContractVersion = "cli-json-v1"
            Command = commandDocument entry.Identity
            Registry = registryDocument entry
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

    let private featuresFor behavior =
        match behavior with
        | UnroutedSourceOnly ->
            { JsonMode = UnsupportedUntilRouted; Schema = UnsupportedUntilRouted; Examples = UnsupportedUntilRouted; Select = UnsupportedUntilRouted }
        | CommonRenderOutputEnvelope ->
            { JsonMode = ExistingBehavior; Schema = FutureInertIntrospection; Examples = FutureInertIntrospection; Select = ExistingBehavior }
        | _ -> { JsonMode = RequiresMigration; Schema = FutureInertIntrospection; Examples = FutureInertIntrospection; Select = FutureReturnValueProjection }

    let private envelopeFor routed behavior dtoDisposition =
        match routed, behavior with
        | false, _ -> SourceOnlyUnsupported "Defined in source but not root-routed for V1."
        | true, CommonRenderOutputEnvelope -> ExistingGraceResultEnvelope dtoDisposition
        | true, _ -> MigrationRequiredToGraceResultEnvelope dtoDisposition

    let internal commandIdentity groupPath commandName = { GroupPath = groupPath; CommandName = commandName }

    let discoverLeafCommands (rootCommand: Command) =
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
            row [ "access" ] "check" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "access" ] "grant-role" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "access" ] "list-path-permissions" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "access" ] "list-role-assignments" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "access" ] "list-roles" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "access" ] "remove-path-permission" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "access" ] "revoke-role" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "access" ] "upsert-path-permission" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
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
            row [ "auth" ] "login" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "auth" ] "logout" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "auth" ] "status" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "auth"; "token" ] "clear" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "auth"; "token" ] "create" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "auth"; "token" ] "list" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "auth"; "token" ] "revoke" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "auth"; "token" ] "set" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "auth"; "token" ] "status" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "auth" ] "whoami" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
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
            row [ "maintenance" ] "check-ignore-entries" true false common_renderOutput_envelope read_list_search local_client RequiresCliDto
            row [ "maintenance" ] "list-contents" true false common_renderOutput_envelope read_list_search local_client RequiresCliDto
            row [ "maintenance" ] "scan" true true common_renderOutput_envelope progress_local_workflow local_client RequiresCliDto
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
            row [] "watch" true true common_renderOutput_envelope progress_local_workflow local_client RequiresCliDto
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
            row [ "workitem"; "attach" ] "notes" true true common_renderOutput_envelope read_or_mutating_verify server_via_sdk ReuseExistingApiOrSdkDto
            row [ "workitem"; "attach" ] "prompt" true true common_renderOutput_envelope read_or_mutating_verify server_via_sdk ReuseExistingApiOrSdkDto
            row [ "workitem"; "attach" ] "summary" true true common_renderOutput_envelope read_or_mutating_verify server_via_sdk ReuseExistingApiOrSdkDto
            row [ "workitem"; "attachments" ] "download" true true common_renderOutput_envelope progress_local_workflow composite_local_server RequiresCliDto
            row [ "workitem"; "attachments" ] "list" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "workitem"; "attachments" ] "show" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "workitem" ] "create" true true common_renderOutput_envelope mutating_state_transition server_via_sdk ReuseExistingApiOrSdkDto
            row [ "workitem"; "link" ] "prset" true true common_renderOutput_envelope read_or_mutating_verify server_via_sdk ReuseExistingApiOrSdkDto
            row [ "workitem"; "link" ] "ref" true true common_renderOutput_envelope read_or_mutating_verify server_via_sdk ReuseExistingApiOrSdkDto
            row [ "workitem"; "links" ] "list" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row
                [ "workitem"; "links"; "remove" ]
                "notes"
                true
                false
                common_renderOutput_envelope
                read_or_mutating_verify
                server_via_sdk
                ReuseExistingApiOrSdkDto
            row
                [ "workitem"; "links"; "remove" ]
                "prompt"
                true
                false
                common_renderOutput_envelope
                read_or_mutating_verify
                server_via_sdk
                ReuseExistingApiOrSdkDto
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
            row
                [ "workitem"; "links"; "remove" ]
                "summary"
                true
                false
                common_renderOutput_envelope
                read_or_mutating_verify
                server_via_sdk
                ReuseExistingApiOrSdkDto
            row [ "workitem" ] "show" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
            row [ "workitem" ] "status" true false common_renderOutput_envelope read_list_search server_via_sdk ReuseExistingApiOrSdkDto
        ]

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
