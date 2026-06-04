namespace Grace.Server.Tests

open Grace.Actors.ApprovalRequest
open Grace.Server
open Grace.Shared.Utilities
open Grace.Types.Common
open Grace.Types.Webhooks
open NUnit.Framework
open System

[<TestFixture>]
type ApprovalRequestActorDecisionTests() =

    let metadata () = EventMetadata.New (generateCorrelationId ()) "test"

    let requestFor attempt =
        { ApprovalRequest.Default with
            ApprovalRequestId = Guid.NewGuid()
            ApprovalPolicyId = Guid.NewGuid()
            ApprovalPolicyVersion = 1
            Subject = "promotion"
            Scope =
                { ApprovalScope.Default with
                    OwnerId = Guid.NewGuid()
                    OrganizationId = Guid.NewGuid()
                    RepositoryId = Guid.NewGuid()
                    TargetBranchId = Guid.NewGuid()
                    StepsComputationAttempt = attempt
                    ApprovalPolicyId = Some(Guid.NewGuid())
                    ApprovalPolicyVersion = Some 1
                }
            RequiredResponder = "role:ApprovalResponder"
            CreatedBy = UserId "workflow"
            CreatedAt = getCurrentInstant ()
        }

    let decision clientDecisionId approvalDecision =
        { ApprovalRequestDecision.Default with
            Decision = approvalDecision
            DecidedBy = UserId "approver"
            DecidedAt = getCurrentInstant ()
            Reason = Some "looks good"
            ClientDecisionId = clientDecisionId
        }

    [<Test>]
    member _.CreateForSamePolicySubjectScopeAndAttemptIsIdempotent() =
        let request = requestFor (Some 7)
        let first = decideCommand ApprovalRequest.Default (ApprovalRequestCommand.Create request) (metadata ())

        match first with
        | Error error -> Assert.Fail error.Error
        | Ok created ->
            let replay = decideCommand created.Request (ApprovalRequestCommand.Create request) (metadata ())

            match replay with
            | Error error -> Assert.Fail error.Error
            | Ok replayed ->
                Assert.That(replayed.WasIdempotentReplay, Is.True)
                Assert.That(replayed.Events, Is.Empty)
                Assert.That(replayed.Request.ApprovalRequestId, Is.EqualTo(request.ApprovalRequestId))

    [<Test>]
    member _.DuplicateIdenticalDecisionIsIdempotentButConflictingDuplicateFails() =
        let request = requestFor (Some 1)

        let created =
            decideCommand ApprovalRequest.Default (ApprovalRequestCommand.Create request) (metadata ())
            |> Result.defaultWith (fun error -> failwith error.Error)

        let approve = decision "client-decision-1" ApprovalDecision.Approve

        let approved =
            decideCommand created.Request (ApprovalRequestCommand.RecordDecision approve) (metadata ())
            |> Result.defaultWith (fun error -> failwith error.Error)

        let replay =
            decideCommand approved.Request (ApprovalRequestCommand.RecordDecision approve) (metadata ())
            |> Result.defaultWith (fun error -> failwith error.Error)

        Assert.That(replay.WasIdempotentReplay, Is.True)
        Assert.That(replay.Events, Is.Empty)

        let conflicting = { approve with Reason = Some "different payload" }

        match decideCommand approved.Request (ApprovalRequestCommand.RecordDecision conflicting) (metadata ()) with
        | Ok _ -> Assert.Fail "Conflicting duplicate decision should fail."
        | Error error -> Assert.That(error.Error, Does.Contain("different approval decision"))

    [<Test>]
    member _.TerminalRequestsCannotBeChanged() =
        let request = requestFor None

        let created =
            decideCommand ApprovalRequest.Default (ApprovalRequestCommand.Create request) (metadata ())
            |> Result.defaultWith (fun error -> failwith error.Error)

        let expired =
            decideCommand created.Request ApprovalRequestCommand.Expire (metadata ())
            |> Result.defaultWith (fun error -> failwith error.Error)

        match decideCommand expired.Request (ApprovalRequestCommand.RecordDecision(decision "late" ApprovalDecision.Approve)) (metadata ()) with
        | Ok _ -> Assert.Fail "Terminal request should reject later decisions."
        | Error error -> Assert.That(error.Error, Does.Contain("already Expired"))

    [<Test>]
    member _.ExpiredCancelledAndSupersededStatesAreRepresentedAndAuditable() =
        let expireRequest = requestFor None
        let cancelRequest = requestFor None
        let supersedeRequest = requestFor None
        let replacementRequestId = Guid.NewGuid()

        let createdExpired =
            decideCommand ApprovalRequest.Default (ApprovalRequestCommand.Create expireRequest) (metadata ())
            |> Result.defaultWith (fun error -> failwith error.Error)

        let expired =
            decideCommand createdExpired.Request ApprovalRequestCommand.Expire (metadata ())
            |> Result.defaultWith (fun error -> failwith error.Error)

        let createdCancelled =
            decideCommand ApprovalRequest.Default (ApprovalRequestCommand.Create cancelRequest) (metadata ())
            |> Result.defaultWith (fun error -> failwith error.Error)

        let cancelled =
            decideCommand createdCancelled.Request ApprovalRequestCommand.Cancel (metadata ())
            |> Result.defaultWith (fun error -> failwith error.Error)

        let createdSuperseded =
            decideCommand ApprovalRequest.Default (ApprovalRequestCommand.Create supersedeRequest) (metadata ())
            |> Result.defaultWith (fun error -> failwith error.Error)

        let superseded =
            decideCommand createdSuperseded.Request (ApprovalRequestCommand.Supersede replacementRequestId) (metadata ())
            |> Result.defaultWith (fun error -> failwith error.Error)

        Assert.That(expired.Request.Status, Is.EqualTo(ApprovalRequestStatus.Expired))
        Assert.That(cancelled.Request.Status, Is.EqualTo(ApprovalRequestStatus.Cancelled))
        Assert.That(superseded.Request.Status, Is.EqualTo(ApprovalRequestStatus.Superseded))
        Assert.That(superseded.Request.SupersededByApprovalRequestId, Is.EqualTo(Some replacementRequestId))
        Assert.That(expired.Events |> List.length, Is.EqualTo(1))
        Assert.That(cancelled.Events |> List.length, Is.EqualTo(1))
        Assert.That(superseded.Events |> List.length, Is.EqualTo(1))

    [<Test>]
    member _.StaleAttemptDoesNotMatchCurrentAttemptCreateKey() =
        let oldAttempt = requestFor (Some 1)
        let currentAttempt = { oldAttempt with ApprovalRequestId = Guid.NewGuid(); Scope = { oldAttempt.Scope with StepsComputationAttempt = Some 2 } }

        let created =
            decideCommand ApprovalRequest.Default (ApprovalRequestCommand.Create oldAttempt) (metadata ())
            |> Result.defaultWith (fun error -> failwith error.Error)

        match decideCommand created.Request (ApprovalRequestCommand.Create currentAttempt) (metadata ()) with
        | Ok _ -> Assert.Fail "A request for a different attempt should not replay as the existing request."
        | Error error -> Assert.That(error.Error, Does.Contain("different payload"))

[<TestFixture>]
type GeneratedApprovalRequestIdTests() =

    let requestFor attempt =
        { ApprovalRequest.Default with
            ApprovalRequestId = Guid.Empty
            ApprovalPolicyId = Guid.Parse("11111111-1111-1111-1111-111111111111")
            ApprovalPolicyVersion = 3
            Subject = "promotion"
            Scope =
                { ApprovalScope.Default with
                    OwnerId = Guid.Parse("22222222-2222-2222-2222-222222222222")
                    OrganizationId = Guid.Parse("33333333-3333-3333-3333-333333333333")
                    RepositoryId = Guid.Parse("44444444-4444-4444-4444-444444444444")
                    TargetBranchId = Guid.Parse("55555555-5555-5555-5555-555555555555")
                    PromotionSetId = Some(Guid.Parse("66666666-6666-6666-6666-666666666666"))
                    StepsComputationAttempt = attempt
                    ApprovalPolicyId = Some(Guid.Parse("11111111-1111-1111-1111-111111111111"))
                    ApprovalPolicyVersion = Some 3
                }
            RequiredResponder = "role:ApprovalResponder"
            CreatedBy = UserId "workflow"
            CreatedAt = getCurrentInstant ()
        }

    [<Test>]
    member _.GeneratedRequestIdIsStableForSameLogicalKeyAndDistinctAcrossAttempts() =
        let first = requestFor (Some 7)
        let replay = { first with CreatedAt = getCurrentInstant (); CreatedBy = UserId "retrying-workflow" }
        let nextAttempt = { first with Scope = { first.Scope with StepsComputationAttempt = Some 8 } }

        let firstId = ApprovalStore.buildGeneratedApprovalRequestId first
        let replayId = ApprovalStore.buildGeneratedApprovalRequestId replay
        let nextAttemptId = ApprovalStore.buildGeneratedApprovalRequestId nextAttempt

        Assert.That(firstId, Is.Not.EqualTo(Guid.Empty))
        Assert.That(replayId, Is.EqualTo(firstId))
        Assert.That(nextAttemptId, Is.Not.EqualTo(firstId))
