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

/// Contains common helpers.
module Common =

    // Domain nouns
    /// The Id of the branch.
    type BranchId = Guid

    /// The name of the branch.
    type BranchName = string

    /// The name of the storage container used by Grace.
    type ContainerName = string

    /// The CorrelationId used during a Grace operation.
    type CorrelationId = string

    /// Identifies the Grace client that initiated an API request.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ClientType =
        | CLI of Version: string

        /// Returns the display representation for this value.
        override this.ToString() = getDiscriminatedUnionFullName this

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ClientType>()

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

    /// The Id of the work item.
    type WorkItemId = Guid

    /// The repository-scoped monotonically increasing work item number.
    type WorkItemNumber = int64

    /// The Id of the promotion set.
    type PromotionSetId = Guid

    /// The Id of a promotion set step.
    type PromotionSetStepId = Guid

    /// The Id of the review notes payload.
    type ReviewNotesId = Guid

    /// The Id of the review checkpoint.
    type ReviewCheckpointId = Guid

    /// The Id of an analysis receipt.
    type AnalysisReceiptId = Guid

    /// The Id of a conflict resolution receipt.
    type ConflictReceiptId = Guid

    /// The Id of a validation set.
    type ValidationSetId = Guid

    /// The Id of a validation result.
    type ValidationResultId = Guid

    /// The Id of an artifact.
    type ArtifactId = Guid

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

    /// A BLAKE3 hash value.
    type Blake3Hash = string

    /// The content-addressed identifier for one chunk of manifest-backed file content.
    type ChunkAddress = string

    /// The content-addressed identifier for a reusable content block.
    type ContentBlockAddress = string

    /// The content-addressed identifier for a file manifest.
    type ManifestAddress = string

    /// The StoragePool-wide scope for manifest-backed content-addressed storage.
    type StoragePoolId = string

    /// Optimistic concurrency version for mutable content block metadata.
    type MetadataVersion = int64

    /// The Id of an upload session.
    type UploadSessionId = Guid

    /// Caller-supplied retry identity for upload-session commands.
    type UploadSessionOperationId = string

    /// Caller-supplied retry identity for repository-content-counter commands.
    type RepositoryContentCounterOperationId = string

    /// Caller-supplied retry identity for manifest-contribution workflow commands.
    type ManifestContributionWorkflowOperationId = string

    /// Stable retry identity for manifest-backed content ownership ledger commands.
    type ContentOwnershipLedgerOperationId = string

    /// Stable identity for one active manifest-backed usage entry in the content ownership ledger.
    type ContentOwnershipLedgerActiveUsageId = string

    /// Repository-local count of live references to a manifest-backed file.
    type ReferenceCount = int64

    /// The content-derived hash for a complete manifest-backed file.
    type FileContentHash = string

    /// The versioned chunking suite used for a manifest-backed file.
    type ChunkingSuiteId = string

    /// Represents storage account name.
    type StorageAccountName = string
    /// Represents storage connection string.
    type StorageConnectionString = string
    /// Represents storage container name.
    type StorageContainerName = string

    /// A Uri for a file in object storage that has a shared access signature (SAS) token.
    type UriWithSharedAccessSignature = Uri

    /// Represents user id.
    type UserId = string

    /// The result of a single validation check.
    type ValidationResult<'T> = ValueTask<Result<unit, 'T>>

    /// Represents line endings.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type LineEndings =
        | CrLf
        | Cr
        | PlatformDependent

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<LineEndings>()

    /// Represents object storage provider.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ObjectStorageProvider =
        | AWSS3
        | AzureBlobStorage
        | GoogleCloudStorage
        | Unknown // Not calling it None because that really belongs to Option.

        /// Selects Azure Blob Storage as the default object-store provider for new Grace configuration.
        static member DefaultObjectStorageProvider = ObjectStorageProvider.AzureBlobStorage

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ObjectStorageProvider>()

    /// Indicates what database is being used for Actor state storage.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ActorStateStorageProvider =
        | AzureCosmosDb
        | MongoDB
        | Unknown // Not calling it None because that really belongs to Option.
        // etc.
        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ActorStateStorageProvider>()

    /// Represents owner type.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type OwnerType =
        | Public
        | Private

        /// Returns the display representation for this value.
        override this.ToString() = getDiscriminatedUnionFullName this

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<OwnerType>()

    /// Represents organization type.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type OrganizationType =
        | Public
        | Private

        /// Returns the display representation for this value.
        override this.ToString() = getDiscriminatedUnionFullName this

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<OrganizationType>()

    /// Represents search visibility.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type SearchVisibility =
        | Visible
        | NotVisible

        /// Returns the display representation for this value.
        override this.ToString() = getDiscriminatedUnionFullName this

        /// Returns known nested union types for serializers.
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

        /// Returns the display representation for this value.
        override this.ToString() = getDiscriminatedUnionFullName this

        /// Parses a configured object-store provider name, accepting known names case-insensitively.
        static member FromString s = discriminatedUnionFromString<ReferenceType> s

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ReferenceType>()

    // Records

    /// EventMetadata is included in the recording of every event that occurs in Grace.
    [<CLIMutable>]
    type EventMetadata =
        {
            Timestamp: Instant
            CorrelationId: CorrelationId
            Principal: string
            ClientType: ClientType option
            Properties: Dictionary<string, string>
        }

        /// Returns the display representation for this value.
        override this.ToString() = serialize this

        /// Initializes the value with a fresh identifier while preserving the supplied domain fields.
        static member New correlationId principal =
            {
                Timestamp = getCurrentInstant ()
                CorrelationId = correlationId
                Principal = principal
                ClientType = Microsoft.FSharp.Core.Option.None
                Properties = Dictionary<string, string>()
            }

    /// Repository-level creation policy for choosing whole-file content or manifest-backed content.
    ///
    /// The policy is a creation-time gate. Grace stores the resulting content reference shape rather than a separate
    /// mutable eligibility decision.
    [<CLIMutable; MessagePackObject; GenerateSerializer>]
    type ManifestEligibilityPolicy =
        {
            [<Key(0)>]
            Class: string
            [<Key(1)>]
            ThresholdBytes: int64
            [<Key(2)>]
            BinaryScanBytes: int
        }

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default = { Class = nameof ManifestEligibilityPolicy; ThresholdBytes = 1024L * 1024L; BinaryScanBytes = 8 * 1024 }

    /// A content block is an inert CAS contract shell for a contiguous byte range inside manifest-backed file content.
    [<CLIMutable; MessagePackObject; GenerateSerializer>]
    type ContentBlock =
        {
            [<Key(0)>]
            Class: string
            [<Key(1)>]
            Address: ContentBlockAddress
            [<Key(2)>]
            Offset: int64
            [<Key(3)>]
            Size: int64
        }

        /// Builds the contract value from required caller inputs and generated defaults used by this surface.
        static member Create(address: ContentBlockAddress, offset: int64, size: int64) =
            { Class = "ContentBlock"; Address = address; Offset = offset; Size = size }

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default = ContentBlock.Create(String.Empty, 0L, 0L)

    /// A file manifest is a CAS contract shell for content assembled from one or more content blocks.
    ///
    /// Validation reconstructs bytes by reading `Blocks` in order. Each block range must be positive, contiguous from
    /// offset zero, and cover exactly `Size`. `ChunkingSuiteId`, `FileContentHash`, and the ordered block ranges are
    /// included in the stable manifest address preimage.
    [<CLIMutable; MessagePackObject; GenerateSerializer; CustomEquality; NoComparison>]
    type FileManifest =
        {
            [<Key(0)>]
            Class: string
            [<Key(1)>]
            ManifestAddress: ManifestAddress
            [<Key(2)>]
            Size: int64
            [<Key(3)>]
            Blocks: List<ContentBlock>
            [<Key(4)>]
            ChunkingSuiteId: ChunkingSuiteId
            [<Key(5)>]
            FileContentHash: FileContentHash
            [<Key(6)>]
            StoragePoolId: StoragePoolId
        }

        /// Builds the contract value from required caller inputs and generated defaults used by this surface.
        static member Create
            (
                manifestAddress: ManifestAddress,
                chunkingSuiteId: ChunkingSuiteId,
                fileContentHash: FileContentHash,
                size: int64,
                storagePoolId: StoragePoolId,
                blocks: ContentBlock list
            ) =
            {
                Class = "FileManifest"
                ManifestAddress = manifestAddress
                Size = size
                Blocks = List<ContentBlock>(blocks)
                ChunkingSuiteId = chunkingSuiteId
                FileContentHash = fileContentHash
                StoragePoolId = storagePoolId
            }

        /// Builds the contract value from required caller inputs and generated defaults used by this surface.
        static member Create
            (
                manifestAddress: ManifestAddress,
                chunkingSuiteId: ChunkingSuiteId,
                fileContentHash: FileContentHash,
                size: int64,
                blocks: ContentBlock list
            ) =
            FileManifest.Create(manifestAddress, chunkingSuiteId, fileContentHash, size, StoragePoolId "default", blocks)

        /// Builds the contract value from required caller inputs and generated defaults used by this surface.
        static member Create(manifestAddress: ManifestAddress, size: int64, blocks: ContentBlock list) =
            FileManifest.Create(manifestAddress, ChunkingSuiteId String.Empty, FileContentHash String.Empty, size, blocks)

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default = FileManifest.Create(String.Empty, 0L, [])

        /// Compares the domain identity fields that define whether two values refer to the same Grace object.
        override this.Equals(other: obj) =
            match other with
            | :? FileManifest as otherManifest ->
                this.Class = otherManifest.Class
                && this.ManifestAddress = otherManifest.ManifestAddress
                && this.Size = otherManifest.Size
                && this.ChunkingSuiteId = otherManifest.ChunkingSuiteId
                && this.FileContentHash = otherManifest.FileContentHash
                && this.StoragePoolId = otherManifest.StoragePoolId
                && this.Blocks.Count = otherManifest.Blocks.Count
                && Seq.forall2 (=) this.Blocks otherManifest.Blocks
            | _ -> false

        /// Computes a hash code from the same domain identity fields used by equality.
        override this.GetHashCode() =
            let mutable hashCode = HashCode()
            hashCode.Add(this.Class)
            hashCode.Add(this.ManifestAddress)
            hashCode.Add(this.Size)
            hashCode.Add(this.ChunkingSuiteId)
            hashCode.Add(this.FileContentHash)
            hashCode.Add(this.StoragePoolId)

            for block in this.Blocks do
                hashCode.Add(block)

            hashCode.ToHashCode()

    /// Identifies the shape of a FileVersion content reference.
    type FileContentReferenceType =
        | WholeFileContent = 0
        | FileManifest = 1

    /// Identifies how a FileVersion refers to its file bytes.
    [<CLIMutable; MessagePackObject; GenerateSerializer>]
    type FileContentReference =
        {
            [<Key(0)>]
            Class: string
            [<Key(1)>]
            ReferenceType: FileContentReferenceType
            [<Key(2)>]
            Manifest: FileManifest option
        }

        /// Classifies the object as whole-file content rather than a manifest or content block.
        static member WholeFileContent = { Class = "FileContentReference"; ReferenceType = FileContentReferenceType.WholeFileContent; Manifest = None }

        /// Classifies the object as a file manifest that references content blocks.
        static member FileManifest manifest =
            { Class = "FileContentReference"; ReferenceType = FileContentReferenceType.FileManifest; Manifest = Some manifest }

    /// A FileVersion represents a version of a file in a repository with unique contents, and therefore with unique
    /// SHA-256 and BLAKE3 hashes. It is immutable.
    ///
    /// It is the server-side representation of the LocalFileVersion type, used for the local object cache.
    [<MessagePackObject; GenerateSerializer>]
    type FileVersion() =
        [<Key(0)>]
        member val Class: string = "FileVersion" with get, set

        //RepositoryId: RepositoryId
        [<Key(1)>]
        member val RelativePath: RelativePath = String.Empty with get, set

        [<Key(2)>]
        member val Sha256Hash: Sha256Hash = String.Empty with get, set

        [<Key(8)>]
        member val Blake3Hash: Blake3Hash = String.Empty with get, set

        [<Key(3)>]
        member val IsBinary: bool = false with get, set

        [<Key(4)>]
        member val Size: int64 = 0L with get, set

        [<Key(5)>]
        member val CreatedAt: Instant = getCurrentInstant () with get, set

        [<Key(6)>]
        member val BlobUri: string = String.Empty with get, set

        [<Key(7)>]
        member val ContentReference: FileContentReference = FileContentReference.WholeFileContent with get, set

        /// Builds a content-addressed value carrying both BLAKE3 and SHA-256 hashes for compatibility checks.
        static member CreateWithHashes
            //(repositoryId: RepositoryId)
            (relativePath: RelativePath)
            (sha256Hash: Sha256Hash)
            (blake3Hash: Blake3Hash)
            (blobUri: string)
            (isBinary: bool)
            (size: int64)
            =
            let fileVersion = FileVersion()
            fileVersion.Class <- "FileVersion"
            //fileVersion.RepositoryId <- repositoryId
            fileVersion.RelativePath <- RelativePath(normalizeFilePath $"{relativePath}")
            fileVersion.Sha256Hash <- sha256Hash
            fileVersion.Blake3Hash <- blake3Hash
            fileVersion.BlobUri <- blobUri
            fileVersion.IsBinary <- isBinary
            fileVersion.Size <- size
            fileVersion.CreatedAt <- getCurrentInstant ()
            fileVersion.ContentReference <- FileContentReference.WholeFileContent
            fileVersion

        /// Builds the contract value from required caller inputs and generated defaults used by this surface.
        static member Create
            //(repositoryId: RepositoryId)
            (relativePath: RelativePath)
            (sha256Hash: Sha256Hash)
            (blobUri: string)
            (isBinary: bool)
            (size: int64)
            =
            FileVersion.CreateWithHashes relativePath sha256Hash (Blake3Hash String.Empty) blobUri isBinary size

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default = FileVersion.Create String.Empty String.Empty String.Empty false 0L

        /// Converts a FileVersion to a LocalFileVersion.
        member this.ToLocalFileVersion lastWriteTimeUtc =
            LocalFileVersion.CreateWithHashes this.RelativePath this.Sha256Hash this.Blake3Hash this.IsBinary this.Size this.CreatedAt true lastWriteTimeUtc

        /// Get the object directory file name, which includes the SHA256 Hash value. Example: hello.js -> hello_04bef0a4b298de9c02930234.js
        [<IgnoreMember>]
        member this.GetObjectFileName = getObjectFileName this.RelativePath this.Sha256Hash

        /// Gets the relative directory path of the file. Example: "/dir/subdir/file.js" -> "/dir/subdir/".
        [<IgnoreMember>]
        member this.RelativeDirectory = getRelativeDirectory $"{this.RelativePath}" ""

        /// Compares the domain identity fields that define whether two values refer to the same Grace object.
        override this.Equals(other: obj) =
            match other with
            | :? FileVersion as otherFileVersion ->
                this.Class = otherFileVersion.Class
                && this.RelativePath = otherFileVersion.RelativePath
                && this.Sha256Hash = otherFileVersion.Sha256Hash
                && this.Blake3Hash = otherFileVersion.Blake3Hash
                && this.IsBinary = otherFileVersion.IsBinary
                && this.Size = otherFileVersion.Size
                && this.CreatedAt = otherFileVersion.CreatedAt
                && this.BlobUri = otherFileVersion.BlobUri
                && this.ContentReference = otherFileVersion.ContentReference
            | _ -> false

        /// Computes a hash code from the same domain identity fields used by equality.
        override this.GetHashCode() =
            let mutable hashCode = HashCode()
            hashCode.Add(this.Class)
            hashCode.Add(this.RelativePath)
            hashCode.Add(this.Sha256Hash)
            hashCode.Add(this.Blake3Hash)
            hashCode.Add(this.IsBinary)
            hashCode.Add(this.Size)
            hashCode.Add(this.CreatedAt)
            hashCode.Add(this.BlobUri)
            hashCode.Add(this.ContentReference)
            hashCode.ToHashCode()

    /// A LocalFileVersion represents a version of a file in a repository with unique contents, and therefore with
    /// unique SHA-256 and BLAKE3 hashes. It is immutable.
    ///
    /// It is the local representation of the FileVersion type, used on the server.
    and [<CLIMutable; MessagePackObject>] LocalFileVersion =
        {
            [<Key(0)>]
            Class: string
            //[<Key(1)>]
            //RepositoryId: RepositoryId
            [<Key(1)>]
            RelativePath: RelativePath
            [<Key(2)>]
            Sha256Hash: Sha256Hash
            [<Key(8)>]
            Blake3Hash: Blake3Hash
            [<Key(3)>]
            IsBinary: bool
            [<Key(4)>]
            Size: int64
            [<Key(5)>]
            CreatedAt: Instant
            [<Key(6)>]
            UploadedToObjectStorage: bool
            [<Key(7)>]
            LastWriteTimeUtc: DateTime
        }

        /// Builds a content-addressed value carrying both BLAKE3 and SHA-256 hashes for compatibility checks.
        static member CreateWithHashes
            //(repositoryId: RepositoryId)
            (relativePath: RelativePath)
            (sha256Hash: Sha256Hash)
            (blake3Hash: Blake3Hash)
            (isBinary: bool)
            (size: int64)
            (createdAt: Instant)
            (uploadedToObjectStorage: bool)
            (lastWriteTimeUtc: DateTime)
            =
            {
                Class = "LocalFileVersion"
                //RepositoryId = repositoryId
                RelativePath = RelativePath(normalizeFilePath $"{relativePath}")
                Sha256Hash = sha256Hash
                Blake3Hash = blake3Hash
                IsBinary = isBinary
                Size = size
                CreatedAt = createdAt
                UploadedToObjectStorage = uploadedToObjectStorage
                LastWriteTimeUtc = lastWriteTimeUtc
            }

        /// Builds the contract value from required caller inputs and generated defaults used by this surface.
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
            LocalFileVersion.CreateWithHashes relativePath sha256Hash (Blake3Hash String.Empty) isBinary size createdAt uploadedToObjectStorage lastWriteTimeUtc

        /// Converts a LocalFileVersion to a FileVersion. NOTE: at this point, we don't know the BlobUri.
        [<IgnoreMember>]
        member this.ToFileVersion = FileVersion.CreateWithHashes this.RelativePath this.Sha256Hash this.Blake3Hash String.Empty this.IsBinary this.Size

        /// Get the object directory file name, which includes the SHA256 Hash value. Example: hello.js -> hello_04bef0a4b298de9c02930234.js
        [<IgnoreMember>]
        member this.GetObjectFileName = getObjectFileName this.RelativePath this.Sha256Hash

        /// Gets the relative directory path of the file. Example: "/dir/subdir/file.js" -> "/dir/subdir/".
        [<IgnoreMember>]
        member this.RelativeDirectory = getRelativeDirectory $"{this.RelativePath}" ""

    /// A DirectoryVersion represents a version of a directory in a repository with unique contents, and therefore with
    /// unique SHA-256 and BLAKE3 hashes.
    ///
    /// It is the server-side representation of the LocalDirectoryVersion type. LocalDirectoryVersion is used for the local object cache.
    [<MessagePackObject; GenerateSerializer>]
    type DirectoryVersion() =
        [<Key(0)>]
        member val Class: string = nameof DirectoryVersion with get, set

        [<Key(1)>]
        member val DirectoryVersionId: DirectoryVersionId = DirectoryVersionId.Empty with get, set

        [<Key(2)>]
        member val OwnerId: OwnerId = OwnerId.Empty with get, set

        [<Key(3)>]
        member val OrganizationId: OrganizationId = OrganizationId.Empty with get, set

        [<Key(4)>]
        member val RepositoryId: RepositoryId = RepositoryId.Empty with get, set

        [<Key(5)>]
        member val RelativePath: RelativePath = RelativePath String.Empty with get, set

        [<Key(6)>]
        member val Sha256Hash: Sha256Hash = Sha256Hash String.Empty with get, set

        [<Key(12)>]
        member val Blake3Hash: Blake3Hash = Blake3Hash String.Empty with get, set

        [<Key(7)>]
        member val Directories: List<DirectoryVersionId> = List<DirectoryVersionId>() with get, set

        [<Key(8)>]
        member val Files: List<FileVersion> = List<FileVersion>() with get, set

        [<Key(9)>]
        member val Size: int64 = InitialDirectorySize with get, set

        [<Key(10)>]
        member val CreatedAt: Instant = DefaultTimestamp with get, set

        [<Key(11)>]
        member val HashesValidated: bool = false with get, set

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<DirectoryVersion>()

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default = DirectoryVersion()

        /// Builds a content-addressed value carrying both BLAKE3 and SHA-256 hashes for compatibility checks.
        static member CreateWithHashes
            (directoryVersionId: DirectoryVersionId)
            (ownerId: OwnerId)
            (organizationId: OrganizationId)
            (repositoryId: RepositoryId)
            (relativePath: RelativePath)
            (sha256Hash: Sha256Hash)
            (blake3Hash: Blake3Hash)
            (directories: List<DirectoryVersionId>)
            (files: List<FileVersion>)
            (size: int64)
            =
            let directoryVersion = DirectoryVersion()
            directoryVersion.Class <- nameof DirectoryVersion
            directoryVersion.DirectoryVersionId <- directoryVersionId
            directoryVersion.OwnerId <- ownerId
            directoryVersion.OrganizationId <- organizationId
            directoryVersion.RepositoryId <- repositoryId
            directoryVersion.RelativePath <- relativePath
            directoryVersion.Sha256Hash <- sha256Hash
            directoryVersion.Blake3Hash <- blake3Hash
            directoryVersion.Directories <- directories
            directoryVersion.Files <- files
            directoryVersion.Size <- size
            directoryVersion.CreatedAt <- getCurrentInstant ()
            directoryVersion.HashesValidated <- false
            directoryVersion

        /// Builds the contract value from required caller inputs and generated defaults used by this surface.
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
            DirectoryVersion.CreateWithHashes
                directoryVersionId
                ownerId
                organizationId
                repositoryId
                relativePath
                sha256Hash
                (Blake3Hash String.Empty)
                directories
                files
                size

        /// Projects a directory-version DTO into the local index shape cached by Grace clients.
        member this.ToLocalDirectoryVersion lastWriteTimeUtc =
            LocalDirectoryVersion.CreateWithHashes
                this.DirectoryVersionId
                this.OwnerId
                this.OrganizationId
                this.RepositoryId
                this.RelativePath
                this.Sha256Hash
                this.Blake3Hash
                this.Directories
                (this
                    .Files
                    .Select(fun f -> f.ToLocalFileVersion lastWriteTimeUtc)
                    .ToList())
                this.Size
                lastWriteTimeUtc

        /// Compares the domain identity fields that define whether two values refer to the same Grace object.
        override this.Equals(other: obj) =
            match other with
            | :? DirectoryVersion as otherDirectoryVersion ->
                this.Class = otherDirectoryVersion.Class
                && this.DirectoryVersionId = otherDirectoryVersion.DirectoryVersionId
                && this.OwnerId = otherDirectoryVersion.OwnerId
                && this.OrganizationId = otherDirectoryVersion.OrganizationId
                && this.RepositoryId = otherDirectoryVersion.RepositoryId
                && this.RelativePath = otherDirectoryVersion.RelativePath
                && this.Sha256Hash = otherDirectoryVersion.Sha256Hash
                && this.Blake3Hash = otherDirectoryVersion.Blake3Hash
                && this.Directories.SequenceEqual(otherDirectoryVersion.Directories)
                && this.Files.SequenceEqual(otherDirectoryVersion.Files)
                && this.Size = otherDirectoryVersion.Size
                && this.CreatedAt = otherDirectoryVersion.CreatedAt
                && this.HashesValidated = otherDirectoryVersion.HashesValidated
            | _ -> false

        /// Computes a hash code from the same domain identity fields used by equality.
        override this.GetHashCode() =
            let mutable hashCode = HashCode()
            hashCode.Add(this.Class)
            hashCode.Add(this.DirectoryVersionId)
            hashCode.Add(this.OwnerId)
            hashCode.Add(this.OrganizationId)
            hashCode.Add(this.RepositoryId)
            hashCode.Add(this.RelativePath)
            hashCode.Add(this.Sha256Hash)
            hashCode.Add(this.Blake3Hash)

            for directoryId in this.Directories do
                hashCode.Add(directoryId)

            for file in this.Files do
                hashCode.Add(file)

            hashCode.Add(this.Size)
            hashCode.Add(this.CreatedAt)
            hashCode.Add(this.HashesValidated)
            hashCode.ToHashCode()

    /// A LocalDirectoryVersion represents a version of a directory in a repository with unique contents, and therefore
    /// with unique SHA-256 and BLAKE3 hashes.
    ///
    /// It is the local representation of the DirectoryVersion type. DirectoryVersion is used on the server.
    and [<MessagePackObject>] LocalDirectoryVersion() =
        [<Key(0)>]
        member val Class: string = "LocalDirectoryVersion" with get, set

        [<Key(1)>]
        member val DirectoryVersionId: DirectoryVersionId = DirectoryVersionId.Empty with get, set

        [<Key(2)>]
        member val OwnerId: OwnerId = OwnerId.Empty with get, set

        [<Key(3)>]
        member val OrganizationId: OrganizationId = OrganizationId.Empty with get, set

        [<Key(4)>]
        member val RepositoryId: RepositoryId = RepositoryId.Empty with get, set

        [<Key(5)>]
        member val RelativePath: RelativePath = RelativePath String.Empty with get, set

        [<Key(6)>]
        member val Sha256Hash: Sha256Hash = Sha256Hash String.Empty with get, set

        [<Key(12)>]
        member val Blake3Hash: Blake3Hash = Blake3Hash String.Empty with get, set

        [<Key(7)>]
        member val Directories: List<DirectoryVersionId> = List<DirectoryVersionId>() with get, set

        [<Key(8)>]
        member val Files: List<LocalFileVersion> = List<LocalFileVersion>() with get, set

        [<Key(9)>]
        member val Size: int64 = InitialDirectorySize with get, set

        [<Key(10)>]
        member val CreatedAt: Instant = DefaultTimestamp with get, set

        [<Key(11)>]
        member val LastWriteTimeUtc: DateTime = DateTime.UtcNow with get, set

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default = LocalDirectoryVersion()

        /// Builds a content-addressed value carrying both BLAKE3 and SHA-256 hashes for compatibility checks.
        static member CreateWithHashes
            (directoryVersionId: DirectoryVersionId)
            (ownerId: OwnerId)
            (organizationId: OrganizationId)
            (repositoryId: RepositoryId)
            (relativePath: RelativePath)
            (sha256Hash: Sha256Hash)
            (blake3Hash: Blake3Hash)
            (directories: List<DirectoryVersionId>)
            (files: List<LocalFileVersion>)
            (size: int64)
            (lastWriteTimeUtc: DateTime)
            =
            let directoryVersion = LocalDirectoryVersion()
            directoryVersion.Class <- "LocalDirectoryVersion"
            directoryVersion.DirectoryVersionId <- directoryVersionId
            directoryVersion.OwnerId <- ownerId
            directoryVersion.OrganizationId <- organizationId
            directoryVersion.RepositoryId <- repositoryId
            directoryVersion.RelativePath <- relativePath
            directoryVersion.Sha256Hash <- sha256Hash
            directoryVersion.Blake3Hash <- blake3Hash
            directoryVersion.Directories <- directories
            directoryVersion.Files <- files
            directoryVersion.Size <- size
            directoryVersion.CreatedAt <- getCurrentInstant ()
            directoryVersion.LastWriteTimeUtc <- lastWriteTimeUtc
            directoryVersion

        /// Builds the contract value from required caller inputs and generated defaults used by this surface.
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
            LocalDirectoryVersion.CreateWithHashes
                directoryVersionId
                ownerId
                organizationId
                repositoryId
                relativePath
                sha256Hash
                (Blake3Hash String.Empty)
                directories
                files
                size
                lastWriteTimeUtc

        /// Converts a LocalDirectoryVersion to a DirectoryVersion.
        [<IgnoreMember>]
        member this.ToDirectoryVersion =
            DirectoryVersion.CreateWithHashes
                this.DirectoryVersionId
                this.OwnerId
                this.OrganizationId
                this.RepositoryId
                this.RelativePath
                this.Sha256Hash
                this.Blake3Hash
                this.Directories
                (this
                    .Files
                    .Select(fun f -> f.ToFileVersion)
                    .ToList())
                this.Size

        /// Compares the domain identity fields that define whether two values refer to the same Grace object.
        override this.Equals(other: obj) =
            match other with
            | :? LocalDirectoryVersion as otherDirectoryVersion ->
                this.Class = otherDirectoryVersion.Class
                && this.DirectoryVersionId = otherDirectoryVersion.DirectoryVersionId
                && this.OwnerId = otherDirectoryVersion.OwnerId
                && this.OrganizationId = otherDirectoryVersion.OrganizationId
                && this.RepositoryId = otherDirectoryVersion.RepositoryId
                && this.RelativePath = otherDirectoryVersion.RelativePath
                && this.Sha256Hash = otherDirectoryVersion.Sha256Hash
                && this.Blake3Hash = otherDirectoryVersion.Blake3Hash
                && this.Directories.SequenceEqual(otherDirectoryVersion.Directories)
                && this.Files.SequenceEqual(otherDirectoryVersion.Files)
                && this.Size = otherDirectoryVersion.Size
                && this.CreatedAt = otherDirectoryVersion.CreatedAt
                && this.LastWriteTimeUtc = otherDirectoryVersion.LastWriteTimeUtc
            | _ -> false

        /// Computes a hash code from the same domain identity fields used by equality.
        override this.GetHashCode() =
            let mutable hashCode = HashCode()
            hashCode.Add(this.Class)
            hashCode.Add(this.DirectoryVersionId)
            hashCode.Add(this.OwnerId)
            hashCode.Add(this.OrganizationId)
            hashCode.Add(this.RepositoryId)
            hashCode.Add(this.RelativePath)
            hashCode.Add(this.Sha256Hash)
            hashCode.Add(this.Blake3Hash)

            for directoryId in this.Directories do
                hashCode.Add(directoryId)

            for file in this.Files do
                hashCode.Add(file)

            hashCode.Add(this.Size)
            hashCode.Add(this.CreatedAt)
            hashCode.Add(this.LastWriteTimeUtc)
            hashCode.ToHashCode()

    /// Specifies whether a specific entry in a directory is a DirectoryVersion or a FileVersion.
    and [<KnownType("GetKnownTypes"); GenerateSerializer>] DirectoryEntry =
        | Directory of DirectoryVersion
        | File of FileVersion

        /// Returns known nested union types for serializers.
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

        /// Returns known nested union types for serializers.
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

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<RepositoryType>()

    /// Specifies the current operational status of a repository.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type RepositoryStatus =
        | Active
        | Suspended
        | Closed
        | Deleted

        /// Returns known nested union types for serializers.
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

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<DirectoryPermission>()

    /// Defines the kinds of links that can exist between references.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ReferenceLinkType =
        | BasedOn of ReferenceId
        | IncludedInPromotionSet of PromotionSetId
        | PromotionSetTerminal of PromotionSetId // Marks the final promotion in a promotion set.

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ReferenceLinkType>()

    /// Defines the permissions granted to a group defined by a claim.
    ///
    /// This is combined with a RelativePath to define a PathPermission.
    [<GenerateSerializer>]
    type ClaimPermission =
        {
            [<Id 0u>]
            Claim: string
            [<Id 1u>]
            DirectoryPermission: DirectoryPermission
        }

    /// Defines a set of claims and their permissions that should be applied to a relative path in the repository.
    ///
    /// NOTE: This type is being used only at the directory level for now, but I intend to implement it at the file level as well.
    [<GenerateSerializer>]
    type PathPermission =
        {
            [<Id 0u>]
            Path: RelativePath
            [<Id 1u>]
            Permissions: List<ClaimPermission>
        }

    /// Cleans up extra backslashes (escape characters) and converts \r\n to Environment.NewLine.
    let cleanJson (s: string) =
        s
            .Replace("\\\\\\\\", @"\")
            .Replace("\\\\", @"\")
            .Replace(@"\r\n", Environment.NewLine)

    /// A serializable view of a .NET Exception
    type ExceptionObject =
        {
            Message: string
            StackTrace: string
            InnerException: ExceptionObject option
        }

        /// Checks if the ExceptionObject is set to the default value.
        member this.IsDefault() = this = ExceptionObject.Default

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default = { Message = String.Empty; StackTrace = String.Empty; InnerException = None }

        /// Builds an ExceptionObject from a .NET Exception from the validated inputs used by this contract.
        static member Create(ex: Exception) =
            {
                Message = ex.Message
                StackTrace = if String.IsNullOrEmpty ex.StackTrace then String.Empty else ex.StackTrace
                InnerException =
                    if ex.InnerException <> null then
                        Some(ExceptionObject.Create(ex.InnerException))
                    else
                        None
            }

    /// The primary type used in Grace to represent successful results.
    type GraceReturnValue<'T> =
        {
            ReturnValue: 'T
            EventTime: Instant
            CorrelationId: string
            Properties: Dictionary<string, obj>
        }

        /// Builds an exception response that preserves caller-supplied diagnostic metadata.
        static member CreateWithMetadata<'T> (returnValue: 'T) (correlationId: string) (properties: Dictionary<string, obj>) =
            { ReturnValue = returnValue; EventTime = getCurrentInstant (); CorrelationId = correlationId; Properties = properties }

        /// Builds the contract value from required caller inputs and generated defaults used by this surface.
        static member Create<'T> (returnValue: 'T) (correlationId: string) =
            GraceReturnValue.CreateWithMetadata returnValue correlationId (Dictionary<string, obj>())

        /// Adds a key-value pair to GraceReturnValue's Properties dictionary.
        member this.enhance((key: string), (value: obj)) =
            //logToConsole $"In GraceReturnValue.enhance: Enhancing GraceReturnValue with key: {key}, value: {value}."

            match String.IsNullOrEmpty(key), isNull (value) with
            | false, false -> this.Properties[ key ] <- value
            | false, true -> this.Properties[ key ] <- null
            | true, _ -> ()

            this

        /// Adds a set of key-value pairs from a Dictionary to GraceReturnValue's Properties dictionary.
        member this.enhance(dict: IReadOnlyDictionary<string, obj>) =
            // logToConsole $"In GraceReturnValue.enhance: isNull(dict): {isNull (dict)}."
            // logToConsole $"In GraceReturnValue.enhance: Enhancing GraceReturnValue with {dict.Count} properties."

            dict
            |> Seq.iter (fun kvp -> this.enhance (kvp.Key, kvp.Value) |> ignore)

            this

        /// Returns the display representation for this value.
        override this.ToString() =
            // Breaking out the Properties because Dictionary<> doesn't have a good ToString() method.
            let output =
                {|
                    ReturnValue = this.ReturnValue
                    EventTime = this.EventTime
                    CorrelationId = this.CorrelationId
                    Properties = this.Properties.Select(fun kvp -> {| Key = kvp.Key; Value = kvp.Value |})
                |}
            //logToConsole $"GraceReturnValue: {serialize output}"
            serialize output

    /// The primary type used in Grace to represent error results.
    type GraceError =
        {
            Exception: ExceptionObject
            Error: string
            EventTime: Instant
            CorrelationId: string
            Properties: Dictionary<string, obj>
        }

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default =
            {
                Exception = ExceptionObject.Default
                Error = "Empty error message"
                EventTime = getCurrentInstant ()
                CorrelationId = String.Empty
                Properties = new Dictionary<string, obj>()
            }

        /// Builds the contract value from required caller inputs and generated defaults used by this surface.
        static member Create (error: string) (correlationId: string) =
            {
                Exception = ExceptionObject.Default
                Error = error
                EventTime = getCurrentInstant ()
                CorrelationId = correlationId
                Properties = new Dictionary<string, obj>()
            }

        /// Builds a response that preserves exception details alongside the operation metadata.
        static member CreateWithException (ex: Exception) (error: string) (correlationId: string) =
            {
                Exception = ExceptionObject.Create(ex)
                Error = error
                EventTime = getCurrentInstant ()
                CorrelationId = correlationId
                Properties = new Dictionary<string, obj>()
            }

        /// Builds an exception response that preserves caller-supplied diagnostic metadata.
        static member CreateWithMetadata (ex: Exception) (error: string) (correlationId: string) (properties: Dictionary<string, obj>) =
            { Exception = ExceptionObject.Create(ex); Error = error; EventTime = getCurrentInstant (); CorrelationId = correlationId; Properties = properties }

        /// Adds a key-value pair to GraceError's Properties dictionary.
        member this.enhance(key: string, value: obj) =
            match String.IsNullOrEmpty(key), isNull (value) with
            | false, false -> this.Properties[ key ] <- $"{value}"
            | false, true -> this.Properties[ key ] <- null
            | true, _ -> ()

            this

        /// Adds a set of key-value pairs from a Dictionary to GraceError's Properties dictionary.
        member this.enhance(dict: IReadOnlyDictionary<string, obj>) =
            dict
            |> Seq.iter (fun kvp -> this.Properties[ kvp.Key ] <- $"{kvp.Value}")

            this

        /// Returns the display representation for this value.
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

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<FileSystemEntryType>()

        /// Returns the display representation for this value.
        override this.ToString() = getDiscriminatedUnionFullName this

    /// Specifies whether a change detected in a diff is an add, change, or delete.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type DifferenceType =
        | Add
        | Change
        | Delete

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<DifferenceType>()

        /// Returns the display representation for this value.
        override this.ToString() = getDiscriminatedUnionFullName this

    /// A file system difference is a change detected (at a file level) in a diff. It specifies the type of change (add, change, or delete), the type of file system entry (directory or file), and the relative path of the entry.
    [<GenerateSerializer>]
    type FileSystemDifference =
        {
            DifferenceType: DifferenceType
            FileSystemEntryType: FileSystemEntryType
            RelativePath: RelativePath
        }

        /// Builds the contract value from required caller inputs and generated defaults used by this surface.
        static member Create differenceType fileSystemEntryType relativePath =
            { DifferenceType = differenceType; FileSystemEntryType = fileSystemEntryType; RelativePath = relativePath }

    /// Represents the upload metadata contract.
    [<GenerateSerializer>]
    type UploadMetadata = { RelativePath: RelativePath; BlobUriWithSasToken: Uri; Sha256Hash: Sha256Hash }

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
        {
            [<Key(0)>]
            Index: GraceIndex
            [<Key(1)>]
            RootDirectoryId: DirectoryVersionId
            [<Key(2)>]
            RootDirectorySha256Hash: Sha256Hash
            [<Key(3)>]
            LastSuccessfulFileUpload: Instant
            [<Key(4)>]
            LastSuccessfulDirectoryVersionUpload: Instant
            [<Key(5)>]
            RootDirectoryBlake3Hash: Blake3Hash
        }

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default =
            {
                Index = GraceIndex()
                RootDirectoryId = DirectoryVersionId.Empty
                RootDirectorySha256Hash = Sha256Hash String.Empty
                LastSuccessfulFileUpload = getCurrentInstant ()
                LastSuccessfulDirectoryVersionUpload = getCurrentInstant ()
                RootDirectoryBlake3Hash = Blake3Hash String.Empty
            }

    /// GraceObjectCache is a snapshot of the contents of the local object cache.
    [<MessagePackObject>]
    type GraceObjectCache =
        {
            [<Key(0)>]
            Index: GraceIndex
        }

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default = { Index = GraceIndex() }

    /// Holds the results of a diff between two versions of a file.
    [<GenerateSerializer>]
    type FileDiff =
        {
            RelativePath: RelativePath
            FileSha1: Sha256Hash
            CreatedAt1: Instant
            FileSha2: Sha256Hash
            CreatedAt2: Instant
            IsBinary: bool
            InlineDiff: List<DiffPiece []>
            SideBySideOld: List<DiffPiece []>
            SideBySideNew: List<DiffPiece []>
        }

        /// Builds the contract value from required caller inputs and generated defaults used by this surface.
        static member Create
            (relativePath: RelativePath)
            (fileSha1: Sha256Hash)
            (createdAt1: Instant)
            (fileSha2: Sha256Hash)
            (createdAt2: Instant)
            (isBinary: bool)
            (inlineDiff: List<DiffPiece []>)
            (sideBySideOld: List<DiffPiece []>)
            (sideBySideNew: List<DiffPiece []>)
            =
            {
                RelativePath = relativePath
                FileSha1 = fileSha1
                CreatedAt1 = createdAt1
                FileSha2 = fileSha2
                CreatedAt2 = createdAt2
                IsBinary = isBinary
                InlineDiff = inlineDiff
                SideBySideOld = sideBySideOld
                SideBySideNew = sideBySideNew
            }

    /// Represents promotion type.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type PromotionType =
        | SingleStep
        | Complex

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<PromotionType>()

        /// Returns the display representation for this value.
        override this.ToString() = getDiscriminatedUnionFullName this

    /// Defines how promotions are handled for a branch.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type BranchPromotionMode =
        | IndividualOnly // Default behavior: promotions always applied individually.
        | GroupOnly // Promotions to this branch must go through a promotion group.
        | Hybrid // Promotions can be grouped by default, with an opt-out flag.

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<BranchPromotionMode>()

        /// Returns the display representation for this value.
        override this.ToString() = getDiscriminatedUnionFullName this

    /// Defines the conflict resolution policy for a repository.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ConflictResolutionPolicy =
        | NoConflicts of unit // Any conflict blocks recomputation/apply.
        | ConflictsAllowed of float32 // Conflicts allowed if model confidence >= threshold (0.0 to 1.0).

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ConflictResolutionPolicy>()

        /// Returns the display representation for this value.
        override this.ToString() = getDiscriminatedUnionFullName this

    /// Holds the entity Id's involved in an API call. It's populated in ValidateIds.Middleware.fs.
    type GraceIds =
        {
            OwnerId: OwnerId
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
            HasBranch: bool
        }

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default =
            {
                OwnerId = OwnerId.Empty
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
                HasBranch = false
            }

        /// Returns the display representation for this value.
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

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ReminderTypes>()

        /// Returns the display representation for this value.
        override this.ToString() = getDiscriminatedUnionFullName this

    /// Defines the different statuses of a reminder.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ReminderStatus =
        /// The reminder is pending and has not yet been dispatched.
        | Pending
        /// The reminder has been dispatched to the target actor.
        | Dispatched
        /// The reminder failed to execute.
        | Failed
        /// The reminder has been cancelled.
        | Cancelled

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ReminderStatus>()

        /// Returns the display representation for this value.
        override this.ToString() = getDiscriminatedUnionFullName this

    /// Defines the different statuses of a .zip file.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ZipFileStatus =
        | NotCreated
        | Creating
        | Exists

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ReminderStatus>()

    /// Represents the appearance contract.
    [<GenerateSerializer>]
    type Appearance = { Root: DirectoryVersionId; Parent: DirectoryVersionId; Created: Instant }

    /// A list of known Grace Server API version strings.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ServerApiVersions =
        | ``V2022-02-01``
        | Latest
        | Edge

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ServerApiVersions>()

        /// Returns the display representation for this value.
        override this.ToString() = getDiscriminatedUnionFullName this

    /// Supported outbound pub-sub providers for Grace.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type GracePubSubSystem =
        | UnknownPubSubProvider
        | AzureEventHubs
        | AzureServiceBus
        | AwsSqs
        | GoogleCloudPubSub

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<GracePubSubSystem>()

        /// Returns the display representation for this value.
        override this.ToString() = getDiscriminatedUnionFullName this

    /// Settings for Azure Service Bus pub-sub integration.
    [<GenerateSerializer>]
    type AzureServiceBusPubSubSettings =
        {
            ConnectionString: string
            FullyQualifiedNamespace: string
            TopicName: string
            SubscriptionName: string
            UseManagedIdentity: bool
        }

    /// Settings for Grace pub-sub integration.
    [<GenerateSerializer>]
    type GracePubSubSettings =
        {
            System: GracePubSubSystem
            AzureServiceBus: AzureServiceBusPubSubSettings option
        }

        /// Represents the normalized empty instance used before persisted state or caller input contributes values.
        static member Empty: GracePubSubSettings = { System = GracePubSubSystem.UnknownPubSubProvider; AzureServiceBus = None }
