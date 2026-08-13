namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI.Command
open Grace.Types.Common
open NUnit.Framework

/// Proves the finite per-path transition algebra without reading or changing a filesystem.
module WorkingDirectoryUpdateTransitionTests =
    /// Builds a distinct exact file identity for the table fixture.
    let private file (path: string) (sha256: string) (blake3: string) : WorkingDirectoryUpdateTransition.FileIdentity =
        { Path = RelativePath path; Sha256Hash = Sha256Hash sha256; Blake3Hash = Blake3Hash blake3 }

    /// Wraps the fixture file as an ordinary file identity.
    let private fileIdentity path sha256 blake3 = WorkingDirectoryUpdateTransition.File(file path sha256 blake3)

    /// Wraps one exact normalized directory path as an ordinary directory identity.
    let private directoryIdentity (path: string) = WorkingDirectoryUpdateTransition.Directory(RelativePath path)

    /// Builds one independently expected retained requirement.
    let private retained identity convergence : WorkingDirectoryUpdateTransition.Requirement =
        { Role = WorkingDirectoryUpdateTransition.Retained; Identity = identity; Convergence = convergence }

    /// Builds one independently expected removal requirement.
    let private removal identity convergence : WorkingDirectoryUpdateTransition.Requirement =
        { Role = WorkingDirectoryUpdateTransition.Removal; Identity = identity; Convergence = convergence }

    /// Builds one independently expected materialization requirement.
    let private materialization identity convergence : WorkingDirectoryUpdateTransition.Requirement =
        { Role = WorkingDirectoryUpdateTransition.Materialization; Identity = identity; Convergence = convergence }

    /// Holds one normative input row and its independently specified immutable result.
    type private Row =
        {
            Name: string
            Admission: WorkingDirectoryUpdateTransition.AdmissionMode
            Transition: WorkingDirectoryUpdateTransition.TransitionIntent
            Observation: WorkingDirectoryUpdateTransition.Observation
            Expected: WorkingDirectoryUpdateTransition.Result
        }

    /// Provides the complete positive transition table without deriving any expected row from classifier output.
    let private positiveRows () =
        let acceptedFile = fileIdentity "path" "sha-accepted" "blake-accepted"
        let selectedFile = fileIdentity "path" "sha-final" "blake-final"
        let foreignFile = fileIdentity "path" "sha-foreign" "blake-foreign"
        let acceptedDirectory = directoryIdentity "path"
        let selectedDirectory = directoryIdentity "path"
        let rows = ResizeArray<Row>()

        let add name admission transition observation expected =
            rows.Add({ Name = name; Admission = admission; Transition = transition; Observation = observation; Expected = expected })

        add
            "Fresh retain file"
            WorkingDirectoryUpdateTransition.Fresh
            (WorkingDirectoryUpdateTransition.Retain acceptedFile)
            (WorkingDirectoryUpdateTransition.Present acceptedFile)
            (WorkingDirectoryUpdateTransition.Requirements [ retained acceptedFile WorkingDirectoryUpdateTransition.NeedsApply ])

        add
            "Exact retain file"
            WorkingDirectoryUpdateTransition.ExactAdoption
            (WorkingDirectoryUpdateTransition.Retain acceptedFile)
            (WorkingDirectoryUpdateTransition.Present acceptedFile)
            (WorkingDirectoryUpdateTransition.Requirements [ retained acceptedFile WorkingDirectoryUpdateTransition.AlreadySatisfied ])

        add
            "Fresh retain directory"
            WorkingDirectoryUpdateTransition.Fresh
            (WorkingDirectoryUpdateTransition.Retain acceptedDirectory)
            (WorkingDirectoryUpdateTransition.Present acceptedDirectory)
            (WorkingDirectoryUpdateTransition.Requirements [ retained acceptedDirectory WorkingDirectoryUpdateTransition.NeedsApply ])

        add
            "Exact retain directory"
            WorkingDirectoryUpdateTransition.ExactAdoption
            (WorkingDirectoryUpdateTransition.Retain acceptedDirectory)
            (WorkingDirectoryUpdateTransition.Present acceptedDirectory)
            (WorkingDirectoryUpdateTransition.Requirements [ retained acceptedDirectory WorkingDirectoryUpdateTransition.AlreadySatisfied ])

        add
            "Fresh create file"
            WorkingDirectoryUpdateTransition.Fresh
            (WorkingDirectoryUpdateTransition.Create selectedFile)
            WorkingDirectoryUpdateTransition.Absent
            (WorkingDirectoryUpdateTransition.Requirements [ materialization selectedFile WorkingDirectoryUpdateTransition.NeedsApply ])

        add
            "Exact create file absent"
            WorkingDirectoryUpdateTransition.ExactAdoption
            (WorkingDirectoryUpdateTransition.Create selectedFile)
            WorkingDirectoryUpdateTransition.Absent
            (WorkingDirectoryUpdateTransition.Requirements [ materialization selectedFile WorkingDirectoryUpdateTransition.NeedsApply ])

        add
            "Exact create file final"
            WorkingDirectoryUpdateTransition.ExactAdoption
            (WorkingDirectoryUpdateTransition.Create selectedFile)
            (WorkingDirectoryUpdateTransition.Present selectedFile)
            (WorkingDirectoryUpdateTransition.Requirements [ materialization selectedFile WorkingDirectoryUpdateTransition.AlreadySatisfied ])

        add
            "Fresh create directory"
            WorkingDirectoryUpdateTransition.Fresh
            (WorkingDirectoryUpdateTransition.Create selectedDirectory)
            WorkingDirectoryUpdateTransition.Absent
            (WorkingDirectoryUpdateTransition.Requirements [ materialization selectedDirectory WorkingDirectoryUpdateTransition.NeedsApply ])

        add
            "Exact create directory absent"
            WorkingDirectoryUpdateTransition.ExactAdoption
            (WorkingDirectoryUpdateTransition.Create selectedDirectory)
            WorkingDirectoryUpdateTransition.Absent
            (WorkingDirectoryUpdateTransition.Requirements [ materialization selectedDirectory WorkingDirectoryUpdateTransition.NeedsApply ])

        add
            "Exact create directory final"
            WorkingDirectoryUpdateTransition.ExactAdoption
            (WorkingDirectoryUpdateTransition.Create selectedDirectory)
            (WorkingDirectoryUpdateTransition.Present selectedDirectory)
            (WorkingDirectoryUpdateTransition.Requirements [ materialization selectedDirectory WorkingDirectoryUpdateTransition.AlreadySatisfied ])

        add
            "Fresh remove file"
            WorkingDirectoryUpdateTransition.Fresh
            (WorkingDirectoryUpdateTransition.Remove acceptedFile)
            (WorkingDirectoryUpdateTransition.Present acceptedFile)
            (WorkingDirectoryUpdateTransition.Requirements [ removal acceptedFile WorkingDirectoryUpdateTransition.NeedsApply ])

        add
            "Exact remove file accepted"
            WorkingDirectoryUpdateTransition.ExactAdoption
            (WorkingDirectoryUpdateTransition.Remove acceptedFile)
            (WorkingDirectoryUpdateTransition.Present acceptedFile)
            (WorkingDirectoryUpdateTransition.Requirements [ removal acceptedFile WorkingDirectoryUpdateTransition.NeedsApply ])

        add
            "Exact remove file absent"
            WorkingDirectoryUpdateTransition.ExactAdoption
            (WorkingDirectoryUpdateTransition.Remove acceptedFile)
            WorkingDirectoryUpdateTransition.Absent
            (WorkingDirectoryUpdateTransition.Requirements [ removal acceptedFile WorkingDirectoryUpdateTransition.AlreadySatisfied ])

        add
            "Fresh remove directory"
            WorkingDirectoryUpdateTransition.Fresh
            (WorkingDirectoryUpdateTransition.Remove acceptedDirectory)
            (WorkingDirectoryUpdateTransition.Present acceptedDirectory)
            (WorkingDirectoryUpdateTransition.Requirements [ removal acceptedDirectory WorkingDirectoryUpdateTransition.NeedsApply ])

        add
            "Exact remove directory accepted"
            WorkingDirectoryUpdateTransition.ExactAdoption
            (WorkingDirectoryUpdateTransition.Remove acceptedDirectory)
            (WorkingDirectoryUpdateTransition.Present acceptedDirectory)
            (WorkingDirectoryUpdateTransition.Requirements [ removal acceptedDirectory WorkingDirectoryUpdateTransition.NeedsApply ])

        add
            "Exact remove directory absent"
            WorkingDirectoryUpdateTransition.ExactAdoption
            (WorkingDirectoryUpdateTransition.Remove acceptedDirectory)
            WorkingDirectoryUpdateTransition.Absent
            (WorkingDirectoryUpdateTransition.Requirements [ removal acceptedDirectory WorkingDirectoryUpdateTransition.AlreadySatisfied ])

        let replacement = WorkingDirectoryUpdateTransition.ReplaceFile(file "path" "sha-accepted" "blake-accepted", file "path" "sha-final" "blake-final")

        add
            "Fresh replace file"
            WorkingDirectoryUpdateTransition.Fresh
            replacement
            (WorkingDirectoryUpdateTransition.Present acceptedFile)
            (WorkingDirectoryUpdateTransition.Requirements [ removal acceptedFile WorkingDirectoryUpdateTransition.NeedsApply
                                                             materialization selectedFile WorkingDirectoryUpdateTransition.NeedsApply ])

        add
            "Exact replace accepted"
            WorkingDirectoryUpdateTransition.ExactAdoption
            replacement
            (WorkingDirectoryUpdateTransition.Present acceptedFile)
            (WorkingDirectoryUpdateTransition.Requirements [ removal acceptedFile WorkingDirectoryUpdateTransition.NeedsApply
                                                             materialization selectedFile WorkingDirectoryUpdateTransition.NeedsApply ])

        add
            "Exact replace absent"
            WorkingDirectoryUpdateTransition.ExactAdoption
            replacement
            WorkingDirectoryUpdateTransition.Absent
            (WorkingDirectoryUpdateTransition.Requirements [ removal acceptedFile WorkingDirectoryUpdateTransition.AlreadySatisfied
                                                             materialization selectedFile WorkingDirectoryUpdateTransition.NeedsApply ])

        add
            "Exact replace final"
            WorkingDirectoryUpdateTransition.ExactAdoption
            replacement
            (WorkingDirectoryUpdateTransition.Present selectedFile)
            (WorkingDirectoryUpdateTransition.Requirements [ removal acceptedFile WorkingDirectoryUpdateTransition.AlreadySatisfied
                                                             materialization selectedFile WorkingDirectoryUpdateTransition.AlreadySatisfied ])

        let fileToDirectory = WorkingDirectoryUpdateTransition.FileToDirectory(file "path" "sha-accepted" "blake-accepted", RelativePath "path")

        add
            "Fresh file to directory"
            WorkingDirectoryUpdateTransition.Fresh
            fileToDirectory
            (WorkingDirectoryUpdateTransition.Present acceptedFile)
            (WorkingDirectoryUpdateTransition.Requirements [ removal acceptedFile WorkingDirectoryUpdateTransition.NeedsApply
                                                             materialization selectedDirectory WorkingDirectoryUpdateTransition.NeedsApply ])

        add
            "Exact file to directory accepted"
            WorkingDirectoryUpdateTransition.ExactAdoption
            fileToDirectory
            (WorkingDirectoryUpdateTransition.Present acceptedFile)
            (WorkingDirectoryUpdateTransition.Requirements [ removal acceptedFile WorkingDirectoryUpdateTransition.NeedsApply
                                                             materialization selectedDirectory WorkingDirectoryUpdateTransition.NeedsApply ])

        add
            "Exact file to directory absent"
            WorkingDirectoryUpdateTransition.ExactAdoption
            fileToDirectory
            WorkingDirectoryUpdateTransition.Absent
            (WorkingDirectoryUpdateTransition.Requirements [ removal acceptedFile WorkingDirectoryUpdateTransition.AlreadySatisfied
                                                             materialization selectedDirectory WorkingDirectoryUpdateTransition.NeedsApply ])

        add
            "Exact file to directory final"
            WorkingDirectoryUpdateTransition.ExactAdoption
            fileToDirectory
            (WorkingDirectoryUpdateTransition.Present selectedDirectory)
            (WorkingDirectoryUpdateTransition.Requirements [ removal acceptedFile WorkingDirectoryUpdateTransition.AlreadySatisfied
                                                             materialization selectedDirectory WorkingDirectoryUpdateTransition.AlreadySatisfied ])

        let directoryToFile = WorkingDirectoryUpdateTransition.DirectoryToFile(RelativePath "path", file "path" "sha-final" "blake-final")

        add
            "Fresh directory to file"
            WorkingDirectoryUpdateTransition.Fresh
            directoryToFile
            (WorkingDirectoryUpdateTransition.Present acceptedDirectory)
            (WorkingDirectoryUpdateTransition.Requirements [ removal acceptedDirectory WorkingDirectoryUpdateTransition.NeedsApply
                                                             materialization selectedFile WorkingDirectoryUpdateTransition.NeedsApply ])

        add
            "Exact directory to file accepted"
            WorkingDirectoryUpdateTransition.ExactAdoption
            directoryToFile
            (WorkingDirectoryUpdateTransition.Present acceptedDirectory)
            (WorkingDirectoryUpdateTransition.Requirements [ removal acceptedDirectory WorkingDirectoryUpdateTransition.NeedsApply
                                                             materialization selectedFile WorkingDirectoryUpdateTransition.NeedsApply ])

        add
            "Exact directory to file absent"
            WorkingDirectoryUpdateTransition.ExactAdoption
            directoryToFile
            WorkingDirectoryUpdateTransition.Absent
            (WorkingDirectoryUpdateTransition.Requirements [ removal acceptedDirectory WorkingDirectoryUpdateTransition.AlreadySatisfied
                                                             materialization selectedFile WorkingDirectoryUpdateTransition.NeedsApply ])

        add
            "Exact directory to file final"
            WorkingDirectoryUpdateTransition.ExactAdoption
            directoryToFile
            (WorkingDirectoryUpdateTransition.Present selectedFile)
            (WorkingDirectoryUpdateTransition.Requirements [ removal acceptedDirectory WorkingDirectoryUpdateTransition.AlreadySatisfied
                                                             materialization selectedFile WorkingDirectoryUpdateTransition.AlreadySatisfied ])

        rows |> Seq.toList, foreignFile

    /// Proves every normative Fresh and ExactAdoption table row returns its independently listed requirement fragment.
    [<Test>]
    let ``transition classifier satisfies every normative row`` () =
        let rows, _ = positiveRows ()

        for row in rows do
            WorkingDirectoryUpdateTransition.classify row.Admission row.Transition row.Observation
            |> should equal row.Expected

    /// Proves every table row carries a distinct name for a direct proof-map audit.
    [<Test>]
    let ``transition normative row map has 28 rows`` () =
        let rows, _ = positiveRows ()
        rows.Length |> should equal 28

        rows
        |> List.map (fun row -> row.Name)
        |> Set.ofList
        |> Set.count
        |> should equal 28

    /// Proves every ordinary observation outside the independently listed finite table rejects by default.
    [<Test>]
    let ``transition classifier rejects the full ordinary observation complement`` () =
        let rows, foreignFile = positiveRows ()

        let ordinaryObservations =
            [
                WorkingDirectoryUpdateTransition.Absent
                WorkingDirectoryUpdateTransition.Present(fileIdentity "path" "sha-accepted" "blake-accepted")
                WorkingDirectoryUpdateTransition.Present(fileIdentity "path" "sha-final" "blake-final")
                WorkingDirectoryUpdateTransition.Present foreignFile
                WorkingDirectoryUpdateTransition.Present(directoryIdentity "path")
                WorkingDirectoryUpdateTransition.Present(fileIdentity "other-path" "sha-accepted" "blake-accepted")
            ]

        let inputs =
            rows
            |> List.map (fun row -> row.Admission, row.Transition)
            |> List.distinct

        for admission, transition in inputs do
            let allowed =
                rows
                |> List.choose (fun row ->
                    if row.Admission = admission
                       && row.Transition = transition then
                        Some row.Observation
                    else
                        None)

            for observation in ordinaryObservations do
                if not (allowed |> List.contains observation) then
                    WorkingDirectoryUpdateTransition.classify admission transition observation
                    |> should equal WorkingDirectoryUpdateTransition.IdentityDrift

    /// Proves each dual hash is independently necessary for accepted and final file identity.
    [<Test>]
    let ``transition classifier rejects independent accepted and final hash drift`` () =
        let accepted = file "path" "sha-accepted" "blake-accepted"
        let selected = file "path" "sha-final" "blake-final"
        let replacement = WorkingDirectoryUpdateTransition.ReplaceFile(accepted, selected)
        let wrongAcceptedSha = fileIdentity "path" "sha-wrong" "blake-accepted"
        let wrongAcceptedBlake = fileIdentity "path" "sha-accepted" "blake-wrong"
        let wrongFinalSha = fileIdentity "path" "sha-wrong" "blake-final"
        let wrongFinalBlake = fileIdentity "path" "sha-final" "blake-wrong"
        let bothWrong = fileIdentity "path" "sha-wrong" "blake-wrong"

        for observation in
            [
                wrongAcceptedSha
                wrongAcceptedBlake
                wrongFinalSha
                wrongFinalBlake
                bothWrong
            ] do
            WorkingDirectoryUpdateTransition.classify
                WorkingDirectoryUpdateTransition.ExactAdoption
                replacement
                (WorkingDirectoryUpdateTransition.Present observation)
            |> should equal WorkingDirectoryUpdateTransition.IdentityDrift

    /// Proves malformed inputs describing multiple exact paths reject instead of inventing a cross-path transition.
    [<Test>]
    let ``transition classifier rejects a transition whose identities name different paths`` () =
        let accepted = file "accepted-path" "sha-accepted" "blake-accepted"
        let selected = file "selected-path" "sha-final" "blake-final"

        WorkingDirectoryUpdateTransition.classify
            WorkingDirectoryUpdateTransition.ExactAdoption
            (WorkingDirectoryUpdateTransition.ReplaceFile(accepted, selected))
            (WorkingDirectoryUpdateTransition.Present(WorkingDirectoryUpdateTransition.File accepted))
        |> should equal WorkingDirectoryUpdateTransition.IdentityDrift

    /// Proves retained file and directory absence is unrelated drift, unlike absence after one named removal.
    [<Test>]
    let ``transition classifier rejects retained absence while reconciling exact retained identities`` () =
        let retainedFile = fileIdentity "retained-file" "sha-accepted" "blake-accepted"
        let retainedDirectory = directoryIdentity "retained-directory"

        for identity in [ retainedFile; retainedDirectory ] do
            WorkingDirectoryUpdateTransition.classify
                WorkingDirectoryUpdateTransition.ExactAdoption
                (WorkingDirectoryUpdateTransition.Retain identity)
                (WorkingDirectoryUpdateTransition.Present identity)
            |> function
                | WorkingDirectoryUpdateTransition.Requirements [ requirement ] ->
                    requirement.Convergence
                    |> should equal WorkingDirectoryUpdateTransition.AlreadySatisfied
                | result -> Assert.Fail($"Expected exact retained identity to reconcile, but received {result}.")

            WorkingDirectoryUpdateTransition.classify
                WorkingDirectoryUpdateTransition.ExactAdoption
                (WorkingDirectoryUpdateTransition.Retain identity)
                WorkingDirectoryUpdateTransition.Absent
            |> should equal WorkingDirectoryUpdateTransition.IdentityDrift

    /// Proves fresh ordinary creation produces pending materialization rather than treating absence as drift.
    [<Test>]
    let ``fresh file creation needs materialization`` () =
        let selected = file "created.txt" "sha-final" "blake-final"

        WorkingDirectoryUpdateTransition.classify
            WorkingDirectoryUpdateTransition.Fresh
            (WorkingDirectoryUpdateTransition.Create(WorkingDirectoryUpdateTransition.File selected))
            WorkingDirectoryUpdateTransition.Absent
        |> should
            equal
            (WorkingDirectoryUpdateTransition.Requirements [ {
                                                                 Role = WorkingDirectoryUpdateTransition.Materialization
                                                                 Identity = WorkingDirectoryUpdateTransition.File selected
                                                                 Convergence = WorkingDirectoryUpdateTransition.NeedsApply
                                                             } ])
