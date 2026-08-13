namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI.Command
open Grace.Shared
open Grace.Shared.Constants
open Grace.Types.Common
open NUnit.Framework
open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text

/// Exercises immutable Working Directory Update topology reconciliation without opening a local path.
module WorkingDirectoryUpdateTopologyTests =
    /// Extracts a successful construction or makes the failing test report the rejected shape.
    let private required =
        function
        | Ok value -> value
        | Error error -> failwith error

    /// Produces complete deterministic dual hashes for a declared file value.
    let private hashes (value: string) =
        let bytes = Encoding.UTF8.GetBytes(value)

        SHA256.HashData(bytes)
        |> Convert.ToHexString
        |> fun sha256 -> Sha256Hash(sha256.ToLowerInvariant()), Blake3Hash(ContentAddress.computeBlake3Hex bytes)

    /// Declares a selected file using exact target bytes.
    let private targetFile (path: string) (value: string) =
        let sha256Hash, blake3Hash = hashes value
        WorkingDirectoryUpdateContracts.PreparedManifestEntry.File(RelativePath path, sha256Hash, blake3Hash)

    /// Creates one normalized immutable selected manifest.
    let private manifest entries =
        WorkingDirectoryUpdateContracts.PreparedManifest.create entries
        |> required

    /// Creates one tracked file with the supplied accepted old identity.
    let private trackedFile (path: string) (value: string) =
        let sha256Hash, blake3Hash = hashes value

        LocalFileVersion.CreateWithHashes
            (RelativePath path)
            sha256Hash
            blake3Hash
            false
            (int64 value.Length)
            (Grace.Shared.Utilities.getCurrentInstant ())
            true
            DateTime.UnixEpoch

    /// Creates one complete accepted status whose index explicitly includes tracked directories and files.
    let private status (trackedDirectories: string list) (trackedFiles: LocalFileVersion list) =
        let index = GraceIndex()

        trackedDirectories
        |> List.iteri (fun indexValue path ->
            let directory =
                LocalDirectoryVersion.CreateWithHashes
                    (Guid.NewGuid())
                    OwnerId.Empty
                    OrganizationId.Empty
                    RepositoryId.Empty
                    (RelativePath path)
                    (Sha256Hash $"directory-{indexValue}")
                    (Blake3Hash $"directory-{indexValue}")
                    (List<DirectoryVersionId>())
                    (List<LocalFileVersion>())
                    0L
                    DateTime.UnixEpoch

            index[directory.DirectoryVersionId] <- directory)

        if trackedFiles |> List.isEmpty |> not then
            let root =
                LocalDirectoryVersion.CreateWithHashes
                    (Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"))
                    OwnerId.Empty
                    OrganizationId.Empty
                    RepositoryId.Empty
                    (RelativePath RootDirectoryPath)
                    (Sha256Hash "root")
                    (Blake3Hash "root")
                    (List<DirectoryVersionId>())
                    (List<LocalFileVersion>(trackedFiles))
                    0L
                    DateTime.UnixEpoch

            index[root.DirectoryVersionId] <- root

        { GraceStatus.Default with Index = index }

    /// Declares an observed regular file with exact dual-hash evidence.
    let private file (path: string) (value: string) =
        let sha256Hash, blake3Hash = hashes value
        WorkingDirectoryUpdate.Topology.RelevantEntry.File(RelativePath path, sha256Hash, blake3Hash)

    /// Creates one immutable, caller-captured relevant topology snapshot.
    let private snapshot entries =
        WorkingDirectoryUpdate.Topology.RelevantTopology.create entries
        |> required

    /// Evaluates one pure topology input tuple.
    let private reconcile admission accepted prepared actual = WorkingDirectoryUpdate.Topology.reconcile admission accepted prepared actual

    /// Extracts requirements from a successful reconciliation decision.
    let private planned =
        function
        | WorkingDirectoryUpdate.Topology.Reconciled requirements -> requirements
        | WorkingDirectoryUpdate.Topology.Rejected rejection ->
            failwith $"Expected reconciliation but received {WorkingDirectoryUpdate.Topology.Rejection.classification rejection}."

    /// Asserts the evaluator rejects the complete immutable tuple.
    let private rejected result =
        match result with
        | WorkingDirectoryUpdate.Topology.Rejected _ -> ()
        | WorkingDirectoryUpdate.Topology.Reconciled _ -> Assert.Fail("Expected pure topology reconciliation to reject.")

    /// Returns each requirement role and its convergence state in application order.
    let private roles requirements =
        requirements
        |> WorkingDirectoryUpdate.Topology.Requirements.items
        |> List.map (fun requirement ->
            WorkingDirectoryUpdate.Topology.Requirement.role requirement, WorkingDirectoryUpdate.Topology.Requirement.state requirement)

    /// Proves a fresh tracked replacement preserves the exact accepted old identity until its copy action.
    [<Test>]
    let ``fresh tracked replacement carries old dual hashes and needs application`` () =
        let requirements =
            reconcile
                WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh
                (status [] [
                    trackedFile "replace.txt" "old"
                 ])
                (manifest [ targetFile "replace.txt" "new" ])
                (snapshot [ file "replace.txt" "old" ])
            |> planned

        let requirement =
            WorkingDirectoryUpdate.Topology.Requirements.items requirements
            |> List.exactlyOne

        WorkingDirectoryUpdate.Topology.Requirement.role requirement
        |> should equal WorkingDirectoryUpdate.Topology.Role.Copy

        WorkingDirectoryUpdate.Topology.Requirement.state requirement
        |> should equal WorkingDirectoryUpdate.Topology.Convergence.NeedsApply

        WorkingDirectoryUpdate.Topology.Requirement.expectedCurrent requirement
        |> should equal (WorkingDirectoryUpdate.Topology.ExpectedEntry.File(hashes "old"))

        WorkingDirectoryUpdate.Topology.Requirement.expectedFinal requirement
        |> should equal (WorkingDirectoryUpdate.Topology.ExpectedEntry.File(hashes "new"))

    /// Proves fresh reconciliation preserves accepted file and directory identities instead of treating their absence as safe creation.
    [<Test>]
    let ``fresh reconciliation rejects missing accepted tracked file and directory identities`` () =
        let missingFile =
            reconcile
                WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh
                (status [] [
                    trackedFile "accepted-file.txt" "accepted-old"
                 ])
                (manifest [ targetFile "accepted-file.txt" "target-new" ])
                (snapshot [])

        match missingFile with
        | WorkingDirectoryUpdate.Topology.Rejected rejection ->
            WorkingDirectoryUpdate.Topology.Rejection.path rejection
            |> should equal (RelativePath "accepted-file.txt")

            WorkingDirectoryUpdate.Topology.Rejection.classification rejection
            |> should equal WorkingDirectoryUpdate.Topology.RejectionClassification.IdentityDrift
        | WorkingDirectoryUpdate.Topology.Reconciled _ -> Assert.Fail("A missing accepted file must not become an absent copy precondition.")

        let missingDirectory =
            reconcile
                WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh
                (status [ "accepted-directory" ] [])
                (manifest [ WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory(RelativePath "accepted-directory") ])
                (snapshot [])

        match missingDirectory with
        | WorkingDirectoryUpdate.Topology.Rejected rejection ->
            WorkingDirectoryUpdate.Topology.Rejection.path rejection
            |> should equal (RelativePath "accepted-directory")

            WorkingDirectoryUpdate.Topology.Rejection.classification rejection
            |> should equal WorkingDirectoryUpdate.Topology.RejectionClassification.IdentityDrift
        | WorkingDirectoryUpdate.Topology.Reconciled _ -> Assert.Fail("A missing accepted directory must not become an absent create-directory precondition.")

    /// Proves matching tracked files and directories stay explicit assertions in a fresh plan.
    [<Test>]
    let ``fresh retained file and directory remain explicit needs-apply assertions`` () =
        let requirements =
            reconcile
                WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh
                (status [ "src" ] [
                    trackedFile "src/keep.txt" "same"
                 ])
                (manifest [ WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory(RelativePath "src")
                            targetFile "src/keep.txt" "same" ])
                (snapshot [ WorkingDirectoryUpdate.Topology.RelevantEntry.Directory(RelativePath "src")
                            file "src/keep.txt" "same" ])
            |> planned

        roles requirements
        |> should
            equal
            [
                WorkingDirectoryUpdate.Topology.Role.Retained, WorkingDirectoryUpdate.Topology.Convergence.NeedsApply
                WorkingDirectoryUpdate.Topology.Role.Retained, WorkingDirectoryUpdate.Topology.Convergence.NeedsApply
            ]

    /// Proves nested and empty selected directories are represented even when no filesystem action has happened yet.
    [<Test>]
    let ``fresh absent nested and empty directories each require creation`` () =
        let requirements =
            reconcile
                WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh
                GraceStatus.Default
                (manifest [ WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory(RelativePath "empty")
                            WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory(RelativePath "src/nested") ])
                (snapshot [])
            |> planned

        roles requirements
        |> should
            equal
            [
                WorkingDirectoryUpdate.Topology.Role.CreateDirectory, WorkingDirectoryUpdate.Topology.Convergence.NeedsApply
                WorkingDirectoryUpdate.Topology.Role.CreateDirectory, WorkingDirectoryUpdate.Topology.Convergence.NeedsApply
                WorkingDirectoryUpdate.Topology.Role.CreateDirectory, WorkingDirectoryUpdate.Topology.Convergence.NeedsApply
            ]

    /// Proves both file-directory transition families retain ordered, exact preconditions.
    [<Test>]
    let ``type swaps remove the accepted tracked shape before creating the target shape`` () =
        let directoryToFile =
            reconcile
                WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh
                (status [ "swap" ] [
                    trackedFile "swap/old.txt" "old"
                 ])
                (manifest [ targetFile "swap" "new" ])
                (snapshot [ WorkingDirectoryUpdate.Topology.RelevantEntry.Directory(RelativePath "swap")
                            file "swap/old.txt" "old" ])
            |> planned

        roles directoryToFile
        |> should
            equal
            [
                WorkingDirectoryUpdate.Topology.Role.RemoveFile, WorkingDirectoryUpdate.Topology.Convergence.NeedsApply
                WorkingDirectoryUpdate.Topology.Role.RemoveDirectory, WorkingDirectoryUpdate.Topology.Convergence.NeedsApply
                WorkingDirectoryUpdate.Topology.Role.Copy, WorkingDirectoryUpdate.Topology.Convergence.NeedsApply
            ]

        let fileToDirectory =
            reconcile
                WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh
                (status [] [ trackedFile "swap" "old" ])
                (manifest [ WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory(RelativePath "swap") ])
                (snapshot [ file "swap" "old" ])
            |> planned

        roles fileToDirectory
        |> should
            equal
            [
                WorkingDirectoryUpdate.Topology.Role.RemoveFile, WorkingDirectoryUpdate.Topology.Convergence.NeedsApply
                WorkingDirectoryUpdate.Topology.Role.CreateDirectory, WorkingDirectoryUpdate.Topology.Convergence.NeedsApply
            ]

    /// Proves exact adoption converges files, removals, and directories only from exact local evidence.
    [<Test>]
    let ``exact adoption marks exact file removal and directory evidence already satisfied`` () =
        let requirements =
            reconcile
                WorkingDirectoryUpdate.Topology.AdmissionMode.ExactAdoption
                (status [ "ready" ] [
                    trackedFile "gone.txt" "old"
                 ])
                (manifest [ WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory(RelativePath "ready")
                            targetFile "adopted.txt" "new" ])
                (snapshot [ WorkingDirectoryUpdate.Topology.RelevantEntry.Directory(RelativePath "ready")
                            file "adopted.txt" "new" ])
            |> planned

        roles requirements
        |> List.forall (fun (_, state) -> state = WorkingDirectoryUpdate.Topology.Convergence.AlreadySatisfied)
        |> should equal true

    /// Proves exact adoption can retain finished work while preserving exact old preconditions for the remainder.
    [<Test>]
    let ``mixed partial adoption skips only satisfied copy requirements`` () =
        let requirements =
            reconcile
                WorkingDirectoryUpdate.Topology.AdmissionMode.ExactAdoption
                (status [] [
                    trackedFile "first.txt" "old-one"
                    trackedFile "second.txt" "old-two"
                 ])
                (manifest [ targetFile "first.txt" "new-one"
                            targetFile "second.txt" "new-two" ])
                (snapshot [ file "first.txt" "new-one"
                            file "second.txt" "old-two" ])
            |> planned

        roles requirements
        |> should
            equal
            [
                WorkingDirectoryUpdate.Topology.Role.Copy, WorkingDirectoryUpdate.Topology.Convergence.AlreadySatisfied
                WorkingDirectoryUpdate.Topology.Role.Copy, WorkingDirectoryUpdate.Topology.Convergence.NeedsApply
            ]

    /// Proves the negative collision, drift, malformed-manifest, root, and reparse families stay at the pure seam.
    [<Test>]
    let ``reconciliation rejects unsafe fresh drift occupations aliases root escape reparse and malformed manifests`` () =
        reconcile
            WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh
            (status [] [
                trackedFile "replace.txt" "old"
             ])
            (manifest [ targetFile "replace.txt" "new" ])
            (snapshot [ file "replace.txt" "new" ])
        |> rejected

        reconcile
            WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh
            (status [] [
                trackedFile "replace.txt" "old"
             ])
            (manifest [ targetFile "replace.txt" "new" ])
            (snapshot [ file "replace.txt" "drift" ])
        |> rejected

        reconcile
            WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh
            GraceStatus.Default
            (manifest [ targetFile "occupied.txt" "new" ])
            (snapshot [ WorkingDirectoryUpdate.Topology.RelevantEntry.Untracked(RelativePath "occupied.txt") ])
        |> rejected

        reconcile
            WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh
            GraceStatus.Default
            (manifest [ targetFile "ignored.txt" "new" ])
            (snapshot [ WorkingDirectoryUpdate.Topology.RelevantEntry.Ignored(RelativePath "ignored.txt") ])
        |> rejected

        reconcile
            WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh
            (status [ "replace" ] [])
            (manifest [ targetFile "replace" "new" ])
            (snapshot [ WorkingDirectoryUpdate.Topology.RelevantEntry.Directory(RelativePath "replace")
                        WorkingDirectoryUpdate.Topology.RelevantEntry.Ignored(RelativePath "replace/user.txt") ])
        |> rejected

        reconcile
            WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh
            GraceStatus.Default
            (manifest [ targetFile "safe.txt" "new" ])
            (snapshot [ WorkingDirectoryUpdate.Topology.RelevantEntry.ReparsePoint(RelativePath "safe.txt") ])
        |> rejected

        reconcile
            WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh
            GraceStatus.Default
            (manifest [ targetFile "safe.txt" "new" ])
            (snapshot [ file "safe.txt" "old"
                        file "SAFE.txt" "old" ])
        |> rejected

        reconcile
            WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh
            GraceStatus.Default
            (manifest [ targetFile "safe.txt" "new" ])
            (snapshot [ WorkingDirectoryUpdate.Topology.RelevantEntry.Untracked(RelativePath "../escape") ])
        |> rejected

        WorkingDirectoryUpdateContracts.PreparedManifest.create [ targetFile "case.txt" "one"
                                                                  targetFile "CASE.txt" "two" ]
        |> Result.isError
        |> should equal true

    /// Proves irrelevant user content is excluded while a tracked removal and a zero-action assertion stay represented.
    [<Test>]
    let ``unrelated user content is excluded but tracked removals and zero-action assertions remain relevant`` () =
        let removal =
            reconcile
                WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh
                (status [ "retained" ] [
                    trackedFile "gone.txt" "old"
                 ])
                (manifest [ WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory(RelativePath "retained") ])
                (snapshot [ file "gone.txt" "old"
                            WorkingDirectoryUpdate.Topology.RelevantEntry.Directory(RelativePath "retained")
                            WorkingDirectoryUpdate.Topology.RelevantEntry.Ignored(RelativePath "notes.txt")
                            WorkingDirectoryUpdate.Topology.RelevantEntry.Untracked(RelativePath "retained/user.txt") ])
            |> planned

        roles removal
        |> should
            equal
            [
                WorkingDirectoryUpdate.Topology.Role.RemoveFile, WorkingDirectoryUpdate.Topology.Convergence.NeedsApply
                WorkingDirectoryUpdate.Topology.Role.Retained, WorkingDirectoryUpdate.Topology.Convergence.NeedsApply
            ]

        WorkingDirectoryUpdate.Topology.Requirements.matchesExpected
            removal
            (snapshot [ file "gone.txt" "old"
                        WorkingDirectoryUpdate.Topology.RelevantEntry.Directory(RelativePath "retained")
                        WorkingDirectoryUpdate.Topology.RelevantEntry.Ignored(RelativePath "notes.txt")
                        WorkingDirectoryUpdate.Topology.RelevantEntry.Untracked(RelativePath "retained/user.txt") ])
        |> should equal true

    /// Proves prefix advancement changes only the next owned action and makes later topology drift observable.
    [<Test>]
    let ``prefix advancement advances one action and rejects any remaining or retained drift`` () =
        let requirements =
            reconcile
                WorkingDirectoryUpdate.Topology.AdmissionMode.ExactAdoption
                (status [] [
                    trackedFile "first.txt" "old-one"
                    trackedFile "second.txt" "old-two"
                    trackedFile "keep.txt" "same"
                 ])
                (manifest [ targetFile "first.txt" "new-one"
                            targetFile "second.txt" "new-two"
                            targetFile "keep.txt" "same" ])
                (snapshot [ file "first.txt" "old-one"
                            file "second.txt" "old-two"
                            file "keep.txt" "same" ])
            |> planned

        let first =
            WorkingDirectoryUpdate.Topology.Requirements.items requirements
            |> List.head

        let advanced =
            WorkingDirectoryUpdate.Topology.Requirements.advance first requirements
            |> required

        roles advanced
        |> should
            equal
            [
                WorkingDirectoryUpdate.Topology.Role.Copy, WorkingDirectoryUpdate.Topology.Convergence.AlreadySatisfied
                WorkingDirectoryUpdate.Topology.Role.Retained, WorkingDirectoryUpdate.Topology.Convergence.AlreadySatisfied
                WorkingDirectoryUpdate.Topology.Role.Copy, WorkingDirectoryUpdate.Topology.Convergence.NeedsApply
            ]

        WorkingDirectoryUpdate.Topology.Requirements.matchesExpected
            advanced
            (snapshot [ file "first.txt" "new-one"
                        file "second.txt" "old-two"
                        file "keep.txt" "same" ])
        |> should equal true

        WorkingDirectoryUpdate.Topology.Requirements.matchesExpected
            advanced
            (snapshot [ file "first.txt" "new-one"
                        file "second.txt" "drift"
                        file "keep.txt" "same" ])
        |> should equal false

        WorkingDirectoryUpdate.Topology.Requirements.matchesExpected
            advanced
            (snapshot [ file "first.txt" "new-one"
                        file "second.txt" "old-two"
                        file "keep.txt" "drift" ])
        |> should equal false

    /// Proves a prefix comparison blocks newly created user content before a remaining recursive directory removal.
    [<Test>]
    let ``prefix comparison rejects a late undeclared descendant below a pending destructive directory`` () =
        let requirements =
            reconcile
                WorkingDirectoryUpdate.Topology.AdmissionMode.ExactAdoption
                (status [ "obsolete" ] [
                    trackedFile "obsolete/tracked.txt" "old"
                 ])
                (manifest [])
                (snapshot [ WorkingDirectoryUpdate.Topology.RelevantEntry.Directory(RelativePath "obsolete")
                            file "obsolete/tracked.txt" "old" ])
            |> planned

        let removedFile =
            WorkingDirectoryUpdate.Topology.Requirements.items requirements
            |> List.head

        let advanced =
            WorkingDirectoryUpdate.Topology.Requirements.advance removedFile requirements
            |> required

        WorkingDirectoryUpdate.Topology.Requirements.matchesExpected
            advanced
            (snapshot [ WorkingDirectoryUpdate.Topology.RelevantEntry.Directory(RelativePath "obsolete") ])
        |> should equal true

        WorkingDirectoryUpdate.Topology.Requirements.matchesExpected
            advanced
            (snapshot [ WorkingDirectoryUpdate.Topology.RelevantEntry.Directory(RelativePath "obsolete")
                        WorkingDirectoryUpdate.Topology.RelevantEntry.Untracked(RelativePath "obsolete/user.txt") ])
        |> should equal false

        [
            WorkingDirectoryUpdate.Topology.RelevantEntry.Ignored(RelativePath "obsolete/ignored.txt")
            WorkingDirectoryUpdate.Topology.RelevantEntry.ReparsePoint(RelativePath "obsolete/reparse")
            file "obsolete/undeclared.txt" "ordinary-local-content"
        ]
        |> List.iter (fun lateDescendant ->
            WorkingDirectoryUpdate.Topology.Requirements.matchesExpected
                advanced
                (snapshot [ WorkingDirectoryUpdate.Topology.RelevantEntry.Directory(RelativePath "obsolete")
                            lateDescendant ])
            |> should equal false)
