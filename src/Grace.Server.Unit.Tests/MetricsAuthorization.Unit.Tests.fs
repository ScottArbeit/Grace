namespace Grace.Server.Tests

open Grace.Server.Security
open Grace.Types.Authorization
open Microsoft.AspNetCore.Http
open NUnit.Framework

/// Covers metrics Authorization Unit behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type MetricsAuthorizationUnitTests() =

    /// Asserts the pass Through condition so failures identify the violated server unit metrics Authorization invariant.
    let assertPassThrough decision =
        match decision with
        | PassThrough -> ()
        | _ -> Assert.Fail($"Expected PassThrough but got {decision}.")

    /// Asserts the reject condition so failures identify the violated server unit metrics Authorization invariant.
    let assertReject (expectedStatus: int) (expectedMessage: string) decision =
        match decision with
        | RejectRequest (statusCode, message) ->
            Assert.That(statusCode, Is.EqualTo(expectedStatus))
            Assert.That(message, Is.EqualTo(expectedMessage))
        | _ -> Assert.Fail($"Expected RejectRequest but got {decision}.")

    /// Verifies that non Metrics Path Passes Through.
    [<Test>]
    member _.NonMetricsPathPassesThrough() =
        let decision = MetricsAuthorization.decideBeforePermissionCheck (PathString("/owner/create")) None

        assertPassThrough decision

    /// Verifies that metrics Unauthenticated Returns401 And Authentication Message.
    [<Test>]
    member _.MetricsUnauthenticatedReturns401AndAuthenticationMessage() =
        let decision = MetricsAuthorization.decideBeforePermissionCheck (PathString("/metrics")) None

        assertReject StatusCodes.Status401Unauthorized "Authentication required." decision

    /// Verifies that metrics Authenticated Requires Permission Check.
    [<Test>]
    member _.MetricsAuthenticatedRequiresPermissionCheck() =
        let decision = MetricsAuthorization.decideBeforePermissionCheck (PathString("/metrics")) (Some "user-1")

        match decision with
        | RequirePermissionCheck -> ()
        | _ -> Assert.Fail($"Expected RequirePermissionCheck but got {decision}.")

    /// Verifies that metrics Denied Returns403 And Generic Forbidden Message By Default.
    [<Test>]
    member _.MetricsDeniedReturns403AndGenericForbiddenMessageByDefault() =
        let decision = MetricsAuthorization.decideAfterPermissionCheck false (Denied "SystemAdmin required.")

        assertReject StatusCodes.Status403Forbidden "Forbidden." decision

    /// Verifies that metrics Denied Includes Reason When Testing Flag Is True.
    [<Test>]
    member _.MetricsDeniedIncludesReasonWhenTestingFlagIsTrue() =
        let decision = MetricsAuthorization.decideAfterPermissionCheck true (Denied "SystemAdmin required.")

        assertReject StatusCodes.Status403Forbidden "SystemAdmin required." decision

    /// Verifies that metrics Denied With Blank Reason Falls Back To Generic Forbidden Message.
    [<Test>]
    member _.MetricsDeniedWithBlankReasonFallsBackToGenericForbiddenMessage() =
        let decision = MetricsAuthorization.decideAfterPermissionCheck true (Denied "   ")

        assertReject StatusCodes.Status403Forbidden "Forbidden." decision

    /// Verifies that metrics Allowed Passes Through.
    [<Test>]
    member _.MetricsAllowedPassesThrough() =
        let decision = MetricsAuthorization.decideAfterPermissionCheck false (Allowed "SystemAdmin")

        assertPassThrough decision
