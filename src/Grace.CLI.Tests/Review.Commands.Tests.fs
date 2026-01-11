namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.Command
open Grace.Shared
open Grace.Types.Policy
open Grace.Types.Queue
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

    let private parseCheckpoint (candidateId: Guid) (extraArgs: string array) =
        let baseArgs =
            [|
                "review"
                "checkpoint"
                "--candidate"
                candidateId.ToString()
                "--reference-id"
                Guid.NewGuid().ToString()
            |]

        GraceCommand.rootCommand.Parse(withIdsAndSilent (Array.append baseArgs extraArgs))

    [<Test>]
    let ``review open rejects invalid candidate id`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "review"
                                    "open"
                                    "--candidate"
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
                                    "--candidate"
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
                                    "--candidate"
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
                                    "--candidate"
                                    Guid.NewGuid().ToString()
                                    "--finding-id"
                                    Guid.NewGuid().ToString()
                                    "--approve"
                                    "--request-changes" |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1

    [<Test>]
    let ``review checkpoint uses candidate policy snapshot when available`` () =
        let candidateId = Guid.NewGuid()
        let parseResult = parseCheckpoint candidateId [||]

        let candidate =
            { IntegrationCandidate.Default with
                CandidateId = candidateId
                TargetBranchId = Guid.NewGuid()
                PolicySnapshotId = PolicySnapshotId "snapshot-candidate"
            }

        let mutable policyCalled = false

        let getCandidate (_: Parameters.Queue.CandidateParameters) = Task.FromResult(Ok(GraceReturnValue.Create candidate graceIds.CorrelationId))

        let getPolicy (_: Parameters.Policy.GetPolicyParameters) =
            policyCalled <- true

            Task.FromResult(
                Ok(GraceReturnValue.Create (Some { PolicySnapshot.Default with PolicySnapshotId = PolicySnapshotId "snapshot-policy" }) graceIds.CorrelationId)
            )

        let result =
            ReviewCommand.resolvePolicySnapshotIdWith getCandidate getPolicy parseResult graceIds candidateId
            |> Async.AwaitTask
            |> Async.RunSynchronously

        match result with
        | Ok snapshotId -> snapshotId |> should equal "snapshot-candidate"
        | Error error -> Assert.Fail error.Error

        Assert.That(policyCalled, Is.False)

    [<Test>]
    let ``review checkpoint falls back to policy snapshot when candidate missing`` () =
        let candidateId = Guid.NewGuid()
        let targetBranchId = Guid.NewGuid()
        let parseResult = parseCheckpoint candidateId [||]

        let candidate =
            { IntegrationCandidate.Default with CandidateId = candidateId; TargetBranchId = targetBranchId; PolicySnapshotId = PolicySnapshotId String.Empty }

        let getCandidate (_: Parameters.Queue.CandidateParameters) = Task.FromResult(Ok(GraceReturnValue.Create candidate graceIds.CorrelationId))

        let policySnapshot = { PolicySnapshot.Default with PolicySnapshotId = PolicySnapshotId "snapshot-policy"; TargetBranchId = targetBranchId }

        let getPolicy (_: Parameters.Policy.GetPolicyParameters) = Task.FromResult(Ok(GraceReturnValue.Create (Some policySnapshot) graceIds.CorrelationId))

        let result =
            ReviewCommand.resolvePolicySnapshotIdWith getCandidate getPolicy parseResult graceIds candidateId
            |> Async.AwaitTask
            |> Async.RunSynchronously

        match result with
        | Ok snapshotId -> snapshotId |> should equal "snapshot-policy"
        | Error error -> Assert.Fail error.Error
