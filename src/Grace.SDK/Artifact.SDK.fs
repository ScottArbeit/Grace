namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Parameters.Artifact
open Grace.Types.Artifact
open System

/// Client API for artifact endpoints.
type Artifact() =

    static member internal BuildDownloadUriRoute(parameters: GetArtifactDownloadUriParameters) =
        let artifactId = Uri.EscapeDataString(parameters.ArtifactId.Trim())
        let organizationId = Uri.EscapeDataString(parameters.OrganizationId.Trim())
        let repositoryId = Uri.EscapeDataString(parameters.RepositoryId.Trim())
        let correlationId = Uri.EscapeDataString(parameters.CorrelationId.Trim())

        $"artifact/{artifactId}/download-uri?organizationId={organizationId}&repositoryId={repositoryId}&correlationId={correlationId}"

    /// Creates artifact metadata and returns an upload URI.
    static member public Create(parameters: CreateArtifactParameters) =
        postServer<CreateArtifactParameters, ArtifactCreateResult> (parameters |> ensureCorrelationIdIsSet, "artifact/create")

    /// Gets a read URI for an existing artifact.
    static member public GetDownloadUri(parameters: GetArtifactDownloadUriParameters) =
        let normalizedParameters = parameters |> ensureCorrelationIdIsSet
        let route = Artifact.BuildDownloadUriRoute normalizedParameters

        getServer<GetArtifactDownloadUriParameters, ArtifactDownloadUriResult> (normalizedParameters, route)
