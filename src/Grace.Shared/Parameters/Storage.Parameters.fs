namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open Grace.Types.Types
open System

module Storage =

    /// Maximum number of key chunks accepted by the ContentBlock discovery endpoint in a single request.
    [<Literal>]
    let MaxDiscoveryKeyChunkAddresses = 256

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

    /// Policy returned with ContentBlock discovery results so clients know the bounded, non-authoritative semantics.
    type ContentBlockDiscoveryPolicy = { MaxKeyChunkAddresses: int; PositiveCandidatesEnabled: bool; EmptyResponseMeansAbsent: bool; IsAuthoritative: bool }

    /// A possible ContentBlock reuse candidate.
    ///
    /// The initial discovery implementation returns no positive candidates yet; this DTO is reserved for later
    /// index-backed discovery without changing the endpoint shape.
    type ContentBlockDiscoveryCandidate = { ContentBlockAddress: ContentBlockAddress; MatchingKeyChunkCount: int }

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
