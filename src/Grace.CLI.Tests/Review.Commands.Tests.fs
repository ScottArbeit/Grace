namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.Command
open Grace.Shared
open Grace.Types.Policy
open Grace.Types.PromotionSet
open Grace.Types.Types
open NUnit.Framework
open System
open System.Threading.Tasks

[<NonParallelizable>]
module ReviewCommandTests =
    let private ownerId = Guid.NewGuid()
    let private organizationId = Guid.NewGuid()
    let private repositoryId = Guid.NewGuid()

    let private graceIds =
        { GraceIds.Default with
            OwnerId = ownerId
            OwnerIdString = ownerId.ToString()
            OrganizationId = organizationId
            OrganizationIdString = organizationId.ToString()
            RepositoryId = repositoryId
            RepositoryIdString = repositoryId.ToString()
            CorrelationId = "corr-review"
        }

    let private withIds (args: string array) =
        Array.append
            args
            [|
                "--owner-id"
                ownerId.ToString()
                "--organization-id"
                organizationId.ToString()
                "--repository-id"
                repositoryId.ToString()
            |]

    let private withIdsAndSilent (args: string array) =
        args
        |> Array.append [| "--output"; "Silent" |]
        |> withIds

    let private parseCheckpoint (promotionSetId: Guid) (extraArgs: string array) =
        let baseArgs =
            [|
                "review"
                "checkpoint"
                "--promotion-set"
                promotionSetId.ToString()
                "--reference-id"
                Guid.NewGuid().ToString()
            |]

        GraceCommand.rootCommand.Parse(withIdsAndSilent (Array.append baseArgs extraArgs))

    [<Test>]
    let ``review open rejects invalid promotion set id`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "review"
                                    "open"
                                    "--promotion-set"
                                    "not-a-guid" |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1

    [<Test>]
    let ``review checkpoint rejects invalid reference id`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "review"
                                    "checkpoint"
                                    "--promotion-set"
                                    Guid.NewGuid().ToString()
                                    "--reference-id"
                                    "not-a-guid"
                                    "--policy-snapshot-id"
                                    "snapshot" |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1

    [<Test>]
    let ``review resolve requires resolution state`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "review"
                                    "resolve"
                                    "--promotion-set"
                                    Guid.NewGuid().ToString()
                                    "--finding-id"
                                    Guid.NewGuid().ToString() |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1

    [<Test>]
    let ``review resolve rejects approve and request changes together`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "review"
                                    "resolve"
                                    "--promotion-set"
                                    Guid.NewGuid().ToString()
                                    "--finding-id"
                                    Guid.NewGuid().ToString()
                                    "--approve"
                                    "--request-changes" |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1

    [<Test>]
    let ``review checkpoint uses explicit policy snapshot when provided`` () =
        let promotionSetId = Guid.NewGuid()

        let parseResult =
            parseCheckpoint
                promotionSetId
                [|
                    "--policy-snapshot-id"
                    "snapshot-explicit"
                |]

        let promotionSet = { PromotionSetDto.Default with PromotionSetId = promotionSetId; TargetBranchId = Guid.NewGuid() }

        let mutable getPromotionSetCalled = false
        let mutable policyCalled = false

        let getPromotionSet (_: Parameters.PromotionSet.GetPromotionSetParameters) =
            getPromotionSetCalled <- true
            Task.FromResult(Ok(GraceReturnValue.Create promotionSet graceIds.CorrelationId))

        let getPolicy (_: Parameters.Policy.GetPolicyParameters) =
            policyCalled <- true

            Task.FromResult(
                Ok(GraceReturnValue.Create (Some { PolicySnapshot.Default with PolicySnapshotId = PolicySnapshotId "snapshot-policy" }) graceIds.CorrelationId)
            )

        let result =
            ReviewCommand.resolvePolicySnapshotIdWith getPromotionSet getPolicy parseResult graceIds promotionSetId
            |> Async.AwaitTask
            |> Async.RunSynchronously

        match result with
        | Ok snapshotId -> snapshotId |> should equal "snapshot-explicit"
        | Error error -> Assert.Fail error.Error

        Assert.That(getPromotionSetCalled, Is.False)
        Assert.That(policyCalled, Is.False)

    [<Test>]
    let ``review checkpoint falls back to policy snapshot when promotion set snapshot is missing`` () =
        let promotionSetId = Guid.NewGuid()
        let targetBranchId = Guid.NewGuid()
        let parseResult = parseCheckpoint promotionSetId [||]

        let promotionSet = { PromotionSetDto.Default with PromotionSetId = promotionSetId; TargetBranchId = targetBranchId }

        let getPromotionSet (_: Parameters.PromotionSet.GetPromotionSetParameters) =
            Task.FromResult(Ok(GraceReturnValue.Create promotionSet graceIds.CorrelationId))

        let policySnapshot = { PolicySnapshot.Default with PolicySnapshotId = PolicySnapshotId "snapshot-policy"; TargetBranchId = targetBranchId }

        let getPolicy (_: Parameters.Policy.GetPolicyParameters) = Task.FromResult(Ok(GraceReturnValue.Create (Some policySnapshot) graceIds.CorrelationId))

        let result =
            ReviewCommand.resolvePolicySnapshotIdWith getPromotionSet getPolicy parseResult graceIds promotionSetId
            |> Async.AwaitTask
            |> Async.RunSynchronously

        match result with
        | Ok snapshotId -> snapshotId |> should equal "snapshot-policy"
        | Error error -> Assert.Fail error.Error
