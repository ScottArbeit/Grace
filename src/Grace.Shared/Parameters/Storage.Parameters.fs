namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open Grace.Types.ContentBlockMetadata
open Grace.Types.Common
open Grace.Types.UploadSession
open NodaTime
open System

module Storage =

    /// Maximum number of key chunks accepted by the ContentBlock discovery endpoint in a single request.
    [<Literal>]
    let MaxDiscoveryKeyChunkAddresses = 256

    /// Maximum candidate windows returned for each accepted key chunk.
    [<Literal>]
    let MaxCandidateWindowsPerKeyChunk = 4

    /// Maximum protected chunks returned in one candidate window.
    [<Literal>]
    let MaxWindowChunks = 256

    /// Maximum protected chunks returned across one discovery response.
    [<Literal>]
    let MaxResponseProtectedChunks = 16384

    /// Discovery responses expire quickly because the dedupe index is non-authoritative.
    [<Literal>]
    let ResponseTtlSeconds = 300

    /// Minimum contiguous run length accepted for reuse claims.
    [<Literal>]
    let MinimumAcceptedReuseRunLength = 8

    /// Maximum reuse range hints accepted by one claim request.
    [<Literal>]
    let MaxReuseRangeClaims = 1024

    /// Parameters used by multiple endpoints in the /diff path.
    type StorageParameters() =
        inherit CommonParameters()
        member val public OwnerId = String.Empty with get, set
        member val public OwnerName: OwnerName = String.Empty with get, set
        member val public OrganizationId = String.Empty with get, set
        member val public OrganizationName: OrganizationName = String.Empty with get, set
        member val public RepositoryId = String.Empty with get, set
        member val public RepositoryName: RepositoryName = String.Empty with get, set

    /// Parameters used by the /diff/populate endpoint.
    type DeleteAllParameters() =
        inherit StorageParameters()

    /// Parameters used by the /diff/get endpoint.
    type GetUploadUriParameters() =
        inherit StorageParameters()
        member val public FileVersions = Array.empty<FileVersion> with get, set

    type GetDownloadUriParameters() =
        inherit StorageParameters()
        member val public FileVersion = FileVersion.Default with get, set

    type GetContentBlockUploadUriParameters() =
        inherit StorageParameters()
        member val public ContentBlockAddress: ContentBlockAddress = String.Empty with get, set
        member val public AuthorizedScope: RelativePath = String.Empty with get, set

    type GetContentBlockDownloadUriParameters() =
        inherit StorageParameters()
        member val public ContentBlockAddress: ContentBlockAddress = String.Empty with get, set

    /// Parameters for /storage/discoverContentBlocks.
    ///
    /// Discovery is intentionally bounded and non-authoritative in this phase. The server accepts at most
    /// MaxDiscoveryKeyChunkAddresses key chunks and must not answer per-chunk existence questions.
    type DiscoverContentBlocksParameters() =
        inherit StorageParameters()
        member val public KeyChunkAddresses = Array.empty<ChunkAddress> with get, set

    type UploadSessionStorageParameters() =
        inherit StorageParameters()
        member val public UploadSessionId: UploadSessionId = Guid.Empty with get, set
        member val public AuthorizedScope: RelativePath = String.Empty with get, set

    type StartManifestUploadSessionParameters() =
        inherit UploadSessionStorageParameters()
        member val public FileContentHash: FileContentHash = String.Empty with get, set
        member val public ExpectedSize: int64 = 0L with get, set
        member val public ChunkingSuiteId: ChunkingSuiteId = String.Empty with get, set
        member val public SamplingPolicySnapshot: string = String.Empty with get, set
        member val public OperationId: UploadSessionOperationId = String.Empty with get, set

    type IssueDedupeDiscoveryParameters() =
        inherit UploadSessionStorageParameters()
        member val public OperationId: UploadSessionOperationId = String.Empty with get, set
        member val public ExpiresAt: Instant = Instant.MinValue with get, set
        member val public MinimumReuseRunLength: int = 0 with get, set
        member val public Hints = Array.empty<ContentBlockReuseRangeHint> with get, set

    type ClaimReuseRangesParameters() =
        inherit UploadSessionStorageParameters()
        member val public OperationId: UploadSessionOperationId = String.Empty with get, set
        member val public DiscoveryOperationId: UploadSessionOperationId = String.Empty with get, set
        member val public Hints = Array.empty<ContentBlockReuseRangeHint> with get, set

    type RegisterContentBlockUploadParameters() =
        inherit UploadSessionStorageParameters()
        member val public OperationId: UploadSessionOperationId = String.Empty with get, set
        member val public ContentBlockAddress: ContentBlockAddress = String.Empty with get, set
        member val public LogicalOffset: int64 = 0L with get, set
        member val public LogicalLength: int64 = 0L with get, set
        member val public ExpectedPayloadLength: int64 = 0L with get, set

    type ConfirmContentBlockUploadParameters() =
        inherit UploadSessionStorageParameters()
        member val public OperationId: UploadSessionOperationId = String.Empty with get, set
        member val public ContentBlockAddress: ContentBlockAddress = String.Empty with get, set
        member val public Payload: byte array = Array.empty with get, set
        member val public StoragePlacement: ContentBlockStoragePlacement = Unchecked.defaultof<ContentBlockStoragePlacement> with get, set

    type FinalizeManifestUploadParameters() =
        inherit UploadSessionStorageParameters()
        member val public OperationId: UploadSessionOperationId = String.Empty with get, set
        member val public Manifest: FileManifest = FileManifest.Default with get, set
        member val public BlockPayloads: FinalizeManifestBlockPayload array = Array.empty with get, set

    /// Policy returned with ContentBlock discovery results so clients know the bounded, non-authoritative semantics.
    type ContentBlockDiscoveryPolicy =
        {
            MaxKeyChunkAddresses: int
            MaxCandidateWindowsPerKeyChunk: int
            MaxWindowChunks: int
            MaxResponseProtectedChunks: int
            ResponseTtlSeconds: int
            MinimumAcceptedReuseRunLength: int
            PositiveCandidatesEnabled: bool
            EmptyResponseMeansAbsent: bool
            IsAuthoritative: bool
        }

    /// A possible ContentBlock reuse candidate.
    ///
    /// The initial discovery implementation returns no positive candidates yet; this DTO is reserved for later
    /// index-backed discovery without changing the endpoint shape.
    type ContentBlockDiscoveryCandidate =
        {
            StoragePoolId: StoragePoolId
            ManifestAddress: ManifestAddress
            ContentBlockAddress: ContentBlockAddress
            OrdinalStart: int
            OrdinalCount: int
            MetadataVersion: MetadataVersion
            MatchingKeyChunkCount: int
            ProtectedChunkAddresses: string array
        }

    /// Result for /storage/discoverContentBlocks.
    ///
    /// Empty CandidateContentBlocks values are safe hints only. They are never proof that requested chunks or future
    /// ContentBlocks are absent.
    type DiscoverContentBlocksResult =
        {
            RequestedKeyChunkCount: int
            AcceptedKeyChunkCount: int
            Policy: ContentBlockDiscoveryPolicy
            CandidateContentBlocks: ContentBlockDiscoveryCandidate array
            IsPartial: bool
            Message: string
        }

    type UploadMetadata = { RelativePath: RelativePath; BlobUriWithSasToken: Uri; Sha256Hash: Sha256Hash; ContentReference: FileContentReference }

    type GetUploadMetadataForFilesParameters() =
        inherit StorageParameters()
        member val public FileVersions = Array.empty<FileVersion> with get, set
