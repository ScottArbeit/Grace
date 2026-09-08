namespace Grace.CLI.Command

open Grace.SDK
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Types
open Grace.Types.Common
open Grace.Types.Library
open Grace.Types.UploadSession
open System

/// Adapts the existing manifest uploader to the actual Library-prepared UploadSession.
module internal LibraryManifestUpload =

    /// Uses the server's typed session/scope/pool response while retaining the ordinary manifest upload pipeline.
    let uploadPrepared
        (configuration: GraceConfiguration)
        operationId
        (prepared: LibraryContentPreparationDto)
        (normalizedPath: string)
        stagedPath
        correlationId
        =
        let sessionId = prepared.UploadSessionId

        let client: ManifestUpload.ManifestUploadClient =
            { ManifestUpload.serverClient with
                StartSession =
                    fun parameters ->
                        task {
                            let session =
                                { UploadSessionDto.Default with
                                    UploadSessionId = sessionId
                                    OwnerId = configuration.OwnerId
                                    OrganizationId = configuration.OrganizationId
                                    RepositoryId = configuration.RepositoryId
                                    StoragePoolId = prepared.StoragePoolId
                                    AuthorizedScope = RelativePath prepared.AuthorizedScope
                                    FileContentHash = FileContentHash prepared.Blake3Hash
                                    ExpectedSize = prepared.Size
                                    ChunkingSuiteId = parameters.ChunkingSuiteId
                                }

                            return
                                Ok(
                                    GraceReturnValue.Create
                                        {
                                            Session = session
                                            OperationId = $"Library-prepare:{operationId:D}"
                                            Events = []
                                            WasIdempotentReplay = true
                                            Message = "The Library upload session is already prepared."
                                        }
                                        correlationId
                                )
                        }
                IssueDedupeDiscovery =
                    fun parameters ->
                        parameters.UploadSessionId <- sessionId
                        ManifestUpload.serverClient.IssueDedupeDiscovery parameters
                ClaimReuseRanges =
                    fun parameters ->
                        parameters.UploadSessionId <- sessionId
                        ManifestUpload.serverClient.ClaimReuseRanges parameters
                RegisterBlockUpload =
                    fun parameters ->
                        parameters.UploadSessionId <- sessionId
                        ManifestUpload.serverClient.RegisterBlockUpload parameters
                UploadContentBlock =
                    fun parameters bytes ->
                        parameters.UploadSessionId <- sessionId
                        ManifestUpload.serverClient.UploadContentBlock parameters bytes
                ConfirmBlockUploaded =
                    fun parameters ->
                        parameters.UploadSessionId <- sessionId
                        ManifestUpload.serverClient.ConfirmBlockUploaded parameters
                FinalizeManifest =
                    fun parameters ->
                        parameters.UploadSessionId <- sessionId
                        ManifestUpload.serverClient.FinalizeManifest parameters
            }

        let fileVersion =
            FileVersion.CreateWithHashes normalizedPath (Sha256Hash prepared.Sha256Hash) (Blake3Hash prepared.Blake3Hash) String.Empty true prepared.Size

        let request: ManifestUpload.ManifestUploadRequest =
            {
                OwnerId = configuration.OwnerId
                OwnerName = configuration.OwnerName
                OrganizationId = configuration.OrganizationId
                OrganizationName = configuration.OrganizationName
                RepositoryId = configuration.RepositoryId
                RepositoryName = configuration.RepositoryName
                AuthorizedScope = RelativePath prepared.AuthorizedScope
                FileVersion = fileVersion
                LocalFilePath = stagedPath
                CorrelationId = correlationId
                PlannerOptions =
                    { LocalPlanner.Options.Default with EligibilityPolicy = { LocalPlanner.Options.Default.EligibilityPolicy with ThresholdBytes = 1L } }
            }

        ManifestUpload.uploadFileWithClient client request
