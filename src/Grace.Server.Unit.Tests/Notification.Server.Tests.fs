namespace Grace.Server.Tests

open Grace.Server.Notification
open Grace.Server.Branch
open Grace.Server.Security
open Grace.Types.Common
open Grace.Types.Authorization
open Grace.Types.Reference
open Grace.Types.Library
open Microsoft.AspNetCore.SignalR
open Microsoft.AspNetCore.Http
open NUnit.Framework
open System
open System.Collections.Generic
open System.IO
open System.Security.Claims
open System.Threading
open System.Threading.Tasks

/// Covers notification Server behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type NotificationServerTests() =

    /// Creates a fake permission evaluator that records the requested authorization resource.
    let evaluatorReturning decision (observed: ResizeArray<Operation * Resource>) =
        { new IGracePermissionEvaluator with
            member _.CheckAsync(_, _, operation, resource) =
                observed.Add(operation, resource)
                Task.FromResult decision
        }

    /// Creates a branch dto with stable identity for subscription authorization tests.
    let subscriptionBranch ownerId organizationId repositoryId branchId : Grace.Types.Branch.BranchDto =
        { Grace.Types.Branch.BranchDto.Default with OwnerId = ownerId; OrganizationId = organizationId; RepositoryId = repositoryId; BranchId = branchId }

    /// Creates a fake SignalR hub context that records current-branch group sends without a hosted server.
    let recordingHubContext (observedTargets: ResizeArray<string * string * string array>) (observedPayloads: ResizeArray<CurrentBranchReferenceNotification>) =
        let client =
            { new IGraceClientConnection with
                member _.RegisterRepository _ = Task.CompletedTask
                member _.RegisterParentBranch _ _ = Task.CompletedTask
                member _.RegisterCurrentBranch _ _ = Task.CompletedTask
                member _.RegisterLibraryContent _ = Task.CompletedTask
                member _.NotifyRepository(_, _) = Task.CompletedTask
                member _.NotifyLibraryContentAvailable _ = Task.CompletedTask

                member _.NotifyCurrentBranchReference payload =
                    observedPayloads.Add payload
                    Task.CompletedTask

                member _.NotifyOnCommit(_, _, _, _) = Task.CompletedTask
                member _.NotifyOnCheckpoint(_, _, _, _) = Task.CompletedTask
                member _.NotifyOnSave(_, _, _, _) = Task.CompletedTask
                member _.NotifyAutomationEvent _ = Task.CompletedTask
                member _.ServerToClientMessage _ = Task.CompletedTask
            }

        let clients =
            { new IHubClients<IGraceClientConnection> with
                member _.All = client
                member _.AllExcept _ = client
                member _.Client _ = client
                member _.Clients _ = client

                member _.Group groupName =
                    observedTargets.Add("Group", groupName, Array.empty)
                    client

                member _.GroupExcept(groupName, excludedConnectionIds) =
                    observedTargets.Add("GroupExcept", groupName, excludedConnectionIds |> Seq.toArray)
                    client

                member _.Groups _ = client
                member _.User _ = client
                member _.Users _ = client
            }

        let groups =
            { new IGroupManager with
                member _.AddToGroupAsync(_, _, _: CancellationToken) = Task.CompletedTask
                member _.RemoveFromGroupAsync(_, _, _: CancellationToken) = Task.CompletedTask
            }

        { new IHubContext<NotificationHub, IGraceClientConnection> with
            member _.Clients = clients
            member _.Groups = groups
        }

    /// Creates a fake repository-group hub that records or fails library wake delivery.
    let libraryWakeHubContext failDelivery (observedGroups: ResizeArray<string>) (observedPayloads: ResizeArray<LibraryContentAvailable>) =
        let client =
            { new IGraceClientConnection with
                member _.RegisterRepository _ = Task.CompletedTask
                member _.RegisterParentBranch _ _ = Task.CompletedTask
                member _.RegisterCurrentBranch _ _ = Task.CompletedTask
                member _.RegisterLibraryContent _ = Task.CompletedTask
                member _.NotifyRepository(_, _) = Task.CompletedTask

                member _.NotifyLibraryContentAvailable payload =
                    if failDelivery then
                        Task.FromException(InvalidOperationException("Injected wake failure."))
                    else
                        observedPayloads.Add payload
                        Task.CompletedTask

                member _.NotifyCurrentBranchReference _ = Task.CompletedTask
                member _.NotifyOnCommit(_, _, _, _) = Task.CompletedTask
                member _.NotifyOnCheckpoint(_, _, _, _) = Task.CompletedTask
                member _.NotifyOnSave(_, _, _, _) = Task.CompletedTask
                member _.NotifyAutomationEvent _ = Task.CompletedTask
                member _.ServerToClientMessage _ = Task.CompletedTask
            }

        let clients =
            { new IHubClients<IGraceClientConnection> with
                member _.All = client
                member _.AllExcept _ = client
                member _.Client _ = client
                member _.Clients _ = client

                member _.Group groupName =
                    observedGroups.Add groupName
                    client

                member _.GroupExcept(_, _) = client
                member _.Groups _ = client
                member _.User _ = client
                member _.Users _ = client
            }

        { new IHubContext<NotificationHub, IGraceClientConnection> with
            member _.Clients = clients
            member _.Groups = Unchecked.defaultof<IGroupManager>
        }

    /// Creates a fake SignalR group manager that records add/remove operations in order.
    let recordingGroupManager (operations: ResizeArray<string * string>) =
        { new IGroupManager with
            member _.AddToGroupAsync(_, groupName, _: CancellationToken) =
                operations.Add("add", groupName)
                Task.CompletedTask

            member _.RemoveFromGroupAsync(_, groupName, _: CancellationToken) =
                operations.Add("remove", groupName)
                Task.CompletedTask
        }

    /// Verifies that branch Name Glob Matching Is Case Insensitive And Supports Wildcard.
    [<TestCase("main", "main", true)>]
    [<TestCase("MAIN", "main", true)>]
    [<TestCase("release/2026.02", "release/*", true)>]
    [<TestCase("feature/promo-set", "feature/*", true)>]
    [<TestCase("main", "*", true)>]
    [<TestCase("release", "main", false)>]
    [<TestCase("feature/promo", "release/*", false)>]
    member _.BranchNameGlobMatchingIsCaseInsensitiveAndSupportsWildcard(branchName: string, glob: string, expected: bool) =
        let actual = Subscriber.matchesBranchGlob (BranchName branchName) glob
        Assert.That(actual, Is.EqualTo(expected))

    /// Verifies that current branch SignalR group keys cannot collide with legacy raw GUID groups.
    [<Test>]
    member _.CurrentBranchGroupKeyIsDistinctFromRepositoryAndParentBranchGroups() =
        let repositoryId = Guid.Parse("11111111-1111-1111-1111-111111111111")
        let branchId = Guid.Parse("22222222-2222-2222-2222-222222222222")
        let groupKey = currentBranchGroupKey repositoryId branchId

        Assert.Multiple(
            Action (fun () ->
                Assert.That(groupKey, Is.EqualTo("current-branch:11111111111111111111111111111111:22222222222222222222222222222222"))
                Assert.That(groupKey, Is.Not.EqualTo($"{repositoryId}"))
                Assert.That(groupKey, Is.Not.EqualTo($"{branchId}")))
        )

    /// Verifies the coarse wake targets only the authorized library group and preserves its content-free payload.
    [<Test>]
    member _.LibraryContentWakeTargetsRepositoryGroup() =
        task {
            let repositoryId = Guid.Parse("11111111-1111-1111-1111-111111111111")
            let observedGroups = ResizeArray<string>()
            let observedPayloads = ResizeArray<LibraryContentAvailable>()

            let payload =
                LibraryContentAvailable.Create(
                    repositoryId,
                    "epoch-token",
                    "cursor-token",
                    Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    NodaTime.Instant.FromUtc(2026, 8, 27, 20, 0),
                    "correlation-id"
                )

            let! delivered = notifyLibraryContentAvailableClients (libraryWakeHubContext false observedGroups observedPayloads) payload

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(delivered, Is.True)
                    Assert.That(observedGroups, Has.Count.EqualTo(1))
                    Assert.That(observedGroups[0], Is.EqualTo(libraryGroupKey repositoryId))
                    Assert.That(observedPayloads, Has.Count.EqualTo(1))
                    Assert.That(observedPayloads[0], Is.EqualTo(payload)))
            )
        }

    /// Verifies library wake subscriptions reuse the repository-scoped library read permission.
    [<Test>]
    member _.LibraryContentSubscriptionRequiresLibraryReadAuthorization() =
        task {
            let ownerId = Guid.Parse("11111111-1111-1111-1111-111111111111")
            let organizationId = Guid.Parse("22222222-2222-2222-2222-222222222222")
            let repositoryId = Guid.Parse("33333333-3333-3333-3333-333333333333")
            let observed = ResizeArray<Operation * Resource>()
            let evaluator = evaluatorReturning (Allowed "allowed") observed

            let principal =
                System.Security.Claims.ClaimsPrincipal(
                    System.Security.Claims.ClaimsIdentity(
                        [
                            System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "caller")
                        ]
                    )
                )

            let repositoryDto =
                { Grace.Types.Repository.RepositoryDto.Default with OwnerId = ownerId; OrganizationId = organizationId; RepositoryId = repositoryId }

            let! allowed = canRegisterLibraryContentSubscription evaluator principal repositoryId repositoryDto

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(allowed, Is.True)
                    Assert.That(observed, Has.Count.EqualTo(1))

                    let operation, resource = observed[0]
                    Assert.That(operation, Is.EqualTo(Operation.LibraryRead))
                    Assert.That(resource, Is.EqualTo(Resource.Repository(ownerId, organizationId, repositoryId))))
            )
        }

    /// Verifies denied library read permission blocks wake subscriptions without changing group state.
    [<Test>]
    member _.LibraryContentSubscriptionRejectsDeniedReadAuthorization() =
        task {
            let repositoryId = Guid.NewGuid()
            let observed = ResizeArray<Operation * Resource>()
            let evaluator = evaluatorReturning (Denied "denied") observed

            let repositoryDto =
                { Grace.Types.Repository.RepositoryDto.Default with OwnerId = Guid.NewGuid(); OrganizationId = Guid.NewGuid(); RepositoryId = repositoryId }

            let! allowed = canRegisterLibraryContentSubscription evaluator (System.Security.Claims.ClaimsPrincipal()) repositoryId repositoryDto

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(allowed, Is.False)
                    Assert.That(observed, Has.Count.EqualTo(1))
                    let operation, _ = observed[0]
                    Assert.That(operation, Is.EqualTo(Operation.LibraryRead)))
            )
        }

    /// Verifies caller-supplied repository ids cannot authorize another stored repository identity.
    [<Test>]
    member _.LibraryContentSubscriptionRejectsMismatchedStoredRepositoryIdentity() =
        task {
            let repositoryId = Guid.NewGuid()
            let observed = ResizeArray<Operation * Resource>()
            let evaluator = evaluatorReturning (Allowed "allowed") observed

            let repositoryDto =
                { Grace.Types.Repository.RepositoryDto.Default with OwnerId = Guid.NewGuid(); OrganizationId = Guid.NewGuid(); RepositoryId = Guid.NewGuid() }

            let! allowed = canRegisterLibraryContentSubscription evaluator (System.Security.Claims.ClaimsPrincipal()) repositoryId repositoryDto

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(allowed, Is.False)
                    Assert.That(observed, Is.Empty))
            )
        }

    /// Verifies live delivery failure remains a best-effort false result instead of escaping to the durable caller.
    [<Test>]
    member _.LibraryContentWakeFailureDoesNotEscape() =
        task {
            let repositoryId = Guid.Parse("33333333-3333-3333-3333-333333333333")

            let payload =
                LibraryContentAvailable.Create(
                    repositoryId,
                    "epoch-token",
                    "cursor-token",
                    Guid.Parse("44444444-4444-4444-4444-444444444444"),
                    NodaTime.Instant.FromUtc(2026, 8, 27, 20, 5),
                    "correlation-id"
                )

            let! delivered = notifyLibraryContentAvailableClients (libraryWakeHubContext true (ResizeArray()) (ResizeArray())) payload
            Assert.That(delivered, Is.False)
        }

    /// Verifies that current-branch registration replaces stale group membership for a reused connection.
    [<Test>]
    member _.CurrentBranchRegistrationReplacesPriorCurrentBranchGroup() =
        task {
            let repositoryId = Guid.Parse("11111111-1111-1111-1111-111111111111")
            let oldBranchId = Guid.Parse("22222222-2222-2222-2222-222222222222")
            let newBranchId = Guid.Parse("33333333-3333-3333-3333-333333333333")
            let items = Dictionary<obj, obj>()
            let operations = ResizeArray<string * string>()
            let groups = recordingGroupManager operations

            do! replaceCurrentBranchGroupMembership groups "connection-1" items repositoryId oldBranchId CancellationToken.None
            do! replaceCurrentBranchGroupMembership groups "connection-1" items repositoryId newBranchId CancellationToken.None

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(operations, Has.Count.EqualTo(3))
                    Assert.That(operations[0], Is.EqualTo(("add", currentBranchGroupKey repositoryId oldBranchId)))
                    Assert.That(operations[1], Is.EqualTo(("remove", currentBranchGroupKey repositoryId oldBranchId)))
                    Assert.That(operations[2], Is.EqualTo(("add", currentBranchGroupKey repositoryId newBranchId))))
            )
        }

    /// Verifies that repeating current-branch registration for the same branch does not churn group membership.
    [<Test>]
    member _.CurrentBranchRegistrationDoesNotRemoveWhenBranchIsUnchanged() =
        task {
            let repositoryId = Guid.Parse("11111111-1111-1111-1111-111111111111")
            let branchId = Guid.Parse("22222222-2222-2222-2222-222222222222")
            let items = Dictionary<obj, obj>()
            let operations = ResizeArray<string * string>()
            let groups = recordingGroupManager operations

            do! replaceCurrentBranchGroupMembership groups "connection-1" items repositoryId branchId CancellationToken.None
            do! replaceCurrentBranchGroupMembership groups "connection-1" items repositoryId branchId CancellationToken.None

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(operations, Has.Count.EqualTo(2))
                    Assert.That(operations[0], Is.EqualTo(("add", currentBranchGroupKey repositoryId branchId)))
                    Assert.That(operations[1], Is.EqualTo(("add", currentBranchGroupKey repositoryId branchId))))
            )
        }

    /// Verifies that disconnect cleanup removes only server-side current-branch bookkeeping for the connection.
    [<Test>]
    member _.CurrentBranchDisconnectClearsPerConnectionState() =
        task {
            let repositoryId = Guid.Parse("11111111-1111-1111-1111-111111111111")
            let branchId = Guid.Parse("22222222-2222-2222-2222-222222222222")
            let items = Dictionary<obj, obj>()
            let operations = ResizeArray<string * string>()
            let groups = recordingGroupManager operations

            do! replaceCurrentBranchGroupMembership groups "connection-1" items repositoryId branchId CancellationToken.None

            let cleared = clearCurrentBranchGroupMembershipState items
            let clearedAgain = clearCurrentBranchGroupMembershipState items

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(cleared, Is.True)
                    Assert.That(clearedAgain, Is.False)
                    Assert.That(items, Is.Empty)
                    Assert.That(operations, Has.Count.EqualTo(1))
                    Assert.That(operations[0], Is.EqualTo(("add", currentBranchGroupKey repositoryId branchId))))
            )
        }

    /// Verifies that current-branch SignalR subscriptions require branch read authorization.
    [<Test>]
    member _.CurrentBranchSubscriptionRequiresBranchReadAuthorization() =
        task {
            let ownerId = Guid.Parse("11111111-1111-1111-1111-111111111111")
            let organizationId = Guid.Parse("22222222-2222-2222-2222-222222222222")
            let repositoryId = Guid.Parse("33333333-3333-3333-3333-333333333333")
            let branchId = Guid.Parse("44444444-4444-4444-4444-444444444444")
            let observed = ResizeArray<Operation * Resource>()
            let evaluator = evaluatorReturning (Allowed "allowed") observed

            let principal =
                ClaimsPrincipal(
                    ClaimsIdentity(
                        [|
                            Claim(PrincipalMapper.GraceUserIdClaim, "watch-user")
                        |],
                        "test"
                    )
                )

            let branchDto = subscriptionBranch ownerId organizationId repositoryId branchId

            let! allowed = canRegisterCurrentBranchSubscription evaluator principal repositoryId branchId branchDto

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(allowed, Is.True)
                    Assert.That(observed, Has.Count.EqualTo(1))

                    let operation, resource = observed[0]
                    Assert.That(operation, Is.EqualTo(Operation.BranchRead))
                    Assert.That(resource, Is.EqualTo(Resource.Branch(ownerId, organizationId, repositoryId, branchId))))
            )
        }

    /// Verifies that denied branch read permission blocks current-branch SignalR subscriptions.
    [<Test>]
    member _.CurrentBranchSubscriptionRejectsDeniedBranchRead() =
        task {
            let repositoryId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            let observed = ResizeArray<Operation * Resource>()
            let evaluator = evaluatorReturning (Denied "denied") observed

            let principal =
                ClaimsPrincipal(
                    ClaimsIdentity(
                        [|
                            Claim(PrincipalMapper.GraceUserIdClaim, "watch-user")
                        |],
                        "test"
                    )
                )

            let branchDto = subscriptionBranch (Guid.NewGuid()) (Guid.NewGuid()) repositoryId branchId

            let! allowed = canRegisterCurrentBranchSubscription evaluator principal repositoryId branchId branchDto

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(allowed, Is.False)
                    Assert.That(observed, Has.Count.EqualTo(1)))
            )
        }

    /// Verifies that caller-supplied ids cannot authorize a different stored branch identity.
    [<Test>]
    member _.CurrentBranchSubscriptionRejectsMismatchedStoredBranchIdentity() =
        task {
            let repositoryId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            let observed = ResizeArray<Operation * Resource>()
            let evaluator = evaluatorReturning (Allowed "allowed") observed

            let principal =
                ClaimsPrincipal(
                    ClaimsIdentity(
                        [|
                            Claim(PrincipalMapper.GraceUserIdClaim, "watch-user")
                        |],
                        "test"
                    )
                )

            let branchDto = subscriptionBranch (Guid.NewGuid()) (Guid.NewGuid()) repositoryId (Guid.NewGuid())

            let! allowed = canRegisterCurrentBranchSubscription evaluator principal repositoryId branchId branchDto

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(allowed, Is.False)
                    Assert.That(observed, Has.Count.EqualTo(0)))
            )
        }

    /// Verifies that current-branch Reference notifications are restricted to materializable branch history events.
    [<Test>]
    member _.CurrentBranchReferenceEligibilityExcludesPromotionTagExternalAndRebase() =
        let eligible =
            [|
                ReferenceType.Commit
                ReferenceType.Checkpoint
                ReferenceType.Save
            |]

        let ineligible =
            [|
                ReferenceType.Promotion
                ReferenceType.Tag
                ReferenceType.External
                ReferenceType.Rebase
            |]

        Assert.Multiple(
            Action (fun () ->
                for referenceType in eligible do
                    Assert.That(Subscriber.shouldNotifyCurrentBranchReference referenceType, Is.True, $"Expected {referenceType} current-branch emission.")

                for referenceType in ineligible do
                    Assert.That(
                        Subscriber.shouldNotifyCurrentBranchReference referenceType,
                        Is.False,
                        $"Did not expect {referenceType} current-branch emission."
                    ))
        )

    /// Verifies that the current-branch payload preserves the lookup identity clients need after server recomputation.
    [<Test>]
    member _.CurrentBranchReferencePayloadCarriesBranchAndReferenceLookupIdentity() =
        let referenceId = Guid.Parse("11111111-1111-1111-1111-111111111111")
        let ownerId = Guid.Parse("22222222-2222-2222-2222-222222222222")
        let organizationId = Guid.Parse("33333333-3333-3333-3333-333333333333")
        let repositoryId = Guid.Parse("44444444-4444-4444-4444-444444444444")
        let branchId = Guid.Parse("55555555-5555-5555-5555-555555555555")
        let directoryId = Guid.Parse("66666666-6666-6666-6666-666666666666")

        let payload =
            Subscriber.createCurrentBranchReferenceNotification
                referenceId
                ownerId
                organizationId
                repositoryId
                branchId
                (BranchName "feature/watch")
                directoryId
                (Sha256Hash "sha")
                (Blake3Hash "blake3")
                ReferenceType.Checkpoint
                (ReferenceText "checkpoint")
                "corr-current"

        Assert.Multiple(
            Action (fun () ->
                Assert.That(payload.ReferenceId, Is.EqualTo(referenceId))
                Assert.That(payload.OwnerId, Is.EqualTo(ownerId))
                Assert.That(payload.OrganizationId, Is.EqualTo(organizationId))
                Assert.That(payload.RepositoryId, Is.EqualTo(repositoryId))
                Assert.That(payload.BranchId, Is.EqualTo(branchId))
                Assert.That(payload.BranchName, Is.EqualTo(BranchName "feature/watch"))
                Assert.That(payload.DirectoryId, Is.EqualTo(directoryId))
                Assert.That(payload.ReferenceType, Is.EqualTo(ReferenceType.Checkpoint))
                Assert.That(payload.ReferenceText, Is.EqualTo(ReferenceText "checkpoint"))
                Assert.That(payload.CorrelationId, Is.EqualTo("corr-current")))
        )

    /// Verifies that clients cannot forge current-branch Reference broadcasts through a public hub method.
    [<Test>]
    member _.NotificationHubDoesNotExposeCurrentBranchBroadcastAsClientInvokableMethod() =
        let hubMethodNames =
            typeof<NotificationHub>.GetMethods ()
            |> Array.map (fun methodInfo -> methodInfo.Name)
            |> Set.ofArray

        Assert.That(hubMethodNames, Does.Not.Contain("NotifyCurrentBranchReference"))

    /// Verifies that trusted server-side code can still emit current-branch Reference notifications.
    [<Test>]
    member _.ServerSideCurrentBranchBroadcastTargetsCurrentBranchGroup() =
        task {
            let observedTargets = ResizeArray<string * string * string array>()
            let observedPayloads = ResizeArray<CurrentBranchReferenceNotification>()
            let repositoryId = Guid.Parse("44444444-4444-4444-4444-444444444444")
            let branchId = Guid.Parse("55555555-5555-5555-5555-555555555555")

            let payload =
                { CurrentBranchReferenceNotification.Default with
                    RepositoryId = repositoryId
                    BranchId = branchId
                    ReferenceId = Guid.Parse("11111111-1111-1111-1111-111111111111")
                    ReferenceType = ReferenceType.Save
                }

            let hubContext = recordingHubContext observedTargets observedPayloads

            do! notifyCurrentBranchReferenceClients hubContext payload

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(observedTargets, Has.Count.EqualTo(1))
                    let selector, groupName, excludedConnectionIds = observedTargets[0]
                    Assert.That(selector, Is.EqualTo("Group"))
                    Assert.That(groupName, Is.EqualTo(currentBranchGroupKey repositoryId branchId))
                    Assert.That(excludedConnectionIds, Is.Empty)
                    Assert.That(observedPayloads, Has.Count.EqualTo(1))
                    Assert.That(observedPayloads[0], Is.EqualTo(payload)))
            )
        }

    /// Proves a live publication excludes only the exact connected Watch subscription bound before publication.
    [<Test>]
    member _.CurrentBranchBroadcastExcludesOnlyExactPublishingWatchConnection() =
        task {
            let sourceProcessId = Guid.NewGuid()
            let peerProcessId = Guid.NewGuid()
            let repositoryId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            let referenceId = Guid.NewGuid()
            let sourceConnectionId = $"source-{Guid.NewGuid():N}"
            let peerConnectionId = $"peer-same-machine-{Guid.NewGuid():N}"

            registerWatchSourceSubscription sourceConnectionId sourceProcessId repositoryId branchId
            registerWatchSourceSubscription peerConnectionId peerProcessId repositoryId branchId

            prepareWatchPublicationSource sourceProcessId repositoryId branchId referenceId
            |> ignore

            let observedTargets = ResizeArray<string * string * string array>()
            let observedPayloads = ResizeArray<CurrentBranchReferenceNotification>()
            let payload = { CurrentBranchReferenceNotification.Default with RepositoryId = repositoryId; BranchId = branchId; ReferenceId = referenceId }

            do! notifyCurrentBranchReferenceClients (recordingHubContext observedTargets observedPayloads) payload

            let selector, groupName, excludedConnectionIds = observedTargets[0]

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(selector, Is.EqualTo("GroupExcept"))
                    Assert.That(groupName, Is.EqualTo(currentBranchGroupKey repositoryId branchId))
                    Assert.That(excludedConnectionIds, Has.Length.EqualTo(1))
                    Assert.That(excludedConnectionIds[0], Is.EqualTo(sourceConnectionId))
                    Assert.That(excludedConnectionIds, Does.Not.Contain(peerConnectionId))
                    Assert.That(observedPayloads, Has.Count.EqualTo(1))
                    Assert.That(observedPayloads[0], Is.EqualTo(payload)))
            )
        }

    /// Proves absent, malformed, cross-branch, and expired source bindings use the ordinary group delivery path.
    [<Test>]
    member _.CurrentBranchBroadcastFallsThroughWhenSourceBindingIsNotExactAndLive() =
        task {
            let sourceProcessId = Guid.NewGuid()
            let repositoryId = Guid.NewGuid()
            let publishedRepositoryId = Guid.NewGuid()
            let subscribedBranchId = Guid.NewGuid()
            let publishedBranchId = Guid.NewGuid()
            let connectionId = $"source-{Guid.NewGuid():N}"
            registerWatchSourceSubscription connectionId sourceProcessId repositoryId subscribedBranchId

            let cases =
                [|
                    "absent", Guid.Empty, repositoryId, subscribedBranchId, Guid.NewGuid()
                    "malformed-equivalent", Guid.NewGuid(), repositoryId, subscribedBranchId, Guid.NewGuid()
                    "cross-branch", sourceProcessId, repositoryId, publishedBranchId, Guid.NewGuid()
                    "cross-repository", sourceProcessId, publishedRepositoryId, subscribedBranchId, Guid.NewGuid()
                |]

            for caseName, processId, caseRepositoryId, caseBranchId, referenceId in cases do
                prepareWatchPublicationSource processId caseRepositoryId caseBranchId referenceId
                |> ignore

                let observedTargets = ResizeArray<string * string * string array>()

                let payload =
                    { CurrentBranchReferenceNotification.Default with RepositoryId = caseRepositoryId; BranchId = caseBranchId; ReferenceId = referenceId }

                do! notifyCurrentBranchReferenceClients (recordingHubContext observedTargets (ResizeArray())) payload

                let selector, _, excludedConnectionIds = observedTargets[0]
                Assert.That(selector, Is.EqualTo("Group"), caseName)
                Assert.That(excludedConnectionIds, Is.Empty, caseName)
        }

    /// Proves a same-connection branch refresh invalidates a publication bound to the previous subscription tuple.
    [<Test>]
    member _.CurrentBranchBroadcastFallsThroughAfterSubscriptionRefresh() =
        task {
            let sourceProcessId = Guid.NewGuid()
            let repositoryId = Guid.NewGuid()
            let previousBranchId = Guid.NewGuid()
            let currentBranchId = Guid.NewGuid()
            let referenceId = Guid.NewGuid()
            let connectionId = $"refresh-{Guid.NewGuid():N}"
            registerWatchSourceSubscription connectionId sourceProcessId repositoryId previousBranchId

            prepareWatchPublicationSource sourceProcessId repositoryId previousBranchId referenceId
            |> ignore

            registerWatchSourceSubscription connectionId sourceProcessId repositoryId currentBranchId

            let observedTargets = ResizeArray<string * string * string array>()

            let payload =
                { CurrentBranchReferenceNotification.Default with RepositoryId = repositoryId; BranchId = previousBranchId; ReferenceId = referenceId }

            do! notifyCurrentBranchReferenceClients (recordingHubContext observedTargets (ResizeArray())) payload

            let selector, _, excludedConnectionIds = observedTargets[0]
            Assert.That(selector, Is.EqualTo("Group"))
            Assert.That(excludedConnectionIds, Does.Not.Contain(connectionId))
        }

    /// Proves disconnect, reconnect, subscription refresh, and process-ID reuse cannot suppress the replacement connection.
    [<Test>]
    member _.CurrentBranchBroadcastFallsThroughAfterSourceConnectionIdentityChanges() =
        task {
            let sourceProcessId = Guid.NewGuid()
            let repositoryId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            let referenceId = Guid.NewGuid()
            let originalConnectionId = $"original-{Guid.NewGuid():N}"
            let replacementConnectionId = $"replacement-{Guid.NewGuid():N}"

            registerWatchSourceSubscription originalConnectionId sourceProcessId repositoryId branchId

            prepareWatchPublicationSource sourceProcessId repositoryId branchId referenceId
            |> ignore

            unregisterWatchSourceSubscription originalConnectionId
            registerWatchSourceSubscription replacementConnectionId sourceProcessId repositoryId branchId

            let observedTargets = ResizeArray<string * string * string array>()
            let payload = { CurrentBranchReferenceNotification.Default with RepositoryId = repositoryId; BranchId = branchId; ReferenceId = referenceId }
            do! notifyCurrentBranchReferenceClients (recordingHubContext observedTargets (ResizeArray())) payload

            let selector, _, excludedConnectionIds = observedTargets[0]
            Assert.That(selector, Is.EqualTo("Group"))
            Assert.That(excludedConnectionIds, Does.Not.Contain(replacementConnectionId))
        }

    /// Proves one transient publication binding is consumed so repeated live delivery falls back to the client ledger.
    [<Test>]
    member _.CurrentBranchBroadcastConsumesSourceBindingAfterFirstPublication() =
        task {
            let sourceProcessId = Guid.NewGuid()
            let repositoryId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            let referenceId = Guid.NewGuid()
            let connectionId = $"source-{Guid.NewGuid():N}"
            registerWatchSourceSubscription connectionId sourceProcessId repositoryId branchId

            prepareWatchPublicationSource sourceProcessId repositoryId branchId referenceId
            |> ignore

            let observedTargets = ResizeArray<string * string * string array>()
            let payload = { CurrentBranchReferenceNotification.Default with RepositoryId = repositoryId; BranchId = branchId; ReferenceId = referenceId }
            let hubContext = recordingHubContext observedTargets (ResizeArray())

            do! notifyCurrentBranchReferenceClients hubContext payload
            do! notifyCurrentBranchReferenceClients hubContext payload

            let selectors =
                observedTargets
                |> Seq.map (fun (selector, _, _) -> selector)
                |> Seq.toArray

            Assert.That(selectors, Has.Length.EqualTo(2))
            Assert.That(selectors[0], Is.EqualTo("GroupExcept"))
            Assert.That(selectors[1], Is.EqualTo("Group"))
        }

    /// Proves malformed or absent HTTP delivery metadata is ignored while a canonical process ID remains usable.
    [<Test>]
    member _.WatchProcessHeaderParsingFailsSafe() =
        let processId = Guid.NewGuid()
        let validContext = DefaultHttpContext()

        validContext.Request.Headers[
            Grace.Shared.Constants.WatchProcessIdHeaderKey
        ] <- processId.ToString("N")

        let malformedContext = DefaultHttpContext()

        malformedContext.Request.Headers[
            Grace.Shared.Constants.WatchProcessIdHeaderKey
        ] <- "not-a-process-id"

        let absentContext = DefaultHttpContext()

        Assert.Multiple(
            Action (fun () ->
                Assert.That(tryGetWatchProcessId validContext, Is.EqualTo(Some processId))
                Assert.That(tryGetWatchProcessId malformedContext, Is.EqualTo(None))
                Assert.That(tryGetWatchProcessId absentContext, Is.EqualTo(None)))
        )

    /// Proves failed changes and expiry both remove suppression without advancing any replay or client cursor state.
    [<Test>]
    member _.WatchPublicationBindingsAreRemovableAndExpiring() =
        let sourceProcessId = Guid.NewGuid()
        let repositoryId = Guid.NewGuid()
        let branchId = Guid.NewGuid()
        let connectionId = $"source-{Guid.NewGuid():N}"
        let failedReferenceId = Guid.NewGuid()
        let expiredReferenceId = Guid.NewGuid()
        let replacedByMissingReferenceId = Guid.NewGuid()
        let now = DateTimeOffset.Parse("2026-08-07T08:00:00Z")
        registerWatchSourceSubscription connectionId sourceProcessId repositoryId branchId

        let failedBinding = prepareWatchPublicationSourceAt now sourceProcessId repositoryId branchId failedReferenceId

        failedBinding
        |> Option.iter removeWatchPublicationSource

        prepareWatchPublicationSourceAt now sourceProcessId repositoryId branchId expiredReferenceId
        |> ignore

        prepareWatchPublicationSourceAt now sourceProcessId repositoryId branchId replacedByMissingReferenceId
        |> ignore

        prepareWatchPublicationSourceAt now Guid.Empty repositoryId branchId replacedByMissingReferenceId
        |> ignore

        Assert.Multiple(
            Action (fun () ->
                Assert.That(tryTakeWatchPublicationSourceConnectionAt now repositoryId branchId failedReferenceId, Is.EqualTo(None))
                Assert.That(tryTakeWatchPublicationSourceConnectionAt now repositoryId branchId replacedByMissingReferenceId, Is.EqualTo(None))

                Assert.That(
                    tryTakeWatchPublicationSourceConnectionAt (now + TimeSpan.FromMinutes(3.0)) repositoryId branchId expiredReferenceId,
                    Is.EqualTo(None)
                ))
        )

    /// Verifies that commit current-branch broadcasts are ordered after zip generation and derived diff work.
    [<Test>]
    member _.CommitCurrentBranchNotificationRunsAfterZipAndDiffReadiness() =
        let notificationServerPath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Server", "Notification.Server.fs"))
        let notificationSource = File.ReadAllText notificationServerPath
        let commitStart = notificationSource.IndexOf("| ReferenceType.Commit ->", StringComparison.Ordinal)
        let checkpointStart = notificationSource.IndexOf("| ReferenceType.Checkpoint ->", commitStart, StringComparison.Ordinal)

        Assert.That(commitStart, Is.GreaterThanOrEqualTo(0), "Notification subscriber must handle Commit references.")
        Assert.That(checkpointStart, Is.GreaterThan(commitStart), "Commit branch must be bounded by the Checkpoint branch.")

        let commitBranch = notificationSource.Substring(commitStart, checkpointStart - commitStart)
        let zipIndex = commitBranch.IndexOf("directoryVersionActorProxy.GetZipFileUri correlationId", StringComparison.Ordinal)
        let commitDiffIndex = commitBranch.IndexOf("let! latestTwoCommits = getCommits repositoryId branchId 2 correlationId", StringComparison.Ordinal)

        let parentPromotionDiffIndex =
            commitBranch.IndexOf("match! getLatestPromotion branchDto.RepositoryId branchDto.ParentBranchId", StringComparison.Ordinal)

        let parentLookupIndex =
            commitBranch.IndexOf("let! parentBranchDto = getBranchDto branchDto.ParentBranchId repositoryId correlationId", StringComparison.Ordinal)

        let parentNotificationIndex = commitBranch.IndexOf(".NotifyOnCommit(", StringComparison.Ordinal)
        let currentBranchNotificationIndex = commitBranch.IndexOf("do! emitCurrentBranchReference branchDto.BranchName", StringComparison.Ordinal)

        Assert.Multiple(
            Action (fun () ->
                Assert.That(zipIndex, Is.GreaterThanOrEqualTo(0), "Commit handling must create the directory-version zip before notifying Watch.")
                Assert.That(commitDiffIndex, Is.GreaterThan(zipIndex), "Commit diff work should remain after zip generation.")

                Assert.That(
                    currentBranchNotificationIndex,
                    Is.GreaterThan(commitDiffIndex),
                    "Current-branch commit payloads must follow commit artifact and same-branch diff readiness."
                )

                Assert.That(
                    parentPromotionDiffIndex,
                    Is.GreaterThan(currentBranchNotificationIndex),
                    "Parent-promotion work must not suppress same-branch commit delivery."
                )

                Assert.That(parentLookupIndex, Is.GreaterThan(currentBranchNotificationIndex), "Parent lookup must not suppress same-branch commit delivery.")

                Assert.That(
                    parentNotificationIndex,
                    Is.GreaterThan(parentPromotionDiffIndex),
                    "Parent-branch commit notifications must not run before commit artifact and diff readiness."
                ))
        )

    /// Verifies that checkpoint current-branch broadcasts are ordered after branch-specific diff readiness.
    [<Test>]
    member _.CheckpointCurrentBranchNotificationRunsAfterDiffReadiness() =
        let notificationServerPath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Server", "Notification.Server.fs"))
        let notificationSource = File.ReadAllText notificationServerPath
        let checkpointStart = notificationSource.IndexOf("| ReferenceType.Checkpoint ->", StringComparison.Ordinal)
        let saveStart = notificationSource.IndexOf("| ReferenceType.Save ->", checkpointStart, StringComparison.Ordinal)

        Assert.That(checkpointStart, Is.GreaterThanOrEqualTo(0), "Notification subscriber must handle Checkpoint references.")
        Assert.That(saveStart, Is.GreaterThan(checkpointStart), "Checkpoint branch must be bounded by the Save branch.")

        let checkpointBranch = notificationSource.Substring(checkpointStart, saveStart - checkpointStart)
        let parentNotificationIndex = checkpointBranch.IndexOf(".NotifyOnCheckpoint(", StringComparison.Ordinal)
        let checkpointDiffIndex = checkpointBranch.IndexOf("let! checkpoints = getCheckpoints repositoryId branchId 2 correlationId", StringComparison.Ordinal)
        let commitDiffIndex = checkpointBranch.IndexOf("match! getLatestCommit repositoryId branchId", StringComparison.Ordinal)
        let currentBranchNotificationIndex = checkpointBranch.IndexOf("do! emitCurrentBranchReference branchDto.BranchName", StringComparison.Ordinal)

        let parentLookupIndex =
            checkpointBranch.IndexOf("let! parentBranchDto = getBranchDto branchDto.ParentBranchId repositoryId correlationId", StringComparison.Ordinal)

        Assert.Multiple(
            Action (fun () ->
                Assert.That(parentNotificationIndex, Is.GreaterThanOrEqualTo(0), "Checkpoint handling must preserve parent notification.")
                Assert.That(checkpointDiffIndex, Is.GreaterThanOrEqualTo(0), "Checkpoint diff work should remain in the checkpoint branch.")
                Assert.That(commitDiffIndex, Is.GreaterThan(checkpointDiffIndex), "Commit comparison work should remain after checkpoint diff setup.")

                Assert.That(
                    currentBranchNotificationIndex,
                    Is.GreaterThan(commitDiffIndex),
                    "Current-branch checkpoint payloads must follow checkpoint diff readiness."
                )

                Assert.That(
                    parentLookupIndex,
                    Is.GreaterThan(currentBranchNotificationIndex),
                    "Parent lookup must not suppress same-branch checkpoint delivery."
                )

                Assert.That(parentNotificationIndex, Is.GreaterThan(parentLookupIndex), "Parent checkpoint notification must remain after parent lookup."))
        )

    /// Verifies that save current-branch broadcasts are ordered after branch-specific diff readiness.
    [<Test>]
    member _.SaveCurrentBranchNotificationRunsAfterDiffReadiness() =
        let notificationServerPath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Server", "Notification.Server.fs"))
        let notificationSource = File.ReadAllText notificationServerPath
        let saveStart = notificationSource.IndexOf("| ReferenceType.Save ->", StringComparison.Ordinal)
        let tagStart = notificationSource.IndexOf("| ReferenceType.Tag", saveStart, StringComparison.Ordinal)

        Assert.That(saveStart, Is.GreaterThanOrEqualTo(0), "Notification subscriber must handle Save references.")
        Assert.That(tagStart, Is.GreaterThan(saveStart), "Save branch must be bounded by non-materialized reference handling.")

        let saveBranch = notificationSource.Substring(saveStart, tagStart - saveStart)
        let parentNotificationIndex = saveBranch.IndexOf(".NotifyOnSave(", StringComparison.Ordinal)
        let saveDiffIndex = saveBranch.IndexOf("let! latestTwoSaves = getSaves branchDto.RepositoryId branchId 2 correlationId", StringComparison.Ordinal)
        let commitDiffIndex = saveBranch.IndexOf("match! getLatestCommit branchDto.RepositoryId branchDto.BranchId", StringComparison.Ordinal)
        let checkpointDiffIndex = saveBranch.IndexOf("match! getLatestCheckpoint branchDto.RepositoryId branchDto.BranchId", StringComparison.Ordinal)
        let currentBranchNotificationIndex = saveBranch.IndexOf("do! emitCurrentBranchReference branchDto.BranchName", StringComparison.Ordinal)

        let parentLookupIndex =
            saveBranch.IndexOf("let! parentBranchDto = getBranchDto branchDto.ParentBranchId repositoryId correlationId", StringComparison.Ordinal)

        Assert.Multiple(
            Action (fun () ->
                Assert.That(parentNotificationIndex, Is.GreaterThanOrEqualTo(0), "Save handling must preserve parent notification.")
                Assert.That(saveDiffIndex, Is.GreaterThanOrEqualTo(0), "Save-to-save diff work should remain in the Save branch.")
                Assert.That(commitDiffIndex, Is.GreaterThan(saveDiffIndex), "Save-to-commit diff work should remain after save diff setup.")
                Assert.That(checkpointDiffIndex, Is.GreaterThan(commitDiffIndex), "Save-to-checkpoint diff work should remain after commit comparison.")

                Assert.That(
                    currentBranchNotificationIndex,
                    Is.GreaterThan(checkpointDiffIndex),
                    "Current-branch save payloads must follow save diff readiness."
                )

                Assert.That(parentLookupIndex, Is.GreaterThan(currentBranchNotificationIndex), "Parent lookup must not suppress same-branch save delivery.")
                Assert.That(parentNotificationIndex, Is.GreaterThan(parentLookupIndex), "Parent save notification must remain after parent lookup."))
        )

    /// Verifies that all parent notifications guard the default parent sentinel after same-branch delivery.
    [<Test>]
    member _.ReferenceNotificationsSkipDefaultParentLookups() =
        let notificationServerPath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Server", "Notification.Server.fs"))
        let notificationSource = File.ReadAllText notificationServerPath
        let parentGuardPattern = "if\\s+branchDto\\.ParentBranchId\\s*<>\\s*Constants\\.DefaultParentBranchId\\s+then"

        Assert.That(
            Text
                .RegularExpressions
                .Regex
                .Matches(
                    notificationSource,
                    parentGuardPattern
                )
                .Count,
            Is.EqualTo(3),
            "Commit, Checkpoint, and Save notification paths must each skip default-parent lookup and parent delivery."
        )

    /// Verifies that the additive current-branch contract does not remove parent-branch notification methods.
    [<Test>]
    member _.SignalRClientContractKeepsParentBranchNotificationsWithCurrentBranchPayload() =
        let methodNames =
            typeof<IGraceClientConnection>.GetMethods ()
            |> Array.map (fun methodInfo -> methodInfo.Name)
            |> Set.ofArray

        Assert.Multiple(
            Action (fun () ->
                Assert.That(methodNames, Does.Contain("NotifyOnSave"))
                Assert.That(methodNames, Does.Contain("NotifyOnCheckpoint"))
                Assert.That(methodNames, Does.Contain("NotifyOnCommit"))
                Assert.That(methodNames, Does.Contain("NotifyCurrentBranchReference")))
        )
