namespace Grace.CLI

open System
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
        }

    let private featuresFor behavior =
        match behavior with
        | UnroutedSourceOnly ->
            { JsonMode = UnsupportedUntilRouted; Schema = UnsupportedUntilRouted; Examples = UnsupportedUntilRouted; Select = UnsupportedUntilRouted }
        | CommonRenderOutputEnvelope ->
            { JsonMode = ExistingBehavior; Schema = FutureInertIntrospection; Examples = FutureInertIntrospection; Select = FutureReturnValueProjection }
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
        let routeDisposition =
            if routed then
                Routed
            else
                SourceOnlyUnrouted "Defined-only reference command; not attached to GraceCommand.rootCommand."

        {
            Identity = commandIdentity groupPath commandName
            RouteDisposition = routeDisposition
            CurrentJsonBehavior = behavior
            Category = category
            ExecutionScope = executionScope
            Mutating = mutating
            EnvelopeContract = envelopeFor routed behavior dtoDisposition
            Features = featuresFor behavior
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
            row [ "alias" ] "list" true false human_only help_introspection local_client RequiresCliDto
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
            row [] "connect" true true partial_manual_success progress_local_workflow composite_local_server RequiresCliDto
            row [ "diff" ] "checkpoint" true true human_progress_only_success progress_local_workflow composite_local_server RequiresCliDto
            row [ "diff" ] "commit" true true human_progress_only_success progress_local_workflow composite_local_server RequiresCliDto
            row [ "diff" ] "directoryid" true true human_progress_only_success progress_local_workflow composite_local_server RequiresCliDto
            row [ "diff" ] "promotion" true true human_progress_only_success progress_local_workflow composite_local_server RequiresCliDto
            row [ "diff" ] "save" true true human_progress_only_success progress_local_workflow composite_local_server RequiresCliDto
            row [ "diff" ] "sha" true true human_progress_only_success progress_local_workflow composite_local_server RequiresCliDto
            row [ "diff" ] "tag" true true human_progress_only_success progress_local_workflow composite_local_server RequiresCliDto
            row [ "directory-version" ] "get-zip-file" true true partial_manual_success progress_local_workflow composite_local_server RequiresCliDto
            row [ "history" ] "delete" true true manual_json_unenveloped mutating local_client RequiresCliDto
            row [ "history" ] "off" true true manual_json_unenveloped mutating local_client RequiresCliDto
            row [ "history" ] "on" true true manual_json_unenveloped mutating local_client RequiresCliDto
            row [ "history" ] "run" true true human_proc_only fire_and_forget_progress local_client RequiresCliDto
            row [ "history" ] "search" true false manual_json_unenveloped read_list_search local_client RequiresCliDto
            row [ "history" ] "show" true false manual_json_unenveloped read_list_search local_client RequiresCliDto
            row [ "maintenance" ] "check-ignore-entries" true false human_progress_only_success read_list_search local_client RequiresCliDto
            row [ "maintenance" ] "list-contents" true false human_progress_only_success read_list_search local_client RequiresCliDto
            row [ "maintenance" ] "scan" true true human_progress_only_success progress_local_workflow local_client RequiresCliDto
            row [ "maintenance" ] "stats" true false human_progress_only_success read_list_search local_client RequiresCliDto
            row [ "maintenance" ] "update-index" true true human_progress_only_success progress_local_workflow local_client RequiresCliDto
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
            row [ "repository" ] "init" true true partial_manual_success progress_local_workflow composite_local_server RequiresCliDto
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
            row [ "review"; "report" ] "export" true false partial_manual_success read_list_search composite_local_server RequiresCliDto
            row [ "review"; "report" ] "show" true false partial_manual_success read_list_search composite_local_server RequiresCliDto
            row [ "review" ] "resolve" true true common_renderOutput_envelope mutating_state_transition verify ReuseExistingApiOrSdkDto
            row [] "watch" true true human_progress_only_success progress_local_workflow local_client RequiresCliDto
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
            row [ "workitem"; "attachments" ] "download" true true partial_manual_success progress_local_workflow composite_local_server RequiresCliDto
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
