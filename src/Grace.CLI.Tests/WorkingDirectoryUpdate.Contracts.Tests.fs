namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI.Command
open Grace.Shared
open Grace.Types.Common
open NUnit.Framework
open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks

module WorkingDirectoryUpdate = WorkingDirectoryUpdateContracts

/// Covers immutable selected-target and caller-operation identity construction.
module WorkingDirectoryUpdateContractsTests =
    /// Extracts a valid test value or fails with the contract rejection reason.
    let private required =
        function
        | Ok value -> value
        | Error error -> failwith error

    /// Builds a complete selected target used by contract scenarios.
    let private target repositoryId branchId rootDirectoryVersionId sha256Hash blake3Hash =
        WorkingDirectoryUpdate.Target.create repositoryId branchId rootDirectoryVersionId sha256Hash blake3Hash
        |> required

    /// Supplies the fixed repository fact used by canonical operation vectors.
    let private repositoryId = Guid.Parse("5f48b9a7-5537-4d2d-aeda-16c6d66a1bbc")

    /// Supplies the fixed selected-branch fact used by canonical operation vectors.
    let private branchId = Guid.Parse("f191d2d1-8194-4e48-b4e0-9f183dab177e")

    /// Supplies the fixed target root version fact used by canonical operation vectors.
    let private rootDirectoryVersionId = Guid.Parse("b1f5373a-7303-4dc1-b085-113e8efed444")

    /// Supplies the fixed target SHA-256 fact used by canonical operation vectors.
    let private sha256Hash = Sha256Hash "40786b40bc5f3bc9070bf49f72bbf1f8b160bb952156e3c9894438c82d03dbd9"

    /// Supplies the fixed target BLAKE3 fact used by canonical operation vectors.
    let private blake3Hash = Blake3Hash "dc938391649e1c587adcf4ddfe0b06b7a6c47df9e9812c4bea6d01a7c9eab836"

    /// Supplies a deterministic absolute local root accepted by the host platform.
    let private localRootPath = if OperatingSystem.IsWindows() then @"C:\Grace\repo" else "/Grace/repo"

    /// Supplies a second deterministic absolute local root for Connect identity sensitivity proof.
    let private otherLocalRootPath = if OperatingSystem.IsWindows() then @"C:\Grace\other" else "/Grace/other"

    /// Builds the fixed complete target whose operation vectors are pinned below.
    let private selectedTarget () = target repositoryId branchId rootDirectoryVersionId sha256Hash blake3Hash

    /// Asserts a one-field tuple variation cannot retain the baseline operation identity.
    let private shouldChange baseline operation =
        WorkingDirectoryUpdate.Operation.value operation
        |> should not' (equal baseline)

    /// Supplies deterministic bytes to the private request constructor without a filesystem dependency.
    type private BindingReader(bytes: byte array) =
        interface WorkingDirectoryUpdate.IPreparedContentReader with
            /// Lists the single deterministic file used by target-binding tests.
            member _.FilePaths = seq { "binding.txt" }

            /// Opens a fresh stream so preparation owns immutable verified bytes.
            member _.OpenReadAsync(_relativePath, _cancellationToken) = Task.FromResult<Stream>(new MemoryStream(bytes, writable = false))

            /// Requires no cleanup because this test reader owns no external resource.
            member _.Dispose() = ()

    /// Builds verified private content required to construct a Request in target-binding scenarios.
    let private preparedContent () =
        let bytes = Encoding.UTF8.GetBytes("target binding")

        let sha256Hash =
            SHA256.HashData(bytes)
            |> Convert.ToHexString
            |> fun value -> Sha256Hash(value.ToLowerInvariant())

        let blake3Hash = Blake3Hash(ContentAddress.computeBlake3Hex bytes)

        let preparedManifest =
            WorkingDirectoryUpdate.PreparedManifest.create [ WorkingDirectoryUpdate.PreparedManifestEntry.File(
                                                                 RelativePath "binding.txt",
                                                                 sha256Hash,
                                                                 blake3Hash
                                                             ) ]
            |> required

        WorkingDirectoryUpdate.PreparedContent.create preparedManifest (new BindingReader(bytes)) CancellationToken.None
        |> fun task -> task.GetAwaiter().GetResult()
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
        let selectedTarget = selectedTarget ()
        let repositoryId = WorkingDirectoryUpdate.Target.repositoryId selectedTarget
        let branchId = WorkingDirectoryUpdate.Target.branchId selectedTarget

        WorkingDirectoryUpdate.Operation.watchReplay repositoryId branchId ""
        |> Result.isError
        |> should equal true

    /// Proves a hash-selected Branch operation keeps its exact target identity without inventing a Reference identity.
    [<Test>]
    let ``DirectoryVersion Branch selection binds identity to its exact target`` () =
        let selectedTarget = selectedTarget ()
        let previousBranchId = branchId

        let selectedTargetOperation =
            WorkingDirectoryUpdate.Operation.branchSwitchWithSelection previousBranchId WorkingDirectoryUpdate.BranchSelection.DirectoryVersion selectedTarget
            |> required

        let differentTarget =
            target
                repositoryId
                branchId
                (Guid.Parse("e71c392d-16a8-4ec1-a759-9f1b56fe5363"))
                (Sha256Hash "50786b40bc5f3bc9070bf49f72bbf1f8b160bb952156e3c9894438c82d03dbd9")
                (Blake3Hash "ec938391649e1c587adcf4ddfe0b06b7a6c47df9e9812c4bea6d01a7c9eab836")

        let differentTargetOperation =
            WorkingDirectoryUpdate.Operation.branchSwitchWithSelection previousBranchId WorkingDirectoryUpdate.BranchSelection.DirectoryVersion differentTarget
            |> required

        let referenceOperation =
            WorkingDirectoryUpdate.Operation.branchSwitch previousBranchId (Guid.Parse("d9622ad2-552d-4ab1-996e-2d756af82047")) selectedTarget
            |> required

        WorkingDirectoryUpdate.Operation.matchesTarget selectedTarget selectedTargetOperation
        |> should equal true

        WorkingDirectoryUpdate.Operation.matchesTarget differentTarget selectedTargetOperation
        |> should equal false

        WorkingDirectoryUpdate.Operation.value selectedTargetOperation
        |> should not' (equal (WorkingDirectoryUpdate.Operation.value differentTargetOperation))

        WorkingDirectoryUpdate.Operation.value selectedTargetOperation
        |> should not' (equal (WorkingDirectoryUpdate.Operation.value referenceOperation))

        WorkingDirectoryUpdate.Operation.branchSwitch Guid.Empty (Guid.NewGuid()) selectedTarget
        |> Result.isError
        |> should equal true

    /// Proves DirectoryVersion selection retains the current Branch while Reference selection may transition Branches.
    [<Test>]
    let ``DirectoryVersion Branch selection rejects a target from another Branch`` () =
        let selectedTarget = selectedTarget ()

        let otherBranchTarget = target repositoryId (Guid.Parse("c9e1d511-13b0-4a65-b2f2-0f3e0c0cd690")) rootDirectoryVersionId sha256Hash blake3Hash

        WorkingDirectoryUpdate.Operation.branchSwitchWithSelection branchId WorkingDirectoryUpdate.BranchSelection.DirectoryVersion otherBranchTarget
        |> function
            | Error message ->
                message
                |> should equal "DirectoryVersion Branch selection must retain the current Branch."
            | Ok _ -> failwith "Expected DirectoryVersion selection to reject a target from another Branch."

        WorkingDirectoryUpdate.Operation.branchSwitch branchId (Guid.Parse("d9622ad2-552d-4ab1-996e-2d756af82047")) otherBranchTarget
        |> Result.isOk
        |> should equal true

        WorkingDirectoryUpdate.Target.create
            (Guid.NewGuid())
            (Guid.NewGuid())
            (Guid.NewGuid())
            (Sha256Hash "40786B40BC5F3BC9070BF49F72BBF1F8B160BB952156E3C9894438C82D03DBD9")
            (Blake3Hash "dc938391649e1c587adcf4ddfe0b06b7a6c47df9e9812c4bea6d01a7c9eab836")
        |> Result.isError
        |> should equal true

    /// Pins canonical caller vectors and proves every accepted tuple field participates in identity.
    [<Test>]
    let ``caller identities use stable canonical vectors and complete tuples`` () =
        let selectedTarget = selectedTarget ()
        let previousBranchId = Guid.Parse("2c461ab1-72a0-42c3-9c2e-ea9c0c3b83de")
        let selectedReferenceId = Guid.Parse("d9622ad2-552d-4ab1-996e-2d756af82047")

        let localRootScope =
            WorkingDirectoryUpdate.LocalRootScope.create localRootPath
            |> required

        let watch =
            WorkingDirectoryUpdate.Operation.watchReplay repositoryId branchId "cursor-001"
            |> required

        let branch =
            WorkingDirectoryUpdate.Operation.branchSwitch previousBranchId selectedReferenceId selectedTarget
            |> required

        let connect =
            WorkingDirectoryUpdate.Operation.connectBootstrap selectedTarget "initial-cursor" localRootScope
            |> required

        let watchValue = WorkingDirectoryUpdate.Operation.value watch
        let branchValue = WorkingDirectoryUpdate.Operation.value branch
        let connectValue = WorkingDirectoryUpdate.Operation.value connect

        let expectedConnectValue =
            if OperatingSystem.IsWindows() then
                "sha256:33f3783b372f75f2df7df1312d6b298521ced2e6817e1958d769edca736dcac6"
            else
                "sha256:e56c2d2453222159def2f09bd75b8df8bbdb6686fb0bafe0e11d69217612e30f"

        watchValue
        |> should equal "sha256:66d663c833c8a6984092cbd243d78dd7c01518aae7fa3456f234e7c7339f94f2"

        branchValue
        |> should equal "sha256:8c706ec29cafceeb72203b736ac4bf16112413af8a48bddf2a112d287ad520e8"

        connectValue |> should equal expectedConnectValue

        [
            WorkingDirectoryUpdate.Operation.watchReplay (Guid.Parse("8c7de5d5-6683-4c49-b0e0-4ea99a3294ef")) branchId "cursor-001"
            WorkingDirectoryUpdate.Operation.watchReplay repositoryId (Guid.Parse("c9e1d511-13b0-4a65-b2f2-0f3e0c0cd690")) "cursor-001"
            WorkingDirectoryUpdate.Operation.watchReplay repositoryId branchId "cursor-002"
        ]
        |> List.map required
        |> List.iter (shouldChange watchValue)

        /// Creates a target that varies exactly one caller-tuple fact per test case.
        let varyTarget repository branch root sha256 blake3 = target repository branch root sha256 blake3

        [
            WorkingDirectoryUpdate.Operation.branchSwitch (Guid.Parse("b7a4ba94-3bbd-440a-92d1-08bca832fa3a")) selectedReferenceId selectedTarget
            WorkingDirectoryUpdate.Operation.branchSwitch previousBranchId (Guid.Parse("20d59b96-8b2f-4493-9e7d-4282f51420ce")) selectedTarget
            WorkingDirectoryUpdate.Operation.branchSwitch
                previousBranchId
                selectedReferenceId
                (varyTarget (Guid.Parse("8c7de5d5-6683-4c49-b0e0-4ea99a3294ef")) branchId rootDirectoryVersionId sha256Hash blake3Hash)
            WorkingDirectoryUpdate.Operation.branchSwitch
                previousBranchId
                selectedReferenceId
                (varyTarget repositoryId (Guid.Parse("c9e1d511-13b0-4a65-b2f2-0f3e0c0cd690")) rootDirectoryVersionId sha256Hash blake3Hash)
            WorkingDirectoryUpdate.Operation.branchSwitch
                previousBranchId
                selectedReferenceId
                (varyTarget repositoryId branchId (Guid.Parse("e71c392d-16a8-4ec1-a759-9f1b56fe5363")) sha256Hash blake3Hash)
            WorkingDirectoryUpdate.Operation.branchSwitch
                previousBranchId
                selectedReferenceId
                (varyTarget
                    repositoryId
                    branchId
                    rootDirectoryVersionId
                    (Sha256Hash "50786b40bc5f3bc9070bf49f72bbf1f8b160bb952156e3c9894438c82d03dbd9")
                    blake3Hash)
            WorkingDirectoryUpdate.Operation.branchSwitch
                previousBranchId
                selectedReferenceId
                (varyTarget
                    repositoryId
                    branchId
                    rootDirectoryVersionId
                    sha256Hash
                    (Blake3Hash "ec938391649e1c587adcf4ddfe0b06b7a6c47df9e9812c4bea6d01a7c9eab836"))
        ]
        |> List.map required
        |> List.iter (shouldChange branchValue)

        [
            WorkingDirectoryUpdate.Operation.connectBootstrap
                (varyTarget (Guid.Parse("8c7de5d5-6683-4c49-b0e0-4ea99a3294ef")) branchId rootDirectoryVersionId sha256Hash blake3Hash)
                "initial-cursor"
                localRootScope
            WorkingDirectoryUpdate.Operation.connectBootstrap
                (varyTarget repositoryId (Guid.Parse("c9e1d511-13b0-4a65-b2f2-0f3e0c0cd690")) rootDirectoryVersionId sha256Hash blake3Hash)
                "initial-cursor"
                localRootScope
            WorkingDirectoryUpdate.Operation.connectBootstrap
                (varyTarget repositoryId branchId (Guid.Parse("e71c392d-16a8-4ec1-a759-9f1b56fe5363")) sha256Hash blake3Hash)
                "initial-cursor"
                localRootScope
            WorkingDirectoryUpdate.Operation.connectBootstrap
                (varyTarget
                    repositoryId
                    branchId
                    rootDirectoryVersionId
                    (Sha256Hash "50786b40bc5f3bc9070bf49f72bbf1f8b160bb952156e3c9894438c82d03dbd9")
                    blake3Hash)
                "initial-cursor"
                localRootScope
            WorkingDirectoryUpdate.Operation.connectBootstrap
                (varyTarget
                    repositoryId
                    branchId
                    rootDirectoryVersionId
                    sha256Hash
                    (Blake3Hash "ec938391649e1c587adcf4ddfe0b06b7a6c47df9e9812c4bea6d01a7c9eab836"))
                "initial-cursor"
                localRootScope
            WorkingDirectoryUpdate.Operation.connectBootstrap selectedTarget "next-cursor" localRootScope
            WorkingDirectoryUpdate.Operation.connectBootstrap
                selectedTarget
                "initial-cursor"
                (WorkingDirectoryUpdate.LocalRootScope.create otherLocalRootPath
                 |> required)
        ]
        |> List.map required
        |> List.iter (shouldChange connectValue)

        WorkingDirectoryUpdate.Operation.watchReplay repositoryId branchId "cursor-001"
        |> required
        |> WorkingDirectoryUpdate.Operation.value
        |> should equal watchValue

        WorkingDirectoryUpdate.Operation.callerKind branch
        |> should equal WorkingDirectoryUpdate.CallerKind.Branch

        WorkingDirectoryUpdate.Operation.callerKind connect
        |> should equal WorkingDirectoryUpdate.CallerKind.Connect

    /// Verifies random marker attempts cannot redefine the fixed logical operation vector.
    [<Test>]
    let ``attempt tokens remain independent from operation identity`` () =
        let operation =
            WorkingDirectoryUpdate.Operation.watchReplay repositoryId branchId "cursor-001"
            |> required

        let firstAttempt = WorkingDirectoryUpdate.AttemptToken.create ()
        let secondAttempt = WorkingDirectoryUpdate.AttemptToken.create ()

        WorkingDirectoryUpdate.AttemptToken.value firstAttempt
        |> should not' (equal (WorkingDirectoryUpdate.AttemptToken.value secondAttempt))

        WorkingDirectoryUpdate.Operation.value operation
        |> should equal "sha256:66d663c833c8a6984092cbd243d78dd7c01518aae7fa3456f234e7c7339f94f2"

    /// Verifies Branch and Connect receipts cannot substitute any root identity fact after the generic request seam is removed.
    [<Test>]
    let ``Branch and Connect bind requests and receipts to their complete selected target`` () =
        let selectedTarget = selectedTarget ()
        let branchOperation =
            WorkingDirectoryUpdate.Operation.branchSwitch
                (Guid.Parse("2c461ab1-72a0-42c3-9c2e-ea9c0c3b83de"))
                (Guid.Parse("d9622ad2-552d-4ab1-996e-2d756af82047"))
                selectedTarget
            |> required

        let connectOperation =
            WorkingDirectoryUpdate.Operation.connectBootstrap
                selectedTarget
                "initial-cursor"
                (WorkingDirectoryUpdate.LocalRootScope.create localRootPath
                 |> required)
            |> required

        let mismatchedTargets =
            [
                target repositoryId branchId (Guid.Parse("e71c392d-16a8-4ec1-a759-9f1b56fe5363")) sha256Hash blake3Hash
                target repositoryId branchId rootDirectoryVersionId (Sha256Hash "50786b40bc5f3bc9070bf49f72bbf1f8b160bb952156e3c9894438c82d03dbd9") blake3Hash
                target repositoryId branchId rootDirectoryVersionId sha256Hash (Blake3Hash "ec938391649e1c587adcf4ddfe0b06b7a6c47df9e9812c4bea6d01a7c9eab836")
            ]

        let assertCompleteBinding operation =
            WorkingDirectoryUpdate.Receipt.create selectedTarget operation true
            |> Result.isOk
            |> should equal true

            mismatchedTargets
            |> List.iter (fun mismatchedTarget ->
                WorkingDirectoryUpdate.Receipt.create mismatchedTarget operation true
                |> Result.isError
                |> should equal true)

        assertCompleteBinding branchOperation
        assertCompleteBinding connectOperation
