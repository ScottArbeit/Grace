namespace Grace.CLI.Command

open Grace.Shared.Constants
open Grace.Types.Common
open System
open System.Collections.Generic
open System.IO

/// Reconciles immutable accepted, selected, and observed topology before Working Directory Update can mutate local paths.
module internal WorkingDirectoryUpdate =
    /// Represents the complete finite pre-mutation topology contract consumed by the later application stage.
    module Topology =
        /// Distinguishes new admission from exact recovery of the same incomplete operation.
        type AdmissionMode =
            | Fresh
            | ExactAdoption

        /// Explains why immutable local evidence cannot satisfy the accepted reconciliation contract.
        type RejectionClassification =
            | Ignored
            | Untracked
            | AmbiguousTarget
            | EscapesLocalRoot
            | ReparsePoint
            | IdentityDrift

        /// Carries the normalized path and deterministic reason that stopped reconciliation.
        type Rejection = private { Path: RelativePath; Classification: RejectionClassification }

        /// Describes one immutable entry captured by the future local reread stage.
        type RelevantEntry =
            | File of RelativePath * Sha256Hash * Blake3Hash
            | Directory of RelativePath
            | Ignored of RelativePath
            | Untracked of RelativePath
            | ReparsePoint of RelativePath

        /// Stores a complete immutable relevant topology snapshot without a filesystem handle.
        type RelevantTopology = private RelevantTopology of RelevantEntry list

        /// Names the accepted or final identity asserted by a requirement.
        type ExpectedEntry =
            | Absent
            | Directory
            | File of Sha256Hash * Blake3Hash

        /// Names the later action or retained assertion encoded by a requirement.
        type Role =
            | Retained
            | RemoveFile
            | RemoveDirectory
            | CreateDirectory
            | Copy

        /// States whether exact immutable evidence still requires application or has already converged.
        type Convergence =
            | NeedsApply
            | AlreadySatisfied

        /// Carries immutable before/after identity and convergence facts for one ordered requirement.
        type Requirement =
            private
                {
                    Path: RelativePath
                    ExpectedCurrent: ExpectedEntry
                    ExpectedFinal: ExpectedEntry
                    Role: Role
                    State: Convergence
                    AdmissionMode: AdmissionMode
                }

        /// Represents the ordered immutable requirement vector and its destructive-scope assertions.
        type Requirements = private Requirements of Requirement list

        /// Represents either the complete immutable reconciliation vector or one deterministic rejection.
        type Result =
            | Reconciled of Requirements
            | Rejected of Rejection

        /// Provides stable read access to a rejected topology result.
        module Rejection =
            /// Gets the normalized path that prevents reconciliation.
            let path (rejection: Rejection) = rejection.Path

            /// Gets the exact classification that prevents reconciliation.
            let classification (rejection: Rejection) = rejection.Classification

        /// Provides construction and immutable access for captured relevant topology.
        module RelevantTopology =
            /// Stores one caller-captured entry sequence without doing a filesystem read.
            let create entries =
                if isNull (box entries) then
                    Error "Relevant topology entries must not be null."
                else
                    entries |> Seq.toList |> RelevantTopology |> Ok

            /// Returns the caller-captured entries in their immutable order.
            let entries (RelevantTopology entries) = entries

        /// Provides immutable access to individual reconciliation requirements.
        module Requirement =
            /// Gets the normalized requirement path.
            let path requirement = requirement.Path

            /// Gets the identity that must exist before this requirement can apply.
            let expectedCurrent requirement = requirement.ExpectedCurrent

            /// Gets the identity that must exist after this requirement has converged.
            let expectedFinal requirement = requirement.ExpectedFinal

            /// Gets the action or assertion role.
            let role requirement = requirement.Role

            /// Gets whether this requirement needs application or is already complete.
            let state requirement = requirement.State

            /// Gets the admission mode that classified this requirement.
            let admissionMode requirement = requirement.AdmissionMode

        /// Produces a Windows-stable normalized comparison key.
        let private key (path: RelativePath) =
            string path
            |> fun value -> value.ToUpperInvariant()

        /// Determines whether a candidate is strictly below one normalized repository-relative path.
        let private isBelow (root: RelativePath) (candidate: RelativePath) =
            let rootValue = string root
            let candidateValue = string candidate
            candidateValue.StartsWith(rootValue + "/", StringComparison.OrdinalIgnoreCase)

        /// Returns the immutable path carried by any observed entry.
        let private entryPath =
            function
            | RelevantEntry.File (path, _, _)
            | RelevantEntry.Directory path
            | RelevantEntry.Ignored path
            | RelevantEntry.Untracked path
            | RelevantEntry.ReparsePoint path -> path

        /// Provides immutable requirement-vector operations for application-stage prefix checks.
        module Requirements =
            /// Returns requirements in deterministic application order.
            let items (Requirements requirements) = requirements

            /// Advances exactly one next pending mutating requirement after its owner proves that action completed.
            let advance completed (Requirements requirements) =
                let nextPending =
                    requirements
                    |> List.tryFind (fun requirement ->
                        requirement.State = NeedsApply
                        && requirement.Role <> Retained)

                match nextPending with
                | None -> Error "No pending topology action can advance."
                | Some nextPending when nextPending <> completed -> Error "Only the next pending topology action can advance."
                | Some _ ->
                    requirements
                    |> List.map (fun requirement ->
                        if requirement = completed then
                            { requirement with ExpectedCurrent = requirement.ExpectedFinal; State = AlreadySatisfied }
                        else
                            requirement)
                    |> Requirements
                    |> Ok

            /// Compares every declared expectation and every remaining destructive scope against a complete immutable snapshot.
            let matchesExpected (Requirements requirements) (RelevantTopology entries) =
                let actual = Dictionary<string, RelevantEntry>(StringComparer.OrdinalIgnoreCase)
                let mutable ambiguous = false

                for entry in entries do
                    let path = entryPath entry

                    if actual.ContainsKey(string path) then
                        ambiguous <- true
                    else
                        actual[string path] <- entry

                let exactSpelling =
                    requirements
                    |> List.forall (fun requirement ->
                        match actual.TryGetValue(string requirement.Path) with
                        | true, entry -> String.Equals(string requirement.Path, string (entryPath entry), StringComparison.Ordinal)
                        | false, _ -> true)

                let expectedAt path =
                    requirements
                    |> List.filter (fun requirement -> String.Equals(string requirement.Path, string path, StringComparison.OrdinalIgnoreCase))
                    |> List.tryFind (fun requirement -> requirement.State = NeedsApply)
                    |> Option.orElseWith (fun () ->
                        requirements
                        |> List.filter (fun requirement -> String.Equals(string requirement.Path, string path, StringComparison.OrdinalIgnoreCase))
                        |> List.tryLast)
                    |> Option.map (fun requirement -> requirement.ExpectedCurrent)

                let exactAt path expected =
                    match expected, actual.TryGetValue(string path) with
                    | ExpectedEntry.Absent, (false, _) -> true
                    | ExpectedEntry.Directory, (true, RelevantEntry.Directory _) -> true
                    | ExpectedEntry.File (sha256Hash, blake3Hash), (true, RelevantEntry.File (_, actualSha256, actualBlake3)) ->
                        sha256Hash = actualSha256
                        && blake3Hash = actualBlake3
                    | _ -> false

                let destructiveDescendant =
                    requirements
                    |> List.exists (fun requirement ->
                        requirement.State = NeedsApply
                        && requirement.Role = RemoveDirectory
                        && (entries
                            |> List.exists (fun entry ->
                                let path = entryPath entry

                                path <> requirement.Path
                                && isBelow requirement.Path path
                                && expectedAt path |> Option.isNone)))

                not ambiguous
                && exactSpelling
                && not destructiveDescendant
                && (requirements
                    |> List.map (fun requirement -> requirement.Path)
                    |> List.distinctBy key
                    |> List.forall (fun path -> expectedAt path |> Option.exists (exactAt path)))

        /// Counts path components for deterministic deepest-first removal and shallowest-first creation ordering.
        let private depth (path: RelativePath) =
            if string path = RootDirectoryPath then
                0
            else
                (string path).Split(
                    '/',
                    StringSplitOptions.RemoveEmptyEntries
                )
                    .Length

        /// Validates that a snapshot path remains a non-empty normalized repository-relative path.
        let private validRelativePath (path: RelativePath) =
            let value = string path

            not (String.IsNullOrWhiteSpace value)
            && not (Path.IsPathRooted value)
            && not (value.StartsWith("/", StringComparison.Ordinal))
            && not (value.StartsWith("\\", StringComparison.Ordinal))
            && (value.Split('/', StringSplitOptions.None)
                |> Array.forall (fun part -> part <> "" && part <> "." && part <> ".."))

        /// Converts ordinary observed evidence to its comparable expected identity.
        let private expectedOfEntry =
            function
            | RelevantEntry.File (_, sha256Hash, blake3Hash) -> Some(ExpectedEntry.File(sha256Hash, blake3Hash))
            | RelevantEntry.Directory _ -> Some ExpectedEntry.Directory
            | RelevantEntry.Ignored _
            | RelevantEntry.Untracked _
            | RelevantEntry.ReparsePoint _ -> None

        /// Tests exact expected identity equality, including both file hashes.
        let private equalsExpected expected actual =
            match expected, actual with
            | ExpectedEntry.File (expectedSha256, expectedBlake3), Some (ExpectedEntry.File (actualSha256, actualBlake3)) ->
                expectedSha256 = actualSha256
                && expectedBlake3 = actualBlake3
            | ExpectedEntry.Directory, Some ExpectedEntry.Directory
            | ExpectedEntry.Absent, Some ExpectedEntry.Absent -> true
            | _ -> false

        /// Reads observed evidence at one path, treating complete-snapshot omission as immutable absence.
        let private actualAt (actual: Dictionary<string, RelevantEntry>) path =
            match actual.TryGetValue(key path) with
            | true, entry -> expectedOfEntry entry
            | false, _ -> Some ExpectedEntry.Absent

        /// Builds accepted file and directory identity maps while rejecting case or kind ambiguity.
        let private trackedTopology (status: GraceStatus) =
            let files = Dictionary<string, RelativePath * Sha256Hash * Blake3Hash>(StringComparer.OrdinalIgnoreCase)
            let directories = Dictionary<string, RelativePath>(StringComparer.OrdinalIgnoreCase)
            let mutable rejection = None

            status.Index.Values
            |> Seq.sortBy (fun directory -> key directory.RelativePath)
            |> Seq.iter (fun directory ->
                let directoryKey = key directory.RelativePath

                if files.ContainsKey directoryKey
                   || directories.ContainsKey directoryKey then
                    rejection <- Some { Path = directory.RelativePath; Classification = AmbiguousTarget }
                else
                    directories[directoryKey] <- directory.RelativePath

                directory.Files
                |> Seq.sortBy (fun file -> key file.RelativePath)
                |> Seq.iter (fun file ->
                    let fileKey = key file.RelativePath

                    if files.ContainsKey fileKey
                       || directories.ContainsKey fileKey then
                        rejection <- Some { Path = file.RelativePath; Classification = AmbiguousTarget }
                    else
                        files[fileKey] <- file.RelativePath, file.Sha256Hash, file.Blake3Hash))

            rejection, files, directories

        /// Builds selected final identities, including all implicit selected parent directories.
        let private targetTopology manifest =
            let files = Dictionary<string, RelativePath * Sha256Hash * Blake3Hash>(StringComparer.OrdinalIgnoreCase)
            let directories = Dictionary<string, RelativePath>(StringComparer.OrdinalIgnoreCase)
            let mutable rejection = None

            let addDirectory path =
                let directoryKey = key path

                if files.ContainsKey directoryKey then
                    rejection <- Some { Path = path; Classification = AmbiguousTarget }
                elif not (directories.ContainsKey directoryKey) then
                    directories[directoryKey] <- path

            let addParents path =
                let segments =
                    (string path)
                        .Split('/', StringSplitOptions.RemoveEmptyEntries)

                for index in 1 .. segments.Length - 1 do
                    addDirectory (RelativePath(String.Join('/', segments[0 .. index - 1])))

            WorkingDirectoryUpdateContracts.PreparedManifest.entries manifest
            |> Seq.iter (function
                | WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory path ->
                    addDirectory path
                    addParents path
                | WorkingDirectoryUpdateContracts.PreparedManifestEntry.File (path, sha256Hash, blake3Hash) ->
                    let fileKey = key path

                    if files.ContainsKey fileKey
                       || directories.ContainsKey fileKey then
                        rejection <- Some { Path = path; Classification = AmbiguousTarget }
                    else
                        files[fileKey] <- path, sha256Hash, blake3Hash

                    addParents path)

            rejection, files, directories

        /// Validates observed topology paths and produces a case-insensitive immutable lookup.
        let private actualTopology (RelevantTopology entries) =
            let actual = Dictionary<string, RelevantEntry>(StringComparer.OrdinalIgnoreCase)
            let mutable rejection = None

            entries
            |> List.iter (fun entry ->
                let path = entryPath entry

                if not (validRelativePath path) then
                    rejection <- Some { Path = path; Classification = EscapesLocalRoot }
                elif actual.ContainsKey(key path) then
                    rejection <- Some { Path = path; Classification = AmbiguousTarget }
                else
                    actual[key path] <- entry)

            rejection, actual, entries

        /// Rejects a Windows-stable lookup match whose normalized spelling differs from the accepted or selected path.
        let private firstCaseAlias
            (trackedFiles: Dictionary<string, RelativePath * Sha256Hash * Blake3Hash>)
            (trackedDirectories: Dictionary<string, RelativePath>)
            (targetFiles: Dictionary<string, RelativePath * Sha256Hash * Blake3Hash>)
            (targetDirectories: Dictionary<string, RelativePath>)
            (entries: RelevantEntry list)
            =
            let comparePaths left right =
                let keyOrder = StringComparer.Ordinal.Compare(key left, key right)

                if keyOrder <> 0 then
                    keyOrder
                else
                    StringComparer.Ordinal.Compare(string left, string right)

            let expectedPaths =
                seq {
                    yield!
                        trackedFiles.Values
                        |> Seq.map (fun (path, _, _) -> path)

                    yield! trackedDirectories.Values

                    yield!
                        targetFiles.Values
                        |> Seq.map (fun (path, _, _) -> path)

                    yield! targetDirectories.Values
                }
                |> Seq.sortWith comparePaths
                |> Seq.toList

            let conflictingExpected =
                expectedPaths
                |> List.pairwise
                |> List.tryPick (fun (left, right) ->
                    if
                        key left = key right
                        && not (String.Equals(string left, string right, StringComparison.Ordinal))
                    then
                        Some { Path = right; Classification = AmbiguousTarget }
                    else
                        None)

            let expectedAt path =
                expectedPaths
                |> List.tryFind (fun expected -> key expected = key path)

            let conflictingObserved =
                entries
                |> List.sortBy (entryPath >> key)
                |> List.tryPick (fun entry ->
                    let path = entryPath entry

                    match expectedAt path with
                    | Some expected when not (String.Equals(string expected, string path, StringComparison.Ordinal)) ->
                        Some { Path = path; Classification = AmbiguousTarget }
                    | _ -> None)

            conflictingExpected
            |> Option.orElse conflictingObserved

        /// Rejects non-tracked content beneath a directory that remains destructive at the current prefix.
        let private firstUnsafeDescendant
            (trackedFiles: Dictionary<string, RelativePath * Sha256Hash * Blake3Hash>)
            (trackedDirectories: Dictionary<string, RelativePath>)
            (entries: RelevantEntry list)
            (root: RelativePath)
            =
            entries
            |> List.sortBy (entryPath >> key)
            |> List.tryPick (fun entry ->
                let path = entryPath entry

                if isBelow root path then
                    match entry with
                    | RelevantEntry.Ignored _ -> Some { Path = path; Classification = RejectionClassification.Ignored }
                    | RelevantEntry.Untracked _ -> Some { Path = path; Classification = RejectionClassification.Untracked }
                    | RelevantEntry.ReparsePoint _ -> Some { Path = path; Classification = RejectionClassification.ReparsePoint }
                    | _ when
                        not
                            (
                                trackedFiles.ContainsKey(key path)
                                || trackedDirectories.ContainsKey(key path)
                            )
                        ->
                        Some { Path = path; Classification = RejectionClassification.Untracked }
                    | _ -> None
                else
                    None)

        /// Creates an exact immutable requirement.
        let private requirement path expectedCurrent expectedFinal role state admissionMode =
            { Path = path; ExpectedCurrent = expectedCurrent; ExpectedFinal = expectedFinal; Role = role; State = state; AdmissionMode = admissionMode }

        /// Reconciles the finite admission, accepted, selected, and observed topology matrix without reading or mutating a path.
        let reconcile admissionMode acceptedStatus manifest topology =
            let trackedRejection, trackedFiles, trackedDirectories = trackedTopology acceptedStatus
            let targetRejection, targetFiles, targetDirectories = targetTopology manifest
            let actualRejection, actual, entries = actualTopology topology
            let caseAlias = firstCaseAlias trackedFiles trackedDirectories targetFiles targetDirectories entries

            match trackedRejection, targetRejection, actualRejection, caseAlias with
            | Some rejection, _, _, _
            | _, Some rejection, _, _
            | _, _, Some rejection, _
            | _, _, _, Some rejection -> Rejected rejection
            | None, None, None, None ->
                let mutable rejection = None
                let removals = ResizeArray<Requirement>()
                let creates = ResizeArray<Requirement>()
                let copies = ResizeArray<Requirement>()

                let finalAt path =
                    match targetFiles.TryGetValue(key path), targetDirectories.TryGetValue(key path) with
                    | (true, (_, sha256Hash, blake3Hash)), _ -> Some(ExpectedEntry.File(sha256Hash, blake3Hash))
                    | _, (true, _) -> Some ExpectedEntry.Directory
                    | _ -> None

                let addRemoval path accepted role =
                    let observed = actualAt actual path
                    let final = finalAt path

                    let state, current =
                        if equalsExpected accepted observed then
                            NeedsApply, accepted
                        elif admissionMode = ExactAdoption
                             && equalsExpected ExpectedEntry.Absent observed then
                            AlreadySatisfied, ExpectedEntry.Absent
                        elif admissionMode = ExactAdoption
                             && final
                                |> Option.exists (fun identity -> equalsExpected identity observed) then
                            AlreadySatisfied, observed |> Option.get
                        else
                            rejection <- Some { Path = path; Classification = IdentityDrift }
                            NeedsApply, accepted

                    if Option.isNone rejection then
                        removals.Add(requirement path current ExpectedEntry.Absent role state admissionMode)

                trackedFiles.Values
                |> Seq.sortBy (fun (path, _, _) -> key path)
                |> Seq.iter (fun (path, sha256Hash, blake3Hash) ->
                    if Option.isNone rejection then
                        let remove = not (targetFiles.ContainsKey(key path))

                        if remove then
                            addRemoval path (ExpectedEntry.File(sha256Hash, blake3Hash)) RemoveFile)

                trackedDirectories.Values
                |> Seq.sortBy key
                |> Seq.iter (fun path ->
                    if Option.isNone rejection
                       && string path <> RootDirectoryPath then
                        let remove =
                            not (targetDirectories.ContainsKey(key path))
                            || targetFiles.ContainsKey(key path)

                        if remove then
                            match firstUnsafeDescendant trackedFiles trackedDirectories entries path with
                            | Some unsafe -> rejection <- Some unsafe
                            | None -> addRemoval path ExpectedEntry.Directory RemoveDirectory)

                targetDirectories.Values
                |> Seq.sortBy key
                |> Seq.iter (fun path ->
                    if Option.isNone rejection
                       && string path <> RootDirectoryPath then
                        let observed = actualAt actual path
                        let acceptedDirectory = trackedDirectories.ContainsKey(key path)
                        let acceptedFile = trackedFiles.ContainsKey(key path)

                        match observed with
                        | Some ExpectedEntry.Absent when acceptedDirectory && admissionMode = Fresh ->
                            rejection <- Some { Path = path; Classification = IdentityDrift }
                        | Some ExpectedEntry.Absent ->
                            creates.Add(requirement path ExpectedEntry.Absent ExpectedEntry.Directory CreateDirectory NeedsApply admissionMode)
                        | Some ExpectedEntry.Directory when acceptedDirectory ->
                            let state = if admissionMode = Fresh then NeedsApply else AlreadySatisfied
                            creates.Add(requirement path ExpectedEntry.Directory ExpectedEntry.Directory Retained state admissionMode)
                        | Some ExpectedEntry.Directory when acceptedFile && admissionMode = ExactAdoption ->
                            creates.Add(requirement path ExpectedEntry.Directory ExpectedEntry.Directory CreateDirectory AlreadySatisfied admissionMode)
                        | Some ExpectedEntry.Directory when
                            not acceptedFile
                            && not acceptedDirectory
                            && admissionMode = ExactAdoption
                            ->
                            creates.Add(requirement path ExpectedEntry.Directory ExpectedEntry.Directory CreateDirectory AlreadySatisfied admissionMode)
                        | Some (ExpectedEntry.File _) when acceptedFile ->
                            creates.Add(requirement path ExpectedEntry.Absent ExpectedEntry.Directory CreateDirectory NeedsApply admissionMode)
                        | Some ExpectedEntry.Directory when
                            not acceptedFile
                            && not acceptedDirectory
                            && admissionMode = ExactAdoption
                            ->
                            rejection <- Some { Path = path; Classification = RejectionClassification.Untracked }
                        | _ -> rejection <- Some { Path = path; Classification = IdentityDrift })

                targetFiles.Values
                |> Seq.sortBy (fun (path, _, _) -> key path)
                |> Seq.iter (fun (path, sha256Hash, blake3Hash) ->
                    if Option.isNone rejection then
                        let final = ExpectedEntry.File(sha256Hash, blake3Hash)
                        let observed = actualAt actual path
                        let acceptedFile = trackedFiles.TryGetValue(key path)
                        let acceptedDirectory = trackedDirectories.ContainsKey(key path)

                        match observed, acceptedFile with
                        | Some ExpectedEntry.Absent, (true, _) when admissionMode = Fresh -> rejection <- Some { Path = path; Classification = IdentityDrift }
                        | Some ExpectedEntry.Absent, _ -> copies.Add(requirement path ExpectedEntry.Absent final Copy NeedsApply admissionMode)
                        | Some actual, (true, (_, oldSha256, oldBlake3)) ->
                            let accepted = ExpectedEntry.File(oldSha256, oldBlake3)

                            if equalsExpected accepted (Some final)
                               && equalsExpected accepted (Some actual) then
                                let state = if admissionMode = Fresh then NeedsApply else AlreadySatisfied

                                copies.Add(requirement path accepted final Retained state admissionMode)
                            elif equalsExpected accepted (Some actual) then
                                copies.Add(requirement path accepted final Copy NeedsApply admissionMode)
                            elif admissionMode = ExactAdoption
                                 && equalsExpected final (Some actual) then
                                copies.Add(requirement path final final Copy AlreadySatisfied admissionMode)
                            else
                                rejection <- Some { Path = path; Classification = IdentityDrift }
                        | Some actual, (false, _) when
                            acceptedDirectory
                            && admissionMode = ExactAdoption
                            && equalsExpected final (Some actual)
                            ->
                            copies.Add(requirement path final final Copy AlreadySatisfied admissionMode)
                        | Some actual, (false, _) when
                            not acceptedDirectory
                            && admissionMode = ExactAdoption
                            && equalsExpected final (Some actual)
                            ->
                            copies.Add(requirement path final final Copy AlreadySatisfied admissionMode)
                        | Some ExpectedEntry.Directory, (false, _) when acceptedDirectory ->
                            copies.Add(requirement path ExpectedEntry.Absent final Copy NeedsApply admissionMode)
                        | Some _, _ -> rejection <- Some { Path = path; Classification = IdentityDrift }
                        | None, _ -> rejection <- Some { Path = path; Classification = IdentityDrift })

                match rejection with
                | Some value -> Rejected value
                | None ->
                    let orderedRemovals =
                        removals
                        |> Seq.sortWith (fun left right ->
                            let depthOrder = compare (depth right.Path) (depth left.Path)

                            if depthOrder <> 0 then
                                depthOrder
                            else
                                StringComparer.OrdinalIgnoreCase.Compare(string left.Path, string right.Path))
                        |> Seq.toList

                    let orderedCreates =
                        creates
                        |> Seq.sortWith (fun left right ->
                            let depthOrder = compare (depth left.Path) (depth right.Path)

                            if depthOrder <> 0 then
                                depthOrder
                            else
                                StringComparer.OrdinalIgnoreCase.Compare(string left.Path, string right.Path))
                        |> Seq.toList

                    let orderedCopies =
                        copies
                        |> Seq.sortBy (fun requirement -> key requirement.Path)
                        |> Seq.toList

                    Requirements(orderedRemovals @ orderedCreates @ orderedCopies)
                    |> Reconciled
