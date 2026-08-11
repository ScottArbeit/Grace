namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI.Command
open Grace.Types.Common
open NUnit.Framework
open System

/// Covers immutable selected-target and caller-operation identity construction.
module WorkingDirectoryUpdateContractsTests =
    /// Extracts a valid test value or fails with the contract rejection reason.
    let private required =
        function
        | Ok value -> value
        | Error error -> failwith error

    /// Builds a complete selected target used by contract scenarios.
    let private target () =
        WorkingDirectoryUpdate.Target.create
            (Guid.Parse("5f48b9a7-5537-4d2d-aeda-16c6d66a1bbc"))
            (Guid.Parse("f191d2d1-8194-4e48-b4e0-9f183dab177e"))
            (Guid.Parse("b1f5373a-7303-4dc1-b085-113e8efed444"))
            (Sha256Hash "40786b40bc5f3bc9070bf49f72bbf1f8b160bb952156e3c9894438c82d03dbd9")
            (Blake3Hash "dc938391649e1c587adcf4ddfe0b06b7a6c47df9e9812c4bea6d01a7c9eab836")
        |> required

    /// Verifies target construction rejects incomplete identifiers and noncanonical hashes.
    [<Test>]
    let ``target requires complete canonical identity`` () =
        WorkingDirectoryUpdate.Target.create
            Guid.Empty
            (Guid.NewGuid())
            (Guid.NewGuid())
            (Sha256Hash "40786b40bc5f3bc9070bf49f72bbf1f8b160bb952156e3c9894438c82d03dbd9")
            (Blake3Hash "dc938391649e1c587adcf4ddfe0b06b7a6c47df9e9812c4bea6d01a7c9eab836")
        |> Result.isError
        |> should equal true

    /// Verifies caller operations reject incomplete cursor and Reference identity fields.
    [<Test>]
    let ``operations require complete cursor and reference identity`` () =
        let selectedTarget = target ()
        let repositoryId = WorkingDirectoryUpdate.Target.repositoryId selectedTarget
        let branchId = WorkingDirectoryUpdate.Target.branchId selectedTarget

        WorkingDirectoryUpdate.Operation.watchReplay repositoryId branchId ""
        |> Result.isError
        |> should equal true

        WorkingDirectoryUpdate.Operation.branchSwitch Guid.Empty (Guid.NewGuid()) selectedTarget
        |> Result.isError
        |> should equal true

        WorkingDirectoryUpdate.Target.create
            (Guid.NewGuid())
            (Guid.NewGuid())
            (Guid.NewGuid())
            (Sha256Hash "40786B40BC5F3BC9070BF49F72BBF1F8B160BB952156E3C9894438C82D03DBD9")
            (Blake3Hash "dc938391649e1c587adcf4ddfe0b06b7a6c47df9e9812c4bea6d01a7c9eab836")
        |> Result.isError
        |> should equal true

    /// Verifies each caller tuple produces a stable canonical operation identity.
    [<Test>]
    let ``caller identities are deterministic and include their complete tuples`` () =
        let selectedTarget = target ()
        let repositoryId = WorkingDirectoryUpdate.Target.repositoryId selectedTarget
        let branchId = WorkingDirectoryUpdate.Target.branchId selectedTarget
        let previousBranchId = Guid.Parse("2c461ab1-72a0-42c3-9c2e-ea9c0c3b83de")
        let selectedReferenceId = Guid.Parse("d9622ad2-552d-4ab1-996e-2d756af82047")

        let localRootScope =
            WorkingDirectoryUpdate.LocalRootScope.create @"C:\Grace\repo"
            |> required

        let watchOne =
            WorkingDirectoryUpdate.Operation.watchReplay repositoryId branchId "cursor-001"
            |> required

        let watchOneAgain =
            WorkingDirectoryUpdate.Operation.watchReplay repositoryId branchId "cursor-001"
            |> required

        let watchTwo =
            WorkingDirectoryUpdate.Operation.watchReplay repositoryId branchId "cursor-002"
            |> required

        let branch =
            WorkingDirectoryUpdate.Operation.branchSwitch previousBranchId selectedReferenceId selectedTarget
            |> required

        let connect =
            WorkingDirectoryUpdate.Operation.connectBootstrap selectedTarget "initial-cursor" localRootScope
            |> required

        WorkingDirectoryUpdate.Operation.value watchOne
        |> should equal (WorkingDirectoryUpdate.Operation.value watchOneAgain)

        WorkingDirectoryUpdate.Operation.value watchOne
        |> should not' (equal (WorkingDirectoryUpdate.Operation.value watchTwo))

        WorkingDirectoryUpdate.Operation.callerKind branch
        |> should equal WorkingDirectoryUpdate.CallerKind.Branch

        WorkingDirectoryUpdate.Operation.callerKind connect
        |> should equal WorkingDirectoryUpdate.CallerKind.Connect
