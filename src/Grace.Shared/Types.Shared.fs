namespace Grace.Shared

open DiffPlex.DiffBuilder.Model
open Grace.Shared.Utilities
open NodaTime
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.IO
open System.Linq
open System.Runtime.Serialization
open System.Text
open System.Text.Json
open System.IO.Enumeration

module Types =

    // Domain nouns
    type BranchId = Guid
    type BranchName = string
    type ContainerName = string
    type DirectoryId = Guid    
    type FilePath = string    
    type GraceIgnoreEntry = string
    type OrganizationId = Guid
    type OrganizationName = string
    type OwnerId = Guid
    type OwnerName = string
    type ReferenceId = Guid
    type ReferenceName = string 
    type ReferenceText = string
    type RepositoryId = Guid
    type RepositoryName = string
    type RelativePath = string        
    type Sha256Hash = string
    type StorageAccountName = string
    type StorageConnectionString = string
    type StorageContainerName = string
    //type StorageAccountInfo = StorageAccountName * ObjectStorageProvider
    type UriWithSharedAccessSignature = string
    type UserId = string
    
    [<KnownType("GetKnownTypes")>]
    type LineEndings =
        | CrLf
        | Cr
        | PlatformDependent
        static member GetKnownTypes() = GetKnownTypes<LineEndings>()

    [<KnownType("GetKnownTypes")>]
    type ObjectStorageProvider =
        | AWSS3
        | AzureBlobStorage
        | GoogleCloudStorage
        | Unknown   // Not calling it None because that really belongs to Option.
        static member GetKnownTypes() = GetKnownTypes<ObjectStorageProvider>()

    [<KnownType("GetKnownTypes")>]
    type ActorStateStorageProvider =
        | AzureCosmosDb
        | Cassandra
        | Unknown   // Not calling it None because that really belongs to Option.
        // etc.
        static member GetKnownTypes() = GetKnownTypes<ActorStateStorageProvider>()

    [<KnownType("GetKnownTypes")>]
    type OwnerType =
        | Public
        | Private
        override this.ToString() = Utilities.discriminatedUnionFullNameToString this
        static member GetKnownTypes() = GetKnownTypes<OwnerType>()

    [<KnownType("GetKnownTypes")>]
    type OrganizationType =
        | Public
        | Private
        override this.ToString() = Utilities.discriminatedUnionFullNameToString this
        static member GetKnownTypes() = GetKnownTypes<OrganizationType>()

    [<KnownType("GetKnownTypes")>]
    type SearchVisibility =
        | Visible
        | NotVisible
        override this.ToString() = Utilities.discriminatedUnionFullNameToString this
        static member GetKnownTypes() = GetKnownTypes<SearchVisibility>()
    
    [<KnownType("GetKnownTypes")>]
    type ReferenceType =
        | Merge
        | Commit
        | Checkpoint
        | Save
        | Tag
        override this.ToString() = Utilities.discriminatedUnionFullNameToString this
        static member FromString s = Utilities.discriminatedUnionFromString<ReferenceType> s
        static member GetKnownTypes() = GetKnownTypes<ReferenceType>()

    // Records
    type EventMetadata =
        {
            Timestamp: Instant
            CorrelationId: string
            Principal: string
            Properties: Dictionary<string, string>
        }
        override this.ToString() = JsonSerializer.Serialize(this, Constants.JsonSerializerOptions)
        static member New correlationId principal = {
            Timestamp = getCurrentInstant(); 
            CorrelationId = correlationId; 
            Principal = principal; 
            Properties = Dictionary<string, string>()
        }

    [<CLIMutable>]
    type FileVersion = 
        {
            Class: string
            RepositoryId: RepositoryId
            RelativePath: RelativePath
            Sha256Hash: Sha256Hash
            IsBinary: bool
            Size: uint64
            CreatedAt: Instant
            BlobUri: string
        }
        static member Create (repositoryId: RepositoryId) (relativePath: RelativePath) (sha256Hash: Sha256Hash) (blobUri: string) (isBinary: bool) (size: uint64) =
            { Class = "FileVersion"; RepositoryId = repositoryId; RelativePath = RelativePath (normalizeFilePath $"{relativePath}"); 
                Sha256Hash = sha256Hash; BlobUri = blobUri; IsBinary = isBinary; Size = size; CreatedAt = getCurrentInstant() }

        /// Converts a FileVersion to a LocalFileVersion.
        member this.ToLocalFileVersion lastWriteTimeUtc = LocalFileVersion.Create this.RepositoryId this.RelativePath this.Sha256Hash this.IsBinary this.Size this.CreatedAt true lastWriteTimeUtc

        /// Get the object directory file name, which includes the SHA256 Hash value. Example: hello.js -> hello_04bef0a4b298de9c02930234.js
        member this.GetObjectFileName =
            // FileInfo.Extension includes the '.'.
            let originalFile = FileInfo(this.RelativePath)
            if not <| String.IsNullOrEmpty(originalFile.Extension) then
                $"{originalFile.Name.Replace(originalFile.Extension, String.Empty)}_{this.Sha256Hash}{originalFile.Extension}"
            else
                $"{originalFile.Name}_{this.Sha256Hash}"

        /// Gets the relative directory path of the file. Example: "/dir/subdir/file.js" -> "/dir/subdir/".
        member this.RelativeDirectory = getRelativeDirectory $"{this.RelativePath}" ""
    
    and [<KnownType("GetKnownTypes")>]
        [<CLIMutable>]
        LocalFileVersion = 
        {
            Class: string
            RepositoryId: RepositoryId
            RelativePath: RelativePath
            Sha256Hash: Sha256Hash
            IsBinary: bool
            Size: uint64
            CreatedAt: Instant
            UploadedToObjectStorage: bool
            LastWriteTimeUtc: DateTime
        }
        static member GetKnownTypes() = GetKnownTypes<LocalFileVersion>()
        static member Create (repositoryId: RepositoryId) (relativePath: RelativePath) (sha256Hash: Sha256Hash) (isBinary: bool)
            (size: uint64) (createdAt: Instant) (uploadedToObjectStorage: bool) (lastWriteTimeUtc: DateTime) =
            {   
                Class = "LocalFileVersion"
                RepositoryId = repositoryId
                RelativePath = RelativePath (normalizeFilePath $"{relativePath}")
                Sha256Hash = sha256Hash
                IsBinary = isBinary
                Size = size
                CreatedAt = createdAt
                UploadedToObjectStorage = uploadedToObjectStorage
                LastWriteTimeUtc = lastWriteTimeUtc
            }

        /// Converts a LocalFileVersion to a FileVersion. NOTE: at this point, we don't know the BlobUri.
        member this.ToFileVersion = FileVersion.Create this.RepositoryId this.RelativePath this.Sha256Hash String.Empty this.IsBinary this.Size
    
        /// Get the object directory file name, which includes the SHA256 Hash value. Example: hello.js -> hello_04bef0a4b298de9c02930234.js
        member this.GetObjectFileName =
            // FileInfo.Extension includes the '.'.
            let originalFile = FileInfo($"{this.RelativePath}")
            if not <| String.IsNullOrEmpty(originalFile.Extension) then
                $"{originalFile.Name.Replace(originalFile.Extension, String.Empty)}_{this.Sha256Hash}{originalFile.Extension}"
            else
                $"{originalFile.Name}_{this.Sha256Hash}"

        /// Gets the relative directory path of the file. Example: "/dir/subdir/file.js" -> "/dir/subdir/".
        member this.RelativeDirectory = getRelativeDirectory $"{this.RelativePath}" ""

    [<KnownType("GetKnownTypes")>]
    type DirectoryVersion = 
        {
            Class: string
            DirectoryId: DirectoryId
            RepositoryId: RepositoryId
            RelativePath: RelativePath
            Sha256Hash: Sha256Hash
            Directories: List<DirectoryId>
            Files: List<FileVersion>
            Size: uint64
            RecursiveSize: uint64
            CreatedAt: Instant
        }
        static member GetKnownTypes() = GetKnownTypes<DirectoryVersion>()
        static member Default = 
            {Class = "DirectoryVersion"; DirectoryId = Guid.Empty; RepositoryId = Guid.Empty; 
                RelativePath = RelativePath String.Empty; Sha256Hash = Sha256Hash String.Empty; 
                Directories = List<DirectoryId>(); Files = List<FileVersion>(); Size = Constants.InitialDirectorySize; 
                RecursiveSize = Constants.InitialDirectorySize; CreatedAt = Instant.MinValue}

        static member Create (directoryId: DirectoryId) (repositoryId: RepositoryId) (relativePath: RelativePath) 
            (sha256Hash: Sha256Hash) (directories: List<DirectoryId>) (files: List<FileVersion>) (size: uint64) =
            {Class = "DirectoryVersion"; DirectoryId = directoryId; RepositoryId = repositoryId; 
                RelativePath = relativePath; Sha256Hash = sha256Hash; 
                Directories = directories; Files = files; Size = size; 
                RecursiveSize = Constants.InitialDirectorySize; CreatedAt = getCurrentInstant()}

        member this.ToLocalDirectoryVersion lastWriteTimeUtc =
            LocalDirectoryVersion.Create this.DirectoryId this.RepositoryId this.RelativePath this.Sha256Hash this.Directories
                (this.Files.Select(fun f -> f.ToLocalFileVersion lastWriteTimeUtc).ToList()) this.Size lastWriteTimeUtc        
        
    and [<KnownType("GetKnownTypes")>]
        [<CLIMutable>]
        LocalDirectoryVersion = 
        {
            Class: string
            DirectoryId: DirectoryId
            RepositoryId: RepositoryId
            RelativePath: RelativePath
            Sha256Hash: Sha256Hash
            Directories: List<DirectoryId>
            Files: List<LocalFileVersion>
            Size: uint64
            RecursiveSize: uint64
            CreatedAt: Instant
            LastWriteTimeUtc: DateTime
        }
        static member GetKnownTypes() = GetKnownTypes<LocalDirectoryVersion>()
        static member Default = 
            {Class = "LocalDirectoryVersion"; RepositoryId = Guid.Empty; DirectoryId = Guid.Empty; RelativePath = RelativePath String.Empty; 
                Sha256Hash = Sha256Hash String.Empty; Directories = List<DirectoryId>(); Files = List<LocalFileVersion>(); Size = Constants.InitialDirectorySize; 
                RecursiveSize = Constants.InitialDirectorySize; CreatedAt = Instant.MinValue; LastWriteTimeUtc = DateTime.UtcNow}
        
        static member Create (directoryId: DirectoryId) (repositoryId: RepositoryId) (relativePath: RelativePath) (sha256Hash: Sha256Hash) (directories: List<DirectoryId>) (files: List<LocalFileVersion>) (size: uint64) (lastWriteTimeUtc: DateTime) =
            {Class = "LocalDirectoryVersion"; DirectoryId = directoryId; RepositoryId = repositoryId; RelativePath = relativePath; 
                Sha256Hash = sha256Hash; Directories = directories; Files = files; Size = size; 
                RecursiveSize = Constants.InitialDirectorySize; CreatedAt = getCurrentInstant(); LastWriteTimeUtc = lastWriteTimeUtc}

        /// Converts a LocalDirectoryVersion to a DirectoryVersion.
        member this.ToDirectoryVersion =
            DirectoryVersion.Create this.DirectoryId this.RepositoryId this.RelativePath this.Sha256Hash this.Directories
                (this.Files.Select(fun f -> f.ToFileVersion).ToList()) this.Size

    and [<KnownType("GetKnownTypes")>]
        DirectoryEntry =
        | Directory of DirectoryVersion
        | File of FileVersion
        static member GetKnownTypes() = GetKnownTypes<DirectoryEntry>()

        /// Converts a DirectoryEntry to a LocalDirectoryEntry.
        member this.ToLocalDirectoryEntry lastWriteTimeUtc =
            match this with 
            | DirectoryEntry.Directory d -> LocalDirectory (d.ToLocalDirectoryVersion lastWriteTimeUtc)
            | DirectoryEntry.File f -> LocalFile (f.ToLocalFileVersion lastWriteTimeUtc)    
        
    and [<KnownType("GetKnownTypes")>]
        LocalDirectoryEntry =
        | LocalDirectory of LocalDirectoryVersion
        | LocalFile of LocalFileVersion
        static member GetKnownTypes() = GetKnownTypes<LocalDirectoryEntry>()

        /// Converts a LocalDirectoryEntry to a DirectoryEntry.
        member this.ToDirectoryEntry : DirectoryEntry =
            match this with
            | LocalDirectory d -> DirectoryEntry.Directory d.ToDirectoryVersion
            | LocalFile f -> DirectoryEntry.File f.ToFileVersion

    [<KnownType("GetKnownTypes")>]
    type RepositoryVisibility =
        | Private
        | Public
        static member GetKnownTypes() = GetKnownTypes<RepositoryVisibility>()

    [<KnownType("GetKnownTypes")>]
    type RepositoryStatus =
        | Active
        | Suspended
        | Closed
        | Deleted
        static member GetKnownTypes() = GetKnownTypes<RepositoryStatus>()

    type Validations<'T, 'U> = 'T -> Result<bool, 'U> list

    [<KnownType("GetKnownTypes")>]
    type DirectoryPermission =
        | FullControl
        | Modify
        | ListContents
        | Read
        | NoAccess
        | NotSet
        static member GetKnownTypes() = GetKnownTypes<DirectoryPermission>()

    type ClaimPermission =
        {
            Claim: string
            DirectoryPermission: DirectoryPermission
        }

    type PathPermission =
        {
            Path: RelativePath
            Permissions: List<ClaimPermission>
        }

    type GraceReturnValue<'T> = 
        {
            ReturnValue: 'T
            EventTime: Instant
            CorrelationId: string
            Properties: Dictionary<String, String>
        }
        static member Create<'T> (returnValue: 'T) (correlationId: string) =
            {ReturnValue = returnValue; EventTime = getCurrentInstant(); CorrelationId = correlationId; Properties = new Dictionary<String, String>()}
        member this.enhance(key, value) =
            this.Properties.Add(key, value)
            this
        override this.ToString() = JsonSerializer.Serialize(this, Constants.JsonSerializerOptions).Replace("\\\\\\\\", @"\").Replace("\\\\", @"\").Replace(@"\r\n", Environment.NewLine)

    type GraceError =
        {
            Error: string
            EventTime: Instant
            CorrelationId: string
            Properties: Dictionary<String, String>
        }
        static member Create (error: string) (correlationId: string) =
            {Error = error; EventTime = getCurrentInstant(); CorrelationId = correlationId; Properties = new Dictionary<String, String>()}
        member this.enhance(key, value) =
            this.Properties.Add(key, value)
            this
        override this.ToString() = JsonSerializer.Serialize(this, Constants.JsonSerializerOptions).Replace("\\\\\\\\", @"\").Replace("\\\\", @"\").Replace(@"\r\n", Environment.NewLine)

    type GraceResult<'T> = Result<GraceReturnValue<'T>, GraceError>

    [<KnownType("GetKnownTypes")>]
    type FileSystemEntryType =
        | Directory
        | File
        static member GetKnownTypes() = GetKnownTypes<FileSystemEntryType>()
        override this.ToString() = Utilities.discriminatedUnionFullNameToString this

    [<KnownType("GetKnownTypes")>]
    type DifferenceType =
        | Add 
        | Change 
        | Delete 
        static member GetKnownTypes() = GetKnownTypes<DifferenceType>()
        override this.ToString() = Utilities.discriminatedUnionFullNameToString this

    type FileSystemDifference =
        {
            DifferenceType: DifferenceType
            FileSystemEntryType: FileSystemEntryType
            RelativePath: RelativePath
        }
        static member Create differenceType fileSystemEntryType relativePath =
            {DifferenceType = differenceType; FileSystemEntryType = fileSystemEntryType; RelativePath = relativePath}

    type UploadMetadata =
        {
            BlobUriWithSasToken: Uri
            Sha256Hash: Sha256Hash
        }

    type GraceIndex = ConcurrentDictionary<Guid, LocalDirectoryVersion>
    type ObjectCache = ConcurrentDictionary<Guid, LocalDirectoryVersion>
    type ObjectIndex = ConcurrentDictionary<Sha256Hash, LocalDirectoryVersion>
    
    type GraceStatus = 
        {
            Index: GraceIndex
            RootDirectoryId: DirectoryId
            RootDirectorySha256Hash: Sha256Hash
            LastSuccessfulFileUpload: Instant
            LastSuccessfulDirectoryVersionUpload: Instant
        }
        static member Default =
            {
                Index = GraceIndex()
                RootDirectoryId = DirectoryId.Empty
                RootDirectorySha256Hash = Sha256Hash String.Empty
                LastSuccessfulFileUpload = getCurrentInstant()
                LastSuccessfulDirectoryVersionUpload = getCurrentInstant()
            }

    type GraceObjectCache =
        {
            Index: GraceIndex
        }
        static member Default =
            {
                Index = GraceIndex()
            }

    type FileDiff =
        {
            RelativePath: RelativePath
            FileSha1: Sha256Hash
            CreatedAt1: Instant
            FileSha2: Sha256Hash
            CreatedAt2: Instant
            IsBinary: bool
            InlineDiff: List<DiffPiece[]>
            SideBySideOld: List<DiffPiece[]>
            SideBySideNew: List<DiffPiece[]>
        }
        static member Create (relativePath: RelativePath) (fileSha1: Sha256Hash) (createdAt1: Instant) (fileSha2: Sha256Hash) (createdAt2: Instant) (isBinary: bool) (inlineDiff: List<DiffPiece[]>) (sideBySideOld: List<DiffPiece[]>) (sideBySideNew: List<DiffPiece[]>) =
            {RelativePath = relativePath; FileSha1 = fileSha1; CreatedAt1 = createdAt1; FileSha2 = fileSha2; CreatedAt2 = createdAt2; IsBinary = isBinary; InlineDiff = inlineDiff; SideBySideOld = sideBySideOld; SideBySideNew = sideBySideNew}

    [<KnownType("GetKnownTypes")>]
    type MergeType =
        | SingleStep
        | Complex
        static member GetKnownTypes() = GetKnownTypes<FileSystemEntryType>()
        override this.ToString() = Utilities.discriminatedUnionFullNameToString this
