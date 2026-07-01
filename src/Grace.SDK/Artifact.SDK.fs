namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Parameters.Artifact
open Grace.Types.Artifact
open System

/// Client API for artifact endpoints.
type Artifact() =

    /// URL-encodes artifact and repository identifiers into the artifact download-URI route.
    static member internal BuildDownloadUriRoute(parameters: GetArtifactDownloadUriParameters) =
        let artifactId = Uri.EscapeDataString(parameters.ArtifactId.Trim())
        let ownerId = Uri.EscapeDataString(parameters.OwnerId.Trim())
        let organizationId = Uri.EscapeDataString(parameters.OrganizationId.Trim())
        let repositoryId = Uri.EscapeDataString(parameters.RepositoryId.Trim())
        let correlationId = Uri.EscapeDataString(parameters.CorrelationId.Trim())

        $"artifact/{artifactId}/download-uri?ownerId={ownerId}&organizationId={organizationId}&repositoryId={repositoryId}&correlationId={correlationId}"

    /// Registers artifact metadata with the server and returns the upload URI for the artifact payload.
    static member public Create(parameters: CreateArtifactParameters) =
        postServer<CreateArtifactParameters, ArtifactCreateResult> (parameters |> ensureCorrelationIdIsSet, "artifact/create")

    /// Gets a read URI for an existing artifact.
    static member public GetDownloadUri(parameters: GetArtifactDownloadUriParameters) =
        let normalizedParameters = parameters |> ensureCorrelationIdIsSet
        let route = Artifact.BuildDownloadUriRoute normalizedParameters

        getServer<GetArtifactDownloadUriParameters, ArtifactDownloadUriResult> (normalizedParameters, route)
