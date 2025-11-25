namespace Grace.Types

open DiffPlex.DiffBuilder.Model
open Grace.Shared.Constants
open Grace.Shared.Utilities
open NodaTime
open Orleans
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
    /// The Id of the branch.
    type BranchId = Guid

    /// The name of the branch.
    type BranchName = string

    /// The name of the storage container used by Grace.
    type ContainerName = string

    /// The CorrelationId used during a Grace operation.
    type CorrelationId = string

    /// The reason given for deleting a branch or reference.
    type DeleteReason = string

    /// The Id of the directory version.
    type DirectoryVersionId = Guid

    /// The file path of a file in a repository.
    type FilePath = string

    /// An entry in the .graceignore file.
    type GraceIgnoreEntry = string

    /// The Id of the organization.
    type OrganizationId = Guid

    /// The name of the organization.
    type OrganizationName = string

    /// The Id of the owner of the repository.
    type OwnerId = Guid

    /// The name of the owner of the repository.
    type OwnerName = string

    /// The Id of the parent branch.
    type ParentBranchId = BranchId

    /// The Id of the reference.
    type ReferenceId = Guid

    /// The text of the reference, generally submitted as the -m parameter in `grace save/checkpoint/commit/etc.`.
    type ReferenceText = string

    /// The Id of the reminder.
    type ReminderId = Guid

    /// The Id of the repository.
    type RepositoryId = Guid

    /// The name of the repository.
    type RepositoryName = string

    /// The path from the root directory of the repository.
    ///
    /// Example: Repository root directory is "C:\Source\Grace",
    /// the full file path is "C:\Source\Grace\src\Grace.Shared\Types.Shared.fs",
    /// and the relative path is "src\Grace.Shared\Types.Shared.fs".
    type RelativePath = string

    /// A SHA-256 hash value.
    type Sha256Hash = string

    type StorageAccountName = string
    type StorageConnectionString = string
    type StorageContainerName = string

    /// A Uri for a file in object storage that has a shared access signature (SAS) token.
    type UriWithSharedAccessSignature = Uri

    type UserId = string

    /// The result of a single validation check.
    type ValidationResult<'T> = ValueTask<Result<unit, 'T>>

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type LineEndings =
        | CrLf
        | Cr
        | PlatformDependent

        static member GetKnownTypes() = GetKnownTypes<LineEndings>()

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ObjectStorageProvider =
        | AWSS3
        | AzureBlobStorage
        | GoogleCloudStorage
        | Unknown // Not calling it None because that really belongs to Option.

        static member DefaultObjectStorageProvider = ObjectStorageProvider.AzureBlobStorage

        static member GetKnownTypes() = GetKnownTypes<ObjectStorageProvider>()

    /// Indicates what database is being used for Actor state storage.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ActorStateStorageProvider =
        | AzureCosmosDb
        | MongoDB
        | Unknown // Not calling it None because that really belongs to Option.
        // etc.
        static member GetKnownTypes() = GetKnownTypes<ActorStateStorageProvider>()

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type OwnerType =
        | Public
        | Private

        override this.ToString() = getDiscriminatedUnionFullName this

        static member GetKnownTypes() = GetKnownTypes<OwnerType>()

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type OrganizationType =
        | Public
        | Private

        override this.ToString() = getDiscriminatedUnionFullName this

        static member GetKnownTypes() = GetKnownTypes<OrganizationType>()

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type SearchVisibility =
        | Visible
        | NotVisible

        override this.ToString() = getDiscriminatedUnionFullName this

        static member GetKnownTypes() = GetKnownTypes<SearchVisibility>()

    /// Defines the different types of references that can exist in a branch.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ReferenceType =
        | Promotion
        | Commit
        | Checkpoint
        | Save
        | Tag
        | External
        | Rebase

        override this.ToString() = getDiscriminatedUnionFullName this

        static member FromString s = discriminatedUnionFromString<ReferenceType> s

        static member GetKnownTypes() = GetKnownTypes<ReferenceType>()

    // Records

    /// EventMetadata is included in the recording of every event that occurs in Grace.
    [<CLIMutable>]
    type EventMetadata =
        { Timestamp: Instant
          CorrelationId: CorrelationId
          Principal: string
          Properties: Dictionary<string, obj> }

        override this.ToString() = serialize this

        static member New correlationId principal =
            { Timestamp = getCurrentInstant (); CorrelationId = correlationId; Principal = principal; Properties = Dictionary<string, obj>() }

    /// A FileVersion represents a version of a file in a repository with unique contents, and therefore with a unique SHA-256 hash. It is immutable.
    ///
    /// It is the server-side representation of the LocalFileVersion type, used for the local object cache.
    [<CLIMutable; MessagePackObject; GenerateSerializer>]
    type FileVersion =
        { [<Key(0)>]
          Class: string
          //RepositoryId: RepositoryId
          [<Key(1)>]
          RelativePath: RelativePath
          [<Key(2)>]
          Sha256Hash: Sha256Hash
          [<Key(3)>]
          IsBinary: bool
          [<Key(4)>]
          Size: int64
          [<Key(5)>]
          CreatedAt: Instant
          [<Key(6)>]
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
        [<IgnoreMember>]
        member this.GetObjectFileName = getObjectFileName this.RelativePath this.Sha256Hash

        /// Gets the relative directory path of the file. Example: "/dir/subdir/file.js" -> "/dir/subdir/".
        [<IgnoreMember>]
        member this.RelativeDirectory = getRelativeDirectory $"{this.RelativePath}" ""

    /// A LocalFileVersion represents a version of a file in a repository with unique contents, and therefore with a unique SHA-256 hash. It is immutable.
    ///
    /// It is the local representation of the FileVersion type, used on the server.
    and [<CLIMutable; MessagePackObject>] LocalFileVersion =
        { [<Key(0)>]
          Class: string
          //[<Key(1)>]
          //RepositoryId: RepositoryId
          [<Key(1)>]
          RelativePath: RelativePath
          [<Key(2)>]
          Sha256Hash: Sha256Hash
          [<Key(3)>]
          IsBinary: bool
          [<Key(4)>]
          Size: int64
          [<Key(5)>]
          CreatedAt: Instant
          [<Key(6)>]
          UploadedToObjectStorage: bool
          [<Key(7)>]
          LastWriteTimeUtc: DateTime }

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
    [<CLIMutable; MessagePackObject; GenerateSerializer>]
    type DirectoryVersion =
        { [<Key(0)>]
          Class: string
          [<Key(1)>]
          DirectoryVersionId: DirectoryVersionId
          [<Key(2)>]
          OwnerId: OwnerId
          [<Key(3)>]
          OrganizationId: OrganizationId
          [<Key(4)>]
          RepositoryId: RepositoryId
          [<Key(5)>]
          RelativePath: RelativePath
          [<Key(6)>]
          Sha256Hash: Sha256Hash
          [<Key(7)>]
          Directories: List<DirectoryVersionId>
          [<Key(8)>]
          Files: List<FileVersion>
          [<Key(9)>]
          Size: int64
          [<Key(10)>]
          CreatedAt: Instant }

        static member GetKnownTypes() = GetKnownTypes<DirectoryVersion>()

        static member Default =
            { Class = nameof DirectoryVersion
              DirectoryVersionId = DirectoryVersionId.Empty
              OwnerId = OwnerId.Empty
              OrganizationId = OrganizationId.Empty
              RepositoryId = RepositoryId.Empty
              RelativePath = RelativePath String.Empty
              Sha256Hash = Sha256Hash String.Empty
              Directories = List<DirectoryVersionId>()
              Files = List<FileVersion>()
              Size = InitialDirectorySize
              CreatedAt = DefaultTimestamp }

        static member Create
            (directoryVersionId: DirectoryVersionId)
            (ownerId: OwnerId)
            (organizationId: OrganizationId)
            (repositoryId: RepositoryId)
            (relativePath: RelativePath)
            (sha256Hash: Sha256Hash)
            (directories: List<DirectoryVersionId>)
            (files: List<FileVersion>)
            (size: int64)
            =
            { Class = nameof DirectoryVersion
              DirectoryVersionId = directoryVersionId
              OwnerId = ownerId
              OrganizationId = organizationId
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
                this.OwnerId
                this.OrganizationId
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
    and [<CLIMutable; MessagePackObject>] LocalDirectoryVersion =
        { [<Key(0)>]
          Class: string
          [<Key(1)>]
          DirectoryVersionId: DirectoryVersionId
          [<Key(2)>]
          OwnerId: OwnerId
          [<Key(3)>]
          OrganizationId: OrganizationId
          [<Key(4)>]
          RepositoryId: RepositoryId
          [<Key(5)>]
          RelativePath: RelativePath
          [<Key(6)>]
          Sha256Hash: Sha256Hash
          [<Key(7)>]
          Directories: List<DirectoryVersionId>
          [<Key(8)>]
          Files: List<LocalFileVersion>
          [<Key(9)>]
          Size: int64
          [<Key(10)>]
          CreatedAt: Instant
          [<Key(11)>]
          LastWriteTimeUtc: DateTime }

        static member Default =
            { Class = "LocalDirectoryVersion"
              OwnerId = OwnerId.Empty
              OrganizationId = OrganizationId.Empty
              RepositoryId = RepositoryId.Empty
              DirectoryVersionId = DirectoryVersionId.Empty
              RelativePath = RelativePath String.Empty
              Sha256Hash = Sha256Hash String.Empty
              Directories = List<DirectoryVersionId>()
              Files = List<LocalFileVersion>()
              Size = InitialDirectorySize
              CreatedAt = DefaultTimestamp
              LastWriteTimeUtc = DateTime.UtcNow }

        static member Create
            (directoryVersionId: DirectoryVersionId)
            (ownerId: OwnerId)
            (organizationId: OrganizationId)
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
              OwnerId = ownerId
              OrganizationId = organizationId
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
                this.OwnerId
                this.OrganizationId
                this.RepositoryId
                this.RelativePath
                this.Sha256Hash
                this.Directories
                (this.Files.Select(fun f -> f.ToFileVersion).ToList())
                this.Size

    /// Specifies whether a specific entry in a directory is a DirectoryVersion or a FileVersion.
    and [<KnownType("GetKnownTypes"); GenerateSerializer>] DirectoryEntry =
        | Directory of DirectoryVersion
        | File of FileVersion

        static member GetKnownTypes() = GetKnownTypes<DirectoryEntry>()

        /// Converts a DirectoryEntry to a LocalDirectoryEntry.
        member this.ToLocalDirectoryEntry lastWriteTimeUtc =
            match this with
            | DirectoryEntry.Directory d -> LocalDirectory(d.ToLocalDirectoryVersion lastWriteTimeUtc)
            | DirectoryEntry.File f -> LocalFile(f.ToLocalFileVersion lastWriteTimeUtc)

    /// Specifies whether a specific entry in a local directory is a LocalDirectoryVersion or a LocalFileVersion.
    and [<KnownType("GetKnownTypes"); GenerateSerializer>] LocalDirectoryEntry =
        | LocalDirectory of LocalDirectoryVersion
        | LocalFile of LocalFileVersion

        static member GetKnownTypes() = GetKnownTypes<LocalDirectoryEntry>()

        /// Converts a LocalDirectoryEntry to a DirectoryEntry.
        member this.ToDirectoryEntry: DirectoryEntry =
            match this with
            | LocalDirectory d -> DirectoryEntry.Directory d.ToDirectoryVersion
            | LocalFile f -> DirectoryEntry.File f.ToFileVersion

    /// Specifies whether a repository is public or private.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type RepositoryType =
        | Private
        | Public

        static member GetKnownTypes() = GetKnownTypes<RepositoryType>()

    /// Specifies the current operational status of a repository.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type RepositoryStatus =
        | Active
        | Suspended
        | Closed
        | Deleted

        static member GetKnownTypes() = GetKnownTypes<RepositoryStatus>()

    // /// Defines the type of the list of validations used in server endpoints.
    // type Validations<'T, 'U> = 'T -> Result<bool, 'U> list

    /// Defines the specific permissions that can be granted to a user or group on a directory.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type DirectoryPermission =
        | FullControl
        | Modify
        | ListContents
        | Read
        | NoAccess
        | NotSet

        static member GetKnownTypes() = GetKnownTypes<DirectoryPermission>()

    /// Defines the kinds of links that can exist between references.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ReferenceLinkType =
        | BasedOn of ReferenceId
        | IncludedInPromotionGroup of Guid
        | PromotionGroupTerminal of Guid  // Marks the final promotion in a promotion group.

        static member GetKnownTypes() = GetKnownTypes<ReferenceLinkType>()

    /// Defines the permissions granted to a group defined by a claim.
    ///
    /// This is combined with a RelativePath to define a PathPermission.
    [<GenerateSerializer>]
    type ClaimPermission = { Claim: string; DirectoryPermission: DirectoryPermission }

    /// Defines a set of claims and their permissions that should be applied to a relative path in the repository.
    ///
    /// NOTE: This type is being used only at the directory level for now, but I intend to implement it at the file level as well.
    [<GenerateSerializer>]
    type PathPermission = { Path: RelativePath; Permissions: List<ClaimPermission> }

    /// Cleans up extra backslashes (escape characters) and converts \r\n to Environment.NewLine.
    let cleanJson (s: string) =
        s
            .Replace("\\\\\\\\", @"\")
            .Replace("\\\\", @"\")
            .Replace(@"\r\n", Environment.NewLine)

    /// A serializable view of a .NET Exception
    type ExceptionObject =
        { Message: string
          StackTrace: string
          InnerException: ExceptionObject option }

        /// Checks if the ExceptionObject is set to the default value.
        member this.IsDefault() = this = ExceptionObject.Default

        static member Default = { Message = String.Empty; StackTrace = String.Empty; InnerException = None }

        /// Creates an ExceptionObject from a .NET Exception.
        static member Create(ex: Exception) =
            { Message = ex.Message
              StackTrace = if String.IsNullOrEmpty ex.StackTrace then String.Empty else ex.StackTrace
              InnerException =
                if ex.InnerException <> null then
                    Some(ExceptionObject.Create(ex.InnerException))
                else
                    None }

    /// The primary type used in Grace to represent successful results.
    type GraceReturnValue<'T> =
        { ReturnValue: 'T
          EventTime: Instant
          CorrelationId: string
          Properties: Dictionary<string, obj> }

        static member CreateWithMetadata<'T> (returnValue: 'T) (correlationId: string) (properties: Dictionary<string, obj>) =
            { ReturnValue = returnValue; EventTime = getCurrentInstant (); CorrelationId = correlationId; Properties = properties }

        static member Create<'T> (returnValue: 'T) (correlationId: string) =
            GraceReturnValue.CreateWithMetadata returnValue correlationId (Dictionary<string, obj>())

        /// Adds a key-value pair to GraceReturnValue's Properties dictionary.
        member this.enhance((key: string), (value: obj)) =
            //logToConsole $"In GraceReturnValue.enhance: Enhancing GraceReturnValue with key: {key}, value: {value}."

            match String.IsNullOrEmpty(key), isNull (value) with
            | false, false -> this.Properties[key] <- value
            | false, true -> this.Properties[key] <- null
            | true, _ -> ()

            this

        /// Adds a set of key-value pairs from a Dictionary to GraceReturnValue's Properties dictionary.
        member this.enhance(dict: IReadOnlyDictionary<string, obj>) =
            // logToConsole $"In GraceReturnValue.enhance: isNull(dict): {isNull (dict)}."
            // logToConsole $"In GraceReturnValue.enhance: Enhancing GraceReturnValue with {dict.Count} properties."

            dict |> Seq.iter (fun kvp -> this.enhance (kvp.Key, kvp.Value) |> ignore)

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
        { Exception: ExceptionObject
          Error: string
          EventTime: Instant
          CorrelationId: string
          Properties: Dictionary<string, obj> }

        static member Default =
            { Exception = ExceptionObject.Default
              Error = "Empty error message"
              EventTime = getCurrentInstant ()
              CorrelationId = String.Empty
              Properties = new Dictionary<string, obj>() }

        static member Create (error: string) (correlationId: string) =
            { Exception = ExceptionObject.Default
              Error = error
              EventTime = getCurrentInstant ()
              CorrelationId = correlationId
              Properties = new Dictionary<string, obj>() }

        static member CreateWithException (ex: Exception) (error: string) (correlationId: string) =
            { Exception = ExceptionObject.Create(ex)
              Error = error
              EventTime = getCurrentInstant ()
              CorrelationId = correlationId
              Properties = new Dictionary<string, obj>() }

        static member CreateWithMetadata (ex: Exception) (error: string) (correlationId: string) (properties: Dictionary<string, obj>) =
            { Exception = ExceptionObject.Create(ex); Error = error; EventTime = getCurrentInstant (); CorrelationId = correlationId; Properties = properties }

        /// Adds a key-value pair to GraceError's Properties dictionary.
        member this.enhance(key: string, value: obj) =
            match String.IsNullOrEmpty(key), isNull (value) with
            | false, false -> this.Properties[key] <- value
            | false, true -> this.Properties[key] <- null
            | true, _ -> ()

            this

        /// Adds a set of key-value pairs from a Dictionary to GraceError's Properties dictionary.
        member this.enhance(dict: IReadOnlyDictionary<string, obj>) =
            dict |> Seq.iter (fun kvp -> this.Properties[kvp.Key] <- kvp.Value)
            this

        override this.ToString() =
            let sb = stringBuilderPool.Get()

            try
                let errorText =
                    this.Properties
                    |> Seq.fold (fun (state: StringBuilder) kvp -> state.AppendLine($"  {kvp.Key}: {kvp.Value}; ")) sb

                if sb.Length >= 2 then sb.Remove(sb.Length - 2, 2) |> ignore

                let message = if this.Exception.IsDefault() then this.Error else (serialize this.Exception)

                $"Error: {message}{Environment.NewLine}EventTime: {formatInstantExtended this.EventTime}{Environment.NewLine}CorrelationId: {this.CorrelationId}{Environment.NewLine}Properties:{Environment.NewLine}{errorText.ToString()}{Environment.NewLine}"
            finally
                stringBuilderPool.Return(sb)

    /// The primary type used to represent Grace operations results.
    type GraceResult<'T> = Result<GraceReturnValue<'T>, GraceError>

    /// Specifies whether a file system entry is a directory or a file.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type FileSystemEntryType =
        | Directory
        | File

        static member GetKnownTypes() = GetKnownTypes<FileSystemEntryType>()

        override this.ToString() = getDiscriminatedUnionFullName this

    /// Specifies whether a change detected in a diff is an add, change, or delete.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type DifferenceType =
        | Add
        | Change
        | Delete

        static member GetKnownTypes() = GetKnownTypes<DifferenceType>()

        override this.ToString() = getDiscriminatedUnionFullName this

    /// A file system difference is a change detected (at a file level) in a diff. It specifies the type of change (add, change, or delete), the type of file system entry (directory or file), and the relative path of the entry.
    [<GenerateSerializer>]
    type FileSystemDifference =
        { DifferenceType: DifferenceType
          FileSystemEntryType: FileSystemEntryType
          RelativePath: RelativePath }

        static member Create differenceType fileSystemEntryType relativePath =
            { DifferenceType = differenceType; FileSystemEntryType = fileSystemEntryType; RelativePath = relativePath }

    [<GenerateSerializer>]
    type UploadMetadata = { BlobUriWithSasToken: Uri; Sha256Hash: Sha256Hash }

    /// GraceIndex is Grace's representation of the contents of the local working directory (in GraceStatus), or of the object cache (in GraceObjectCache).
    [<GenerateSerializer>]
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
    [<GenerateSerializer>]
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

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type PromotionType =
        | SingleStep
        | Complex

        static member GetKnownTypes() = GetKnownTypes<PromotionType>()

        override this.ToString() = getDiscriminatedUnionFullName this

    /// Defines how promotions are handled for a branch.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type BranchPromotionMode =
        | IndividualOnly   // Current behavior: promotions always applied individually.
        | GroupOnly        // Promotions to this branch must go through a promotion group.
        | Hybrid           // Promotions can be grouped by default, with an opt-out flag.

        static member GetKnownTypes() = GetKnownTypes<BranchPromotionMode>()

        override this.ToString() = getDiscriminatedUnionFullName this

    /// Defines the conflict resolution policy for a repository.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ConflictResolutionPolicy =
        | NoConflicts of unit                         // Any conflict blocks the promotion group.
        | ConflictsAllowedWithConfidence of float     // Conflicts allowed if model confidence >= threshold (0.0 to 1.0).

        static member GetKnownTypes() = GetKnownTypes<ConflictResolutionPolicy>()

        override this.ToString() = getDiscriminatedUnionFullName this

    /// Holds the entity Id's involved in an API call. It's populated in ValidateIds.Middleware.fs.
    type GraceIds =
        { OwnerId: OwnerId
          OwnerIdString: string
          OwnerName: OwnerName
          OrganizationId: OrganizationId
          OrganizationIdString: string
          OrganizationName: OrganizationName
          RepositoryId: RepositoryId
          RepositoryIdString: string
          RepositoryName: RepositoryName
          BranchId: BranchId
          BranchIdString: string
          BranchName: BranchName
          CorrelationId: string
          HasOwner: bool
          HasOrganization: bool
          HasRepository: bool
          HasBranch: bool }

        static member Default =
            { OwnerId = OwnerId.Empty
              OwnerIdString = String.Empty
              OwnerName = OwnerName.Empty
              OrganizationId = OrganizationId.Empty
              OrganizationIdString = String.Empty
              OrganizationName = OrganizationName.Empty
              RepositoryId = RepositoryId.Empty
              RepositoryIdString = String.Empty
              RepositoryName = RepositoryName.Empty
              BranchId = BranchId.Empty
              BranchIdString = String.Empty
              BranchName = BranchName.Empty
              CorrelationId = String.Empty
              HasOwner = false
              HasOrganization = false
              HasRepository = false
              HasBranch = false }

        override this.ToString() = serialize this

    /// Defines the different types of reminders used in Grace.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ReminderTypes =
        /// Maintenance reminders are used to remind the actor to perform maintenance on itself.
        | Maintenance
        /// Physical deletion reminders are used to remind the actor to delete itself after the LogicalDelete hold time has expired.
        | PhysicalDeletion
        /// DeleteCachedState reminders are used to remind the actor to delete its cached state after the time set in the repository has expired.
        | DeleteCachedState
        /// DeleteZipFile reminders are used to remind the DirectoryVersion actor to delete the directory version contents .zip file after the time set in the repository has expired.
        | DeleteZipFile

        static member GetKnownTypes() = GetKnownTypes<ReminderTypes>()

        override this.ToString() = getDiscriminatedUnionFullName this

    /// Defines the different statuses of a .zip file.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ZipFileStatus =
        | NotCreated
        | Creating
        | Exists

    [<GenerateSerializer>]
    type Appearance = { Root: DirectoryVersionId; Parent: DirectoryVersionId; Created: Instant }
