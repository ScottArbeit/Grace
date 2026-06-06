namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.CommandOutputContract
open NUnit.Framework
open System
open System.Text.Json

[<TestFixture>]
[<NonParallelizable>]
module CommandOutputContractRegistryTests =

    let private commandId (identity: CommandIdentity) = identity.CommandId

    let private countBy behavior =
        CommandOutputContract.entries
        |> List.filter (fun entry -> entry.CurrentJsonBehavior = behavior)
        |> List.length

    let private assertSetEqual expected actual =
        let expectedSet = Set.ofSeq expected
        let actualSet = Set.ofSeq actual

        if expectedSet <> actualSet then
            let missing =
                Set.difference expectedSet actualSet
                |> String.concat ", "

            let extra =
                Set.difference actualSet expectedSet
                |> String.concat ", "

            Assert.Fail($"Set mismatch. Missing: {missing}. Extra: {extra}.")

    [<Test>]
    let ``registry contains accepted inventory totals`` () =
        CommandOutputContract.entries.Length
        |> should equal 201

        CommandOutputContract.routedEntries.Length
        |> should equal 192

        CommandOutputContract.sourceOnlyEntries.Length
        |> should equal 9

        countBy CommonRenderOutputEnvelope
        |> should equal 163

        countBy HumanProgressOnlySuccess
        |> should equal 16

        countBy PartialManualSuccess |> should equal 6
        countBy ManualJsonUnenveloped |> should equal 5
        countBy HumanProcOnly |> should equal 1
        countBy HumanOnly |> should equal 1
        countBy UnroutedSourceOnly |> should equal 9

    [<Test>]
    let ``every live routed leaf command has one registry entry`` () =
        let discovered =
            CommandOutputContract.discoverLeafCommands GraceCommand.rootCommand
            |> List.map commandId

        let registered =
            CommandOutputContract.routedEntries
            |> List.map (fun entry -> entry.Identity.CommandId)

        discovered.Length |> should equal 192

        discovered.Length
        |> should equal (discovered |> List.distinct |> List.length)

        registered.Length
        |> should equal (registered |> List.distinct |> List.length)

        assertSetEqual discovered registered

    [<Test>]
    let ``source-only entries are explicit and unsupported`` () =
        let sourceOnlyIds =
            CommandOutputContract.sourceOnlyEntries
            |> List.map (fun entry -> entry.Identity.CommandId)

        sourceOnlyIds
        |> should
            equal
            [
                "reference.assign"
                "reference.checkpoint"
                "reference.commit"
                "reference.create-external"
                "reference.delete"
                "reference.get"
                "reference.promote"
                "reference.save"
                "reference.tag"
            ]

        for entry in CommandOutputContract.sourceOnlyEntries do
            entry.CurrentJsonBehavior
            |> should equal UnroutedSourceOnly

            match entry.RouteDisposition, entry.EnvelopeContract with
            | SourceOnlyUnrouted _, SourceOnlyUnsupported _ -> ()
            | other -> Assert.Fail($"Expected source-only unsupported disposition for {entry.Identity.CommandId}, got {other}.")

            entry.Features.JsonMode
            |> should equal UnsupportedUntilRouted

            entry.Features.Schema
            |> should equal UnsupportedUntilRouted

            entry.Features.Examples
            |> should equal UnsupportedUntilRouted

            entry.Features.Select
            |> should equal UnsupportedUntilRouted

    [<Test>]
    let ``routed backlog categories are represented`` () =
        let behaviors =
            CommandOutputContract.routedEntries
            |> List.map (fun entry -> entry.CurrentJsonBehavior)
            |> Set.ofList

        behaviors.Contains HumanOnly |> should equal true

        behaviors.Contains HumanProgressOnlySuccess
        |> should equal true

        behaviors.Contains ManualJsonUnenveloped
        |> should equal true

        behaviors.Contains PartialManualSuccess
        |> should equal true

        behaviors.Contains HumanProcOnly
        |> should equal true

    [<Test>]
    let ``registry metadata is consumable without invoking handlers`` () =
        let identity = CommandOutputContract.commandIdentity [] "connect"

        match CommandOutputContract.tryFind identity with
        | Some entry ->
            entry.Identity.CommandId |> should equal "connect"

            entry.CurrentJsonBehavior
            |> should equal PartialManualSuccess

            entry.EnvelopeContract
            |> should equal (MigrationRequiredToGraceResultEnvelope RequiresCliDto)

            entry.Features.JsonMode
            |> should equal RequiresMigration

            entry.Features.Schema
            |> should equal FutureInertIntrospection

            entry.Features.Examples
            |> should equal FutureInertIntrospection

            entry.Features.Select
            |> should equal FutureReturnValueProjection
        | None -> Assert.Fail("connect should have a registry entry.")

    [<Test>]
    let ``current common renderer entries keep the Grace envelope model`` () =
        let commonEntries =
            CommandOutputContract.entries
            |> List.filter (fun entry -> entry.CurrentJsonBehavior = CommonRenderOutputEnvelope)

        commonEntries.Length |> should equal 163

        for entry in commonEntries do
            match entry.EnvelopeContract with
            | ExistingGraceResultEnvelope ReuseExistingApiOrSdkDto -> ()
            | other -> Assert.Fail($"Expected existing Grace result envelope metadata for {entry.Identity.CommandId}, got {other}.")

            entry.Features.Select
            |> should equal ExistingBehavior

    [<Test>]
    let ``schema ready registry entries describe success and error envelopes`` () =
        let identity = CommandOutputContract.commandIdentity [ "auth" ] "logout"

        match CommandOutputContract.tryFind identity with
        | Some entry ->
            let document = CommandOutputContract.introspectionDocument Schema entry

            document.Kind |> should equal "schema"

            document.Command.Id |> should equal "auth.logout"

            match document.Schema with
            | Some schema ->
                schema.Status |> should equal "schema-ready"

                schema.ReturnValueContract
                |> should equal "string"

                use successSchema = JsonDocument.Parse(Grace.Shared.Utilities.serialize schema.SuccessSchema)
                let successRoot = successSchema.RootElement

                successRoot.GetProperty("title").GetString()
                |> should equal "GraceReturnValue<string>"

                let properties = successRoot.GetProperty("properties")

                properties
                    .GetProperty("ReturnValue")
                    .GetProperty("type")
                    .GetString()
                |> should equal "string"

                properties.GetProperty("ReturnValue").ValueKind
                |> should equal JsonValueKind.Object

                properties
                    .GetProperty("EventTime")
                    .GetProperty("type")
                    .GetString()
                |> should equal "string"

                properties
                    .GetProperty("CorrelationId")
                    .GetProperty("type")
                    .GetString()
                |> should equal "string"

                properties
                    .GetProperty("Properties")
                    .GetProperty("type")
                    .GetString()
                |> should equal "array"

                use errorSchema = JsonDocument.Parse(Grace.Shared.Utilities.serialize schema.ErrorSchema)

                errorSchema
                    .RootElement
                    .GetProperty("title")
                    .GetString()
                |> should equal "GraceError"

                errorSchema
                    .RootElement
                    .GetProperty("properties")
                    .GetProperty("Error")
                    .GetProperty("type")
                    .GetString()
                |> should equal "string"
            | None -> Assert.Fail("Schema introspection should include a schema document.")
        | None -> Assert.Fail("auth.logout should have a registry entry.")

    [<Test>]
    let ``metadata incomplete registry entries are explicit`` () =
        let identity = CommandOutputContract.commandIdentity [ "repository" ] "init"

        match CommandOutputContract.tryFind identity with
        | Some entry ->
            entry.ReturnValueContract.Status
            |> should equal MetadataIncomplete

            let schemaDocument = CommandOutputContract.introspectionDocument Schema entry

            match schemaDocument.Schema with
            | Some schema ->
                schema.Status
                |> should equal "metadata-incomplete"

                schema.Notes
                |> should
                    contain
                    "This command is routed, but its JSON success path still requires migration before schema/examples can describe the emitted ReturnValue."
            | None -> Assert.Fail("Metadata-incomplete schema introspection should include a schema document.")

            let examplesDocument = CommandOutputContract.introspectionDocument Examples entry
            examplesDocument.Examples.Length |> should equal 2

            examplesDocument.Examples[0].Name
            |> should equal "metadata-incomplete"
        | None -> Assert.Fail("repository.init should have a registry entry.")

    [<Test>]
    let ``dto and union contracts are not schema ready until their full emitted shapes are declared`` () =
        let cases =
            [
                CommandOutputContract.commandIdentity [ "repository" ] "get", "RepositoryDto metadata is incomplete"
                CommandOutputContract.commandIdentity [ "workitem" ] "show", "WorkItemDto metadata is incomplete"
                CommandOutputContract.commandIdentity [ "access" ] "check", "PermissionCheckResult metadata is incomplete"
            ]

        for identity, expectedNote in cases do
            match CommandOutputContract.tryFind identity with
            | Some entry ->
                entry.ReturnValueContract.Status
                |> should equal MetadataIncomplete

                let schemaDocument = CommandOutputContract.introspectionDocument Schema entry

                match schemaDocument.Schema with
                | Some schema ->
                    schema.Status
                    |> should equal "metadata-incomplete"

                    schema.Notes
                    |> List.exists (fun note -> note.Contains(expectedNote, StringComparison.Ordinal))
                    |> should equal true
                | None -> Assert.Fail($"Expected schema document for {identity.CommandId}.")

                let examplesDocument = CommandOutputContract.introspectionDocument Examples entry

                examplesDocument.Examples[0].Name
                |> should equal "metadata-incomplete"
            | None -> Assert.Fail($"{identity.CommandId} should have a registry entry.")

    [<Test>]
    let ``examples for schema ready commands parse as Grace envelopes`` () =
        let identity = CommandOutputContract.commandIdentity [ "auth" ] "logout"

        match CommandOutputContract.tryFind identity with
        | Some entry ->
            let document = CommandOutputContract.introspectionDocument Examples entry

            document.Examples.Length |> should equal 2

            document.Examples[0].Name
            |> should equal "success-envelope-shape"

            document.Examples[1].Name
            |> should equal "error-envelope-shape"

            use success = JsonDocument.Parse(Grace.Shared.Utilities.serialize document.Examples[0].Document)
            let successRoot = success.RootElement

            successRoot.GetProperty("ReturnValue").GetString()
            |> should equal "Signed out."

            let successProperties = successRoot.GetProperty("Properties")

            successProperties.ValueKind
            |> should equal JsonValueKind.Array

            successProperties[0]
                .GetProperty("Key")
                .GetString()
            |> should equal "cli.contractVersion"

            use error = JsonDocument.Parse(Grace.Shared.Utilities.serialize document.Examples[1].Document)
            let errorRoot = error.RootElement

            errorRoot.GetProperty("Error").GetString()
            |> should equal "error message"

            errorRoot.GetProperty("CorrelationId").GetString()
            |> should equal "correlation-id"
        | None -> Assert.Fail("auth.logout should have a registry entry.")
