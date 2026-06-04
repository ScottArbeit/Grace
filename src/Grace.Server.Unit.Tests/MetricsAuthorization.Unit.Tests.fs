namespace Grace.Server.Tests

open Grace.Server.Security
open Grace.Types.Authorization
open Microsoft.AspNetCore.Http
open NUnit.Framework

[<Parallelizable(ParallelScope.All)>]
type MetricsAuthorizationUnitTests() =

    let assertPassThrough decision =
        match decision with
        | PassThrough -> ()
        | _ -> Assert.Fail($"Expected PassThrough but got {decision}.")

    let assertReject (expectedStatus: int) (expectedMessage: string) decision =
        match decision with
        | RejectRequest (statusCode, message) ->
            Assert.That(statusCode, Is.EqualTo(expectedStatus))
            Assert.That(message, Is.EqualTo(expectedMessage))
        | _ -> Assert.Fail($"Expected RejectRequest but got {decision}.")

    [<Test>]
    member _.NonMetricsPathPassesThrough() =
        let decision = MetricsAuthorization.decideBeforePermissionCheck (PathString("/owner/create")) None

        assertPassThrough decision

    [<Test>]
    member _.MetricsUnauthenticatedReturns401AndAuthenticationMessage() =
        let decision = MetricsAuthorization.decideBeforePermissionCheck (PathString("/metrics")) None

        assertReject StatusCodes.Status401Unauthorized "Authentication required." decision

    [<Test>]
    member _.MetricsAuthenticatedRequiresPermissionCheck() =
        let decision = MetricsAuthorization.decideBeforePermissionCheck (PathString("/metrics")) (Some "user-1")

        match decision with
        | RequirePermissionCheck -> ()
        | _ -> Assert.Fail($"Expected RequirePermissionCheck but got {decision}.")

    [<Test>]
    member _.MetricsDeniedReturns403AndGenericForbiddenMessageByDefault() =
        let decision = MetricsAuthorization.decideAfterPermissionCheck false (Denied "SystemAdmin required.")

        assertReject StatusCodes.Status403Forbidden "Forbidden." decision

    [<Test>]
    member _.MetricsDeniedIncludesReasonWhenTestingFlagIsTrue() =
        let decision = MetricsAuthorization.decideAfterPermissionCheck true (Denied "SystemAdmin required.")

        assertReject StatusCodes.Status403Forbidden "SystemAdmin required." decision

    [<Test>]
    member _.MetricsDeniedWithBlankReasonFallsBackToGenericForbiddenMessage() =
        let decision = MetricsAuthorization.decideAfterPermissionCheck true (Denied "   ")

        assertReject StatusCodes.Status403Forbidden "Forbidden." decision

    [<Test>]
    member _.MetricsAllowedPassesThrough() =
        let decision = MetricsAuthorization.decideAfterPermissionCheck false (Allowed "SystemAdmin")

        assertPassThrough decision
