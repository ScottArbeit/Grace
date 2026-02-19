namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Parameters.Artifact
open Grace.Types.Artifact

/// Client API for artifact endpoints.
type Artifact() =

    /// Creates artifact metadata and returns an upload URI.
    static member public Create(parameters: CreateArtifactParameters) =
        postServer<CreateArtifactParameters, ArtifactCreateResult> (parameters |> ensureCorrelationIdIsSet, "artifact/create")

    /// Gets a read URI for an existing artifact.
    static member public GetDownloadUri(parameters: GetArtifactDownloadUriParameters) =
        let normalizedParameters = parameters |> ensureCorrelationIdIsSet

        let route =
            $"artifact/{normalizedParameters.ArtifactId}/download-uri?ownerId={normalizedParameters.OwnerId}&organizationId={normalizedParameters.OrganizationId}&repositoryId={normalizedParameters.RepositoryId}&correlationId={normalizedParameters.CorrelationId}"

        getServer<GetArtifactDownloadUriParameters, ArtifactDownloadUriResult> (normalizedParameters, route)
