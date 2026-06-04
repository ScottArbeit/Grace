namespace Grace.Server.Tests

open Grace.Actors
open Grace.Types.Events
open Grace.Types.PromotionSet
open Grace.Types.Common
open Grace.Types.Webhooks
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic
open System.Threading.Tasks

type private FixedApprovalPolicySnapshotResolver(policies: PromotionSetApprovalPolicySnapshot list) =
    interface IApprovalPolicySnapshotResolver with
        member _.GetCurrentApprovalPoliciesForPromotionApply(_, _, _, _, _) = Task.FromResult policies

[<Parallelizable(ParallelScope.All)>]
type PromotionSetCommandValidationTests() =

    let createMetadata correlationId =
        {
            Timestamp = Instant.FromUtc(2026, 2, 21, 11, 0)
            CorrelationId = correlationId
            Principal = "tester"
            ClientType = Microsoft.FSharp.Core.Option.None
            Properties = Dictionary<string, string>()
        }

    let existingPromotionSet status computationStatus =
        { PromotionSetDto.Default with
            PromotionSetId = Guid.NewGuid()
            OwnerId = Guid.NewGuid()
            OrganizationId = Guid.NewGuid()
            RepositoryId = Guid.NewGuid()
            TargetBranchId = Guid.NewGuid()
            Status = status
            StepsComputationStatus = computationStatus
        }

    let approvalPolicyFor (dto: PromotionSetDto) policyId version =
        { PromotionSetApprovalPolicySnapshot.Default with
            ApprovalPolicyId = policyId
            Version = version
            Subject = "promotion"
            OwnerId = dto.OwnerId
            OrganizationId = dto.OrganizationId
            RepositoryId = dto.RepositoryId
            TargetBranchId = dto.TargetBranchId
            RequiredResponder = "role:ApprovalResponder"
        }

    let storedApprovalPolicyFor (dto: PromotionSetDto) (policy: PromotionSetApprovalPolicySnapshot) =
        { ApprovalPolicy.Default with
            ApprovalPolicyId = policy.ApprovalPolicyId
            Version = policy.Version
            Subject = policy.Subject
            Scope =
                { ApprovalScope.Default with
                    OwnerId = dto.OwnerId
                    OrganizationId = dto.OrganizationId
                    RepositoryId = dto.RepositoryId
                    TargetBranchId = dto.TargetBranchId
                    ApprovalPolicyId = Some policy.ApprovalPolicyId
                    ApprovalPolicyVersion = Some policy.Version
                }
            RequiredResponder = policy.RequiredResponder
            TimeoutSeconds = policy.TimeoutSeconds
            Status = ApprovalPolicyStatus.Enabled
        }

    let approvalRequestFor (dto: PromotionSetDto) (policy: PromotionSetApprovalPolicySnapshot) =
        { ApprovalRequest.Default with
            ApprovalRequestId = Guid.NewGuid()
            ApprovalPolicyId = policy.ApprovalPolicyId
            ApprovalPolicyVersion = policy.Version
            Subject = "promotion"
            Scope = PromotionSet.approvalScope dto policy
            RequiredResponder = policy.RequiredResponder
            Status = ApprovalRequestStatus.Pending
        }

    let assertPastExpiresAtSummaryIsStaleFor status =
        let dto = existingPromotionSet PromotionSetStatus.Ready StepsComputationStatus.Computed
        let policy = approvalPolicyFor dto (Guid.NewGuid()) 1

        let request = { approvalRequestFor dto policy with Status = status; ExpiresAt = Some(Instant.FromUtc(2026, 1, 1, 0, 0)) }

        let summary = Grace.Server.PromotionSet.approvalSummaryFromRequest dto policy (Some request)

        Assert.That(summary.State, Is.EqualTo(PromotionSetApprovalState.Stale))
        Assert.That(summary.ApprovalPolicyId, Is.EqualTo(Some policy.ApprovalPolicyId))
        Assert.That(summary.Reason, Is.EqualTo(Some "Approval request is expired."))

    [<Test>]
    member _.ApplyRejectedWhenPromotionSetAlreadySucceeded() =
        let dto = existingPromotionSet PromotionSetStatus.Succeeded StepsComputationStatus.Computed
        let metadata = createMetadata "corr-apply-succeeded"

        match PromotionSet.validateCommandForState [] dto (PromotionSetCommand.Apply []) metadata with
        | Ok _ -> Assert.Fail("Expected apply validation to fail for succeeded PromotionSet.")
        | Error graceError -> Assert.That(graceError.Error, Is.EqualTo("PromotionSet has already been applied successfully."))

    [<Test>]
    member _.ApplyRejectedWhenPromotionSetAlreadyRunning() =
        let dto = existingPromotionSet PromotionSetStatus.Running StepsComputationStatus.Computing
        let metadata = createMetadata "corr-apply-running"

        match PromotionSet.validateCommandForState [] dto (PromotionSetCommand.Apply []) metadata with
        | Ok _ -> Assert.Fail("Expected apply validation to fail for running PromotionSet.")
        | Error graceError -> Assert.That(graceError.Error, Is.EqualTo("PromotionSet is already running."))

    [<Test>]
    member _.RecomputeRejectedWhenStepsAlreadyComputing() =
        let dto = existingPromotionSet PromotionSetStatus.Ready StepsComputationStatus.Computing
        let metadata = createMetadata "corr-recompute-computing"

        match PromotionSet.validateCommandForState [] dto (PromotionSetCommand.RecomputeStepsIfStale(Option.None)) metadata with
        | Ok _ -> Assert.Fail("Expected recompute validation to fail while already computing.")
        | Error graceError -> Assert.That(graceError.Error, Is.EqualTo("PromotionSet steps are already computing."))

    [<Test>]
    member _.ResolveConflictsRejectedWhenNotBlocked() =
        let dto = existingPromotionSet PromotionSetStatus.Ready StepsComputationStatus.ComputeFailed
        let metadata = createMetadata "corr-resolve-not-blocked"

        let resolutions =
            [
                { FilePath = "src/app.fs"; Accepted = true; OverrideContentArtifactId = Option.None }
            ]

        match PromotionSet.validateCommandForState [] dto (PromotionSetCommand.ResolveConflicts(Guid.NewGuid(), resolutions)) metadata with
        | Ok _ -> Assert.Fail("Expected resolve validation to fail when PromotionSet is not blocked.")
        | Error graceError -> Assert.That(graceError.Error, Is.EqualTo("PromotionSet is not blocked for conflict review."))

    [<Test>]
    member _.DuplicateCorrelationIdRejected() =
        let dto = existingPromotionSet PromotionSetStatus.Ready StepsComputationStatus.Computed
        let duplicateCorrelationId = "corr-duplicate"

        let existingEvents: PromotionSetEvent list =
            [
                { Event = PromotionSetEventType.ApplyStarted; Metadata = createMetadata duplicateCorrelationId }
            ]

        match PromotionSet.validateCommandForState existingEvents dto (PromotionSetCommand.Apply []) (createMetadata duplicateCorrelationId) with
        | Ok _ -> Assert.Fail("Expected duplicate correlation ID validation to fail.")
        | Error graceError -> Assert.That(graceError.Error, Is.EqualTo("Duplicate correlation ID for PromotionSet command."))

    [<Test>]
    member _.ApprovalPolicySelectionUsesDeterministicMatchingOrder() =
        let dto = existingPromotionSet PromotionSetStatus.Ready StepsComputationStatus.Computed
        let laterPolicy = approvalPolicyFor dto (Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")) 1
        let earlierPolicy = approvalPolicyFor dto (Guid.Parse("11111111-1111-1111-1111-111111111111")) 3

        let nonPromotionPolicy = { approvalPolicyFor dto (Guid.Parse("00000000-0000-0000-0000-000000000001")) 1 with Subject = "webhook" }

        let wrongScopePolicy = { approvalPolicyFor dto (Guid.Parse("00000000-0000-0000-0000-000000000002")) 1 with RepositoryId = Guid.NewGuid() }

        match
            PromotionSet.selectApprovalPolicy
                dto
                [
                    laterPolicy
                    wrongScopePolicy
                    nonPromotionPolicy
                    earlierPolicy
                ]
            with
        | Option.Some selected -> Assert.That(selected.ApprovalPolicyId, Is.EqualTo(earlierPolicy.ApprovalPolicyId))
        | Option.None -> Assert.Fail("Expected a matching approval policy.")

    [<Test>]
    member _.InvalidMatchingApprovalPolicyIsReportedInsteadOfDropped() =
        let dto = existingPromotionSet PromotionSetStatus.Ready StepsComputationStatus.Computed
        let invalidPolicy = { approvalPolicyFor dto (Guid.Parse("11111111-1111-1111-1111-111111111111")) 1 with RequiredResponder = " " }
        let validPolicy = approvalPolicyFor dto (Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")) 1

        match PromotionSet.selectApprovalPolicyOrInvalid dto [ validPolicy; invalidPolicy ] with
        | Error (PromotionSet.InvalidMatchingApprovalPolicy selected) -> Assert.That(selected.ApprovalPolicyId, Is.EqualTo(invalidPolicy.ApprovalPolicyId))
        | Ok _ -> Assert.Fail("Expected invalid matching approval policy to block apply gate selection.")
        | Error _ -> Assert.Fail("Expected the invalid matching approval policy to be reported.")

    [<Test>]
    member _.MultipleMatchingApprovalPoliciesAreRejectedInsteadOfCollapsed() =
        let dto = existingPromotionSet PromotionSetStatus.Ready StepsComputationStatus.Computed
        let firstPolicy = approvalPolicyFor dto (Guid.Parse("11111111-1111-1111-1111-111111111111")) 1
        let secondPolicy = approvalPolicyFor dto (Guid.Parse("22222222-2222-2222-2222-222222222222")) 1

        match PromotionSet.selectApprovalPolicyOrInvalid dto [ secondPolicy; firstPolicy ] with
        | Error (PromotionSet.MultipleMatchingApprovalPolicies matchingPolicies) ->
            Assert.That(matchingPolicies.Length, Is.EqualTo(2))
            Assert.That(matchingPolicies[0].ApprovalPolicyId, Is.EqualTo(firstPolicy.ApprovalPolicyId))
        | Ok _ -> Assert.Fail("Expected multiple matching approval policies to block apply gate selection.")
        | Error _ -> Assert.Fail("Expected multiple matching approval policies to be reported.")

    [<Test>]
    member _.CurrentPolicyResolverOverridesStaleCallerSnapshotAtApplyGate() =
        task {
            let dto = existingPromotionSet PromotionSetStatus.Ready StepsComputationStatus.Computed
            let stalePolicy = approvalPolicyFor dto (Guid.Parse("11111111-1111-1111-1111-111111111111")) 1
            let currentPolicy = approvalPolicyFor dto (Guid.Parse("22222222-2222-2222-2222-222222222222")) 2
            let resolver = FixedApprovalPolicySnapshotResolver([ currentPolicy ]) :> IApprovalPolicySnapshotResolver

            let! policiesResult = PromotionSet.currentApprovalPoliciesForGate (Some resolver) dto [ stalePolicy ] "corr-current-policy"

            match policiesResult with
            | Ok policies ->
                match PromotionSet.selectApprovalPolicy dto policies with
                | Option.Some selected -> Assert.That(selected.ApprovalPolicyId, Is.EqualTo(currentPolicy.ApprovalPolicyId))
                | Option.None -> Assert.Fail("Expected current resolver policy to be selected.")
            | Error graceError -> Assert.Fail($"Expected resolver policies, but got {graceError.Error}.")
        }

    [<Test>]
    member _.MissingCurrentPolicyResolverFailsClosedWhenNoFallbackSnapshotsExist() =
        task {
            let dto = existingPromotionSet PromotionSetStatus.Ready StepsComputationStatus.Computed

            let! policiesResult = PromotionSet.currentApprovalPoliciesForGate Option.None dto [] "corr-missing-policy-resolver"

            match policiesResult with
            | Error graceError -> Assert.That(graceError.Error, Is.EqualTo("Approval policy resolver is unavailable for promotion apply."))
            | Ok _ -> Assert.Fail("Expected missing resolver without fallback snapshots to fail closed.")
        }

    [<Test>]
    member _.NonHostedFallbackSnapshotsRemainUsableWhenExplicitlySupplied() =
        task {
            let dto = existingPromotionSet PromotionSetStatus.Ready StepsComputationStatus.Computed
            let fallbackPolicy = approvalPolicyFor dto (Guid.Parse("11111111-1111-1111-1111-111111111111")) 1

            let! policiesResult = PromotionSet.currentApprovalPoliciesForGate Option.None dto [ fallbackPolicy ] "corr-fallback-policy"

            match policiesResult with
            | Ok [ selected ] -> Assert.That(selected.ApprovalPolicyId, Is.EqualTo(fallbackPolicy.ApprovalPolicyId))
            | Ok policies -> Assert.Fail($"Expected one fallback policy, got {policies.Length}.")
            | Error graceError -> Assert.Fail($"Expected fallback snapshots, but got {graceError.Error}.")
        }

    [<Test>]
    member _.DerivedApprovalSummaryReportsInvalidMatchingPolicyAsStale() =
        task {
            let dto = existingPromotionSet PromotionSetStatus.Ready StepsComputationStatus.Computed
            let invalidPolicy = { approvalPolicyFor dto (Guid.NewGuid()) 1 with RequiredResponder = " " }

            Grace.Server.ApprovalStore.upsertPolicy (storedApprovalPolicyFor dto invalidPolicy)
            |> ignore

            let! summary = Grace.Server.PromotionSet.deriveApprovalSummary dto "corr-invalid-summary"

            Assert.That(summary.State, Is.EqualTo(PromotionSetApprovalState.Stale))
            Assert.That(summary.ApprovalPolicyId, Is.EqualTo(Some invalidPolicy.ApprovalPolicyId))

            Assert.That(
                summary.Reason,
                Is.EqualTo(Some "Approval policy is invalid for apply because RequiredResponder is blank or policy identity is invalid.")
            )
        }

    [<Test>]
    member _.DerivedApprovalSummaryReportsMultipleMatchingPoliciesAsStale() =
        task {
            let dto = existingPromotionSet PromotionSetStatus.Ready StepsComputationStatus.Computed
            let firstPolicy = approvalPolicyFor dto (Guid.NewGuid()) 1
            let secondPolicy = approvalPolicyFor dto (Guid.NewGuid()) 1

            Grace.Server.ApprovalStore.upsertPolicy (storedApprovalPolicyFor dto firstPolicy)
            |> ignore

            Grace.Server.ApprovalStore.upsertPolicy (storedApprovalPolicyFor dto secondPolicy)
            |> ignore

            let! summary = Grace.Server.PromotionSet.deriveApprovalSummary dto "corr-multiple-summary"

            Assert.That(summary.State, Is.EqualTo(PromotionSetApprovalState.Stale))
            Assert.That(summary.ApprovalPolicyId.IsNone, Is.True)
            Assert.That(summary.Reason, Is.EqualTo(Some "Multiple enabled approval policies match promotion apply scope; apply requires exactly one."))
        }

    [<Test>]
    member _.DerivedApprovalSummaryKeepsExplicitExpiredStatusWhenRequestExpiresAtIsPast() =
        let dto = existingPromotionSet PromotionSetStatus.Ready StepsComputationStatus.Computed
        let policy = approvalPolicyFor dto (Guid.NewGuid()) 1

        let request = { approvalRequestFor dto policy with Status = ApprovalRequestStatus.Expired; ExpiresAt = Some(Instant.FromUtc(2026, 1, 1, 0, 0)) }

        let summary = Grace.Server.PromotionSet.approvalSummaryFromRequest dto policy (Some request)

        Assert.That(summary.State, Is.EqualTo(PromotionSetApprovalState.Expired))
        Assert.That(summary.ApprovalPolicyId, Is.EqualTo(Some policy.ApprovalPolicyId))
        Assert.That(summary.Reason, Is.EqualTo(Some "Approval request is expired."))

    [<Test>]
    member _.DerivedApprovalSummaryReportsPastExpiresAtPendingRequestAsStale() = assertPastExpiresAtSummaryIsStaleFor ApprovalRequestStatus.Pending

    [<Test>]
    member _.DerivedApprovalSummaryReportsPastExpiresAtApprovedRequestAsStale() = assertPastExpiresAtSummaryIsStaleFor ApprovalRequestStatus.Approved

    [<Test>]
    member _.ApprovalRequestMustMatchExactCurrentAttemptIdentity() =
        let dto = { existingPromotionSet PromotionSetStatus.Ready StepsComputationStatus.Computed with StepsComputationAttempt = 4 }

        let policy = approvalPolicyFor dto (Guid.NewGuid()) 2
        let currentRequest = approvalRequestFor dto policy

        let priorAttemptRequest = { currentRequest with Scope = { currentRequest.Scope with StepsComputationAttempt = Option.Some 3 } }

        let priorPolicyVersionRequest =
            { currentRequest with
                ApprovalPolicyVersion = policy.Version - 1
                Scope = { currentRequest.Scope with ApprovalPolicyVersion = Option.Some(policy.Version - 1) }
            }

        Assert.That(PromotionSet.requestMatchesCurrentAttempt dto policy currentRequest, Is.True)
        Assert.That(PromotionSet.requestMatchesCurrentAttempt dto policy priorAttemptRequest, Is.False)
        Assert.That(PromotionSet.requestMatchesCurrentAttempt dto policy priorPolicyVersionRequest, Is.False)

    [<Test>]
    member _.GeneratedApprovalRequestIdIncludesCurrentAttemptIdentity() =
        let dto = { existingPromotionSet PromotionSetStatus.Ready StepsComputationStatus.Computed with StepsComputationAttempt = 4 }

        let policy = approvalPolicyFor dto (Guid.NewGuid()) 1
        let currentRequest = approvalRequestFor dto policy

        let replayRequest = { currentRequest with ApprovalRequestId = Guid.NewGuid() }

        let nextAttemptRequest = { currentRequest with Scope = { currentRequest.Scope with StepsComputationAttempt = Option.Some 5 } }

        Assert.That(PromotionSet.buildGeneratedApprovalRequestId replayRequest, Is.EqualTo(PromotionSet.buildGeneratedApprovalRequestId currentRequest))

        Assert.That(
            PromotionSet.buildGeneratedApprovalRequestId nextAttemptRequest,
            Is.Not.EqualTo(PromotionSet.buildGeneratedApprovalRequestId currentRequest)
        )

    [<Test>]
    member _.UpdateInputPromotionsRejectedAfterSuccess() =
        let dto = existingPromotionSet PromotionSetStatus.Succeeded StepsComputationStatus.Computed
        let metadata = createMetadata "corr-update-succeeded"

        let pointers =
            [
                { BranchId = Guid.NewGuid(); ReferenceId = Guid.NewGuid(); DirectoryVersionId = Guid.NewGuid() }
            ]

        match PromotionSet.validateCommandForState [] dto (PromotionSetCommand.UpdateInputPromotions pointers) metadata with
        | Ok _ -> Assert.Fail("Expected update-input validation to fail for succeeded PromotionSet.")
        | Error graceError -> Assert.That(graceError.Error, Is.EqualTo("PromotionSet has already succeeded and cannot be edited."))
