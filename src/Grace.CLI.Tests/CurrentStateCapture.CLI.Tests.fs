namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.Services
open Grace.Shared
open Grace.Shared.Constants
open Grace.Types.Branch
open Grace.Types.Common
open Grace.Types.Reference
open NUnit.Framework
open System
open System.Collections.Generic
open System.IO
open System.Threading.Tasks

[<NonParallelizable>]
module CurrentStateCaptureCliTests =
    let private correlationId = "current-state-capture-tests"
    let private currentBranchId = Guid.NewGuid()
    let private parentBranchId = Guid.NewGuid()
    let private rootDirectoryId = Guid.NewGuid()
    let private rootSha = Sha256Hash "current-root-sha"
    let private savedReferenceId = Guid.NewGuid()

    let private rootDirectoryVersion directoryVersionId sha256Hash =
        LocalDirectoryVersion.Create
            directoryVersionId
            OwnerId.Empty
            OrganizationId.Empty
            RepositoryId.Empty
            Constants.RootDirectoryPath
            sha256Hash
            (List<DirectoryVersionId>())
            (List<LocalFileVersion>())
            0L
            DateTime.UtcNow

    let private graceStatus directoryVersionId sha256Hash =
        let root = rootDirectoryVersion directoryVersionId sha256Hash
        let index = GraceIndex()
        index.TryAdd(directoryVersionId, root) |> ignore

        { GraceStatus.Default with Index = index; RootDirectoryId = directoryVersionId; RootDirectorySha256Hash = sha256Hash }

    let private referenceDto referenceId directoryVersionId sha256Hash =
        { ReferenceDto.Default with
            ReferenceId = referenceId
            BranchId = currentBranchId
            ReferenceType = ReferenceType.Save
            DirectoryId = directoryVersionId
            Sha256Hash = sha256Hash
        }

    let private branch saveEnabled latestReference =
        { BranchDto.Default with BranchId = currentBranchId; SaveEnabled = saveEnabled; LatestReference = latestReference; LatestSave = latestReference }

    let private defaultOperations branchDto =
        {
            GetBranch = fun () -> Task.FromResult(Ok(GraceReturnValue.Create branchDto correlationId))
            GetGraceWatchStatus = fun () -> Task.FromResult(None)
            ReadGraceStatus = fun () -> Task.FromResult(graceStatus rootDirectoryId rootSha)
            ScanForDifferences = fun _ -> Task.FromResult(List<FileSystemDifference>())
            CopyUpdatedFilesToObjectCache = fun _ -> Task.FromResult(Seq.empty<LocalFileVersion>)
            BuildUpdatedGraceStatus = fun status _ -> Task.FromResult(status, List<LocalDirectoryVersion>())
            UploadFileVersions = fun _ -> Task.FromResult(Ok())
            UploadDirectoryVersions = fun _ -> Task.FromResult(Ok())
            ApplyGraceStatusIncremental = fun _ _ _ -> Task.FromResult(())
            CreateSaveReference = fun _ _ -> Task.FromResult(Ok(Guid.NewGuid()))
        }

    [<Test>]
    let ``explicit reference bypasses local state capture`` () =
        let explicitReferenceId = Guid.NewGuid()
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

    [<Test>]
    let ``GraceWatch state uses matching existing branch reference`` () =
        let latest = referenceDto savedReferenceId rootDirectoryId rootSha

        let watchStatus: GraceWatchStatus =
            {
                UpdatedAt = Grace.Shared.Utilities.getCurrentInstant ()
                IsStartupClaim = false
                RootDirectoryId = rootDirectoryId
                RootDirectorySha256Hash = rootSha
                LastFileUploadInstant = NodaTime.Instant.MinValue
                LastDirectoryVersionInstant = NodaTime.Instant.MinValue
                DirectoryIds = HashSet<DirectoryVersionId>([| rootDirectoryId |])
            }

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

            captured.Source |> should equal GraceWatch
            readStatusCalled |> should equal false
        | Error error -> Assert.Fail($"Expected GraceWatch success, got: {error.Error}")

    [<Test>]
    let ``unchanged child branch does not use parent BasedOn reference`` () =
        let parentReferenceId = Guid.NewGuid()
        let createdSaveId = Guid.NewGuid()
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

    [<Test>]
    let ``local changes auto-create save with annotate message`` () =
        let updatedRootId = Guid.NewGuid()
        let updatedRootSha = Sha256Hash "updated-root-sha"
        let createdSaveId = Guid.NewGuid()
        let mutable uploadedDirectories = false
        let mutable appliedStatus = false
        let mutable saveMessage = String.Empty

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
                    fun _ message ->
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

            captured.Source |> should equal CreatedSave

            captured.CreatedSaveMessage
            |> should equal (Some branchAnnotateImplicitSaveMessage)

            saveMessage
            |> should equal branchAnnotateImplicitSaveMessage

            uploadedDirectories |> should equal true
            appliedStatus |> should equal true
        | Error error -> Assert.Fail($"Expected auto-save success, got: {error.Error}")

    [<Test>]
    let ``local changes upload changed file versions already present in object cache`` () =
        let updatedRootId = Guid.NewGuid()
        let updatedRootSha = Sha256Hash "updated-root-sha"
        let createdSaveId = Guid.NewGuid()
        let changedPath = RelativePath "src/file.txt"

        let changedFile =
            LocalFileVersion.Create changedPath (Sha256Hash "changed-file-sha") false 12L (Grace.Shared.Utilities.getCurrentInstant ()) true DateTime.UtcNow

        let mutable uploadedFiles = List<LocalFileVersion>()

        let differences =
            List<FileSystemDifference>(
                [|
                    FileSystemDifference.Create DifferenceType.Change FileSystemEntryType.File changedPath
                |]
            )

        let updatedStatus = graceStatus updatedRootId updatedRootSha

        let updatedRoot =
            LocalDirectoryVersion.Create
                updatedRootId
                OwnerId.Empty
                OrganizationId.Empty
                RepositoryId.Empty
                Constants.RootDirectoryPath
                updatedRootSha
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

            uploadedFiles.Count |> should equal 1

            uploadedFiles[0].RelativePath
            |> should equal changedPath

            uploadedFiles[0].Sha256Hash
            |> should equal changedFile.Sha256Hash
        | Error error -> Assert.Fail($"Expected auto-save success, got: {error.Error}")

    [<Test>]
    let ``created save reference id is parsed from response properties`` () =
        let createdSaveId = Guid.NewGuid()
        let properties = Dictionary<string, obj>()
        properties.Add(nameof ReferenceId, createdSaveId)
        let returnValue = GraceReturnValue.CreateWithMetadata "Branch command succeeded." correlationId properties

        match parseCreatedReferenceId correlationId returnValue with
        | Ok referenceId -> referenceId |> should equal createdSaveId
        | Error error -> Assert.Fail($"Expected ReferenceId property parse success, got: {error.Error}")

    [<Test>]
    let ``save disabled fails before uploading local changes`` () =
        let mutable uploadedFiles = false
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
                        Task.FromResult(Ok())
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
