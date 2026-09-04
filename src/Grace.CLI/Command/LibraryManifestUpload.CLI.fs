namespace Grace.CLI.Command

open Grace.SDK
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Types.Common
open Grace.Types.Library
open Grace.Types
open Grace.Types.UploadSession
open System
open System.Text.Json

/// Adapts the existing manifest uploader to a server-prepared Library content identity.
module internal LibraryManifestUpload =

    /// Uploads one stable staged file through the prepared session without creating another content identity.
    let uploadPrepared
        (configuration: GraceConfiguration)
        (operationId: Guid)
        (prepared: LibraryPreparedContentDto)
        (normalizedPath: string)
        (stagedPath: string)
        (correlationId: string)
        =
        let preparedSessionId = prepared.PreparedContentId

        let storagePoolId =
            let instructions =
                prepared.UploadInstructions
                |> Option.defaultWith (fun () -> invalidOp "Prepared Library content omitted its upload instructions.")

            use document = JsonDocument.Parse(instructions)

            document
                .RootElement
                .GetProperty(nameof StoragePoolId)
                .GetString()
            |> Option.ofObj
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
            |> Option.defaultWith (fun () -> invalidOp "Prepared Library upload instructions omitted StoragePoolId.")

        let client: ManifestUpload.ManifestUploadClient =
            { ManifestUpload.serverClient with
                StartSession =
                    fun parameters ->
                        task {
                            let session =
                                { UploadSessionDto.Default with
                                    UploadSessionId = preparedSessionId
                                    OwnerId = configuration.OwnerId
                                    OrganizationId = configuration.OrganizationId
                                    RepositoryId = configuration.RepositoryId
                                    StoragePoolId = StoragePoolId storagePoolId
                                    AuthorizedScope = RelativePath $"Library/{preparedSessionId:D}"
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
                                            Message = "Prepared Library upload session is already started."
                                        }
                                        correlationId
                                )
                        }
                IssueDedupeDiscovery =
                    fun parameters ->
                        parameters.UploadSessionId <- preparedSessionId
                        ManifestUpload.serverClient.IssueDedupeDiscovery parameters
                ClaimReuseRanges =
                    fun parameters ->
                        parameters.UploadSessionId <- preparedSessionId
                        ManifestUpload.serverClient.ClaimReuseRanges parameters
                RegisterBlockUpload =
                    fun parameters ->
                        parameters.UploadSessionId <- preparedSessionId
                        ManifestUpload.serverClient.RegisterBlockUpload parameters
                UploadContentBlock =
                    fun parameters bytes ->
                        parameters.UploadSessionId <- preparedSessionId
                        ManifestUpload.serverClient.UploadContentBlock parameters bytes
                ConfirmBlockUploaded =
                    fun parameters ->
                        parameters.UploadSessionId <- preparedSessionId
                        ManifestUpload.serverClient.ConfirmBlockUploaded parameters
                FinalizeManifest =
                    fun parameters ->
                        parameters.UploadSessionId <- preparedSessionId
                        ManifestUpload.serverClient.FinalizeManifest parameters
            }

        let fileVersion =
            FileVersion.CreateWithHashes
                (RelativePath normalizedPath)
                (Sha256Hash prepared.Sha256Hash)
                (Blake3Hash prepared.Blake3Hash)
                String.Empty
                false
                prepared.Size

        let request: ManifestUpload.ManifestUploadRequest =
            {
                OwnerId = configuration.OwnerId
                OwnerName = configuration.OwnerName
                OrganizationId = configuration.OrganizationId
                OrganizationName = configuration.OrganizationName
                RepositoryId = configuration.RepositoryId
                RepositoryName = configuration.RepositoryName
                AuthorizedScope = RelativePath $"Library/{preparedSessionId:D}"
                FileVersion = fileVersion
                LocalFilePath = stagedPath
                CorrelationId = correlationId
                PlannerOptions =
                    { LocalPlanner.Options.Default with EligibilityPolicy = { LocalPlanner.Options.Default.EligibilityPolicy with ThresholdBytes = 1L } }
            }

        ManifestUpload.uploadFileWithClient client request
