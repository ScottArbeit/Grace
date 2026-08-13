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
