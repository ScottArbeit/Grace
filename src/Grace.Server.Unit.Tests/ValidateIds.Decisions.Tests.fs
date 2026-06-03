namespace Grace.Server.Tests

open Grace.Server.Middleware
open Microsoft.AspNetCore.Http
open NUnit.Framework
open System
open System.Threading.Tasks

type BodyWithAllEntityProperties() =
    member val OwnerId = "" with get, set
    member val OwnerName = "" with get, set
    member val OrganizationId = "" with get, set
    member val OrganizationName = "" with get, set
    member val RepositoryId = "" with get, set
    member val RepositoryName = "" with get, set
    member val BranchId = "" with get, set
    member val BranchName = "" with get, set

type BodyWithMissingEntityProperties() =
    member val OwnerId = "" with get, set
    member val RepositoryName = "" with get, set

[<Parallelizable(ParallelScope.All)>]
type ValidateIdsDecisionsTests() =

    let endpointWithMetadata (metadata: obj array) =
        Endpoint(RequestDelegate(fun _ -> Task.CompletedTask), EndpointMetadataCollection(metadata), "test endpoint")

    [<Test>]
    member _.IgnoredPathsReturnNoBodyType() =
        let endpoint = endpointWithMetadata [| typeof<BodyWithAllEntityProperties> |]

        for path in
            [
                "/healthz"
                "/notifications"
                "/approval/request/_seedGenerated"
            ] do
            let result = ValidateIdsDecisions.tryGetBodyType path endpoint

            Assert.That(result, Is.EqualTo(None), $"Expected {path} to be ignored.")

    [<Test>]
    member _.IgnoredPathMatchingIsCaseInsensitiveAndPrefixAware() =
        let endpoint = endpointWithMetadata [| typeof<BodyWithAllEntityProperties> |]

        for path in
            [
                "/HeAlThZ/details"
                "/NOTIFICATIONS/stream"
                "/Approval/Request/_SeedGenerated/test"
            ] do
            let result = ValidateIdsDecisions.tryGetBodyType path endpoint

            Assert.That(result, Is.EqualTo(None), $"Expected {path} to be ignored.")

    [<Test>]
    member _.EndpointWithNoMetadataReturnsNoBodyType() =
        let endpoint = endpointWithMetadata [||]
        let result = ValidateIdsDecisions.tryGetBodyType "/owner/create" endpoint

        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.EndpointWithNonTypeMetadataReturnsNoBodyType() =
        let endpoint =
            endpointWithMetadata [| "metadata"
                                    42
                                    DateTimeOffset.UnixEpoch |]

        let result = ValidateIdsDecisions.tryGetBodyType "/owner/create" endpoint

        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.EndpointWithBodyTypeMetadataReturnsThatType() =
        let endpoint =
            endpointWithMetadata [| "metadata"
                                    typeof<BodyWithAllEntityProperties> |]

        let result = ValidateIdsDecisions.tryGetBodyType "/owner/create" endpoint

        Assert.That(result, Is.EqualTo(Some(typeof<BodyWithAllEntityProperties>)))

    [<Test>]
    member _.EntityPropertyDiscoveryFindsOwnerOrganizationRepositoryAndBranchProperties() =
        let properties = ValidateIdsDecisions.discoverEntityProperties typeof<BodyWithAllEntityProperties>

        Assert.That(properties.OwnerId.Value.Name, Is.EqualTo("OwnerId"))
        Assert.That(properties.OwnerName.Value.Name, Is.EqualTo("OwnerName"))
        Assert.That(properties.OrganizationId.Value.Name, Is.EqualTo("OrganizationId"))
        Assert.That(properties.OrganizationName.Value.Name, Is.EqualTo("OrganizationName"))
        Assert.That(properties.RepositoryId.Value.Name, Is.EqualTo("RepositoryId"))
        Assert.That(properties.RepositoryName.Value.Name, Is.EqualTo("RepositoryName"))
        Assert.That(properties.BranchId.Value.Name, Is.EqualTo("BranchId"))
        Assert.That(properties.BranchName.Value.Name, Is.EqualTo("BranchName"))

    [<Test>]
    member _.EntityPropertyDiscoveryLeavesMissingNameOrIdPropertiesAbsent() =
        let properties = ValidateIdsDecisions.discoverEntityProperties typeof<BodyWithMissingEntityProperties>

        Assert.That(properties.OwnerId.IsSome, Is.True)
        Assert.That(properties.OwnerName.IsNone, Is.True)
        Assert.That(properties.OrganizationId.IsNone, Is.True)
        Assert.That(properties.OrganizationName.IsNone, Is.True)
        Assert.That(properties.RepositoryId.IsNone, Is.True)
        Assert.That(properties.RepositoryName.IsSome, Is.True)
        Assert.That(properties.BranchId.IsNone, Is.True)
        Assert.That(properties.BranchName.IsNone, Is.True)
