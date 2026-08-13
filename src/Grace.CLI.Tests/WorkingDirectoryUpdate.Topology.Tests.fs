namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.Command
open Grace.Shared
open Grace.Shared.Constants
open Grace.Types.Common
open NUnit.Framework
open NodaTime
open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text

/// Exercises the finite pure Working Directory Update reconciliation matrix with validator-complete status graphs.
module WorkingDirectoryUpdateTopologyTests =
    let private required =
        function
        | Ok value -> value
        | Error error -> failwith error

    let private hashes (value: string) =
        let bytes = Encoding.UTF8.GetBytes value

        Sha256Hash(
            Convert
                .ToHexString(SHA256.HashData bytes)
                .ToLowerInvariant()
        ),
        Blake3Hash(ContentAddress.computeBlake3Hex bytes)

    let private targetFile (path: string) (value: string) =
        let sha256Hash, blake3Hash = hashes value
        WorkingDirectoryUpdateContracts.PreparedManifestEntry.File(RelativePath path, sha256Hash, blake3Hash)

    let private manifest entries =
        WorkingDirectoryUpdateContracts.PreparedManifest.create entries
        |> required

    let private trackedFile (path: string) (value: string) : LocalFileVersion =
        let sha256Hash, blake3Hash = hashes value

        LocalFileVersion.CreateWithHashes
            (RelativePath path)
            sha256Hash
            blake3Hash
            false
            (int64 value.Length)
            (Instant.FromUnixTimeTicks 0L)
            true
            DateTime.UnixEpoch

    let private parent (path: string) =
        let index = path.LastIndexOf('/')
        if index < 0 then RootDirectoryPath else path.Substring(0, index)

    /// Builds one rooted, parent-linked recursive dual-hash graph accepted by production validation.
    let private status (directories: string list) (files: LocalFileVersion list) =
        let paths =
            seq {
                yield RootDirectoryPath
                yield! directories

                for file in files do
                    yield parent (string file.RelativePath)
            }
            |> Seq.distinct
            |> Seq.sortByDescending (fun path -> if path = RootDirectoryPath then 0 else path.Split('/').Length)
            |> Seq.toArray

        let ids =
            paths
            |> Array.map (fun path -> path, Guid.NewGuid())
            |> dict

        let built = Dictionary<string, LocalDirectoryVersion>()

        for path in paths do
            let children =
                paths
                |> Array.filter (fun child -> child <> path && parent child = path)
                |> Array.map (fun child -> built[child])

            let directFiles: LocalFileVersion array =
                files
                |> List.filter (fun (file: LocalFileVersion) -> parent (string file.RelativePath) = path)
                |> List.toArray

            let entries =
                seq {
                    yield!
                        children
                        |> Seq.map (fun child ->
                            Services.DirectoryVersionPreimageEntry.Directory child.RelativePath child.Size child.Blake3Hash child.Sha256Hash)

                    yield!
                        directFiles
                        |> Seq.map (fun file -> Services.DirectoryVersionPreimageEntry.File file.RelativePath file.Size file.Blake3Hash file.Sha256Hash)
                }
                |> Seq.toArray

            built[path] <- LocalDirectoryVersion.CreateWithHashes
                               ids[path]
                               OwnerId.Empty
                               OrganizationId.Empty
                               RepositoryId.Empty
                               (RelativePath path)
                               (Services.computeSha256ForDirectoryEntries (RelativePath path) entries)
                               (Services.computeBlake3ForDirectory (RelativePath path) entries)
                               (List<DirectoryVersionId>(
                                   children
                                   |> Array.map (fun child -> child.DirectoryVersionId)
                               ))
                               (List<LocalFileVersion>(directFiles))
                               (entries |> Array.sumBy (fun entry -> entry.Size))
                               DateTime.UnixEpoch

        let root = built[RootDirectoryPath]
        let index = GraceIndex()

        for directory in built.Values do
            index[directory.DirectoryVersionId] <- directory

        let result =
            {
                Index = index
                RootDirectoryId = root.DirectoryVersionId
                RootDirectorySha256Hash = root.Sha256Hash
                RootDirectoryBlake3Hash = root.Blake3Hash
                LastSuccessfulFileUpload = Instant.FromUnixTimeTicks 0L
                LastSuccessfulDirectoryVersionUpload = Instant.FromUnixTimeTicks 0L
            }

        LocalStateDb.validateCompleteStatusTree result
        |> required

        result

    let private file (path: string) (value: string) =
        let sha256Hash, blake3Hash = hashes value
        WorkingDirectoryUpdate.Topology.RelevantEntry.File(RelativePath path, sha256Hash, blake3Hash)

    let private directory (path: string) = WorkingDirectoryUpdate.Topology.RelevantEntry.Directory(RelativePath path)

    let private snapshot entries =
        WorkingDirectoryUpdate.Topology.RelevantTopology.create entries
        |> required

    /// Enforces the production validator gate immediately before each behavior assertion.
    let private reconcile mode accepted selected observed =
        LocalStateDb.validateCompleteStatusTree accepted
        |> required

        WorkingDirectoryUpdate.Topology.reconcile mode accepted selected (snapshot observed)

    let private planned =
        function
        | WorkingDirectoryUpdate.Topology.Reconciled value -> value
        | WorkingDirectoryUpdate.Topology.Rejected rejection -> failwith $"Unexpected {WorkingDirectoryUpdate.Topology.Rejection.classification rejection}"

    let private rejected =
        function
        | WorkingDirectoryUpdate.Topology.Rejected _ -> ()
        | _ -> Assert.Fail("Expected rejection.")

    /// Asserts the deterministic path and classification selected for unsafe topology evidence.
    let private rejectedAs (path: string) classification =
        function
        | WorkingDirectoryUpdate.Topology.Rejected rejection ->
            WorkingDirectoryUpdate.Topology.Rejection.path rejection
            |> should equal (RelativePath path)

            WorkingDirectoryUpdate.Topology.Rejection.classification rejection
            |> should equal classification
        | _ -> Assert.Fail("Expected rejection.")

    /// Projects every immutable requirement field so the tests retain identity, role, mode, and convergence assertions.
    let private details requirements =
        requirements
        |> WorkingDirectoryUpdate.Topology.Requirements.items
        |> List.map (fun item ->
            WorkingDirectoryUpdate.Topology.Requirement.path item,
            WorkingDirectoryUpdate.Topology.Requirement.expectedCurrent item,
            WorkingDirectoryUpdate.Topology.Requirement.expectedFinal item,
            WorkingDirectoryUpdate.Topology.Requirement.role item,
            WorkingDirectoryUpdate.Topology.Requirement.state item,
            WorkingDirectoryUpdate.Topology.Requirement.admissionMode item)

    /// Projects application role and convergence state for concise matrix assertions.
    let private states requirements =
        requirements
        |> WorkingDirectoryUpdate.Topology.Requirements.items
        |> List.map (fun item -> WorkingDirectoryUpdate.Topology.Requirement.role item, WorkingDirectoryUpdate.Topology.Requirement.state item)

    [<Test>]
    let ``validator fixture gate rejects disconnected status before behavior proof`` () =
        let invalid =
            {
                Index = GraceIndex()
                RootDirectoryId = Guid.NewGuid()
                RootDirectorySha256Hash = Sha256Hash "bad"
                RootDirectoryBlake3Hash = Blake3Hash "bad"
                LastSuccessfulFileUpload = Instant.FromUnixTimeTicks 0L
                LastSuccessfulDirectoryVersionUpload = Instant.FromUnixTimeTicks 0L
            }

        LocalStateDb.validateCompleteStatusTree invalid
        |> Result.isError
        |> should equal true

        LocalStateDb.validateCompleteStatusTree (status [] [])
        |> Result.isOk
        |> should equal true

    [<Test>]
    let ``fresh retains exact identities and rejects missing final or wrong accepted evidence`` () =
        let accepted =
            status [ "src" ] [
                trackedFile "src/keep.txt" "old"
                trackedFile "replace.txt" "old"
            ]

        let selected =
            manifest [ WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory(RelativePath "src")
                       targetFile "src/keep.txt" "old"
                       targetFile "replace.txt" "new"
                       targetFile "new.txt" "new" ]

        reconcile
            WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh
            accepted
            selected
            [
                directory "src"
                file "src/keep.txt" "old"
                file "replace.txt" "old"
            ]
        |> planned
        |> states
        |> List.forall (fun (_, state) -> state = WorkingDirectoryUpdate.Topology.Convergence.NeedsApply)
        |> should equal true

        reconcile
            WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh
            accepted
            selected
            [
                directory "src"
                file "src/keep.txt" "old"
                file "replace.txt" "new"
            ]
        |> rejected

        reconcile
            WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh
            accepted
            selected
            [
                directory "src"
                file "src/keep.txt" "old"
                file "replace.txt" "wrong"
            ]
        |> rejected

    [<Test>]
    let ``exact adoption reconciles old absent and final evidence for both type swaps`` () =
        let fileToDirectory = status [] [ trackedFile "swap" "old" ]

        let directoryToFile =
            status [ "swap" ] [
                trackedFile "swap/old.txt" "old"
            ]

        let fileToDirectoryTarget = manifest [ WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory(RelativePath "swap") ]
        let directoryToFileTarget = manifest [ targetFile "swap" "new" ]

        let assertStates accepted selected observed expected =
            reconcile WorkingDirectoryUpdate.Topology.AdmissionMode.ExactAdoption accepted selected observed
            |> planned
            |> states
            |> should equal expected

        assertStates
            fileToDirectory
            fileToDirectoryTarget
            [ file "swap" "old" ]
            [
                WorkingDirectoryUpdate.Topology.Role.RemoveFile, WorkingDirectoryUpdate.Topology.Convergence.NeedsApply
                WorkingDirectoryUpdate.Topology.Role.CreateDirectory, WorkingDirectoryUpdate.Topology.Convergence.NeedsApply
            ]

        assertStates
            fileToDirectory
            fileToDirectoryTarget
            []
            [
                WorkingDirectoryUpdate.Topology.Role.RemoveFile, WorkingDirectoryUpdate.Topology.Convergence.AlreadySatisfied
                WorkingDirectoryUpdate.Topology.Role.CreateDirectory, WorkingDirectoryUpdate.Topology.Convergence.NeedsApply
            ]

        assertStates
            fileToDirectory
            fileToDirectoryTarget
            [ directory "swap" ]
            [
                WorkingDirectoryUpdate.Topology.Role.RemoveFile, WorkingDirectoryUpdate.Topology.Convergence.AlreadySatisfied
                WorkingDirectoryUpdate.Topology.Role.CreateDirectory, WorkingDirectoryUpdate.Topology.Convergence.AlreadySatisfied
            ]

        assertStates
            directoryToFile
            directoryToFileTarget
            [
                directory "swap"
                file "swap/old.txt" "old"
            ]
            [
                WorkingDirectoryUpdate.Topology.Role.RemoveFile, WorkingDirectoryUpdate.Topology.Convergence.NeedsApply
                WorkingDirectoryUpdate.Topology.Role.RemoveDirectory, WorkingDirectoryUpdate.Topology.Convergence.NeedsApply
                WorkingDirectoryUpdate.Topology.Role.Copy, WorkingDirectoryUpdate.Topology.Convergence.NeedsApply
            ]

        assertStates
            directoryToFile
            directoryToFileTarget
            []
            [
                WorkingDirectoryUpdate.Topology.Role.RemoveFile, WorkingDirectoryUpdate.Topology.Convergence.AlreadySatisfied
                WorkingDirectoryUpdate.Topology.Role.RemoveDirectory, WorkingDirectoryUpdate.Topology.Convergence.AlreadySatisfied
                WorkingDirectoryUpdate.Topology.Role.Copy, WorkingDirectoryUpdate.Topology.Convergence.NeedsApply
            ]

        assertStates
            directoryToFile
            directoryToFileTarget
            [ file "swap" "new" ]
            [
                WorkingDirectoryUpdate.Topology.Role.RemoveFile, WorkingDirectoryUpdate.Topology.Convergence.AlreadySatisfied
                WorkingDirectoryUpdate.Topology.Role.RemoveDirectory, WorkingDirectoryUpdate.Topology.Convergence.AlreadySatisfied
                WorkingDirectoryUpdate.Topology.Role.Copy, WorkingDirectoryUpdate.Topology.Convergence.AlreadySatisfied
            ]

        reconcile WorkingDirectoryUpdate.Topology.AdmissionMode.ExactAdoption directoryToFile directoryToFileTarget [ file "swap" "wrong" ]
        |> rejected

        reconcile WorkingDirectoryUpdate.Topology.AdmissionMode.ExactAdoption fileToDirectory fileToDirectoryTarget [ file "swap" "wrong" ]
        |> rejected

    [<Test>]
    let ``fresh matrix covers absent create removal same-kind replacement and zero-action assertions`` () =
        let empty = status [] []

        reconcile WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh empty (manifest [ targetFile "create.txt" "new" ]) []
        |> planned
        |> states
        |> should
            equal
            [
                WorkingDirectoryUpdate.Topology.Role.Copy, WorkingDirectoryUpdate.Topology.Convergence.NeedsApply
            ]

        reconcile WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh empty (manifest [ targetFile "create.txt" "new" ]) [ file "create.txt" "new" ]
        |> rejected

        let accepted =
            status [ "gone" ] [
                trackedFile "gone/old.txt" "old"
                trackedFile "replace.txt" "old"
            ]

        reconcile
            WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh
            accepted
            (manifest [ targetFile "replace.txt" "new" ])
            [
                directory "gone"
                file "gone/old.txt" "old"
                file "replace.txt" "old"
            ]
        |> planned
        |> states
        |> should
            equal
            [
                WorkingDirectoryUpdate.Topology.Role.RemoveFile, WorkingDirectoryUpdate.Topology.Convergence.NeedsApply
                WorkingDirectoryUpdate.Topology.Role.RemoveDirectory, WorkingDirectoryUpdate.Topology.Convergence.NeedsApply
                WorkingDirectoryUpdate.Topology.Role.Copy, WorkingDirectoryUpdate.Topology.Convergence.NeedsApply
            ]

        let retained = status [ "empty" ] []

        reconcile
            WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh
            retained
            (manifest [ WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory(RelativePath "empty") ])
            [ directory "empty" ]
        |> planned
        |> states
        |> should
            equal
            [
                WorkingDirectoryUpdate.Topology.Role.Retained, WorkingDirectoryUpdate.Topology.Convergence.NeedsApply
            ]

    [<Test>]
    let ``prefix comparison rejects every late descendant under remaining destructive scope and advances one action`` () =
        let accepted =
            status [ "replace" ] [
                trackedFile "replace/old.txt" "old"
            ]

        let requirements =
            reconcile
                WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh
                accepted
                (manifest [ targetFile "replace" "new" ])
                [
                    directory "replace"
                    file "replace/old.txt" "old"
                ]
            |> planned

        let first =
            requirements
            |> WorkingDirectoryUpdate.Topology.Requirements.items
            |> List.head

        let advanced =
            WorkingDirectoryUpdate.Topology.Requirements.advance first requirements
            |> required

        for late in
            [
                file "replace/late.txt" "late"
                WorkingDirectoryUpdate.Topology.RelevantEntry.Ignored(RelativePath "replace/late.txt")
                WorkingDirectoryUpdate.Topology.RelevantEntry.Untracked(RelativePath "replace/late.txt")
                WorkingDirectoryUpdate.Topology.RelevantEntry.ReparsePoint(RelativePath "replace/late.txt")
            ] do
            WorkingDirectoryUpdate.Topology.Requirements.matchesExpected advanced (snapshot [ directory "replace"; late ])
            |> should equal false

        let items =
            advanced
            |> WorkingDirectoryUpdate.Topology.Requirements.items

        items
        |> List.filter (fun item -> WorkingDirectoryUpdate.Topology.Requirement.state item = WorkingDirectoryUpdate.Topology.Convergence.AlreadySatisfied)
        |> List.length
        |> should equal 1

    [<Test>]
    let ``unrelated entries remain excluded while retained assertions remain exact`` () =
        let accepted =
            status [ "retained" ] [
                trackedFile "retained/keep.txt" "same"
            ]

        let requirements =
            reconcile
                WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh
                accepted
                (manifest [ WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory(RelativePath "retained")
                            targetFile "retained/keep.txt" "same" ])
                [
                    directory "retained"
                    file "retained/keep.txt" "same"
                    WorkingDirectoryUpdate.Topology.RelevantEntry.Untracked(RelativePath "outside.txt")
                ]
            |> planned

        WorkingDirectoryUpdate.Topology.Requirements.matchesExpected
            requirements
            (snapshot [ directory "retained"
                        file "retained/keep.txt" "same"
                        WorkingDirectoryUpdate.Topology.RelevantEntry.Untracked(RelativePath "outside.txt") ])
        |> should equal true

    [<Test>]
    let ``fresh type swaps nested directories and zero action are all explicit requirements`` () =
        let fileToDirectory =
            reconcile
                WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh
                (status [] [ trackedFile "swap" "old" ])
                (manifest [ WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory(RelativePath "swap") ])
                [ file "swap" "old" ]
            |> planned

        states fileToDirectory
        |> should
            equal
            [
                WorkingDirectoryUpdate.Topology.Role.RemoveFile, WorkingDirectoryUpdate.Topology.Convergence.NeedsApply
                WorkingDirectoryUpdate.Topology.Role.CreateDirectory, WorkingDirectoryUpdate.Topology.Convergence.NeedsApply
            ]

        let directoryToFile =
            reconcile
                WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh
                (status [ "swap"; "swap/nested" ] [
                    trackedFile "swap/nested/old.txt" "old"
                 ])
                (manifest [ targetFile "swap" "new" ])
                [
                    directory "swap"
                    directory "swap/nested"
                    file "swap/nested/old.txt" "old"
                ]
            |> planned

        states directoryToFile
        |> should
            equal
            [
                WorkingDirectoryUpdate.Topology.Role.RemoveFile, WorkingDirectoryUpdate.Topology.Convergence.NeedsApply
                WorkingDirectoryUpdate.Topology.Role.RemoveDirectory, WorkingDirectoryUpdate.Topology.Convergence.NeedsApply
                WorkingDirectoryUpdate.Topology.Role.RemoveDirectory, WorkingDirectoryUpdate.Topology.Convergence.NeedsApply
                WorkingDirectoryUpdate.Topology.Role.Copy, WorkingDirectoryUpdate.Topology.Convergence.NeedsApply
            ]

        let nestedAndEmpty =
            reconcile
                WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh
                (status [] [])
                (manifest [ WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory(RelativePath "empty")
                            targetFile "nested/deep.txt" "new" ])
                []
            |> planned

        states nestedAndEmpty
        |> should
            equal
            [
                WorkingDirectoryUpdate.Topology.Role.CreateDirectory, WorkingDirectoryUpdate.Topology.Convergence.NeedsApply
                WorkingDirectoryUpdate.Topology.Role.CreateDirectory, WorkingDirectoryUpdate.Topology.Convergence.NeedsApply
                WorkingDirectoryUpdate.Topology.Role.Copy, WorkingDirectoryUpdate.Topology.Convergence.NeedsApply
            ]

        let zeroAction =
            reconcile
                WorkingDirectoryUpdate.Topology.AdmissionMode.ExactAdoption
                (status [ "retained" ] [
                    trackedFile "retained/file.txt" "same"
                 ])
                (manifest [ WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory(RelativePath "retained")
                            targetFile "retained/file.txt" "same" ])
                [
                    directory "retained"
                    file "retained/file.txt" "same"
                ]
            |> planned

        states zeroAction
        |> should
            equal
            [
                WorkingDirectoryUpdate.Topology.Role.Retained, WorkingDirectoryUpdate.Topology.Convergence.AlreadySatisfied
                WorkingDirectoryUpdate.Topology.Role.Retained, WorkingDirectoryUpdate.Topology.Convergence.AlreadySatisfied
            ]

    [<Test>]
    let ``exact adoption ordinary file and directory creates converge only on their exact selected identities`` () =
        let accepted = status [] []
        let fileSha256, fileBlake3 = hashes "new"
        let fileFinal = WorkingDirectoryUpdate.Topology.ExpectedEntry.File(fileSha256, fileBlake3)
        let fileTarget = manifest [ targetFile "created.txt" "new" ]
        let directoryTarget = manifest [ WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory(RelativePath "created") ]

        reconcile WorkingDirectoryUpdate.Topology.AdmissionMode.ExactAdoption accepted fileTarget []
        |> planned
        |> details
        |> should
            equal
            [
                RelativePath "created.txt",
                WorkingDirectoryUpdate.Topology.ExpectedEntry.Absent,
                fileFinal,
                WorkingDirectoryUpdate.Topology.Role.Copy,
                WorkingDirectoryUpdate.Topology.Convergence.NeedsApply,
                WorkingDirectoryUpdate.Topology.AdmissionMode.ExactAdoption
            ]

        reconcile WorkingDirectoryUpdate.Topology.AdmissionMode.ExactAdoption accepted fileTarget [ file "created.txt" "new" ]
        |> planned
        |> details
        |> should
            equal
            [
                RelativePath "created.txt",
                fileFinal,
                fileFinal,
                WorkingDirectoryUpdate.Topology.Role.Copy,
                WorkingDirectoryUpdate.Topology.Convergence.AlreadySatisfied,
                WorkingDirectoryUpdate.Topology.AdmissionMode.ExactAdoption
            ]

        reconcile WorkingDirectoryUpdate.Topology.AdmissionMode.ExactAdoption accepted directoryTarget []
        |> planned
        |> details
        |> should
            equal
            [
                RelativePath "created",
                WorkingDirectoryUpdate.Topology.ExpectedEntry.Absent,
                WorkingDirectoryUpdate.Topology.ExpectedEntry.Directory,
                WorkingDirectoryUpdate.Topology.Role.CreateDirectory,
                WorkingDirectoryUpdate.Topology.Convergence.NeedsApply,
                WorkingDirectoryUpdate.Topology.AdmissionMode.ExactAdoption
            ]

        reconcile WorkingDirectoryUpdate.Topology.AdmissionMode.ExactAdoption accepted directoryTarget [ directory "created" ]
        |> planned
        |> details
        |> should
            equal
            [
                RelativePath "created",
                WorkingDirectoryUpdate.Topology.ExpectedEntry.Directory,
                WorkingDirectoryUpdate.Topology.ExpectedEntry.Directory,
                WorkingDirectoryUpdate.Topology.Role.CreateDirectory,
                WorkingDirectoryUpdate.Topology.Convergence.AlreadySatisfied,
                WorkingDirectoryUpdate.Topology.AdmissionMode.ExactAdoption
            ]

        reconcile WorkingDirectoryUpdate.Topology.AdmissionMode.ExactAdoption accepted fileTarget [ file "created.txt" "wrong" ]
        |> rejectedAs "created.txt" WorkingDirectoryUpdate.Topology.RejectionClassification.IdentityDrift

        reconcile WorkingDirectoryUpdate.Topology.AdmissionMode.ExactAdoption accepted directoryTarget [ file "created" "wrong" ]
        |> rejectedAs "created" WorkingDirectoryUpdate.Topology.RejectionClassification.IdentityDrift

    [<Test>]
    let ``exact adoption same-kind file replacement preserves old identity across old absent and selected-final evidence`` () =
        let accepted =
            status [] [
                trackedFile "replace.txt" "old"
            ]

        let oldSha256, oldBlake3 = hashes "old"
        let finalSha256, finalBlake3 = hashes "new"
        let oldIdentity = WorkingDirectoryUpdate.Topology.ExpectedEntry.File(oldSha256, oldBlake3)
        let finalIdentity = WorkingDirectoryUpdate.Topology.ExpectedEntry.File(finalSha256, finalBlake3)
        let selected = manifest [ targetFile "replace.txt" "new" ]

        let expected current state =
            [
                RelativePath "replace.txt",
                current,
                finalIdentity,
                WorkingDirectoryUpdate.Topology.Role.Copy,
                state,
                WorkingDirectoryUpdate.Topology.AdmissionMode.ExactAdoption
            ]

        reconcile WorkingDirectoryUpdate.Topology.AdmissionMode.ExactAdoption accepted selected [ file "replace.txt" "old" ]
        |> planned
        |> details
        |> should equal (expected oldIdentity WorkingDirectoryUpdate.Topology.Convergence.NeedsApply)

        reconcile WorkingDirectoryUpdate.Topology.AdmissionMode.ExactAdoption accepted selected []
        |> planned
        |> details
        |> should equal (expected WorkingDirectoryUpdate.Topology.ExpectedEntry.Absent WorkingDirectoryUpdate.Topology.Convergence.NeedsApply)

        reconcile WorkingDirectoryUpdate.Topology.AdmissionMode.ExactAdoption accepted selected [ file "replace.txt" "new" ]
        |> planned
        |> details
        |> should equal (expected finalIdentity WorkingDirectoryUpdate.Topology.Convergence.AlreadySatisfied)

        reconcile WorkingDirectoryUpdate.Topology.AdmissionMode.ExactAdoption accepted selected [ file "replace.txt" "wrong" ]
        |> rejectedAs "replace.txt" WorkingDirectoryUpdate.Topology.RejectionClassification.IdentityDrift

    [<Test>]
    let ``fresh admission rejects a missing accepted file or directory with deterministic identity drift`` () =
        reconcile
            WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh
            (status [] [
                trackedFile "required.txt" "old"
             ])
            (manifest [ targetFile "required.txt" "old" ])
            []
        |> rejectedAs "required.txt" WorkingDirectoryUpdate.Topology.RejectionClassification.IdentityDrift

        reconcile
            WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh
            (status [ "required" ] [])
            (manifest [ WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory(RelativePath "required") ])
            []
        |> rejectedAs "required" WorkingDirectoryUpdate.Topology.RejectionClassification.IdentityDrift

    [<Test>]
    let ``single case-only aliases reject reconciliation and cannot satisfy prefix assertions`` () =
        let accepted =
            status [] [
                trackedFile "case.txt" "same"
            ]

        let selected = manifest [ targetFile "case.txt" "same" ]

        reconcile WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh accepted selected [ file "Case.txt" "same" ]
        |> rejectedAs "Case.txt" WorkingDirectoryUpdate.Topology.RejectionClassification.AmbiguousTarget

        reconcile
            WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh
            (status [] [
                trackedFile "Case.txt" "same"
             ])
            (manifest [ targetFile "case.txt" "same" ])
            [ file "Case.txt" "same" ]
        |> rejectedAs "case.txt" WorkingDirectoryUpdate.Topology.RejectionClassification.AmbiguousTarget

        let requirements =
            reconcile WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh accepted selected [ file "case.txt" "same" ]
            |> planned

        WorkingDirectoryUpdate.Topology.Requirements.matchesExpected requirements (snapshot [ file "Case.txt" "same" ])
        |> should equal false

    [<Test>]
    let ``requirements have deterministic normalized path ordering and retain all assertion fields`` () =
        let aSha256, aBlake3 = hashes "a"
        let zSha256, zBlake3 = hashes "z"

        let requirements =
            reconcile
                WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh
                (status [] [])
                (manifest [ WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory(RelativePath "z")
                            WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory(RelativePath "a/b")
                            targetFile "z.txt" "z"
                            targetFile "a.txt" "a" ])
                []
            |> planned

        details requirements
        |> should
            equal
            [
                (RelativePath "a",
                 WorkingDirectoryUpdate.Topology.ExpectedEntry.Absent,
                 WorkingDirectoryUpdate.Topology.ExpectedEntry.Directory,
                 WorkingDirectoryUpdate.Topology.Role.CreateDirectory,
                 WorkingDirectoryUpdate.Topology.Convergence.NeedsApply,
                 WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh)
                (RelativePath "z",
                 WorkingDirectoryUpdate.Topology.ExpectedEntry.Absent,
                 WorkingDirectoryUpdate.Topology.ExpectedEntry.Directory,
                 WorkingDirectoryUpdate.Topology.Role.CreateDirectory,
                 WorkingDirectoryUpdate.Topology.Convergence.NeedsApply,
                 WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh)
                (RelativePath "a/b",
                 WorkingDirectoryUpdate.Topology.ExpectedEntry.Absent,
                 WorkingDirectoryUpdate.Topology.ExpectedEntry.Directory,
                 WorkingDirectoryUpdate.Topology.Role.CreateDirectory,
                 WorkingDirectoryUpdate.Topology.Convergence.NeedsApply,
                 WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh)
                (RelativePath "a.txt",
                 WorkingDirectoryUpdate.Topology.ExpectedEntry.Absent,
                 WorkingDirectoryUpdate.Topology.ExpectedEntry.File(aSha256, aBlake3),
                 WorkingDirectoryUpdate.Topology.Role.Copy,
                 WorkingDirectoryUpdate.Topology.Convergence.NeedsApply,
                 WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh)
                (RelativePath "z.txt",
                 WorkingDirectoryUpdate.Topology.ExpectedEntry.Absent,
                 WorkingDirectoryUpdate.Topology.ExpectedEntry.File(zSha256, zBlake3),
                 WorkingDirectoryUpdate.Topology.Role.Copy,
                 WorkingDirectoryUpdate.Topology.Convergence.NeedsApply,
                 WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh)
            ]

    [<Test>]
    let ``false-positive resistance keeps admission modes retained assertions and one-action prefix advancement distinct`` () =
        let accepted =
            status [ "retained" ] [
                trackedFile "retained/keep.txt" "same"
                trackedFile "changed.txt" "old"
            ]

        let selected =
            manifest [ WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory(RelativePath "retained")
                       targetFile "retained/keep.txt" "same"
                       targetFile "changed.txt" "new" ]

        let observed =
            [
                directory "retained"
                file "retained/keep.txt" "same"
                file "changed.txt" "old"
            ]

        let retained =
            reconcile WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh accepted selected observed
            |> planned

        details retained
        |> List.map (fun (path, _, _, role, state, mode) -> path, role, state, mode)
        |> should
            equal
            [
                (RelativePath "retained",
                 WorkingDirectoryUpdate.Topology.Role.Retained,
                 WorkingDirectoryUpdate.Topology.Convergence.NeedsApply,
                 WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh)
                (RelativePath "changed.txt",
                 WorkingDirectoryUpdate.Topology.Role.Copy,
                 WorkingDirectoryUpdate.Topology.Convergence.NeedsApply,
                 WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh)
                (RelativePath "retained/keep.txt",
                 WorkingDirectoryUpdate.Topology.Role.Retained,
                 WorkingDirectoryUpdate.Topology.Convergence.NeedsApply,
                 WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh)
            ]

        let first =
            retained
            |> WorkingDirectoryUpdate.Topology.Requirements.items
            |> List.find (fun item -> WorkingDirectoryUpdate.Topology.Requirement.role item = WorkingDirectoryUpdate.Topology.Role.Copy)

        WorkingDirectoryUpdate.Topology.Requirements.advance first retained
        |> required
        |> WorkingDirectoryUpdate.Topology.Requirements.items
        |> List.map WorkingDirectoryUpdate.Topology.Requirement.state
        |> should
            equal
            [
                WorkingDirectoryUpdate.Topology.Convergence.NeedsApply
                WorkingDirectoryUpdate.Topology.Convergence.AlreadySatisfied
                WorkingDirectoryUpdate.Topology.Convergence.NeedsApply
            ]

        reconcile WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh (status [] []) (manifest [ targetFile "mode.txt" "new" ]) [ file "mode.txt" "new" ]
        |> rejectedAs "mode.txt" WorkingDirectoryUpdate.Topology.RejectionClassification.IdentityDrift

        reconcile
            WorkingDirectoryUpdate.Topology.AdmissionMode.ExactAdoption
            (status [] [])
            (manifest [ targetFile "mode.txt" "new" ])
            [ file "mode.txt" "new" ]
        |> planned
        |> details
        |> List.map (fun (_, _, _, _, state, _) -> state)
        |> should
            equal
            [
                WorkingDirectoryUpdate.Topology.Convergence.AlreadySatisfied
            ]

    [<Test>]
    let ``pure matrix rejects named unsafe evidence and malformed snapshots`` () =
        let empty = status [] []
        let selected = manifest [ targetFile "target.txt" "new" ]

        reconcile
            WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh
            empty
            selected
            [
                WorkingDirectoryUpdate.Topology.RelevantEntry.Ignored(RelativePath "target.txt")
            ]
        |> rejected

        reconcile
            WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh
            empty
            selected
            [
                WorkingDirectoryUpdate.Topology.RelevantEntry.Untracked(RelativePath "target.txt")
            ]
        |> rejected

        reconcile
            WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh
            empty
            selected
            [
                WorkingDirectoryUpdate.Topology.RelevantEntry.ReparsePoint(RelativePath "target.txt")
            ]
        |> rejected

        reconcile
            WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh
            empty
            selected
            [
                file "Target.txt" "new"
                file "target.txt" "new"
            ]
        |> rejected

        reconcile WorkingDirectoryUpdate.Topology.AdmissionMode.Fresh empty selected [ file "../escape.txt" "new" ]
        |> rejected

        WorkingDirectoryUpdateContracts.PreparedManifest.create [ targetFile "Case.txt" "new"
                                                                  targetFile "case.txt" "new" ]
        |> Result.isError
        |> should equal true
