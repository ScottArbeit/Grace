namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.CommandOutputContract
open NUnit.Framework
open System

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
