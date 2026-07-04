namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.Services
open Grace.Shared.Client.Configuration
open Grace.Shared
open Grace.Shared.Constants
open Grace.Types.Branch
open Grace.Types.DirectoryVersion
open Grace.Types.Common
open Grace.Types.Reference
open Microsoft.Data.Sqlite
open NUnit.Framework
open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks

/// Groups current state capture cli coverage for the CLI test project.
[<NonParallelizable>]
module CurrentStateCaptureCliTests =
    let private correlationId = "current-state-capture-tests"
    let private currentBranchId = Guid.NewGuid()
    let private parentBranchId = Guid.NewGuid()
    let private rootDirectoryId = Guid.NewGuid()
    let private rootSha = Sha256Hash "current-root-sha"
    let private rootBlake3 = Blake3Hash "current-root-blake3"
    let private savedReferenceId = Guid.NewGuid()

    /// Builds root directory version with hashes test data used to exercise CLI current State Capture behavior.
    let private rootDirectoryVersionWithHashes directoryVersionId sha256Hash blake3Hash =
        LocalDirectoryVersion.CreateWithHashes
            directoryVersionId
            OwnerId.Empty
            OrganizationId.Empty
            RepositoryId.Empty
            Constants.RootDirectoryPath
            sha256Hash
            blake3Hash
            (List<DirectoryVersionId>())
            (List<LocalFileVersion>())
            0L
            DateTime.UtcNow

    /// Builds root directory version test data used to exercise CLI current State Capture behavior.
    let private rootDirectoryVersion directoryVersionId sha256Hash = rootDirectoryVersionWithHashes directoryVersionId sha256Hash rootBlake3

    /// Builds grace status with root blake3 test data used to exercise CLI current State Capture behavior.
    let private graceStatusWithRootBlake3 directoryVersionId sha256Hash blake3Hash =
        let root = rootDirectoryVersionWithHashes directoryVersionId sha256Hash blake3Hash
        let index = GraceIndex()
        index.TryAdd(directoryVersionId, root) |> ignore

        { GraceStatus.Default with
            Index = index
            RootDirectoryId = directoryVersionId
            RootDirectorySha256Hash = sha256Hash
            RootDirectoryBlake3Hash = root.Blake3Hash
        }

    /// Builds grace status test data used to exercise CLI current State Capture behavior.
    let private graceStatus directoryVersionId sha256Hash = graceStatusWithRootBlake3 directoryVersionId sha256Hash rootBlake3

    /// Builds sha256 hash test data used to exercise CLI current State Capture behavior.
    let private sha256Hash (bytes: byte array) =
        SHA256.HashData(bytes)
        |> Convert.ToHexString
        |> fun value -> Sha256Hash(value.ToLowerInvariant())

    /// Builds directory version test data used to exercise CLI current State Capture behavior.
    let private directoryVersion
        (configuration: GraceConfiguration)
        (directoryVersionId: DirectoryVersionId)
        (relativePath: RelativePath)
        (directoryIds: DirectoryVersionId seq)
        (files: LocalFileVersion seq)
        =
        LocalDirectoryVersion.CreateWithHashes
            directoryVersionId
            configuration.OwnerId
            configuration.OrganizationId
            configuration.RepositoryId
            relativePath
            (Sha256Hash $"sha-{directoryVersionId:N}")
            (Blake3Hash $"blake3-{directoryVersionId:N}")
            (List<DirectoryVersionId>(directoryIds))
            (List<LocalFileVersion>(files))
            (files |> Seq.sumBy (fun file -> int64 file.Size))
            DateTime.UtcNow

    /// Builds file version test data used to exercise CLI current State Capture behavior.
    let private fileVersion (relativePath: RelativePath) (contents: string) =
        let bytes = Encoding.UTF8.GetBytes(contents)

        LocalFileVersion.CreateWithHashes
            relativePath
            (sha256Hash bytes)
            (Blake3Hash(ContentAddress.computeBlake3Hex bytes))
            false
            (int64 bytes.Length)
            (Grace.Shared.Utilities.getCurrentInstant ())
            true
            DateTime.UtcNow

    /// Builds grace status from directories test data used to exercise CLI current State Capture behavior.
    let private graceStatusFromDirectories (root: LocalDirectoryVersion) (directories: LocalDirectoryVersion seq) =
        let index = GraceIndex()

        for directoryVersion in directories do
            index.TryAdd(directoryVersion.DirectoryVersionId, directoryVersion)
            |> ignore

        { GraceStatus.Default with
            Index = index
            RootDirectoryId = root.DirectoryVersionId
            RootDirectorySha256Hash = root.Sha256Hash
            RootDirectoryBlake3Hash = root.Blake3Hash
        }

    /// Builds reference dto test data used to exercise CLI current State Capture behavior.
    let private referenceDto referenceId directoryVersionId sha256Hash =
        { ReferenceDto.Default with
            ReferenceId = referenceId
            BranchId = currentBranchId
            ReferenceType = ReferenceType.Save
            DirectoryId = directoryVersionId
            Sha256Hash = sha256Hash
            Blake3Hash = rootBlake3
        }

    /// Builds branch test data used to exercise CLI current State Capture behavior.
    let private branch saveEnabled latestReference =
        { BranchDto.Default with BranchId = currentBranchId; SaveEnabled = saveEnabled; LatestReference = latestReference; LatestSave = latestReference }

    /// Builds manifest reference for test data used to exercise CLI current State Capture behavior.
    let private manifestReferenceFor bytes =
        let blockAddress = ContentBlockAddress(ContentAddress.computeBlake3Hex bytes)

        let manifest =
            FileManifest.Create(
                ManifestAddress String.Empty,
                RabinChunking.SuiteName,
                FileContentHash(ContentAddress.computeBlake3Hex bytes),
                int64 bytes.Length,
                [
                    ContentBlock.Create(blockAddress, 0L, int64 bytes.Length)
                ]
            )

        FileContentReference.FileManifest { manifest with ManifestAddress = ContentAddress.computeManifestAddressForManifest manifest }

    /// Configures for root for the test scenario.
    let private configureForRoot (root: string) =
        let configuration = GraceConfiguration()
        configuration.OwnerId <- Guid.NewGuid()
        configuration.OrganizationId <- Guid.NewGuid()
        configuration.RepositoryId <- Guid.NewGuid()
        configuration.RootDirectory <- root
        configuration.StandardizedRootDirectory <- Grace.Shared.Utilities.normalizeFilePath root
        configuration.GraceDirectory <- Path.Combine(root, Constants.GraceConfigDirectory)
        configuration.ObjectDirectory <- Path.Combine(configuration.GraceDirectory, Constants.GraceObjectsDirectory)
        configuration.GraceStatusFile <- Path.Combine(configuration.GraceDirectory, Constants.GraceLocalStateDbFileName)
        configuration.GraceObjectCacheFile <- configuration.GraceStatusFile
        configuration.ConfigurationDirectory <- configuration.GraceDirectory
        configuration.IsPopulated <- true
        updateConfiguration configuration
        configuration

    /// Runs the supplied action with temp repo applied.
    let private withTempRepo (action: string -> GraceConfiguration -> unit) =
        let root = Path.Combine(Path.GetTempPath(), $"grace-current-state-{Guid.NewGuid():N}")
        let previousDirectory = Environment.CurrentDirectory
        let previousConfiguration = if configurationFileExists () then Some(Current()) else None
        let previousParseResult = parseResult

        try
            Directory.CreateDirectory(root) |> ignore
            Environment.CurrentDirectory <- root
            parseResult <- GraceCommand.rootCommand.Parse(Array.empty<string>)
            let configuration = configureForRoot root
            action root configuration
        finally
            parseResult <- previousParseResult
            Environment.CurrentDirectory <- previousDirectory

            match previousConfiguration with
            | Some configuration -> updateConfiguration configuration
            | None -> resetConfiguration ()

            if Directory.Exists(root) then
                SqliteConnection.ClearAllPools()
                Directory.Delete(root, true)

    /// Counts rows in the local state database so tests can assert persisted structure without model shortcuts.
    let private countLocalStateRows dbPath commandText parameterName parameterValue =
        use connection = new SqliteConnection($"Data Source={dbPath}")
        connection.Open()

        use command = connection.CreateCommand()
        command.CommandText <- commandText

        command.Parameters.Add(parameterName, SqliteType.Text)
        |> ignore

        command.Parameters[parameterName].Value <- parameterValue
        Convert.ToInt32(command.ExecuteScalar())

    /// Verifies that directory delete removes subtree reference from nearest surviving parent.
    [<Test>]
    let ``directory delete removes subtree reference from nearest surviving parent`` () =
        withTempRepo (fun root configuration ->
            Directory.CreateDirectory(Path.Combine(root, "src"))
            |> ignore

            let rootId = Guid.NewGuid()
            let srcId = Guid.NewGuid()
            let deletedId = Guid.NewGuid()
            let nestedDeletedId = Guid.NewGuid()
            let deletedFile = fileVersion (RelativePath "src/old/file.txt") "deleted file"
            let nestedDeleted = directoryVersion configuration nestedDeletedId (RelativePath "src/old/nested") Seq.empty Seq.empty
            let deleted = directoryVersion configuration deletedId (RelativePath "src/old") [| nestedDeletedId |] [| deletedFile |]
            let src = directoryVersion configuration srcId (RelativePath "src") [| deletedId |] Seq.empty
            let previousRoot = directoryVersion configuration rootId Constants.RootDirectoryPath [| srcId |] Seq.empty

            let previousStatus =
                graceStatusFromDirectories
                    previousRoot
                    [|
                        previousRoot
                        src
                        deleted
                        nestedDeleted
                    |]

            let differences =
                List<FileSystemDifference>(
                    [|
                        FileSystemDifference.Create DifferenceType.Delete FileSystemEntryType.File (RelativePath "src/old/file.txt")
                        FileSystemDifference.Create DifferenceType.Delete FileSystemEntryType.Directory (RelativePath "src/old/nested")
                        FileSystemDifference.Create DifferenceType.Delete FileSystemEntryType.Directory (RelativePath "src/old")
                    |]
                )

            /// Builds updated status test data used to exercise CLI current State Capture behavior.
            let updatedStatus, newDirectoryVersions =
                (getNewGraceStatusAndDirectoryVersions previousStatus differences)
                    .Result

            updatedStatus.Index.Values
            |> Seq.exists (fun directoryVersion -> directoryVersion.RelativePath = RelativePath "src/old")
            |> should equal false

            updatedStatus.Index.Values
            |> Seq.exists (fun directoryVersion -> directoryVersion.RelativePath = RelativePath "src/old/nested")
            |> should equal false

            let updatedRoot = updatedStatus.Index[updatedStatus.RootDirectoryId]

            updatedRoot.DirectoryVersionId
            |> should not' (equal rootId)

            updatedRoot.Directories.Count |> should equal 1

            let updatedSrc = updatedStatus.Index[updatedRoot.Directories[0]]

            updatedSrc.RelativePath
            |> should equal (RelativePath "src")

            updatedSrc.DirectoryVersionId
            |> should not' (equal srcId)

            updatedSrc.Directories
            |> Seq.exists (fun directoryId ->
                directoryId = deletedId
                || directoryId = nestedDeletedId)
            |> should equal false

            newDirectoryVersions
            |> Seq.map (fun directoryVersion -> directoryVersion.RelativePath)
            |> should
                equivalent
                [
                    RelativePath "src"
                    Constants.RootDirectoryPath
                ])

    /// Verifies that directory delete preserves sibling that differs only by case.
    [<Test>]
    let ``directory delete preserves sibling that differs only by case`` () =
        withTempRepo (fun root configuration ->
            Directory.CreateDirectory(Path.Combine(root, "src"))
            |> ignore

            let rootId = Guid.NewGuid()
            let srcId = Guid.NewGuid()
            let deletedId = Guid.NewGuid()
            let deletedNestedId = Guid.NewGuid()
            let survivingId = Guid.NewGuid()
            let deletedNested = directoryVersion configuration deletedNestedId (RelativePath "src/Foo/nested") Seq.empty Seq.empty
            let deleted = directoryVersion configuration deletedId (RelativePath "src/Foo") [| deletedNestedId |] Seq.empty
            let surviving = directoryVersion configuration survivingId (RelativePath "src/foo") Seq.empty Seq.empty
            let src = directoryVersion configuration srcId (RelativePath "src") [| deletedId; survivingId |] Seq.empty
            let previousRoot = directoryVersion configuration rootId Constants.RootDirectoryPath [| srcId |] Seq.empty

            let previousStatus =
                graceStatusFromDirectories
                    previousRoot
                    [|
                        previousRoot
                        src
                        deleted
                        deletedNested
                        surviving
                    |]

            let differences =
                List<FileSystemDifference>(
                    [|
                        FileSystemDifference.Create DifferenceType.Delete FileSystemEntryType.Directory (RelativePath "src/Foo/nested")
                        FileSystemDifference.Create DifferenceType.Delete FileSystemEntryType.Directory (RelativePath "src/Foo")
                    |]
                )

            /// Builds updated status test data used to exercise CLI current State Capture behavior.
            let updatedStatus, newDirectoryVersions =
                (getNewGraceStatusAndDirectoryVersions previousStatus differences)
                    .Result

            updatedStatus.Index.Values
            |> Seq.exists (fun directoryVersion -> directoryVersion.RelativePath = RelativePath "src/Foo")
            |> should equal false

            updatedStatus.Index.Values
            |> Seq.exists (fun directoryVersion -> directoryVersion.RelativePath = RelativePath "src/Foo/nested")
            |> should equal false

            updatedStatus.Index.ContainsKey(survivingId)
            |> should equal true

            let updatedRoot = updatedStatus.Index[updatedStatus.RootDirectoryId]
            updatedRoot.Directories.Count |> should equal 1

            let updatedSrc = updatedStatus.Index[updatedRoot.Directories[0]]

            updatedSrc.RelativePath
            |> should equal (RelativePath "src")

            updatedSrc.Directories
            |> Seq.map (fun directoryId -> updatedStatus.Index[directoryId].RelativePath)
            |> should equivalent [ RelativePath "src/foo" ]

            newDirectoryVersions
            |> Seq.map (fun directoryVersion -> directoryVersion.RelativePath)
            |> should
                equivalent
                [
                    RelativePath "src"
                    Constants.RootDirectoryPath
                ])

    /// Verifies that directory delete for unknown path is ignored without changing root.
    [<Test>]
    let ``directory delete for unknown path is ignored without changing root`` () =
        withTempRepo (fun _ configuration ->
            let rootId = Guid.NewGuid()
            let previousRoot = directoryVersion configuration rootId Constants.RootDirectoryPath Seq.empty Seq.empty
            let previousStatus = graceStatusFromDirectories previousRoot [| previousRoot |]

            let differences =
                List<FileSystemDifference>(
                    [|
                        FileSystemDifference.Create DifferenceType.Delete FileSystemEntryType.Directory (RelativePath "missing")
                    |]
                )

            /// Builds updated status test data used to exercise CLI current State Capture behavior.
            let updatedStatus, newDirectoryVersions =
                (getNewGraceStatusAndDirectoryVersions previousStatus differences)
                    .Result

            updatedStatus.RootDirectoryId
            |> should equal previousStatus.RootDirectoryId

            updatedStatus.RootDirectorySha256Hash
            |> should equal previousStatus.RootDirectorySha256Hash

            updatedStatus.RootDirectoryBlake3Hash
            |> should equal previousStatus.RootDirectoryBlake3Hash

            updatedStatus.Index.Count
            |> should equal previousStatus.Index.Count

            newDirectoryVersions.Count |> should equal 0)

    /// Verifies that file delete still removes file reference and rebuilds root.
    [<Test>]
    let ``file delete still removes file reference and rebuilds root`` () =
        withTempRepo (fun _ configuration ->
            let rootId = Guid.NewGuid()
            let deletedFile = fileVersion (RelativePath "deleted.txt") "deleted file"
            let previousRoot = directoryVersion configuration rootId Constants.RootDirectoryPath Seq.empty [| deletedFile |]
            let previousStatus = graceStatusFromDirectories previousRoot [| previousRoot |]

            let differences =
                List<FileSystemDifference>(
                    [|
                        FileSystemDifference.Create DifferenceType.Delete FileSystemEntryType.File (RelativePath "deleted.txt")
                    |]
                )

            /// Builds updated status test data used to exercise CLI current State Capture behavior.
            let updatedStatus, newDirectoryVersions =
                (getNewGraceStatusAndDirectoryVersions previousStatus differences)
                    .Result

            let updatedRoot = updatedStatus.Index[updatedStatus.RootDirectoryId]

            updatedRoot.DirectoryVersionId
            |> should not' (equal rootId)

            updatedRoot.Files.Count |> should equal 0
            newDirectoryVersions.Count |> should equal 1

            newDirectoryVersions[0].RelativePath
            |> should equal Constants.RootDirectoryPath)

    /// Verifies that empty directory delete removes child reference from root.
    [<Test>]
    let ``empty directory delete removes child reference from root`` () =
        withTempRepo (fun _ configuration ->
            let rootId = Guid.NewGuid()
            let emptyId = Guid.NewGuid()
            let empty = directoryVersion configuration emptyId (RelativePath "empty") Seq.empty Seq.empty
            let previousRoot = directoryVersion configuration rootId Constants.RootDirectoryPath [| emptyId |] Seq.empty
            let previousStatus = graceStatusFromDirectories previousRoot [| previousRoot; empty |]

            let differences =
                List<FileSystemDifference>(
                    [|
                        FileSystemDifference.Create DifferenceType.Delete FileSystemEntryType.Directory (RelativePath "empty")
                    |]
                )

            /// Builds updated status test data used to exercise CLI current State Capture behavior.
            let updatedStatus, newDirectoryVersions =
                (getNewGraceStatusAndDirectoryVersions previousStatus differences)
                    .Result

            updatedStatus.Index.Values
            |> Seq.exists (fun directoryVersion -> directoryVersion.RelativePath = RelativePath "empty")
            |> should equal false

            let updatedRoot = updatedStatus.Index[updatedStatus.RootDirectoryId]

            updatedRoot.DirectoryVersionId
            |> should not' (equal rootId)

            updatedRoot.Directories.Count |> should equal 0
            newDirectoryVersions.Count |> should equal 1

            newDirectoryVersions[0].RelativePath
            |> should equal Constants.RootDirectoryPath)

    /// Verifies that empty directory additions create directory-version status without uploaded files.
    [<Test>]
    let ``empty directory add creates GraceStatus directory version without uploaded files`` () =
        withTempRepo (fun root configuration ->
            let emptyPath = Path.Combine(root, "empty")

            Directory.CreateDirectory(emptyPath) |> ignore

            let rootId = Guid.NewGuid()
            let previousRoot = directoryVersion configuration rootId Constants.RootDirectoryPath Seq.empty Seq.empty
            let previousStatus = graceStatusFromDirectories previousRoot [| previousRoot |]

            let differences =
                List<FileSystemDifference>(
                    [|
                        FileSystemDifference.Create DifferenceType.Add FileSystemEntryType.Directory (RelativePath "empty")
                    |]
                )

            let updatedStatus, newDirectoryVersions =
                (getNewGraceStatusAndDirectoryVersions previousStatus differences)
                    .Result

            let emptyDirectory =
                updatedStatus.Index.Values
                |> Seq.find (fun directoryVersion -> directoryVersion.RelativePath = RelativePath "empty")

            emptyDirectory.Directories.Count |> should equal 0
            emptyDirectory.Files.Count |> should equal 0
            emptyDirectory.Size |> should equal 0L

            let updatedRoot = updatedStatus.Index[updatedStatus.RootDirectoryId]

            updatedRoot.Directories
            |> Seq.exists (fun directoryVersionId -> directoryVersionId = emptyDirectory.DirectoryVersionId)
            |> should equal true

            getChangedFileVersionsReferencedByUpdatedDirectories differences newDirectoryVersions
            |> Seq.isEmpty
            |> should equal true)

    /// Verifies that a directory rename modeled as delete plus add preserves empty children in the added subtree.
    [<Test>]
    let ``directory rename delete plus add preserves empty child directories in added subtree`` () =
        withTempRepo (fun root configuration ->
            let oldRootRelativePath = RelativePath "old-assets"
            let oldContentRelativePath = RelativePath "old-assets/content"
            let oldEmptyChildRelativePath = RelativePath "old-assets/empty-child"
            let oldFileRelativePath = RelativePath "old-assets/content/asset.txt"
            let newRootRelativePath = RelativePath "new-assets"
            let newContentRelativePath = RelativePath "new-assets/content"
            let newEmptyChildRelativePath = RelativePath "new-assets/empty-child"

            Directory.CreateDirectory(Path.Combine(root, string newContentRelativePath))
            |> ignore

            Directory.CreateDirectory(Path.Combine(root, string newEmptyChildRelativePath))
            |> ignore

            let rootId = Guid.NewGuid()
            let oldRootId = Guid.NewGuid()
            let oldContentId = Guid.NewGuid()
            let oldEmptyChildId = Guid.NewGuid()
            let oldFile = fileVersion oldFileRelativePath "old subtree content"
            let oldContent = directoryVersion configuration oldContentId oldContentRelativePath Seq.empty [| oldFile |]
            let oldEmptyChild = directoryVersion configuration oldEmptyChildId oldEmptyChildRelativePath Seq.empty Seq.empty
            let oldRoot = directoryVersion configuration oldRootId oldRootRelativePath [| oldContentId; oldEmptyChildId |] Seq.empty
            let previousRoot = directoryVersion configuration rootId Constants.RootDirectoryPath [| oldRootId |] Seq.empty

            let previousStatus =
                graceStatusFromDirectories
                    previousRoot
                    [|
                        previousRoot
                        oldRoot
                        oldContent
                        oldEmptyChild
                    |]

            let differences =
                List<FileSystemDifference>(
                    [|
                        FileSystemDifference.Create DifferenceType.Delete FileSystemEntryType.Directory oldRootRelativePath
                        FileSystemDifference.Create DifferenceType.Add FileSystemEntryType.Directory newRootRelativePath
                        FileSystemDifference.Create DifferenceType.Add FileSystemEntryType.Directory newContentRelativePath
                        FileSystemDifference.Create DifferenceType.Add FileSystemEntryType.Directory newEmptyChildRelativePath
                    |]
                )

            let updatedStatus, newDirectoryVersions =
                (getNewGraceStatusAndDirectoryVersions previousStatus differences)
                    .Result

            updatedStatus.Index.Values
            |> Seq.exists (fun directoryVersion -> directoryVersion.RelativePath = oldRootRelativePath)
            |> should equal false

            updatedStatus.Index.Values
            |> Seq.exists (fun directoryVersion -> directoryVersion.RelativePath = oldEmptyChildRelativePath)
            |> should equal false

            let newRoot =
                updatedStatus.Index.Values
                |> Seq.find (fun directoryVersion -> directoryVersion.RelativePath = newRootRelativePath)

            let newEmptyChild =
                updatedStatus.Index.Values
                |> Seq.find (fun directoryVersion -> directoryVersion.RelativePath = newEmptyChildRelativePath)

            newEmptyChild.Directories.Count |> should equal 0
            newEmptyChild.Files.Count |> should equal 0
            newEmptyChild.Size |> should equal 0L

            newRoot.Directories
            |> Seq.map (fun directoryId -> updatedStatus.Index[directoryId].RelativePath)
            |> should
                equivalent
                [|
                    newContentRelativePath
                    newEmptyChildRelativePath
                |]

            let newContent =
                updatedStatus.Index.Values
                |> Seq.find (fun directoryVersion -> directoryVersion.RelativePath = newContentRelativePath)

            newContent.Directories.Count |> should equal 0
            newContent.Files.Count |> should equal 0

            newDirectoryVersions
            |> Seq.map (fun directoryVersion -> directoryVersion.RelativePath)
            |> should
                equivalent
                [|
                    newRootRelativePath
                    newContentRelativePath
                    newEmptyChildRelativePath
                    Constants.RootDirectoryPath
                |])

    /// Verifies that empty LocalDirectoryVersion rows survive local status and object-cache persistence.
    [<Test>]
    let ``empty directory version persists to status and object cache without child rows`` () =
        withTempRepo (fun _ configuration ->
            let rootId = Guid.NewGuid()
            let emptyId = Guid.NewGuid()
            let empty = directoryVersion configuration emptyId (RelativePath "empty") Seq.empty Seq.empty
            let root = directoryVersion configuration rootId Constants.RootDirectoryPath [| emptyId |] Seq.empty
            let status = graceStatusFromDirectories root [| root; empty |]

            (writeGraceStatusFile status)
                .GetAwaiter()
                .GetResult()

            (upsertObjectCache status.Index.Values)
                .GetAwaiter()
                .GetResult()

            let readBack = (readGraceStatusFile ()).GetAwaiter().GetResult()

            readBack.Index.ContainsKey(emptyId)
            |> should equal true

            let readBackEmpty = readBack.Index[emptyId]

            readBackEmpty.RelativePath
            |> should equal (RelativePath "empty")

            readBackEmpty.Directories.Count |> should equal 0
            readBackEmpty.Files.Count |> should equal 0
            readBackEmpty.Size |> should equal 0L

            readBack.Index[rootId].Directories
            |> Seq.exists (fun directoryVersionId -> directoryVersionId = emptyId)
            |> should equal true

            countLocalStateRows
                configuration.GraceStatusFile
                "SELECT COUNT(*) FROM object_cache_directories WHERE directory_version_id = $directory_version_id;"
                "$directory_version_id"
                $"{emptyId}"
            |> should equal 1

            countLocalStateRows
                configuration.GraceStatusFile
                "SELECT COUNT(*) FROM object_cache_directory_children WHERE parent_directory_version_id = $parent_directory_version_id;"
                "$parent_directory_version_id"
                $"{emptyId}"
            |> should equal 0

            countLocalStateRows
                configuration.GraceStatusFile
                "SELECT COUNT(*) FROM object_cache_directory_files WHERE directory_version_id = $directory_version_id;"
                "$directory_version_id"
                $"{emptyId}"
            |> should equal 0)

    /// Verifies that read-only current-state scans track non-ignored empty directories and skip ignored ones.
    [<Test>]
    let ``read-only current-state scan tracks non-ignored empty directory and skips ignored empty directory`` () =
        let root = Path.Combine(Path.GetTempPath(), $"grace-current-state-scan-{Guid.NewGuid():N}")
        let graceDirectory = Path.Combine(root, Constants.GraceConfigDirectory)

        try
            Directory.CreateDirectory(Path.Combine(root, "empty"))
            |> ignore

            Directory.CreateDirectory(Path.Combine(root, "ignored"))
            |> ignore

            Directory.CreateDirectory(graceDirectory)
            |> ignore

            let previousRoot = rootDirectoryVersion rootDirectoryId rootSha
            let previousStatus = graceStatusFromDirectories previousRoot [| previousRoot |]

            let scanInput =
                {
                    RootDirectory = root
                    GraceDirectory = graceDirectory
                    GraceStatusFile = Path.Combine(graceDirectory, Constants.GraceLocalStateDbFileName)
                    DirectoryIgnoreEntries = [| "ignored" |]
                    FileIgnoreEntries = Array.empty
                }

            match (scanWorkingTreeForDifferencesReadOnly scanInput previousStatus)
                .Result
                with
            | Error error -> Assert.Fail($"Expected scan success, got: {error}")
            | Ok differences ->
                differences
                |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, difference.RelativePath)
                |> should
                    equal
                    [|
                        DifferenceType.Add, FileSystemEntryType.Directory, RelativePath "empty"
                    |]
        finally
            if Directory.Exists(root) then Directory.Delete(root, true)

    /// Verifies that materialization creates empty directories carried by directory-version DTOs.
    [<Test>]
    let ``updateWorkingDirectory materializes empty directory version`` () =
        withTempRepo (fun root configuration ->
            let previousRootId = Guid.NewGuid()
            let updatedRootId = Guid.NewGuid()
            let emptyId = Guid.NewGuid()
            let previousRoot = directoryVersion configuration previousRootId Constants.RootDirectoryPath Seq.empty Seq.empty
            let empty = directoryVersion configuration emptyId (RelativePath "empty") Seq.empty Seq.empty
            let updatedRoot = directoryVersion configuration updatedRootId Constants.RootDirectoryPath [| emptyId |] Seq.empty
            let previousStatus = graceStatusFromDirectories previousRoot [| previousRoot |]
            let updatedStatus = graceStatusFromDirectories updatedRoot [| updatedRoot; empty |]

            let directoryVersionDtos =
                [|
                    { DirectoryVersionDto.Default with DirectoryVersion = updatedRoot.ToDirectoryVersion }
                    { DirectoryVersionDto.Default with DirectoryVersion = empty.ToDirectoryVersion }
                |]

            updateWorkingDirectory previousStatus updatedStatus directoryVersionDtos correlationId
            |> fun task -> task.GetAwaiter().GetResult()

            let emptyPath = Path.Combine(root, "empty")

            Directory.Exists(emptyPath) |> should equal true

            Directory.EnumerateFileSystemEntries(emptyPath)
            |> Seq.isEmpty
            |> should equal true)

    /// Builds default operations test data used to exercise CLI current State Capture behavior.
    let private defaultOperations branchDto =
        {
            GetBranch = fun () -> Task.FromResult(Ok(GraceReturnValue.Create branchDto correlationId))
            GetGraceWatchStatus = fun () -> Task.FromResult(None)
            ReadGraceStatus = fun () -> Task.FromResult(graceStatus rootDirectoryId rootSha)
            ScanForDifferences = fun _ -> Task.FromResult(List<FileSystemDifference>())
            CopyUpdatedFilesToObjectCache = fun _ -> Task.FromResult(Seq.empty<LocalFileVersion>)
            BuildUpdatedGraceStatus = fun status _ -> Task.FromResult(status, List<LocalDirectoryVersion>())
            UploadFileVersions = fun _ -> Task.FromResult(Ok Array.empty<FileVersion>)
            GetSavedDirectoryVersions = fun _ -> Task.FromResult(Ok Array.empty<DirectoryVersion>)
            UploadDirectoryVersions = fun _ -> Task.FromResult(Ok())
            ApplyGraceStatusIncremental = fun _ _ _ -> Task.FromResult(())
            CreateSaveReference = fun _ _ -> Task.FromResult(Ok(Guid.NewGuid()))
        }

    /// Verifies that explicit reference bypasses local state capture.
    [<Test>]
    let ``explicit reference bypasses local state capture`` () =
        let explicitReferenceId = Guid.NewGuid()
        /// Tracks get Branch Called changes so this scenario can assert the resulting side effect explicitly.
        let mutable getBranchCalled = false

        let operations =
            { defaultOperations (branch true ReferenceDto.Default) with
                GetBranch =
                    fun () ->
                        getBranchCalled <- true
                        Task.FromResult(Ok(GraceReturnValue.Create BranchDto.Default correlationId))
            }

        let result =
            (resolveCliCurrentStateTargetReference operations (Some explicitReferenceId) branchAnnotateImplicitSaveMessage correlationId)
                .Result

        match result with
        | Ok captured ->
            captured.TargetReferenceId
            |> should equal explicitReferenceId

            captured.Source |> should equal ExplicitReference
            captured.CreatedSaveMessage |> should equal None
            getBranchCalled |> should equal false
        | Error error -> Assert.Fail($"Expected explicit reference success, got: {error.Error}")

    /// Verifies that grace watch state uses matching existing branch reference.
    [<Test>]
    let ``GraceWatch state uses matching existing branch reference`` () =
        let latest = referenceDto savedReferenceId rootDirectoryId rootSha

        let watchStatus: GraceWatchStatus =
            {
                UpdatedAt = Grace.Shared.Utilities.getCurrentInstant ()
                IsStartupClaim = false
                RepositoryId = RepositoryId.Empty
                RepositoryName = RepositoryName String.Empty
                BranchId = BranchId.Empty
                BranchName = BranchName String.Empty
                RootDirectory = Environment.CurrentDirectory
                HasPendingWatchWork = false
                IsWorkingTreeClean = true
                RootDirectoryId = rootDirectoryId
                RootDirectorySha256Hash = rootSha
                RootDirectoryBlake3Hash = rootBlake3
                LastFileUploadInstant = NodaTime.Instant.MinValue
                LastDirectoryVersionInstant = NodaTime.Instant.MinValue
                DirectoryIds = HashSet<DirectoryVersionId>([| rootDirectoryId |])
            }

        /// Tracks read Status Called changes so this scenario can assert the resulting side effect explicitly.
        let mutable readStatusCalled = false

        let operations =
            { defaultOperations (branch true latest) with
                GetGraceWatchStatus = fun () -> Task.FromResult(Some watchStatus)
                ReadGraceStatus =
                    fun () ->
                        readStatusCalled <- true
                        Task.FromResult(graceStatus rootDirectoryId rootSha)
            }

        let result =
            (resolveCliCurrentStateTargetReference operations None branchAnnotateImplicitSaveMessage correlationId)
                .Result

        match result with
        | Ok captured ->
            captured.TargetReferenceId
            |> should equal savedReferenceId

            captured.RootDirectoryId
            |> should equal rootDirectoryId

            captured.RootDirectoryBlake3Hash
            |> should equal rootBlake3

            captured.Source |> should equal GraceWatch
            readStatusCalled |> should equal false
        | Error error -> Assert.Fail($"Expected GraceWatch success, got: {error.Error}")

    /// Verifies that unchanged local state reuses same root reference when local blake3 is unknown.
    [<Test>]
    let ``unchanged local state reuses same root reference when local BLAKE3 is unknown`` () =
        let latest = referenceDto savedReferenceId rootDirectoryId rootSha
        let unknownRootBlake3 = Blake3Hash String.Empty
        /// Tracks create Save Called changes so this scenario can assert the resulting side effect explicitly.
        let mutable createSaveCalled = false

        let operations =
            { defaultOperations (branch false latest) with
                ReadGraceStatus = fun () -> Task.FromResult(graceStatusWithRootBlake3 rootDirectoryId rootSha unknownRootBlake3)
                CreateSaveReference =
                    fun _ _ ->
                        createSaveCalled <- true
                        Task.FromResult(Ok(Guid.NewGuid()))
            }

        let result =
            (resolveCliCurrentStateTargetReference operations None branchAnnotateImplicitSaveMessage correlationId)
                .Result

        match result with
        | Ok captured ->
            captured.TargetReferenceId
            |> should equal savedReferenceId

            captured.RootDirectoryId
            |> should equal rootDirectoryId

            captured.RootDirectorySha256Hash
            |> should equal rootSha

            captured.RootDirectoryBlake3Hash
            |> should equal rootBlake3

            captured.Source |> should equal ExistingReference
            createSaveCalled |> should equal false
        | Error error -> Assert.Fail($"Expected unknown local BLAKE3 to reuse existing reference, got: {error.Error}")

    /// Verifies that unchanged local state does not reuse same sha 256 reference with different blake3.
    [<Test>]
    let ``unchanged local state does not reuse same SHA-256 reference with different BLAKE3`` () =
        let staleReferenceId = Guid.NewGuid()
        let createdSaveId = Guid.NewGuid()
        let differentBlake3 = Blake3Hash "different-root-blake3"
        let staleReference = { referenceDto staleReferenceId rootDirectoryId rootSha with Blake3Hash = differentBlake3 }
        /// Tracks created Save Root changes so this scenario can assert the resulting side effect explicitly.
        let mutable createdSaveRoot = Unchecked.defaultof<LocalDirectoryVersion>

        let operations =
            { defaultOperations (branch true staleReference) with
                CreateSaveReference =
                    fun rootDirectoryVersion _ ->
                        createdSaveRoot <- rootDirectoryVersion
                        Task.FromResult(Ok(createdSaveId))
            }

        let result =
            (resolveCliCurrentStateTargetReference operations None branchAnnotateImplicitSaveMessage correlationId)
                .Result

        match result with
        | Ok captured ->
            captured.TargetReferenceId
            |> should equal createdSaveId

            captured.TargetReferenceId
            |> should not' (equal staleReferenceId)

            captured.RootDirectoryId
            |> should equal rootDirectoryId

            captured.RootDirectorySha256Hash
            |> should equal rootSha

            captured.RootDirectoryBlake3Hash
            |> should equal rootBlake3

            captured.Source |> should equal CreatedSave

            createdSaveRoot.Sha256Hash |> should equal rootSha

            createdSaveRoot.Blake3Hash
            |> should equal rootBlake3
        | Error error -> Assert.Fail($"Expected mismatched BLAKE3 reference to create a new save, got: {error.Error}")

    /// Verifies that unchanged child branch does not use parent based on reference.
    [<Test>]
    let ``unchanged child branch does not use parent BasedOn reference`` () =
        let parentReferenceId = Guid.NewGuid()
        let createdSaveId = Guid.NewGuid()
        /// Tracks created Save changes so this scenario can assert the resulting side effect explicitly.
        let mutable createdSave = false

        let parentBasedOn = { referenceDto parentReferenceId rootDirectoryId rootSha with BranchId = parentBranchId }

        let branchDto = { branch true ReferenceDto.Default with BasedOn = parentBasedOn }

        let operations =
            { defaultOperations branchDto with
                CreateSaveReference =
                    fun rootDirectoryVersion _ ->
                        createdSave <- true

                        rootDirectoryVersion.DirectoryVersionId
                        |> should equal rootDirectoryId

                        rootDirectoryVersion.Sha256Hash
                        |> should equal rootSha

                        rootDirectoryVersion.Blake3Hash
                        |> should equal rootBlake3

                        Task.FromResult(Ok(createdSaveId))
            }

        let result =
            (resolveCliCurrentStateTargetReference operations None branchAnnotateImplicitSaveMessage correlationId)
                .Result

        match result with
        | Ok captured ->
            captured.TargetReferenceId
            |> should equal createdSaveId

            captured.Source |> should equal CreatedSave
            createdSave |> should equal true
        | Error error -> Assert.Fail($"Expected implicit Save success, got: {error.Error}")

    /// Verifies that local changes auto create save with annotate message.
    [<Test>]
    let ``local changes auto-create save with annotate message`` () =
        let updatedRootId = Guid.NewGuid()
        let updatedRootSha = Sha256Hash "updated-root-sha"
        let createdSaveId = Guid.NewGuid()
        /// Tracks uploaded Directories changes so this scenario can assert the resulting side effect explicitly.
        let mutable uploadedDirectories = false
        /// Tracks applied Status changes so this scenario can assert the resulting side effect explicitly.
        let mutable appliedStatus = false
        /// Tracks save Message changes so this scenario can assert the resulting side effect explicitly.
        let mutable saveMessage = String.Empty
        /// Tracks created Save Root changes so this scenario can assert the resulting side effect explicitly.
        let mutable createdSaveRoot = Unchecked.defaultof<LocalDirectoryVersion>

        let differences =
            List<FileSystemDifference>(
                [|
                    FileSystemDifference.Create DifferenceType.Add FileSystemEntryType.Directory (RelativePath "new-folder")
                |]
            )

        let updatedStatus = graceStatus updatedRootId updatedRootSha
        let updatedRoot = rootDirectoryVersion updatedRootId updatedRootSha

        let operations =
            { defaultOperations (branch true ReferenceDto.Default) with
                ScanForDifferences = fun _ -> Task.FromResult(differences)
                BuildUpdatedGraceStatus = fun _ _ -> Task.FromResult(updatedStatus, List<LocalDirectoryVersion>([| updatedRoot |]))
                UploadDirectoryVersions =
                    fun _ ->
                        uploadedDirectories <- true
                        Task.FromResult(Ok())
                ApplyGraceStatusIncremental =
                    fun _ _ _ ->
                        appliedStatus <- true
                        Task.FromResult(())
                CreateSaveReference =
                    fun rootDirectoryVersion message ->
                        createdSaveRoot <- rootDirectoryVersion
                        saveMessage <- message
                        Task.FromResult(Ok(createdSaveId))
            }

        let result =
            (resolveCliCurrentStateTargetReference operations None branchAnnotateImplicitSaveMessage correlationId)
                .Result

        match result with
        | Ok captured ->
            captured.TargetReferenceId
            |> should equal createdSaveId

            captured.RootDirectoryId
            |> should equal updatedRootId

            captured.RootDirectoryBlake3Hash
            |> should equal createdSaveRoot.Blake3Hash

            createdSaveRoot.Blake3Hash
            |> should not' (equal (Blake3Hash String.Empty))

            captured.Source |> should equal CreatedSave

            captured.CreatedSaveMessage
            |> should equal (Some branchAnnotateImplicitSaveMessage)

            saveMessage
            |> should equal branchAnnotateImplicitSaveMessage

            uploadedDirectories |> should equal true
            appliedStatus |> should equal true
        | Error error -> Assert.Fail($"Expected auto-save success, got: {error.Error}")

    /// Verifies that delete only file changes create save after directory upload and then apply local status.
    [<Test>]
    let ``delete-only file changes create save after directory upload and then apply local status`` () =
        let updatedRootId = Guid.NewGuid()
        let updatedRootSha = Sha256Hash "updated-root-delete-sha"
        let createdSaveId = Guid.NewGuid()
        let saveMessage = "Delete deleted.txt"
        let operationOrder = ResizeArray<string>()
        /// Tracks uploaded File Versions changes so this scenario can assert the resulting side effect explicitly.
        let mutable uploadedFileVersions = Seq.empty<LocalFileVersion>
        /// Tracks saved Directory Versions changes so this scenario can assert the resulting side effect explicitly.
        let mutable savedDirectoryVersions = List<DirectoryVersion>()
        /// Tracks created Save Root changes so this scenario can assert the resulting side effect explicitly.
        let mutable createdSaveRoot = Unchecked.defaultof<LocalDirectoryVersion>
        /// Tracks created Save Message changes so this scenario can assert the resulting side effect explicitly.
        let mutable createdSaveMessage = String.Empty
        /// Tracks applied Status changes so this scenario can assert the resulting side effect explicitly.
        let mutable appliedStatus = GraceStatus.Default
        /// Tracks applied Directory Versions changes so this scenario can assert the resulting side effect explicitly.
        let mutable appliedDirectoryVersions = Seq.empty<LocalDirectoryVersion>
        /// Tracks applied Differences changes so this scenario can assert the resulting side effect explicitly.
        let mutable appliedDifferences = Seq.empty<FileSystemDifference>

        let differences =
            List<FileSystemDifference>(
                [|
                    FileSystemDifference.Create DifferenceType.Delete FileSystemEntryType.File (RelativePath "deleted.txt")
                |]
            )

        let updatedStatus = graceStatus updatedRootId updatedRootSha

        let updatedRoot = rootDirectoryVersion updatedRootId updatedRootSha

        let operations =
            { defaultOperations (branch true ReferenceDto.Default) with
                ScanForDifferences = fun _ -> Task.FromResult(differences)
                BuildUpdatedGraceStatus = fun _ _ -> Task.FromResult(updatedStatus, List<LocalDirectoryVersion>([| updatedRoot |]))
                UploadFileVersions =
                    fun fileVersions ->
                        uploadedFileVersions <- fileVersions |> Seq.toArray
                        Task.FromResult(Ok Array.empty<FileVersion>)
                UploadDirectoryVersions =
                    fun directoryVersions ->
                        operationOrder.Add("upload-directories")
                        savedDirectoryVersions <- directoryVersions
                        Task.FromResult(Ok())
                CreateSaveReference =
                    fun rootDirectoryVersion message ->
                        operationOrder.Add("create-save")
                        createdSaveRoot <- rootDirectoryVersion
                        createdSaveMessage <- message
                        Task.FromResult(Ok(createdSaveId))
                ApplyGraceStatusIncremental =
                    fun status directoryVersions applied ->
                        operationOrder.Add("apply-status")
                        appliedStatus <- status
                        appliedDirectoryVersions <- directoryVersions |> Seq.toArray
                        appliedDifferences <- applied |> Seq.toArray
                        Task.FromResult(())
            }

        let result =
            (resolveCliCurrentStateTargetReference operations None saveMessage correlationId)
                .Result

        match result with
        | Ok captured ->
            captured.TargetReferenceId
            |> should equal createdSaveId

            captured.Source |> should equal CreatedSave

            captured.RootDirectoryId
            |> should equal updatedRootId

            uploadedFileVersions
            |> Seq.isEmpty
            |> should equal true

            savedDirectoryVersions.Count |> should equal 1

            savedDirectoryVersions[0].DirectoryVersionId
            |> should equal updatedRootId

            createdSaveRoot.DirectoryVersionId
            |> should equal updatedRootId

            createdSaveMessage |> should equal saveMessage

            appliedStatus.RootDirectoryId
            |> should equal updatedRootId

            appliedDirectoryVersions
            |> Seq.map (fun directoryVersion -> directoryVersion.DirectoryVersionId)
            |> should equal [| updatedRootId |]

            appliedDifferences
            |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, difference.RelativePath)
            |> should
                equal
                [|
                    DifferenceType.Delete, FileSystemEntryType.File, RelativePath "deleted.txt"
                |]

            operationOrder
            |> Seq.toArray
            |> should
                equal
                [|
                    "upload-directories"
                    "create-save"
                    "apply-status"
                |]
        | Error error -> Assert.Fail($"Expected delete-only auto-save success, got: {error.Error}")

    /// Verifies that directory only deletes still create save with updated root directory version.
    [<Test>]
    let ``directory-only deletes still create save with updated root directory version`` () =
        let updatedRootId = Guid.NewGuid()
        let updatedRootSha = Sha256Hash "updated-root-directory-delete-sha"
        let createdSaveId = Guid.NewGuid()
        let operationOrder = ResizeArray<string>()
        /// Tracks created Save Root changes so this scenario can assert the resulting side effect explicitly.
        let mutable createdSaveRoot = Unchecked.defaultof<LocalDirectoryVersion>
        /// Tracks created Save Message changes so this scenario can assert the resulting side effect explicitly.
        let mutable createdSaveMessage = "not-empty"
        /// Tracks applied Status changes so this scenario can assert the resulting side effect explicitly.
        let mutable appliedStatus = GraceStatus.Default

        let differences =
            List<FileSystemDifference>(
                [|
                    FileSystemDifference.Create DifferenceType.Delete FileSystemEntryType.Directory (RelativePath "empty")
                |]
            )

        let updatedStatus = graceStatus updatedRootId updatedRootSha
        let updatedRoot = rootDirectoryVersion updatedRootId updatedRootSha

        let operations =
            { defaultOperations (branch true ReferenceDto.Default) with
                ScanForDifferences = fun _ -> Task.FromResult(differences)
                BuildUpdatedGraceStatus = fun _ _ -> Task.FromResult(updatedStatus, List<LocalDirectoryVersion>([| updatedRoot |]))
                UploadFileVersions =
                    fun fileVersions ->
                        fileVersions |> Seq.isEmpty |> should equal true
                        Task.FromResult(Ok Array.empty<FileVersion>)
                UploadDirectoryVersions =
                    fun directoryVersions ->
                        operationOrder.Add("upload-directories")
                        directoryVersions.Count |> should equal 1

                        directoryVersions[0].DirectoryVersionId
                        |> should equal updatedRootId

                        Task.FromResult(Ok())
                CreateSaveReference =
                    fun rootDirectoryVersion message ->
                        operationOrder.Add("create-save")
                        createdSaveRoot <- rootDirectoryVersion
                        createdSaveMessage <- message
                        Task.FromResult(Ok(createdSaveId))
                ApplyGraceStatusIncremental =
                    fun status _ _ ->
                        operationOrder.Add("apply-status")
                        appliedStatus <- status
                        Task.FromResult(())
            }

        let result =
            (resolveCliCurrentStateTargetReference operations None String.Empty correlationId)
                .Result

        match result with
        | Ok captured ->
            captured.TargetReferenceId
            |> should equal createdSaveId

            captured.Source |> should equal CreatedSave

            captured.RootDirectoryId
            |> should equal updatedRootId

            createdSaveRoot.DirectoryVersionId
            |> should equal updatedRootId

            createdSaveMessage |> should equal String.Empty

            appliedStatus.RootDirectoryId
            |> should equal updatedRootId

            operationOrder
            |> Seq.toArray
            |> should
                equal
                [|
                    "upload-directories"
                    "create-save"
                    "apply-status"
                |]
        | Error error -> Assert.Fail($"Expected directory-only delete auto-save success, got: {error.Error}")

    /// Verifies that changed file upload selection comes from updated directory versions.
    [<Test>]
    let ``changed file upload selection comes from updated directory versions`` () =
        let changedPath = RelativePath "src/cached-large.bin"
        let unchangedPath = RelativePath "src/unchanged-large.bin"

        let changedFile =
            LocalFileVersion.CreateWithHashes
                changedPath
                (Sha256Hash "changed-file-sha")
                (Blake3Hash "changed-file-blake3")
                true
                42L
                (Grace.Shared.Utilities.getCurrentInstant ())
                true
                DateTime.UtcNow

        let unchangedFile =
            LocalFileVersion.CreateWithHashes
                unchangedPath
                (Sha256Hash "unchanged-file-sha")
                (Blake3Hash "unchanged-file-blake3")
                true
                43L
                (Grace.Shared.Utilities.getCurrentInstant ())
                true
                DateTime.UtcNow

        let differences =
            List<FileSystemDifference>(
                [|
                    FileSystemDifference.Create DifferenceType.Change FileSystemEntryType.File changedPath
                    FileSystemDifference.Create DifferenceType.Add FileSystemEntryType.Directory (RelativePath "src/new-folder")
                |]
            )

        let updatedRoot =
            LocalDirectoryVersion.CreateWithHashes
                rootDirectoryId
                OwnerId.Empty
                OrganizationId.Empty
                RepositoryId.Empty
                Constants.RootDirectoryPath
                rootSha
                rootBlake3
                (List<DirectoryVersionId>())
                (List<LocalFileVersion>([| changedFile; unchangedFile |]))
                (changedFile.Size + unchangedFile.Size)
                DateTime.UtcNow

        let selected = getChangedFileVersionsReferencedByUpdatedDirectories differences (List<LocalDirectoryVersion>([| updatedRoot |]))

        selected |> should equal [ changedFile ]

    /// Verifies that local changes upload changed file versions already present in object cache.
    [<Test>]
    let ``local changes upload changed file versions already present in object cache`` () =
        let updatedRootId = Guid.NewGuid()
        let updatedRootSha = Sha256Hash "updated-root-sha"
        let createdSaveId = Guid.NewGuid()
        let changedPath = RelativePath "src/file.txt"

        let changedFile =
            LocalFileVersion.CreateWithHashes
                changedPath
                (Sha256Hash "changed-file-sha")
                (Blake3Hash "changed-file-blake3")
                false
                12L
                (Grace.Shared.Utilities.getCurrentInstant ())
                true
                DateTime.UtcNow

        /// Tracks uploaded Files changes so this scenario can assert the resulting side effect explicitly.
        let mutable uploadedFiles = List<LocalFileVersion>()

        let differences =
            List<FileSystemDifference>(
                [|
                    FileSystemDifference.Create DifferenceType.Change FileSystemEntryType.File changedPath
                |]
            )

        let updatedStatus = graceStatus updatedRootId updatedRootSha

        let updatedRoot =
            LocalDirectoryVersion.CreateWithHashes
                updatedRootId
                OwnerId.Empty
                OrganizationId.Empty
                RepositoryId.Empty
                Constants.RootDirectoryPath
                updatedRootSha
                rootBlake3
                (List<DirectoryVersionId>())
                (List<LocalFileVersion>([| changedFile |]))
                changedFile.Size
                DateTime.UtcNow

        let operations =
            { defaultOperations (branch true ReferenceDto.Default) with
                ScanForDifferences = fun _ -> Task.FromResult(differences)
                CopyUpdatedFilesToObjectCache = fun _ -> Task.FromResult(Seq.empty<LocalFileVersion>)
                BuildUpdatedGraceStatus = fun _ _ -> Task.FromResult(updatedStatus, List<LocalDirectoryVersion>([| updatedRoot |]))
                UploadFileVersions =
                    fun fileVersions ->
                        uploadedFiles <- List<LocalFileVersion>(fileVersions)

                        Task.FromResult(
                            Ok(
                                fileVersions
                                |> Seq.map (fun fileVersion -> fileVersion.ToFileVersion)
                                |> Seq.toArray
                            )
                        )
                CreateSaveReference = fun _ _ -> Task.FromResult(Ok(createdSaveId))
            }

        let result =
            (resolveCliCurrentStateTargetReference operations None branchAnnotateImplicitSaveMessage correlationId)
                .Result

        match result with
        | Ok captured ->
            captured.TargetReferenceId
            |> should equal createdSaveId

            uploadedFiles.Count |> should equal 1

            uploadedFiles[0].RelativePath
            |> should equal changedPath

            uploadedFiles[0].Sha256Hash
            |> should equal changedFile.Sha256Hash
        | Error error -> Assert.Fail($"Expected auto-save success, got: {error.Error}")

    /// Verifies that local changes save directory versions with enriched uploaded file versions.
    [<Test>]
    let ``local changes save directory versions with enriched uploaded file versions`` () =
        let updatedRootId = Guid.NewGuid()
        let updatedRootSha = Sha256Hash "updated-root-sha"
        let createdSaveId = Guid.NewGuid()
        let changedPath = RelativePath "src/large.bin"
        let payload = Encoding.UTF8.GetBytes("large manifest-backed payload")

        let changedFile =
            LocalFileVersion.CreateWithHashes
                changedPath
                (sha256Hash payload)
                (Blake3Hash(ContentAddress.computeBlake3Hex payload))
                true
                (int64 payload.Length)
                (Grace.Shared.Utilities.getCurrentInstant ())
                true
                DateTime.UtcNow

        let uploadedFile = changedFile.ToFileVersion
        uploadedFile.Blake3Hash <- Blake3Hash(ContentAddress.computeBlake3Hex payload)
        uploadedFile.ContentReference <- manifestReferenceFor payload

        /// Tracks saved Directory Files changes so this scenario can assert the resulting side effect explicitly.
        let mutable savedDirectoryFiles = List<FileVersion>()

        let differences =
            List<FileSystemDifference>(
                [|
                    FileSystemDifference.Create DifferenceType.Change FileSystemEntryType.File changedPath
                |]
            )

        let updatedStatus = graceStatus updatedRootId updatedRootSha

        let updatedRoot =
            LocalDirectoryVersion.CreateWithHashes
                updatedRootId
                OwnerId.Empty
                OrganizationId.Empty
                RepositoryId.Empty
                Constants.RootDirectoryPath
                updatedRootSha
                rootBlake3
                (List<DirectoryVersionId>())
                (List<LocalFileVersion>([| changedFile |]))
                changedFile.Size
                DateTime.UtcNow

        let operations =
            { defaultOperations (branch true ReferenceDto.Default) with
                ScanForDifferences = fun _ -> Task.FromResult(differences)
                BuildUpdatedGraceStatus = fun _ _ -> Task.FromResult(updatedStatus, List<LocalDirectoryVersion>([| updatedRoot |]))
                UploadFileVersions = fun _ -> Task.FromResult(Ok [| uploadedFile |])
                UploadDirectoryVersions =
                    fun directoryVersions ->
                        savedDirectoryFiles <- directoryVersions[0].Files
                        Task.FromResult(Ok())
                CreateSaveReference = fun _ _ -> Task.FromResult(Ok(createdSaveId))
            }

        let result =
            (resolveCliCurrentStateTargetReference operations None branchAnnotateImplicitSaveMessage correlationId)
                .Result

        match result with
        | Ok captured ->
            captured.TargetReferenceId
            |> should equal createdSaveId

            savedDirectoryFiles.Count |> should equal 1

            savedDirectoryFiles[0].Blake3Hash
            |> should equal uploadedFile.Blake3Hash

            savedDirectoryFiles[0]
                .ContentReference
                .ReferenceType
            |> should equal FileContentReferenceType.FileManifest
        | Error error -> Assert.Fail($"Expected auto-save success, got: {error.Error}")

    /// Verifies that local changes preserve saved manifest references for unchanged sibling files.
    [<Test>]
    let ``local changes preserve saved manifest references for unchanged sibling files`` () =
        let updatedRootId = Guid.NewGuid()
        let updatedRootSha = Sha256Hash "updated-root-sha"
        let createdSaveId = Guid.NewGuid()
        let changedPath = RelativePath "src/changed.txt"
        let unchangedPath = RelativePath "src/unchanged-large.bin"
        let changedPayload = Encoding.UTF8.GetBytes("changed whole-file payload")
        let unchangedPayload = Encoding.UTF8.GetBytes("unchanged manifest-backed payload")

        let changedFile =
            LocalFileVersion.CreateWithHashes
                changedPath
                (sha256Hash changedPayload)
                (Blake3Hash(ContentAddress.computeBlake3Hex changedPayload))
                false
                (int64 changedPayload.Length)
                (Grace.Shared.Utilities.getCurrentInstant ())
                true
                DateTime.UtcNow

        let unchangedFile =
            LocalFileVersion.CreateWithHashes
                unchangedPath
                (sha256Hash unchangedPayload)
                (Blake3Hash(ContentAddress.computeBlake3Hex unchangedPayload))
                true
                (int64 unchangedPayload.Length)
                (Grace.Shared.Utilities.getCurrentInstant ())
                true
                DateTime.UtcNow

        let uploadedChangedFile = changedFile.ToFileVersion
        let savedManifestFile = unchangedFile.ToFileVersion
        savedManifestFile.ContentReference <- manifestReferenceFor unchangedPayload

        let savedRoot =
            DirectoryVersion.CreateWithHashes
                rootDirectoryId
                OwnerId.Empty
                OrganizationId.Empty
                RepositoryId.Empty
                Constants.RootDirectoryPath
                rootSha
                rootBlake3
                (List<DirectoryVersionId>())
                (List<FileVersion>([| savedManifestFile |]))
                unchangedFile.Size

        /// Tracks saved Directory Files changes so this scenario can assert the resulting side effect explicitly.
        let mutable savedDirectoryFiles = List<FileVersion>()

        let differences =
            List<FileSystemDifference>(
                [|
                    FileSystemDifference.Create DifferenceType.Change FileSystemEntryType.File changedPath
                |]
            )

        let updatedStatus = graceStatus updatedRootId updatedRootSha

        let updatedRoot =
            LocalDirectoryVersion.CreateWithHashes
                updatedRootId
                OwnerId.Empty
                OrganizationId.Empty
                RepositoryId.Empty
                Constants.RootDirectoryPath
                updatedRootSha
                rootBlake3
                (List<DirectoryVersionId>())
                (List<LocalFileVersion>([| changedFile; unchangedFile |]))
                (changedFile.Size + unchangedFile.Size)
                DateTime.UtcNow

        let operations =
            { defaultOperations (branch true ReferenceDto.Default) with
                ScanForDifferences = fun _ -> Task.FromResult(differences)
                BuildUpdatedGraceStatus = fun _ _ -> Task.FromResult(updatedStatus, List<LocalDirectoryVersion>([| updatedRoot |]))
                UploadFileVersions = fun _ -> Task.FromResult(Ok [| uploadedChangedFile |])
                GetSavedDirectoryVersions = fun _ -> Task.FromResult(Ok [| savedRoot |])
                UploadDirectoryVersions =
                    fun directoryVersions ->
                        savedDirectoryFiles <- directoryVersions[0].Files
                        Task.FromResult(Ok())
                CreateSaveReference = fun _ _ -> Task.FromResult(Ok(createdSaveId))
            }

        let result =
            (resolveCliCurrentStateTargetReference operations None branchAnnotateImplicitSaveMessage correlationId)
                .Result

        match result with
        | Ok captured ->
            captured.TargetReferenceId
            |> should equal createdSaveId

            savedDirectoryFiles.Count |> should equal 2

            let unchangedSavedFile =
                savedDirectoryFiles
                |> Seq.find (fun fileVersion -> fileVersion.RelativePath = unchangedPath)

            unchangedSavedFile.ContentReference.ReferenceType
            |> should equal FileContentReferenceType.FileManifest
        | Error error -> Assert.Fail($"Expected auto-save success, got: {error.Error}")

    /// Verifies that local changes preserve trusted manifest reference when unchanged sibling blake3 is unknown.
    [<Test>]
    let ``local changes preserve trusted manifest reference when unchanged sibling BLAKE3 is unknown`` () =
        let updatedRootId = Guid.NewGuid()
        let updatedRootSha = Sha256Hash "updated-root-sha"
        let createdSaveId = Guid.NewGuid()
        let changedPath = RelativePath "src/changed.txt"
        let unchangedPath = RelativePath "src/unknown-blake3-large.bin"
        let changedPayload = Encoding.UTF8.GetBytes("changed whole-file payload")
        let unchangedPayload = Encoding.UTF8.GetBytes("unchanged manifest-backed payload with unknown local blake3")

        let changedFile =
            LocalFileVersion.CreateWithHashes
                changedPath
                (sha256Hash changedPayload)
                (Blake3Hash(ContentAddress.computeBlake3Hex changedPayload))
                false
                (int64 changedPayload.Length)
                (Grace.Shared.Utilities.getCurrentInstant ())
                true
                DateTime.UtcNow

        let unchangedLocalFile =
            LocalFileVersion.CreateWithHashes
                unchangedPath
                (sha256Hash unchangedPayload)
                (Blake3Hash String.Empty)
                true
                (int64 unchangedPayload.Length)
                (Grace.Shared.Utilities.getCurrentInstant ())
                true
                DateTime.UtcNow

        let trustedUnchangedFile =
            LocalFileVersion.CreateWithHashes
                unchangedPath
                (sha256Hash unchangedPayload)
                (Blake3Hash(ContentAddress.computeBlake3Hex unchangedPayload))
                true
                (int64 unchangedPayload.Length)
                (Grace.Shared.Utilities.getCurrentInstant ())
                true
                DateTime.UtcNow

        let uploadedChangedFile = changedFile.ToFileVersion
        let savedManifestFile = trustedUnchangedFile.ToFileVersion
        savedManifestFile.ContentReference <- manifestReferenceFor unchangedPayload

        let savedRoot =
            DirectoryVersion.CreateWithHashes
                rootDirectoryId
                OwnerId.Empty
                OrganizationId.Empty
                RepositoryId.Empty
                Constants.RootDirectoryPath
                rootSha
                rootBlake3
                (List<DirectoryVersionId>())
                (List<FileVersion>([| savedManifestFile |]))
                trustedUnchangedFile.Size

        /// Tracks saved Directory Files changes so this scenario can assert the resulting side effect explicitly.
        let mutable savedDirectoryFiles = List<FileVersion>()

        let differences =
            List<FileSystemDifference>(
                [|
                    FileSystemDifference.Create DifferenceType.Change FileSystemEntryType.File changedPath
                |]
            )

        let updatedStatus = graceStatus updatedRootId updatedRootSha

        let updatedRoot =
            LocalDirectoryVersion.CreateWithHashes
                updatedRootId
                OwnerId.Empty
                OrganizationId.Empty
                RepositoryId.Empty
                Constants.RootDirectoryPath
                updatedRootSha
                rootBlake3
                (List<DirectoryVersionId>())
                (List<LocalFileVersion>([| changedFile; unchangedLocalFile |]))
                (changedFile.Size + unchangedLocalFile.Size)
                DateTime.UtcNow

        let operations =
            { defaultOperations (branch true ReferenceDto.Default) with
                ScanForDifferences = fun _ -> Task.FromResult(differences)
                BuildUpdatedGraceStatus = fun _ _ -> Task.FromResult(updatedStatus, List<LocalDirectoryVersion>([| updatedRoot |]))
                UploadFileVersions = fun _ -> Task.FromResult(Ok [| uploadedChangedFile |])
                GetSavedDirectoryVersions = fun _ -> Task.FromResult(Ok [| savedRoot |])
                UploadDirectoryVersions =
                    fun directoryVersions ->
                        savedDirectoryFiles <- directoryVersions[0].Files
                        Task.FromResult(Ok())
                CreateSaveReference = fun _ _ -> Task.FromResult(Ok(createdSaveId))
            }

        let result =
            (resolveCliCurrentStateTargetReference operations None branchAnnotateImplicitSaveMessage correlationId)
                .Result

        match result with
        | Ok captured ->
            captured.TargetReferenceId
            |> should equal createdSaveId

            let unchangedSavedFile =
                savedDirectoryFiles
                |> Seq.find (fun fileVersion -> fileVersion.RelativePath = unchangedPath)

            unchangedSavedFile.Blake3Hash
            |> should equal trustedUnchangedFile.Blake3Hash

            unchangedSavedFile.ContentReference.ReferenceType
            |> should equal FileContentReferenceType.FileManifest
        | Error error -> Assert.Fail($"Expected unknown local BLAKE3 manifest preservation success, got: {error.Error}")

    /// Verifies that local changes do not preserve trusted manifest reference when populated blake3 conflicts.
    [<Test>]
    let ``local changes do not preserve trusted manifest reference when populated BLAKE3 conflicts`` () =
        let updatedRootId = Guid.NewGuid()
        let updatedRootSha = Sha256Hash "updated-root-sha"
        let createdSaveId = Guid.NewGuid()
        let changedPath = RelativePath "src/changed.txt"
        let unchangedPath = RelativePath "src/conflicting-blake3-large.bin"
        let changedPayload = Encoding.UTF8.GetBytes("changed whole-file payload")
        let unchangedPayload = Encoding.UTF8.GetBytes("unchanged manifest-backed payload with conflicting local blake3")

        let changedFile =
            LocalFileVersion.CreateWithHashes
                changedPath
                (sha256Hash changedPayload)
                (Blake3Hash(ContentAddress.computeBlake3Hex changedPayload))
                false
                (int64 changedPayload.Length)
                (Grace.Shared.Utilities.getCurrentInstant ())
                true
                DateTime.UtcNow

        let conflictingLocalFile =
            LocalFileVersion.CreateWithHashes
                unchangedPath
                (sha256Hash unchangedPayload)
                (Blake3Hash "conflicting-local-blake3")
                true
                (int64 unchangedPayload.Length)
                (Grace.Shared.Utilities.getCurrentInstant ())
                true
                DateTime.UtcNow

        let trustedUnchangedFile =
            LocalFileVersion.CreateWithHashes
                unchangedPath
                (sha256Hash unchangedPayload)
                (Blake3Hash(ContentAddress.computeBlake3Hex unchangedPayload))
                true
                (int64 unchangedPayload.Length)
                (Grace.Shared.Utilities.getCurrentInstant ())
                true
                DateTime.UtcNow

        let uploadedChangedFile = changedFile.ToFileVersion
        let savedManifestFile = trustedUnchangedFile.ToFileVersion
        savedManifestFile.ContentReference <- manifestReferenceFor unchangedPayload

        let savedRoot =
            DirectoryVersion.CreateWithHashes
                rootDirectoryId
                OwnerId.Empty
                OrganizationId.Empty
                RepositoryId.Empty
                Constants.RootDirectoryPath
                rootSha
                rootBlake3
                (List<DirectoryVersionId>())
                (List<FileVersion>([| savedManifestFile |]))
                trustedUnchangedFile.Size

        /// Tracks saved Directory Files changes so this scenario can assert the resulting side effect explicitly.
        let mutable savedDirectoryFiles = List<FileVersion>()

        let differences =
            List<FileSystemDifference>(
                [|
                    FileSystemDifference.Create DifferenceType.Change FileSystemEntryType.File changedPath
                |]
            )

        let updatedStatus = graceStatus updatedRootId updatedRootSha

        let updatedRoot =
            LocalDirectoryVersion.CreateWithHashes
                updatedRootId
                OwnerId.Empty
                OrganizationId.Empty
                RepositoryId.Empty
                Constants.RootDirectoryPath
                updatedRootSha
                rootBlake3
                (List<DirectoryVersionId>())
                (List<LocalFileVersion>([| changedFile; conflictingLocalFile |]))
                (changedFile.Size + conflictingLocalFile.Size)
                DateTime.UtcNow

        let operations =
            { defaultOperations (branch true ReferenceDto.Default) with
                ScanForDifferences = fun _ -> Task.FromResult(differences)
                BuildUpdatedGraceStatus = fun _ _ -> Task.FromResult(updatedStatus, List<LocalDirectoryVersion>([| updatedRoot |]))
                UploadFileVersions = fun _ -> Task.FromResult(Ok [| uploadedChangedFile |])
                GetSavedDirectoryVersions = fun _ -> Task.FromResult(Ok [| savedRoot |])
                UploadDirectoryVersions =
                    fun directoryVersions ->
                        savedDirectoryFiles <- directoryVersions[0].Files
                        Task.FromResult(Ok())
                CreateSaveReference = fun _ _ -> Task.FromResult(Ok(createdSaveId))
            }

        let result =
            (resolveCliCurrentStateTargetReference operations None branchAnnotateImplicitSaveMessage correlationId)
                .Result

        match result with
        | Ok captured ->
            captured.TargetReferenceId
            |> should equal createdSaveId

            let unchangedSavedFile =
                savedDirectoryFiles
                |> Seq.find (fun fileVersion -> fileVersion.RelativePath = unchangedPath)

            unchangedSavedFile.Blake3Hash
            |> should equal conflictingLocalFile.Blake3Hash

            unchangedSavedFile.ContentReference.ReferenceType
            |> should equal FileContentReferenceType.WholeFileContent
        | Error error -> Assert.Fail($"Expected conflicting local BLAKE3 to avoid manifest preservation, got: {error.Error}")

    /// Verifies that local changes preserve saved manifest references and dual hash save identity.
    [<Test>]
    let ``local changes preserve saved manifest references and dual-hash save identity`` () =
        let updatedRootId = Guid.NewGuid()
        let updatedRootSha = Sha256Hash "updated-root-sha"
        let createdSaveId = Guid.NewGuid()
        let changedPath = RelativePath "src/changed.txt"
        let unchangedPath = RelativePath "src/unchanged-large-proof.bin"
        let changedPayload = Encoding.UTF8.GetBytes("changed whole-file payload")
        let unchangedPayload = Encoding.UTF8.GetBytes("unchanged manifest-backed payload with current dual hashes")

        let changedFile =
            LocalFileVersion.CreateWithHashes
                changedPath
                (sha256Hash changedPayload)
                (Blake3Hash(ContentAddress.computeBlake3Hex changedPayload))
                false
                (int64 changedPayload.Length)
                (Grace.Shared.Utilities.getCurrentInstant ())
                true
                DateTime.UtcNow

        let unchangedFile =
            LocalFileVersion.CreateWithHashes
                unchangedPath
                (sha256Hash unchangedPayload)
                (Blake3Hash(ContentAddress.computeBlake3Hex unchangedPayload))
                true
                (int64 unchangedPayload.Length)
                (Grace.Shared.Utilities.getCurrentInstant ())
                true
                DateTime.UtcNow

        let savedManifestFile = unchangedFile.ToFileVersion
        savedManifestFile.ContentReference <- manifestReferenceFor unchangedPayload

        let savedRoot =
            DirectoryVersion.CreateWithHashes
                rootDirectoryId
                OwnerId.Empty
                OrganizationId.Empty
                RepositoryId.Empty
                Constants.RootDirectoryPath
                rootSha
                rootBlake3
                (List<DirectoryVersionId>())
                (List<FileVersion>([| savedManifestFile |]))
                unchangedFile.Size

        /// Tracks saved Directory Versions changes so this scenario can assert the resulting side effect explicitly.
        let mutable savedDirectoryVersions = List<DirectoryVersion>()
        /// Tracks created Save Root changes so this scenario can assert the resulting side effect explicitly.
        let mutable createdSaveRoot = Unchecked.defaultof<LocalDirectoryVersion>
        /// Tracks applied Status changes so this scenario can assert the resulting side effect explicitly.
        let mutable appliedStatus = GraceStatus.Default
        /// Tracks applied Directory Versions changes so this scenario can assert the resulting side effect explicitly.
        let mutable appliedDirectoryVersions = List<LocalDirectoryVersion>()

        let differences =
            List<FileSystemDifference>(
                [|
                    FileSystemDifference.Create DifferenceType.Change FileSystemEntryType.File changedPath
                |]
            )

        let updatedStatus = graceStatus updatedRootId updatedRootSha

        let updatedRoot =
            LocalDirectoryVersion.CreateWithHashes
                updatedRootId
                OwnerId.Empty
                OrganizationId.Empty
                RepositoryId.Empty
                Constants.RootDirectoryPath
                updatedRootSha
                rootBlake3
                (List<DirectoryVersionId>())
                (List<LocalFileVersion>([| changedFile; unchangedFile |]))
                (changedFile.Size + unchangedFile.Size)
                DateTime.UtcNow

        let operations =
            { defaultOperations (branch true ReferenceDto.Default) with
                ScanForDifferences = fun _ -> Task.FromResult(differences)
                BuildUpdatedGraceStatus = fun _ _ -> Task.FromResult(updatedStatus, List<LocalDirectoryVersion>([| updatedRoot |]))
                UploadFileVersions = fun _ -> Task.FromResult(Ok [| changedFile.ToFileVersion |])
                GetSavedDirectoryVersions = fun _ -> Task.FromResult(Ok [| savedRoot |])
                UploadDirectoryVersions =
                    fun directoryVersions ->
                        savedDirectoryVersions <- directoryVersions
                        Task.FromResult(Ok())
                ApplyGraceStatusIncremental =
                    fun status directoryVersions _ ->
                        appliedStatus <- status
                        appliedDirectoryVersions <- List<LocalDirectoryVersion>(directoryVersions)
                        Task.FromResult(())
                CreateSaveReference =
                    fun rootDirectoryVersion _ ->
                        createdSaveRoot <- rootDirectoryVersion
                        Task.FromResult(Ok(createdSaveId))
            }

        let result =
            (resolveCliCurrentStateTargetReference operations None branchAnnotateImplicitSaveMessage correlationId)
                .Result

        match result with
        | Ok captured ->
            captured.TargetReferenceId
            |> should equal createdSaveId

            captured.RootDirectoryId
            |> should equal updatedRootId

            savedDirectoryVersions.Count |> should equal 1

            let savedDirectoryVersion = savedDirectoryVersions[0]

            savedDirectoryVersion.Files.Count
            |> should equal 2

            let unchangedSavedFile =
                savedDirectoryVersion.Files
                |> Seq.find (fun fileVersion -> fileVersion.RelativePath = unchangedPath)

            unchangedSavedFile.Blake3Hash
            |> should equal savedManifestFile.Blake3Hash

            unchangedSavedFile.ContentReference.ReferenceType
            |> should equal FileContentReferenceType.FileManifest

            createdSaveRoot.Sha256Hash
            |> should equal savedDirectoryVersion.Sha256Hash

            createdSaveRoot.Blake3Hash
            |> should equal savedDirectoryVersion.Blake3Hash

            appliedStatus.RootDirectorySha256Hash
            |> should equal savedDirectoryVersion.Sha256Hash

            appliedStatus.RootDirectoryBlake3Hash
            |> should equal savedDirectoryVersion.Blake3Hash

            let appliedRoot =
                appliedDirectoryVersions
                |> Seq.find (fun directoryVersion -> directoryVersion.DirectoryVersionId = updatedRootId)

            appliedRoot.Sha256Hash
            |> should equal savedDirectoryVersion.Sha256Hash

            appliedRoot.Blake3Hash
            |> should equal savedDirectoryVersion.Blake3Hash
        | Error error -> Assert.Fail($"Expected auto-save success, got: {error.Error}")

    /// Verifies that local changes do not apply local status when save reference creation fails.
    [<Test>]
    let ``local changes do not apply local status when save reference creation fails`` () =
        let updatedRootId = Guid.NewGuid()
        let updatedRootSha = Sha256Hash "updated-root-sha"
        /// Tracks applied Status changes so this scenario can assert the resulting side effect explicitly.
        let mutable appliedStatus = false

        let differences =
            List<FileSystemDifference>(
                [|
                    FileSystemDifference.Create DifferenceType.Add FileSystemEntryType.Directory (RelativePath "new-folder")
                |]
            )

        let updatedStatus = graceStatus updatedRootId updatedRootSha
        let updatedRoot = rootDirectoryVersion updatedRootId updatedRootSha

        let operations =
            { defaultOperations (branch true ReferenceDto.Default) with
                ScanForDifferences = fun _ -> Task.FromResult(differences)
                BuildUpdatedGraceStatus = fun _ _ -> Task.FromResult(updatedStatus, List<LocalDirectoryVersion>([| updatedRoot |]))
                ApplyGraceStatusIncremental =
                    fun _ _ _ ->
                        appliedStatus <- true
                        Task.FromResult(())
                CreateSaveReference = fun _ _ -> Task.FromResult(Error(GraceError.Create "save reference failed" correlationId))
            }

        let result =
            (resolveCliCurrentStateTargetReference operations None branchAnnotateImplicitSaveMessage correlationId)
                .Result

        match result with
        | Ok captured -> Assert.Fail($"Expected save-reference failure, got: {captured}")
        | Error error ->
            error.Error
            |> should contain "save reference failed"

            appliedStatus |> should equal false

    /// Verifies that read only current state scan detects changed blake3 when sha 256 still matches.
    [<Test>]
    let ``read-only current-state scan detects changed BLAKE3 when SHA-256 still matches`` () =
        let root = Path.Combine(Path.GetTempPath(), $"grace-current-state-scan-{Guid.NewGuid():N}")
        let graceDirectory = Path.Combine(root, Constants.GraceConfigDirectory)
        let filePath = Path.Combine(root, "same-sha-different-blake3.txt")
        let relativePath = RelativePath "same-sha-different-blake3.txt"

        try
            Directory.CreateDirectory(root) |> ignore

            Directory.CreateDirectory(graceDirectory)
            |> ignore

            let payload = Encoding.UTF8.GetBytes("same sha, different blake3")
            File.WriteAllBytes(filePath, payload)

            let fileInfo = FileInfo(filePath)

            let priorFile =
                LocalFileVersion.CreateWithHashes
                    relativePath
                    (sha256Hash payload)
                    (Blake3Hash "stale-or-wrong-blake3")
                    false
                    fileInfo.Length
                    (Grace.Shared.Utilities.getCurrentInstant ())
                    true
                    (fileInfo.LastWriteTimeUtc.AddSeconds(-10.0))

            let previousRoot = rootDirectoryVersion rootDirectoryId rootSha

            previousRoot.Files <- List<LocalFileVersion>([| priorFile |])
            previousRoot.Size <- priorFile.Size

            let index = GraceIndex()

            index.TryAdd(rootDirectoryId, previousRoot)
            |> ignore

            let previousStatus =
                { GraceStatus.Default with
                    Index = index
                    RootDirectoryId = rootDirectoryId
                    RootDirectorySha256Hash = rootSha
                    RootDirectoryBlake3Hash = rootBlake3
                }

            let scanInput =
                {
                    RootDirectory = root
                    GraceDirectory = graceDirectory
                    GraceStatusFile = Path.Combine(graceDirectory, Constants.GraceLocalStateDbFileName)
                    DirectoryIgnoreEntries = Array.empty
                    FileIgnoreEntries = Array.empty
                }

            match (scanWorkingTreeForDifferencesReadOnly scanInput previousStatus)
                .Result
                with
            | Error error -> Assert.Fail($"Expected scan success, got: {error}")
            | Ok differences ->
                differences.Count |> should equal 1

                differences[0].DifferenceType
                |> should equal DifferenceType.Change

                differences[0].RelativePath
                |> should equal relativePath
        finally
            if Directory.Exists(root) then Directory.Delete(root, true)

    /// Verifies that update working directory replaces same sha file when blake3 changes.
    [<Test>]
    let ``updateWorkingDirectory replaces same-sha file when blake3 changes`` () =
        withTempRepo (fun root configuration ->
            let relativePath = RelativePath "collision.txt"
            let sharedSha256Hash = Sha256Hash "same-sha256"
            let previousBlake3Hash = Blake3Hash "previous-blake3"
            let updatedBlake3Hash = Blake3Hash "updated-blake3"
            let previousBytes = Encoding.UTF8.GetBytes("old-bytes")
            let updatedBytes = Encoding.UTF8.GetBytes("new-bytes")
            let workingFilePath = Path.Combine(root, string relativePath)

            File.WriteAllBytes(workingFilePath, previousBytes)

            let previousFile =
                LocalFileVersion.CreateWithHashes
                    relativePath
                    sharedSha256Hash
                    previousBlake3Hash
                    false
                    (int64 previousBytes.Length)
                    (Grace.Shared.Utilities.getCurrentInstant ())
                    true
                    DateTime.UtcNow

            let previousRoot =
                LocalDirectoryVersion.CreateWithHashes
                    rootDirectoryId
                    configuration.OwnerId
                    configuration.OrganizationId
                    configuration.RepositoryId
                    Constants.RootDirectoryPath
                    rootSha
                    (Blake3Hash "previous-root-blake3")
                    (List<DirectoryVersionId>())
                    (List<LocalFileVersion>([| previousFile |]))
                    (int64 previousBytes.Length)
                    DateTime.UtcNow

            let previousIndex = GraceIndex()

            previousIndex.TryAdd(rootDirectoryId, previousRoot)
            |> ignore

            let previousStatus = { GraceStatus.Default with Index = previousIndex; RootDirectoryId = rootDirectoryId; RootDirectorySha256Hash = rootSha }

            let updatedFile = FileVersion.CreateWithHashes relativePath sharedSha256Hash updatedBlake3Hash String.Empty false (int64 updatedBytes.Length)

            let updatedRoot =
                DirectoryVersion.CreateWithHashes
                    (Guid.NewGuid())
                    configuration.OwnerId
                    configuration.OrganizationId
                    configuration.RepositoryId
                    Constants.RootDirectoryPath
                    (Sha256Hash "updated-root-sha")
                    (Blake3Hash "updated-root-blake3")
                    (List<DirectoryVersionId>())
                    (List<FileVersion>([| updatedFile |]))
                    (int64 updatedBytes.Length)

            let updatedDto = { DirectoryVersionDto.Default with DirectoryVersion = updatedRoot; RecursiveSize = int64 updatedBytes.Length }

            let updatedLocalFile = updatedFile.ToLocalFileVersion DateTime.UtcNow

            let previousObjectPath = previousFile.FullObjectPath
            let updatedObjectPath = updatedLocalFile.FullObjectPath

            previousObjectPath
            |> should not' (equal updatedObjectPath)

            Directory.CreateDirectory(Path.GetDirectoryName(previousObjectPath))
            |> ignore

            File.WriteAllBytes(previousObjectPath, previousBytes)

            Directory.CreateDirectory(Path.GetDirectoryName(updatedLocalFile.FullObjectPath))
            |> ignore

            File.WriteAllBytes(updatedLocalFile.FullObjectPath, updatedBytes)

            let updatedStatus = graceStatus updatedRoot.DirectoryVersionId updatedRoot.Sha256Hash

            updateWorkingDirectory previousStatus updatedStatus [| updatedDto |] correlationId
            |> fun task -> task.GetAwaiter().GetResult()

            File.ReadAllBytes(workingFilePath)
            |> should equal updatedBytes)

    /// Verifies that read only current state scan treats missing stored blake3 as unknown when sha 256 still matches.
    [<Test>]
    let ``read-only current-state scan treats missing stored BLAKE3 as unknown when SHA-256 still matches`` () =
        let root = Path.Combine(Path.GetTempPath(), $"grace-current-state-scan-{Guid.NewGuid():N}")
        let graceDirectory = Path.Combine(root, Constants.GraceConfigDirectory)
        let filePath = Path.Combine(root, "missing-blake3.txt")
        let relativePath = RelativePath "missing-blake3.txt"

        try
            Directory.CreateDirectory(root) |> ignore

            Directory.CreateDirectory(graceDirectory)
            |> ignore

            let payload = Encoding.UTF8.GetBytes("same sha, missing blake3")
            File.WriteAllBytes(filePath, payload)

            let fileInfo = FileInfo(filePath)

            let priorFile =
                LocalFileVersion.CreateWithHashes
                    relativePath
                    (sha256Hash payload)
                    (Blake3Hash String.Empty)
                    false
                    fileInfo.Length
                    (Grace.Shared.Utilities.getCurrentInstant ())
                    true
                    (fileInfo.LastWriteTimeUtc.AddSeconds(-10.0))

            let previousRoot = rootDirectoryVersion rootDirectoryId rootSha

            previousRoot.Files <- List<LocalFileVersion>([| priorFile |])
            previousRoot.Size <- priorFile.Size

            let index = GraceIndex()

            index.TryAdd(rootDirectoryId, previousRoot)
            |> ignore

            let previousStatus =
                { GraceStatus.Default with
                    Index = index
                    RootDirectoryId = rootDirectoryId
                    RootDirectorySha256Hash = rootSha
                    RootDirectoryBlake3Hash = rootBlake3
                }

            let scanInput =
                {
                    RootDirectory = root
                    GraceDirectory = graceDirectory
                    GraceStatusFile = Path.Combine(graceDirectory, Constants.GraceLocalStateDbFileName)
                    DirectoryIgnoreEntries = Array.empty
                    FileIgnoreEntries = Array.empty
                }

            match (scanWorkingTreeForDifferencesReadOnly scanInput previousStatus)
                .Result
                with
            | Error error -> Assert.Fail($"Expected scan success, got: {error}")
            | Ok differences -> differences.Count |> should equal 0
        finally
            if Directory.Exists(root) then Directory.Delete(root, true)

    /// Verifies that created save reference id is parsed from response properties.
    [<Test>]
    let ``created save reference id is parsed from response properties`` () =
        let createdSaveId = Guid.NewGuid()
        let properties = Dictionary<string, obj>()
        properties.Add(nameof ReferenceId, createdSaveId)
        let returnValue = GraceReturnValue.CreateWithMetadata "Branch command succeeded." correlationId properties

        match parseCreatedReferenceId correlationId returnValue with
        | Ok referenceId -> referenceId |> should equal createdSaveId
        | Error error -> Assert.Fail($"Expected ReferenceId property parse success, got: {error.Error}")

    /// Verifies that save disabled fails before uploading local changes.
    [<Test>]
    let ``save disabled fails before uploading local changes`` () =
        /// Tracks uploaded Files changes so this scenario can assert the resulting side effect explicitly.
        let mutable uploadedFiles = false
        /// Tracks created Save changes so this scenario can assert the resulting side effect explicitly.
        let mutable createdSave = false

        let differences =
            List<FileSystemDifference>(
                [|
                    FileSystemDifference.Create DifferenceType.Add FileSystemEntryType.Directory (RelativePath "new-folder")
                |]
            )

        let operations =
            { defaultOperations (branch false ReferenceDto.Default) with
                ScanForDifferences = fun _ -> Task.FromResult(differences)
                UploadFileVersions =
                    fun _ ->
                        uploadedFiles <- true
                        Task.FromResult(Ok Array.empty<FileVersion>)
                CreateSaveReference =
                    fun _ _ ->
                        createdSave <- true
                        Task.FromResult(Ok(Guid.NewGuid()))
            }

        let result =
            (resolveCliCurrentStateTargetReference operations None branchAnnotateImplicitSaveMessage correlationId)
                .Result

        match result with
        | Ok captured -> Assert.Fail($"Expected Save-disabled failure, got: {captured}")
        | Error error ->
            error.Error
            |> should contain "Save is disabled on this branch"

            uploadedFiles |> should equal false
            createdSave |> should equal false

    /// Verifies that helper does not write diagnostics to stdout.
    [<Test>]
    let ``helper does not write diagnostics to stdout`` () =
        let explicitReferenceId = Guid.NewGuid()
        use writer = new StringWriter()
        let originalOut = Console.Out

        try
            Console.SetOut(writer)

            let result =
                (resolveCliCurrentStateTargetReference
                    (defaultOperations (branch true ReferenceDto.Default))
                    (Some explicitReferenceId)
                    branchAnnotateImplicitSaveMessage
                    correlationId)
                    .Result

            match result with
            | Ok _ -> writer.ToString() |> should equal String.Empty
            | Error error -> Assert.Fail($"Expected explicit reference success, got: {error.Error}")
        finally
            Console.SetOut(originalOut)
