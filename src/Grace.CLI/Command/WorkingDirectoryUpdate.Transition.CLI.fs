namespace Grace.CLI.Command

open Grace.Types.Common

/// Classifies one exact local path's immutable admission evidence before whole-topology composition.
module internal WorkingDirectoryUpdateTransition =

    /// Holds the two hashes and exact normalized path required to recognize one ordinary file.
    type FileIdentity = { Path: RelativePath; Sha256Hash: Sha256Hash; Blake3Hash: Blake3Hash }

    /// Represents one ordinary file or directory identity at the exact normalized path.
    type Identity =
        | File of FileIdentity
        | Directory of RelativePath

    /// Distinguishes a newly admitted operation from an exact same-operation retry.
    type AdmissionMode =
        | Fresh
        | ExactAdoption

    /// States the only per-path transitions the later topology composition may request.
    type TransitionIntent =
        | Retain of Identity
        | Create of Identity
        | Remove of Identity
        | ReplaceFile of FileIdentity * FileIdentity
        | FileToDirectory of FileIdentity * RelativePath
        | DirectoryToFile of RelativePath * FileIdentity

    /// Represents one ordinary observation after composition excludes every non-ordinary local shape.
    type Observation =
        | Absent
        | Present of Identity

    /// Names the immutable work role that a later stage can compose and order without reclassifying evidence.
    type RequirementRole =
        | Retained
        | Removal
        | Materialization

    /// Records whether one requirement remains for a later application stage or is already converged.
    type Convergence =
        | NeedsApply
        | AlreadySatisfied

    /// Carries one path-local requirement with its exact identity and convergence state.
    type Requirement = { Role: RequirementRole; Identity: Identity; Convergence: Convergence }

    /// Returns either the complete path-local requirement fragment or the closed-domain drift disposition.
    type Result =
        | Requirements of Requirement list
        | IdentityDrift

    /// Creates one retained assertion for the exact accepted identity.
    let private retained identity convergence = { Role = Retained; Identity = identity; Convergence = convergence }

    /// Creates one removal requirement for the exact accepted identity.
    let private removal identity convergence = { Role = Removal; Identity = identity; Convergence = convergence }

    /// Creates one materialization requirement for the exact selected identity.
    let private materialization identity convergence = { Role = Materialization; Identity = identity; Convergence = convergence }

    /// Returns the exact normalized identity path without reducing it to a Windows comparison key.
    let private path =
        function
        | File file -> file.Path
        | Directory directory -> directory

    /// Rejects malformed transition inputs that do not describe one exact normalized path.
    let private isSinglePathTransition =
        function
        | Retain identity
        | Create identity
        | Remove identity -> true, path identity
        | ReplaceFile (accepted, selected) ->
            let acceptedPath = accepted.Path

            acceptedPath = selected.Path
            && accepted <> selected,
            acceptedPath
        | FileToDirectory (accepted, selected) ->
            let acceptedPath = accepted.Path
            acceptedPath = selected, acceptedPath
        | DirectoryToFile (accepted, selected) -> accepted = selected.Path, accepted

    /// Classifies one closed per-path admission, transition, and ordinary-observation tuple without touching a filesystem.
    let classify admission transition observation : Result =
        match isSinglePathTransition transition with
        | false, _ -> IdentityDrift
        | true, _ ->
            match admission, transition, observation with
            | Fresh, Retain accepted, Present observed when observed = accepted -> Requirements [ retained accepted NeedsApply ]
            | ExactAdoption, Retain accepted, Present observed when observed = accepted -> Requirements [ retained accepted AlreadySatisfied ]
            | (Fresh
              | ExactAdoption),
              Create selected,
              Absent -> Requirements [ materialization selected NeedsApply ]
            | ExactAdoption, Create selected, Present observed when observed = selected -> Requirements [ materialization selected AlreadySatisfied ]
            | (Fresh
              | ExactAdoption),
              Remove accepted,
              Present observed when observed = accepted -> Requirements [ removal accepted NeedsApply ]
            | ExactAdoption, Remove accepted, Absent -> Requirements [ removal accepted AlreadySatisfied ]
            | (Fresh
              | ExactAdoption),
              ReplaceFile (accepted, selected),
              Present (File observed) when observed = accepted ->
                Requirements [ removal (File accepted) NeedsApply
                               materialization (File selected) NeedsApply ]
            | ExactAdoption, ReplaceFile (accepted, selected), Absent ->
                Requirements [ removal (File accepted) AlreadySatisfied
                               materialization (File selected) NeedsApply ]
            | ExactAdoption, ReplaceFile (accepted, selected), Present (File observed) when observed = selected ->
                Requirements [ removal (File accepted) AlreadySatisfied
                               materialization (File selected) AlreadySatisfied ]
            | (Fresh
              | ExactAdoption),
              FileToDirectory (accepted, selected),
              Present (File observed) when observed = accepted ->
                Requirements [ removal (File accepted) NeedsApply
                               materialization (Directory selected) NeedsApply ]
            | ExactAdoption, FileToDirectory (accepted, selected), Absent ->
                Requirements [ removal (File accepted) AlreadySatisfied
                               materialization (Directory selected) NeedsApply ]
            | ExactAdoption, FileToDirectory (accepted, selected), Present (Directory observed) when observed = selected ->
                Requirements [ removal (File accepted) AlreadySatisfied
                               materialization (Directory selected) AlreadySatisfied ]
            | (Fresh
              | ExactAdoption),
              DirectoryToFile (accepted, selected),
              Present (Directory observed) when observed = accepted ->
                Requirements [ removal (Directory accepted) NeedsApply
                               materialization (File selected) NeedsApply ]
            | ExactAdoption, DirectoryToFile (accepted, selected), Absent ->
                Requirements [ removal (Directory accepted) AlreadySatisfied
                               materialization (File selected) NeedsApply ]
            | ExactAdoption, DirectoryToFile (accepted, selected), Present (File observed) when observed = selected ->
                Requirements [ removal (Directory accepted) AlreadySatisfied
                               materialization (File selected) AlreadySatisfied ]
            | _ -> IdentityDrift
