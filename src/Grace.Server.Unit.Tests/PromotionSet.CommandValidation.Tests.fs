namespace Grace.Server.Tests

open Grace.Actors
open Grace.Server
open Grace.Shared
open Grace.Shared.Parameters.PromotionSet
open Grace.Types.Events
open Grace.Types.PromotionSet
open Grace.Types.Common
open Grace.Types.Visibility
open Grace.Types.Webhooks
open Microsoft.AspNetCore.Http
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic
open System.Threading.Tasks

/// Covers private behavior in no-Aspire server unit tests.
type private FixedApprovalPolicySnapshotResolver(policies: PromotionSetApprovalPolicySnapshot list) =
    interface IApprovalPolicySnapshotResolver with
        /// Verifies that get Current Approval Policies For Promotion Apply.
        member _.GetCurrentApprovalPoliciesForPromotionApply(_, _, _, _, _) = Task.FromResult policies

/// Covers promotion Set Command Validation behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type PromotionSetCommandValidationTests() =

    /// Constructs metadata fixtures used by the server unit promotion Set Command Validation assertions.
    let createMetadata correlationId =
        {
            Timestamp = Instant.FromUtc(2026, 2, 21, 11, 0)
            CorrelationId = correlationId
            Principal = "tester"
            ClientType = Microsoft.FSharp.Core.Option.None
            Properties = Dictionary<string, string>()
        }

    /// Builds existing Promotion Set test data for the server unit promotion Set Command Validation scenarios in this file.
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

    /// Builds approval Policy For test data for the server unit promotion Set Command Validation scenarios in this file.
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

    /// Builds stored Approval Policy For test data for the server unit promotion Set Command Validation scenarios in this file.
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

    /// Builds approval Request For test data for the server unit promotion Set Command Validation scenarios in this file.
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

    /// Asserts the past Expires At Summary Is Stale For condition so failures identify the violated server unit promotion Set Command Validation invariant.
    let assertPastExpiresAtSummaryIsStaleFor status =
        let dto = existingPromotionSet PromotionSetStatus.Ready StepsComputationStatus.Computed
        let policy = approvalPolicyFor dto (Guid.NewGuid()) 1

        let request = { approvalRequestFor dto policy with Status = status; ExpiresAt = Some(Instant.FromUtc(2026, 1, 1, 0, 0)) }

        let summary = Grace.Server.PromotionSet.approvalSummaryFromRequest dto policy (Some request)

        Assert.That(summary.State, Is.EqualTo(PromotionSetApprovalState.Stale))
        Assert.That(summary.ApprovalPolicyId, Is.EqualTo(Some policy.ApprovalPolicyId))
        Assert.That(summary.Reason, Is.EqualTo(Some "Approval request is expired."))

    /// Verifies that hidden PromotionSet get uses the stable empty missing DTO shape.
    [<Test>]
    member _.MissingPromotionSetRouteDtoKeepsEmptyPromotionSetId() =
        let missingDto = PromotionSet.missingPromotionSetDtoForRoute ()

        Assert.That(missingDto, Is.EqualTo(PromotionSetDto.Default))
        Assert.That(missingDto.PromotionSetId, Is.EqualTo(PromotionSetId.Empty))

    /// Verifies that hidden conflict resolution states do not leak blocked or attempt-specific information.
    [<Test>]
    member _.ConflictResolutionPrecheckHidesStateBeforeStatusAndAttemptChecks() =
        let correlationId = "corr-hidden-conflict"

        let hiddenBlockedWrongAttempt =
            { existingPromotionSet PromotionSetStatus.Blocked StepsComputationStatus.ComputeFailed with StepsComputationAttempt = 3 }

        let hiddenNotBlocked = existingPromotionSet PromotionSetStatus.Ready StepsComputationStatus.Computed
        let missing = PromotionSetDto.Default

        let hiddenBlockedError = PromotionSet.tryGetConflictResolutionPrecheckError false hiddenBlockedWrongAttempt 99 correlationId

        let hiddenNotBlockedError = PromotionSet.tryGetConflictResolutionPrecheckError false hiddenNotBlocked 1 correlationId

        let missingError = PromotionSet.tryGetConflictResolutionPrecheckError false missing 1 correlationId

        for error in
            [
                hiddenBlockedError
                hiddenNotBlockedError
                missingError
            ] do
            match error with
            | Option.Some graceError -> Assert.That(graceError.Error, Is.EqualTo("PromotionSet does not exist."))
            | Option.None -> Assert.Fail("Expected hidden or missing conflict resolution to return the missing-equivalent error.")

    /// Verifies that observable conflict resolution still reports state-specific route errors.
    [<Test>]
    member _.ConflictResolutionPrecheckKeepsObservableStateSpecificErrors() =
        let correlationId = "corr-visible-conflict"
        let notBlocked = existingPromotionSet PromotionSetStatus.Ready StepsComputationStatus.Computed
        let blockedWrongAttempt = { existingPromotionSet PromotionSetStatus.Blocked StepsComputationStatus.ComputeFailed with StepsComputationAttempt = 3 }
        let blockedMatchingAttempt = { blockedWrongAttempt with StepsComputationAttempt = 9 }

        let notBlockedError = PromotionSet.tryGetConflictResolutionPrecheckError true notBlocked 1 correlationId

        let wrongAttemptError = PromotionSet.tryGetConflictResolutionPrecheckError true blockedWrongAttempt 99 correlationId

        let allowed = PromotionSet.tryGetConflictResolutionPrecheckError true blockedMatchingAttempt 9 correlationId

        match notBlockedError with
        | Option.Some graceError -> Assert.That(graceError.Error, Is.EqualTo("PromotionSet is not blocked for conflict resolution."))
        | Option.None -> Assert.Fail("Expected an observable non-blocked PromotionSet to keep the route-specific error.")

        match wrongAttemptError with
        | Option.Some graceError -> Assert.That(graceError.Error, Is.EqualTo("StepsComputationAttempt does not match current PromotionSet state."))
        | Option.None -> Assert.Fail("Expected an observable wrong-attempt PromotionSet to keep the route-specific error.")

        Assert.That(allowed.IsNone, Is.True)

    /// Verifies that apply remains valid after success so post-commit ownership transfer can be retried idempotently.
    [<Test>]
    member _.ApplyAllowedWhenPromotionSetAlreadySucceededForTransferRetry() =
        let dto = existingPromotionSet PromotionSetStatus.Succeeded StepsComputationStatus.Computed
        let metadata = createMetadata "corr-apply-succeeded"

        match PromotionSet.validateCommandForState [] dto (PromotionSetCommand.Apply []) metadata with
        | Ok _ -> Assert.Pass()
        | Error graceError -> Assert.Fail($"Expected apply validation to allow succeeded PromotionSet transfer retry, got {graceError.Error}.")

    /// Verifies that apply Rejected When Promotion Set Already Running.
    [<Test>]
    member _.ApplyRejectedWhenPromotionSetAlreadyRunning() =
        let dto = existingPromotionSet PromotionSetStatus.Running StepsComputationStatus.Computing
        let metadata = createMetadata "corr-apply-running"

        match PromotionSet.validateCommandForState [] dto (PromotionSetCommand.Apply []) metadata with
        | Ok _ -> Assert.Fail("Expected apply validation to fail for running PromotionSet.")
        | Error graceError -> Assert.That(graceError.Error, Is.EqualTo("PromotionSet is already running."))

    /// Verifies that recompute Rejected When Steps Already Computing.
    [<Test>]
    member _.RecomputeRejectedWhenStepsAlreadyComputing() =
        let dto = existingPromotionSet PromotionSetStatus.Ready StepsComputationStatus.Computing
        let metadata = createMetadata "corr-recompute-computing"

        match PromotionSet.validateCommandForState [] dto (PromotionSetCommand.RecomputeStepsIfStale(Option.None)) metadata with
        | Ok _ -> Assert.Fail("Expected recompute validation to fail while already computing.")
        | Error graceError -> Assert.That(graceError.Error, Is.EqualTo("PromotionSet steps are already computing."))

    /// Verifies that resolve Conflicts Rejected When Not Blocked.
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

    /// Verifies that duplicate Correlation Id Rejected.
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

    /// Verifies that approval Policy Selection Uses Deterministic Matching Order.
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

    /// Verifies that invalid Matching Approval Policy Is Reported Instead Of Dropped.
    [<Test>]
    member _.InvalidMatchingApprovalPolicyIsReportedInsteadOfDropped() =
        let dto = existingPromotionSet PromotionSetStatus.Ready StepsComputationStatus.Computed
        let invalidPolicy = { approvalPolicyFor dto (Guid.Parse("11111111-1111-1111-1111-111111111111")) 1 with RequiredResponder = " " }
        let validPolicy = approvalPolicyFor dto (Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")) 1

        match PromotionSet.selectApprovalPolicyOrInvalid dto [ validPolicy; invalidPolicy ] with
        | Error (PromotionSet.InvalidMatchingApprovalPolicy selected) -> Assert.That(selected.ApprovalPolicyId, Is.EqualTo(invalidPolicy.ApprovalPolicyId))
        | Ok _ -> Assert.Fail("Expected invalid matching approval policy to block apply gate selection.")
        | Error _ -> Assert.Fail("Expected the invalid matching approval policy to be reported.")

    /// Verifies that multiple Matching Approval Policies Are Rejected Instead Of Collapsed.
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

    /// Verifies that current Policy Resolver Overrides Stale Caller Snapshot At Apply Gate.
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

    /// Verifies that missing Current Policy Resolver Fails Closed When No Fallback Snapshots Exist.
    [<Test>]
    member _.MissingCurrentPolicyResolverFailsClosedWhenNoFallbackSnapshotsExist() =
        task {
            let dto = existingPromotionSet PromotionSetStatus.Ready StepsComputationStatus.Computed

            let! policiesResult = PromotionSet.currentApprovalPoliciesForGate Option.None dto [] "corr-missing-policy-resolver"

            match policiesResult with
            | Error graceError -> Assert.That(graceError.Error, Is.EqualTo("Approval policy resolver is unavailable for promotion apply."))
            | Ok _ -> Assert.Fail("Expected missing resolver without fallback snapshots to fail closed.")
        }

    /// Verifies that non Hosted Fallback Snapshots Remain Usable When Explicitly Supplied.
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

    /// Verifies that derived Approval Summary Reports Invalid Matching Policy As Stale.
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

    /// Verifies that derived Approval Summary Reports Multiple Matching Policies As Stale.
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

    /// Verifies that derived Approval Summary Keeps Explicit Expired Status When Request Expires At Is Past.
    [<Test>]
    member _.DerivedApprovalSummaryKeepsExplicitExpiredStatusWhenRequestExpiresAtIsPast() =
        let dto = existingPromotionSet PromotionSetStatus.Ready StepsComputationStatus.Computed
        let policy = approvalPolicyFor dto (Guid.NewGuid()) 1

        let request = { approvalRequestFor dto policy with Status = ApprovalRequestStatus.Expired; ExpiresAt = Some(Instant.FromUtc(2026, 1, 1, 0, 0)) }

        let summary = Grace.Server.PromotionSet.approvalSummaryFromRequest dto policy (Some request)

        Assert.That(summary.State, Is.EqualTo(PromotionSetApprovalState.Expired))
        Assert.That(summary.ApprovalPolicyId, Is.EqualTo(Some policy.ApprovalPolicyId))
        Assert.That(summary.Reason, Is.EqualTo(Some "Approval request is expired."))

    /// Verifies that derived Approval Summary Reports Past Expires At Pending Request As Stale.
    [<Test>]
    member _.DerivedApprovalSummaryReportsPastExpiresAtPendingRequestAsStale() = assertPastExpiresAtSummaryIsStaleFor ApprovalRequestStatus.Pending

    /// Verifies that derived Approval Summary Reports Past Expires At Approved Request As Stale.
    [<Test>]
    member _.DerivedApprovalSummaryReportsPastExpiresAtApprovedRequestAsStale() = assertPastExpiresAtSummaryIsStaleFor ApprovalRequestStatus.Approved

    /// Verifies that approval Request Must Match Exact Current Attempt Identity.
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

    /// Verifies that generated Approval Request Id Includes Current Attempt Identity.
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

    /// Verifies that update Input Promotions Rejected After Success.
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

    /// Verifies that blank PromotionSet visibility inputs preserve the existing public workflow default.
    [<Test>]
    member _.PromotionSetVisibilityResolverDefaultsToPublicRepositoryOwned() =
        let parameters = CreatePromotionSetParameters()

        match Grace.Server.PromotionSet.resolvePromotionSetVisibility parameters with
        | Ok (visibility, ownership) ->
            Assert.That(visibility, Is.EqualTo(ResourceVisibility.Public))
            Assert.That(ownership, Is.EqualTo(ResourceOwnership.RepositoryOwned))
        | Error errorMessage -> Assert.Fail($"Expected default visibility resolution, got {errorMessage}.")

    /// Verifies that private PromotionSet visibility always resolves to contributor-owned hidden workflow state.
    [<Test>]
    member _.PromotionSetVisibilityResolverMakesPrivateContributorOwned() =
        let parameters = CreatePromotionSetParameters()
        parameters.Visibility <- "Private"

        match Grace.Server.PromotionSet.resolvePromotionSetVisibility parameters with
        | Ok (visibility, ownership) ->
            Assert.That(visibility, Is.EqualTo(ResourceVisibility.Private))
            Assert.That(ownership, Is.EqualTo(ResourceOwnership.ContributorOwned))
        | Error errorMessage -> Assert.Fail($"Expected private visibility resolution, got {errorMessage}.")

    /// Verifies that conflicting PromotionSet visibility inputs are rejected instead of becoming observable no-ops.
    [<Test>]
    member _.PromotionSetVisibilityResolverRejectsPrivateRepositoryOwnedConflict() =
        let parameters = CreatePromotionSetParameters()
        parameters.Visibility <- "Private"
        parameters.Ownership <- "RepositoryOwned"

        match Grace.Server.PromotionSet.resolvePromotionSetVisibility parameters with
        | Error errorMessage -> Assert.That(errorMessage, Is.EqualTo("Private PromotionSet visibility requires ContributorOwned ownership."))
        | Ok _ -> Assert.Fail("Expected private RepositoryOwned conflict to be rejected.")

    /// Verifies that hidden duplicate creates use the same success body type as unused caller-supplied create ids.
    [<Test>]
    member _.HiddenDuplicateCreateReturnValueMatchesCreateSuccessEnvelopeShape() =
        let promotionSetId: PromotionSetId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee")
        let context = DefaultHttpContext()
        context.Items[ Constants.CorrelationId ] <- "corr-hidden-duplicate-create"
        context.Request.Path <- PathString("/promotionSet/create")

        let parameters = CreatePromotionSetParameters()
        parameters.PromotionSetId <- $"{promotionSetId}"
        parameters.TargetBranchId <- "ffffffff-ffff-ffff-ffff-ffffffffffff"
        parameters.Visibility <- "Private"

        let returnValue = Grace.Server.PromotionSet.hiddenPromotionSetCreateReturnValue context parameters promotionSetId

        Assert.That(returnValue.ReturnValue, Is.EqualTo("Promotion set command succeeded."))
        Assert.That(returnValue.CorrelationId, Is.EqualTo("corr-hidden-duplicate-create"))
        Assert.That(returnValue.Properties[nameof PromotionSetId], Is.EqualTo(box promotionSetId))
        Assert.That(returnValue.Properties["Path"], Is.EqualTo("/promotionSet/create"))
