namespace Grace.Server.Tests

open Grace.Actors.Repository
open Grace.Shared
open Grace.Shared.Constants
open Grace.Types.Common
open NUnit.Framework

[<Parallelizable(ParallelScope.All)>]
type RepositoryActorTests() =

    [<Test>]
    member _.StoragePoolAssignmentAllowsConfiguredDefaultPool() =
        match RepositoryActor.ValidateStoragePoolAssignment "corr-default-pool" (StoragePoolId Constants.DefaultStoragePoolId) with
        | Ok () -> ()
        | Error error -> Assert.Fail($"Expected default StoragePool assignment to succeed, got {error.Error}.")

    [<Test>]
    member _.StoragePoolAssignmentRejectsNonDefaultPoolUntilConfiguredPoolsCanBeLoaded() =
        match RepositoryActor.ValidateStoragePoolAssignment "corr-unconfigured-pool" (StoragePoolId "pool-not-configured") with
        | Ok () -> Assert.Fail("Expected non-default StoragePool assignment to fail closed until a configured pool registry exists.")
        | Error error -> Assert.That(error.Error, Does.Contain("not configured or active"))
