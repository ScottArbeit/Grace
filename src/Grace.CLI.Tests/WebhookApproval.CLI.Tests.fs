namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.Command
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Queue
open Grace.Types.PromotionSet
open Grace.Types.Common
open Grace.Types.Webhooks
open NodaTime
open NUnit.Framework
open Spectre.Console
open System
open System.IO
open System.Threading.Tasks

/// Groups webhook approval command coverage for the CLI test project.
[<NonParallelizable>]
module WebhookApprovalCommandTests =
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

    /// Sets ansi console output needed by the test scenario.
    let private setAnsiConsoleOutput (writer: TextWriter) =
        let settings = AnsiConsoleSettings()
        settings.Out <- AnsiConsoleOutput(writer)
        AnsiConsole.Console <- AnsiConsole.Create(settings)

    /// Captures output produced by the action.
    let private captureOutput (action: unit -> unit) =
        use writer = new StringWriter()
        let originalOut = Console.Out

        try
            Console.SetOut(writer)
            setAnsiConsoleOutput writer
            action ()
            writer.ToString()
        finally
            Console.SetOut(originalOut)
            setAnsiConsoleOutput originalOut

    /// Verifies that pending approval output includes request policy status expiration and next command.
    [<Test>]
    let ``pending approval output includes request policy status expiration and next command`` () =
        let requestId = Guid.NewGuid()
        let policyId = Guid.NewGuid()
        let expiresAt = Instant.FromUtc(2026, 6, 3, 11, 0)

        let summary =
            {
                Class = nameof PromotionSetApprovalSummary
                PromotionSetId = Guid.NewGuid()
                TargetBranchId = branchId
                StepsComputationAttempt = 3
                State = PromotionSetApprovalState.Pending
                ApprovalRequestId = Some requestId
                ApprovalPolicyId = Some policyId
                RequiredResponder = Some "maintainer"
                LastDecisionAt = Some(Instant.FromUtc(2026, 6, 3, 10, 0))
                ExpiresAt = Some expiresAt
                Reason = Some "Approval is required before apply can continue."
            }

        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIds [| "promotion-set"
                           "apply"
                           "--promotion-set"
                           summary.PromotionSetId.ToString() |]
            )

        let output = captureOutput (fun () -> ApprovalCommand.renderPendingApproval parseResult summary)

        output |> should contain (requestId.ToString())
        output |> should contain (policyId.ToString())
        output |> should contain "Pending"
        output |> should contain "LastDecisionAt"
        output |> should contain "ExpiresAt"
        output |> should contain (expiresAt.ToString())

        output
        |> should contain "grace approval request approve --request"

    /// Verifies that json output redacts webhook destination and signing secret version.
    [<Test>]
    let ``json output redacts webhook destination and signing secret version`` () =
        let secretUrl = "https://example.test/webhook?sig=secret-token"
        let secretVersion = "kv-secret-version"

        let rule =
            { WebhookRule.Default with
                WebhookRuleId = Guid.NewGuid()
                Name = "apply"
                EventName = "promotion-set.applied"
                Url = { Url = secretUrl; Safety = OutboundUrlSafety.PublicHttps }
                SigningSecretVersion = secretVersion
            }

        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIds [| "webhook"
                           "show"
                           "--webhook"
                           rule.WebhookRuleId.ToString()
                           "--output"
                           "Json" |]
            )

        let output =
            captureOutput (fun () ->
                Common.renderOutput parseResult (Ok(GraceReturnValue.Create rule "corr"))
                |> ignore)

        output |> should not' (contain secretUrl)
        output |> should not' (contain "secret-token")
        output |> should not' (contain secretVersion)
        output |> should contain "Safety"

        let listOutput =
            captureOutput (fun () ->
                Common.renderOutput parseResult (Ok(GraceReturnValue.Create [| rule |] "corr"))
                |> ignore)

        listOutput |> should not' (contain secretUrl)
        listOutput |> should not' (contain secretVersion)

    /// Verifies that json output redacts approval policy notification url.
    [<Test>]
    let ``json output redacts approval policy notification url`` () =
        let secretUrl = "https://example.test/approval?callback=secret-token"

        let policy =
            { ApprovalPolicy.Default with
                ApprovalPolicyId = Guid.NewGuid()
                Name = "release"
                Subject = "promotion"
                RequiredResponder = "maintainer"
                NotificationUrl = Some { Url = secretUrl; Safety = OutboundUrlSafety.PublicHttps }
            }

        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIds [| "approval"
                           "policy"
                           "show"
                           "--policy"
                           policy.ApprovalPolicyId.ToString()
                           "--output"
                           "Json" |]
            )

        let output =
            captureOutput (fun () ->
                Common.renderOutput parseResult (Ok(GraceReturnValue.Create policy "corr"))
                |> ignore)

        output |> should not' (contain secretUrl)
        output |> should not' (contain "secret-token")
        output |> should contain "NotificationUrl"

        let listOutput =
            captureOutput (fun () ->
                Common.renderOutput parseResult (Ok(GraceReturnValue.Create [| policy |] "corr"))
                |> ignore)

        listOutput |> should not' (contain secretUrl)

    /// Verifies that approval request wait returns error when timeout expires while pending.
    [<Test>]
    let ``approval request wait returns error when timeout expires while pending`` () =
        let requestId = Guid.NewGuid()

        let pending = { ApprovalRequest.Default with ApprovalRequestId = requestId; Status = ApprovalRequestStatus.Pending }

        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIds [| "approval"
                           "request"
                           "wait"
                           "--request"
                           requestId.ToString()
                           "--wait-timeout-seconds"
                           "1"
                           "--poll-seconds"
                           "1"
                           "--output"
                           "Silent" |]
            )

        /// Builds show request test data used to exercise CLI webhook Approval behavior.
        let showRequest (_: Grace.Shared.Parameters.Approval.ShowApprovalRequestParameters) = Task.FromResult(Ok(GraceReturnValue.Create pending "corr"))

        match ApprovalCommand.waitRequestWith showRequest parseResult
              |> fun task -> task.GetAwaiter().GetResult()
            with
        | Error error ->
            error.Error |> should contain "timed out"
            error.Error |> should contain "Pending"
        | Ok _ -> Assert.Fail("Expected approval request wait to fail when the timeout expires while the request is pending.")

    /// Verifies that promotion set list fails when a child promotion set fetch fails.
    [<Test>]
    let ``promotion set list fails when a child promotion set fetch fails`` () =
        let firstPromotionSetId = Guid.NewGuid()
        let secondPromotionSetId = Guid.NewGuid()

        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIds [| "promotion-set"
                           "list"
                           "--branch"
                           branchId.ToString()
                           "--output"
                           "Silent" |]
            )

        let queue =
            { PromotionQueue.Default with
                TargetBranchId = branchId
                PromotionSetIds =
                    [
                        firstPromotionSetId
                        secondPromotionSetId
                    ]
            }

        /// Gets queue status needed by the test scenario.
        let getQueueStatus (_: Grace.Shared.Parameters.Queue.QueueStatusParameters) = Task.FromResult(Ok(GraceReturnValue.Create queue "corr"))

        /// Gets promotion set needed by the test scenario.
        let getPromotionSet (parameters: Grace.Shared.Parameters.PromotionSet.GetPromotionSetParameters) =
            if parameters.PromotionSetId = firstPromotionSetId.ToString() then
                Task.FromResult(
                    Ok(GraceReturnValue.Create { PromotionSetDto.Default with PromotionSetId = firstPromotionSetId; TargetBranchId = branchId } "corr")
                )
            else
                Task.FromResult(Error(GraceError.Create "stale promotion set id" "corr"))

        match PromotionSetCommand.listPromotionSetsWith getQueueStatus getPromotionSet parseResult
              |> fun task -> task.GetAwaiter().GetResult()
            with
        | Error error ->
            error.Error
            |> should contain "promotion set fetch"

            serialize error.Properties
            |> should contain "stale promotion set id"
        | Ok _ -> Assert.Fail("Expected promotion-set list to fail when any child promotion set fetch fails.")

    /// Verifies that promotion set list output includes compact approval summary.
    [<Test>]
    let ``promotion set list output includes compact approval summary`` () =
        let promotionSet =
            { PromotionSetDto.Default with
                PromotionSetId = Guid.NewGuid()
                TargetBranchId = branchId
                Status = PromotionSetStatus.Blocked
                StepsComputationAttempt = 2
            }

        let summary =
            { PromotionSetApprovalSummary.NotRequired promotionSet.PromotionSetId branchId 2 with
                State = PromotionSetApprovalState.Pending
                ApprovalRequestId = Some(Guid.NewGuid())
                ApprovalPolicyId = Some(Guid.NewGuid())
            }

        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIds [| "promotion-set"
                           "list"
                           "--branch"
                           branchId.ToString() |]
            )

        let output = captureOutput (fun () -> PromotionSetCommand.renderPromotionSetList parseResult [ promotionSet, Some summary ])

        output
        |> should
            contain
            (promotionSet
                .PromotionSetId
                .ToString()
                .Substring(0, 12))

        output |> should contain "Pending"

        output
        |> should
            contain
            (summary
                .ApprovalRequestId
                .Value
                .ToString()
                .Substring(0, 12))

    /// Verifies that webhook delivery output includes delivery identity retry status and redacted failure.
    [<Test>]
    let ``webhook delivery output includes delivery identity retry status and redacted failure`` () =
        let delivery =
            { WebhookDelivery.Default with
                WebhookDeliveryId = Guid.NewGuid()
                WebhookRuleId = Guid.NewGuid()
                EventName = "promotion-set.applied"
                EventVersion = 1
                AttemptCount = 2
                Status = WebhookDeliveryStatus.RetryScheduled
                NextAttemptAt = Some(Instant.FromUtc(2026, 6, 3, 10, 15))
                LastError = Some "[redacted] HTTP 500"
            }

        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIds [| "webhook"
                           "delivery"
                           "show"
                           "--delivery"
                           delivery.WebhookDeliveryId.ToString() |]
            )

        let output = captureOutput (fun () -> WebhookCommand.renderWebhookDelivery parseResult delivery)

        output
        |> should contain (delivery.WebhookDeliveryId.ToString())

        output
        |> should contain (delivery.WebhookRuleId.ToString())

        output |> should contain "promotion-set.applied"
        output |> should contain "2"
        output |> should contain "RetryScheduled"
        output |> should contain "[redacted] HTTP 500"
