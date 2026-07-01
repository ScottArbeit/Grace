namespace Grace.Server.Tests

open Grace.Server
open NUnit.Framework

/// Covers queue Promotion Set behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type QueuePromotionSetTests() =
    /// Verifies that queue Initialization Requires Policy Snapshot When Queue Missing.
    [<Test>]
    member _.QueueInitializationRequiresPolicySnapshotWhenQueueMissing() =
        let requiresSnapshot = Grace.Server.Queue.requiresPolicySnapshotForInitialization false ""

        let noRequirementWhenProvided = Grace.Server.Queue.requiresPolicySnapshotForInitialization false "snapshot"

        let noRequirementWhenQueueExists = Grace.Server.Queue.requiresPolicySnapshotForInitialization true ""

        Assert.That(requiresSnapshot, Is.True)
        Assert.That(noRequirementWhenProvided, Is.False)
        Assert.That(noRequirementWhenQueueExists, Is.False)
