namespace Grace.Server.Tests

open Grace.Server
open NUnit.Framework

[<Parallelizable(ParallelScope.All)>]
type QueuePromotionSetTests() =
    [<Test>]
    member _.QueueInitializationRequiresPolicySnapshotWhenQueueMissing() =
        let requiresSnapshot = Grace.Server.Queue.requiresPolicySnapshotForInitialization false ""

        let noRequirementWhenProvided = Grace.Server.Queue.requiresPolicySnapshotForInitialization false "snapshot"

        let noRequirementWhenQueueExists = Grace.Server.Queue.requiresPolicySnapshotForInitialization true ""

        Assert.That(requiresSnapshot, Is.True)
        Assert.That(noRequirementWhenProvided, Is.False)
        Assert.That(noRequirementWhenQueueExists, Is.False)
