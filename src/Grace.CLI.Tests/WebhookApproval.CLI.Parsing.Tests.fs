namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open NUnit.Framework
open System

/// Groups webhook approval command parsing coverage for the CLI test project.
[<Parallelizable(ParallelScope.All)>]
module WebhookApprovalCommandParsingTests =
    let private ownerId = Guid.NewGuid()
    let private organizationId = Guid.NewGuid()
    let private repositoryId = Guid.NewGuid()
    let private branchId = Guid.NewGuid()

    /// Runs the supplied action with ids applied.
    let private withIds (args: string array) =
        Array.append
            args
            [|
                "--owner-id"
                ownerId.ToString()
                "--organization-id"
                organizationId.ToString()
                "--repository-id"
                repositoryId.ToString()
            |]

    /// Asserts that parses matches the expected contract.
    let private assertParses (args: string array) =
        let parseResult = GraceCommand.rootCommand.Parse(withIds args)
        parseResult.Errors |> should be Empty

    /// Asserts that does not parse matches the expected contract.
    let private assertDoesNotParse (args: string array) =
        let parseResult = GraceCommand.rootCommand.Parse(args)

        parseResult.Errors.Count
        |> should be (greaterThan 0)

    /// Verifies that webhook command parses required verbs.
    [<TestCase("create")>]
    [<TestCase("list")>]
    [<TestCase("show")>]
    [<TestCase("update")>]
    [<TestCase("enable")>]
    [<TestCase("disable")>]
    [<TestCase("delete")>]
    [<TestCase("test")>]
    [<TestCase("deliveries")>]
    let ``webhook command parses required verbs`` verb =
        let webhookId = Guid.NewGuid().ToString()

        let args =
            match verb with
            | "create" ->
                [|
                    "webhook"
                    "create"
                    "--name"
                    "apply"
                    "--event"
                    "promotion-set.applied"
                    "--url"
                    "https://example.test/webhook"
                |]
            | "list" -> [| "webhook"; "list" |]
            | "show" ->
                [|
                    "webhook"
                    "show"
                    "--webhook"
                    webhookId
                |]
            | "update" ->
                [|
                    "webhook"
                    "update"
                    "--webhook"
                    webhookId
                    "--event"
                    "promotion-set.applied"
                    "--url"
                    "https://example.test/webhook"
                |]
            | "test" ->
                [|
                    "webhook"
                    "test"
                    "--webhook"
                    webhookId
                |]
            | "deliveries" ->
                [|
                    "webhook"
                    "deliveries"
                    "--webhook"
                    webhookId
                |]
            | other ->
                [|
                    "webhook"
                    other
                    "--webhook"
                    webhookId
                |]

        assertParses args

    /// Verifies that webhook delivery show parses.
    [<Test>]
    let ``webhook delivery show parses`` () =
        assertParses [| "webhook"
                        "delivery"
                        "show"
                        "--delivery"
                        Guid.NewGuid().ToString() |]

    /// Verifies that approval policy command parses required verbs.
    [<TestCase("create")>]
    [<TestCase("list")>]
    [<TestCase("show")>]
    [<TestCase("update")>]
    [<TestCase("enable")>]
    [<TestCase("disable")>]
    [<TestCase("delete")>]
    [<TestCase("evaluate")>]
    let ``approval policy command parses required verbs`` verb =
        let policyId = Guid.NewGuid().ToString()

        let args =
            match verb with
            | "create" ->
                [|
                    "approval"
                    "policy"
                    "create"
                    "--name"
                    "release"
                    "--subject"
                    Guid.NewGuid().ToString()
                    "--required-responder"
                    "maintainer"
                |]
            | "list" -> [| "approval"; "policy"; "list" |]
            | "show" ->
                [|
                    "approval"
                    "policy"
                    "show"
                    "--policy"
                    policyId
                |]
            | "update" ->
                [|
                    "approval"
                    "policy"
                    "update"
                    "--policy"
                    policyId
                    "--name"
                    "release"
                    "--subject"
                    Guid.NewGuid().ToString()
                    "--required-responder"
                    "maintainer"
                |]
            | "evaluate" ->
                [|
                    "approval"
                    "policy"
                    "evaluate"
                    "--subject"
                    Guid.NewGuid().ToString()
                |]
            | other ->
                [|
                    "approval"
                    "policy"
                    other
                    "--policy"
                    policyId
                |]

        assertParses args

    /// Verifies that approval request command parses required verbs.
    [<TestCase("list")>]
    [<TestCase("show")>]
    [<TestCase("approve")>]
    [<TestCase("reject")>]
    [<TestCase("wait")>]
    [<TestCase("history")>]
    let ``approval request command parses required verbs`` verb =
        let requestId = Guid.NewGuid().ToString()

        let args =
            match verb with
            | "list" -> [| "approval"; "request"; "list" |]
            | "reject" ->
                [|
                    "approval"
                    "request"
                    "reject"
                    "--request"
                    requestId
                    "--reason"
                    "needs more review"
                |]
            | other ->
                [|
                    "approval"
                    "request"
                    other
                    "--request"
                    requestId
                |]

        assertParses args

    /// Verifies that promotion set approval commands parse.
    [<Test>]
    let ``promotion set approval commands parse`` () =
        let promotionSetId = Guid.NewGuid().ToString()

        assertParses [| "promotion-set"
                        "show"
                        "--promotion-set"
                        promotionSetId |]

        assertParses [| "promotion-set"
                        "list"
                        "--branch"
                        branchId.ToString() |]

        assertParses [| "promotion-set"
                        "request-approval"
                        "--promotion-set"
                        promotionSetId |]

    /// Verifies that forbidden webhook and approval nouns do not parse.
    [<Test>]
    let ``forbidden webhook and approval nouns do not parse`` () =
        assertDoesNotParse [| "hook"; "list" |]

        assertDoesNotParse [| "subscription"
                              "list" |]

        assertDoesNotParse [| "notification"
                              "rule"
                              "list" |]

        assertDoesNotParse [| "approval"
                              "request"
                              "create" |]

    /// Verifies that webhook create requires event and url.
    [<Test>]
    let ``webhook create requires event and url`` () =
        assertDoesNotParse (
            withIds [| "webhook"
                       "create"
                       "--url"
                       "https://example.test/webhook" |]
        )

        assertDoesNotParse (
            withIds [| "webhook"
                       "create"
                       "--event"
                       "promotion-set.applied" |]
        )

    /// Verifies that webhook update requires url to match server update contract.
    [<Test>]
    let ``webhook update requires url to match server update contract`` () =
        assertDoesNotParse (
            withIds [| "webhook"
                       "update"
                       "--webhook"
                       Guid.NewGuid().ToString()
                       "--event"
                       "promotion-set.applied" |]
        )

    /// Verifies that approval policy create requires name subject and required responder.
    [<Test>]
    let ``approval policy create requires name subject and required responder`` () =
        assertDoesNotParse (
            withIds [| "approval"
                       "policy"
                       "create"
                       "--subject"
                       "promotion"
                       "--required-responder"
                       "maintainer" |]
        )

        assertDoesNotParse (
            withIds [| "approval"
                       "policy"
                       "create"
                       "--name"
                       "release"
                       "--required-responder"
                       "maintainer" |]
        )

        assertDoesNotParse (
            withIds [| "approval"
                       "policy"
                       "create"
                       "--name"
                       "release"
                       "--subject"
                       "promotion" |]
        )
