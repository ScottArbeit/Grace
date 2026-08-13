namespace Grace.CLI.Command

open Grace.Shared.Constants
open Grace.Types.Common
open System
open System.Collections.Generic
open System.IO

/// Evaluates immutable Working Directory Update topology without opening or changing local paths.
module internal WorkingDirectoryUpdate =
    /// Holds the selection retry policy used to classify existing real bytes.
    module Topology =
        /// Distinguishes first admission from exact same-operation adoption.
        type AdmissionMode =
            | Fresh
            | ExactAdoption

        /// Classifies why an immutable topology tuple cannot safely be reconciled.
        type RejectionClassification =
            | Ignored
            | Untracked
            | AmbiguousTarget
            | EscapesLocalRoot
            | ReparsePoint
            | IdentityDrift

        /// Carries the path and stable reason for a rejected reconciliation.
        type Rejection = private { Path: RelativePath; Classification: RejectionClassification }

        /// Represents one observed real entry supplied by the later filesystem-capture stage.
        type RelevantEntry =
            | File of RelativePath * Sha256Hash * Blake3Hash
            | Directory of RelativePath
            | Ignored of RelativePath
            | Untracked of RelativePath
            | ReparsePoint of RelativePath

        /// Stores one immutable caller-captured topology snapshot without a filesystem handle.
        type RelevantTopology = private RelevantTopology of RelevantEntry list

        /// Names the precondition or resulting kind of one requirement path.
        type ExpectedEntry =
            | Absent
            | Directory
            | File of Sha256Hash * Blake3Hash

        /// Names the role a later local-application stage must perform or assert.
        type Role =
            | Retained
            | RemoveFile
            | RemoveDirectory
            | CreateDirectory
            | Copy

        /// Identifies whether a requirement still needs its role performed or is proven converged.
        type Convergence =
            | NeedsApply
            | AlreadySatisfied

        /// Carries exact before and after topology facts for one ordered local requirement.
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

        /// Represents one immutable requirement sequence consumable by the local application stage.
        type Requirements = private Requirements of Requirement list

        /// Represents a deterministic rejection or the entire immutable reconciliation sequence.
        type Result =
            | Reconciled of Requirements
            | Rejected of Rejection

        /// Reads the failed path from a rejected result.
        module Rejection =
            /// Gets the path that made the immutable reconciliation unsafe.
            let path rejection = rejection.Path

            /// Gets the classification that made the immutable reconciliation unsafe.
            let classification rejection = rejection.Classification

        /// Builds and reads immutable input snapshots.
        module RelevantTopology =
            /// Preserves a caller-captured entry sequence without performing validation reads.
            let create entries =
                if isNull (box entries) then
                    Error "Relevant topology entries must not be null."
                else
                    entries |> Seq.toList |> RelevantTopology |> Ok

            /// Returns every captured entry in its original immutable order.
            let entries (RelevantTopology entries) = entries

        /// Reads one immutable reconciliation requirement.
        module Requirement =
            /// Gets the requirement path.
            let path requirement = requirement.Path

            /// Gets the exact current-state precondition for this requirement.
            let expectedCurrent requirement = requirement.ExpectedCurrent

            /// Gets the exact state expected after this requirement has converged.
            let expectedFinal requirement = requirement.ExpectedFinal

            /// Gets the assertion or action role.
            let role requirement = requirement.Role

            /// Gets the current convergence classification.
            let state requirement = requirement.State

            /// Gets the admission policy that classified this requirement.
            let admissionMode requirement = requirement.AdmissionMode

        /// Provides ordered requirement access and pure prefix advancement.
        module Requirements =
            /// Returns requirements in their deterministic application order.
            let items (Requirements requirements) = requirements

            /// Advances exactly the next pending action after its owner has completed that action.
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

            /// Compares all required paths with one immutable topology snapshot while excluding unrelated entries.
            let matchesExpected (Requirements requirements) (RelevantTopology entries) =
                let actual = Dictionary<string, RelevantEntry>(StringComparer.OrdinalIgnoreCase)
                let mutable ambiguous = false

                for entry in entries do
                    let entryPath =
                        match entry with
                        | RelevantEntry.File (path, _, _)
                        | RelevantEntry.Directory path
                        | RelevantEntry.Ignored path
                        | RelevantEntry.Untracked path
                        | RelevantEntry.ReparsePoint path -> path

                    let key = string entryPath

                    if actual.ContainsKey(key) then ambiguous <- true else actual[key] <- entry

                let expectedByPath =
                    requirements
                    |> List.groupBy (fun requirement -> string requirement.Path)
                    |> List.map (fun (path, pathRequirements) ->
                        let selected =
                            pathRequirements
                            |> List.tryFind (fun requirement -> requirement.State = NeedsApply)
                            |> Option.defaultValue (List.last pathRequirements)

                        path, selected.ExpectedCurrent)

                not ambiguous
                && (expectedByPath
                    |> List.forall (fun (path, expected) ->
                        match expected, actual.TryGetValue(path) with
                        | ExpectedEntry.Absent, (false, _) -> true
                        | ExpectedEntry.Directory, (true, RelevantEntry.Directory _) -> true
                        | ExpectedEntry.File (sha256Hash, blake3Hash), (true, RelevantEntry.File (_, actualSha256, actualBlake3)) ->
                            actualSha256 = sha256Hash
                            && actualBlake3 = blake3Hash
                        | _ -> false))

        /// Produces the Windows comparison key for an already-normalized repository-relative path.
        let private key (path: RelativePath) =
            string path
            |> fun value -> value.ToUpperInvariant()

        /// Tests whether a path is the nominated root or an entry below it.
        let private isAtOrBelow (root: RelativePath) (path: RelativePath) =
            let rootValue = string root
            let pathValue = string path

            String.Equals(rootValue, pathValue, StringComparison.OrdinalIgnoreCase)
            || pathValue.StartsWith(rootValue + "/", StringComparison.OrdinalIgnoreCase)

        /// Counts segments so removal and creation order is deterministic.
        let private depth (path: RelativePath) =
            if string path = RootDirectoryPath then
                0
            else
                (string path).Split(
                    '/',
                    StringSplitOptions.RemoveEmptyEntries
                )
                    .Length

        /// Checks that one caller-supplied snapshot path stays repository relative without opening it.
        let private validRelativePath (path: RelativePath) =
            let value = string path

            not (String.IsNullOrWhiteSpace(value))
            && not (Path.IsPathRooted(value))
            && not (value.StartsWith("/", StringComparison.Ordinal))
            && not (value.StartsWith("\\", StringComparison.Ordinal))
            && value.Split('/', StringSplitOptions.None)
               |> Array.forall (fun segment -> segment <> "" && segment <> "." && segment <> "..")

        /// Returns the entry path independently of its observed classification.
        let private entryPath =
            function
            | RelevantEntry.File (path, _, _)
            | RelevantEntry.Directory path
            | RelevantEntry.Ignored path
            | RelevantEntry.Untracked path
            | RelevantEntry.ReparsePoint path -> path

        /// Converts an observed ordinary entry into its comparable exact shape.
        let private ordinaryExpected =
            function
            | RelevantEntry.File (_, sha256Hash, blake3Hash) -> Some(ExpectedEntry.File(sha256Hash, blake3Hash))
            | RelevantEntry.Directory _ -> Some ExpectedEntry.Directory
            | RelevantEntry.Ignored _
            | RelevantEntry.Untracked _
            | RelevantEntry.ReparsePoint _ -> None

        /// Builds complete accepted tracked topology while rejecting case and kind collisions.
        let private trackedTopology (status: GraceStatus) =
            let files = Dictionary<string, RelativePath * Sha256Hash * Blake3Hash>(StringComparer.OrdinalIgnoreCase)
            let directories = Dictionary<string, RelativePath>(StringComparer.OrdinalIgnoreCase)
            let mutable rejection = None

            status.Index.Values
            |> Seq.sortWith (fun left right -> StringComparer.OrdinalIgnoreCase.Compare(string left.RelativePath, string right.RelativePath))
            |> Seq.iter (fun directoryVersion ->
                let directoryPath = directoryVersion.RelativePath
                let directoryKey = key directoryPath

                if
                    files.ContainsKey(directoryKey)
                    || directories.ContainsKey(directoryKey)
                then
                    rejection <- Some { Path = directoryPath; Classification = AmbiguousTarget }
                else
                    directories[directoryKey] <- directoryPath

                directoryVersion.Files
                |> Seq.sortWith (fun left right -> StringComparer.OrdinalIgnoreCase.Compare(string left.RelativePath, string right.RelativePath))
                |> Seq.iter (fun fileVersion ->
                    let fileKey = key fileVersion.RelativePath

                    if
                        files.ContainsKey(fileKey)
                        || directories.ContainsKey(fileKey)
                    then
                        rejection <- Some { Path = fileVersion.RelativePath; Classification = AmbiguousTarget }
                    else
                        files[fileKey] <- fileVersion.RelativePath, fileVersion.Sha256Hash, fileVersion.Blake3Hash))

            rejection, files, directories

        /// Derives selected target files and every required parent directory from the normalized prepared manifest.
        let private targetTopology manifest =
            let files = Dictionary<string, RelativePath * Sha256Hash * Blake3Hash>(StringComparer.OrdinalIgnoreCase)
            let directories = Dictionary<string, RelativePath>(StringComparer.OrdinalIgnoreCase)
            let mutable rejection = None

            let addDirectory path =
                let directoryKey = key path

                if files.ContainsKey(directoryKey) then
                    rejection <- Some { Path = path; Classification = AmbiguousTarget }
                elif not (directories.ContainsKey(directoryKey)) then
                    directories[directoryKey] <- path

            let addParents path =
                let segments =
                    (string path)
                        .Split('/', StringSplitOptions.RemoveEmptyEntries)

                for index in 1 .. segments.Length - 1 do
                    addDirectory (RelativePath(String.Join('/', segments[0 .. index - 1])))

            WorkingDirectoryUpdateContracts.PreparedManifest.entries manifest
            |> Seq.iter (fun entry ->
                match entry with
                | WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory path ->
                    addDirectory path
                    addParents path
                | WorkingDirectoryUpdateContracts.PreparedManifestEntry.File (path, sha256Hash, blake3Hash) ->
                    let fileKey = key path

                    if
                        files.ContainsKey(fileKey)
                        || directories.ContainsKey(fileKey)
                    then
                        rejection <- Some { Path = path; Classification = AmbiguousTarget }
                    else
                        files[fileKey] <- path, sha256Hash, blake3Hash

                    addParents path)

            rejection, files, directories

        /// Builds a stable entry map and rejects only root escape and case aliases before relevance is known.
        let private actualTopology (RelevantTopology entries) =
            let actual = Dictionary<string, RelevantEntry>(StringComparer.OrdinalIgnoreCase)
            let mutable rejection = None

            entries
            |> List.iter (fun entry ->
                let path = entryPath entry
                let pathKey = key path

                if not (validRelativePath path) then
                    rejection <- Some { Path = path; Classification = EscapesLocalRoot }
                elif actual.ContainsKey(pathKey) then
                    rejection <- Some { Path = path; Classification = AmbiguousTarget }
                else
                    actual[pathKey] <- entry)

            rejection, actual, entries

        /// Reads an exact actual entry or the immutable absence represented by a complete snapshot.
        let private actualAt (actual: Dictionary<string, RelevantEntry>) path =
            match actual.TryGetValue(key path) with
            | true, entry -> ordinaryExpected entry
            | false, _ -> Some ExpectedEntry.Absent

        /// Checks whether any ignored, untracked, reparse, or undeclared entry occupies a subtree being destroyed.
        let private unsafeDescendant
            (trackedFiles: Dictionary<string, RelativePath * Sha256Hash * Blake3Hash>)
            (trackedDirectories: Dictionary<string, RelativePath>)
            (entries: RelevantEntry list)
            root
            =
            entries
            |> List.tryPick (fun entry ->
                let path = entryPath entry

                if path <> root && isAtOrBelow root path then
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

        /// Determines whether an exact file value equals one expected identity.
        let private equalsExpected expected actual =
            match expected, actual with
            | ExpectedEntry.File (expectedSha256, expectedBlake3), Some (ExpectedEntry.File (actualSha256, actualBlake3)) ->
                expectedSha256 = actualSha256
                && expectedBlake3 = actualBlake3
            | ExpectedEntry.Directory, Some ExpectedEntry.Directory
            | ExpectedEntry.Absent, Some ExpectedEntry.Absent -> true
            | _ -> false

        /// Builds one exact requirement without exposing a mutable action or filesystem object.
        let private requirement path expectedCurrent expectedFinal role state admissionMode =
            { Path = path; ExpectedCurrent = expectedCurrent; ExpectedFinal = expectedFinal; Role = role; State = state; AdmissionMode = admissionMode }

        /// Evaluates accepted status, selected manifest, admission policy, and one immutable real topology snapshot.
        let reconcile admissionMode currentStatus manifest topology =
            let trackedRejection, trackedFiles, trackedDirectories = trackedTopology currentStatus
            let targetRejection, targetFiles, targetDirectories = targetTopology manifest
            let actualRejection, actual, entries = actualTopology topology

            match trackedRejection, targetRejection, actualRejection with
            | Some rejection, _, _
            | _, Some rejection, _
            | _, _, Some rejection -> Rejected rejection
            | None, None, None ->
                let mutable rejection = None
                let removals = ResizeArray<Requirement>()
                let creates = ResizeArray<Requirement>()
                let copies = ResizeArray<Requirement>()

                let addRemoval path expected role =
                    let state =
                        match actualAt actual path with
                        | Some ExpectedEntry.Absent when admissionMode = ExactAdoption -> AlreadySatisfied
                        | Some actualEntry when equalsExpected expected (Some actualEntry) -> NeedsApply
                        | _ ->
                            rejection <- Some { Path = path; Classification = IdentityDrift }
                            NeedsApply

                    if Option.isNone rejection then
                        removals.Add(requirement path expected ExpectedEntry.Absent role state admissionMode)

                for KeyValue (_, (path, sha256Hash, blake3Hash)) in trackedFiles do
                    if Option.isNone rejection then
                        let needsRemoval =
                            not (targetFiles.ContainsKey(key path))
                            && (targetDirectories.ContainsKey(key path)
                                || targetFiles.Values
                                   |> Seq.exists (fun (targetPath, _, _) -> isAtOrBelow targetPath path))
                            || (not (targetFiles.ContainsKey(key path))
                                && not (targetDirectories.ContainsKey(key path)))

                        if needsRemoval then
                            addRemoval path (ExpectedEntry.File(sha256Hash, blake3Hash)) RemoveFile

                for KeyValue (_, path) in trackedDirectories do
                    if Option.isNone rejection
                       && string path <> RootDirectoryPath then
                        let needsRemoval =
                            not (targetDirectories.ContainsKey(key path))
                            || targetFiles.ContainsKey(key path)
                            || targetFiles.Values
                               |> Seq.exists (fun (targetPath, _, _) -> isAtOrBelow targetPath path)

                        if needsRemoval then
                            match unsafeDescendant trackedFiles trackedDirectories entries path with
                            | Some unsafe -> rejection <- Some unsafe
                            | None -> addRemoval path ExpectedEntry.Directory RemoveDirectory

                for KeyValue (_, targetPath) in targetDirectories do
                    if Option.isNone rejection
                       && string targetPath <> RootDirectoryPath then
                        let targetActual = actualAt actual targetPath
                        let trackedDirectory = trackedDirectories.ContainsKey(key targetPath)
                        let trackedFile = trackedFiles.ContainsKey(key targetPath)

                        match targetActual with
                        | Some ExpectedEntry.Absent ->
                            creates.Add(requirement targetPath ExpectedEntry.Absent ExpectedEntry.Directory CreateDirectory NeedsApply admissionMode)
                        | Some ExpectedEntry.Directory when trackedDirectory ->
                            let state = if admissionMode = Fresh then NeedsApply else AlreadySatisfied
                            creates.Add(requirement targetPath ExpectedEntry.Directory ExpectedEntry.Directory Retained state admissionMode)
                        | Some ExpectedEntry.Directory when admissionMode = ExactAdoption ->
                            creates.Add(requirement targetPath ExpectedEntry.Directory ExpectedEntry.Directory Retained AlreadySatisfied admissionMode)
                        | Some (ExpectedEntry.File _) when trackedFile ->
                            creates.Add(requirement targetPath ExpectedEntry.Absent ExpectedEntry.Directory CreateDirectory NeedsApply admissionMode)
                        | _ -> rejection <- Some { Path = targetPath; Classification = RejectionClassification.Untracked }

                for KeyValue (_, (targetPath, targetSha256, targetBlake3)) in targetFiles do
                    if Option.isNone rejection then
                        let targetExpected = ExpectedEntry.File(targetSha256, targetBlake3)
                        let targetActual = actualAt actual targetPath

                        match targetActual, trackedFiles.TryGetValue(key targetPath), trackedDirectories.ContainsKey(key targetPath) with
                        | Some ExpectedEntry.Absent, _, _ ->
                            copies.Add(requirement targetPath ExpectedEntry.Absent targetExpected Copy NeedsApply admissionMode)
                        | Some (ExpectedEntry.File _), (true, (_, oldSha256, oldBlake3)), _ when
                            oldSha256 = targetSha256
                            && oldBlake3 = targetBlake3
                            ->
                            let state = if admissionMode = Fresh then NeedsApply else AlreadySatisfied
                            copies.Add(requirement targetPath targetExpected targetExpected Retained state admissionMode)
                        | Some actualEntry, (true, (_, oldSha256, oldBlake3)), _ ->
                            let oldExpected = ExpectedEntry.File(oldSha256, oldBlake3)

                            if equalsExpected oldExpected (Some actualEntry) then
                                copies.Add(requirement targetPath oldExpected targetExpected Copy NeedsApply admissionMode)
                            elif admissionMode = ExactAdoption
                                 && equalsExpected targetExpected (Some actualEntry) then
                                copies.Add(requirement targetPath targetExpected targetExpected Copy AlreadySatisfied admissionMode)
                            else
                                rejection <- Some { Path = targetPath; Classification = IdentityDrift }
                        | Some (ExpectedEntry.File _), (false, _), _ when
                            admissionMode = ExactAdoption
                            && equalsExpected targetExpected targetActual
                            ->
                            copies.Add(requirement targetPath targetExpected targetExpected Copy AlreadySatisfied admissionMode)
                        | Some ExpectedEntry.Directory, _, true ->
                            copies.Add(requirement targetPath ExpectedEntry.Absent targetExpected Copy NeedsApply admissionMode)
                        | _ -> rejection <- Some { Path = targetPath; Classification = RejectionClassification.Untracked }

                match rejection with
                | Some rejected -> Rejected rejected
                | None ->
                    let orderedRemovals =
                        removals
                        |> Seq.sortWith (fun left right ->
                            let byDepth = compare (depth right.Path) (depth left.Path)

                            if byDepth <> 0 then
                                byDepth
                            else
                                StringComparer.OrdinalIgnoreCase.Compare(string left.Path, string right.Path))
                        |> Seq.toList

                    let orderedCreates =
                        creates
                        |> Seq.sortWith (fun left right ->
                            let byDepth = compare (depth left.Path) (depth right.Path)

                            if byDepth <> 0 then
                                byDepth
                            else
                                StringComparer.OrdinalIgnoreCase.Compare(string left.Path, string right.Path))
                        |> Seq.toList

                    let orderedCopies =
                        copies
                        |> Seq.sortWith (fun left right -> StringComparer.OrdinalIgnoreCase.Compare(string left.Path, string right.Path))
                        |> Seq.toList

                    Requirements(orderedRemovals @ orderedCreates @ orderedCopies)
                    |> Reconciled
