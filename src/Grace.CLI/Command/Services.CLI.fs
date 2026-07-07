namespace Grace.CLI

open Microsoft.Extensions
open FSharp.Collections
open Grace.CLI.Text
open Grace.SDK
open Grace.Shared.Client.Configuration
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Parameters.DirectoryVersion
open Grace.Shared.Services
open Grace.Types.Common
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open NodaTime
open NodaTime.Text
open Spectre.Console
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.CommandLine
open System.CommandLine.Parsing
open System.Diagnostics
open System.Globalization
open System.IO
open System.IO.Compression
open System.IO.Enumeration
open System.IO.Pipelines
open System.Linq
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open System.Reactive.Linq
open System.Threading
open Grace.Shared.Parameters
open Grace.Shared.Parameters.Branch
open Grace.Shared.Parameters.Storage
open Grace.Types.Branch
open Grace.Types.Reference
open System.Runtime.Intrinsics.Arm

/// Groups the services command parser, handlers, and output helpers.
module Services =

    let mutable lockObject = new Lock()

    /// Utility method to write to the console using color.
    let logToAnsiConsole (color: string) message =
        lock lockObject (fun () ->
            AnsiConsole.MarkupLine $"[{color}]{getCurrentInstantExtended ()} {Environment.CurrentManagedThreadId:X2} {Markup.Escape(message)}[/]")

    /// A cache of paths that we've already decided to ignore or not.
    let private shouldIgnoreCache = ConcurrentDictionary<FilePath, bool>()

    /// Clears process-local `.graceignore` decisions after repository configuration changes.
    let clearShouldIgnoreCache () = shouldIgnoreCache.Clear()

    // This section is "borrowed" from Common.CLI.fs, because Services.CLI.fs comes before Common.CLI.fs in the build order.

    /// Checks if the output format from the command line is a specific format.
    let private isOutputFormat (outputFormat: string) (parseResult: ParseResult) =
        if parseResult
            .ToString()
            .IndexOf($"<{outputFormat}>", StringComparison.InvariantCultureIgnoreCase) > 0 then
            true
        else if outputFormat = "Normal" then
            true
        else
            false

    /// Names the compact runtime state advertised by the Grace Watch IPC status file.
    type GraceWatchRuntimeMode =
        /// Grace Watch has claimed startup ownership but has not published a usable status snapshot yet.
        | StartingUp = 0
        /// Grace Watch has a usable root snapshot and can serve incremental status shortcuts.
        | HealthyIncremental = 1
        /// Grace Watch is rebuilding trusted state before incremental shortcuts can be used.
        | Resynchronizing = 2
        /// Grace Watch has stopped accepting incremental shortcuts until an explicit resume or resync occurs.
        | Suspended = 3
        /// Grace Watch is shutting down and should not be treated as a live incremental source.
        | Stopping = 4

    /// Checks whether the repository root resolves differently-cased names to the same file.
    let private detectWatchRootPathCaseInsensitiveLookup (rootDirectory: string) =
        let mutable probePath = String.Empty
        let mutable alternateProbePath = String.Empty

        try
            try
                let probeDirectory = Path.Combine(rootDirectory, Constants.GraceConfigDirectory)

                Directory.CreateDirectory(probeDirectory)
                |> ignore

                let probeName = $"grace-watch-root-case-probe-{Guid.NewGuid():N}"
                probePath <- Path.Combine(probeDirectory, probeName.ToLowerInvariant())
                alternateProbePath <- Path.Combine(probeDirectory, probeName.ToUpperInvariant())

                File.WriteAllText(probePath, String.Empty)
                File.Exists(alternateProbePath)
            with
            | _ -> OperatingSystem.IsWindows()
        finally
            for path in [| probePath; alternateProbePath |] do
                try
                    if
                        not (String.IsNullOrWhiteSpace(path))
                        && File.Exists(path)
                    then
                        File.Delete(path)
                with
                | _ -> ()

    let mutable private watchRootPathCaseInsensitiveLookup = detectWatchRootPathCaseInsensitiveLookup

    /// Chooses the root path identity comparison used when trusting Watch IPC snapshots.
    let private watchRootPathComparisonForRepository rootDirectory =
        if watchRootPathCaseInsensitiveLookup rootDirectory then
            StringComparison.OrdinalIgnoreCase
        else
            StringComparison.Ordinal

    /// Compares normalized repository root paths using the supplied repository path identity semantics.
    let internal watchRootDirectoriesMatchWithComparison comparison persistedRootDirectory currentRootDirectory =
        let normalizeRootDirectory (rootDirectory: string) =
            Path
                .GetFullPath(rootDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

        String.Equals(normalizeRootDirectory persistedRootDirectory, normalizeRootDirectory currentRootDirectory, comparison)

    /// Installs root path case lookup behavior used by Watch IPC identity tests.
    let internal setWatchRootPathCaseInsensitiveLookupForTests detector = watchRootPathCaseInsensitiveLookup <- detector

    /// Restores the production root path case lookup behavior after Watch IPC identity tests.
    let internal resetWatchRootPathCaseInsensitiveLookupForTests () = watchRootPathCaseInsensitiveLookup <- detectWatchRootPathCaseInsensitiveLookup

    /// GraceWatchStatus defines the schema for the inter-process communication (IPC) file that lets Grace know if `grace watch` is already running.
    ///
    /// It's written by `grace watch`. It holds everything required to allow other instances of Grace to skip checking current status.
    [<Struct>]
    type GraceWatchStatus =
        {
            UpdatedAt: Instant
            IsStartupClaim: bool
            RepositoryId: RepositoryId
            RepositoryName: RepositoryName
            BranchId: BranchId
            BranchName: BranchName
            RootDirectory: string
            HasPendingWatchWork: bool
            IsWorkingTreeClean: bool
            RootDirectoryId: DirectoryVersionId
            RootDirectorySha256Hash: Sha256Hash
            RootDirectoryBlake3Hash: Blake3Hash
            LastFileUploadInstant: Instant
            LastDirectoryVersionInstant: Instant
            DirectoryIds: HashSet<DirectoryVersionId>
        }

        /// Reports whether this status has the root identity fields required for trusted incremental consumers.
        member private this.HasUsableRootSnapshot =
            this.RootDirectoryId <> Guid.Empty
            && not (String.IsNullOrWhiteSpace($"{this.RootDirectorySha256Hash}"))
            && not (String.IsNullOrWhiteSpace($"{this.RootDirectoryBlake3Hash}"))

        /// Reports whether this status has at least one directory identity for current-state comparisons.
        member private this.HasDirectoryIndexSnapshot =
            not (isNull this.DirectoryIds)
            && this.DirectoryIds.Count > 0

        /// Reports whether this status is recent enough to represent a live Watch process.
        member private this.IsFreshSnapshot =
            this.UpdatedAt > getCurrentInstant()
                .Minus(Duration.FromMinutes(5.0))

        /// Identifies the current Grace Watch runtime mode without introducing a larger state hierarchy.
        member this.Mode =
            if this.IsStartupClaim then
                GraceWatchRuntimeMode.StartingUp
            elif this.HasUsableRootSnapshot
                 && this.HasDirectoryIndexSnapshot then
                GraceWatchRuntimeMode.HealthyIncremental
            else
                GraceWatchRuntimeMode.Resynchronizing

        /// Lists compact safety facts that command and agent consumers can inspect before trusting the status snapshot.
        member this.SafetyFlags =
            let isStartupClaim = this.IsStartupClaim
            let hasUsableRootSnapshot = this.HasUsableRootSnapshot
            let hasDirectoryIndexSnapshot = this.HasDirectoryIndexSnapshot
            let isFreshSnapshot = this.IsFreshSnapshot
            let mode = this.Mode

            let hasPendingWatchWork =
                this.HasPendingWatchWork
                || not this.IsWorkingTreeClean

            let isTrustedCleanSnapshot =
                mode = GraceWatchRuntimeMode.HealthyIncremental
                && isFreshSnapshot
                && not isStartupClaim
                && hasUsableRootSnapshot
                && hasDirectoryIndexSnapshot
                && not hasPendingWatchWork

            let isLiveDirtySnapshot =
                mode = GraceWatchRuntimeMode.HealthyIncremental
                && isFreshSnapshot
                && not isStartupClaim
                && hasUsableRootSnapshot
                && hasDirectoryIndexSnapshot
                && hasPendingWatchWork

            [|
                if isStartupClaim then "startupClaim"

                if not isFreshSnapshot then "staleStatus"

                if hasPendingWatchWork then "pendingWatchWork"
                elif isTrustedCleanSnapshot then "cleanWorkingTree"
                else "noQueuedWatchWork"

                if hasUsableRootSnapshot then "usableRoot" else "missingRoot"

                if hasDirectoryIndexSnapshot then "directoryIndex" else "missingDirectoryIndex"

                if isTrustedCleanSnapshot then "incrementalSafe"
                elif isLiveDirtySnapshot then "pendingStatusApply"
                else "requiresExplicitResync"
            |]

        /// Defines the empty Grace Watch status used before a live status snapshot is available.
        static member Default =
            {
                UpdatedAt = Instant.MinValue
                IsStartupClaim = false
                RepositoryId = RepositoryId.Empty
                RepositoryName = RepositoryName String.Empty
                BranchId = BranchId.Empty
                BranchName = BranchName String.Empty
                RootDirectory = String.Empty
                HasPendingWatchWork = false
                IsWorkingTreeClean = true
                RootDirectoryId = Guid.Empty
                RootDirectorySha256Hash = Sha256Hash String.Empty
                RootDirectoryBlake3Hash = Blake3Hash String.Empty
                LastFileUploadInstant = Instant.MinValue
                LastDirectoryVersionInstant = Instant.MinValue
                DirectoryIds = HashSet<DirectoryVersionId>()
            }

    /// Mirrors GraceWatchStatus for IPC writes while adding compact runtime fields that older readers can ignore.
    type private GraceWatchStatusContract =
        {
            UpdatedAt: Instant
            IsStartupClaim: bool
            RepositoryId: RepositoryId
            RepositoryName: RepositoryName
            BranchId: BranchId
            BranchName: BranchName
            RootDirectory: string
            HasPendingWatchWork: bool
            IsWorkingTreeClean: bool
            RootDirectoryId: DirectoryVersionId
            RootDirectorySha256Hash: Sha256Hash
            RootDirectoryBlake3Hash: Blake3Hash
            LastFileUploadInstant: Instant
            LastDirectoryVersionInstant: Instant
            DirectoryIds: HashSet<DirectoryVersionId>
            Mode: GraceWatchRuntimeMode option
            SafetyFlags: string array
        }

    /// Describes the Watch IPC snapshot without requiring it to be trusted for incremental status shortcuts.
    type GraceWatchStatusInspection =
        {
            Exists: bool
            Status: GraceWatchStatus option
            PersistedMode: GraceWatchRuntimeMode option
            SafetyFlags: string array
            ReadError: string option
        }

        /// Reports the best runtime mode available from persisted IPC metadata or derived status facts.
        member this.EffectiveMode =
            match this.PersistedMode, this.Status with
            | Some mode, _ -> Some mode
            | None, Some status -> Some status.Mode
            | None, None -> None

        /// Reports whether the IPC heartbeat is recent enough to represent a live Watch process.
        member this.IsFresh =
            match this.Status with
            | Some status ->
                status.UpdatedAt > getCurrentInstant()
                    .Minus(Duration.FromMinutes(5.0))
            | None -> false

        /// Reports whether the IPC snapshot belongs to the current repository identity before trust checks.
        member this.HasCurrentRepositoryIdentity =
            match this.Status, this.EffectiveMode with
            | Some status, Some _ ->
                let current = Current()

                let rootDirectoryMatchesCurrent =
                    if String.IsNullOrWhiteSpace(status.RootDirectory) then
                        false
                    else
                        try
                            watchRootDirectoriesMatchWithComparison
                                (watchRootPathComparisonForRepository current.RootDirectory)
                                status.RootDirectory
                                current.RootDirectory
                        with
                        | _ -> false

                let repositoryIdentityMatches =
                    if status.RepositoryId <> RepositoryId.Empty then
                        status.RepositoryId = current.RepositoryId
                    else
                        not (String.IsNullOrWhiteSpace(string status.RepositoryName))
                        && String.Equals(string status.RepositoryName, current.RepositoryName, StringComparison.OrdinalIgnoreCase)

                let branchIdentityMatches =
                    if status.BranchId <> BranchId.Empty then
                        status.BranchId = current.BranchId
                    else
                        not (String.IsNullOrWhiteSpace(string status.BranchName))
                        && String.Equals(string status.BranchName, current.BranchName, StringComparison.OrdinalIgnoreCase)

                repositoryIdentityMatches
                && branchIdentityMatches
                && rootDirectoryMatchesCurrent
            | _ -> false

        /// Reports whether a recovery snapshot came from a same-branch legacy Watch writer without root identity fields.
        member private this.HasLegacyLiveWatchStateIdentity =
            match this.Status with
            | Some status ->
                String.IsNullOrWhiteSpace(status.RootDirectory)
                && status.RepositoryId = RepositoryId.Empty
                && String.IsNullOrWhiteSpace(string status.RepositoryName)
                && status.BranchId = BranchId.Empty
                && String.IsNullOrWhiteSpace(string status.BranchName)
            | None -> false

        /// Reports whether live Watch state has enough persisted identity to be preserved by another command.
        member this.HasCurrentLiveWatchStateIdentity =
            match this.Status, this.EffectiveMode with
            | Some _, Some effectiveMode ->
                this.HasCurrentRepositoryIdentity
                || (effectiveMode
                    <> GraceWatchRuntimeMode.HealthyIncremental
                    && this.HasLegacyLiveWatchStateIdentity)
            | _ -> false

        /// Reports whether the IPC snapshot can serve trusted incremental shortcuts.
        member this.IsUsable =
            match this.Status, this.EffectiveMode with
            | Some status, Some GraceWatchRuntimeMode.HealthyIncremental ->
                this.IsFresh
                && not status.IsStartupClaim
                && status.Mode = GraceWatchRuntimeMode.HealthyIncremental
                && status.IsWorkingTreeClean
                && not status.HasPendingWatchWork
                && this.HasCurrentRepositoryIdentity
            | _ -> false

        /// Reports whether a fresh Watch process appears to own the IPC file even when shortcuts are unavailable.
        member this.IsLiveProcess =
            this.Exists
            && this.IsFresh
            && this.Status.IsSome
            && this.EffectiveMode
               <> Some GraceWatchRuntimeMode.Stopping

    /// Reports whether a compact Watch safety flag must be recomputed from a fresh heartbeat before consumers trust it.
    let private isRawGraceWatchSafetyFlagLivenessSensitive safetyFlag =
        String.Equals(safetyFlag, "incrementalSafe", StringComparison.Ordinal)
        || String.Equals(safetyFlag, "cleanWorkingTree", StringComparison.Ordinal)
        || String.Equals(safetyFlag, "pendingStatusApply", StringComparison.Ordinal)

    /// Reports whether a compact Watch safety flag would let a command skip local status verification.
    let private isCleanShortcutSafetyFlag safetyFlag =
        String.Equals(safetyFlag, "incrementalSafe", StringComparison.Ordinal)
        || String.Equals(safetyFlag, "cleanWorkingTree", StringComparison.Ordinal)

    /// Reports whether a compact Watch safety flag depends on current-repository identity validation.
    let private isIdentitySensitiveSafetyFlag safetyFlag =
        isCleanShortcutSafetyFlag safetyFlag
        || String.Equals(safetyFlag, "pendingStatusApply", StringComparison.Ordinal)

    /// Reports whether a compact Watch safety flag should be kept after inspection trust checks.
    let private isAllowedInspectedSafetyFlag isCurrentRepositoryLiveDirtySnapshot hasCurrentRepositoryIdentity safetyFlag =
        if isCleanShortcutSafetyFlag safetyFlag then
            false
        elif String.Equals(safetyFlag, "pendingStatusApply", StringComparison.Ordinal) then
            isCurrentRepositoryLiveDirtySnapshot
        elif hasCurrentRepositoryIdentity then
            true
        else
            not (isIdentitySensitiveSafetyFlag safetyFlag)

    /// Removes liveness-sensitive safety claims from persisted IPC JSON so raw readers never trust a dead Watch process.
    let private safetyFlagsForGraceWatchStatusContract persistedModeOverride (status: GraceWatchStatus) =
        let safetyFlags =
            status.SafetyFlags
            |> Array.filter (isRawGraceWatchSafetyFlagLivenessSensitive >> not)

        match persistedModeOverride with
        | Some GraceWatchRuntimeMode.Suspended
        | Some GraceWatchRuntimeMode.Resynchronizing
        | Some GraceWatchRuntimeMode.Stopping ->
            let nonIncrementalFlags =
                if safetyFlags
                   |> Array.exists (fun safetyFlag -> String.Equals(safetyFlag, "pendingWatchWork", StringComparison.Ordinal)) then
                    [| "requiresExplicitResync" |]
                else
                    [|
                        "noQueuedWatchWork"
                        "requiresExplicitResync"
                    |]

            safetyFlags
            |> Array.append nonIncrementalFlags
            |> Array.distinct
        | _ -> safetyFlags

    /// Removes clean shortcut claims from inspected IPC when current repository identity makes the snapshot unusable.
    let private safetyFlagsForGraceWatchStatusInspection (inspection: GraceWatchStatusInspection) =
        if inspection.IsUsable then
            inspection.SafetyFlags
        else
            let isCurrentRepositoryLiveDirtySnapshot =
                match inspection.Status, inspection.EffectiveMode with
                | Some status, Some GraceWatchRuntimeMode.HealthyIncremental ->
                    inspection.IsFresh
                    && not status.IsStartupClaim
                    && status.Mode = GraceWatchRuntimeMode.HealthyIncremental
                    && inspection.HasCurrentRepositoryIdentity
                    && (status.HasPendingWatchWork
                        || not status.IsWorkingTreeClean)
                | _ -> false

            let safetyFlags =
                inspection.SafetyFlags
                |> Array.filter (isAllowedInspectedSafetyFlag isCurrentRepositoryLiveDirtySnapshot inspection.HasCurrentRepositoryIdentity)

            if safetyFlags
               |> Array.exists (fun safetyFlag -> String.Equals(safetyFlag, "requiresExplicitResync", StringComparison.Ordinal)) then
                safetyFlags
            elif isCurrentRepositoryLiveDirtySnapshot then
                safetyFlags
            else
                [|
                    yield! safetyFlags
                    "requiresExplicitResync"
                |]
                |> Array.distinct

    /// Selects only durable compact modes for persisted IPC JSON so liveness must be derived from the raw status data.
    let private modeForGraceWatchStatusContract (status: GraceWatchStatus) =
        match status.Mode with
        | GraceWatchRuntimeMode.StartingUp
        | GraceWatchRuntimeMode.HealthyIncremental
        | GraceWatchRuntimeMode.Resynchronizing
        | GraceWatchRuntimeMode.Stopping -> None
        | mode -> Some mode

    /// Reports whether Grace Watch may perform a full working-tree scan in the supplied runtime mode.
    let isGraceWatchScanLegal mode =
        match mode with
        | GraceWatchRuntimeMode.StartingUp
        | GraceWatchRuntimeMode.Resynchronizing -> true
        | GraceWatchRuntimeMode.HealthyIncremental
        | GraceWatchRuntimeMode.Suspended
        | GraceWatchRuntimeMode.Stopping
        | _ -> false

    /// Reports whether Grace Watch may record filesystem observations for later incremental application.
    let isGraceWatchObservationCaptureLegal mode =
        match mode with
        | GraceWatchRuntimeMode.StartingUp
        | GraceWatchRuntimeMode.HealthyIncremental
        | GraceWatchRuntimeMode.Resynchronizing -> true
        | GraceWatchRuntimeMode.Suspended
        | GraceWatchRuntimeMode.Stopping
        | _ -> false

    /// Reports whether Grace Watch may apply normal event-derived observations to Grace Status and branch history.
    let isGraceWatchObservationApplicationLegal mode =
        match mode with
        | GraceWatchRuntimeMode.HealthyIncremental -> true
        | GraceWatchRuntimeMode.StartingUp
        | GraceWatchRuntimeMode.Resynchronizing
        | GraceWatchRuntimeMode.Suspended
        | GraceWatchRuntimeMode.Stopping
        | _ -> false

    /// Names the reason Watch either should or should not materialize a same-branch remote Reference.
    type LatestCurrentBranchReferenceDecisionReason =
        /// No supplied Reference notification applies to the requested repository, branch, and eligible Reference types.
        | NoApplicableReference = 0
        /// The latest applicable Reference does not include the remote root identity required for safe materialization.
        | ReferenceRootIdentityUnavailable = 1
        /// The local Watch status is unavailable, so remote materialization cannot proceed from a trusted boundary.
        | LocalStatusUnavailable = 2
        /// The local Watch status belongs to a different repository or branch than the chosen Reference.
        | LocalStatusIdentityMismatch = 3
        /// The local Watch status is not a clean healthy-incremental snapshot.
        | LocalStatusRequiresResync = 4
        /// The remote Reference already matches the local root identity, so materialization would be a no-op.
        | SameRoot = 5
        /// The remote Reference is the latest applicable current-branch Reference and differs from the local root.
        | RemoteMaterializationRequired = 6

    /// Carries the Watch decision for the latest applicable current-branch Reference notification.
    type LatestCurrentBranchReferenceDecision =
        {
            NeedsMaterialization: bool
            Reason: LatestCurrentBranchReferenceDecisionReason
            Reference: CurrentBranchReferenceNotification option
        }

        /// Defines the deterministic no-reference decision used when all candidates are stale or unsupported.
        static member NoApplicableReference =
            { NeedsMaterialization = false; Reason = LatestCurrentBranchReferenceDecisionReason.NoApplicableReference; Reference = None }

    /// Reports whether a current-branch Reference notification has enough root identity for a deterministic decision.
    let private currentBranchReferenceHasRootIdentity (payload: CurrentBranchReferenceNotification) =
        payload.DirectoryId <> DirectoryVersionId.Empty
        && not (String.IsNullOrWhiteSpace($"{payload.Sha256Hash}"))
        && not (String.IsNullOrWhiteSpace($"{payload.Blake3Hash}"))

    /// Reports whether a current-branch Reference notification applies to this Watch repository and branch.
    let private currentBranchReferenceAppliesToWatch repositoryId branchId (payload: CurrentBranchReferenceNotification) =
        payload.RepositoryId = repositoryId
        && payload.BranchId = branchId
        && CurrentBranchReferenceNotification.IsEligibleReferenceType payload.ReferenceType

    /// Reports whether a remote Reference points at the same root already held by the local Watch status.
    let private currentBranchReferenceMatchesLocalRoot (status: GraceWatchStatus) (payload: CurrentBranchReferenceNotification) =
        status.RootDirectoryId = payload.DirectoryId
        || (status.RootDirectorySha256Hash = payload.Sha256Hash
            && status.RootDirectoryBlake3Hash = payload.Blake3Hash)

    /// Chooses whether the latest applicable current-branch Reference requires remote materialization.
    let decideLatestCurrentBranchReferenceMaterialization
        repositoryId
        branchId
        (localStatus: GraceWatchStatus option)
        (payloads: CurrentBranchReferenceNotification seq)
        =
        let latestApplicable =
            payloads
            |> Seq.filter (currentBranchReferenceAppliesToWatch repositoryId branchId)
            |> Seq.tryLast

        match latestApplicable with
        | None -> LatestCurrentBranchReferenceDecision.NoApplicableReference
        | Some payload when not (currentBranchReferenceHasRootIdentity payload) ->
            { NeedsMaterialization = false; Reason = LatestCurrentBranchReferenceDecisionReason.ReferenceRootIdentityUnavailable; Reference = Some payload }
        | Some payload ->
            match localStatus with
            | None -> { NeedsMaterialization = false; Reason = LatestCurrentBranchReferenceDecisionReason.LocalStatusUnavailable; Reference = Some payload }
            | Some status when
                status.RepositoryId <> repositoryId
                || status.BranchId <> branchId
                ->
                { NeedsMaterialization = false; Reason = LatestCurrentBranchReferenceDecisionReason.LocalStatusIdentityMismatch; Reference = Some payload }
            | Some status when
                status.SafetyFlags
                |> Array.exists (fun safetyFlag -> String.Equals(safetyFlag, "incrementalSafe", StringComparison.Ordinal))
                |> not
                ->
                { NeedsMaterialization = false; Reason = LatestCurrentBranchReferenceDecisionReason.LocalStatusRequiresResync; Reference = Some payload }
            | Some status when currentBranchReferenceMatchesLocalRoot status payload ->
                { NeedsMaterialization = false; Reason = LatestCurrentBranchReferenceDecisionReason.SameRoot; Reference = Some payload }
            | Some _ ->
                { NeedsMaterialization = true; Reason = LatestCurrentBranchReferenceDecisionReason.RemoteMaterializationRequired; Reference = Some payload }

    /// Converts the in-memory Watch status model to the IPC JSON contract written for commands and agents.
    let private toGraceWatchStatusContractWithPersistedMode persistedModeOverride (status: GraceWatchStatus) =
        {
            UpdatedAt = status.UpdatedAt
            IsStartupClaim = status.IsStartupClaim
            RepositoryId = status.RepositoryId
            RepositoryName = status.RepositoryName
            BranchId = status.BranchId
            BranchName = status.BranchName
            RootDirectory = status.RootDirectory
            HasPendingWatchWork = status.HasPendingWatchWork
            IsWorkingTreeClean = status.IsWorkingTreeClean
            RootDirectoryId = status.RootDirectoryId
            RootDirectorySha256Hash = status.RootDirectorySha256Hash
            RootDirectoryBlake3Hash = status.RootDirectoryBlake3Hash
            LastFileUploadInstant = status.LastFileUploadInstant
            LastDirectoryVersionInstant = status.LastDirectoryVersionInstant
            DirectoryIds = status.DirectoryIds
            Mode =
                persistedModeOverride
                |> Option.orElseWith (fun () -> modeForGraceWatchStatusContract status)
            SafetyFlags = safetyFlagsForGraceWatchStatusContract persistedModeOverride status
        }

    /// Converts the in-memory Watch status model without forcing a runtime mode into IPC JSON.
    let private toGraceWatchStatusContract status = toGraceWatchStatusContractWithPersistedMode None status

    /// Writes the compact Watch IPC JSON contract to the already-open status stream.
    let private writeGraceWatchStatusContractToStream fileStream graceWatchStatus = serializeAsync fileStream (toGraceWatchStatusContract graceWatchStatus)

    /// Writes the compact Watch IPC JSON contract while preserving a known non-incremental runtime mode.
    let private writeGraceWatchStatusContractWithPersistedModeToStream fileStream mode graceWatchStatus =
        serializeAsync fileStream (toGraceWatchStatusContractWithPersistedMode (Some mode) graceWatchStatus)

    /// Reads the compact persisted Watch runtime mode from IPC JSON written by newer Watch processes.
    let private tryReadGraceWatchPersistedMode (json: string) =
        try
            use document = JsonDocument.Parse(json)
            let mutable modeElement = Unchecked.defaultof<JsonElement>

            if document.RootElement.TryGetProperty("Mode", &modeElement)
               && modeElement.ValueKind <> JsonValueKind.Null
               && modeElement.ValueKind <> JsonValueKind.Undefined then
                let modeText =
                    if modeElement.ValueKind = System.Text.Json.JsonValueKind.String then
                        modeElement.GetString()
                    else
                        modeElement.GetRawText()

                let mutable parsedMode = Unchecked.defaultof<GraceWatchRuntimeMode>

                if Enum.TryParse<GraceWatchRuntimeMode>(modeText, true, &parsedMode) then
                    Some parsedMode
                else
                    None
            else
                None
        with
        | _ -> None

    let mutable graceWatchStatusUpdateTime = Instant.MinValue
    let mutable private graceWatchHasPendingWorkForStatus = 0
    let private graceWatchStatusWriteGate = new SemaphoreSlim(1, 1)
    let mutable parseResult: ParseResult = null
    let mutable private invocationCorrelationId: CorrelationId option = None

    /// Records whether the next Grace Watch IPC write should advertise unapplied local observations.
    let internal setGraceWatchHasPendingWorkForStatus hasPendingWork =
        Interlocked.Exchange(&graceWatchHasPendingWorkForStatus, (if hasPendingWork then 1 else 0))
        |> ignore

    /// Reads the pending-work fact that is copied into Grace Watch IPC status snapshots.
    let private hasGraceWatchPendingWorkForStatus () =
        Volatile.Read(&graceWatchHasPendingWorkForStatus)
        <> 0

    /// Restores fields omitted by Watch IPC snapshots written before the current status identity and clean contract.
    let private normalizeLegacyGraceWatchStatusFields (json: string) =
        try
            let statusNode = JsonNode.Parse(json).AsObject()

            let defaultStatusNode =
                JsonNode
                    .Parse(serialize GraceWatchStatus.Default)
                    .AsObject()

            let mutable addedLegacyField = false

            for defaultProperty in defaultStatusNode do
                if not (statusNode.ContainsKey(defaultProperty.Key)) then
                    statusNode[defaultProperty.Key] <- defaultProperty.Value.DeepClone()
                    addedLegacyField <- true

            if addedLegacyField then
                statusNode.ToJsonString(Constants.JsonSerializerOptions)
            else
                json
        with
        | _ -> json

    /// Reads Watch IPC JSON while backfilling fields omitted by legacy Watch writers.
    let private deserializeNormalizedGraceWatchStatus (json: string) =
        json
        |> normalizeLegacyGraceWatchStatusFields
        |> deserialize<GraceWatchStatus>

    /// Coordinates directory version preimage entries behavior for this CLI command path.
    let private directoryVersionPreimageEntries (directories: seq<LocalDirectoryVersion>) (files: seq<LocalFileVersion>) =
        let directoryEntries =
            directories
            |> Seq.map (fun directory -> DirectoryVersionPreimageEntry.Directory directory.RelativePath directory.Size directory.Blake3Hash directory.Sha256Hash)

        let fileEntries =
            files
            |> Seq.map (fun file -> DirectoryVersionPreimageEntry.File file.RelativePath file.Size file.Blake3Hash file.Sha256Hash)

        Seq.append directoryEntries fileEntries
        |> Seq.toArray

    /// Checks whether compute directory version hashes is true for the parsed command input.
    let private computeDirectoryVersionHashes relativeDirectoryPath directories files =
        let entries = directoryVersionPreimageEntries directories files

        let sha256Hash = computeSha256ForDirectoryEntries relativeDirectoryPath entries
        let blake3Hash = computeBlake3ForDirectory relativeDirectoryPath entries

        sha256Hash, blake3Hash

    /// Coordinates reset invocation correlation id behavior for this CLI command path.
    let resetInvocationCorrelationId () = invocationCorrelationId <- None

    /// Reads cli assembly file version from ParseResult, local configuration, or Grace ids.
    let getCliAssemblyFileVersion () =
        let assembly = Assembly.GetExecutingAssembly()

        if String.IsNullOrWhiteSpace assembly.Location then
            $"{assembly.GetName().Version}"
        else
            let fileVersion =
                FileVersionInfo
                    .GetVersionInfo(
                        assembly.Location
                    )
                    .FileVersion

            if String.IsNullOrWhiteSpace fileVersion then
                $"{assembly.GetName().Version}"
            else
                fileVersion

    /// Reads cli client type from ParseResult, local configuration, or Grace ids.
    let getCliClientType () = ClientType.CLI(getCliAssemblyFileVersion ())

    /// Coordinates configure sdk client identity behavior for this CLI command path.
    let configureSdkClientIdentity () = Grace.SDK.ClientIdentity.configure (getCliClientType ())

    // Extension methods for dealing with local file changes.
    /// Adds CLI helper members to an existing Grace type.
    type DirectoryVersion with
        /// Gets the full path for this file in the working directory.
        member this.FullName = Path.Combine(Current().RootDirectory, $"{this.RelativePath}")

    // Extension methods for dealing with local files.
    /// Adds CLI helper members to an existing Grace type.
    type LocalDirectoryVersion with
        /// Gets the full path for this file in the working directory.
        member this.FullName = Path.Combine(Current().RootDirectory, $"{this.RelativePath}")
        /// Gets a DirectoryInfo instance for the parent directory of this local file.
        member this.DirectoryInfo = DirectoryInfo(this.FullName)

    /// Gets the local object-cache file name. Rows without BLAKE3 keep the SHA-only name; dual-hash rows
    /// include BLAKE3 so same-path SHA-256 collisions do not overwrite or reuse the wrong local bytes.
    let getLocalObjectCacheFileName (relativePath: RelativePath) (sha256Hash: Sha256Hash) (blake3Hash: Blake3Hash) =
        if String.IsNullOrWhiteSpace(string blake3Hash) then
            getObjectFileName relativePath sha256Hash
        else
            getObjectFileName relativePath (Sha256Hash $"{sha256Hash}_{blake3Hash}")

    // Extension methods for dealing with local files.
    /// Adds CLI helper members to an existing Grace type.
    type LocalFileVersion with
        /// Gets the full path for this file in the working directory.
        member this.FullName = getNativeFilePath (Path.Combine(Current().RootDirectory, $"{this.RelativePath}"))

        /// Gets the full working directory path for this file.
        member this.FullRelativePath = FileInfo(this.FullName).DirectoryName
        /// Gets a FileInfo instance for this local file.
        member this.FileInfo = FileInfo(this.FullName)

        /// Gets the RelativeDirectory for this file.
        member this.RelativeDirectory = Path.GetRelativePath(Current().RootDirectory, this.FileInfo.DirectoryName)

        /// Gets the full name of the object file for this LocalFileVersion.
        member this.FullObjectPath =
            getNativeFilePath (
                Path.Combine(Current().ObjectDirectory, this.RelativePath, getLocalObjectCacheFileName this.RelativePath this.Sha256Hash this.Blake3Hash)
            )

    /// Reads local object cache path for file version from ParseResult, local configuration, or Grace ids.
    let getLocalObjectCachePathForFileVersion (fileVersion: FileVersion) =
        getNativeFilePath (
            Path.Combine(
                Current().ObjectDirectory,
                fileVersion.RelativePath,
                getLocalObjectCacheFileName fileVersion.RelativePath fileVersion.Sha256Hash fileVersion.Blake3Hash
            )
        )

    /// Flag to determine if we should do case-insensitive file name processing on the current platform.
    let ignoreCase = runningOnWindows

    /// Checks whether a file path matches the parsed `.graceignore` entry.
    let checkIgnoreLineAgainstFile (fileToCheck: FilePath) (graceIgnoreEntry: string) =
        let fileName = Path.GetFileName(fileToCheck)

        let ignoreEntryMatches = FileSystemName.MatchesSimpleExpression(graceIgnoreEntry, fileName, ignoreCase)

        ignoreEntryMatches

    /// Checks whether a directory path matches the parsed `.graceignore` entry.
    let checkIgnoreLineAgainstDirectory (directoryInfoToCheck: DirectoryInfo) (graceIgnoreEntry: string) =
        let normalizedDirectoryPath =
            if Path.EndsInDirectorySeparator(directoryInfoToCheck.FullName) then
                normalizeFilePath directoryInfoToCheck.FullName
            else
                normalizeFilePath (directoryInfoToCheck.FullName + "/")

        let normalizedDirectoryPathWithoutSeparator = Path.TrimEndingDirectorySeparator(normalizedDirectoryPath)

        if
            FileSystemName.MatchesSimpleExpression(graceIgnoreEntry, normalizedDirectoryPath, ignoreCase)
            || FileSystemName.MatchesSimpleExpression(graceIgnoreEntry, normalizedDirectoryPathWithoutSeparator, ignoreCase)
            || FileSystemName.MatchesSimpleExpression(graceIgnoreEntry, directoryInfoToCheck.Name, ignoreCase)
        then
            //logToAnsiConsole Colors.Changed $"checkIgnoreLineAgainstDirectory: directory '{normalizedDirectoryPath}' matches ignore entry '{graceIgnoreEntry}'."
            true
        else
            //logToAnsiConsole
            //    Colors.Verbose
            //    $"checkIgnoreLineAgainstDirectory: directory '{normalizedDirectoryPath}' does not match ignore entry '{graceIgnoreEntry}'."

            false

    /// Checks whether a repository file path should be skipped by Grace indexing.
    let shouldIgnoreFile (filePath: FilePath) =
        let mutable shouldIgnore = false
        let wasAlreadyCached = shouldIgnoreCache.TryGetValue(filePath, &shouldIgnore)
        //logToConsole $"In shouldIgnoreFile: filePath: {filePath}; wasAlreadyCached: {wasAlreadyCached}; shouldIgnore: {shouldIgnore}"
        if wasAlreadyCached then
            shouldIgnore
        else
            // Ignore it if:
            //   it's in the .grace directory, or
            //   it's the Grace Status file, or
            //   it's a Grace-owned temporary file, or
            //   it's a directory itself, or
            //   it matches something in graceignore.txt.
            let fileInfo = FileInfo(filePath)

            let shouldIgnoreThisFile =
                filePath.StartsWith(Current().GraceDirectory, StringComparison.InvariantCultureIgnoreCase) // it's in the /.grace directory
                || filePath.Equals(Current().GraceStatusFile, StringComparison.InvariantCultureIgnoreCase) // it's the Grace local state DB
                || filePath.Equals(Current().GraceStatusFile + "-wal", StringComparison.InvariantCultureIgnoreCase) // sqlite WAL
                || filePath.Equals(Current().GraceStatusFile + "-shm", StringComparison.InvariantCultureIgnoreCase) // sqlite SHM
                || filePath.Equals(Current().GraceStatusFile + "-journal", StringComparison.InvariantCultureIgnoreCase) // sqlite journal
                || filePath.EndsWith(".gracetmp") // it's a Grace temporary file
                || Directory.Exists(filePath) // it's a directory
                //|| fileInfo.Attributes.HasFlag(FileAttributes.Temporary)                                          // it's temporary - why doesn't this work
                || Current().GraceDirectoryIgnoreEntries // one of the directories in the path matches a directory ignore line
                   |> Array.exists (fun graceIgnoreLine -> checkIgnoreLineAgainstDirectory fileInfo.Directory graceIgnoreLine)
                || Current().GraceDirectoryIgnoreEntries // the file name matches a directory ignore line (which is weird, but possible)
                   |> Array.exists (fun graceIgnoreLine -> checkIgnoreLineAgainstFile filePath graceIgnoreLine)
                || Current().GraceFileIgnoreEntries // the file name matches a file ignore line
                   |> Array.exists (fun graceIgnoreLine -> checkIgnoreLineAgainstFile filePath graceIgnoreLine)


            //logToAnsiConsole Colors.Verbose $"In shouldIgnoreFile: filePath: {filePath}; shouldIgnore: {shouldIgnoreThisFile}"
            shouldIgnoreCache.TryAdd(filePath, shouldIgnoreThisFile)
            |> ignore

            shouldIgnoreThisFile

    let private notString = "not "

    /// Checks whether a repository directory should be skipped by Grace indexing.
    let shouldIgnoreDirectory (directoryPath: string) =
        let mutable shouldIgnore = false
        let wasAlreadyCached = shouldIgnoreCache.TryGetValue(directoryPath, &shouldIgnore)

        if wasAlreadyCached then
            shouldIgnore
        else
            let directoryInfo = DirectoryInfo(directoryPath)

            let shouldIgnoreDirectory =
                directoryInfo.FullName.StartsWith(Current().GraceDirectory)
                || Current()
                    .GraceDirectoryIgnoreEntries.Any(fun graceIgnoreLine -> checkIgnoreLineAgainstDirectory directoryInfo graceIgnoreLine)

            shouldIgnoreCache.TryAdd(directoryPath, shouldIgnoreDirectory)
            |> ignore
            //logToAnsiConsole Colors.Verbose $"In shouldIgnoreDirectory: directoryPath: {directoryPath}; shouldIgnore: {shouldIgnoreDirectory}"
            shouldIgnoreDirectory

    /// Checks whether a repository directory remains eligible for Grace indexing.
    let shouldNotIgnoreDirectory (directoryPath: string) = not <| shouldIgnoreDirectory directoryPath

    /// Maps a FileInfo entry into the local file-version shape used by repository scans.
    let createLocalFileVersion (fileInfo: FileInfo) =
        task {
            if fileInfo.Exists then
                try
                    let relativePath = Path.GetRelativePath(Current().RootDirectory, fileInfo.FullName)

                    use stream = fileInfo.Open(fileStreamOptionsRead)

                    let! isBinary = isBinaryFile stream

                    stream.Position <- 0
                    let! sha256Hash = computeSha256ForFile stream relativePath

                    stream.Position <- 0
                    let! fileContentHash = computeBlake3ForFile stream
                    let blake3Hash = Blake3Hash $"{fileContentHash}"

                    let returnValue =
                        LocalFileVersion.CreateWithHashes
                            relativePath
                            sha256Hash
                            blake3Hash
                            isBinary
                            fileInfo.Length
                            (Instant.FromDateTimeUtc(fileInfo.LastWriteTimeUtc))
                            true
                            fileInfo.LastWriteTimeUtc

                    return Some returnValue
                with
                | ex ->
                    logToAnsiConsole Colors.Error $"Exception in createLocalFileVersion for file {fileInfo.FullName}:"
                    logToAnsiConsole Colors.Error $"{ExceptionResponse.Create ex}"
                    return None
            else
                return None
        }

    /// Models working tree scan input values passed between the parser and services handlers.
    type WorkingTreeScanInput =
        {
            RootDirectory: string
            GraceDirectory: string
            GraceStatusFile: string
            DirectoryIgnoreEntries: string array
            FileIgnoreEntries: string array
        }

    /// Normalizes Grace ids for full path by keeping explicit scope values and clearing implicit child scopes.
    let private normalizeFullPath path =
        Path
            .GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

    /// Evaluates is within grace directory against parsed options and command state.
    let private isWithinGraceDirectory (scanInput: WorkingTreeScanInput) path =
        let graceDirectory = normalizeFullPath scanInput.GraceDirectory
        let candidate = Path.GetFullPath(path)

        candidate.Equals(graceDirectory, StringComparison.OrdinalIgnoreCase)
        || candidate.StartsWith(
            graceDirectory
            + string Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase
        )
        || candidate.StartsWith(
            graceDirectory
            + string Path.AltDirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase
        )

    /// Coordinates should ignore file for scan behavior for this CLI command path.
    let private shouldIgnoreFileForScan (scanInput: WorkingTreeScanInput) (cache: ConcurrentDictionary<FilePath, bool>) (filePath: FilePath) =
        let mutable shouldIgnore = false

        if cache.TryGetValue(filePath, &shouldIgnore) then
            shouldIgnore
        else
            let fileInfo = FileInfo(filePath)

            let shouldIgnoreThisFile =
                isWithinGraceDirectory scanInput filePath
                || filePath.Equals(scanInput.GraceStatusFile, StringComparison.InvariantCultureIgnoreCase)
                || filePath.Equals(scanInput.GraceStatusFile + "-wal", StringComparison.InvariantCultureIgnoreCase)
                || filePath.Equals(scanInput.GraceStatusFile + "-shm", StringComparison.InvariantCultureIgnoreCase)
                || filePath.Equals(scanInput.GraceStatusFile + "-journal", StringComparison.InvariantCultureIgnoreCase)
                || filePath.EndsWith(".gracetmp", StringComparison.OrdinalIgnoreCase)
                || Directory.Exists(filePath)
                || scanInput.DirectoryIgnoreEntries
                   |> Array.exists (fun graceIgnoreLine -> checkIgnoreLineAgainstDirectory fileInfo.Directory graceIgnoreLine)
                || scanInput.DirectoryIgnoreEntries
                   |> Array.exists (fun graceIgnoreLine -> checkIgnoreLineAgainstFile filePath graceIgnoreLine)
                || scanInput.FileIgnoreEntries
                   |> Array.exists (fun graceIgnoreLine -> checkIgnoreLineAgainstFile filePath graceIgnoreLine)

            cache.TryAdd(filePath, shouldIgnoreThisFile)
            |> ignore

            shouldIgnoreThisFile

    /// Coordinates should ignore directory for scan behavior for this CLI command path.
    let private shouldIgnoreDirectoryForScan (scanInput: WorkingTreeScanInput) (cache: ConcurrentDictionary<FilePath, bool>) (directoryPath: string) =
        let mutable shouldIgnore = false

        if cache.TryGetValue(directoryPath, &shouldIgnore) then
            shouldIgnore
        else
            let directoryInfo = DirectoryInfo(directoryPath)

            let shouldIgnoreDirectory =
                isWithinGraceDirectory scanInput directoryInfo.FullName
                || scanInput.DirectoryIgnoreEntries.Any(fun graceIgnoreLine -> checkIgnoreLineAgainstDirectory directoryInfo graceIgnoreLine)

            cache.TryAdd(directoryPath, shouldIgnoreDirectory)
            |> ignore

            shouldIgnoreDirectory

    /// Converts a scanned file into the local file-version record stored in the index.
    let private createLocalFileVersionForScan (scanInput: WorkingTreeScanInput) (fileInfo: FileInfo) =
        task {
            if fileInfo.Exists then
                use stream = fileInfo.Open(fileStreamOptionsRead)

                let! isBinary = isBinaryFile stream

                stream.Position <- 0
                let relativePath = normalizeFilePath (Path.GetRelativePath(scanInput.RootDirectory, fileInfo.FullName))
                let! sha256Hash = computeSha256ForFile stream relativePath

                stream.Position <- 0
                let! fileContentHash = computeBlake3ForFile stream
                let blake3Hash = Blake3Hash $"{fileContentHash}"

                return
                    Some(
                        LocalFileVersion.CreateWithHashes
                            relativePath
                            sha256Hash
                            blake3Hash
                            isBinary
                            fileInfo.Length
                            (Instant.FromDateTimeUtc(fileInfo.LastWriteTimeUtc))
                            true
                            fileInfo.LastWriteTimeUtc
                    )
            else
                return None
        }

    /// Reads working directory write times for scan from ParseResult, local configuration, or Grace ids.
    let private getWorkingDirectoryWriteTimesForScan (scanInput: WorkingTreeScanInput) =
        let cache = ConcurrentDictionary<FilePath, bool>()
        let localWriteTimes = Dictionary<FileSystemEntryType * RelativePath, DateTime>()

        /// Coordinates rec behavior for this CLI command path.
        let rec collect (directoryInfo: DirectoryInfo) =
            if not (shouldIgnoreDirectoryForScan scanInput cache directoryInfo.FullName) then
                let directoryFullPath = RelativePath(normalizeFilePath (Path.GetRelativePath(scanInput.RootDirectory, directoryInfo.FullName)))

                localWriteTimes[(FileSystemEntryType.Directory, directoryFullPath)] <- directoryInfo.LastWriteTimeUtc

                directoryInfo
                    .GetFiles()
                    .Where(fun file -> not (shouldIgnoreFileForScan scanInput cache file.FullName))
                |> Seq.iter (fun fileInfo ->
                    let fileFullPath = RelativePath(normalizeFilePath (Path.GetRelativePath(scanInput.RootDirectory, fileInfo.FullName)))
                    localWriteTimes[(FileSystemEntryType.File, fileFullPath)] <- fileInfo.LastWriteTimeUtc)

                directoryInfo
                    .GetDirectories()
                    .Where(fun directory -> not (shouldIgnoreDirectoryForScan scanInput cache directory.FullName))
                |> Seq.iter collect

        collect (DirectoryInfo(scanInput.RootDirectory))
        localWriteTimes

    /// Evaluates has file content changed against parsed options and command state.
    let private hasFileContentChanged existingSha256Hash existingBlake3Hash (newFileVersion: LocalFileVersion) =
        newFileVersion.Sha256Hash <> existingSha256Hash
        || (existingBlake3Hash <> Blake3Hash String.Empty
            && newFileVersion.Blake3Hash <> existingBlake3Hash)

    /// Coordinates scan working tree for differences read only behavior for this CLI command path.
    let scanWorkingTreeForDifferencesReadOnly (scanInput: WorkingTreeScanInput) (previousGraceStatus: GraceStatus) =
        task {
            try
                let lookupCache = Dictionary<FileSystemEntryType * RelativePath, DateTime * Sha256Hash * Blake3Hash>()

                for kvp in previousGraceStatus.Index do
                    let directoryVersion = kvp.Value

                    lookupCache.TryAdd(
                        (FileSystemEntryType.Directory, directoryVersion.RelativePath),
                        (directoryVersion.LastWriteTimeUtc, directoryVersion.Sha256Hash, Blake3Hash String.Empty)
                    )
                    |> ignore

                    for file in directoryVersion.Files do
                        lookupCache.TryAdd((FileSystemEntryType.File, file.RelativePath), (file.LastWriteTimeUtc, file.Sha256Hash, file.Blake3Hash))
                        |> ignore

                let localWriteTimes = getWorkingDirectoryWriteTimesForScan scanInput
                let differences = List<FileSystemDifference>()

                for kvp in localWriteTimes do
                    let fileSystemEntryType, relativePath = kvp.Key
                    let lastWriteTimeUtc = kvp.Value

                    if not (lookupCache.ContainsKey((fileSystemEntryType, relativePath))) then
                        differences.Add(FileSystemDifference.Create Add fileSystemEntryType relativePath)
                    elif fileSystemEntryType.IsFile then
                        let knownLastWriteTimeUtc, existingSha256Hash, existingBlake3Hash = lookupCache[(fileSystemEntryType, relativePath)]

                        if lastWriteTimeUtc <> knownLastWriteTimeUtc then
                            let fileInfo = FileInfo(Path.Combine(scanInput.RootDirectory, relativePath))
                            let! newFileVersion = createLocalFileVersionForScan scanInput fileInfo

                            match newFileVersion with
                            | Some fileVersion when hasFileContentChanged existingSha256Hash existingBlake3Hash fileVersion ->
                                differences.Add(FileSystemDifference.Create Change fileSystemEntryType relativePath)
                            | _ -> ()

                for kvp in lookupCache do
                    let fileSystemEntryType, relativePath = kvp.Key

                    if not (localWriteTimes.ContainsKey((fileSystemEntryType, relativePath))) then
                        differences.Add(FileSystemDifference.Create Delete fileSystemEntryType relativePath)

                return Ok differences
            with
            | ex -> return Error ex.Message
        }

    /// Gets the LocalDirectoryVersion for the root directory of the repository from GraceStatus.
    let getRootDirectoryVersion (graceStatus: GraceStatus) =
        graceStatus.Index.Values.FirstOrDefault(
            (fun localDirectoryVersion -> localDirectoryVersion.RelativePath = Constants.RootDirectoryPath),
            LocalDirectoryVersion.Default
        )

    /// Checks whether sync grace status root directory hash is true for the parsed command input.
    let syncGraceStatusRootDirectoryHash (graceStatus: GraceStatus) =
        let rootDirectoryVersion = getRootDirectoryVersion graceStatus

        if rootDirectoryVersion.DirectoryVersionId = DirectoryVersionId.Empty then
            graceStatus
        else
            { graceStatus with
                RootDirectoryId = rootDirectoryVersion.DirectoryVersionId
                RootDirectorySha256Hash = rootDirectoryVersion.Sha256Hash
                RootDirectoryBlake3Hash = rootDirectoryVersion.Blake3Hash
            }

    let mutable private lastScanForDifferencesSucceeded = true

    /// Coordinates was last scan for differences successful behavior for this CLI command path.
    let internal wasLastScanForDifferencesSuccessful () = lastScanForDifferencesSucceeded

    /// Coordinates set last scan for differences successful for watch tests behavior for this CLI command path.
    let internal setLastScanForDifferencesSuccessfulForWatchTests succeeded = lastScanForDifferencesSucceeded <- succeeded

    /// Preserves the old test reset hook after working-tree scans moved to fresh per-scan write-time maps.
    let internal clearWorkingDirectoryWriteTimesForWatchRescan () = ()

    /// Gets a dictionary of local paths and their last write times using an explicit working-tree root.
    let getWorkingDirectoryWriteTimesAtRoot rootDirectory (directoryInfo: DirectoryInfo) =
        let localWriteTimes = ConcurrentDictionary<FileSystemEntryType * RelativePath, DateTime>()
        let rootedWorkingDirectory = Path.GetFullPath(rootDirectory)

        /// Adds non-ignored working-tree entries to the current scan's write-time map.
        let rec addWorkingDirectoryWriteTimes (directoryInfo: DirectoryInfo) =
            if shouldNotIgnoreDirectory directoryInfo.FullName then
                // Add the current directory to the lookup dictionary
                let directoryFullPath = RelativePath(normalizeFilePath (Path.GetRelativePath(rootedWorkingDirectory, directoryInfo.FullName)))

                localWriteTimes.AddOrUpdate(
                    (FileSystemEntryType.Directory, directoryFullPath),
                    (fun _ -> directoryInfo.LastWriteTimeUtc),
                    (fun _ _ -> directoryInfo.LastWriteTimeUtc)
                )
                |> ignore

                // Add each file to the lookup dictionary
                for f in
                    directoryInfo
                        .GetFiles()
                        .Where(fun f -> not <| shouldIgnoreFile f.FullName) do
                    let fileFullPath = RelativePath(normalizeFilePath (Path.GetRelativePath(rootedWorkingDirectory, f.FullName)))

                    localWriteTimes.AddOrUpdate((FileSystemEntryType.File, fileFullPath), (fun _ -> f.LastWriteTimeUtc), (fun _ _ -> f.LastWriteTimeUtc))
                    |> ignore

                // Call recursively for each subdirectory
                let parallelLoopResult = Parallel.ForEach(directoryInfo.GetDirectories(), Constants.ParallelOptions, (fun d -> addWorkingDirectoryWriteTimes d))

                if parallelLoopResult.IsCompleted then
                    ()
                else
                    printfn $"Failed while gathering local write times."

        addWorkingDirectoryWriteTimes directoryInfo
        localWriteTimes

    /// Gets a dictionary of local paths and their last write times.
    let getWorkingDirectoryWriteTimes (directoryInfo: DirectoryInfo) = getWorkingDirectoryWriteTimesAtRoot (Current().RootDirectory) directoryInfo

    /// Reads local state db path from ParseResult, local configuration, or Grace ids.
    let private getLocalStateDbPath () = Current().GraceStatusFile

    /// Reads only GraceStatus meta fields (no index).
    let readGraceStatusMeta () =
        task {
            let! meta = LocalStateDb.readStatusMeta (getLocalStateDbPath ())

            return
                { GraceStatus.Default with
                    RootDirectoryId = meta.RootDirectoryId
                    RootDirectorySha256Hash = meta.RootDirectorySha256Hash
                    RootDirectoryBlake3Hash = meta.RootDirectoryBlake3Hash
                    LastSuccessfulFileUpload = meta.LastSuccessfulFileUpload
                    LastSuccessfulDirectoryVersionUpload = meta.LastSuccessfulDirectoryVersionUpload
                }
        }

    /// Reads the full GraceStatus snapshot including the index.
    let readGraceStatusSnapshot () = LocalStateDb.readStatusSnapshot (getLocalStateDbPath ())

    /// Retrieves the Grace status snapshot.
    let readGraceStatusFile () = readGraceStatusSnapshot ()

    /// Writes the full Grace status snapshot to disk.
    let writeGraceStatusFile (graceStatus: GraceStatus) = LocalStateDb.replaceStatusSnapshot (getLocalStateDbPath ()) graceStatus

    /// Applies incremental Grace status updates to the local DB.
    let applyGraceStatusIncremental
        (graceStatus: GraceStatus)
        (newDirectoryVersions: IEnumerable<LocalDirectoryVersion>)
        (differences: IEnumerable<FileSystemDifference>)
        =
        LocalStateDb.applyStatusIncremental (getLocalStateDbPath ()) graceStatus newDirectoryVersions differences

    /// Upserts new directory versions into the object cache tables.
    let upsertObjectCache (newDirectoryVersions: IEnumerable<LocalDirectoryVersion>) =
        LocalStateDb.upsertObjectCache (getLocalStateDbPath ()) newDirectoryVersions

    /// Compares a captured working-tree root against the Grace index file and returns the differences.
    let scanForDifferencesAtRoot rootDirectory (previousGraceStatus: GraceStatus) =
        task {
            try
                let rootedWorkingDirectory = Path.GetFullPath(rootDirectory)
                let lookupCache = Dictionary<FileSystemEntryType * RelativePath, (DateTime * Sha256Hash * Blake3Hash)>()

                let differences = ConcurrentStack<FileSystemDifference>()

                let mutable fileCount = 0
                // Create an indexed lookup table of path -> lastWriteTimeUtc from the Grace Status index.
                for kvp in previousGraceStatus.Index do
                    let directoryVersion = kvp.Value

                    lookupCache.TryAdd(
                        (FileSystemEntryType.Directory, directoryVersion.RelativePath),
                        (directoryVersion.LastWriteTimeUtc, directoryVersion.Sha256Hash, Blake3Hash String.Empty)
                    )
                    |> ignore

                    for file in directoryVersion.Files do
                        fileCount <- fileCount + 1

                        lookupCache.TryAdd((FileSystemEntryType.File, file.RelativePath), (file.LastWriteTimeUtc, file.Sha256Hash, file.Blake3Hash))
                        |> ignore

                if parseResult |> isOutputFormat "Verbose" then
                    logToAnsiConsole
                        Colors.Verbose
                        $"scanForDifferences: previousGraceStatus contains {previousGraceStatus.Index.Count} DirectoryVersion entries, and {fileCount} files."

                // Get an indexed lookup dictionary of path -> lastWriteTimeUtc from the working directory.
                let localWriteTimes = getWorkingDirectoryWriteTimesAtRoot rootedWorkingDirectory (DirectoryInfo(rootedWorkingDirectory))

                // Loop through the working directory list and compare it to the Grace Status index.
                for kvp in localWriteTimes do
                    let ((fileSystemEntryType, relativePath), lastWriteTimeUtc) = kvp.Deconstruct()
                    // Check for additions
                    if
                        not
                        <| lookupCache.ContainsKey((fileSystemEntryType, relativePath))
                    then
                        // This is new file or directory.
                        differences.Push(FileSystemDifference.Create Add fileSystemEntryType relativePath)

                    // Check for changes
                    if lookupCache.ContainsKey((fileSystemEntryType, relativePath)) then
                        let (knownLastWriteTimeUtc, existingSha256Hash, existingBlake3Hash) = lookupCache[(fileSystemEntryType, relativePath)]
                        // Has the LastWriteTimeUtc changed from the one in GraceStatus?
                        if fileSystemEntryType.IsFile
                           && lastWriteTimeUtc <> knownLastWriteTimeUtc then
                            // If it's a directory, ignore it. I don't care when the local directory was created vs. the one stored in GraceIndex.
                            // If this is a file, then check that the contents have actually changed.
                            let fileInfo = FileInfo(Path.Combine(rootedWorkingDirectory, relativePath))

                            match! createLocalFileVersion fileInfo with
                            | Some newFileVersion ->
                                if hasFileContentChanged existingSha256Hash existingBlake3Hash newFileVersion then
                                    differences.Push(FileSystemDifference.Create Change fileSystemEntryType relativePath)

                                    if parseResult |> isOutputFormat "Verbose" then
                                        logToAnsiConsole
                                            Colors.Verbose
                                            $"scanForDifferences: Found change in file: {relativePath}; existing Sha256Hash: {getShortSha256Hash existingSha256Hash}; new Sha256Hash: {getShortSha256Hash newFileVersion.Sha256Hash}; existing Blake3Hash: {existingBlake3Hash}; new Blake3Hash: {newFileVersion.Blake3Hash}."
                            | None -> ()

                // Check for deletions
                for keyValuePair in lookupCache do
                    let (fileSystemEntryType, relativePath) = keyValuePair.Key
                    let (knownLastWriteTimeUtc, existingSha256Hash, existingBlake3Hash) = keyValuePair.Value

                    if
                        not
                        <| localWriteTimes.ContainsKey((fileSystemEntryType, relativePath))
                    then
                        if parseResult |> isOutputFormat "Verbose" then
                            logToAnsiConsole Colors.Verbose $"scanForDifferences: Deletion found: {relativePath}."

                        differences.Push(FileSystemDifference.Create Delete fileSystemEntryType relativePath)

                lastScanForDifferencesSucceeded <- true
                return differences.ToList()
            with
            | ex ->
                lastScanForDifferencesSucceeded <- false
                logToAnsiConsole Colors.Error $"{ExceptionResponse.Create ex}"
                return List<FileSystemDifference>()
        }

    /// Compared the repository's working directory against the Grace index file and returns the differences.
    let scanForDifferences (previousGraceStatus: GraceStatus) =
        task {
            try
                return! scanForDifferencesAtRoot (Current().RootDirectory) previousGraceStatus
            with
            | ex ->
                lastScanForDifferencesSucceeded <- false
                logToAnsiConsole Colors.Error $"{ExceptionResponse.Create ex}"
                return List<FileSystemDifference>()
        }

    //let processedThings = ConcurrentQueue<string>()
    let mutable newDirectoryVersionCount = 0
    let mutable existingDirectoryVersionCount = 0

    /// Gathers all of the LocalDirectoryVersions and LocalFileVersions for the requested directory and its subdirectories, and returns them along with the Sha256Hash of the requested directory.
    let rec collectDirectoriesAndFiles
        (relativeDirectoryPath: RelativePath)
        (previousDirectoryVersions: Dictionary<RelativePath, LocalDirectoryVersion>)
        (newGraceStatus: GraceStatus)
        (parseResult: ParseResult)
        =

        /// Gets a list of subdirectories and files from a specific directory
        let getDirectoryContents (previousDirectoryVersions: Dictionary<RelativePath, LocalDirectoryVersion>) (directoryInfo: DirectoryInfo) =
            task {
                try
                    let files = ConcurrentQueue<LocalFileVersion>()
                    let directories = ConcurrentQueue<LocalDirectoryVersion>()

                    // Create LocalFileVersion instances for each file in this directory.
                    do!
                        Parallel.ForEachAsync(
                            directoryInfo
                                .GetFiles()
                                .Where(fun f -> not <| shouldIgnoreFile f.FullName),
                            Constants.ParallelOptions,
                            (fun fileInfo continuationToken ->
                                ValueTask(
                                    task {
                                        try
                                            match! createLocalFileVersion fileInfo with
                                            | Some fileVersion -> files.Enqueue(fileVersion)
                                            | None -> ()
                                        with
                                        | ex ->
                                            logToAnsiConsole Colors.Error $"Exception in getDirectoryContents (Parallel.ForEachAsync):"
                                            logToAnsiConsole Colors.Error $"{ExceptionResponse.Create ex}"
                                    }
                                ))
                        )

                    // Create or reuse existing LocalDirectoryVersion instances for each subdirectory in this directory.
                    let defaultDirectoryVersion = KeyValuePair(String.Empty, LocalDirectoryVersion.Default)

                    do!
                        Parallel.ForEachAsync(
                            directoryInfo
                                .GetDirectories()
                                .Where(fun d -> shouldNotIgnoreDirectory d.FullName),
                            Constants.ParallelOptions,
                            (fun subdirectoryInfo continuationToken ->
                                ValueTask(
                                    task {
                                        try

                                            let subdirectoryRelativePath = Path.GetRelativePath(Current().RootDirectory, subdirectoryInfo.FullName)


                                            let! (subdirectoryVersions: List<LocalDirectoryVersion>,
                                                  filesInSubdirectory: List<LocalFileVersion>,
                                                  sha256Hash,
                                                  blake3Hash) =
                                                collectDirectoriesAndFiles subdirectoryRelativePath previousDirectoryVersions newGraceStatus parseResult

                                            // Check if we already have a LocalDirectoryVersion for this subdirectory.
                                            let existingSubdirectoryVersion =
                                                previousDirectoryVersions
                                                    .FirstOrDefault(
                                                        (fun existingDirectoryVersion ->
                                                            existingDirectoryVersion.Key = normalizeFilePath subdirectoryRelativePath),
                                                        defaultValue = defaultDirectoryVersion
                                                    )
                                                    .Value

                                            // Check if we already have this exact SHA-256 hash for this relative path; if so, keep the existing SubdirectoryVersion and its Guid.
                                            // If the DirectoryId is Guid.Empty (from LocalDirectoryVersion.Default), or the Sha256Hash doesn't match, create a new LocalDirectoryVersion reflecting the changes.
                                            if existingSubdirectoryVersion.DirectoryVersionId = Guid.Empty
                                               || existingSubdirectoryVersion.Sha256Hash
                                                  <> sha256Hash
                                               || existingSubdirectoryVersion.Blake3Hash
                                                  <> blake3Hash then
                                                let directoryIds =
                                                    subdirectoryVersions
                                                        .OrderBy(fun d -> d.RelativePath)
                                                        .Select(fun d -> d.DirectoryVersionId)
                                                        .ToList()

                                                let subdirectoryVersion =
                                                    LocalDirectoryVersion.CreateWithHashes
                                                        (Guid.NewGuid())
                                                        (Current().OwnerId)
                                                        (Current().OrganizationId)
                                                        (Current().RepositoryId)
                                                        (RelativePath(normalizeFilePath subdirectoryRelativePath))
                                                        sha256Hash
                                                        blake3Hash
                                                        directoryIds
                                                        filesInSubdirectory
                                                        (getLocalDirectorySize filesInSubdirectory)
                                                        subdirectoryInfo.LastWriteTimeUtc
                                                //processedThings.Enqueue($"New      {subdirectoryVersion.RelativePath}")
                                                newGraceStatus.Index.TryAdd(subdirectoryVersion.DirectoryVersionId, subdirectoryVersion)
                                                |> ignore

                                                Interlocked.Increment(&newDirectoryVersionCount)
                                                |> ignore

                                                directories.Enqueue(subdirectoryVersion)
                                            else
                                                //processedThings.Enqueue($"Existing {existingSubdirectoryVersion.RelativePath}")
                                                newGraceStatus.Index.TryAdd(existingSubdirectoryVersion.DirectoryVersionId, existingSubdirectoryVersion)
                                                |> ignore

                                                Interlocked.Increment(&existingDirectoryVersionCount)
                                                |> ignore

                                                directories.Enqueue(existingSubdirectoryVersion)
                                        with
                                        | ex -> logToAnsiConsole Colors.Error $"Exception in getDirectoryContents: {ExceptionResponse.Create ex}"
                                    }
                                ))
                        )

                    return
                        (directories
                            .OrderBy(fun d -> d.RelativePath)
                             .ToList(),
                         files.OrderBy(fun d -> d.RelativePath).ToList())
                with
                | ex ->
                    logToAnsiConsole Colors.Error $"Exception in getDirectoryContents:"
                    logToAnsiConsole Colors.Error $"{ExceptionResponse.Create ex}"
                    return (List<LocalDirectoryVersion>(), List<LocalFileVersion>())
            }

        task {
            try
                let directoryInfo = DirectoryInfo(Path.Combine(Current().RootDirectory, relativeDirectoryPath))

                let! (directories, files) = getDirectoryContents previousDirectoryVersions directoryInfo
                //for file in files do processedThings.Enqueue(file.RelativePath)

                let sha256Hash, blake3Hash = computeDirectoryVersionHashes relativeDirectoryPath directories files

                return (directories, files, sha256Hash, blake3Hash)
            with
            | ex ->
                logToAnsiConsole Colors.Error $"Exception in collectDirectoriesAndFiles: {ExceptionResponse.Create ex}"
                return (List<LocalDirectoryVersion>(), List<LocalFileVersion>(), Sha256Hash.Empty, Blake3Hash String.Empty)
        }

    /// Scans the repository working directory and writes the Grace index file.
    let createNewGraceStatusFile (previousGraceStatus: GraceStatus) (parseResult: ParseResult) =
        task {
            try
                // Start with a new GraceStatus instance.
                let newGraceStatus = GraceStatus.Default
                let rootDirectoryInfo = DirectoryInfo(Current().RootDirectory)

                // Get the previous GraceStatus index values into a Dictionary for faster lookup.
                let previousDirectoryVersions = Dictionary<RelativePath, LocalDirectoryVersion>()

                for kvp in previousGraceStatus.Index do
                    if
                        not
                        <| previousDirectoryVersions.TryAdd(kvp.Value.RelativePath, kvp.Value)
                    then
                        logToAnsiConsole Colors.Error $"createNewGraceStatusFile: Failed to add {kvp.Value.RelativePath} to previousDirectoryVersions."

                let! (subdirectoriesInRootDirectory, filesInRootDirectory, rootSha256Hash, rootBlake3Hash) =
                    collectDirectoriesAndFiles Constants.RootDirectoryPath previousDirectoryVersions newGraceStatus parseResult

                //let getBySha256HashParameters = GetBySha256HashParameters(RepositoryId = $"{Current().RepositoryId}", Sha256Hash = rootSha256Hash)
                //let! directoryId = Directory.GetBySha256Hash(getBySha256HashParameters)

                // Check for existing root directory version so we don't update the Guid if it already exists.
                let rootDirectoryVersion =
                    let previousRootDirectoryVersion =
                        previousDirectoryVersions
                            .FirstOrDefault(
                                (fun existingDirectoryVersion -> existingDirectoryVersion.Key = Constants.RootDirectoryPath),
                                defaultValue = KeyValuePair(String.Empty, LocalDirectoryVersion.Default)
                            )
                            .Value

                    if previousRootDirectoryVersion.Sha256Hash = rootSha256Hash
                       && previousRootDirectoryVersion.Blake3Hash = rootBlake3Hash then
                        previousRootDirectoryVersion
                    else
                        let subdirectoryIds =
                            subdirectoriesInRootDirectory
                                .OrderBy(fun d -> d.RelativePath)
                                .Select(fun d -> d.DirectoryVersionId)
                                .ToList()

                        LocalDirectoryVersion.CreateWithHashes
                            (Guid.NewGuid())
                            (Current().OwnerId)
                            (Current().OrganizationId)
                            (Current().RepositoryId)
                            (RelativePath(normalizeFilePath Constants.RootDirectoryPath))
                            (Sha256Hash rootSha256Hash)
                            rootBlake3Hash
                            subdirectoryIds
                            filesInRootDirectory
                            (getLocalDirectorySize filesInRootDirectory)
                            rootDirectoryInfo.LastWriteTimeUtc

                newGraceStatus.Index.TryAdd(Guid.Parse($"{rootDirectoryVersion.DirectoryVersionId}"), rootDirectoryVersion)
                |> ignore

                //let sb = StringBuilder(processedThings.Count)
                //for dir in processedThings.OrderBy(fun d -> d) do
                //    sb.AppendLine(dir) |> ignore
                //do! File.WriteAllTextAsync(@$"C:\Intel\ProcessedThings{sb.Length}.txt", sb.ToString())
                let rootDirectoryVersion = getRootDirectoryVersion newGraceStatus

                if parseResult |> isOutputFormat "Verbose" then
                    logToAnsiConsole Colors.Verbose $"Finished createNewGraceStatusFile. newGraceStatus.Index.Count: {newGraceStatus.Index.Count}."

                let newGraceStatus =
                    { newGraceStatus with
                        RootDirectoryId = rootDirectoryVersion.DirectoryVersionId
                        RootDirectorySha256Hash = rootDirectoryVersion.Sha256Hash
                        RootDirectoryBlake3Hash = rootDirectoryVersion.Blake3Hash
                    }

                return newGraceStatus
            with
            | ex ->
                logToAnsiConsole Colors.Error $"Exception in createNewGraceStatusFile: {ExceptionResponse.Create ex}"
                return GraceStatus.Default
        }

    /// Adds a LocalDirectoryVersion to the local object cache.
    let addDirectoryToObjectCache (localDirectoryVersion: LocalDirectoryVersion) =
        task {
            let! exists = LocalStateDb.isDirectoryVersionInObjectCache (getLocalStateDbPath ()) localDirectoryVersion.DirectoryVersionId

            if not exists then
                let allFilesExist =
                    localDirectoryVersion.Files
                    |> Seq.forall (fun file -> File.Exists(file.FullObjectPath))

                if allFilesExist then
                    do! upsertObjectCache [ localDirectoryVersion ]
                    return Ok()
                else
                    return Error "Directory could not be added to object cache. All files do not exist in /objects directory."
            else
                return Ok()
        }

    /// Removes a directory from the local object cache.
    let removeDirectoryFromObjectCache (directoryId: DirectoryVersionId) =
        task { do! LocalStateDb.removeObjectCacheDirectory (getLocalStateDbPath ()) directoryId }

    /// Defines structured data exchanged by CLI helpers.
    type internal ObjectStorageDownloadFile = { LocalFileVersion: LocalFileVersion; SourceFileVersion: FileVersion option }

    /// Reads object storage download file from local data needed by the command workflow without changing remote state.
    let internal objectStorageDownloadFileFromLocal localFileVersion = { LocalFileVersion = localFileVersion; SourceFileVersion = None }

    /// Reads object storage download file from file version data needed by the command workflow without changing remote state.
    let internal objectStorageDownloadFileFromFileVersion (fileVersion: FileVersion) =
        { LocalFileVersion = fileVersion.ToLocalFileVersion DateTime.UtcNow; SourceFileVersion = Some fileVersion }

    /// Reads file version for object storage download data needed by the command workflow without changing remote state.
    let internal fileVersionForObjectStorageDownload downloadFile =
        match downloadFile.SourceFileVersion with
        | Some fileVersion -> fileVersion
        | None -> downloadFile.LocalFileVersion.ToFileVersion

    /// Reads download files from object storage with clients data needed by the command workflow without changing remote state.
    let internal downloadFilesFromObjectStorageWithClients
        (manifestDownload: ManifestDownload.ManifestDownloadRequest -> Task<GraceResult<ManifestDownload.ManifestDownloadResult>>)
        (wholeFileDownload: GetDownloadUriParameters -> string -> Task<GraceResult<string>>)
        (getDownloadUriParameters: GetDownloadUriParameters)
        (files: IEnumerable<ObjectStorageDownloadFile>)
        (correlationId: string)
        =
        task {
            match Current().ObjectStorageProvider with
            | ObjectStorageProvider.Unknown -> return Ok()
            | AzureBlobStorage ->
                let results =
                    files.ToArray()
                    |> Array.where (fun f ->
                        not
                        <| File.Exists(f.LocalFileVersion.FullObjectPath))
                    |> Array.Parallel.map (fun downloadFile ->
                        (task {
                            let localFileVersion = downloadFile.LocalFileVersion
                            let parameters = GetDownloadUriParameters()
                            parameters.OwnerId <- getDownloadUriParameters.OwnerId
                            parameters.OwnerName <- getDownloadUriParameters.OwnerName
                            parameters.OrganizationId <- getDownloadUriParameters.OrganizationId
                            parameters.OrganizationName <- getDownloadUriParameters.OrganizationName
                            parameters.RepositoryId <- getDownloadUriParameters.RepositoryId
                            parameters.RepositoryName <- getDownloadUriParameters.RepositoryName
                            let fileVersion = fileVersionForObjectStorageDownload downloadFile
                            parameters.FileVersion <- fileVersion
                            parameters.CorrelationId <- getDownloadUriParameters.CorrelationId

                            if fileVersion.ContentReference.ReferenceType = FileContentReferenceType.FileManifest then
                                let objectFilePath = localFileVersion.FullObjectPath

                                /// Coordinates delete partial manifest cache file behavior for this CLI command path.
                                let deletePartialManifestCacheFile () =
                                    try
                                        if File.Exists objectFilePath then File.Delete objectFilePath
                                    with
                                    | ex ->
                                        logToAnsiConsole
                                            Colors.Verbose
                                            $"Failed to delete partial manifest cache file {objectFilePath}: {ExceptionResponse.Create ex}"

                                try
                                    let objectFileInfo = FileInfo(objectFilePath)

                                    Directory.CreateDirectory(objectFileInfo.Directory.FullName)
                                    |> ignore

                                    use outputStream = File.Open(objectFileInfo.FullName, fileStreamOptionsWrite)

                                    let manifestRequest: ManifestDownload.ManifestDownloadRequest =
                                        {
                                            OwnerId = getDownloadUriParameters.OwnerId
                                            OwnerName = getDownloadUriParameters.OwnerName
                                            OrganizationId = getDownloadUriParameters.OrganizationId
                                            OrganizationName = getDownloadUriParameters.OrganizationName
                                            RepositoryId = getDownloadUriParameters.RepositoryId
                                            RepositoryName = getDownloadUriParameters.RepositoryName
                                            FileVersion = fileVersion
                                            OutputStream = Some outputStream
                                            CorrelationId = getDownloadUriParameters.CorrelationId
                                            ExpectedChunkingSuiteId = ChunkingSuiteId RabinChunking.SuiteName
                                        }

                                    match! manifestDownload manifestRequest with
                                    | Error error ->
                                        outputStream.Dispose()
                                        deletePartialManifestCacheFile ()
                                        return Error error
                                    | Ok returnValue when returnValue.ReturnValue.UsedManifestDownload ->
                                        return Ok(GraceReturnValue.Create "Retrieved manifest-backed file from object storage." correlationId)
                                    | Ok _ ->
                                        outputStream.Dispose()
                                        deletePartialManifestCacheFile ()
                                        return! wholeFileDownload parameters correlationId
                                with
                                | ex ->
                                    deletePartialManifestCacheFile ()

                                    return
                                        Error(
                                            GraceError.Create
                                                $"Failed writing manifest-backed file to object cache: {ExceptionResponse.Create ex}"
                                                correlationId
                                        )
                            else
                                return! wholeFileDownload parameters correlationId
                        })
                            .Result)

                let (results, errors) =
                    results
                    |> Array.partition (fun result ->
                        match result with
                        | Ok _ -> true
                        | Error _ -> false)

                if errors.Count() > 0 then
                    let sb = stringBuilderPool.Get()

                    try
                        sb.Append($"Some files could not be downloaded from object storage.{Environment.NewLine}")
                        |> ignore

                        errors
                        |> Seq.iter (fun e ->
                            match e with
                            | Ok _ -> ()
                            | Error e ->
                                sb.AppendLine($"{e.Error}{Environment.NewLine}{serialize e.Properties}")
                                |> ignore)

                        return Error(sb.ToString())
                    finally
                        stringBuilderPool.Return(sb) |> ignore
                else
                    return Ok()
            | AWSS3 -> return Ok()
            | GoogleCloudStorage -> return Ok()
        }

    /// Downloads files from object storage that aren't already present in the local object cache.
    let downloadFilesFromObjectStorage (getDownloadUriParameters: GetDownloadUriParameters) (files: IEnumerable<LocalFileVersion>) (correlationId: string) =
        let downloadFiles =
            files
            |> Seq.map objectStorageDownloadFileFromLocal

        downloadFilesFromObjectStorageWithClients
            ManifestDownload.downloadFile
            Storage.GetFileFromObjectStorage
            getDownloadUriParameters
            downloadFiles
            correlationId

    /// Scans the repository working directory and writes the Grace index file.
    let downloadFileVersionsFromObjectStorage (getDownloadUriParameters: GetDownloadUriParameters) (files: IEnumerable<FileVersion>) (correlationId: string) =
        let downloadFiles =
            files
            |> Seq.map objectStorageDownloadFileFromFileVersion

        downloadFilesFromObjectStorageWithClients
            ManifestDownload.downloadFile
            Storage.GetFileFromObjectStorage
            getDownloadUriParameters
            downloadFiles
            correlationId

    /// Downloads missing working-directory materialization objects before target mutation starts.
    let mutable private downloadFileVersionsForWorkingDirectoryUpdate = downloadFileVersionsFromObjectStorage

    /// Replaces missing-object download behavior for working-directory mutation regression tests.
    let internal setDownloadFileVersionsForWorkingDirectoryUpdateForTests
        (downloadFileVersions: GetDownloadUriParameters -> IEnumerable<FileVersion> -> string -> Task<Result<unit, string>>)
        =
        downloadFileVersionsForWorkingDirectoryUpdate <- downloadFileVersions

    /// Restores production object download behavior after working-directory mutation regression tests.
    let internal resetDownloadFileVersionsForWorkingDirectoryUpdateForTests () =
        downloadFileVersionsForWorkingDirectoryUpdate <- downloadFileVersionsFromObjectStorage

    /// Reads find file version for upload metadata data needed by the command workflow without changing remote state.
    let internal findFileVersionForUploadMetadata (fileVersions: IEnumerable<FileVersion>) (uploadMetadata: UploadMetadata) =
        fileVersions.First (fun fileVersion ->
            fileVersion.RelativePath = uploadMetadata.RelativePath
            && fileVersion.Sha256Hash = uploadMetadata.Sha256Hash
            && fileVersion.Blake3Hash = uploadMetadata.Blake3Hash)

    /// Reads upload metadata identity data needed by the command workflow without changing remote state.
    let internal uploadMetadataIdentity (uploadMetadata: UploadMetadata) = uploadMetadata.RelativePath, uploadMetadata.Sha256Hash, uploadMetadata.Blake3Hash

    /// Coordinates local file version identity behavior for this CLI command path.
    let internal localFileVersionIdentity (fileVersion: LocalFileVersion) = fileVersion.RelativePath, fileVersion.Sha256Hash, fileVersion.Blake3Hash

    /// Reads upload whole files to object storage data needed by the command workflow without changing remote state.
    let private uploadWholeFilesToObjectStorage (parameters: GetUploadMetadataForFilesParameters) =
        task {
            match Current().ObjectStorageProvider with
            | ObjectStorageProvider.Unknown -> return Error(GraceError.Create (getErrorMessage StorageError.NotImplemented) parameters.CorrelationId)
            | AzureBlobStorage ->
                //logToAnsiConsole Colors.Verbose $"Uploading {fileVersions.Count()} files to object storage."
                if parameters.FileVersions.Count() > 0 then
                    match! Storage.GetUploadMetadataForFiles parameters with
                    | Ok graceReturnValue ->
                        let filesToUpload = graceReturnValue.ReturnValue
                        //logToAnsiConsole Colors.Verbose $"In Services.uploadFilesToObjectStorage(): filesToUpload: {serialize filesToUpload}."
                        let errors = ConcurrentQueue<GraceError>()

                        do!
                            Parallel.ForEachAsync(
                                filesToUpload,
                                Constants.ParallelOptions,
                                (fun uploadMetadata ct ->
                                    ValueTask(
                                        task {
                                            let fileVersion = findFileVersionForUploadMetadata parameters.FileVersions uploadMetadata
                                            //logToAnsiConsole Colors.Verbose $"In Services.uploadFilesToObjectStorage(): Uploading {fileVersion.GetObjectFileName} to object storage."
                                            match!
                                                Storage.SaveFileToObjectStorage
                                                    (RepositoryId.Parse(parameters.RepositoryId))
                                                    fileVersion
                                                    (uploadMetadata.BlobUriWithSasToken)
                                                    parameters.CorrelationId
                                                with
                                            | Ok result -> () //logToAnsiConsole Colors.Verbose $"In Services.uploadFilesToObjectStorage(): Uploaded {fileVersion.GetObjectFileName} to object storage."
                                            | Error error ->
                                                logToAnsiConsole
                                                    Colors.Error
                                                    $"Error uploading {fileVersion.GetObjectFileName} to object storage: {error.Error}"

                                                errors.Enqueue(error)
                                        }
                                    ))
                            )

                        if errors |> Seq.isEmpty then
                            return Ok(GraceReturnValue.Create parameters.FileVersions parameters.CorrelationId)
                        else
                            // use Seq.fold to create a single error message from the ConcurrentQueue<GraceError>
                            let errorMessage =
                                errors
                                |> Seq.fold (fun acc error -> $"{acc}\n{error.Error}") ""

                            let graceError = GraceError.Create (getErrorMessage StorageError.FailedUploadingFilesToObjectStorage) parameters.CorrelationId

                            return Error graceError |> enhance "Errors" errorMessage
                    | Error error -> return Error error
                else
                    return Ok(GraceReturnValue.Create parameters.FileVersions parameters.CorrelationId)
            | AWSS3 -> return Error(GraceError.Create (getErrorMessage StorageError.NotImplemented) parameters.CorrelationId)
            | GoogleCloudStorage -> return Error(GraceError.Create (getErrorMessage StorageError.NotImplemented) parameters.CorrelationId)
        }

    /// Carries the parsed values consumed by the copy storage command handler.
    let private copyStorageParameters (source: GetUploadMetadataForFilesParameters) (fileVersions: FileVersion array) =
        let copy = GetUploadMetadataForFilesParameters()
        copy.OwnerId <- source.OwnerId
        copy.OwnerName <- source.OwnerName
        copy.OrganizationId <- source.OrganizationId
        copy.OrganizationName <- source.OrganizationName
        copy.RepositoryId <- source.RepositoryId
        copy.RepositoryName <- source.RepositoryName
        copy.CorrelationId <- source.CorrelationId
        copy.FileVersions <- fileVersions
        copy

    /// Coordinates storage id or current behavior for this CLI command path.
    let private storageIdOrCurrent<'Id> (rawValue: string) (currentValue: 'Id) (parse: string -> 'Id) =
        if String.IsNullOrWhiteSpace rawValue then currentValue else parse rawValue

    /// Converts CLI parameter values into the manifest upload request sent to Grace services.
    let private createManifestUploadRequest (parameters: GetUploadMetadataForFilesParameters) (fileVersion: FileVersion) =
        let current = Current()

        let request: ManifestUpload.ManifestUploadRequest =
            {
                OwnerId = storageIdOrCurrent parameters.OwnerId current.OwnerId OwnerId.Parse
                OwnerName = parameters.OwnerName
                OrganizationId = storageIdOrCurrent parameters.OrganizationId current.OrganizationId OrganizationId.Parse
                OrganizationName = parameters.OrganizationName
                RepositoryId = storageIdOrCurrent parameters.RepositoryId current.RepositoryId RepositoryId.Parse
                RepositoryName = parameters.RepositoryName
                AuthorizedScope = fileVersion.RelativePath
                FileVersion = fileVersion
                LocalFilePath = getLocalObjectCachePathForFileVersion fileVersion
                CorrelationId = parameters.CorrelationId
                PlannerOptions = LocalPlanner.Options.Default
            }

        request

    /// Reads upload files to object storage with clients data needed by the command workflow without changing remote state.
    let internal uploadFilesToObjectStorageWithClients
        (manifestUpload: FileVersion -> Task<GraceResult<ManifestUpload.ManifestUploadResult>>)
        (wholeFileUpload: GetUploadMetadataForFilesParameters -> Task<GraceResult<FileVersion array>>)
        (parameters: GetUploadMetadataForFilesParameters)
        =
        task {
            if parameters.FileVersions.Count() = 0 then
                return Ok(GraceReturnValue.Create Array.empty<FileVersion> parameters.CorrelationId)
            else
                let fallbackFileVersions = ConcurrentQueue<FileVersion>()
                let uploadedFileVersions = ConcurrentQueue<FileVersion>()
                let errors = ConcurrentQueue<GraceError>()
                let mutable fileVersionIndex = 0

                while fileVersionIndex < parameters.FileVersions.Length do
                    let fileVersion = parameters.FileVersions[fileVersionIndex]

                    match! manifestUpload fileVersion with
                    | Ok result when not result.ReturnValue.UsedManifestUpload -> fallbackFileVersions.Enqueue(fileVersion)
                    | Ok result -> uploadedFileVersions.Enqueue(result.ReturnValue.FileVersion)
                    | Error error -> errors.Enqueue(error)

                    fileVersionIndex <- fileVersionIndex + 1

                if errors |> Seq.isEmpty |> not then
                    let errorMessage =
                        errors
                        |> Seq.fold (fun acc error -> $"{acc}\n{error.Error}") ""

                    let graceError = GraceError.Create (getErrorMessage StorageError.FailedUploadingFilesToObjectStorage) parameters.CorrelationId
                    return Error graceError |> enhance "Errors" errorMessage
                else
                    let fallback = fallbackFileVersions.ToArray()

                    if fallback.Length = 0 then
                        return Ok(GraceReturnValue.Create (uploadedFileVersions.ToArray()) parameters.CorrelationId)
                    else
                        match! wholeFileUpload (copyStorageParameters parameters fallback) with
                        | Error error -> return Error error
                        | Ok fallbackResult ->
                            for fileVersion in fallbackResult.ReturnValue do
                                uploadedFileVersions.Enqueue(fileVersion)

                            return Ok(GraceReturnValue.Create (uploadedFileVersions.ToArray()) parameters.CorrelationId)
        }

    /// Uploads all new or changed files from a directory to object storage.
    let uploadFilesToObjectStorage (parameters: GetUploadMetadataForFilesParameters) =
        uploadFilesToObjectStorageWithClients
            (fun fileVersion -> ManifestUpload.uploadFile (createManifestUploadRequest parameters fileVersion))
            uploadWholeFilesToObjectStorage
            parameters

    /// Reads uploaded file version identity data needed by the command workflow without changing remote state.
    let private uploadedFileVersionIdentity (fileVersion: FileVersion) = (fileVersion.RelativePath, fileVersion.Sha256Hash, fileVersion.Blake3Hash)

    /// Reads uploaded file version path and sha identity data needed by the command workflow without changing remote state.
    let private uploadedFileVersionPathAndShaIdentity (fileVersion: FileVersion) = (fileVersion.RelativePath, fileVersion.Sha256Hash)

    /// Evaluates is manifest backed file version against parsed options and command state.
    let private isManifestBackedFileVersion (fileVersion: FileVersion) =
        not (isNull (box fileVersion.ContentReference))
        && fileVersion.ContentReference.ReferenceType = FileContentReferenceType.FileManifest

    /// Coordinates local unknown or same blake3 matches trusted behavior for this CLI command path.
    let private localUnknownOrSameBlake3MatchesTrusted savedBlake3 localBlake3 =
        not (String.IsNullOrWhiteSpace(string savedBlake3))
        && (String.IsNullOrWhiteSpace(string localBlake3)
            || savedBlake3 = localBlake3)

    /// Tries to map find single trusted path sha match and returns a GraceError instead of throwing on unsupported input.
    let private tryFindSingleTrustedPathShaMatch
        (savedManifestBackedByPathAndSha: Dictionary<RelativePath * Sha256Hash, List<FileVersion>>)
        (localFileVersion: FileVersion)
        =
        let mutable savedManifestBackedFileVersions = Unchecked.defaultof<List<FileVersion>>

        if savedManifestBackedByPathAndSha.TryGetValue(uploadedFileVersionPathAndShaIdentity localFileVersion, &savedManifestBackedFileVersions) then
            let compatibleFileVersions =
                savedManifestBackedFileVersions
                |> Seq.filter (fun savedFileVersion -> localUnknownOrSameBlake3MatchesTrusted savedFileVersion.Blake3Hash localFileVersion.Blake3Hash)
                |> Seq.distinctBy uploadedFileVersionIdentity
                |> Seq.truncate 2
                |> Seq.toArray

            if compatibleFileVersions.Length = 1 then
                Some compatibleFileVersions[0]
            else
                None
        else
            None

    let applyUploadedFileVersionsToDirectoryVersionsWithSavedDirectoryVersions
        (uploadedFileVersions: IEnumerable<FileVersion>)
        (savedDirectoryVersions: IEnumerable<DirectoryVersion>)
        (localDirectoryVersions: IEnumerable<LocalDirectoryVersion>)
        =
        let uploadedByIdentity = Dictionary<RelativePath * Sha256Hash * Blake3Hash, FileVersion>()

        for uploadedFileVersion in uploadedFileVersions do
            uploadedByIdentity[uploadedFileVersionIdentity uploadedFileVersion] <- uploadedFileVersion

        let savedManifestBackedByIdentity = Dictionary<RelativePath * Sha256Hash * Blake3Hash, FileVersion>()
        let savedManifestBackedByPathAndSha = Dictionary<RelativePath * Sha256Hash, List<FileVersion>>()
        let savedDirectoryVersionsById = Dictionary<DirectoryVersionId, DirectoryVersion>()

        for savedDirectoryVersion in savedDirectoryVersions do
            savedDirectoryVersionsById[savedDirectoryVersion.DirectoryVersionId] <- savedDirectoryVersion

            for savedFileVersion in savedDirectoryVersion.Files do
                if isManifestBackedFileVersion savedFileVersion then
                    savedManifestBackedByIdentity[uploadedFileVersionIdentity savedFileVersion] <- savedFileVersion

                    let pathAndShaIdentity = uploadedFileVersionPathAndShaIdentity savedFileVersion
                    let mutable savedFiles = Unchecked.defaultof<List<FileVersion>>

                    if not (savedManifestBackedByPathAndSha.TryGetValue(pathAndShaIdentity, &savedFiles)) then
                        savedFiles <- List<FileVersion>()
                        savedManifestBackedByPathAndSha[pathAndShaIdentity] <- savedFiles

                    savedFiles.Add(savedFileVersion)

        let directoryVersions = Dictionary<DirectoryVersionId, DirectoryVersion>()
        let localDirectoryVersionsById = Dictionary<DirectoryVersionId, LocalDirectoryVersion>()

        for localDirectoryVersion in localDirectoryVersions do
            localDirectoryVersionsById[localDirectoryVersion.DirectoryVersionId] <- localDirectoryVersion

        /// Builds command objects or parameters for execution.
        let rec buildDirectoryVersion (localDirectoryVersion: LocalDirectoryVersion) =
            if directoryVersions.ContainsKey localDirectoryVersion.DirectoryVersionId then
                directoryVersions[localDirectoryVersion.DirectoryVersionId]
            else
                let directoryVersion = localDirectoryVersion.ToDirectoryVersion

                for index in 0 .. directoryVersion.Files.Count - 1 do
                    let fileVersion = directoryVersion.Files[index]
                    let mutable uploadedFileVersion = Unchecked.defaultof<FileVersion>

                    if uploadedByIdentity.TryGetValue(uploadedFileVersionIdentity fileVersion, &uploadedFileVersion) then
                        directoryVersion.Files[ index ] <- uploadedFileVersion
                    else
                        let mutable savedManifestBackedFileVersion = Unchecked.defaultof<FileVersion>

                        if savedManifestBackedByIdentity.TryGetValue(uploadedFileVersionIdentity fileVersion, &savedManifestBackedFileVersion) then
                            directoryVersion.Files[ index ] <- savedManifestBackedFileVersion
                        else
                            match tryFindSingleTrustedPathShaMatch savedManifestBackedByPathAndSha fileVersion with
                            | Some savedManifestBackedFileVersion -> directoryVersion.Files[ index ] <- savedManifestBackedFileVersion
                            | None -> ()

                let localChildDirectories = List<LocalDirectoryVersion>()

                for childDirectoryId in directoryVersion.Directories do
                    let mutable localChildDirectoryVersion = Unchecked.defaultof<LocalDirectoryVersion>
                    let mutable savedChildDirectoryVersion = Unchecked.defaultof<DirectoryVersion>

                    if localDirectoryVersionsById.TryGetValue(childDirectoryId, &localChildDirectoryVersion) then
                        let childDirectoryVersion = buildDirectoryVersion localChildDirectoryVersion

                        localChildDirectories.Add(childDirectoryVersion.ToLocalDirectoryVersion localChildDirectoryVersion.LastWriteTimeUtc)
                    elif savedDirectoryVersionsById.TryGetValue(childDirectoryId, &savedChildDirectoryVersion) then
                        localChildDirectories.Add(savedChildDirectoryVersion.ToLocalDirectoryVersion localDirectoryVersion.LastWriteTimeUtc)
                    else
                        failwith
                            $"Unable to resolve child DirectoryVersionId '{childDirectoryId}' while recomputing DirectoryVersion '{directoryVersion.RelativePath}'."

                let localFiles =
                    directoryVersion
                        .Files
                        .Select(fun fileVersion -> fileVersion.ToLocalFileVersion localDirectoryVersion.LastWriteTimeUtc)
                        .ToList()

                let sha256Hash, blake3Hash = computeDirectoryVersionHashes directoryVersion.RelativePath localChildDirectories localFiles

                directoryVersion.Sha256Hash <- sha256Hash
                directoryVersion.Blake3Hash <- blake3Hash
                directoryVersions[directoryVersion.DirectoryVersionId] <- directoryVersion
                directoryVersion

        let uploadedDirectoryVersions =
            localDirectoryVersions
            |> Seq.map buildDirectoryVersion
            |> Seq.toList

        for uploadedDirectoryVersion in uploadedDirectoryVersions do
            let mutable localDirectoryVersion = Unchecked.defaultof<LocalDirectoryVersion>

            if localDirectoryVersionsById.TryGetValue(uploadedDirectoryVersion.DirectoryVersionId, &localDirectoryVersion) then
                let lastWriteTimeUtc = localDirectoryVersion.LastWriteTimeUtc
                let uploadedLocalDirectoryVersion = uploadedDirectoryVersion.ToLocalDirectoryVersion lastWriteTimeUtc

                localDirectoryVersion.Sha256Hash <- uploadedLocalDirectoryVersion.Sha256Hash
                localDirectoryVersion.Blake3Hash <- uploadedLocalDirectoryVersion.Blake3Hash
                localDirectoryVersion.Directories <- uploadedLocalDirectoryVersion.Directories
                localDirectoryVersion.Files <- uploadedLocalDirectoryVersion.Files
                localDirectoryVersion.Size <- uploadedLocalDirectoryVersion.Size

        List<DirectoryVersion>(uploadedDirectoryVersions)

    let applyUploadedFileVersionsToDirectoryVersions
        (uploadedFileVersions: IEnumerable<FileVersion>)
        (localDirectoryVersions: IEnumerable<LocalDirectoryVersion>)
        =
        applyUploadedFileVersionsToDirectoryVersionsWithSavedDirectoryVersions uploadedFileVersions Array.empty localDirectoryVersions

    /// Rebuilds a LocalDirectoryVersion with a new directory id after child file or directory changes.
    ///
    /// If this is a new subdirectory, the LocalDirectoryVersion will have just been created with empty subdirectory and file lists.
    let processChangedDirectoryVersion (newGraceStatus: GraceStatus) (previousDirectoryVersion: LocalDirectoryVersion) =

        // We process the deepest directories first, so we know any subdirectories of this one will already be in newGraceStatus.

        // Get LocalDirectoryVersion instances for the subdirectories of this DirectoryVersion.
        let subdirectoryVersions =
            if previousDirectoryVersion.Directories.Count > 0 then
                previousDirectoryVersion
                    .Directories
                    .Select(fun directoryId ->
                        let localDirectoryVersion =
                            newGraceStatus
                                .Index
                                .FirstOrDefault(
                                    (fun kvp -> kvp.Key = directoryId),
                                    KeyValuePair(Guid.Empty, LocalDirectoryVersion.Default)
                                )
                                .Value

                        if localDirectoryVersion.DirectoryVersionId
                           <> Guid.Empty then
                            Some localDirectoryVersion
                        else
                            None)
                    .Where(fun opt -> Option.isSome opt)
                    .Select(fun opt -> Option.get opt)
                    .ToList()
            else
                List<LocalDirectoryVersion>()

        // Get the new SHA-256 hash for the updated contents of this directory.
        let newSha256Hash, newBlake3Hash =
            computeDirectoryVersionHashes previousDirectoryVersion.RelativePath subdirectoryVersions previousDirectoryVersion.Files

        let directoryInfo = DirectoryInfo(Path.Combine(Current().RootDirectory, previousDirectoryVersion.RelativePath))

        // Create a new LocalDirectoryVersion that contains a new Id and the updated SHA-256 hash.
        let newDirectoryVersion =
            LocalDirectoryVersion.CreateWithHashes
                (Guid.NewGuid())
                previousDirectoryVersion.OwnerId
                previousDirectoryVersion.OrganizationId
                previousDirectoryVersion.RepositoryId
                previousDirectoryVersion.RelativePath
                newSha256Hash
                newBlake3Hash
                previousDirectoryVersion.Directories
                previousDirectoryVersion.Files
                (getLocalDirectorySize previousDirectoryVersion.Files)
                directoryInfo.LastWriteTimeUtc

        newDirectoryVersion

    /// Determines if the given difference is for a directory, instead of a file.
    let isDirectoryChange (difference: FileSystemDifference) =
        match difference.FileSystemEntryType with
        | FileSystemEntryType.Directory -> true
        | FileSystemEntryType.File -> false

    /// Determines if the given difference is for a file, instead of a directory.
    let isFileChange difference = not <| isDirectoryChange difference

    /// Processes directory additions or changes
    let private processDirectoryChange (newGraceStatus: GraceStatus) (previousGraceStatus: GraceStatus) (difference: FileSystemDifference) =

        let previousRootVersion = getRootDirectoryVersion previousGraceStatus

        let previousDirectoryVersion =
            previousGraceStatus
                .Index
                .FirstOrDefault(
                    (fun kvp -> kvp.Value.RelativePath = difference.RelativePath),
                    KeyValuePair(Guid.Empty, LocalDirectoryVersion.Default)
                )
                .Value

        if previousDirectoryVersion.DirectoryVersionId
           <> Guid.Empty then
            Some(processChangedDirectoryVersion newGraceStatus previousDirectoryVersion)
        else
            let directoryInfo = DirectoryInfo(Path.Combine(Current().RootDirectory, difference.RelativePath))
            let sha256Hash, blake3Hash = computeDirectoryVersionHashes difference.RelativePath (List()) (List())

            Some(
                LocalDirectoryVersion.CreateWithHashes
                    (Guid.NewGuid())
                    previousRootVersion.OwnerId
                    previousRootVersion.OrganizationId
                    previousRootVersion.RepositoryId
                    difference.RelativePath
                    sha256Hash
                    blake3Hash
                    (List())
                    (List())
                    0L
                    directoryInfo.LastWriteTimeUtc
            )

    /// Processes file additions to a directory
    let private processFileAddition
        (changedDirectoryVersions: ConcurrentDictionary<RelativePath, LocalDirectoryVersion>)
        (newGraceStatus: GraceStatus)
        (difference: FileSystemDifference)
        =
        task {

            let fileInfo = FileInfo(Path.Combine(Current().RootDirectory, difference.RelativePath))
            let relativeDirectoryPath = getLocalRelativeDirectory fileInfo.DirectoryName (Current().RootDirectory)

            let directoryVersion =
                let mutable changedDirectoryVersion = LocalDirectoryVersion.Default

                if changedDirectoryVersions.TryGetValue(relativeDirectoryPath, &changedDirectoryVersion) then
                    changedDirectoryVersion
                else
                    newGraceStatus.Index.Values.FirstOrDefault((fun dv -> dv.RelativePath = relativeDirectoryPath), LocalDirectoryVersion.Default)

            match! createLocalFileVersion fileInfo with
            | Some fileVersion ->
                directoryVersion.Files.Add(fileVersion)
                directoryVersion.Size <- directoryVersion.Files.Sum(fun file -> int64 (file.Size))
                let updated = directoryVersion

                changedDirectoryVersions.AddOrUpdate(directoryVersion.RelativePath, (fun _ -> updated), (fun _ _ -> updated))
                |> ignore
            | None -> ()
        }

    /// Processes file changes (updates)
    let private processFileChange
        (changedDirectoryVersions: ConcurrentDictionary<RelativePath, LocalDirectoryVersion>)
        (newGraceStatus: GraceStatus)
        (difference: FileSystemDifference)
        =
        task {

            let fileInfo = FileInfo(Path.Combine(Current().RootDirectory, difference.RelativePath))
            let relativeDirectoryPath = normalizeFilePath (getLocalRelativeDirectory fileInfo.DirectoryName (Current().RootDirectory))

            let directoryVersion =
                let alreadyChanged =
                    changedDirectoryVersions.Values.FirstOrDefault((fun dv -> dv.RelativePath = relativeDirectoryPath), LocalDirectoryVersion.Default)

                if alreadyChanged.DirectoryVersionId <> Guid.Empty then
                    alreadyChanged
                else
                    newGraceStatus.Index.Values.First(fun dv -> dv.RelativePath = relativeDirectoryPath)

            let existingFileIndex = directoryVersion.Files.FindIndex(fun file -> file.RelativePath = difference.RelativePath)
            let existingFileVersion = directoryVersion.Files[existingFileIndex]

            match! createLocalFileVersion fileInfo with
            | Some fileVersion ->
                if fileVersion.Sha256Hash
                   <> existingFileVersion.Sha256Hash
                   || fileVersion.Blake3Hash
                      <> existingFileVersion.Blake3Hash then
                    directoryVersion.Files.RemoveAt(existingFileIndex)
                    directoryVersion.Files.Add(fileVersion)
                    directoryVersion.Size <- directoryVersion.Files.Sum(fun file -> int64 (file.Size))
                    let updated = directoryVersion

                    changedDirectoryVersions.AddOrUpdate(directoryVersion.RelativePath, (fun _ -> updated), (fun _ _ -> updated))
                    |> ignore
            | None -> ()
        }

    /// Processes file deletions
    let private processFileDeletion
        (changedDirectoryVersions: ConcurrentDictionary<RelativePath, LocalDirectoryVersion>)
        (newGraceStatus: GraceStatus)
        (difference: FileSystemDifference)
        =

        let fileInfo = FileInfo(Path.Combine(Current().RootDirectory, difference.RelativePath))
        let relativeDirectoryPath = getLocalRelativeDirectory fileInfo.DirectoryName (Current().RootDirectory)

        let directoryVersion =
            newGraceStatus
                .Index
                .Values
                .Where(fun dv -> dv.RelativePath = relativeDirectoryPath)
                .ToList()

        match directoryVersion.Count with
        | 0 -> ()
        | 1 ->
            let dv = directoryVersion[0]
            let index = dv.Files.FindIndex(fun file -> file.RelativePath = difference.RelativePath)
            dv.Files.RemoveAt(index)
            dv.Size <- dv.Files.Sum(fun file -> int64 (file.Size))
            let updated = dv

            changedDirectoryVersions.AddOrUpdate(dv.RelativePath, (fun _ -> updated), (fun _ _ -> updated))
            |> ignore
        | _ -> ()

    /// Recursively processes changed directories from leaf to root
    let rec private processChangedDirectoriesBottomUp
        (newGraceStatus: GraceStatus)
        (changedDirectoryVersions: ConcurrentDictionary<RelativePath, LocalDirectoryVersion>)
        (newDirectoryVersions: List<LocalDirectoryVersion>)
        =

        if changedDirectoryVersions.IsEmpty then
            ()
        else
            let relativePath =
                changedDirectoryVersions
                    .Keys
                    .OrderByDescending(fun rp -> countSegments rp)
                    .First()

            let mutable previousDirectoryVersion = LocalDirectoryVersion.Default

            if changedDirectoryVersions.TryRemove(relativePath, &previousDirectoryVersion) then
                let newDirectoryVersion = processChangedDirectoryVersion newGraceStatus previousDirectoryVersion
                newDirectoryVersions.Add(newDirectoryVersion)

                let mutable previous = LocalDirectoryVersion.Default
                let foundPrevious = newGraceStatus.Index.TryRemove(previousDirectoryVersion.DirectoryVersionId, &previous)

                newGraceStatus.Index.TryAdd(newDirectoryVersion.DirectoryVersionId, newDirectoryVersion)
                |> ignore

                match getParentPath relativePath with
                | Some path ->
                    let dv =
                        let alreadyChanged = changedDirectoryVersions.Values.FirstOrDefault((fun dv -> dv.RelativePath = path), LocalDirectoryVersion.Default)

                        if alreadyChanged.DirectoryVersionId <> Guid.Empty then
                            alreadyChanged
                        else
                            newGraceStatus.Index.Values.First(fun dv -> dv.RelativePath = path)

                    if foundPrevious then
                        dv.Directories.Remove(previous.DirectoryVersionId)
                        |> ignore

                    dv.Directories.Add(newDirectoryVersion.DirectoryVersionId)
                    |> ignore

                    changedDirectoryVersions.AddOrUpdate(path, (fun _ -> dv), (fun _ _ -> dv))
                    |> ignore

                    // Recursively process the parent
                    processChangedDirectoriesBottomUp newGraceStatus changedDirectoryVersions newDirectoryVersions
                | None -> ()

    /// Normalizes Grace ids for directory difference path by keeping explicit scope values and clearing implicit child scopes.
    let private normalizeDirectoryDifferencePath (relativePath: RelativePath) =
        let normalized = normalizeFilePath $"{relativePath}"

        if normalized = Constants.RootDirectoryPath then
            Constants.RootDirectoryPath
        else
            normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

    /// Evaluates is directory path in subtree against parsed options and command state.
    let private isDirectoryPathInSubtree (subtreeRoot: RelativePath) (candidate: RelativePath) =
        let normalizedSubtreeRoot = normalizeDirectoryDifferencePath subtreeRoot
        let normalizedCandidate = normalizeDirectoryDifferencePath candidate

        normalizedCandidate.Equals(normalizedSubtreeRoot, StringComparison.Ordinal)
        || normalizedCandidate.StartsWith($"{normalizedSubtreeRoot}/", StringComparison.Ordinal)

    /// Coordinates select top level deleted directories behavior for this CLI command path.
    let private selectTopLevelDeletedDirectories (differences: IEnumerable<FileSystemDifference>) =
        let topLevelDeletedDirectories = List<RelativePath>()

        let deletedDirectories =
            differences
                .Where(fun difference ->
                    isDirectoryChange difference
                    && difference.DifferenceType = Delete)
                .Select(fun difference -> normalizeDirectoryDifferencePath difference.RelativePath)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(fun relativePath -> countSegments relativePath)

        for deletedDirectory in deletedDirectories do
            let ancestorAlreadyDeleted =
                topLevelDeletedDirectories
                |> Seq.exists (fun existingDeletedDirectory ->
                    not (existingDeletedDirectory.Equals(deletedDirectory, StringComparison.Ordinal))
                    && isDirectoryPathInSubtree existingDeletedDirectory deletedDirectory)

            if not ancestorAlreadyDeleted then
                topLevelDeletedDirectories.Add(deletedDirectory)

        topLevelDeletedDirectories

    /// Tries to map find surviving directory version and returns a GraceError instead of throwing on unsupported input.
    let private tryFindSurvivingDirectoryVersion
        (changedDirectoryVersions: ConcurrentDictionary<RelativePath, LocalDirectoryVersion>)
        (newGraceStatus: GraceStatus)
        (relativePath: RelativePath)
        =

        let mutable changedDirectoryVersion = LocalDirectoryVersion.Default

        if changedDirectoryVersions.TryGetValue(relativePath, &changedDirectoryVersion) then
            Some changedDirectoryVersion
        else
            let directoryVersion = newGraceStatus.Index.Values.FirstOrDefault((fun dv -> dv.RelativePath = relativePath), LocalDirectoryVersion.Default)

            if directoryVersion.DirectoryVersionId <> Guid.Empty then
                Some directoryVersion
            else
                None

    /// Coordinates rec behavior for this CLI command path.
    let rec private tryFindNearestSurvivingParentDirectoryVersion
        (changedDirectoryVersions: ConcurrentDictionary<RelativePath, LocalDirectoryVersion>)
        (newGraceStatus: GraceStatus)
        (relativePath: RelativePath)
        =

        match getParentPath relativePath with
        | None -> None
        | Some parentPath ->
            match tryFindSurvivingDirectoryVersion changedDirectoryVersions newGraceStatus parentPath with
            | Some parentDirectoryVersion -> Some parentDirectoryVersion
            | None -> tryFindNearestSurvivingParentDirectoryVersion changedDirectoryVersions newGraceStatus parentPath

    /// Coordinates process directory deletion behavior for this CLI command path.
    let private processDirectoryDeletion
        (changedDirectoryVersions: ConcurrentDictionary<RelativePath, LocalDirectoryVersion>)
        (newGraceStatus: GraceStatus)
        (relativePath: RelativePath)
        =

        if relativePath = Constants.RootDirectoryPath then
            ()
        else
            let deletedDirectoryVersions =
                newGraceStatus
                    .Index
                    .Values
                    .Where(fun dv -> isDirectoryPathInSubtree relativePath dv.RelativePath)
                    .ToList()

            let deletedDirectoryVersion = deletedDirectoryVersions.FirstOrDefault((fun dv -> dv.RelativePath = relativePath), LocalDirectoryVersion.Default)

            if deletedDirectoryVersion.DirectoryVersionId
               <> Guid.Empty then
                for deletedDirectoryVersion in deletedDirectoryVersions do
                    let mutable removedDirectoryVersion = LocalDirectoryVersion.Default

                    newGraceStatus.Index.TryRemove(deletedDirectoryVersion.DirectoryVersionId, &removedDirectoryVersion)
                    |> ignore

                    let mutable changedRemovedDirectoryVersion = LocalDirectoryVersion.Default

                    changedDirectoryVersions.TryRemove(deletedDirectoryVersion.RelativePath, &changedRemovedDirectoryVersion)
                    |> ignore

                match tryFindNearestSurvivingParentDirectoryVersion changedDirectoryVersions newGraceStatus relativePath with
                | Some parentDirectoryVersion ->
                    parentDirectoryVersion.Directories.Remove(deletedDirectoryVersion.DirectoryVersionId)
                    |> ignore

                    changedDirectoryVersions.AddOrUpdate(
                        parentDirectoryVersion.RelativePath,
                        (fun _ -> parentDirectoryVersion),
                        (fun _ _ -> parentDirectoryVersion)
                    )
                    |> ignore
                | None -> ()

    /// Main refactored function
    let getNewGraceStatusAndDirectoryVersions (previousGraceStatus: GraceStatus) (differences: IEnumerable<FileSystemDifference>) =
        task {
            if parseResult |> isOutputFormat "Verbose" then
                logToAnsiConsole Colors.Verbose $"In getNewGraceStatusAndDirectoryVersions: differences:{Environment.NewLine}{serialize differences}"

            let changedDirectoryVersions = ConcurrentDictionary<RelativePath, LocalDirectoryVersion>()
            let mutable newGraceStatus = { previousGraceStatus with Index = GraceIndex(previousGraceStatus.Index) }

            // Process directory changes (Add/Change/Delete)
            for difference in differences.Where(fun d -> isDirectoryChange d) do
                match difference.DifferenceType with
                | Add
                | Change ->
                    match processDirectoryChange newGraceStatus previousGraceStatus difference with
                    | Some newDirectoryVersion ->
                        let previousDirectoryVersions = newGraceStatus.Index.Values.Where(fun dv -> dv.RelativePath = difference.RelativePath)

                        logToAnsiConsole
                            Colors.Verbose
                            $"Processing directory {difference.DifferenceType} for path: {difference.RelativePath}. Previous versions: {serialize previousDirectoryVersions}."

                        let mutable previous = LocalDirectoryVersion.Default
                        // Remove the previous directory version from the index.
                        for previousDirectoryVersion in previousDirectoryVersions do
                            newGraceStatus.Index.TryRemove(previousDirectoryVersion.DirectoryVersionId, &previous)
                            |> ignore

                        // Add the new directory version to the index.
                        newGraceStatus.Index.AddOrUpdate(
                            newDirectoryVersion.DirectoryVersionId,
                            (fun _ -> newDirectoryVersion),
                            (fun _ _ -> newDirectoryVersion)
                        )
                        |> ignore

                        if difference.DifferenceType = Add then
                            changedDirectoryVersions.AddOrUpdate(difference.RelativePath, (fun _ -> newDirectoryVersion), (fun _ _ -> newDirectoryVersion))
                            |> ignore
                    | None -> ()
                | Delete -> ()

            for deletedDirectory in selectTopLevelDeletedDirectories differences do
                processDirectoryDeletion changedDirectoryVersions newGraceStatus deletedDirectory

            // Process file changes (Add/Change/Delete)
            for difference in differences.Where(fun d -> isFileChange d) do
                match difference.DifferenceType with
                | Add -> do! processFileAddition changedDirectoryVersions newGraceStatus difference
                | Change -> do! processFileChange changedDirectoryVersions newGraceStatus difference
                | Delete -> processFileDeletion changedDirectoryVersions newGraceStatus difference

            // Recursively process changed directories from leaf to root
            let newDirectoryVersions = List<LocalDirectoryVersion>()
            processChangedDirectoriesBottomUp newGraceStatus changedDirectoryVersions newDirectoryVersions

            if newDirectoryVersions.Count > 0 then
                let rootExists =
                    newGraceStatus
                        .Index
                        .Values
                        .FirstOrDefault(
                            (fun dv -> dv.RelativePath = Constants.RootDirectoryPath),
                            LocalDirectoryVersion.Default
                        )
                        .DirectoryVersionId
                    <> Guid.Empty

                if rootExists then
                    let newRootDirectoryVersion = getRootDirectoryVersion newGraceStatus

                    newGraceStatus <-
                        { newGraceStatus with
                            RootDirectoryId = newRootDirectoryVersion.DirectoryVersionId
                            RootDirectorySha256Hash = newRootDirectoryVersion.Sha256Hash
                            RootDirectoryBlake3Hash = newRootDirectoryVersion.Blake3Hash
                        }

                return (newGraceStatus, newDirectoryVersions)
            else
                return (previousGraceStatus, newDirectoryVersions)
        }

    /// Ensures that the provided directory versions are uploaded to Grace Server.
    /// This will add new directory versions, and ignore existing directory versions, as they are immutable.
    let saveDirectoryVersions (directoryVersions: IEnumerable<DirectoryVersion>) correlationId =
        let saveParameters = SaveDirectoryVersionsParameters()
        saveParameters.OwnerId <- $"{Current().OwnerId}"
        saveParameters.OwnerName <- Current().OwnerName
        saveParameters.OrganizationId <- $"{Current().OrganizationId}"
        saveParameters.OrganizationName <- Current().OrganizationName
        saveParameters.RepositoryId <- $"{Current().RepositoryId}"
        saveParameters.RepositoryName <- Current().RepositoryName
        saveParameters.CorrelationId <- correlationId
        saveParameters.DirectoryVersions <- directoryVersions.ToList()

        DirectoryVersion.SaveDirectoryVersions saveParameters

    /// Reads upload directory versions data needed by the command workflow without changing remote state.
    let uploadDirectoryVersions (localDirectoryVersions: List<LocalDirectoryVersion>) correlationId =
        saveDirectoryVersions (localDirectoryVersions.Select(fun ldv -> ldv.ToDirectoryVersion)) correlationId

    /// Reads saved directory versions for root directory from ParseResult, local configuration, or Grace ids.
    let getSavedDirectoryVersionsForRootDirectory (rootDirectoryId: DirectoryVersionId) (correlationId: CorrelationId) =
        task {
            if rootDirectoryId = DirectoryVersionId.Empty then
                return Ok Array.empty
            else
                let current = Current()

                let parameters =
                    GetParameters(
                        OwnerId = $"{current.OwnerId}",
                        OwnerName = current.OwnerName,
                        OrganizationId = $"{current.OrganizationId}",
                        OrganizationName = current.OrganizationName,
                        RepositoryId = $"{current.RepositoryId}",
                        RepositoryName = current.RepositoryName,
                        DirectoryVersionId = $"{rootDirectoryId}",
                        CorrelationId = correlationId
                    )

                match! DirectoryVersion.GetDirectoryVersionsRecursive parameters with
                | Ok returnValue ->
                    return
                        Ok(
                            returnValue.ReturnValue
                            |> Seq.map (fun directoryVersionDto -> directoryVersionDto.DirectoryVersion)
                            |> Seq.toArray
                        )
                | Error error -> return Error error
        }

    /// Computes a stable path segment from repository identity text without leaking local paths into temp directory names.
    let private localRepositoryScopeSegment (value: string) =
        let normalizedValue = if String.IsNullOrWhiteSpace(value) then "empty" else value.Trim()

        Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedValue)))
            .ToLowerInvariant()

    /// Gets the normalized repository root text used to keep local coordination files scoped to one checkout.
    let private localRepositoryRootScope (rootDirectory: string) =
        if String.IsNullOrWhiteSpace(rootDirectory) then
            "empty-root"
        else
            let normalizedRoot =
                Path
                    .GetFullPath(rootDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

            if runningOnWindows then normalizedRoot.ToUpperInvariant() else normalizedRoot

    /// Gets the temp directory shared by local coordination files for one repository root and branch identity.
    let internal localRepositoryBranchTempDirectoryForIdentity
        (repositoryId: Guid)
        (repositoryName: string)
        (rootDirectory: string)
        (branchId: Guid)
        (branchName: string)
        =
        let repositoryScope =
            if repositoryId <> Guid.Empty then
                repositoryId.ToString("N")
            else
                localRepositoryScopeSegment repositoryName

        let rootScope =
            rootDirectory
            |> localRepositoryRootScope
            |> localRepositoryScopeSegment

        let branchScope =
            if branchId <> Guid.Empty then
                branchId.ToString("N")
            else
                localRepositoryScopeSegment branchName

        Path.Combine(Path.GetTempPath(), "Grace", "repositories", repositoryScope, rootScope, "branches", branchScope)

    /// Gets the repository-scoped Watch IPC path for a specific local repository identity.
    let internal IpcFileNameForIdentity (repositoryId: Guid) (repositoryName: string) (rootDirectory: string) (branchId: Guid) (branchName: string) =
        getNativeFilePath (
            Path.Combine(localRepositoryBranchTempDirectoryForIdentity repositoryId repositoryName rootDirectory branchId branchName, Constants.IpcFileName)
        )

    /// The full path of the inter-process communication file that grace watch uses to communicate with other invocations of Grace.
    let IpcFileName () =
        let current = Current()

        IpcFileNameForIdentity current.RepositoryId current.RepositoryName current.RootDirectory current.BranchId current.BranchName

    /// Reads one Watch IPC status file for diagnostics without logging file paths, stack traces, or raw JSON.
    let private inspectGraceWatchStatusFile ipcFileName =
        task {
            if System.IO.File.Exists(ipcFileName) then
                try
                    let json = System.IO.File.ReadAllText(ipcFileName)

                    let status = deserializeNormalizedGraceWatchStatus json

                    let inspection =
                        {
                            Exists = true
                            Status = Some status
                            PersistedMode = tryReadGraceWatchPersistedMode json
                            SafetyFlags = status.SafetyFlags
                            ReadError = None
                        }

                    return { inspection with SafetyFlags = safetyFlagsForGraceWatchStatusInspection inspection }
                with
                | _ ->
                    return
                        {
                            Exists = true
                            Status = None
                            PersistedMode = None
                            SafetyFlags =
                                [|
                                    "unreadableStatus"
                                    "requiresExplicitResync"
                                |]
                            ReadError = Some "The Grace Watch status file exists but could not be read."
                        }
            else
                return
                    {
                        Exists = false
                        Status = None
                        PersistedMode = None
                        SafetyFlags =
                            [|
                                "missingStatus"
                                "requiresExplicitResync"
                            |]
                        ReadError = None
                    }
        }

    /// Reads Watch IPC status for diagnostics without logging file paths, stack traces, or raw JSON.
    let inspectGraceWatchStatus () = inspectGraceWatchStatusFile (IpcFileName())

    /// Reads Watch IPC status for one captured repository branch identity.
    let internal inspectGraceWatchStatusForIdentity repositoryId repositoryName rootDirectory branchId branchName =
        inspectGraceWatchStatusFile (IpcFileNameForIdentity repositoryId repositoryName rootDirectory branchId branchName)

    /// Builds the persisted Grace Watch status snapshot for a specific repository identity.
    let private createGraceWatchStatusWithPendingWorkForIdentity
        (pendingWorkOverride: bool option)
        repositoryId
        repositoryName
        rootDirectory
        branchId
        branchName
        (graceStatus: GraceStatus)
        (directoryIdsOverride: HashSet<DirectoryVersionId> option)
        =
        let hasPendingWatchWork =
            pendingWorkOverride
            |> Option.defaultWith hasGraceWatchPendingWorkForStatus

        let directoryIds =
            match directoryIdsOverride with
            | Some ids -> HashSet<DirectoryVersionId>(ids)
            | None -> HashSet<DirectoryVersionId>(graceStatus.Index.Keys)

        let mutable rootDirectoryVersion = LocalDirectoryVersion.Default

        let rootDirectoryBlake3Hash =
            if graceStatus.Index.TryGetValue(graceStatus.RootDirectoryId, &rootDirectoryVersion) then
                rootDirectoryVersion.Blake3Hash
            elif not (String.IsNullOrWhiteSpace(string graceStatus.RootDirectoryBlake3Hash)) then
                graceStatus.RootDirectoryBlake3Hash
            else
                Blake3Hash String.Empty

        {
            UpdatedAt = getCurrentInstant ()
            IsStartupClaim = false
            RepositoryId = repositoryId
            RepositoryName = repositoryName
            BranchId = branchId
            BranchName = branchName
            RootDirectory = rootDirectory
            HasPendingWatchWork = hasPendingWatchWork
            IsWorkingTreeClean = not hasPendingWatchWork
            RootDirectoryId = graceStatus.RootDirectoryId
            RootDirectorySha256Hash = graceStatus.RootDirectorySha256Hash
            RootDirectoryBlake3Hash = rootDirectoryBlake3Hash
            LastFileUploadInstant = graceStatus.LastSuccessfulFileUpload
            LastDirectoryVersionInstant = graceStatus.LastSuccessfulDirectoryVersionUpload
            DirectoryIds = directoryIds
        }

    /// Builds the persisted Grace Watch status snapshot used by check and startup coordination.
    let private createGraceWatchStatusWithPendingWork
        (pendingWorkOverride: bool option)
        (graceStatus: GraceStatus)
        (directoryIdsOverride: HashSet<DirectoryVersionId> option)
        =
        let current = Current()

        createGraceWatchStatusWithPendingWorkForIdentity
            pendingWorkOverride
            current.RepositoryId
            current.RepositoryName
            current.RootDirectory
            current.BranchId
            current.BranchName
            graceStatus
            directoryIdsOverride

    /// Builds a Watch IPC snapshot using the current process pending-work flag.
    let private createGraceWatchStatus graceStatus directoryIdsOverride = createGraceWatchStatusWithPendingWork None graceStatus directoryIdsOverride

    /// Builds the startup-claim record that prevents duplicate Grace Watch instances for the current repository identity.
    let private createGraceWatchStartupClaim () =
        let current = Current()

        { GraceWatchStatus.Default with
            UpdatedAt = getCurrentInstant ()
            IsStartupClaim = true
            RepositoryId = current.RepositoryId
            RepositoryName = current.RepositoryName
            BranchId = current.BranchId
            BranchName = current.BranchName
            RootDirectory = current.RootDirectory
        }

    /// Checks if the Grace Watch status is within the past 5m.
    let private isGraceWatchStatusFresh (graceWatchStatus: GraceWatchStatus) =
        graceWatchStatus.UpdatedAt > getCurrentInstant()
            .Minus(Duration.FromMinutes(5.0))

    /// Checks if the Grace Watch status is valid and has data.
    let private isGraceWatchStatusUsable (graceWatchStatus: GraceWatchStatus) =
        isGraceWatchStatusFresh graceWatchStatus
        && not graceWatchStatus.IsStartupClaim
        && graceWatchStatus.Mode = GraceWatchRuntimeMode.HealthyIncremental

    /// Writes Grace Watch IPC JSON to disk after the caller has acquired the status write gate.
    let private writeGraceWatchStatusWithPersistedModeCoreToFile (ipcFileName: string) persistedModeOverride fileMode graceWatchStatus =
        task {
            Directory.CreateDirectory(Path.GetDirectoryName(ipcFileName))
            |> ignore

            use fileStream = new FileStream(ipcFileName, fileMode, FileAccess.Write, FileShare.None)

            match persistedModeOverride with
            | Some mode -> do! writeGraceWatchStatusContractWithPersistedModeToStream fileStream mode graceWatchStatus
            | None -> do! writeGraceWatchStatusContractToStream fileStream graceWatchStatus

            graceWatchStatusUpdateTime <- graceWatchStatus.UpdatedAt
        }

    /// Writes Grace Watch IPC JSON to the current identity path after the caller has acquired the status write gate.
    let private writeGraceWatchStatusWithPersistedModeCore persistedModeOverride fileMode graceWatchStatus =
        writeGraceWatchStatusWithPersistedModeCoreToFile (IpcFileName()) persistedModeOverride fileMode graceWatchStatus

    /// Writes grace watch status data through the CLI output contract with an optional runtime-mode override.
    let private writeGraceWatchStatusWithPersistedMode persistedModeOverride fileMode graceWatchStatus =
        task {
            do! graceWatchStatusWriteGate.WaitAsync()

            try
                do! writeGraceWatchStatusWithPersistedModeCore persistedModeOverride fileMode graceWatchStatus
            finally
                graceWatchStatusWriteGate.Release() |> ignore
        }

    /// Writes Grace Watch IPC status without forcing a compact runtime mode override.
    let private writeGraceWatchStatus fileMode graceWatchStatus = writeGraceWatchStatusWithPersistedMode None fileMode graceWatchStatus

    /// Writes Grace Watch IPC content after converting the current Grace status snapshot.
    let private writeGraceWatchInterprocessFileSnapshotUnderGate
        persistedModeOverride
        pendingWorkOverride
        (graceStatus: GraceStatus)
        (directoryIdsOverride: HashSet<DirectoryVersionId> option)
        =
        let newGraceWatchStatus = createGraceWatchStatusWithPendingWork pendingWorkOverride graceStatus directoryIdsOverride
        //logToAnsiConsole Colors.Important $"In updateGraceWatchStatus. newGraceWatchStatus.UpdatedAt: {newGraceWatchStatus.UpdatedAt.ToString(InstantPattern.ExtendedIso.PatternText, CultureInfo.InvariantCulture)}."
        //logToAnsiConsole Colors.Highlighted $"{Markup.Escape(EnhancedStackTrace.Current().ToString())}"

        writeGraceWatchStatusWithPersistedModeCore persistedModeOverride FileMode.Create newGraceWatchStatus

    /// Writes Grace Watch IPC content after converting the current Grace status snapshot.
    let private updateGraceWatchInterprocessFileCore persistedModeOverride pendingWorkOverride graceStatus directoryIdsOverride =
        task {
            try
                do! graceWatchStatusWriteGate.WaitAsync()

                try
                    do! writeGraceWatchInterprocessFileSnapshotUnderGate persistedModeOverride pendingWorkOverride graceStatus directoryIdsOverride
                finally
                    graceWatchStatusWriteGate.Release() |> ignore

                logToAnsiConsole Colors.Important $"Wrote inter-process communication file."
            with
            | ex ->
                logToAnsiConsole Colors.Error $"Exception in updateGraceWatchInterprocessFile."
                logToAnsiConsole Colors.Error $"ex.GetType: {ex.GetType().FullName}{Environment.NewLine}{Environment.NewLine}"
                logToAnsiConsole Colors.Error $"ex.Message: {ex.Message}{Environment.NewLine}{Environment.NewLine}{ex.StackTrace}"
        }

    /// Updates the contents of the `grace watch` status inter-process communication file.
    let updateGraceWatchInterprocessFile (graceStatus: GraceStatus) (directoryIdsOverride: HashSet<DirectoryVersionId> option) =
        updateGraceWatchInterprocessFileCore None None graceStatus directoryIdsOverride

    /// Updates Watch IPC for the repository identity captured before a materialization began.
    let updateGraceWatchInterprocessFileForIdentity
        repositoryId
        repositoryName
        rootDirectory
        branchId
        branchName
        (graceStatus: GraceStatus)
        (directoryIdsOverride: HashSet<DirectoryVersionId> option)
        =
        task {
            let ipcFileName = IpcFileNameForIdentity repositoryId repositoryName rootDirectory branchId branchName

            try
                do! graceWatchStatusWriteGate.WaitAsync()

                try
                    let graceWatchStatus =
                        createGraceWatchStatusWithPendingWorkForIdentity
                            None
                            repositoryId
                            repositoryName
                            rootDirectory
                            branchId
                            branchName
                            graceStatus
                            directoryIdsOverride

                    do! writeGraceWatchStatusWithPersistedModeCoreToFile ipcFileName None FileMode.Create graceWatchStatus
                finally
                    graceWatchStatusWriteGate.Release() |> ignore

                logToAnsiConsole Colors.Important $"Wrote inter-process communication file."
            with
            | ex ->
                logToAnsiConsole Colors.Error $"Exception in updateGraceWatchInterprocessFile."
                logToAnsiConsole Colors.Error $"ex.GetType: {ex.GetType().FullName}{Environment.NewLine}{Environment.NewLine}"
                logToAnsiConsole Colors.Error $"ex.Message: {ex.Message}{Environment.NewLine}{Environment.NewLine}{ex.StackTrace}"
        }

    /// Selects the live Watch pending and mode facts that a non-Watch writer must not erase.
    let private liveWorkPreservationFromInspection (inspection: GraceWatchStatusInspection) =
        let shouldPreserveLiveWork =
            inspection.IsLiveProcess
            && match inspection.EffectiveMode with
               | Some GraceWatchRuntimeMode.HealthyIncremental ->
                   match inspection.Status with
                   | Some status when status.Mode = GraceWatchRuntimeMode.HealthyIncremental -> inspection.HasCurrentRepositoryIdentity
                   | _ -> false
               | Some GraceWatchRuntimeMode.StartingUp
               | Some GraceWatchRuntimeMode.Suspended
               | Some GraceWatchRuntimeMode.Resynchronizing
               | Some GraceWatchRuntimeMode.Stopping -> inspection.HasCurrentLiveWatchStateIdentity
               | Some _ -> inspection.HasCurrentLiveWatchStateIdentity
               | None -> false

        let pendingWorkOverride =
            if shouldPreserveLiveWork then
                match inspection.Status, inspection.EffectiveMode with
                | Some status, Some GraceWatchRuntimeMode.HealthyIncremental ->
                    Some(
                        status.HasPendingWatchWork
                        || not status.IsWorkingTreeClean
                    )
                | Some _, Some _ -> Some true
                | _ -> None
            else
                None

        let persistedModeOverride =
            if shouldPreserveLiveWork then
                match inspection.EffectiveMode with
                | Some GraceWatchRuntimeMode.StartingUp
                | Some GraceWatchRuntimeMode.Suspended
                | Some GraceWatchRuntimeMode.Resynchronizing
                | Some GraceWatchRuntimeMode.Stopping -> inspection.EffectiveMode
                | _ -> None
            else
                None

        persistedModeOverride, pendingWorkOverride

    /// Updates Watch IPC identity fields without turning live Watch pending or recovery state into a clean shortcut.
    let private updateGraceWatchInterprocessFilePreservingLiveWorkStateCore beforeWriteBoundary graceStatus directoryIdsOverride =
        task {
            let! _ = inspectGraceWatchStatus ()

            try
                do! graceWatchStatusWriteGate.WaitAsync()

                try
                    beforeWriteBoundary ()

                    let! boundaryInspection = inspectGraceWatchStatus ()
                    let persistedModeOverride, pendingWorkOverride = liveWorkPreservationFromInspection boundaryInspection

                    do! writeGraceWatchInterprocessFileSnapshotUnderGate persistedModeOverride pendingWorkOverride graceStatus directoryIdsOverride
                finally
                    graceWatchStatusWriteGate.Release() |> ignore

                logToAnsiConsole Colors.Important $"Wrote inter-process communication file."
            with
            | ex ->
                logToAnsiConsole Colors.Error $"Exception in updateGraceWatchInterprocessFile."
                logToAnsiConsole Colors.Error $"ex.GetType: {ex.GetType().FullName}{Environment.NewLine}{Environment.NewLine}"
                logToAnsiConsole Colors.Error $"ex.Message: {ex.Message}{Environment.NewLine}{Environment.NewLine}{ex.StackTrace}"
        }

    /// Updates Watch IPC identity fields without turning live Watch pending or recovery state into a clean shortcut.
    let updateGraceWatchInterprocessFilePreservingLiveWorkState (graceStatus: GraceStatus) (directoryIdsOverride: HashSet<DirectoryVersionId> option) =
        updateGraceWatchInterprocessFilePreservingLiveWorkStateCore ignore graceStatus directoryIdsOverride

    /// Exercises the non-Watch IPC write boundary after a test-controlled live Watch status change.
    let internal updateGraceWatchInterprocessFilePreservingLiveWorkStateAfterInspectionForWatchTests
        beforeWriteBoundary
        (graceStatus: GraceStatus)
        (directoryIdsOverride: HashSet<DirectoryVersionId> option)
        =
        updateGraceWatchInterprocessFilePreservingLiveWorkStateCore beforeWriteBoundary graceStatus directoryIdsOverride

    /// Exercises the gated Watch IPC write boundary after a test-controlled pending-work state change.
    let internal updateGraceWatchInterprocessFileAfterPendingWorkProbeForWatchTests
        beforeStatusCreation
        (graceStatus: GraceStatus)
        (directoryIdsOverride: HashSet<DirectoryVersionId> option)
        =
        task {
            try
                do! graceWatchStatusWriteGate.WaitAsync()

                try
                    beforeStatusCreation ()
                    let newGraceWatchStatus = createGraceWatchStatus graceStatus directoryIdsOverride
                    do! writeGraceWatchStatusWithPersistedModeCore None FileMode.Create newGraceWatchStatus
                finally
                    graceWatchStatusWriteGate.Release() |> ignore
            with
            | ex ->
                logToAnsiConsole Colors.Error $"Exception in updateGraceWatchInterprocessFile."
                logToAnsiConsole Colors.Error $"ex.GetType: {ex.GetType().FullName}{Environment.NewLine}{Environment.NewLine}"
                logToAnsiConsole Colors.Error $"ex.Message: {ex.Message}{Environment.NewLine}{Environment.NewLine}{ex.StackTrace}"
        }

    /// Updates Watch IPC while preserving the suspended mode that cannot be derived from the compact status payload.
    let internal updateGraceWatchInterprocessFileForSuspendedMode (graceStatus: GraceStatus) (directoryIdsOverride: HashSet<DirectoryVersionId> option) =
        updateGraceWatchInterprocessFileCore (Some GraceWatchRuntimeMode.Suspended) None graceStatus directoryIdsOverride

    /// Reads the `grace watch` status inter-process communication file.
    let getGraceWatchStatus () =
        task {
            let! inspection = inspectGraceWatchStatus ()

            // `grace watch` updates the file at least every five minutes to indicate that it's still alive.
            // When `grace watch` exits, the status file is deleted in a try...finally (Program.CLI.fs), so the only
            // circumstance where it would be on-disk without `grace watch` running is if the process were killed.
            // Just to be safe, only return fresh status that contains real root data. A startup claim is only a
            // duplicate-start guard and must not be consumed as a usable repository snapshot.
            if inspection.IsUsable then return inspection.Status else return None // The file is stale, a startup claim, unreadable, or does not contain usable root data.
        }

    /// Atomically claims the `grace watch` status IPC file before foreground watch startup.
    let tryClaimGraceWatchInterprocessFile () =
        task {
            let claimStatus = createGraceWatchStartupClaim ()

            /// Writes claim to existing file data through the CLI output contract.
            let writeClaimToExistingFile (fileStream: FileStream) =
                task {
                    fileStream.SetLength(0L)
                    fileStream.Position <- 0L
                    do! writeGraceWatchStatusContractToStream fileStream claimStatus
                    graceWatchStatusUpdateTime <- claimStatus.UpdatedAt
                }

            try
                do! writeGraceWatchStatus FileMode.CreateNew claimStatus
                return true
            with
            | :? IOException ->
                try
                    use fileStream = new FileStream(IpcFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None)

                    try
                        use reader = new StreamReader(fileStream, Encoding.UTF8, detectEncodingFromByteOrderMarks = true, bufferSize = 4096, leaveOpen = true)
                        let! json = reader.ReadToEndAsync()
                        fileStream.Position <- 0L

                        let existingGraceWatchStatus = deserializeNormalizedGraceWatchStatus json

                        let existingGraceWatchInspection =
                            {
                                Exists = true
                                Status = Some existingGraceWatchStatus
                                PersistedMode = tryReadGraceWatchPersistedMode json
                                SafetyFlags = existingGraceWatchStatus.SafetyFlags
                                ReadError = None
                            }

                        if isGraceWatchStatusFresh existingGraceWatchStatus
                           && existingGraceWatchInspection.HasCurrentRepositoryIdentity then
                            return false
                        else
                            do! writeClaimToExistingFile fileStream
                            return true
                    with
                    | _ ->
                        do! writeClaimToExistingFile fileStream
                        return true
                with
                | :? IOException -> return false

        }

    /// Checks if a file already exists in the object cache.
    let isFileInObjectCache (fileVersion: LocalFileVersion) = task { return File.Exists(fileVersion.FullObjectPath) }

    /// Updates the Grace Status index with new directory versions after getting them from the server.
    let updateGraceStatusWithNewDirectoryVersionsFromServer
        (graceStatus: GraceStatus)
        (newDirectoryVersionDtos: IEnumerable<Grace.Types.DirectoryVersion.DirectoryVersionDto>)
        =
        let newGraceIndex = GraceIndex(graceStatus.Index)
        let mutable dvForDeletions = LocalDirectoryVersion.Default

        if parseResult |> isOutputFormat "Verbose" then
            logToAnsiConsole
                Colors.Verbose
                $"In updateGraceStatusWithNewDirectoryVersionsFromServer: Processing {newDirectoryVersionDtos.Count()} new DirectoryVersions."

        // First, either add the new ones, or replace the existing ones.
        for newDirectoryVersionDto in newDirectoryVersionDtos do
            let newDirectoryVersion = newDirectoryVersionDto.DirectoryVersion

            logToAnsiConsole Colors.Verbose $"Processing new DirectoryVersion: {newDirectoryVersion.RelativePath}."

            let existingDirectoryVersion =
                newGraceIndex.Values.FirstOrDefault((fun dv -> dv.RelativePath = newDirectoryVersion.RelativePath), LocalDirectoryVersion.Default)

            if existingDirectoryVersion.DirectoryVersionId
               <> LocalDirectoryVersion.Default.DirectoryVersionId then
                // We already have an entry with the same RelativePath, so remove the old one and add the new one.
                if parseResult |> isOutputFormat "Verbose" then
                    logToAnsiConsole Colors.Verbose $"Replacing existing DirectoryVersion for path: {newDirectoryVersion.RelativePath}."

                newGraceIndex.TryRemove(existingDirectoryVersion.DirectoryVersionId, &dvForDeletions)
                |> ignore

                if parseResult |> isOutputFormat "Verbose" then
                    logToAnsiConsole Colors.Verbose $"Removed old DirectoryVersion for path: {newDirectoryVersion.RelativePath}."

                newGraceIndex.AddOrUpdate(
                    newDirectoryVersion.DirectoryVersionId,
                    (fun _ -> newDirectoryVersion.ToLocalDirectoryVersion DateTime.UtcNow),
                    (fun _ _ -> newDirectoryVersion.ToLocalDirectoryVersion DateTime.UtcNow)
                )
                |> ignore

                if parseResult |> isOutputFormat "Verbose" then
                    logToAnsiConsole Colors.Verbose $"Added new DirectoryVersion for path: {newDirectoryVersion.RelativePath}."
            else
                // We didn't find the RelativePath, so it's a new DirectoryVersion.
                newGraceIndex.AddOrUpdate(
                    newDirectoryVersion.DirectoryVersionId,
                    (fun _ -> newDirectoryVersion.ToLocalDirectoryVersion DateTime.UtcNow),
                    (fun _ _ -> newDirectoryVersion.ToLocalDirectoryVersion DateTime.UtcNow)
                )
                |> ignore

                if parseResult |> isOutputFormat "Verbose" then
                    logToAnsiConsole Colors.Verbose $"Added new DirectoryVersion for path: {newDirectoryVersion.RelativePath}."

        // Finally, delete any that don't exist anymore.
        // Get the list of the all of the subdirectories referenced in the DirectoryVersions left after replacing existing ones and adding new ones.
        let allSubdirectories =
            HashSet<DirectoryVersionId>(
                newGraceIndex.Values.Select(fun dv -> dv.Directories)
                |> Seq.collect (fun dvs -> dvs)
            )

        let allSubdirectoriesDistinct = allSubdirectories |> Seq.distinct
        // Now check every DirectoryVersion in newGraceIndex to see if it's in the list of subdirectories.
        //   If it's no longer referenced by anyone, that means we can delete it.
        for directoryVersion in newGraceIndex.Values do
            if not
               <| (directoryVersion.RelativePath = Constants.RootDirectoryPath)
               && not
                  <| allSubdirectoriesDistinct.Contains(directoryVersion.DirectoryVersionId) then
                if parseResult |> isOutputFormat "Verbose" then
                    logToAnsiConsole
                        Colors.Verbose
                        $"Removing DirectoryVersion for path: {directoryVersion.RelativePath}; DirectoryVersionId: {directoryVersion.DirectoryVersionId}."

                newGraceIndex.TryRemove(directoryVersion.DirectoryVersionId, &dvForDeletions)
                |> ignore

        let rootDirectoryVersion = newGraceIndex.Values.First(fun dv -> dv.RelativePath = Constants.RootDirectoryPath)

        let newGraceStatus =
            {
                Index = newGraceIndex
                RootDirectoryId = rootDirectoryVersion.DirectoryVersionId
                RootDirectorySha256Hash = rootDirectoryVersion.Sha256Hash
                RootDirectoryBlake3Hash = rootDirectoryVersion.Blake3Hash
                LastSuccessfulDirectoryVersionUpload = graceStatus.LastSuccessfulDirectoryVersionUpload
                LastSuccessfulFileUpload = graceStatus.LastSuccessfulFileUpload
            }

        if parseResult |> isOutputFormat "Verbose" then
            logToAnsiConsole
                Colors.Verbose
                $"In updateGraceStatusWithNewDirectoryVersionsFromServer: Returning new GraceStatus with {newGraceStatus.Index.Count} DirectoryVersions."

        newGraceStatus

    /// Gets the repository-scoped update marker path for a specific local repository identity.
    let internal updateInProgressFileNameForIdentity
        (repositoryId: Guid)
        (repositoryName: string)
        (rootDirectory: string)
        (branchId: Guid)
        (branchName: string)
        =
        let directory = localRepositoryBranchTempDirectoryForIdentity repositoryId repositoryName rootDirectory branchId branchName

        Directory.CreateDirectory(directory) |> ignore

        getNativeFilePath (Path.Combine(directory, Constants.UpdateInProgressFileName))

    /// Gets the file name used to indicate to `grace watch` that updates are in progress from another Grace command, and that it should ignore them.
    let updateInProgressFileName () =
        let current = Current()

        updateInProgressFileNameForIdentity current.RepositoryId current.RepositoryName current.RootDirectory current.BranchId current.BranchName

    /// Describes one working-tree path that a remote directory update is about to replace or remove.
    type WorkingDirectoryTargetMutation = { FullPath: string; IsDirectory: bool; DeletesExistingContent: bool }

    /// Builds a stable key for one working-tree mutation guard decision.
    let private workingDirectoryMutationKey (mutation: WorkingDirectoryTargetMutation) =
        $"{Path.GetFullPath(mutation.FullPath)}|{mutation.IsDirectory}|{mutation.DeletesExistingContent}"

    /// Updates the working directory to match the contents of new DirectoryVersions after checking each mutation target.
    ///
    /// In general, this means copying new and changed files into place, and removing deleted files and directories.
    let internal updateWorkingDirectoryWithTargetGuardAtRoot
        (rootDirectory: string)
        (objectDirectory: string)
        (previousGraceStatus: GraceStatus)
        (updatedGraceStatus: GraceStatus)
        (newDirectoryVersionDtos: IEnumerable<Grace.Types.DirectoryVersion.DirectoryVersionDto>)
        (correlationId: CorrelationId)
        (verifyTargetMutation: WorkingDirectoryTargetMutation -> Task<Result<unit, GraceError>>)
        =
        task {
            let mutable resultError: GraceError option = None
            let newDirectoryVersionDtoArray = newDirectoryVersionDtos |> Seq.toArray
            let rootedWorkingDirectory = Path.GetFullPath(rootDirectory)
            let rootedObjectDirectory = Path.GetFullPath(objectDirectory)

            let mutationKeyComparer =
                if OperatingSystem.IsWindows() then
                    StringComparer.OrdinalIgnoreCase
                else
                    StringComparer.Ordinal

            let preflightedTargets = Dictionary<string, WorkingDirectoryTargetMutation>(mutationKeyComparer)

            let rememberTarget fullPath isDirectory deletesExistingContent =
                let mutation = { FullPath = Path.GetFullPath(fullPath); IsDirectory = isDirectory; DeletesExistingContent = deletesExistingContent }

                preflightedTargets[workingDirectoryMutationKey mutation] <- mutation

            let rootedWorkingPath (relativePath: RelativePath) = getNativeFilePath (Path.Combine(rootedWorkingDirectory, $"{relativePath}"))

            let rootedObjectPath (fileVersion: FileVersion) =
                Path.Combine(
                    rootedObjectDirectory,
                    fileVersion.RelativePath,
                    getLocalObjectCacheFileName fileVersion.RelativePath fileVersion.Sha256Hash fileVersion.Blake3Hash
                )

            let copyObjectFileToWorkingFile objectFilePath workingFilePath =
                try
                    if
                        File.Exists(workingFilePath)
                        || Directory.Exists(workingFilePath)
                    then
                        resultError <-
                            Some(
                                GraceError.Create
                                    $"Working directory update refused to create remote file {workingFilePath} because local content appeared at the target after verification; retry after the tree can be re-evaluated."
                                    correlationId
                            )
                    else
                        Directory.CreateDirectory(Path.GetDirectoryName(workingFilePath))
                        |> ignore

                        File.Copy(objectFilePath, workingFilePath)
                with
                | :? IOException as ex ->
                    resultError <-
                        Some(
                            GraceError.Create
                                $"Working directory update could not copy remote file {workingFilePath}; retry after the tree can be re-evaluated: {ex.Message}"
                                correlationId
                        )
                | :? UnauthorizedAccessException as ex ->
                    resultError <-
                        Some(
                            GraceError.Create
                                $"Working directory update could not copy remote file {workingFilePath}; retry after the tree can be re-evaluated: {ex.Message}"
                                correlationId
                        )

            let directoryMatchesUpdatedSubdirectory
                (updatedGraceStatus: GraceStatus)
                (newDirectoryVersion: LocalDirectoryVersion)
                (subdirectoryInfo: DirectoryInfo)
                =
                let relativeSubdirectoryPath = Path.GetRelativePath(rootedWorkingDirectory, subdirectoryInfo.FullName)

                newDirectoryVersion.Directories
                |> Seq.map (fun directoryVersionId -> updatedGraceStatus.Index[directoryVersionId])
                |> Seq.exists (fun subdirectoryVersion -> subdirectoryVersion.RelativePath = relativeSubdirectoryPath)

            let collectDirectoryTreeDeleteTargets (directoryInfo: DirectoryInfo) =
                let childFiles =
                    directoryInfo.EnumerateFiles("*", SearchOption.AllDirectories)
                    |> Seq.sortByDescending (fun fileInfo -> fileInfo.FullName.Length)
                    |> Seq.toArray

                let childDirectories =
                    directoryInfo.EnumerateDirectories("*", SearchOption.AllDirectories)
                    |> Seq.sortByDescending (fun childDirectoryInfo -> childDirectoryInfo.FullName.Length)
                    |> Seq.toArray
                    |> fun directories -> Array.append directories [| directoryInfo |]

                for fileInfo in childFiles do
                    rememberTarget fileInfo.FullName false true

                for childDirectoryInfo in childDirectories do
                    rememberTarget childDirectoryInfo.FullName true true

            let collectWorkingDirectoryMutationTargets () =
                try
                    for newDirectoryVersionDto in newDirectoryVersionDtoArray do
                        let newDirectoryVersion = newDirectoryVersionDto.DirectoryVersion.ToLocalDirectoryVersion DateTime.UtcNow
                        let newDirectoryFullName = rootedWorkingPath newDirectoryVersion.RelativePath

                        if File.Exists(newDirectoryFullName) then
                            rememberTarget newDirectoryFullName false true
                            rememberTarget newDirectoryFullName true false
                        elif not (Directory.Exists(newDirectoryFullName)) then
                            rememberTarget newDirectoryFullName true false

                        let previousDirectoryVersion =
                            previousGraceStatus.Index.Values.FirstOrDefault(
                                (fun dv -> dv.RelativePath = newDirectoryVersion.RelativePath),
                                LocalDirectoryVersion.Default
                            )

                        let sourceFileVersions = newDirectoryVersionDto.DirectoryVersion.Files.ToArray()

                        let newLocalFileVersions =
                            sourceFileVersions
                            |> Array.map (fun file -> file.ToLocalFileVersion DateTime.UtcNow)

                        for index in 0 .. newLocalFileVersions.Length - 1 do
                            let fileVersion = newLocalFileVersions[index]
                            let existingFileOnDisk = FileInfo(rootedWorkingPath fileVersion.RelativePath)

                            if Directory.Exists(existingFileOnDisk.FullName) then
                                collectDirectoryTreeDeleteTargets (DirectoryInfo(existingFileOnDisk.FullName))
                                rememberTarget existingFileOnDisk.FullName false false
                            elif existingFileOnDisk.Exists then
                                let findFileVersionFromPreviousGraceStatus =
                                    previousDirectoryVersion.Files.Where(fun f -> f.RelativePath = fileVersion.RelativePath)

                                if findFileVersionFromPreviousGraceStatus.Count() > 0 then
                                    let fileVersionFromPreviousGraceStatus = findFileVersionFromPreviousGraceStatus.First()

                                    if existingFileOnDisk.Length <> fileVersion.Size
                                       || fileVersionFromPreviousGraceStatus.Sha256Hash
                                          <> fileVersion.Sha256Hash
                                       || (fileVersionFromPreviousGraceStatus.Blake3Hash
                                           <> Blake3Hash String.Empty
                                           && fileVersion.Blake3Hash <> Blake3Hash String.Empty
                                           && fileVersionFromPreviousGraceStatus.Blake3Hash
                                              <> fileVersion.Blake3Hash) then
                                        rememberTarget existingFileOnDisk.FullName false true
                            else
                                rememberTarget existingFileOnDisk.FullName false false

                        if Directory.Exists(newDirectoryFullName) then
                            let directoryInfo = DirectoryInfo(newDirectoryFullName)

                            for subdirectoryInfo in directoryInfo.EnumerateDirectories().ToArray() do
                                if
                                    not (directoryMatchesUpdatedSubdirectory updatedGraceStatus newDirectoryVersion subdirectoryInfo)
                                    && shouldNotIgnoreDirectory subdirectoryInfo.FullName
                                then
                                    rememberTarget subdirectoryInfo.FullName true true
                                    collectDirectoryTreeDeleteTargets subdirectoryInfo

                            for fileInfo in directoryInfo.EnumerateFiles() do
                                if not
                                   <| newLocalFileVersions.Any(fun fileVersion -> rootedWorkingPath fileVersion.RelativePath = fileInfo.FullName)
                                   && not <| shouldIgnoreFile fileInfo.FullName then
                                    rememberTarget fileInfo.FullName false true
                with
                | :? IOException as ex ->
                    resultError <-
                        Some(
                            GraceError.Create
                                $"Working directory update could not enumerate target mutations before remote writes; retry after the tree can be re-evaluated: {ex.Message}"
                                correlationId
                        )
                | :? UnauthorizedAccessException as ex ->
                    resultError <-
                        Some(
                            GraceError.Create
                                $"Working directory update could not enumerate target mutations before remote writes; retry after the tree can be re-evaluated: {ex.Message}"
                                correlationId
                        )

            let verifyTarget fullPath isDirectory deletesExistingContent =
                task {
                    match resultError with
                    | Some error -> return Error error
                    | None ->
                        let mutation = { FullPath = fullPath; IsDirectory = isDirectory; DeletesExistingContent = deletesExistingContent }

                        match! verifyTargetMutation mutation with
                        | Ok () -> return Ok()
                        | Error error ->
                            resultError <- Some error
                            return Error error
                }

            collectWorkingDirectoryMutationTargets ()

            for mutation in preflightedTargets.Values do
                if resultError.IsNone then
                    match! verifyTarget mutation.FullPath mutation.IsDirectory mutation.DeletesExistingContent with
                    | Ok () -> ()
                    | Error _ -> ()

            /// Deletes a directory tree only after every child reaches the same overwrite boundary as the parent.
            let deleteDirectoryTreeWithTargetGuard (directoryInfo: DirectoryInfo) =
                task {
                    try
                        let childFiles =
                            directoryInfo.EnumerateFiles("*", SearchOption.AllDirectories)
                            |> Seq.sortByDescending (fun fileInfo -> fileInfo.FullName.Length)
                            |> Seq.toArray

                        let childDirectories =
                            directoryInfo.EnumerateDirectories("*", SearchOption.AllDirectories)
                            |> Seq.sortByDescending (fun childDirectoryInfo -> childDirectoryInfo.FullName.Length)
                            |> Seq.toArray
                            |> fun directories -> Array.append directories [| directoryInfo |]

                        let targetsToVerify =
                            [|
                                for fileInfo in childFiles do
                                    fileInfo.FullName, false

                                for childDirectoryInfo in childDirectories do
                                    childDirectoryInfo.FullName, true
                            |]

                        for fullPath, isDirectory in targetsToVerify do
                            if resultError.IsNone then
                                match! verifyTarget fullPath isDirectory true with
                                | Ok () -> ()
                                | Error _ -> ()

                        if resultError.IsNone then
                            for fileInfo in childFiles do
                                if resultError.IsNone then
                                    match! verifyTarget fileInfo.FullName false true with
                                    | Error _ -> ()
                                    | Ok () ->
                                        try
                                            fileInfo.Delete()
                                        with
                                        | :? IOException as ex ->
                                            resultError <-
                                                Some(
                                                    GraceError.Create
                                                        $"Working directory update could not delete verified child file {fileInfo.FullName}; retry after the directory tree can be re-evaluated: {ex.Message}"
                                                        correlationId
                                                )
                                        | :? UnauthorizedAccessException as ex ->
                                            resultError <-
                                                Some(
                                                    GraceError.Create
                                                        $"Working directory update could not delete verified child file {fileInfo.FullName}; retry after the directory tree can be re-evaluated: {ex.Message}"
                                                        correlationId
                                                )

                            for childDirectoryInfo in childDirectories do
                                if resultError.IsNone then
                                    try
                                        childDirectoryInfo.Delete(false)
                                    with
                                    | :? IOException as ex ->
                                        resultError <-
                                            Some(
                                                GraceError.Create
                                                    $"Working directory update could not delete verified directory {childDirectoryInfo.FullName}; retry after the directory tree can be re-evaluated: {ex.Message}"
                                                    correlationId
                                            )
                                    | :? UnauthorizedAccessException as ex ->
                                        resultError <-
                                            Some(
                                                GraceError.Create
                                                    $"Working directory update could not delete verified directory {childDirectoryInfo.FullName}; retry after the directory tree can be re-evaluated: {ex.Message}"
                                                    correlationId
                                            )
                    with
                    | :? IOException as ex ->
                        resultError <-
                            Some(
                                GraceError.Create
                                    $"Working directory update could not enumerate directory {directoryInfo.FullName} before deletion; retry after the directory tree can be re-evaluated: {ex.Message}"
                                    correlationId
                            )
                    | :? UnauthorizedAccessException as ex ->
                        resultError <-
                            Some(
                                GraceError.Create
                                    $"Working directory update could not enumerate directory {directoryInfo.FullName} before deletion; retry after the directory tree can be re-evaluated: {ex.Message}"
                                    correlationId
                            )
                }

            // Loop through each new DirectoryVersion.
            for newDirectoryVersionDto in newDirectoryVersionDtoArray do
                if resultError.IsNone then
                    let newDirectoryVersion = newDirectoryVersionDto.DirectoryVersion
                    let newDirectoryFullName = rootedWorkingPath newDirectoryVersion.RelativePath
                    // Get the previous DirectoryVersion, so we can compare contents below.
                    let previousDirectoryVersion =
                        previousGraceStatus.Index.Values.FirstOrDefault(
                            (fun dv -> dv.RelativePath = newDirectoryVersion.RelativePath),
                            LocalDirectoryVersion.Default
                        )
                    // Ensure that the directory exists on disk.
                    let! directoryCreateGuard =
                        if File.Exists(newDirectoryFullName) then
                            task {
                                match! verifyTarget newDirectoryFullName false true with
                                | Error error -> return Error error
                                | Ok () ->
                                    try
                                        File.Delete(newDirectoryFullName)

                                        if File.Exists(newDirectoryFullName) then
                                            return
                                                Error(
                                                    GraceError.Create
                                                        $"Working directory update could not delete verified file {newDirectoryFullName} before creating a remote directory at the same path; retry after the tree can be re-evaluated: the file still exists after delete."
                                                        correlationId
                                                )
                                        else
                                            return Ok()
                                    with
                                    | :? IOException as ex ->
                                        return
                                            Error(
                                                GraceError.Create
                                                    $"Working directory update could not delete verified file {newDirectoryFullName} before creating a remote directory at the same path; retry after the tree can be re-evaluated: {ex.Message}"
                                                    correlationId
                                            )
                                    | :? UnauthorizedAccessException as ex ->
                                        return
                                            Error(
                                                GraceError.Create
                                                    $"Working directory update could not delete verified file {newDirectoryFullName} before creating a remote directory at the same path; retry after the tree can be re-evaluated: {ex.Message}"
                                                    correlationId
                                            )
                            }
                        elif Directory.Exists(newDirectoryFullName) then
                            Task.FromResult(Ok())
                        else
                            verifyTarget newDirectoryFullName true false

                    let directoryInfo =
                        match directoryCreateGuard with
                        | Ok () ->
                            try
                                let createdDirectory = Directory.CreateDirectory(newDirectoryFullName)

                                if not (Directory.Exists(newDirectoryFullName)) then
                                    resultError <-
                                        Some(
                                            GraceError.Create
                                                $"Working directory update could not create remote directory {newDirectoryFullName}; retry after the tree can be re-evaluated: the directory does not exist after create."
                                                correlationId
                                        )

                                createdDirectory
                            with
                            | :? IOException as ex ->
                                resultError <-
                                    Some(
                                        GraceError.Create
                                            $"Working directory update could not create remote directory {newDirectoryFullName}; retry after the tree can be re-evaluated: {ex.Message}"
                                            correlationId
                                    )

                                DirectoryInfo(newDirectoryFullName)
                            | :? UnauthorizedAccessException as ex ->
                                resultError <-
                                    Some(
                                        GraceError.Create
                                            $"Working directory update could not create remote directory {newDirectoryFullName}; retry after the tree can be re-evaluated: {ex.Message}"
                                            correlationId
                                    )

                                DirectoryInfo(newDirectoryFullName)
                        | Error error ->
                            resultError <- Some error
                            DirectoryInfo(newDirectoryFullName)

                    // Copy new and existing files into place.
                    let sourceFileVersions = newDirectoryVersion.Files.ToArray()

                    let newLocalFileVersions =
                        sourceFileVersions
                        |> Array.map (fun file -> file.ToLocalFileVersion DateTime.UtcNow)

                    for index in 0 .. newLocalFileVersions.Length - 1 do
                        if resultError.IsNone then
                            let sourceFileVersion = sourceFileVersions[index]
                            let fileVersion = newLocalFileVersions[index]
                            let fileFullName = rootedWorkingPath fileVersion.RelativePath
                            let objectFilePath = rootedObjectPath sourceFileVersion
                            let existingFileOnDisk = FileInfo(fileFullName)
                            let objectFile = FileInfo(objectFilePath)

                            if not <| objectFile.Exists then
                                let currentObjectDirectory = Path.GetFullPath(Current().ObjectDirectory)

                                let objectDirectoryComparison =
                                    if OperatingSystem.IsWindows() then
                                        StringComparison.OrdinalIgnoreCase
                                    else
                                        StringComparison.Ordinal

                                if not (String.Equals(currentObjectDirectory, rootedObjectDirectory, objectDirectoryComparison)) then
                                    resultError <-
                                        Some(
                                            GraceError.Create
                                                $"Working directory update refused to download missing object {objectFilePath} because the active object cache changed before fallback download; retry after the tree can be re-evaluated."
                                                correlationId
                                        )

                                // This is an error. There _should_ be a file in the object cache for every file in each DirectoryVersion.
                                //   Anyway, we'll just download it from the server (again).
                                if resultError.IsNone then
                                    let getDownloadUriParameters =
                                        Storage.GetDownloadUriParameters(
                                            OwnerId = $"{Current().OwnerId}",
                                            OwnerName = Current().OwnerName,
                                            OrganizationId = $"{Current().OrganizationId}",
                                            OrganizationName = Current().OrganizationName,
                                            RepositoryId = $"{Current().RepositoryId}",
                                            RepositoryName = Current().RepositoryName,
                                            CorrelationId = correlationId
                                        )

                                    match! downloadFileVersionsForWorkingDirectoryUpdate getDownloadUriParameters [| sourceFileVersion |] correlationId with
                                    | Ok _ ->
                                        objectFile.Refresh()

                                        if objectFile.Exists then
                                            logToAnsiConsole Colors.Verbose $"Downloaded {objectFilePath} from the object storage provider."
                                        else
                                            resultError <-
                                                Some(
                                                    GraceError.Create
                                                        $"Working directory update fallback download did not populate captured object cache path {objectFilePath}; retry after the tree can be re-evaluated."
                                                        correlationId
                                                )
                                    | Error error ->
                                        logToAnsiConsole
                                            Colors.Error
                                            $"An error occurred while downloading a file from the object storage provider. CorrelationId: {correlationId}."

                                        logToAnsiConsole Colors.Error $"{error}"
                                        resultError <- Some(GraceError.Create $"{error}" correlationId)

                            if
                                resultError.IsNone
                                && Directory.Exists(existingFileOnDisk.FullName)
                            then
                                match! verifyTarget existingFileOnDisk.FullName true true with
                                | Error _ -> ()
                                | Ok () ->
                                    do! deleteDirectoryTreeWithTargetGuard (DirectoryInfo(existingFileOnDisk.FullName))

                                    if resultError.IsNone then
                                        match! verifyTarget existingFileOnDisk.FullName false false with
                                        | Ok () -> copyObjectFileToWorkingFile objectFilePath fileFullName
                                        | Error _ -> ()
                            elif resultError.IsNone && existingFileOnDisk.Exists then
                                // Need to compare existing file to new version from the object cache.
                                let findFileVersionFromPreviousGraceStatus =
                                    previousDirectoryVersion.Files.Where(fun f -> f.RelativePath = fileVersion.RelativePath)

                                if findFileVersionFromPreviousGraceStatus.Count() > 0 then
                                    let fileVersionFromPreviousGraceStatus = findFileVersionFromPreviousGraceStatus.First()
                                    // If the length or content identity changes in the new version, we'll delete the
                                    //   file in the working directory, and copy the version from the object cache to replace it.
                                    if existingFileOnDisk.Length <> fileVersion.Size
                                       || fileVersionFromPreviousGraceStatus.Sha256Hash
                                          <> fileVersion.Sha256Hash
                                       || (fileVersionFromPreviousGraceStatus.Blake3Hash
                                           <> Blake3Hash String.Empty
                                           && fileVersion.Blake3Hash <> Blake3Hash String.Empty
                                           && fileVersionFromPreviousGraceStatus.Blake3Hash
                                              <> fileVersion.Blake3Hash) then
                                        //logToAnsiConsole
                                        //    Colors.Verbose
                                        //    $"Replacing {fileVersion.FullName}; previous length: {fileVersionFromPreviousGraceStatus.Size}; new length: {fileVersion.Size}."

                                        match! verifyTarget existingFileOnDisk.FullName false true with
                                        | Ok () ->
                                            existingFileOnDisk.Delete()
                                            copyObjectFileToWorkingFile objectFilePath fileFullName
                                        | Error _ -> ()
                                else
                                    resultError <-
                                        Some(
                                            GraceError.Create
                                                $"Working directory update refused to create remote file {fileFullName} because local content appeared at the target after preflight; retry after the tree can be re-evaluated."
                                                correlationId
                                        )
                            else if resultError.IsNone then
                                // No existing file, so just copy it into place.
                                //logToAnsiConsole Colors.Verbose $"Copying file {fileVersion.FullName} from object cache; no existing file."
                                let trackedDirectoryFromPreviousGraceStatus =
                                    previousGraceStatus.Index.Values
                                    |> Seq.tryFind (fun directoryVersion -> directoryVersion.RelativePath = fileVersion.RelativePath)

                                match trackedDirectoryFromPreviousGraceStatus with
                                | Some _ ->
                                    resultError <-
                                        Some(
                                            GraceError.Create
                                                $"Working directory update refused to recreate remote file {fileFullName} because a tracked directory at the same path was deleted locally after preflight; retry after the tree can be re-evaluated."
                                                correlationId
                                        )
                                | None ->
                                    match! verifyTarget existingFileOnDisk.FullName false false with
                                    | Ok () -> copyObjectFileToWorkingFile objectFilePath fileFullName
                                    | Error _ -> ()

                    // Delete unnecessary directories.
                    // Get DirectoryVersions for the subdirectories of the new DirectoryVersion.
                    //logToAnsiConsole
                    //    Colors.Verbose
                    //    $"Services.CLI.fs: updateWorkingDirectory(): {Markup.Escape(serialize (updatedGraceStatus.Index.Select(fun x -> x.Value.DirectoryVersionId)))}"

                    //logToAnsiConsole
                    //    Colors.Verbose
                    //    $"Services.CLI.fs: updateWorkingDirectory(): {Markup.Escape(serialize (newDirectoryVersions.Select(fun x -> x.DirectoryVersionId)))}"

                    //let previousDirectoryIds = previousGraceStatus.Index.Values.Select(fun dv -> (dv.DirectoryVersionId, dv.RelativePath))
                    //let updatedDirectoryIds = updatedGraceStatus.Index.Values.Select(fun dv -> (dv.DirectoryVersionId, dv.RelativePath))
                    //logToAnsiConsole Colors.Verbose $"{serialize previousDirectoryIds}"
                    //logToAnsiConsole Colors.Verbose $"{serialize updatedDirectoryIds}"

                    let subdirectoryVersions = newDirectoryVersion.Directories.Select(fun directoryVersionId -> updatedGraceStatus.Index[directoryVersionId])
                    // Loop through the actual subdirectories on disk.
                    if resultError.IsNone then
                        for subdirectoryInfo in directoryInfo.EnumerateDirectories().ToArray() do
                            // If we don't have this subdirectory listed in new parent DirectoryVersion, and it's a directory that we shouldn't ignore,
                            //    that means that it was deleted, and we should delete it from the working directory.
                            let relativeSubdirectoryPath = Path.GetRelativePath(rootedWorkingDirectory, subdirectoryInfo.FullName)

                            if not
                               <| (subdirectoryVersions
                                   |> Seq.exists (fun subdirectoryVersion -> subdirectoryVersion.RelativePath = relativeSubdirectoryPath))
                               && shouldNotIgnoreDirectory subdirectoryInfo.FullName then
                                //logToAnsiConsole Colors.Verbose $"Deleting directory {subdirectoryInfo.FullName}."
                                match! verifyTarget subdirectoryInfo.FullName true true with
                                | Ok () -> do! deleteDirectoryTreeWithTargetGuard subdirectoryInfo
                                | Error _ -> ()

                    // Delete unnecessary files.
                    // Loop through the actual files on disk.
                    if resultError.IsNone then
                        for fileInfo in directoryInfo.EnumerateFiles() do
                            // If we don't have this file in the new version of the directory, and it's a file that we shouldn't ignore,
                            //   that means that it was deleted, and we should delete it from the working directory.
                            // Ignored files get... ignored.
                            if not
                               <| newLocalFileVersions.Any(fun fileVersion -> rootedWorkingPath fileVersion.RelativePath = fileInfo.FullName)
                               && not
                                  <| (subdirectoryVersions
                                      |> Seq.exists (fun subdirectoryVersion ->
                                          subdirectoryVersion.RelativePath = Path.GetRelativePath(rootedWorkingDirectory, fileInfo.FullName)))
                               && not <| shouldIgnoreFile fileInfo.FullName then
                                //logToAnsiConsole Colors.Verbose $"Deleting file {fileInfo.FullName}."
                                match! verifyTarget fileInfo.FullName false true with
                                | Ok () -> fileInfo.Delete()
                                | Error _ -> ()

            match resultError with
            | Some error -> return Error error
            | None -> return Ok()
        }

    /// Updates the working directory to match remote DirectoryVersions while guarding each mutation target.
    let updateWorkingDirectoryWithTargetGuard previousGraceStatus updatedGraceStatus newDirectoryVersionDtos correlationId verifyTargetMutation =
        let current = Current()

        updateWorkingDirectoryWithTargetGuardAtRoot
            current.RootDirectory
            current.ObjectDirectory
            previousGraceStatus
            updatedGraceStatus
            newDirectoryVersionDtos
            correlationId
            verifyTargetMutation

    /// Updates the working directory to match the contents of new DirectoryVersions.
    ///
    /// In general, this means copying new and changed files into place, and removing deleted files and directories.
    let updateWorkingDirectory previousGraceStatus updatedGraceStatus newDirectoryVersionDtos correlationId =
        task {
            match!
                updateWorkingDirectoryWithTargetGuard previousGraceStatus updatedGraceStatus newDirectoryVersionDtos correlationId (fun _ ->
                    Task.FromResult(Ok()))
                with
            | Ok () -> ()
            | Error error -> return raise (InvalidOperationException(error.Error))
        }

    /// Sends the current save message to the SDK and records the resulting Grace reference.
    let createSaveReference (rootDirectoryVersion: LocalDirectoryVersion) message correlationId =
        task {
            //Activity.Current <- new Activity("createSaveReference")
            let createReferenceParameters =
                Parameters.Branch.CreateReferenceParameters(
                    OwnerId = $"{Current().OwnerId}",
                    OrganizationId = $"{Current().OrganizationId}",
                    RepositoryId = $"{Current().RepositoryId}",
                    BranchId = $"{Current().BranchId}",
                    CorrelationId = correlationId,
                    DirectoryVersionId = rootDirectoryVersion.DirectoryVersionId,
                    Sha256Hash = rootDirectoryVersion.Sha256Hash,
                    Message = message
                )

            let! result = Branch.Save createReferenceParameters
            //Activity.Current.SetTag("CorrelationId", correlationId) |> ignore
            match result with
            | Ok returnValue ->
                //logToAnsiConsole Colors.Verbose $"Created a save in branch {Current().BranchName}. Sha256Hash: {rootDirectoryVersion.Sha256Hash.Substring(0, 8)}. CorrelationId: {returnValue.CorrelationId}."
                //Activity.Current.AddTag("Created Save reference", "true")
                //                .SetStatus(ActivityStatusCode.Ok, returnValue.ReturnValue) |> ignore
                ()
            | Error error ->
                logToAnsiConsole
                    Colors.Error
                    $"An error occurred while creating a save that contains the current differences. CorrelationId: {error.CorrelationId}."
            //Activity.Current.AddTag("Created Save reference", "false")
            //                .AddTag("Server path", error.Properties["Path"])
            //                .SetStatus(ActivityStatusCode.Error, error.Error) |> ignore
            //Activity.Current.Dispose()
            return result
        }

    /// Models cli current state capture source values passed between the parser and services handlers.
    type CliCurrentStateCaptureSource =
        | ExplicitReference
        | ExistingReference
        | GraceWatch
        | CreatedSave

    /// Models cli current state capture result values passed between the parser and services handlers.
    type CliCurrentStateCaptureResult =
        {
            TargetReferenceId: ReferenceId
            RootDirectoryId: DirectoryVersionId
            RootDirectorySha256Hash: Sha256Hash
            RootDirectoryBlake3Hash: Blake3Hash
            Source: CliCurrentStateCaptureSource
            CreatedSaveMessage: string option
        }

    /// Models cli current state capture operations values passed between the parser and services handlers.
    type CliCurrentStateCaptureOperations =
        {
            GetBranch: unit -> Task<GraceResult<BranchDto>>
            GetGraceWatchStatus: unit -> Task<GraceWatchStatus option>
            ReadGraceStatus: unit -> Task<GraceStatus>
            ScanForDifferences: GraceStatus -> Task<List<FileSystemDifference>>
            CopyUpdatedFilesToObjectCache: List<FileSystemDifference> -> Task<seq<LocalFileVersion>>
            BuildUpdatedGraceStatus: GraceStatus -> List<FileSystemDifference> -> Task<GraceStatus * List<LocalDirectoryVersion>>
            UploadFileVersions: seq<LocalFileVersion> -> Task<Result<FileVersion array, GraceError>>
            GetSavedDirectoryVersions: GraceStatus -> Task<Result<DirectoryVersion array, GraceError>>
            UploadDirectoryVersions: List<DirectoryVersion> -> Task<Result<unit, GraceError>>
            ApplyGraceStatusIncremental: GraceStatus -> IEnumerable<LocalDirectoryVersion> -> IEnumerable<FileSystemDifference> -> Task<unit>
            CreateSaveReference: LocalDirectoryVersion -> string -> Task<Result<ReferenceId, GraceError>>
        }

    /// Checks whether reference has root on branch is true for the parsed command input.
    let private referenceHasRootOnBranch branchId rootDirectoryId rootDirectorySha256Hash rootDirectoryBlake3Hash (referenceDto: ReferenceDto) =
        referenceDto.ReferenceId <> ReferenceId.Empty
        && referenceDto.BranchId = branchId
        && referenceDto.DirectoryId = rootDirectoryId
        && referenceDto.Sha256Hash = rootDirectorySha256Hash
        && localUnknownOrSameBlake3MatchesTrusted referenceDto.Blake3Hash rootDirectoryBlake3Hash

    /// Tries to map find reference for root and returns a GraceError instead of throwing on unsupported input.
    let private tryFindReferenceForRoot rootDirectoryId rootDirectorySha256Hash rootDirectoryBlake3Hash (branchDto: BranchDto) =
        [
            branchDto.LatestReference
            branchDto.LatestSave
            branchDto.LatestCommit
            branchDto.LatestCheckpoint
            branchDto.LatestPromotion
        ]
        |> Seq.tryFind (referenceHasRootOnBranch branchDto.BranchId rootDirectoryId rootDirectorySha256Hash rootDirectoryBlake3Hash)

    /// Reads changed file versions referenced by updated directories from ParseResult, local configuration, or Grace ids.
    let internal getChangedFileVersionsReferencedByUpdatedDirectories
        (differences: List<FileSystemDifference>)
        (directoryVersions: List<LocalDirectoryVersion>)
        =
        let changedFilePaths = HashSet<RelativePath>()

        differences
        |> Seq.iter (fun difference ->
            match difference.DifferenceType, difference.FileSystemEntryType with
            | (DifferenceType.Add
              | DifferenceType.Change),
              FileSystemEntryType.File ->
                changedFilePaths.Add(difference.RelativePath)
                |> ignore
            | _ -> ())

        directoryVersions
        |> Seq.collect (fun directoryVersion -> directoryVersion.Files)
        |> Seq.filter (fun fileVersion -> changedFilePaths.Contains fileVersion.RelativePath)
        |> Seq.distinctBy (fun fileVersion -> fileVersion.RelativePath, fileVersion.Sha256Hash, fileVersion.Blake3Hash)
        |> Seq.toList

    /// Updates CLI authentication state for save disabled error while keeping token handling centralized.
    let private saveDisabledError correlationId =
        GraceError.Create "Save is disabled on this branch, and local changes require a Save before branch annotation can continue." correlationId

    /// Summarizes the files captured by Grace Watch and the save reference created for them.
    let private createCaptureResult source createdSaveMessage rootDirectoryId rootDirectorySha256Hash rootDirectoryBlake3Hash targetReferenceId =
        {
            TargetReferenceId = targetReferenceId
            RootDirectoryId = rootDirectoryId
            RootDirectorySha256Hash = rootDirectorySha256Hash
            RootDirectoryBlake3Hash = rootDirectoryBlake3Hash
            Source = source
            CreatedSaveMessage = createdSaveMessage
        }

    /// Coordinates trusted root blake3 for capture behavior for this CLI command path.
    let private trustedRootBlake3ForCapture localRootBlake3 (referenceDto: ReferenceDto) =
        if String.IsNullOrWhiteSpace(string localRootBlake3) then
            referenceDto.Blake3Hash
        else
            localRootBlake3

    /// Reads sync grace status with uploaded directory versions data needed by the command workflow without changing remote state.
    let private syncGraceStatusWithUploadedDirectoryVersions
        (updatedGraceStatus: GraceStatus)
        (newDirectoryVersions: List<LocalDirectoryVersion>)
        (uploadedDirectoryVersions: List<DirectoryVersion>)
        =
        let localDirectoryVersionsById = Dictionary<DirectoryVersionId, LocalDirectoryVersion>()

        for localDirectoryVersion in newDirectoryVersions do
            localDirectoryVersionsById[localDirectoryVersion.DirectoryVersionId] <- localDirectoryVersion

        let syncedDirectoryVersions =
            uploadedDirectoryVersions
            |> Seq.map (fun uploadedDirectoryVersion ->
                let mutable localDirectoryVersion = Unchecked.defaultof<LocalDirectoryVersion>

                let lastWriteTimeUtc =
                    if localDirectoryVersionsById.TryGetValue(uploadedDirectoryVersion.DirectoryVersionId, &localDirectoryVersion) then
                        localDirectoryVersion.LastWriteTimeUtc
                    else
                        DateTime.UtcNow

                uploadedDirectoryVersion.ToLocalDirectoryVersion lastWriteTimeUtc)
            |> Seq.toList

        let syncedIndex = GraceIndex()

        for kvp in updatedGraceStatus.Index do
            syncedIndex.TryAdd(kvp.Key, kvp.Value) |> ignore

        for localDirectoryVersion in syncedDirectoryVersions do
            syncedIndex[localDirectoryVersion.DirectoryVersionId] <- localDirectoryVersion

        let mutable syncedRootDirectoryVersion = Unchecked.defaultof<LocalDirectoryVersion>

        let rootDirectorySha256Hash =
            if syncedIndex.TryGetValue(updatedGraceStatus.RootDirectoryId, &syncedRootDirectoryVersion) then
                syncedRootDirectoryVersion.Sha256Hash
            else
                updatedGraceStatus.RootDirectorySha256Hash

        let rootDirectoryBlake3Hash =
            if syncedIndex.TryGetValue(updatedGraceStatus.RootDirectoryId, &syncedRootDirectoryVersion) then
                syncedRootDirectoryVersion.Blake3Hash
            else
                updatedGraceStatus.RootDirectoryBlake3Hash

        { updatedGraceStatus with Index = syncedIndex; RootDirectorySha256Hash = rootDirectorySha256Hash; RootDirectoryBlake3Hash = rootDirectoryBlake3Hash },
        List<LocalDirectoryVersion>(syncedDirectoryVersions)

    /// Captures the current repository root into a Grace save reference for watch-driven persistence.
    let private createSaveForCurrentRoot operations branchDto saveMessage correlationId rootDirectoryVersion =
        task {
            if not branchDto.SaveEnabled then
                return Error(saveDisabledError correlationId)
            else
                match! operations.CreateSaveReference rootDirectoryVersion saveMessage with
                | Ok referenceId ->
                    return
                        Ok(
                            createCaptureResult
                                CreatedSave
                                (Some saveMessage)
                                rootDirectoryVersion.DirectoryVersionId
                                rootDirectoryVersion.Sha256Hash
                                rootDirectoryVersion.Blake3Hash
                                referenceId
                        )
                | Error error -> return Error error
        }

    /// Resolves cli current state target reference from command options, configuration, or local state.
    let resolveCliCurrentStateTargetReference operations (explicitReferenceId: ReferenceId option) (saveMessage: string) (correlationId: CorrelationId) =
        task {
            match explicitReferenceId with
            | Some referenceId when referenceId <> ReferenceId.Empty ->
                return Ok(createCaptureResult ExplicitReference None DirectoryVersionId.Empty (Sha256Hash String.Empty) (Blake3Hash String.Empty) referenceId)
            | _ ->
                match! operations.GetBranch() with
                | Error error -> return Error error
                | Ok branchReturnValue ->
                    let branchDto = branchReturnValue.ReturnValue

                    match! operations.GetGraceWatchStatus() with
                    | Some graceWatchStatus ->
                        match
                            tryFindReferenceForRoot
                                graceWatchStatus.RootDirectoryId
                                graceWatchStatus.RootDirectorySha256Hash
                                graceWatchStatus.RootDirectoryBlake3Hash
                                branchDto
                            with
                        | Some referenceDto ->
                            return
                                Ok(
                                    createCaptureResult
                                        GraceWatch
                                        None
                                        graceWatchStatus.RootDirectoryId
                                        graceWatchStatus.RootDirectorySha256Hash
                                        (trustedRootBlake3ForCapture graceWatchStatus.RootDirectoryBlake3Hash referenceDto)
                                        referenceDto.ReferenceId
                                )
                        | None ->
                            let rootDirectoryVersion =
                                LocalDirectoryVersion.CreateWithHashes
                                    graceWatchStatus.RootDirectoryId
                                    (Current().OwnerId)
                                    (Current().OrganizationId)
                                    (Current().RepositoryId)
                                    Constants.RootDirectoryPath
                                    graceWatchStatus.RootDirectorySha256Hash
                                    graceWatchStatus.RootDirectoryBlake3Hash
                                    (List<DirectoryVersionId>())
                                    (List<LocalFileVersion>())
                                    0L
                                    DateTime.UtcNow

                            return! createSaveForCurrentRoot operations branchDto saveMessage correlationId rootDirectoryVersion
                    | None ->
                        let! previousGraceStatus = operations.ReadGraceStatus()
                        let! differences = operations.ScanForDifferences previousGraceStatus

                        if differences.Count = 0 then
                            match
                                tryFindReferenceForRoot
                                    previousGraceStatus.RootDirectoryId
                                    previousGraceStatus.RootDirectorySha256Hash
                                    previousGraceStatus.RootDirectoryBlake3Hash
                                    branchDto
                                with
                            | Some referenceDto ->
                                return
                                    Ok(
                                        createCaptureResult
                                            ExistingReference
                                            None
                                            previousGraceStatus.RootDirectoryId
                                            previousGraceStatus.RootDirectorySha256Hash
                                            (trustedRootBlake3ForCapture previousGraceStatus.RootDirectoryBlake3Hash referenceDto)
                                            referenceDto.ReferenceId
                                    )
                            | None ->
                                return! createSaveForCurrentRoot operations branchDto saveMessage correlationId (getRootDirectoryVersion previousGraceStatus)
                        elif not branchDto.SaveEnabled then
                            return Error(saveDisabledError correlationId)
                        else
                            let! _ = operations.CopyUpdatedFilesToObjectCache differences
                            let! (updatedGraceStatus, newDirectoryVersions) = operations.BuildUpdatedGraceStatus previousGraceStatus differences
                            let fileVersionsToUpload = getChangedFileVersionsReferencedByUpdatedDirectories differences newDirectoryVersions

                            match! operations.UploadFileVersions fileVersionsToUpload with
                            | Error error -> return Error error
                            | Ok uploadedFileVersions ->
                                match! operations.GetSavedDirectoryVersions previousGraceStatus with
                                | Error error -> return Error error
                                | Ok savedDirectoryVersions ->
                                    let uploadedDirectoryVersions =
                                        applyUploadedFileVersionsToDirectoryVersionsWithSavedDirectoryVersions
                                            uploadedFileVersions
                                            savedDirectoryVersions
                                            newDirectoryVersions

                                    match! operations.UploadDirectoryVersions uploadedDirectoryVersions with
                                    | Error error -> return Error error
                                    | Ok () ->
                                        let updatedGraceStatus, newDirectoryVersions =
                                            syncGraceStatusWithUploadedDirectoryVersions updatedGraceStatus newDirectoryVersions uploadedDirectoryVersions

                                        let updatedGraceStatus =
                                            { updatedGraceStatus with
                                                LastSuccessfulFileUpload = getCurrentInstant ()
                                                LastSuccessfulDirectoryVersionUpload = getCurrentInstant ()
                                            }

                                        let rootDirectoryVersion = getRootDirectoryVersion updatedGraceStatus

                                        match! createSaveForCurrentRoot operations branchDto saveMessage correlationId rootDirectoryVersion with
                                        | Error error -> return Error error
                                        | Ok captured ->
                                            do! operations.ApplyGraceStatusIncremental updatedGraceStatus newDirectoryVersions differences
                                            return Ok captured
        }

    /// Parses command input into typed values.
    let parseCreatedReferenceId correlationId (returnValue: GraceReturnValue<string>) =
        let mutable referenceIdProperty = Unchecked.defaultof<obj>

        if returnValue.Properties.TryGetValue(nameof ReferenceId, &referenceIdProperty) then
            match Guid.TryParse(string referenceIdProperty) with
            | true, referenceId -> Ok referenceId
            | false, _ -> Error(GraceError.Create $"Save reference creation returned an invalid ReferenceId property: {referenceIdProperty}." correlationId)
        else
            Error(GraceError.Create "Save reference creation did not return a ReferenceId property." correlationId)

    /// Generates a temporary file name within the ObjectDirectory, and returns the full file path.
    /// This file name will be used to copy modified files into before renaming them with their proper names and SHA256 values.
    let getTemporaryFilePath () =
        let tempDirectory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "Grace", Current().BranchName))

        Path.GetFullPath(Path.Combine(tempDirectory.FullName, $"{Path.GetRandomFileName()}.gracetmp"))

    /// Copies a file to the Object Directory, and returns a new FileVersion. The SHA-256 hash is computed and included in the object file name.
    let copyToObjectDirectory (filePath: FilePath) : Task<FileVersion option> =
        task {
            try
                if File.Exists(filePath) then
                    // First, capture the file by copying it to a temp name
                    let tempFilePath = getTemporaryFilePath ()
                    //logToConsole $"filePath: {filePath}; tempFilePath: {tempFilePath}"
                    let mutable iteration = 0

                    Constants.DefaultFileCopyRetryPolicy.Execute (fun () ->
                        //iteration <- iteration + 1
                        //logToAnsiConsole Colors.Deemphasized $"Attempt #{iteration} to copy file to object directory..."
                        File.Copy(sourceFileName = filePath, destFileName = tempFilePath, overwrite = true))

                    // Now that we've copied it, compute the SHA-256 hash.
                    let relativeFilePath = Path.GetRelativePath(Current().RootDirectory, filePath)

                    use tempFileStream = File.Open(tempFilePath, fileStreamOptionsRead)

                    let! isBinary = isBinaryFile tempFileStream
                    tempFileStream.Position <- 0
                    let! sha256Hash = computeSha256ForFile tempFileStream relativeFilePath
                    tempFileStream.Position <- 0
                    let! fileContentHash = computeBlake3ForFile tempFileStream
                    let blake3Hash = Blake3Hash $"{fileContentHash}"
                    //logToConsole $"filePath: {filePath}; tempFilePath: {tempFilePath}; SHA256: {sha256Hash}"

                    // I'm going to rename the temp file below, using the SHA-256 hash, so I'll close the file and dispose the stream first.
                    do! tempFileStream.DisposeAsync()

                    // Get the new name for this version of the file, including the SHA-256 hash.
                    let relativeDirectoryPath = getLocalRelativeDirectory filePath (Current().RootDirectory)

                    let objectFileName = getLocalObjectCacheFileName (RelativePath relativeFilePath) (Sha256Hash $"{sha256Hash}") blake3Hash

                    let objectDirectoryPath = Path.Combine(Current().ObjectDirectory, relativeDirectoryPath)

                    let objectFilePath = Path.Combine(objectDirectoryPath, objectFileName)
                    //logToConsole $"relativeDirectoryPath: {relativeDirectoryPath}; objectFileName: {objectFileName}; objectFilePath: {objectFilePath}"

                    /// Converts a filesystem entry into the file-version payload sent during save capture.
                    let createFileVersion (objectFilePathInfo: FileInfo) =
                        FileVersion.CreateWithHashes
                            (RelativePath relativeFilePath)
                            (Sha256Hash $"{sha256Hash}")
                            blake3Hash
                            String.Empty
                            isBinary
                            objectFilePathInfo.Length

                    // If we don't already have this file, with this exact SHA256, make sure the directory exists,
                    //   and rename the temp file to the proper SHA256-enhanced name of the file.
                    if not (File.Exists(objectFilePath)) then
                        //logToConsole $"Before moving temp file to object storage..."
                        Directory.CreateDirectory(objectDirectoryPath)
                        |> ignore // No-op if the directory already exists

                        File.Move(tempFilePath, objectFilePath)
                        //logToConsole $"After moving temp file to object storage..."
                        let objectFilePathInfo = FileInfo(objectFilePath)
                        //logToConsole $"After creating FileInfo; Exists: {objectFilePathInfo.Exists}; FullName = {objectFilePathInfo.FullName}..."
                        //logToConsole $"Finished copyToObjectDirectory for {filePath}; isBinary: {isBinary}; moved temp file to object directory."
                        return Some(createFileVersion objectFilePathInfo)
                    else
                        use objectFileStream = File.Open(objectFilePath, fileStreamOptionsRead)
                        let! existingFileContentHash = computeBlake3ForFile objectFileStream
                        let existingBlake3Hash = Blake3Hash $"{existingFileContentHash}"
                        do! objectFileStream.DisposeAsync()

                        if existingBlake3Hash <> blake3Hash then
                            File.Move(tempFilePath, objectFilePath, true)
                            let objectFilePathInfo = FileInfo(objectFilePath)
                            return Some(createFileVersion objectFilePathInfo)
                        else
                            // The object already exists with matching SHA-256 and BLAKE3. Keep returning a version so
                            // cache-hit changed files remain available to manifest upload/overlay callers.
                            File.Delete(tempFilePath)
                            let objectFilePathInfo = FileInfo(objectFilePath)
                            return Some(createFileVersion objectFilePathInfo)
                //return result
                else
                    logToAnsiConsole Colors.Error $"File {filePath} does not exist."
                    return None
            with
            | ex ->
                logToAnsiConsole Colors.Error $"Exception in copyToObjectDirectory: {ExceptionResponse.Create ex}"
                return None
        }

    /// Coordinates copy updated files to object cache core behavior for this CLI command path.
    let private copyUpdatedFilesToObjectCacheCore (progressTask: ProgressTask option) (differences: List<FileSystemDifference>) =
        task {
            // Get the list of files that have been added or changed.
            let relativePathsOfUpdatedFiles =
                differences
                    .Select(fun difference ->
                        match difference.DifferenceType with
                        | Add ->
                            match difference.FileSystemEntryType with
                            | FileSystemEntryType.File -> Some difference.RelativePath
                            | FileSystemEntryType.Directory -> None
                        | Change ->
                            match difference.FileSystemEntryType with
                            | FileSystemEntryType.File -> Some difference.RelativePath
                            | FileSystemEntryType.Directory -> None
                        | Delete -> None)
                    .Where(fun relativePathOption -> relativePathOption.IsSome)
                    .Select(fun relativePath -> relativePath.Value)
            //logToAnsiConsole Colors.Verbose $"relativePathsOfUpdatedFiles: {serialize relativePathsOfUpdatedFiles}"

            // Create new LocalFileVersion instances for each updated file.
            let increment =
                match progressTask with
                | Some t when differences.Count > 0 -> (100.0 - t.Value) / float differences.Count
                | _ -> 0.0

            let newFileVersions = ConcurrentQueue<LocalFileVersion>()

            do!
                Parallel.ForEachAsync(
                    relativePathsOfUpdatedFiles,
                    Constants.ParallelOptions,
                    (fun relativePath continuationToken ->
                        ValueTask(
                            task {
                                //logToAnsiConsole Colors.Verbose $"In Services.CLI.copyToObjectDirectory: Copying {relativePath} to object storage."
                                match! copyToObjectDirectory (Path.Combine(Current().RootDirectory, relativePath)) with
                                | Some fileVersion -> newFileVersions.Enqueue(fileVersion.ToLocalFileVersion(DateTime.UtcNow))
                                //logToAnsiConsole Colors.Verbose $"Copied {fileVersion.RelativePath} to {fileVersion.GetObjectFileName} in object storage."
                                | None ->
                                    logToAnsiConsole Colors.Error $"Failed to copy {relativePath} to object storage."
                                    ()

                                match progressTask with
                                | Some t -> t.Increment(increment)
                                | None -> ()
                            }
                        ))
                )

            return newFileVersions
        }

    /// Copies new and updated files found in a list of FileSystemDifferences to the object directory.
    let copyUpdatedFilesToObjectCache (t: ProgressTask) (differences: List<FileSystemDifference>) = copyUpdatedFilesToObjectCacheCore (Some t) differences

    /// Coordinates copy updated files to object cache without progress behavior for this CLI command path.
    let copyUpdatedFilesToObjectCacheWithoutProgress (differences: List<FileSystemDifference>) = copyUpdatedFilesToObjectCacheCore None differences

    /// Coordinates default cli current state capture operations behavior for this CLI command path.
    let defaultCliCurrentStateCaptureOperations (correlationId: CorrelationId) =
        /// Reads branch from ParseResult, local configuration, or Grace ids.
        let getBranch () =
            let current = Current()

            let parameters =
                GetBranchParameters(
                    OwnerId = $"{current.OwnerId}",
                    OwnerName = current.OwnerName,
                    OrganizationId = $"{current.OrganizationId}",
                    OrganizationName = current.OrganizationName,
                    RepositoryId = $"{current.RepositoryId}",
                    RepositoryName = current.RepositoryName,
                    BranchId = $"{current.BranchId}",
                    BranchName = current.BranchName,
                    CorrelationId = correlationId
                )

            Grace.SDK.Branch.Get parameters

        /// Reads upload file versions data needed by the command workflow without changing remote state.
        let uploadFileVersions (fileVersions: seq<LocalFileVersion>) =
            task {
                let current = Current()

                let parameters =
                    GetUploadMetadataForFilesParameters(
                        OwnerId = $"{current.OwnerId}",
                        OwnerName = current.OwnerName,
                        OrganizationId = $"{current.OrganizationId}",
                        OrganizationName = current.OrganizationName,
                        RepositoryId = $"{current.RepositoryId}",
                        RepositoryName = current.RepositoryName,
                        CorrelationId = correlationId,
                        FileVersions =
                            (fileVersions
                             |> Seq.map (fun localFileVersion -> localFileVersion.ToFileVersion)
                             |> Seq.toArray)
                    )

                match! uploadFilesToObjectStorage parameters with
                | Ok returnValue -> return Ok returnValue.ReturnValue
                | Error error -> return Error error
            }

        /// Reads upload directory versions for capture data needed by the command workflow without changing remote state.
        let uploadDirectoryVersionsForCapture directoryVersions =
            task {
                match! saveDirectoryVersions directoryVersions correlationId with
                | Ok _ -> return Ok()
                | Error error -> return Error error
            }

        /// Builds the save-reference request that records a Grace Watch capture in the branch history.
        let createSaveReferenceForCapture rootDirectoryVersion saveMessage =
            task {
                match! createSaveReference rootDirectoryVersion saveMessage correlationId with
                | Ok returnValue -> return parseCreatedReferenceId correlationId returnValue
                | Error error -> return Error error
            }

        {
            GetBranch = getBranch
            GetGraceWatchStatus = getGraceWatchStatus
            ReadGraceStatus = readGraceStatusFile
            ScanForDifferences = scanForDifferences
            CopyUpdatedFilesToObjectCache =
                fun differences ->
                    task {
                        let! copied = copyUpdatedFilesToObjectCacheWithoutProgress differences
                        return copied :> seq<LocalFileVersion>
                    }
            BuildUpdatedGraceStatus = getNewGraceStatusAndDirectoryVersions
            UploadFileVersions = uploadFileVersions
            GetSavedDirectoryVersions = fun previousGraceStatus -> getSavedDirectoryVersionsForRootDirectory previousGraceStatus.RootDirectoryId correlationId
            UploadDirectoryVersions = uploadDirectoryVersionsForCapture
            ApplyGraceStatusIncremental = applyGraceStatusIncremental
            CreateSaveReference = createSaveReferenceForCapture
        }

    let branchAnnotateImplicitSaveMessage = "Created during `grace branch annotate` operation."

    /// Resolves cli current state target reference for branch annotate from command options, configuration, or local state.
    let resolveCliCurrentStateTargetReferenceForBranchAnnotate (explicitReferenceId: ReferenceId option) (correlationId: CorrelationId) =
        resolveCliCurrentStateTargetReference
            (defaultCliCurrentStateCaptureOperations correlationId)
            explicitReferenceId
            branchAnnotateImplicitSaveMessage
            correlationId

    /// Coordinates matches option name behavior for this CLI command path.
    let private matchesOptionName (option: Option) (optionName: string) =
        let normalizedName = optionName.TrimStart('-')

        /// Evaluates has alias against parsed options and command state.
        let hasAlias alias =
            option.Aliases
            |> Seq.exists (fun optionAlias -> optionAlias = alias)

        option.Name = optionName
        || option.Name = normalizedName
        || hasAlias optionName
        || hasAlias normalizedName

    /// Coordinates rec behavior for this CLI command path.
    let rec private hasOptionInCommandResult (commandResult: CommandResult) (optionName: string) =
        if isNull commandResult then
            false
        elif commandResult.Command.Options
             |> Seq.exists (fun option -> matchesOptionName option optionName) then
            true
        else
            match commandResult.Parent with
            | :? CommandResult as parent -> hasOptionInCommandResult parent optionName
            | _ -> false

    /// Checks if an option was present in the definition of the command.
    let isOptionPresent (parseResult: ParseResult) (optionName: string) =
        if not <| isNull (parseResult.GetResult(optionName)) then
            true
        else
            hasOptionInCommandResult parseResult.CommandResult optionName

    /// Checks if an option was implicitly specified (i.e. the default value was used), or explicitly specified by the user.
    let isOptionResultImplicit (parseResult: ParseResult) (optionName: string) =
        if isOptionPresent parseResult optionName then
            let result = parseResult.GetResult(optionName)

            if isNull result then
                true
            else
                let option = result :?> OptionResult
                option.Implicit
        else
            false

    /// Resolves correlation id from command options, configuration, or local state.
    let resolveCorrelationId (parseResult: ParseResult) : CorrelationId =
        if isOptionPresent parseResult OptionName.CorrelationId
           && not
              <| isOptionResultImplicit parseResult OptionName.CorrelationId then
            parseResult.GetValue(OptionName.CorrelationId)
        else
            match invocationCorrelationId with
            | Some correlationId -> correlationId
            | None ->
                let correlationId = generateCorrelationId ()
                invocationCorrelationId <- Some correlationId
                correlationId

    /// Adjusts command-line options to account for whether Id's or Name's were explicitly specified by the user,
    ///    or should be taken from default values.
    let getNormalizedIdsAndNames (parseResult: ParseResult) =

        /// Evaluates is explicit name against parsed options and command state.
        let isExplicitName (nameOption: string) =
            isOptionPresent parseResult nameOption
            && not
               <| isOptionResultImplicit parseResult nameOption
            && not
               <| String.IsNullOrWhiteSpace(parseResult.GetValue<string>(nameOption))

        /// Coordinates needs fallback behavior for this CLI command path.
        let needsFallback (idOption: string) (nameOption: string) =
            isOptionPresent parseResult idOption
            && isOptionResultImplicit parseResult idOption
            && parseResult.GetValue<Guid>(idOption) = Guid.Empty
            && not <| isExplicitName nameOption

        let needsConfigFallback =
            needsFallback OptionName.OwnerId OptionName.OwnerName
            || needsFallback OptionName.OrganizationId OptionName.OrganizationName
            || needsFallback OptionName.RepositoryId OptionName.RepositoryName
            || needsFallback OptionName.BranchId OptionName.BranchName

        let config = if needsConfigFallback then Some(Current()) else None

        /// Reads normalized id from ParseResult, local configuration, or Grace ids.
        let getNormalizedId (idOption: string) (nameOption: string) (configValue: Guid) =
            let isImplicit = isOptionResultImplicit parseResult idOption
            let explicitName = isExplicitName nameOption
            let idValue = parseResult.GetValue<Guid>(idOption)

            if isImplicit && explicitName then
                Guid.Empty
            elif isImplicit
                 && idValue = Guid.Empty
                 && not explicitName then
                configValue
            else
                idValue

        // If the name was specified on the command line, but the id wasn't (i.e. the default value was specified, and Implicit = true),
        //   then we should only send the name, and we set the id to Guid.Empty.

        let mutable graceIds = GraceIds.Default

        if isOptionPresent parseResult OptionName.CorrelationId then
            graceIds <- { graceIds with CorrelationId = resolveCorrelationId parseResult }

        if isOptionPresent parseResult OptionName.OwnerId
           || isOptionPresent parseResult OptionName.OwnerName then
            let ownerId =
                let configValue =
                    config
                    |> Option.map (fun current -> current.OwnerId)
                    |> Option.defaultValue OwnerId.Empty

                getNormalizedId OptionName.OwnerId OptionName.OwnerName configValue

            graceIds <-
                { graceIds with
                    OwnerId = ownerId
                    OwnerIdString = if ownerId = Guid.Empty then "" else $"{ownerId}"
                    OwnerName = parseResult.GetValue<string>(OptionName.OwnerName)
                    HasOwner = true
                }

        if isOptionPresent parseResult OptionName.OrganizationId
           || isOptionPresent parseResult OptionName.OrganizationName then
            let organizationId =
                let configValue =
                    config
                    |> Option.map (fun current -> current.OrganizationId)
                    |> Option.defaultValue OrganizationId.Empty

                getNormalizedId OptionName.OrganizationId OptionName.OrganizationName configValue

            graceIds <-
                { graceIds with
                    OrganizationId = organizationId
                    OrganizationIdString = if organizationId = Guid.Empty then "" else $"{organizationId}"
                    OrganizationName = parseResult.GetValue<string>(OptionName.OrganizationName)
                    HasOrganization = true
                }

        if isOptionPresent parseResult OptionName.RepositoryId
           || isOptionPresent parseResult OptionName.RepositoryName then
            let repositoryId =
                let configValue =
                    config
                    |> Option.map (fun current -> current.RepositoryId)
                    |> Option.defaultValue RepositoryId.Empty

                getNormalizedId OptionName.RepositoryId OptionName.RepositoryName configValue

            graceIds <-
                { graceIds with
                    RepositoryId = repositoryId
                    RepositoryIdString = if repositoryId = Guid.Empty then "" else $"{repositoryId}"
                    RepositoryName = parseResult.GetValue<string>(OptionName.RepositoryName)
                    HasRepository = true
                }

        if isOptionPresent parseResult OptionName.BranchId
           || isOptionPresent parseResult OptionName.BranchName then
            let branchId =
                let configValue =
                    config
                    |> Option.map (fun current -> current.BranchId)
                    |> Option.defaultValue BranchId.Empty

                getNormalizedId OptionName.BranchId OptionName.BranchName configValue

            graceIds <-
                { graceIds with
                    BranchId = branchId
                    BranchIdString = if branchId = Guid.Empty then "" else $"{branchId}"
                    BranchName = parseResult.GetValue<string>(OptionName.BranchName)
                    HasBranch = true
                }

        graceIds
