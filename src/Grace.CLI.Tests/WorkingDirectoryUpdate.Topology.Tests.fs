namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.Command
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Constants
open Grace.Types.Common
open NUnit.Framework
open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text

/// Exercises the pure pre-mutation topology boundary against real temporary working trees.
module WorkingDirectoryUpdateTopologyTests =
    /// Extracts a successful private construction or reports its rejected reason.
    let private required =
        function
        | Ok value -> value
        | Error error -> failwith error

    /// Configures a disposable repository root so planner filesystem classification uses production configuration semantics.
    let private configureForRoot (root: string) =
        let configuration = GraceConfiguration()
        configuration.OwnerId <- Guid.NewGuid()
        configuration.OrganizationId <- Guid.NewGuid()
        configuration.RepositoryId <- Guid.NewGuid()
        configuration.RootDirectory <- root
        configuration.StandardizedRootDirectory <- Grace.Shared.Utilities.normalizeFilePath root
        configuration.GraceDirectory <- Path.Combine(root, GraceConfigDirectory)
        configuration.ObjectDirectory <- Path.Combine(configuration.GraceDirectory, GraceObjectsDirectory)
        configuration.GraceStatusFile <- Path.Combine(configuration.GraceDirectory, GraceLocalStateDbFileName)
        configuration.GraceObjectCacheFile <- configuration.GraceStatusFile
        configuration.ConfigurationDirectory <- configuration.GraceDirectory

        Directory.CreateDirectory(configuration.ConfigurationDirectory)
        |> ignore

        saveConfigFile (Path.Combine(configuration.ConfigurationDirectory, GraceConfigFileName)) configuration
        resetConfiguration ()
        Current()

    /// Runs one topology scenario with isolated configuration and a temporary filesystem root.
    let private withTempRepo action =
        let root = Path.Combine(Path.GetTempPath(), $"grace-wdu-topology-{Guid.NewGuid():N}")
        let originalDirectory = Environment.CurrentDirectory
        let originalParseResult = Services.parseResult

        try
            Directory.CreateDirectory(root) |> ignore
            Environment.CurrentDirectory <- root
            Services.parseResult <- GraceCommand.rootCommand.Parse(Array.empty<string>)
            let configuration = configureForRoot root
            action root configuration
        finally
            Services.clearShouldIgnoreCache ()
            Services.parseResult <- originalParseResult
            Environment.CurrentDirectory <- originalDirectory
            resetConfiguration ()

            if Directory.Exists(root) then Directory.Delete(root, true)

    /// Creates an exact target file entry whose declared hashes match the supplied prepared bytes.
    let private targetFile (path: string) (bytes: byte array) =
        let sha256Hash =
            SHA256.HashData(bytes)
            |> Convert.ToHexString
            |> fun value -> Sha256Hash(value.ToLowerInvariant())

        let blake3Hash = Blake3Hash(ContentAddress.computeBlake3Hex bytes)
        WorkingDirectoryUpdateContracts.PreparedManifestEntry.File(RelativePath path, sha256Hash, blake3Hash)

    /// Builds an immutable manifest or stops the test at the exact manifest validation failure.
    let private manifest entries =
        WorkingDirectoryUpdateContracts.PreparedManifest.create entries
        |> required

    /// Builds one tracked local file with deterministic current-status hashes for topology classification.
    let private trackedFile (path: string) (bytes: byte array) =
        let sha256Hash =
            SHA256.HashData(bytes)
            |> Convert.ToHexString
            |> fun value -> Sha256Hash(value.ToLowerInvariant())

        LocalFileVersion.CreateWithHashes
            (RelativePath path)
            sha256Hash
            (Blake3Hash(ContentAddress.computeBlake3Hex bytes))
            false
            (int64 bytes.Length)
            (Grace.Shared.Utilities.getCurrentInstant ())
            true
            DateTime.UtcNow

    /// Builds one complete current status snapshot whose index explicitly names each tracked directory and file.
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
                    DateTime.UtcNow

            index[directory.DirectoryVersionId] <- directory)

        if trackedFiles |> List.isEmpty |> not then
            let root =
                LocalDirectoryVersion.CreateWithHashes
                    (Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"))
                    OwnerId.Empty
                    OrganizationId.Empty
                    RepositoryId.Empty
                    (RelativePath Constants.RootDirectoryPath)
                    (Sha256Hash "root")
                    (Blake3Hash "root")
                    (List<DirectoryVersionId>())
                    (List<LocalFileVersion>(trackedFiles))
                    0L
                    DateTime.UtcNow

            index[root.DirectoryVersionId] <- root

        { GraceStatus.Default with Index = index }

    /// Executes planning synchronously so each real-filesystem test has one stable assertion point.
    let private plan currentStatus preparedManifest =
        WorkingDirectoryUpdate.Topology.plan currentStatus preparedManifest
        |> fun task -> task.GetAwaiter().GetResult()

    /// Captures every path kind and file byte sequence before a rejection proof.
    let private snapshotTree root =
        DirectoryInfo(root)
            .GetFileSystemInfos("*", SearchOption.AllDirectories)
        |> Array.map (fun entry ->
            let path = Grace.Shared.Utilities.normalizeFilePath (Path.GetRelativePath(root, entry.FullName))

            let value =
                if entry :? DirectoryInfo then
                    "directory"
                else
                    Convert.ToHexString(File.ReadAllBytes(entry.FullName))

            path, value)
        |> Array.sort

    /// Asserts a planning rejection leaves every working-tree path, kind, and byte sequence unchanged.
    let private shouldRejectWithoutTreeChange root before result =
        match result with
        | WorkingDirectoryUpdate.Topology.Rejected _ -> snapshotTree root |> should equal before
        | WorkingDirectoryUpdate.Topology.Planned _ -> Assert.Fail("Expected a pre-mutation topology rejection.")

    /// Proves ignored user bytes at a target file can never be planned for replacement.
    [<Test>]
    let ``topology rejects ignored target file without changing distinct user bytes`` () =
        withTempRepo (fun root configuration ->
            let targetPath = Path.Combine(root, "protected.txt")
            let userBytes = Encoding.UTF8.GetBytes("ignored user bytes must survive")
            File.WriteAllBytes(targetPath, userBytes)
            configuration.GraceFileIgnoreEntries <- [| "protected.txt" |]
            Services.clearShouldIgnoreCache ()

            let preparedManifest = manifest [ targetFile "protected.txt" (Encoding.UTF8.GetBytes("selected bytes")) ]
            let before = snapshotTree root
            let result = plan GraceStatus.Default preparedManifest

            match result with
            | WorkingDirectoryUpdate.Topology.Rejected rejection ->
                WorkingDirectoryUpdate.Topology.Rejection.path rejection
                |> should equal (RelativePath "protected.txt")
            | WorkingDirectoryUpdate.Topology.Planned _ -> Assert.Fail("Ignored target bytes must reject before any mutation can be planned.")

            File.ReadAllBytes(targetPath)
            |> should equal userBytes

            snapshotTree root |> should equal before)

    /// Proves absent targets and verified tracked matches make a complete materialization plan without destructive actions.
    [<Test>]
    let ``topology retains matching tracked files and plans absent files and directories`` () =
        withTempRepo (fun root configuration ->
            configuration.GraceFileIgnoreEntries <- Array.empty
            configuration.GraceDirectoryIgnoreEntries <- Array.empty
            let matchingBytes = Encoding.UTF8.GetBytes("matching")
            File.WriteAllBytes(Path.Combine(root, "matching.txt"), matchingBytes)

            let currentStatus =
                status [] [
                    trackedFile "matching.txt" matchingBytes
                ]

            let preparedManifest =
                manifest [ targetFile "matching.txt" matchingBytes
                           WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory(RelativePath "src")
                           targetFile "src/new.txt" (Encoding.UTF8.GetBytes("new")) ]

            match plan currentStatus preparedManifest with
            | WorkingDirectoryUpdate.Topology.Planned topologyPlan ->
                WorkingDirectoryUpdate.Topology.Plan.actions topologyPlan
                |> should
                    equal
                    [
                        WorkingDirectoryUpdate.Topology.EnsureDirectory(RelativePath "src")
                        WorkingDirectoryUpdate.Topology.CopyVerifiedFile(RelativePath "src/new.txt")
                    ]
            | WorkingDirectoryUpdate.Topology.Rejected _ -> Assert.Fail("Expected a safe absent/matching plan."))

    /// Proves tracked target files with a different verified version are copied without inventing a removal action.
    [<Test>]
    let ``topology copies a tracked target file when its verified bytes differ`` () =
        withTempRepo (fun root configuration ->
            configuration.GraceFileIgnoreEntries <- Array.empty
            configuration.GraceDirectoryIgnoreEntries <- Array.empty
            let localBytes = Encoding.UTF8.GetBytes("old tracked bytes")
            let selectedBytes = Encoding.UTF8.GetBytes("selected replacement bytes")
            File.WriteAllBytes(Path.Combine(root, "replace.txt"), localBytes)

            let currentStatus =
                status [] [
                    trackedFile "replace.txt" localBytes
                ]

            match plan currentStatus (manifest [ targetFile "replace.txt" selectedBytes ]) with
            | WorkingDirectoryUpdate.Topology.Planned topologyPlan ->
                WorkingDirectoryUpdate.Topology.Plan.actions topologyPlan
                |> should
                    equal
                    [
                        WorkingDirectoryUpdate.Topology.CopyVerifiedFile(RelativePath "replace.txt")
                    ]
            | WorkingDirectoryUpdate.Topology.Rejected _ -> Assert.Fail("Expected tracked file replacement plan."))

    /// Proves tracked replacement and directory-to-file transitions name all tracked removals before copying target bytes.
    [<Test>]
    let ``topology orders tracked directory to file replacement deepest first`` () =
        withTempRepo (fun root configuration ->
            configuration.GraceFileIgnoreEntries <- Array.empty
            configuration.GraceDirectoryIgnoreEntries <- Array.empty

            Directory.CreateDirectory(Path.Combine(root, "replace", "nested"))
            |> ignore

            File.WriteAllText(Path.Combine(root, "replace", "nested", "old.txt"), "old")

            let currentStatus =
                status [ "replace"; "replace/nested" ] [
                    trackedFile "replace/nested/old.txt" (Encoding.UTF8.GetBytes("old"))
                ]

            let preparedManifest = manifest [ targetFile "replace" (Encoding.UTF8.GetBytes("new file")) ]

            match plan currentStatus preparedManifest with
            | WorkingDirectoryUpdate.Topology.Planned topologyPlan ->
                WorkingDirectoryUpdate.Topology.Plan.actions topologyPlan
                |> should
                    equal
                    [
                        WorkingDirectoryUpdate.Topology.RemoveTrackedFile(RelativePath "replace/nested/old.txt")
                        WorkingDirectoryUpdate.Topology.RemoveTrackedDirectory(RelativePath "replace/nested")
                        WorkingDirectoryUpdate.Topology.RemoveTrackedDirectory(RelativePath "replace")
                        WorkingDirectoryUpdate.Topology.CopyVerifiedFile(RelativePath "replace")
                    ]
            | WorkingDirectoryUpdate.Topology.Rejected rejection ->
                Assert.Fail(
                    $"Expected tracked directory replacement plan; rejected {WorkingDirectoryUpdate.Topology.Rejection.path rejection} as {WorkingDirectoryUpdate.Topology.Rejection.classification rejection}."
                ))

    /// Proves directory targets replace tracked files before shallow directory creation.
    [<Test>]
    let ``topology removes tracked file before creating target directory`` () =
        withTempRepo (fun root configuration ->
            configuration.GraceFileIgnoreEntries <- Array.empty
            configuration.GraceDirectoryIgnoreEntries <- Array.empty
            File.WriteAllText(Path.Combine(root, "src"), "old file")

            let currentStatus =
                status [] [
                    trackedFile "src" (Encoding.UTF8.GetBytes("old file"))
                ]

            let preparedManifest = manifest [ WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory(RelativePath "src") ]

            match plan currentStatus preparedManifest with
            | WorkingDirectoryUpdate.Topology.Planned topologyPlan ->
                WorkingDirectoryUpdate.Topology.Plan.actions topologyPlan
                |> should
                    equal
                    [
                        WorkingDirectoryUpdate.Topology.RemoveTrackedFile(RelativePath "src")
                        WorkingDirectoryUpdate.Topology.EnsureDirectory(RelativePath "src")
                    ]
            | WorkingDirectoryUpdate.Topology.Rejected _ -> Assert.Fail("Expected tracked file replacement plan."))

    /// Proves an already tracked target directory remains in place and produces no deferred work.
    [<Test>]
    let ``topology retains an existing tracked target directory`` () =
        withTempRepo (fun root configuration ->
            configuration.GraceFileIgnoreEntries <- Array.empty
            configuration.GraceDirectoryIgnoreEntries <- Array.empty

            Directory.CreateDirectory(Path.Combine(root, "retained"))
            |> ignore

            let currentStatus = status [ "retained" ] []

            match plan currentStatus (manifest [ WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory(RelativePath "retained") ]) with
            | WorkingDirectoryUpdate.Topology.Planned topologyPlan ->
                WorkingDirectoryUpdate.Topology.Plan.actions topologyPlan
                |> List.isEmpty
                |> should equal true
            | WorkingDirectoryUpdate.Topology.Rejected _ -> Assert.Fail("Expected tracked target directory retention."))

    /// Proves an ignored descendant blocks a tracked directory-to-file replacement before any action is returned.
    [<Test>]
    let ``topology rejects a tracked directory target file with an ignored descendant`` () =
        withTempRepo (fun root configuration ->
            configuration.GraceFileIgnoreEntries <- [| "replace/user.txt" |]
            configuration.GraceDirectoryIgnoreEntries <- Array.empty

            Directory.CreateDirectory(Path.Combine(root, "replace", "nested"))
            |> ignore

            File.WriteAllText(Path.Combine(root, "replace", "nested", "tracked.txt"), "tracked")
            File.WriteAllText(Path.Combine(root, "replace", "user.txt"), "ignored user bytes")

            let currentStatus =
                status [ "replace"; "replace/nested" ] [
                    trackedFile "replace/nested/tracked.txt" (Encoding.UTF8.GetBytes("tracked"))
                ]

            let before = snapshotTree root

            plan currentStatus (manifest [ targetFile "replace" (Encoding.UTF8.GetBytes("selected file")) ])
            |> shouldRejectWithoutTreeChange root before)

    /// Proves untracked target files and nested ignored descendants reject without changing local user content.
    [<Test>]
    let ``topology rejects untracked blockers and ignored directory descendants without mutation`` () =
        withTempRepo (fun root configuration ->
            configuration.GraceFileIgnoreEntries <- [| "ignored.txt" |]
            configuration.GraceDirectoryIgnoreEntries <- Array.empty
            let untrackedPath = Path.Combine(root, "untracked.txt")
            File.WriteAllText(untrackedPath, "untracked")
            let untrackedBefore = snapshotTree root
            let untracked = plan GraceStatus.Default (manifest [ targetFile "untracked.txt" (Encoding.UTF8.GetBytes("selected")) ])
            shouldRejectWithoutTreeChange root untrackedBefore untracked

            File.Delete(untrackedPath)

            Directory.CreateDirectory(Path.Combine(root, "replace"))
            |> ignore

            File.WriteAllText(Path.Combine(root, "replace", "ignored.txt"), "preserve")
            let ignoredBefore = snapshotTree root
            let currentStatus = status [ "replace" ] []
            let ignored = plan currentStatus (manifest [ targetFile "replace" (Encoding.UTF8.GetBytes("selected")) ])
            shouldRejectWithoutTreeChange root ignoredBefore ignored)

    /// Proves target directories reject both ignored and untracked file blockers without deleting user bytes.
    [<Test>]
    let ``topology rejects ignored and untracked files where target directories are required`` () =
        withTempRepo (fun root configuration ->
            configuration.GraceFileIgnoreEntries <- [| "ignored-directory-target" |]
            configuration.GraceDirectoryIgnoreEntries <- Array.empty
            let ignoredPath = Path.Combine(root, "ignored-directory-target")
            File.WriteAllText(ignoredPath, "ignored user bytes")
            let ignoredBefore = snapshotTree root

            plan GraceStatus.Default (manifest [ WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory(RelativePath "ignored-directory-target") ])
            |> shouldRejectWithoutTreeChange root ignoredBefore

            File.Delete(ignoredPath)
            let untrackedPath = Path.Combine(root, "untracked-directory-target")
            File.WriteAllText(untrackedPath, "untracked user bytes")
            let untrackedBefore = snapshotTree root

            plan GraceStatus.Default (manifest [ WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory(RelativePath "untracked-directory-target") ])
            |> shouldRejectWithoutTreeChange root untrackedBefore)

    /// Proves an untracked target-file blocker produces a stable rejected path and preserves its exact user bytes.
    [<Test>]
    let ``topology rejects an untracked target file without changing its bytes`` () =
        withTempRepo (fun root configuration ->
            configuration.GraceFileIgnoreEntries <- Array.empty
            configuration.GraceDirectoryIgnoreEntries <- Array.empty
            let targetPath = Path.Combine(root, "untracked.txt")
            let userBytes = Encoding.UTF8.GetBytes("untracked user bytes must survive")
            File.WriteAllBytes(targetPath, userBytes)
            let before = snapshotTree root
            let result = plan GraceStatus.Default (manifest [ targetFile "untracked.txt" (Encoding.UTF8.GetBytes("selected")) ])

            match result with
            | WorkingDirectoryUpdate.Topology.Rejected rejection ->
                WorkingDirectoryUpdate.Topology.Rejection.path rejection
                |> should equal (RelativePath "untracked.txt")

                WorkingDirectoryUpdate.Topology.Rejection.classification rejection
                |> should equal WorkingDirectoryUpdate.Topology.Untracked
            | WorkingDirectoryUpdate.Topology.Planned _ -> Assert.Fail("Expected untracked target-file rejection.")

            File.ReadAllBytes(targetPath)
            |> should equal userBytes

            snapshotTree root |> should equal before)

    /// Proves implicit directory ancestors are planned shallowest first before later verified file copies.
    [<Test>]
    let ``topology plans implicit target directories shallowest first`` () =
        withTempRepo (fun root configuration ->
            configuration.GraceFileIgnoreEntries <- Array.empty
            configuration.GraceDirectoryIgnoreEntries <- Array.empty
            let preparedManifest = manifest [ targetFile "one/two/three.txt" (Encoding.UTF8.GetBytes("selected")) ]

            match plan GraceStatus.Default preparedManifest with
            | WorkingDirectoryUpdate.Topology.Planned topologyPlan ->
                WorkingDirectoryUpdate.Topology.Plan.actions topologyPlan
                |> should
                    equal
                    [
                        WorkingDirectoryUpdate.Topology.EnsureDirectory(RelativePath "one")
                        WorkingDirectoryUpdate.Topology.EnsureDirectory(RelativePath "one/two")
                        WorkingDirectoryUpdate.Topology.CopyVerifiedFile(RelativePath "one/two/three.txt")
                    ]
            | WorkingDirectoryUpdate.Topology.Rejected rejection ->
                Assert.Fail(
                    $"Expected implicit target directory plan; rejected {WorkingDirectoryUpdate.Topology.Rejection.path rejection} as {WorkingDirectoryUpdate.Topology.Rejection.classification rejection}."
                ))

    /// Proves obsolete tracked empty directories are listed deepest first and user descendants prohibit removal.
    [<Test>]
    let ``topology removes obsolete tracked empty directories and rejects unsafe descendants`` () =
        withTempRepo (fun root configuration ->
            configuration.GraceFileIgnoreEntries <- Array.empty
            configuration.GraceDirectoryIgnoreEntries <- Array.empty

            Directory.CreateDirectory(Path.Combine(root, "obsolete", "nested"))
            |> ignore

            let currentStatus = status [ "obsolete"; "obsolete/nested" ] []

            match plan currentStatus (manifest []) with
            | WorkingDirectoryUpdate.Topology.Planned topologyPlan ->
                WorkingDirectoryUpdate.Topology.Plan.actions topologyPlan
                |> should
                    equal
                    [
                        WorkingDirectoryUpdate.Topology.RemoveTrackedDirectory(RelativePath "obsolete/nested")
                        WorkingDirectoryUpdate.Topology.RemoveTrackedDirectory(RelativePath "obsolete")
                    ]
            | WorkingDirectoryUpdate.Topology.Rejected rejection ->
                Assert.Fail(
                    $"Expected obsolete empty tracked directory removal; rejected {WorkingDirectoryUpdate.Topology.Rejection.path rejection} as {WorkingDirectoryUpdate.Topology.Rejection.classification rejection}."
                )

            File.WriteAllText(Path.Combine(root, "obsolete", "nested", "user.txt"), "user")
            let before = snapshotTree root

            plan currentStatus (manifest [])
            |> shouldRejectWithoutTreeChange root before)

    /// Proves an ignored descendant blocks obsolete tracked-directory removal and preserves the whole subtree.
    [<Test>]
    let ``topology rejects obsolete tracked directory removal with an ignored descendant`` () =
        withTempRepo (fun root configuration ->
            configuration.GraceFileIgnoreEntries <- [| "obsolete/user.txt" |]
            configuration.GraceDirectoryIgnoreEntries <- Array.empty

            Directory.CreateDirectory(Path.Combine(root, "obsolete", "nested"))
            |> ignore

            File.WriteAllText(Path.Combine(root, "obsolete", "nested", "tracked.txt"), "tracked")
            File.WriteAllText(Path.Combine(root, "obsolete", "user.txt"), "ignored user bytes")

            let currentStatus =
                status [ "obsolete"; "obsolete/nested" ] [
                    trackedFile "obsolete/nested/tracked.txt" (Encoding.UTF8.GetBytes("tracked"))
                ]

            let before = snapshotTree root
            let result = plan currentStatus (manifest [])
            shouldRejectWithoutTreeChange root before result)

    /// Proves manifest collision and escape validation reject before the planner can receive ambiguous Windows shapes.
    [<Test>]
    let ``prepared manifest rejects case collisions file directory conflicts and normalized escapes`` () =
        let bytes = Encoding.UTF8.GetBytes("content")

        manifest [ targetFile "safe.txt" bytes ] |> ignore

        WorkingDirectoryUpdateContracts.PreparedManifest.create [ targetFile "Case.txt" bytes
                                                                  targetFile "case.txt" bytes ]
        |> Result.isError
        |> should equal true

        WorkingDirectoryUpdateContracts.PreparedManifest.create [ targetFile "node" bytes
                                                                  WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory(RelativePath "node/child") ]
        |> Result.isError
        |> should equal true

        WorkingDirectoryUpdateContracts.PreparedManifest.create [ targetFile "../escape.txt" bytes ]
        |> Result.isError
        |> should equal true
