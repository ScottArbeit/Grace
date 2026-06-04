namespace Grace.Types.Tests

open Grace.Shared
open Grace.Types.ExternalWebhookEventRegistry
open Grace.Types.PromotionSet
open Grace.Types.Common
open Grace.Types.Webhooks
open NodaTime
open NUnit.Framework
open System

module WebhookContractTestHelpers =

    let assertRoundTrips<'T when 'T: equality> (value: 'T) =
        let json = Utilities.serialize value
        let deserialized = Utilities.deserialize<'T> json

        Assert.That(deserialized, Is.EqualTo(value))

[<Parallelizable(ParallelScope.All)>]
type WebhookContractSerializationTests() =

    let instant = Instant.FromUtc(2026, 6, 2, 12, 0)

    let scope =
        {
            OwnerId = Guid.Parse("11111111-1111-1111-1111-111111111111")
            OrganizationId = Guid.Parse("22222222-2222-2222-2222-222222222222")
            RepositoryId = Guid.Parse("33333333-3333-3333-3333-333333333333")
            TargetBranchId = Guid.Parse("44444444-4444-4444-4444-444444444444")
            PromotionSetId = Some(Guid.Parse("55555555-5555-5555-5555-555555555555"))
            StepsComputationAttempt = Some 7
            ApprovalPolicyId = Some(Guid.Parse("66666666-6666-6666-6666-666666666666"))
            ApprovalPolicyVersion = Some 3
        }

    [<Test>]
    member _.WebhookRuleRoundTripsSerialization() =
        let rule =
            { WebhookRule.Default with
                WebhookRuleId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
                Name = "notify-release-system"
                EventName = PromotionSetAppliedName
                EventVersion = 1
                Scope =
                    {
                        OwnerId = scope.OwnerId
                        OrganizationId = scope.OrganizationId
                        RepositoryId = scope.RepositoryId
                        TargetBranchId = Some scope.TargetBranchId
                    }
                Url = { Url = "https://release.example.com/grace"; Safety = OutboundUrlSafety.PublicHttps }
                SigningSecretVersion = "secret-v1"
                Status = WebhookRuleStatus.Enabled
                CreatedBy = "release-admin"
                CreatedAt = instant
            }

        WebhookContractTestHelpers.assertRoundTrips rule

    [<Test>]
    member _.WebhookDeliveryRoundTripsSerialization() =
        let delivery =
            { WebhookDelivery.Default with
                WebhookDeliveryId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")
                WebhookRuleId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
                EventName = PromotionSetAppliedName
                DedupeKey = "promotion-set.applied:v1:33333333:44444444:55555555:99999999"
                Status = WebhookDeliveryStatus.RetryScheduled
                AttemptCount = 2
                LastAttemptAt = Some instant
                NextAttemptAt = Some(instant + Duration.FromMinutes(5.0))
                LastStatusCode = Some 503
                LastError = Some "Service unavailable."
                CreatedAt = instant
            }

        WebhookContractTestHelpers.assertRoundTrips delivery

    [<Test>]
    member _.ApprovalPolicyRoundTripsSerialization() =
        let policy =
            { ApprovalPolicy.Default with
                ApprovalPolicyId = Guid.Parse("66666666-6666-6666-6666-666666666666")
                Version = 3
                Name = "require-approval-responder-for-main"
                Subject = "promotion-set.apply"
                Scope = scope
                RequiredResponder = "role:ApprovalResponder"
                NotificationUrl = Some { Url = "https://release.example.com/approval-requested"; Safety = OutboundUrlSafety.PublicHttps }
                TimeoutSeconds = Some 14400
                OnTimeout = ApprovalTimeoutAction.Reject
                Status = ApprovalPolicyStatus.Enabled
                CreatedBy = "release-admin"
                CreatedAt = instant
            }

        WebhookContractTestHelpers.assertRoundTrips policy

    [<Test>]
    member _.ApprovalRequestRoundTripsSerialization() =
        let request =
            { ApprovalRequest.Default with
                ApprovalRequestId = Guid.Parse("77777777-7777-7777-7777-777777777777")
                ApprovalPolicyId = Guid.Parse("66666666-6666-6666-6666-666666666666")
                ApprovalPolicyVersion = 3
                Subject = "promotion-set.apply"
                Scope = scope
                RequiredResponder = "role:ApprovalResponder"
                Status = ApprovalRequestStatus.Approved
                Decision =
                    Some
                        {
                            Decision = ApprovalDecision.Approve
                            DecidedBy = "approver-1"
                            DecidedAt = instant
                            Reason = Some "Reviewed release candidate and validation evidence."
                            ClientDecisionId = "decision-123"
                        }
                CreatedBy = "developer-1"
                CreatedAt = instant - Duration.FromMinutes(30.0)
                UpdatedAt = Some instant
            }

        WebhookContractTestHelpers.assertRoundTrips request

    [<Test>]
    member _.ApprovalNotificationDeliveryRoundTripsSerializationWithoutBecomingWebhookDelivery() =
        let delivery =
            { ApprovalNotificationDelivery.Default with
                ApprovalNotificationDeliveryId = Guid.Parse("88888888-8888-8888-8888-888888888888")
                ApprovalRequestId = Guid.Parse("77777777-7777-7777-7777-777777777777")
                ApprovalPolicyId = Guid.Parse("66666666-6666-6666-6666-666666666666")
                Url = { Url = "http://localhost:8080/approval"; Safety = OutboundUrlSafety.LocalUnsafeDevOnly }
                Status = ApprovalNotificationDeliveryStatus.Pending
                CreatedAt = instant
            }

        let json = Utilities.serialize delivery
        let deserialized = Utilities.deserialize<ApprovalNotificationDelivery> json

        Assert.Multiple(
            Action (fun () ->
                Assert.That(deserialized, Is.EqualTo(delivery))
                Assert.That(deserialized.Class, Is.EqualTo(nameof ApprovalNotificationDelivery))
                Assert.That(deserialized.Class, Is.Not.EqualTo(nameof WebhookDelivery)))
        )

    [<Test>]
    member _.PromotionSetApprovalSummaryRoundTripsSerialization() =
        let summary =
            { PromotionSetApprovalSummary.NotRequired scope.PromotionSetId.Value scope.TargetBranchId 7 with
                State = PromotionSetApprovalState.Pending
                ApprovalRequestId = Some(Guid.Parse("77777777-7777-7777-7777-777777777777"))
                ApprovalPolicyId = Some(Guid.Parse("66666666-6666-6666-6666-666666666666"))
                RequiredResponder = Some "role:ApprovalResponder"
                Reason = Some "Approval required before apply."
            }

        WebhookContractTestHelpers.assertRoundTrips summary

[<Parallelizable(ParallelScope.All)>]
type ExternalWebhookEventRegistryTests() =

    [<Test>]
    member _.PromotionSetAppliedParsesAsCanonicalExternalEvent() =
        let parsed = parse "promotion-set.applied"

        match parsed with
        | Ok definition ->
            Assert.Multiple(
                Action (fun () ->
                    Assert.That(definition.Name, Is.EqualTo(PromotionSetAppliedName))
                    Assert.That(definition.Version, Is.EqualTo(1))

                    Assert.That(definition.CanonicalSource, Is.EqualTo(ExternalWebhookEventSource.PromotionSetApplied))

                    Assert.That(definition.DedupeKeyShape.Version, Is.EqualTo(1))
                    Assert.That(definition.DedupeKeyShape.EventFields, Does.Contain("terminalPromotionReferenceId")))
            )
        | Error errorText -> Assert.Fail(errorText)

    [<Test>]
    member _.EventNameParsingIsStableAndCaseSensitive() =
        Assert.Multiple(
            Action (fun () ->
                Assert.That((tryParse "promotion-set.applied").IsSome, Is.True)
                Assert.That((tryParse " Promotion-Set.Applied ").IsNone, Is.True)
                Assert.That((tryParse "PromotionSetApplied").IsNone, Is.True)
                Assert.That((tryParse "").IsNone, Is.True))
        )

    [<Test>]
    member _.RegistryHasUniqueCanonicalSources() =
        match validateUniqueCanonicalSources All with
        | Ok () -> Assert.Pass()
        | Error errorText -> Assert.Fail(errorText)

    [<Test>]
    member _.RegistryRejectsDuplicateCanonicalSources() =
        let duplicate = { promotionSetApplied with Name = "promotion-set.applied.copy" }

        let result =
            validateUniqueCanonicalSources [ promotionSetApplied
                                             duplicate ]

        match result with
        | Ok () -> Assert.Fail("Duplicate canonical sources should be rejected.")
        | Error errorText -> Assert.That(errorText, Does.Contain("duplicate canonical sources"))

    [<Test>]
    member _.PromotionSetAppliedDoesNotUseTerminalPromotionReferenceAsIndependentSource() =
        let sources =
            All
            |> List.filter (fun definition -> definition.Name = PromotionSetAppliedName)
            |> List.map (fun definition -> definition.CanonicalSource)

        Assert.Multiple(
            Action (fun () ->
                Assert.That(sources.Length, Is.EqualTo(1))

                Assert.That(sources[0], Is.EqualTo(ExternalWebhookEventSource.PromotionSetApplied))

                Assert.That(sources, Does.Not.Contain(ExternalWebhookEventSource.TerminalPromotionReferenceCreated)))
        )
