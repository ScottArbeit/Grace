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
    type CorrelationId = string
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

    /// Indicates what database is being used for Actor state storage.
    [<KnownType("GetKnownTypes")>]
    type ActorStateStorageProvider =
        | AzureCosmosDb
        | MongoDB
        | Unknown   // Not calling it None because that really belongs to Option.
        // etc.
        static member GetKnownTypes() = GetKnownTypes<ActorStateStorageProvider>()

    [<KnownType("GetKnownTypes")>]
    type OwnerType =
        | Public
        | Private
        override this.ToString() = Utilities.getDiscriminatedUnionFullName this
        static member GetKnownTypes() = GetKnownTypes<OwnerType>()

    [<KnownType("GetKnownTypes")>]
    type OrganizationType =
        | Public
        | Private
        override this.ToString() = Utilities.getDiscriminatedUnionFullName this
        static member GetKnownTypes() = GetKnownTypes<OrganizationType>()

    [<KnownType("GetKnownTypes")>]
    type SearchVisibility =
        | Visible
        | NotVisible
        override this.ToString() = Utilities.getDiscriminatedUnionFullName this
        static member GetKnownTypes() = GetKnownTypes<SearchVisibility>()
    
    [<KnownType("GetKnownTypes")>]
    type ReferenceType =
        | Promotion
        | Commit
        | Checkpoint
        | Save
        | Tag
        override this.ToString() = Utilities.getDiscriminatedUnionFullName this
        static member FromString s = Utilities.discriminatedUnionFromString<ReferenceType> s
        static member GetKnownTypes() = GetKnownTypes<ReferenceType>()

    // Records

    /// EventMetadata is included in the recording of every event that occurs in Grace.
    type EventMetadata =
        {
            Timestamp: Instant
            CorrelationId: string
            Principal: string
            Properties: Dictionary<string, string>
        }
        override this.ToString() = serialize this
        static member New correlationId principal = {
            Timestamp = getCurrentInstant(); 
            CorrelationId = correlationId; 
            Principal = principal; 
            Properties = Dictionary<string, string>()
        }

    /// A FileVersion represents a version of a file in a repository with unique contents, and therefore with a unique SHA-256 hash. It is immutable.
    ///
    /// It is the server-side representation of the LocalFileVersion type, used for the local object cache.
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
        member this.GetObjectFileName = getObjectFileName this.RelativePath this.Sha256Hash
        /// Gets the relative directory path of the file. Example: "/dir/subdir/file.js" -> "/dir/subdir/".
        member this.RelativeDirectory = getRelativeDirectory $"{this.RelativePath}" ""

    /// A LocalFileVersion represents a version of a file in a repository with unique contents, and therefore with a unique SHA-256 hash. It is immutable.
    ///
    /// It is the local representation of the FileVersion type, used on the server.
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
        member this.GetObjectFileName = getObjectFileName this.RelativePath this.Sha256Hash

        /// Gets the relative directory path of the file. Example: "/dir/subdir/file.js" -> "/dir/subdir/".
        member this.RelativeDirectory = getRelativeDirectory $"{this.RelativePath}" ""

    /// A DirectoryVersion represents a version of a directory in a repository with unique contents, and therefore with a unique SHA-256 hash.
    ///
    /// It is the server-side representation of the LocalDirectoryVersion type, used for the local object cache.
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
        
    /// A LocalDirectoryVersion represents a version of a directory in a repository with unique contents, and therefore with a unique SHA-256 hash.
    ///
    /// It is the local representation of the DirectoryVersion type, used on the server.
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

        /// Specifies whether a specific entry in a directory is a DirectoryVersion or a FileVersion.
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
        
        /// Specifies whether a specific entry in a local directory is a LocalDirectoryVersion or a LocalFileVersion.
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

    /// Specifies whether a repository is public or private.
    [<KnownType("GetKnownTypes")>]
    type RepositoryVisibility =
        | Private
        | Public
        static member GetKnownTypes() = GetKnownTypes<RepositoryVisibility>()

    /// Specifies the current operational status of a repository.
    [<KnownType("GetKnownTypes")>]
    type RepositoryStatus =
        | Active
        | Suspended
        | Closed
        | Deleted
        static member GetKnownTypes() = GetKnownTypes<RepositoryStatus>()

    /// Defines the type of the list of validations used in server endpoints.
    //type Validations<'T, 'U> = 'T -> Result<bool, 'U> list

    /// Defines the specific permissions that can be granted to a user or group on a directory.
    [<KnownType("GetKnownTypes")>]
    type DirectoryPermission =
        | FullControl
        | Modify
        | ListContents
        | Read
        | NoAccess
        | NotSet
        static member GetKnownTypes() = GetKnownTypes<DirectoryPermission>()

    /// Defines the permissions granted to a group defined by a claim.
    /// 
    /// This is combined with a RelativePath to define a PathPermission.
    [<KnownType("GetKnownTypes")>]
    type ClaimPermission =
        {
            Claim: string
            DirectoryPermission: DirectoryPermission
        }

    /// Defines a set of claims and their permissions that should be applied to a relative path in the repository.
    ///
    /// NOTE: This type is being used only at the directory level for now, but I intend to implement it at the file level as well.
    [<KnownType("GetKnownTypes")>]
    type PathPermission =
        {
            Path: RelativePath
            Permissions: List<ClaimPermission>
        }

    /// Cleans up extra backslashes (escape characters) and converts \r\n to Environment.NewLine.
    let cleanJson (s: string) =
        s.Replace("\\\\\\\\", @"\").Replace("\\\\", @"\").Replace(@"\r\n", Environment.NewLine)

    /// The primary type used in Grace to represent successful results.
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
        override this.ToString() = serialize this

    /// The primary type used in Grace to represent error results.
    type GraceError =
        {
            Error: string
            EventTime: Instant
            CorrelationId: string
            Properties: Dictionary<String, String>
        }
        static member Default = {Error = "Empty error message"; EventTime = getCurrentInstant(); CorrelationId = String.Empty; Properties = new Dictionary<String, String>()}
        static member Create (error: string) (correlationId: string) =
            {Error = error; EventTime = getCurrentInstant(); CorrelationId = correlationId; Properties = new Dictionary<String, String>()}
        static member CreateWithMetadata (error: string) (correlationId: string) (properties: Dictionary<String, String>) =
                   {Error = error; EventTime = getCurrentInstant(); CorrelationId = correlationId; Properties = properties}
        member this.enhance(key, value) =
            this.Properties.Add(key, value)
            this
        override this.ToString() = serialize this

    /// The primary type used to represent Grace operations results.
    type GraceResult<'T> = Result<GraceReturnValue<'T>, GraceError>

    /// Specifies whether a file system entry is a directory or a file.
    [<KnownType("GetKnownTypes")>]
    type FileSystemEntryType =
        | Directory
        | File
        static member GetKnownTypes() = GetKnownTypes<FileSystemEntryType>()
        override this.ToString() = Utilities.getDiscriminatedUnionFullName this

    /// Specifies whether a change detected in a diff is an add, change, or delete.
    [<KnownType("GetKnownTypes")>]
    type DifferenceType =
        | Add 
        | Change 
        | Delete 
        static member GetKnownTypes() = GetKnownTypes<DifferenceType>()
        override this.ToString() = Utilities.getDiscriminatedUnionFullName this

    /// A file system difference is a change detected (at a file level) in a diff. It specifies the type of change (add, change, or delete), the type of file system entry (directory or file), and the relative path of the entry.
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

    /// GraceIndex is Grace's representation of the contents of the local object cache. It is an index from the DirectoryId of a LocalDirectoryVersion to the LocalDirectoryVersion itself.
    type GraceIndex = ConcurrentDictionary<Guid, LocalDirectoryVersion>

    //type ObjectCache = ConcurrentDictionary<Guid, LocalDirectoryVersion>

    /// ObjectIndex is an index from the SHA-256 hash of a LocalDirectoryVersion to the LocalDirectoryVersion itself.
    type ObjectIndex = ConcurrentDictionary<Sha256Hash, LocalDirectoryVersion>
    
    /// GraceStatus is a snapshot of the status that `grace watch` holds about the repository and branch while running.
    ///
    /// It is serialized and written by `grace watch` in the inter-process cache file that Grace CLI uses to know that `grace watch` is running.
    /// It gets deserialized by Grace CLI, and is used to speed up the CLI by holding pre-computed results when running certain commands.
    ///
    /// If the interprocess cache file is missing or corrupt, Grace CLI assumes that `grace watch` is not running, and runs all commands from scratch.
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

    /// GraceObjectCache is a snapshot of the contents of the local object cache.
    type GraceObjectCache =
        {
            Index: GraceIndex
        }
        static member Default =
            {
                Index = GraceIndex()
            }

    /// Holds the results of a diff between two versions of a file.
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
    type PromotionType =
        | SingleStep
        | Complex
        static member GetKnownTypes() = GetKnownTypes<PromotionType>()
        override this.ToString() = Utilities.getDiscriminatedUnionFullName this

    /// Holds the entity Id's involved in an API call. It's populated in ValidateIds.Middleware.fs.
    type GraceIds =
        {
            OwnerId: string
            OrganizationId: string
            RepositoryId: string
            BranchId: string
            HasOwner: bool
            HasOrganization: bool
            HasRepository: bool
            HasBranch: bool
        }

        static member Default = 
            {OwnerId = String.Empty; OrganizationId = String.Empty; RepositoryId = String.Empty; BranchId = String.Empty; 
                HasOwner = false; HasOrganization = false; HasRepository = false; HasBranch = false}
        override this.ToString() = serialize this
