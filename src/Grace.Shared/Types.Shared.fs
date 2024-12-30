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
open System.Threading.Tasks
open System.Text.Json
open System.IO.Enumeration
open Microsoft.Extensions.ObjectPool
open System.Text.Json.Serialization
open MessagePack

module Types =

    // Domain nouns
    type BranchId = Guid
    type BranchName = string
    type ContainerName = string
    type CorrelationId = string
    type DeleteReason = string
    type DirectoryVersionId = Guid
    type FilePath = string
    type GraceIgnoreEntry = string
    type OrganizationId = Guid
    type OrganizationName = string
    type OwnerId = Guid
    type OwnerName = string
    type ParentBranchId = BranchId
    type ReferenceId = Guid
    type ReferenceName = string
    type ReferenceText = string
    type ReminderId = Guid
    type RepositoryId = Guid
    type RepositoryName = string
    type RelativePath = string
    type Sha256Hash = string
    type StorageAccountName = string
    type StorageConnectionString = string
    type StorageContainerName = string
    type UriWithSharedAccessSignature = Uri
    type UserId = string
    type ValidationResult<'T> = ValueTask<Result<unit, 'T>>

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
        | Unknown // Not calling it None because that really belongs to Option.

        static member GetKnownTypes() = GetKnownTypes<ObjectStorageProvider>()

    /// Indicates what database is being used for Actor state storage.
    [<KnownType("GetKnownTypes")>]
    type ActorStateStorageProvider =
        | AzureCosmosDb
        | MongoDB
        | Unknown // Not calling it None because that really belongs to Option.
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
        | External
        | Rebase

        override this.ToString() = Utilities.getDiscriminatedUnionFullName this

        static member FromString s = Utilities.discriminatedUnionFromString<ReferenceType> s

        static member GetKnownTypes() = GetKnownTypes<ReferenceType>()

    // Records

    /// EventMetadata is included in the recording of every event that occurs in Grace.
    type EventMetadata =
        { Timestamp: Instant
          CorrelationId: CorrelationId
          Principal: string
          Properties: Dictionary<string, string> }

        override this.ToString() = serialize this

        static member New correlationId principal =
            { Timestamp = getCurrentInstant (); CorrelationId = correlationId; Principal = principal; Properties = Dictionary<string, string>() }

    /// A FileVersion represents a version of a file in a repository with unique contents, and therefore with a unique SHA-256 hash. It is immutable.
    ///
    /// It is the server-side representation of the LocalFileVersion type, used for the local object cache.
    [<CLIMutable>]
    type FileVersion =
        { Class: string
          //RepositoryId: RepositoryId
          RelativePath: RelativePath
          Sha256Hash: Sha256Hash
          IsBinary: bool
          Size: int64
          CreatedAt: Instant
          BlobUri: string }

        static member Create
            //(repositoryId: RepositoryId)
            (relativePath: RelativePath)
            (sha256Hash: Sha256Hash)
            (blobUri: string)
            (isBinary: bool)
            (size: int64)
            =
            { Class = "FileVersion"
              //RepositoryId = repositoryId
              RelativePath = RelativePath(normalizeFilePath $"{relativePath}")
              Sha256Hash = sha256Hash
              BlobUri = blobUri
              IsBinary = isBinary
              Size = size
              CreatedAt = getCurrentInstant () }

        static member Default = FileVersion.Create String.Empty String.Empty String.Empty false 0L

        /// Converts a FileVersion to a LocalFileVersion.
        member this.ToLocalFileVersion lastWriteTimeUtc =
            LocalFileVersion.Create this.RelativePath this.Sha256Hash this.IsBinary this.Size this.CreatedAt true lastWriteTimeUtc

        /// Get the object directory file name, which includes the SHA256 Hash value. Example: hello.js -> hello_04bef0a4b298de9c02930234.js
        member this.GetObjectFileName = getObjectFileName this.RelativePath this.Sha256Hash
        /// Gets the relative directory path of the file. Example: "/dir/subdir/file.js" -> "/dir/subdir/".
        member this.RelativeDirectory = getRelativeDirectory $"{this.RelativePath}" ""

    /// A LocalFileVersion represents a version of a file in a repository with unique contents, and therefore with a unique SHA-256 hash. It is immutable.
    ///
    /// It is the local representation of the FileVersion type, used on the server.
    and [<KnownType("GetKnownTypes"); CLIMutable; MessagePackObject>] LocalFileVersion =
        { [<Key(0)>]
          Class: string
          //[<Key(1)>]
          //RepositoryId: RepositoryId
          [<Key(2)>]
          RelativePath: RelativePath
          [<Key(3)>]
          Sha256Hash: Sha256Hash
          [<Key(4)>]
          IsBinary: bool
          [<Key(5)>]
          Size: int64
          [<Key(6)>]
          CreatedAt: Instant
          [<Key(7)>]
          UploadedToObjectStorage: bool
          [<Key(8)>]
          LastWriteTimeUtc: DateTime }

        static member GetKnownTypes() = GetKnownTypes<LocalFileVersion>()

        static member Create
            //(repositoryId: RepositoryId)
            (relativePath: RelativePath)
            (sha256Hash: Sha256Hash)
            (isBinary: bool)
            (size: int64)
            (createdAt: Instant)
            (uploadedToObjectStorage: bool)
            (lastWriteTimeUtc: DateTime)
            =
            { Class = "LocalFileVersion"
              //RepositoryId = repositoryId
              RelativePath = RelativePath(normalizeFilePath $"{relativePath}")
              Sha256Hash = sha256Hash
              IsBinary = isBinary
              Size = size
              CreatedAt = createdAt
              UploadedToObjectStorage = uploadedToObjectStorage
              LastWriteTimeUtc = lastWriteTimeUtc }

        /// Converts a LocalFileVersion to a FileVersion. NOTE: at this point, we don't know the BlobUri.
        [<IgnoreMember>]
        member this.ToFileVersion = FileVersion.Create this.RelativePath this.Sha256Hash String.Empty this.IsBinary this.Size

        /// Get the object directory file name, which includes the SHA256 Hash value. Example: hello.js -> hello_04bef0a4b298de9c02930234.js
        [<IgnoreMember>]
        member this.GetObjectFileName = getObjectFileName this.RelativePath this.Sha256Hash

        /// Gets the relative directory path of the file. Example: "/dir/subdir/file.js" -> "/dir/subdir/".
        [<IgnoreMember>]
        member this.RelativeDirectory = getRelativeDirectory $"{this.RelativePath}" ""

    /// A DirectoryVersion represents a version of a directory in a repository with unique contents, and therefore with a unique SHA-256 hash.
    ///
    /// It is the server-side representation of the LocalDirectoryVersion type. LocalDirectoryVersion is used for the local object cache.
    [<KnownType("GetKnownTypes")>]
    type DirectoryVersion =
        { Class: string
          DirectoryVersionId: DirectoryVersionId
          RepositoryId: RepositoryId
          RelativePath: RelativePath
          Sha256Hash: Sha256Hash
          Directories: List<DirectoryVersionId>
          Files: List<FileVersion>
          Size: int64
          CreatedAt: Instant }

        static member GetKnownTypes() = GetKnownTypes<DirectoryVersion>()

        static member Default =
            { Class = nameof (DirectoryVersion)
              DirectoryVersionId = Guid.Empty
              RepositoryId = Guid.Empty
              RelativePath = RelativePath String.Empty
              Sha256Hash = Sha256Hash String.Empty
              Directories = List<DirectoryVersionId>()
              Files = List<FileVersion>()
              Size = Constants.InitialDirectorySize
              CreatedAt = Constants.DefaultTimestamp }

        static member Create
            (directoryVersionId: DirectoryVersionId)
            (repositoryId: RepositoryId)
            (relativePath: RelativePath)
            (sha256Hash: Sha256Hash)
            (directories: List<DirectoryVersionId>)
            (files: List<FileVersion>)
            (size: int64)
            =
            { Class = nameof (DirectoryVersion)
              DirectoryVersionId = directoryVersionId
              RepositoryId = repositoryId
              RelativePath = relativePath
              Sha256Hash = sha256Hash
              Directories = directories
              Files = files
              Size = size
              CreatedAt = getCurrentInstant () }

        member this.ToLocalDirectoryVersion lastWriteTimeUtc =
            LocalDirectoryVersion.Create
                this.DirectoryVersionId
                this.RepositoryId
                this.RelativePath
                this.Sha256Hash
                this.Directories
                (this.Files.Select(fun f -> f.ToLocalFileVersion lastWriteTimeUtc).ToList())
                this.Size
                lastWriteTimeUtc

    /// A LocalDirectoryVersion represents a version of a directory in a repository with unique contents, and therefore with a unique SHA-256 hash.
    ///
    /// It is the local representation of the DirectoryVersion type. DirectoryVersion is used on the server.
    and [<KnownType("GetKnownTypes"); CLIMutable; MessagePackObject>] LocalDirectoryVersion =
        { [<Key(0)>]
          Class: string
          [<Key(1)>]
          DirectoryVersionId: DirectoryVersionId
          [<Key(2)>]
          RepositoryId: RepositoryId
          [<Key(3)>]
          RelativePath: RelativePath
          [<Key(4)>]
          Sha256Hash: Sha256Hash
          [<Key(5)>]
          Directories: List<DirectoryVersionId>
          [<Key(6)>]
          Files: List<LocalFileVersion>
          [<Key(7)>]
          Size: int64
          [<Key(8)>]
          CreatedAt: Instant
          [<Key(9)>]
          LastWriteTimeUtc: DateTime }

        static member GetKnownTypes() = GetKnownTypes<LocalDirectoryVersion>()

        static member Default =
            { Class = "LocalDirectoryVersion"
              RepositoryId = Guid.Empty
              DirectoryVersionId = Guid.Empty
              RelativePath = RelativePath String.Empty
              Sha256Hash = Sha256Hash String.Empty
              Directories = List<DirectoryVersionId>()
              Files = List<LocalFileVersion>()
              Size = Constants.InitialDirectorySize
              CreatedAt = Constants.DefaultTimestamp
              LastWriteTimeUtc = DateTime.UtcNow }

        static member Create
            (directoryVersionId: DirectoryVersionId)
            (repositoryId: RepositoryId)
            (relativePath: RelativePath)
            (sha256Hash: Sha256Hash)
            (directories: List<DirectoryVersionId>)
            (files: List<LocalFileVersion>)
            (size: int64)
            (lastWriteTimeUtc: DateTime)
            =
            { Class = "LocalDirectoryVersion"
              DirectoryVersionId = directoryVersionId
              RepositoryId = repositoryId
              RelativePath = relativePath
              Sha256Hash = sha256Hash
              Directories = directories
              Files = files
              Size = size
              CreatedAt = getCurrentInstant ()
              LastWriteTimeUtc = lastWriteTimeUtc }

        /// Converts a LocalDirectoryVersion to a DirectoryVersion.
        [<IgnoreMember>]
        member this.ToDirectoryVersion =
            DirectoryVersion.Create
                this.DirectoryVersionId
                this.RepositoryId
                this.RelativePath
                this.Sha256Hash
                this.Directories
                (this.Files.Select(fun f -> f.ToFileVersion).ToList())
                this.Size

    /// Specifies whether a specific entry in a directory is a DirectoryVersion or a FileVersion.
    and [<KnownType("GetKnownTypes")>] DirectoryEntry =
        | Directory of DirectoryVersion
        | File of FileVersion

        static member GetKnownTypes() = GetKnownTypes<DirectoryEntry>()

        /// Converts a DirectoryEntry to a LocalDirectoryEntry.
        member this.ToLocalDirectoryEntry lastWriteTimeUtc =
            match this with
            | DirectoryEntry.Directory d -> LocalDirectory(d.ToLocalDirectoryVersion lastWriteTimeUtc)
            | DirectoryEntry.File f -> LocalFile(f.ToLocalFileVersion lastWriteTimeUtc)

    /// Specifies whether a specific entry in a local directory is a LocalDirectoryVersion or a LocalFileVersion.
    and [<KnownType("GetKnownTypes")>] LocalDirectoryEntry =
        | LocalDirectory of LocalDirectoryVersion
        | LocalFile of LocalFileVersion

        static member GetKnownTypes() = GetKnownTypes<LocalDirectoryEntry>()

        /// Converts a LocalDirectoryEntry to a DirectoryEntry.
        member this.ToDirectoryEntry: DirectoryEntry =
            match this with
            | LocalDirectory d -> DirectoryEntry.Directory d.ToDirectoryVersion
            | LocalFile f -> DirectoryEntry.File f.ToFileVersion

    /// Specifies whether a repository is public or private.
    [<KnownType("GetKnownTypes")>]
    type RepositoryType =
        | Private
        | Public

        static member GetKnownTypes() = GetKnownTypes<RepositoryType>()

    /// Specifies the current operational status of a repository.
    [<KnownType("GetKnownTypes")>]
    type RepositoryStatus =
        | Active
        | Suspended
        | Closed
        | Deleted

        static member GetKnownTypes() = GetKnownTypes<RepositoryStatus>()

    // /// Defines the type of the list of validations used in server endpoints.
    // type Validations<'T, 'U> = 'T -> Result<bool, 'U> list

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

    /// Defines the kinds of links that can exist between references.
    [<KnownType("GetKnownTypes")>]
    type ReferenceLinkType =
        | BasedOn of ReferenceId

        static member GetKnownTypes() = GetKnownTypes<ReferenceLinkType>()

    /// Defines the permissions granted to a group defined by a claim.
    ///
    /// This is combined with a RelativePath to define a PathPermission.
    [<KnownType("GetKnownTypes")>]
    type ClaimPermission = { Claim: string; DirectoryPermission: DirectoryPermission }

    /// Defines a set of claims and their permissions that should be applied to a relative path in the repository.
    ///
    /// NOTE: This type is being used only at the directory level for now, but I intend to implement it at the file level as well.
    [<KnownType("GetKnownTypes")>]
    type PathPermission = { Path: RelativePath; Permissions: List<ClaimPermission> }

    /// Cleans up extra backslashes (escape characters) and converts \r\n to Environment.NewLine.
    let cleanJson (s: string) =
        s
            .Replace("\\\\\\\\", @"\")
            .Replace("\\\\", @"\")
            .Replace(@"\r\n", Environment.NewLine)

    /// The primary type used in Grace to represent successful results.
    type GraceReturnValue<'T> =
        { ReturnValue: 'T
          EventTime: Instant
          CorrelationId: string
          Properties: Dictionary<String, String> }

        /// Adds a property to a GraceResult instance.
        //static member enhance<'T> (key, value) (result: GraceReturnValue<'T>) =
        //    if not <| String.IsNullOrEmpty(key) then
        //        result.Properties[key] <- value

        //    result

        static member Create<'T> (returnValue: 'T) (correlationId: string) =
            { ReturnValue = returnValue; EventTime = getCurrentInstant (); CorrelationId = correlationId; Properties = new Dictionary<String, String>() }

        static member CreateWithMetadata<'T> (returnValue: 'T) (correlationId: string) (properties: Dictionary<String, String>) =
            { ReturnValue = returnValue; EventTime = getCurrentInstant (); CorrelationId = correlationId; Properties = properties }

        /// Adds a key-value pair to GraceReturnValue's Properties dictionary.
        member this.enhance(key, value) =
            match String.IsNullOrEmpty(key), String.IsNullOrEmpty(value) with
            | false, false -> this.Properties[key] <- value
            | false, true -> this.Properties[key] <- String.Empty
            | true, _ -> ()

            this

        override this.ToString() =
            // Breaking out the Properties because Dictionary<> doesn't have a good ToString() method.
            let output =
                {| ReturnValue = this.ReturnValue
                   EventTime = this.EventTime
                   CorrelationId = this.CorrelationId
                   Properties = this.Properties.Select(fun kvp -> {| Key = kvp.Key; Value = kvp.Value |}) |}
            //logToConsole $"GraceReturnValue: {serialize output}"
            serialize output

    /// The primary type used in Grace to represent error results.
    type GraceError =
        { Error: string
          EventTime: Instant
          CorrelationId: string
          Properties: Dictionary<String, String> }

        static member Default =
            { Error = "Empty error message"; EventTime = getCurrentInstant (); CorrelationId = String.Empty; Properties = new Dictionary<String, String>() }

        static member Create (error: string) (correlationId: string) =
            { Error = error; EventTime = getCurrentInstant (); CorrelationId = correlationId; Properties = new Dictionary<String, String>() }

        static member CreateWithMetadata (error: string) (correlationId: string) (properties: Dictionary<String, String>) =
            { Error = error; EventTime = getCurrentInstant (); CorrelationId = correlationId; Properties = properties }

        /// Adds a key-value pair to GraceError's Properties dictionary.
        member this.enhance(key, value) =
            match String.IsNullOrEmpty(key), String.IsNullOrEmpty(value) with
            | false, false -> this.Properties[key] <- value
            | false, true -> this.Properties[key] <- String.Empty
            | true, _ -> ()

            this

        override this.ToString() =
            let sb = stringBuilderPool.Get()

            try
                let errorText =
                    this.Properties
                    |> Seq.fold (fun (state: StringBuilder) kvp -> state.AppendLine($"  {kvp.Key}: {kvp.Value}; ")) sb

                if sb.Length >= 2 then sb.Remove(sb.Length - 2, 2) |> ignore

                $"Error: {this.Error}{Environment.NewLine}EventTime: {formatInstantExtended this.EventTime}{Environment.NewLine}CorrelationId: {this.CorrelationId}{Environment.NewLine}Properties:{Environment.NewLine}{errorText.ToString()}{Environment.NewLine}"
            finally
                stringBuilderPool.Return(sb)

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
        { DifferenceType: DifferenceType
          FileSystemEntryType: FileSystemEntryType
          RelativePath: RelativePath }

        static member Create differenceType fileSystemEntryType relativePath =
            { DifferenceType = differenceType; FileSystemEntryType = fileSystemEntryType; RelativePath = relativePath }

    type UploadMetadata = { BlobUriWithSasToken: Uri; Sha256Hash: Sha256Hash }

    /// GraceIndex is Grace's representation of the contents of the local working directory (in GraceStatus), or of the object cache (in GraceObjectCache).
    type GraceIndex = ConcurrentDictionary<DirectoryVersionId, LocalDirectoryVersion>

    /// GraceStatus is a snapshot of the status that `grace watch` holds about the repository and branch while running.
    ///
    /// It is serialized and written by `grace watch` in the inter-process cache file that Grace CLI uses to know that `grace watch` is running.
    /// It gets deserialized by Grace CLI, and is used to speed up the CLI by holding pre-computed results when running certain commands.
    ///
    /// If the interprocess cache file is missing or corrupt, Grace CLI assumes that `grace watch` is not running, and runs all commands from scratch.
    [<MessagePackObject>]
    type GraceStatus =
        { [<Key(0)>]
          Index: GraceIndex
          [<Key(1)>]
          RootDirectoryId: DirectoryVersionId
          [<Key(2)>]
          RootDirectorySha256Hash: Sha256Hash
          [<Key(3)>]
          LastSuccessfulFileUpload: Instant
          [<Key(4)>]
          LastSuccessfulDirectoryVersionUpload: Instant }

        static member Default =
            { Index = GraceIndex()
              RootDirectoryId = DirectoryVersionId.Empty
              RootDirectorySha256Hash = Sha256Hash String.Empty
              LastSuccessfulFileUpload = getCurrentInstant ()
              LastSuccessfulDirectoryVersionUpload = getCurrentInstant () }

    /// GraceObjectCache is a snapshot of the contents of the local object cache.
    [<MessagePackObject>]
    type GraceObjectCache =
        { [<Key(0)>]
          Index: GraceIndex }

        static member Default = { Index = GraceIndex() }

    /// Holds the results of a diff between two versions of a file.
    type FileDiff =
        { RelativePath: RelativePath
          FileSha1: Sha256Hash
          CreatedAt1: Instant
          FileSha2: Sha256Hash
          CreatedAt2: Instant
          IsBinary: bool
          InlineDiff: List<DiffPiece[]>
          SideBySideOld: List<DiffPiece[]>
          SideBySideNew: List<DiffPiece[]> }

        static member Create
            (relativePath: RelativePath)
            (fileSha1: Sha256Hash)
            (createdAt1: Instant)
            (fileSha2: Sha256Hash)
            (createdAt2: Instant)
            (isBinary: bool)
            (inlineDiff: List<DiffPiece[]>)
            (sideBySideOld: List<DiffPiece[]>)
            (sideBySideNew: List<DiffPiece[]>)
            =
            { RelativePath = relativePath
              FileSha1 = fileSha1
              CreatedAt1 = createdAt1
              FileSha2 = fileSha2
              CreatedAt2 = createdAt2
              IsBinary = isBinary
              InlineDiff = inlineDiff
              SideBySideOld = sideBySideOld
              SideBySideNew = sideBySideNew }

    [<KnownType("GetKnownTypes")>]
    type PromotionType =
        | SingleStep
        | Complex

        static member GetKnownTypes() = GetKnownTypes<PromotionType>()

        override this.ToString() = Utilities.getDiscriminatedUnionFullName this

    /// Holds the entity Id's involved in an API call. It's populated in ValidateIds.Middleware.fs.
    type GraceIds =
        { OwnerId: string
          OrganizationId: string
          RepositoryId: string
          BranchId: string
          CorrelationId: string
          HasOwner: bool
          HasOrganization: bool
          HasRepository: bool
          HasBranch: bool }

        static member Default =
            { OwnerId = String.Empty
              OrganizationId = String.Empty
              RepositoryId = String.Empty
              BranchId = String.Empty
              CorrelationId = String.Empty
              HasOwner = false
              HasOrganization = false
              HasRepository = false
              HasBranch = false }

        override this.ToString() = serialize this

    /// Holds the different types of reminders used in Grace.
    [<KnownType("GetKnownTypes")>]
    type ReminderTypes =
        | Maintenance
        | PhysicalDeletion
        | DeleteCachedState

        static member GetKnownTypes() = GetKnownTypes<ReminderTypes>()

        override this.ToString() = Utilities.getDiscriminatedUnionFullName this
