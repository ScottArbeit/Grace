namespace Grace.Server.Tests

open Grace.Server.Middleware
open Grace.Shared.Validation.Errors
open Grace.Types.Common
open Microsoft.AspNetCore.Http
open NUnit.Framework
open System
open System.Threading.Tasks

/// Covers body With All Entity Properties behavior in no-Aspire server unit tests.
type BodyWithAllEntityProperties() =
    /// Stores the test owner Id value used by validation scenarios.
    member val OwnerId = "" with get, set
    /// Stores the test owner Name value used by validation scenarios.
    member val OwnerName = "" with get, set
    /// Stores the test organization Id value used by validation scenarios.
    member val OrganizationId = "" with get, set
    /// Stores the test organization Name value used by validation scenarios.
    member val OrganizationName = "" with get, set
    /// Stores the test repository Id value used by validation scenarios.
    member val RepositoryId = "" with get, set
    /// Stores the test repository Name value used by validation scenarios.
    member val RepositoryName = "" with get, set
    /// Stores the test branch Id value used by validation scenarios.
    member val BranchId = "" with get, set
    /// Stores the test branch Name value used by validation scenarios.
    member val BranchName = "" with get, set

/// Covers body With Missing Entity Properties behavior in no-Aspire server unit tests.
type BodyWithMissingEntityProperties() =
    /// Stores the test owner Id value used by validation scenarios.
    member val OwnerId = "" with get, set
    /// Stores the test repository Name value used by validation scenarios.
    member val RepositoryName = "" with get, set

/// Covers validate Ids Decisions behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type ValidateIdsDecisionsTests() =

    /// Builds endpoint With Metadata test data for the server unit validate Ids Decisions scenarios in this file.
    let endpointWithMetadata (metadata: obj array) =
        Endpoint(RequestDelegate(fun _ -> Task.CompletedTask), EndpointMetadataCollection(metadata), "test endpoint")

    /// Verifies that ignored Paths Return No Body Type.
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

    /// Verifies that ignored Path Matching Is Case Insensitive And Prefix Aware.
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

    /// Verifies that endpoint With No Metadata Returns No Body Type.
    [<Test>]
    member _.EndpointWithNoMetadataReturnsNoBodyType() =
        let endpoint = endpointWithMetadata [||]
        let result = ValidateIdsDecisions.tryGetBodyType "/owner/create" endpoint

        Assert.That(result, Is.EqualTo(None))

    /// Verifies that endpoint With Non Type Metadata Returns No Body Type.
    [<Test>]
    member _.EndpointWithNonTypeMetadataReturnsNoBodyType() =
        let endpoint =
            endpointWithMetadata [| "metadata"
                                    42
                                    DateTimeOffset.UnixEpoch |]

        let result = ValidateIdsDecisions.tryGetBodyType "/owner/create" endpoint

        Assert.That(result, Is.EqualTo(None))

    /// Verifies that endpoint With Body Type Metadata Returns That Type.
    [<Test>]
    member _.EndpointWithBodyTypeMetadataReturnsThatType() =
        let endpoint =
            endpointWithMetadata [| "metadata"
                                    typeof<BodyWithAllEntityProperties> |]

        let result = ValidateIdsDecisions.tryGetBodyType "/owner/create" endpoint

        Assert.That(result, Is.EqualTo(Some(typeof<BodyWithAllEntityProperties>)))

    /// Verifies that entity Property Discovery Finds Owner Organization Repository And Branch Properties.
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

    /// Verifies that entity Property Discovery Leaves Missing Name Or Id Properties Absent.
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

    /// Verifies that create Paths Choose Create Validation Mode.
    [<TestCase("/owner/create", ValidateIdsDecisions.EntityKind.Owner)>]
    [<TestCase("/organization/create", ValidateIdsDecisions.EntityKind.Organization)>]
    [<TestCase("/repository/create", ValidateIdsDecisions.EntityKind.Repository)>]
    [<TestCase("/branch/create", ValidateIdsDecisions.EntityKind.Branch)>]
    member _.CreatePathsChooseCreateValidationMode(path: string, entityKind: ValidateIdsDecisions.EntityKind) =
        let result = ValidateIdsDecisions.validationModeForPath entityKind path

        Assert.That(result, Is.EqualTo(ValidateIdsDecisions.EntityValidationMode.Create))

    /// Verifies that non Create Paths Choose Existing Validation Mode.
    [<TestCase("/Owner/Create", ValidateIdsDecisions.EntityKind.Owner)>]
    [<TestCase("/organization/list", ValidateIdsDecisions.EntityKind.Organization)>]
    [<TestCase("/repository/create/details", ValidateIdsDecisions.EntityKind.Repository)>]
    [<TestCase("/branch/delete", ValidateIdsDecisions.EntityKind.Branch)>]
    member _.NonCreatePathsChooseExistingValidationMode(path: string, entityKind: ValidateIdsDecisions.EntityKind) =
        let result = ValidateIdsDecisions.validationModeForPath entityKind path

        if path.Equals("/Owner/Create", StringComparison.InvariantCultureIgnoreCase) then
            Assert.That(result, Is.EqualTo(ValidateIdsDecisions.EntityValidationMode.Create))
        else
            Assert.That(result, Is.EqualTo(ValidateIdsDecisions.EntityValidationMode.Existing))

    /// Verifies that entity Validation Stops When Earlier Scope Already Failed.
    [<Test>]
    member _.EntityValidationStopsWhenEarlierScopeAlreadyFailed() =
        let currentError = Some(GraceError.Create "Earlier failure" "correlation-id")
        let properties = ValidateIdsDecisions.discoverEntityProperties typeof<BodyWithAllEntityProperties>

        let result = ValidateIdsDecisions.shouldValidateEntity currentError properties.OrganizationId properties.OrganizationName

        Assert.That(result, Is.False)

    /// Verifies that entity Validation Requires Id And Name Properties To Exist.
    [<Test>]
    member _.EntityValidationRequiresIdAndNamePropertiesToExist() =
        let properties = ValidateIdsDecisions.discoverEntityProperties typeof<BodyWithMissingEntityProperties>

        let ownerResult = ValidateIdsDecisions.shouldValidateEntity None properties.OwnerId properties.OwnerName
        let repositoryResult = ValidateIdsDecisions.shouldValidateEntity None properties.RepositoryId properties.RepositoryName

        Assert.That(ownerResult, Is.False)
        Assert.That(repositoryResult, Is.False)

    /// Verifies that create Validation Requires Ids.
    [<TestCase(ValidateIdsDecisions.EntityKind.Owner, "OwnerIdIsRequired")>]
    [<TestCase(ValidateIdsDecisions.EntityKind.Organization, "OrganizationIdIsRequired")>]
    [<TestCase(ValidateIdsDecisions.EntityKind.Repository, "RepositoryIdIsRequired")>]
    [<TestCase(ValidateIdsDecisions.EntityKind.Branch, "BranchIdIsRequired")>]
    member _.CreateValidationRequiresIds(entityKind: ValidateIdsDecisions.EntityKind, expectedErrorName: string) =
        task {
            let! error = ValidateIdsDecisions.getEntityValidationErrorMessage entityKind ValidateIdsDecisions.EntityValidationMode.Create "" "valid-name"

            let expected =
                match expectedErrorName with
                | "OwnerIdIsRequired" -> OwnerError.getErrorMessage OwnerError.OwnerIdIsRequired
                | "OrganizationIdIsRequired" -> OrganizationError.getErrorMessage OrganizationError.OrganizationIdIsRequired
                | "RepositoryIdIsRequired" -> RepositoryError.getErrorMessage RepositoryError.RepositoryIdIsRequired
                | "BranchIdIsRequired" -> BranchError.getErrorMessage BranchError.BranchIdIsRequired
                | _ -> failwith "Unexpected test case."

            Assert.That(error, Is.EqualTo(Some expected))
        }

    /// Verifies that create Validation Requires Names.
    [<TestCase(ValidateIdsDecisions.EntityKind.Owner, "OwnerNameIsRequired")>]
    [<TestCase(ValidateIdsDecisions.EntityKind.Organization, "OrganizationNameIsRequired")>]
    [<TestCase(ValidateIdsDecisions.EntityKind.Repository, "RepositoryNameIsRequired")>]
    [<TestCase(ValidateIdsDecisions.EntityKind.Branch, "BranchNameIsRequired")>]
    member _.CreateValidationRequiresNames(entityKind: ValidateIdsDecisions.EntityKind, expectedErrorName: string) =
        task {
            let! error =
                ValidateIdsDecisions.getEntityValidationErrorMessage entityKind ValidateIdsDecisions.EntityValidationMode.Create (Guid.NewGuid().ToString()) ""

            let expected =
                match expectedErrorName with
                | "OwnerNameIsRequired" -> OwnerError.getErrorMessage OwnerError.OwnerNameIsRequired
                | "OrganizationNameIsRequired" -> OrganizationError.getErrorMessage OrganizationError.OrganizationNameIsRequired
                | "RepositoryNameIsRequired" -> RepositoryError.getErrorMessage RepositoryError.RepositoryNameIsRequired
                | "BranchNameIsRequired" -> BranchError.getErrorMessage BranchError.BranchNameIsRequired
                | _ -> failwith "Unexpected test case."

            Assert.That(error, Is.EqualTo(Some expected))
        }

    /// Verifies that existing Validation Allows Only Id.
    [<TestCase(ValidateIdsDecisions.EntityKind.Owner)>]
    [<TestCase(ValidateIdsDecisions.EntityKind.Organization)>]
    [<TestCase(ValidateIdsDecisions.EntityKind.Repository)>]
    [<TestCase(ValidateIdsDecisions.EntityKind.Branch)>]
    member _.ExistingValidationAllowsOnlyId(entityKind: ValidateIdsDecisions.EntityKind) =
        task {
            let! error =
                ValidateIdsDecisions.getEntityValidationErrorMessage
                    entityKind
                    ValidateIdsDecisions.EntityValidationMode.Existing
                    (Guid.NewGuid().ToString())
                    ""

            Assert.That(error, Is.EqualTo(None))
        }

    /// Verifies that existing Validation Allows Only Name.
    [<TestCase(ValidateIdsDecisions.EntityKind.Owner)>]
    [<TestCase(ValidateIdsDecisions.EntityKind.Organization)>]
    [<TestCase(ValidateIdsDecisions.EntityKind.Repository)>]
    [<TestCase(ValidateIdsDecisions.EntityKind.Branch)>]
    member _.ExistingValidationAllowsOnlyName(entityKind: ValidateIdsDecisions.EntityKind) =
        task {
            let! error = ValidateIdsDecisions.getEntityValidationErrorMessage entityKind ValidateIdsDecisions.EntityValidationMode.Existing "" "valid-name"

            Assert.That(error, Is.EqualTo(None))
        }

    /// Verifies that invalid Ids Fail Before Resolution.
    [<TestCase(ValidateIdsDecisions.EntityKind.Owner, "InvalidOwnerId")>]
    [<TestCase(ValidateIdsDecisions.EntityKind.Organization, "InvalidOrganizationId")>]
    [<TestCase(ValidateIdsDecisions.EntityKind.Repository, "InvalidRepositoryId")>]
    [<TestCase(ValidateIdsDecisions.EntityKind.Branch, "InvalidBranchId")>]
    member _.InvalidIdsFailBeforeResolution(entityKind: ValidateIdsDecisions.EntityKind, expectedErrorName: string) =
        task {
            let! error =
                ValidateIdsDecisions.getEntityValidationErrorMessage entityKind ValidateIdsDecisions.EntityValidationMode.Existing "not-a-guid" "valid-name"

            let expected =
                match expectedErrorName with
                | "InvalidOwnerId" -> OwnerError.getErrorMessage OwnerError.InvalidOwnerId
                | "InvalidOrganizationId" -> OrganizationError.getErrorMessage OrganizationError.InvalidOrganizationId
                | "InvalidRepositoryId" -> RepositoryError.getErrorMessage RepositoryError.InvalidRepositoryId
                | "InvalidBranchId" -> BranchError.getErrorMessage BranchError.InvalidBranchId
                | _ -> failwith "Unexpected test case."

            Assert.That(error, Is.EqualTo(Some expected))
        }

    /// Verifies that not Found Error Selection Preserves Id Versus Name Only Errors.
    [<TestCase(ValidateIdsDecisions.EntityKind.Owner, true)>]
    [<TestCase(ValidateIdsDecisions.EntityKind.Owner, false)>]
    [<TestCase(ValidateIdsDecisions.EntityKind.Organization, true)>]
    [<TestCase(ValidateIdsDecisions.EntityKind.Organization, false)>]
    [<TestCase(ValidateIdsDecisions.EntityKind.Repository, true)>]
    [<TestCase(ValidateIdsDecisions.EntityKind.Repository, false)>]
    [<TestCase(ValidateIdsDecisions.EntityKind.Branch, true)>]
    [<TestCase(ValidateIdsDecisions.EntityKind.Branch, false)>]
    member _.NotFoundErrorSelectionPreservesIdVersusNameOnlyErrors(entityKind: ValidateIdsDecisions.EntityKind, hasId: bool) =
        let id = if hasId then Guid.NewGuid().ToString() else ""
        let result = ValidateIdsDecisions.notFoundErrorMessage entityKind id

        let expected =
            match entityKind, hasId with
            | ValidateIdsDecisions.EntityKind.Owner, true -> OwnerError.getErrorMessage OwnerError.OwnerIdDoesNotExist
            | ValidateIdsDecisions.EntityKind.Owner, false -> OwnerError.getErrorMessage OwnerError.OwnerDoesNotExist
            | ValidateIdsDecisions.EntityKind.Organization, true -> OrganizationError.getErrorMessage OrganizationError.OrganizationIdDoesNotExist
            | ValidateIdsDecisions.EntityKind.Organization, false -> OrganizationError.getErrorMessage OrganizationError.OrganizationDoesNotExist
            | ValidateIdsDecisions.EntityKind.Repository, true -> RepositoryError.getErrorMessage RepositoryError.RepositoryIdDoesNotExist
            | ValidateIdsDecisions.EntityKind.Repository, false -> RepositoryError.getErrorMessage RepositoryError.RepositoryDoesNotExist
            | ValidateIdsDecisions.EntityKind.Branch, true -> BranchError.getErrorMessage BranchError.BranchIdDoesNotExist
            | ValidateIdsDecisions.EntityKind.Branch, false -> BranchError.getErrorMessage BranchError.BranchDoesNotExist
            | _ -> failwith "Unexpected test case."

        Assert.That(result, Is.EqualTo(expected))

    /// Verifies that resolved Ids Populate Expected Grace Ids Fields.
    [<TestCase(ValidateIdsDecisions.EntityKind.Owner)>]
    [<TestCase(ValidateIdsDecisions.EntityKind.Organization)>]
    [<TestCase(ValidateIdsDecisions.EntityKind.Repository)>]
    [<TestCase(ValidateIdsDecisions.EntityKind.Branch)>]
    member _.ResolvedIdsPopulateExpectedGraceIdsFields(entityKind: ValidateIdsDecisions.EntityKind) =
        let id = Guid.NewGuid().ToString()
        let result = ValidateIdsDecisions.withEntityId entityKind id GraceIds.Default

        match entityKind with
        | ValidateIdsDecisions.EntityKind.Owner ->
            Assert.That(result.OwnerIdString, Is.EqualTo(id))
            Assert.That(result.OwnerId, Is.EqualTo(Guid.Parse(id)))
            Assert.That(result.HasOwner, Is.True)
        | ValidateIdsDecisions.EntityKind.Organization ->
            Assert.That(result.OrganizationIdString, Is.EqualTo(id))
            Assert.That(result.OrganizationId, Is.EqualTo(Guid.Parse(id)))
            Assert.That(result.HasOrganization, Is.True)
        | ValidateIdsDecisions.EntityKind.Repository ->
            Assert.That(result.RepositoryIdString, Is.EqualTo(id))
            Assert.That(result.RepositoryId, Is.EqualTo(Guid.Parse(id)))
            Assert.That(result.HasRepository, Is.True)
        | ValidateIdsDecisions.EntityKind.Branch ->
            Assert.That(result.BranchIdString, Is.EqualTo(id))
            Assert.That(result.BranchId, Is.EqualTo(Guid.Parse(id)))
            Assert.That(result.HasBranch, Is.True)
        | _ -> failwith "Unexpected test case."
