namespace Grace.CLI

open System
open System.Collections.Generic
open System.CommandLine

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

    /// Models command introspection document values passed between the parser and command output contract handlers.
    type CommandIntrospectionDocument =
        {
            Kind: string
            ContractVersion: string
            Command: CommandIdentityDocument
            Registry: CommandRegistryDocument
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

    /// Constructs the nullable nullable string schema used in generated command-output metadata.
    let private nullableStringSchema description =
        let schema = Dictionary<string, obj>(StringComparer.Ordinal)
        schema["type"] <- [| "string"; "null" |]
        schema["description"] <- description
        box schema

    /// Constructs an array schema and attaches the item schema used by repeated command-output fields.
    let private arraySchema itemSchema description =
        let schema = Dictionary<string, obj>(StringComparer.Ordinal)
        schema["type"] <- "array"
        schema["items"] <- itemSchema
        schema["description"] <- description
        box schema

    let private stringDateTimeSchema =
        let schema = Dictionary<string, obj>(StringComparer.Ordinal)
        schema["type"] <- "string"
        schema["format"] <- "date-time"
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

    let private stringReturnValueSchema = scalarSchema "string"

    let private maintenanceStatsSchema =
        schemaObject
            "MaintenanceStatsDto"
            [
                "DirectoryCount", scalarSchema "integer"
                "FileCount", scalarSchema "integer"
                "TotalFileSize", scalarSchema "integer"
                "RootSha256Hash", nullableStringSchema "Root directory SHA-256 hash when the local index contains or records it."
                "RootBlake3Hash", nullableStringSchema "Root directory BLAKE3 hash when the local index contains it."
            ]
            [|
                "DirectoryCount"
                "FileCount"
                "TotalFileSize"
                "RootSha256Hash"
                "RootBlake3Hash"
            |]

    let private maintenanceStatsExample =
        box {| DirectoryCount = 1; FileCount = 2; TotalFileSize = 42L; RootSha256Hash = "0123456789abcdef"; RootBlake3Hash = "af1349b9f5f9a1a6" |}

    let private maintenanceListContentsFileSchema =
        schemaObject
            "MaintenanceListContentsFileDto"
            [
                "RelativePath", scalarSchema "string"
                "FileName", scalarSchema "string"
                "Sha256Hash", scalarSchema "string"
                "Blake3Hash", scalarSchema "string"
                "Size", scalarSchema "integer"
                "LastWriteTimeUtc", stringDateTimeSchema
            ]
            [|
                "RelativePath"
                "FileName"
                "Sha256Hash"
                "Blake3Hash"
                "Size"
                "LastWriteTimeUtc"
            |]

    let private maintenanceListContentsDirectorySchema =
        schemaObject
            "MaintenanceListContentsDirectoryDto"
            [
                "RelativePath", scalarSchema "string"
                "DirectoryVersionId", scalarSchema "string"
                "Sha256Hash", scalarSchema "string"
                "Blake3Hash", scalarSchema "string"
                "Size", scalarSchema "integer"
                "LastWriteTimeUtc", stringDateTimeSchema
                "Files", arraySchema maintenanceListContentsFileSchema "Files in the indexed directory when file listing is enabled."
            ]
            [|
                "RelativePath"
                "DirectoryVersionId"
                "Sha256Hash"
                "Blake3Hash"
                "Size"
                "LastWriteTimeUtc"
                "Files"
            |]

    let private maintenanceListContentsSchema =
        schemaObject
            "MaintenanceListContentsDto"
            [
                "Summary", maintenanceStatsSchema
                "Directories", arraySchema maintenanceListContentsDirectorySchema "Indexed directories returned by maintenance list-contents."
            ]
            [| "Summary"; "Directories" |]

    let private maintenanceListContentsExample =
        box
            {|
                Summary = {| DirectoryCount = 1; FileCount = 1; TotalFileSize = 12L; RootSha256Hash = "0123456789abcdef"; RootBlake3Hash = "af1349b9f5f9a1a6" |}
                Directories =
                    [|
                        {|
                            RelativePath = "."
                            DirectoryVersionId = "11111111-1111-1111-1111-111111111111"
                            Sha256Hash = "0123456789abcdef"
                            Blake3Hash = "af1349b9f5f9a1a6"
                            Size = 12L
                            LastWriteTimeUtc = "2026-06-05T00:00:00Z"
                            Files =
                                [|
                                    {|
                                        RelativePath = "README.md"
                                        FileName = "README.md"
                                        Sha256Hash = "abcdef0123456789"
                                        Blake3Hash = "b9f5f9a1a6af1349"
                                        Size = 12L
                                        LastWriteTimeUtc = "2026-06-05T00:00:00Z"
                                    |}
                                |]
                        |}
                    |]
            |}

    let private maintenanceIgnoreEntriesSchema =
        schemaObject
            "MaintenanceIgnoreEntriesDto"
            [
                "DirectoryEntries", arraySchema (scalarSchema "string") "Configured Grace directory ignore entries."
                "FileEntries", arraySchema (scalarSchema "string") "Configured Grace file ignore entries."
            ]
            [| "DirectoryEntries"; "FileEntries" |]

    let private maintenanceIgnoreEntriesExample = box {| DirectoryEntries = [| ".git"; ".grace" |]; FileEntries = [| "*.tmp" |] |}

    let private maintenanceScanDifferenceSchema =
        schemaObject
            "MaintenanceScanDifferenceDto"
            [
                "DifferenceType", scalarSchema "string"
                "FileSystemEntryType", scalarSchema "string"
                "RelativePath", scalarSchema "string"
            ]
            [|
                "DifferenceType"
                "FileSystemEntryType"
                "RelativePath"
            |]

    let private maintenanceScanDirectoryVersionSchema =
        schemaObject
            "MaintenanceScanDirectoryVersionDto"
            [
                "DirectoryVersionId", scalarSchema "string"
                "RelativePath", scalarSchema "string"
                "Sha256Hash", scalarSchema "string"
                "Blake3Hash", scalarSchema "string"
            ]
            [|
                "DirectoryVersionId"
                "RelativePath"
                "Sha256Hash"
                "Blake3Hash"
            |]

    let private maintenanceScanSchema =
        schemaObject
            "MaintenanceScanDto"
            [
                "DifferenceCount", scalarSchema "integer"
                "Differences", arraySchema maintenanceScanDifferenceSchema "Detected filesystem differences compared with the local Grace index."
                "NewDirectoryVersionCount", scalarSchema "integer"
                "NewDirectoryVersions", arraySchema maintenanceScanDirectoryVersionSchema "Computed directory versions for detected differences."
            ]
            [|
                "DifferenceCount"
                "Differences"
                "NewDirectoryVersionCount"
                "NewDirectoryVersions"
            |]

    let private maintenanceScanExample =
        box
            {|
                DifferenceCount = 1
                Differences =
                    [|
                        {| DifferenceType = "Added"; FileSystemEntryType = "File"; RelativePath = "README.md" |}
                    |]
                NewDirectoryVersionCount = 1
                NewDirectoryVersions =
                    [|
                        {|
                            DirectoryVersionId = "11111111-1111-1111-1111-111111111111"
                            RelativePath = "."
                            Sha256Hash = "0123456789abcdef"
                            Blake3Hash = "af1349b9f5f9a1a6"
                        |}
                    |]
            |}

    let private doctorCheckSchema =
        schemaObject
            "DoctorCheckDto"
            [
                "Id", scalarSchema "string"
                "Category", scalarSchema "string"
                "Title", scalarSchema "string"
                "Description", scalarSchema "string"
                "DefaultEnabled", scalarSchema "boolean"
                "SupportsOffline", scalarSchema "boolean"
            ]
            [|
                "Id"
                "Category"
                "Title"
                "Description"
                "DefaultEnabled"
                "SupportsOffline"
            |]

    let private doctorCheckResultSchema =
        schemaObject
            "DoctorCheckResultDto"
            [
                "Id", scalarSchema "string"
                "Category", scalarSchema "string"
                "Title", scalarSchema "string"
                "Status", scalarSchema "string"
                "Severity", scalarSchema "string"
                "Summary", scalarSchema "string"
            ]
            [|
                "Id"
                "Category"
                "Title"
                "Status"
                "Severity"
                "Summary"
            |]

    let private doctorSummarySchema =
        schemaObject
            "DoctorSummaryDto"
            [
                "Total", scalarSchema "integer"
                "Ok", scalarSchema "integer"
                "Warning", scalarSchema "integer"
                "Failed", scalarSchema "integer"
                "Skipped", scalarSchema "integer"
            ]
            [|
                "Total"
                "Ok"
                "Warning"
                "Failed"
                "Skipped"
            |]

    let private doctorReportSchema =
        schemaObject
            "DoctorReportDto"
            [
                "ReportVersion", scalarSchema "string"
                "Status", scalarSchema "string"
                "ExitCode", scalarSchema "integer"
                "Full", scalarSchema "boolean"
                "Offline", scalarSchema "boolean"
                "Strict", scalarSchema "boolean"
                "ListOnly", scalarSchema "boolean"
                "RequestedChecks", arraySchema (scalarSchema "string") "Requested check IDs or categories after token normalization."
                "Catalog", arraySchema doctorCheckSchema "Inert doctor check catalog entries included in the report."
                "Checks", arraySchema doctorCheckResultSchema "Scaffolded check results; no real diagnostics run in this slice."
                "Summary", doctorSummarySchema
            ]
            [|
                "ReportVersion"
                "Status"
                "ExitCode"
                "Full"
                "Offline"
                "Strict"
                "ListOnly"
                "RequestedChecks"
                "Catalog"
                "Checks"
                "Summary"
            |]

    let private doctorReportExample =
        box
            {|
                ReportVersion = "doctor-report-v1"
                Status = "Ok"
                ExitCode = 0
                Full = false
                Offline = false
                Strict = false
                ListOnly = true
                RequestedChecks = [| "cli.catalog" |]
                Catalog =
                    [|
                        {|
                            Id = "cli.catalog"
                            Category = "CLI"
                            Title = "CLI command catalog"
                            Description = "Verifies that the Grace CLI command catalog is available. Scaffold only; no runtime probe is executed."
                            DefaultEnabled = true
                            SupportsOffline = true
                        |}
                    |]
                Checks =
                    [|
                        {|
                            Id = "cli.catalog"
                            Category = "CLI"
                            Title = "CLI command catalog"
                            Status = "Ok"
                            Severity = "Info"
                            Summary = "Scaffolded check only; no diagnostic probe ran in this slice."
                        |}
                    |]
                Summary = {| Total = 1; Ok = 1; Warning = 0; Failed = 0; Skipped = 0 |}
            |}

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
        | MigrationRequiredToGraceResultEnvelope disposition -> $"MigrationRequiredToGraceResultEnvelope: {outputDtoDispositionText disposition}"
        | JsonModeErrorOnly reason -> $"JsonModeErrorOnly: {reason}"
        | SourceOnlyUnsupported reason -> $"SourceOnlyUnsupported: {reason}"

    /// Builds command-output contract metadata for return value disposition text so automation can rely on stable JSON shapes.
    let private returnValueDispositionText (contract: EnvelopeContract) =
        match contract with
        | ExistingGraceResultEnvelope disposition
        | MigrationRequiredToGraceResultEnvelope disposition -> outputDtoDispositionText disposition
        | JsonModeErrorOnly reason -> $"Unsupported: {reason}"
        | SourceOnlyUnsupported reason -> $"Unsupported: {reason}"

    /// Builds command-output contract metadata for return value contract for so automation can rely on stable JSON shapes.
    let private returnValueContractFor (identity: CommandIdentity) (envelopeContract: EnvelopeContract) =
        match identity.CommandId, envelopeContract with
        | "maintenance.check-ignore-entries", ExistingGraceResultEnvelope RequiresCliDto ->
            supportedReturnValueContract
                "MaintenanceIgnoreEntriesDto"
                "Grace.CLI.Command.Common.LocalOutputDto"
                maintenanceIgnoreEntriesSchema
                maintenanceIgnoreEntriesExample
                [
                    "Command-specific CLI DTO emitted by maintenance check-ignore-entries in the common Grace result envelope."
                ]
        | "maintenance.list-contents", ExistingGraceResultEnvelope RequiresCliDto ->
            supportedReturnValueContract
                "MaintenanceListContentsDto"
                "Grace.CLI.Command.Common.LocalOutputDto"
                maintenanceListContentsSchema
                maintenanceListContentsExample
                [
                    "Command-specific CLI DTO emitted by maintenance list-contents in the common Grace result envelope."
                ]
        | "maintenance.scan", ExistingGraceResultEnvelope RequiresCliDto ->
            supportedReturnValueContract
                "MaintenanceScanDto"
                "Grace.CLI.Command.Common.LocalOutputDto"
                maintenanceScanSchema
                maintenanceScanExample
                [
                    "Command-specific CLI DTO emitted by maintenance scan in the common Grace result envelope."
                ]
        | "maintenance.stats", ExistingGraceResultEnvelope RequiresCliDto ->
            supportedReturnValueContract
                "MaintenanceStatsDto"
                "Grace.CLI.Command.Common.LocalOutputDto"
                maintenanceStatsSchema
                maintenanceStatsExample
                [
                    "Command-specific CLI DTO emitted by maintenance stats in the common Grace result envelope."
                ]
        | "maintenance.update-index", ExistingGraceResultEnvelope RequiresCliDto ->
            supportedReturnValueContract
                "MaintenanceStatsDto"
                "Grace.CLI.Command.Common.LocalOutputDto"
                maintenanceStatsSchema
                maintenanceStatsExample
                [
                    "Command-specific CLI DTO emitted by maintenance update-index in the common Grace result envelope after the local index is updated."
                ]
        | "doctor", ExistingGraceResultEnvelope RequiresCliDto ->
            supportedReturnValueContract
                "DoctorReportDto"
                "Grace.CLI.Command.Common.LocalOutputDto"
                doctorReportSchema
                doctorReportExample
                [
                    "Command-specific CLI DTO emitted by grace doctor in the common Grace result envelope."
                    "Doctor schema and examples are intentionally available in the S0 scaffold because later slices add real diagnostics behind the stable DTO."
                ]
        | "repository.get", ExistingGraceResultEnvelope ReuseExistingApiOrSdkDto ->
            incompleteReturnValueContract
                "RepositoryDto"
                "RepositoryDto metadata is incomplete: src/Grace.Types/Repository.Types.fs declares additional emitted fields that are not yet represented in the CLI contract registry."
        | "workitem.show", ExistingGraceResultEnvelope ReuseExistingApiOrSdkDto ->
            incompleteReturnValueContract
                "WorkItemDto"
                "WorkItemDto metadata is incomplete: src/Grace.Types/WorkItem.Types.fs declares additional emitted fields that are not yet represented in the CLI contract registry."
        | ("authorize.can"
          | "authorize.check"),
          ExistingGraceResultEnvelope ReuseExistingApiOrSdkDto ->
            incompleteReturnValueContract
                "PermissionCheckResult"
                "PermissionCheckResult metadata is incomplete: src/Grace.Types/Authorization.Types.fs emits the Allowed/Denied discriminated union, not an object with Allowed and Reason fields."
        | "authenticate.logout", ExistingGraceResultEnvelope ReuseExistingApiOrSdkDto ->
            supportedReturnValueContract
                "string"
                "GraceReturnValue<string>"
                stringReturnValueSchema
                (box "Signed out.")
                [
                    "Representative scalar ReturnValue schema for a common Grace result envelope command."
                ]
        | "watch", JsonModeErrorOnly reason -> unsupportedReturnValueContract "WatchResultDto" reason
        | "cache.status", MigrationRequiredToGraceResultEnvelope RequiresCliDto ->
            incompleteReturnValueContract
                "CacheRuntimeStatus"
                "Cache status is a pure successful observation with redacted Lifecycle, CacheId, and Transport values; Lifecycle includes registered, enrollment-recovery-required, and operator-recovery-required."
        | _, SourceOnlyUnsupported reason -> unsupportedReturnValueContract "unsupported" reason
        | _, JsonModeErrorOnly reason -> unsupportedReturnValueContract "unsupported" reason
        | _, MigrationRequiredToGraceResultEnvelope disposition ->
            incompleteReturnValueContract
                (outputDtoDispositionText disposition)
                "This command is routed, but its JSON success path still requires migration before schema/examples can describe the emitted ReturnValue."
        | _, ExistingGraceResultEnvelope disposition ->
            incompleteReturnValueContract
                (outputDtoDispositionText disposition)
                "The registry has envelope metadata for this command, but command-specific ReturnValue schema/example metadata has not been declared yet."

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
        | CommonRenderOutputEnvelope ->
            { JsonMode = ExistingBehavior; Schema = FutureInertIntrospection; Examples = FutureInertIntrospection; Select = ExistingBehavior }
        | _ -> { JsonMode = RequiresMigration; Schema = FutureInertIntrospection; Examples = FutureInertIntrospection; Select = FutureReturnValueProjection }

    /// Builds command-output contract metadata for envelope for so automation can rely on stable JSON shapes.
    let private envelopeFor routed behavior dtoDisposition =
        match routed, behavior with
        | false, _ -> SourceOnlyUnsupported "Defined in source but not root-routed for V1."
        | true, CommonRenderOutputEnvelope -> ExistingGraceResultEnvelope dtoDisposition
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
            Features =
                if identity.CommandId.Equals("doctor", StringComparison.Ordinal) then
                    { JsonMode = ExistingBehavior; Schema = ExistingBehavior; Examples = ExistingBehavior; Select = ExistingBehavior }
                else
                    featuresFor behavior
            ReturnValueContract = returnValueContractFor identity envelopeContract
        }

    let private common_renderOutput_envelope = CommonRenderOutputEnvelope
    let private immediate_json_error_only = ImmediateJsonErrorOnly
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
            row [ "cache" ] "enroll" true true human_proc_only mutating_state_transition local_client RequiresCliDto
            row [ "cache" ] "run" true true human_proc_only fire_and_forget_progress local_client RequiresCliDto
            row [ "cache" ] "status" true false human_proc_only read_list_search local_client RequiresCliDto
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
            row [] "watch" true true immediate_json_error_only progress_local_workflow local_client RequiresCliDto
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
