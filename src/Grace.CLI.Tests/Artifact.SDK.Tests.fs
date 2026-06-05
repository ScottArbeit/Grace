namespace Grace.CLI.Tests

open Grace.SDK
open Grace.Shared.Parameters.Artifact
open NUnit.Framework

[<Parallelizable(ParallelScope.All)>]
type ArtifactSdkTests() =

    [<Test>]
    member _.BuildDownloadUriRouteIncludesOwnerScope() =
        let parameters =
            GetArtifactDownloadUriParameters(
                ArtifactId = " artifact/with spaces ",
                OwnerId = " owner id ",
                OrganizationId = " organization id ",
                RepositoryId = " repository id ",
                CorrelationId = " correlation id "
            )

        let route = Artifact.BuildDownloadUriRoute parameters

        Assert.That(
            route,
            Is.EqualTo(
                "artifact/artifact%2Fwith%20spaces/download-uri?ownerId=owner%20id&organizationId=organization%20id&repositoryId=repository%20id&correlationId=correlation%20id"
            )
        )
