namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open System

module Artifact =

    /// Base parameters for artifact endpoints.
    type ArtifactParameters() =
        inherit CommonParameters()
        member val public OwnerId = String.Empty with get, set
        member val public OwnerName = String.Empty with get, set
        member val public OrganizationId = String.Empty with get, set
        member val public OrganizationName = String.Empty with get, set
        member val public RepositoryId = String.Empty with get, set
        member val public RepositoryName = String.Empty with get, set

    /// Parameters for POST /artifact/create.
    type CreateArtifactParameters() =
        inherit ArtifactParameters()
        member val public ArtifactId = String.Empty with get, set
        member val public ArtifactType = String.Empty with get, set
        member val public MimeType = String.Empty with get, set
        member val public Size = 0L with get, set
        member val public Sha256 = String.Empty with get, set

    /// Parameters for GET /artifact/{artifactId}/download-uri.
    type GetArtifactDownloadUriParameters() =
        inherit ArtifactParameters()
        member val public ArtifactId = String.Empty with get, set
