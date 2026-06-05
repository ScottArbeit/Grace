namespace Grace.Server.Tests

open FSharp.Control
open FSharpPlus
open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Microsoft.Extensions.Logging
open NUnit.Framework
open System
open System.Net.Http.Json
open System.Net
open System.Threading.Tasks
open System.IO
open System.Text
open System.Diagnostics
open Grace.Types.Common
open Grace.Types
open System.Net.Http
open Grace.Shared.Validation

[<Parallelizable(ParallelScope.All)>]
type Repository() =

    let log =
        LoggerFactory
            .Create(fun builder -> builder.AddConsole().AddDebug() |> ignore)
            .CreateLogger("RepositoryTests")

    let grantRepoAdminAsync repositoryId =
        task {
            let parameters = Parameters.Access.GrantRoleParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.PrincipalType <- "User"
            parameters.PrincipalId <- testUserId
            parameters.ScopeKind <- "repo"
            parameters.RoleId <- "RepoAdmin"
            parameters.Source <- "test"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/access/grantRole", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore
        }

    let createRepositoryAsync () =
        task {
            let repositoryId = $"{Guid.NewGuid()}"
            let parameters = Parameters.Repository.CreateRepositoryParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.RepositoryName <- $"AssertionRepository{Guid.NewGuid():N}"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/repository/create", createJsonContent parameters)
            let! responseText = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), responseText)
            return repositoryId
        }

    let getRepositoryAsync repositoryId =
        task {
            let parameters = Parameters.Repository.GetRepositoryParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/repository/get", createJsonContent parameters)
            let! responseText = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), responseText)
            Assert.That(response.Content.Headers.ContentType.MediaType, Is.EqualTo("application/json"))

            let returnValue = deserialize<GraceReturnValue<Repository.RepositoryDto>> responseText
            return returnValue.ReturnValue
        }

    member val public TestContext = TestContext.CurrentContext with get, set

    [<Test>]
    [<Repeat(1)>]
    member public this.SetDescriptionWithValidValues() =
        task {
            let! repositoryId = createRepositoryAsync ()
            let parameters = Parameters.Repository.SetRepositoryDescriptionParameters()
            let expectedDescription = $"Description set at {getCurrentInstantGeneral ()}."

            parameters.Description <- expectedDescription
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.CorrelationId <- generateCorrelationId ()

            do! grantRepoAdminAsync parameters.RepositoryId

            let! response = Client.PostAsync("/repository/setDescription", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore
            let! returnValue = deserializeContent<GraceReturnValue<string>> response
            Assert.That(returnValue.CorrelationId, Is.Not.Empty)

            let! stored = getRepositoryAsync repositoryId
            Assert.That(stored.RepositoryId, Is.EqualTo(Guid.Parse(repositoryId)))
            Assert.That(stored.Description, Is.EqualTo(expectedDescription))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetDescriptionWithInvalidValues() =
        task {
            let parameters = Parameters.Repository.SetRepositoryDescriptionParameters()
            parameters.Description <- $"Description set at {getCurrentInstantGeneral ()}."
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- "not a guid"

            let! response = Client.PostAsync("/repository/setDescription", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! content = response.Content.ReadAsStringAsync()
            Assert.That(content, Does.Contain("is not a valid Guid."))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetDescriptionWithEmptyDescription() =
        task {
            let! repositoryId = createRepositoryAsync ()
            let baseline = "Repository description should survive invalid update."

            let validParameters = Parameters.Repository.SetRepositoryDescriptionParameters()
            validParameters.OwnerId <- ownerId
            validParameters.OrganizationId <- organizationId
            validParameters.RepositoryId <- repositoryId
            validParameters.Description <- baseline
            validParameters.CorrelationId <- generateCorrelationId ()

            do! grantRepoAdminAsync repositoryId

            let! validResponse = Client.PostAsync("/repository/setDescription", createJsonContent validParameters)
            validResponse.EnsureSuccessStatusCode() |> ignore

            let parameters = Parameters.Repository.SetRepositoryDescriptionParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.Description <- String.Empty
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/repository/setDescription", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! responseStream = response.Content.ReadAsStreamAsync()
            let! error = deserializeAsync<GraceError> responseStream
            Assert.That(error.Error, Is.EqualTo(getErrorMessage RepositoryError.DescriptionIsRequired))
            Assert.That(error.CorrelationId, Is.Not.Empty)

            let! stored = getRepositoryAsync repositoryId
            Assert.That(stored.Description, Is.EqualTo(baseline))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetSaveDaysWithValidValues() =
        task {
            let! repositoryId = createRepositoryAsync ()
            let parameters = Parameters.Repository.SetSaveDaysParameters()
            parameters.SaveDays <- 17.5f
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/repository/setSaveDays", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore

            let! stored = getRepositoryAsync repositoryId
            Assert.That(stored.SaveDays, Is.EqualTo(parameters.SaveDays))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetSaveDaysWithInvalidValues() =
        task {
            let parameters = Parameters.Repository.SetSaveDaysParameters()
            parameters.SaveDays <- -1f
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryIds[(rnd.Next(0, numberOfRepositories))]

            let! response = Client.PostAsync("/repository/setSaveDays", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! responseStream = response.Content.ReadAsStreamAsync()
            let! error = deserializeAsync<GraceError> responseStream
            Assert.That(error.Error, Is.EqualTo(getErrorMessage RepositoryError.InvalidSaveDaysValue))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetCheckpointDaysWithValidValues() =
        task {
            let! repositoryId = createRepositoryAsync ()
            let parameters = Parameters.Repository.SetCheckpointDaysParameters()
            parameters.CheckpointDays <- 17.5f
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/repository/setCheckpointDays", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore

            let! stored = getRepositoryAsync repositoryId
            Assert.That(stored.CheckpointDays, Is.EqualTo(parameters.CheckpointDays))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetCheckpointDaysWithInvalidValues() =
        task {
            let parameters = Parameters.Repository.SetCheckpointDaysParameters()
            parameters.CheckpointDays <- -1f
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryIds[(rnd.Next(0, numberOfRepositories))]

            let! response = Client.PostAsync("/repository/setCheckpointDays", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! responseStream = response.Content.ReadAsStreamAsync()
            let! error = deserializeAsync<GraceError> responseStream
            Assert.That(error.Error, Is.EqualTo(getErrorMessage RepositoryError.InvalidCheckpointDaysValue))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.GetBranchesWithValidValues() =
        task {
            let parameters = Parameters.Repository.GetBranchesParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryIds[(rnd.Next(0, numberOfRepositories))]

            let! response = Client.PostAsync("/repository/getBranches", createJsonContent parameters)
            let! content = response.Content.ReadAsStringAsync()
            response.EnsureSuccessStatusCode() |> ignore
            Assert.That(content.Length, Is.GreaterThan(0))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.GetBranchesWithInvalidValues() =
        task {
            let parameters = Parameters.Repository.GetBranchesParameters()
            parameters.OwnerId <- "not a Guid"
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryIds[(rnd.Next(0, numberOfRepositories))]

            let! response = Client.PostAsync("/repository/getBranches", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! error = deserializeContent<GraceError> response
            Assert.That(error.Error, Is.EqualTo(getErrorMessage RepositoryError.InvalidOwnerId))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetStatusWithValidValues() =
        task {
            let parameters = Parameters.Repository.SetRepositoryStatusParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryIds[(rnd.Next(0, numberOfRepositories))]
            parameters.Status <- "Active"

            let! response = Client.PostAsync("/repository/setStatus", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore
            let! returnValue = deserializeContent<GraceReturnValue<string>> response
            let ownerGuid = Common.requireGuidProperty (nameof OwnerId) returnValue.Properties[nameof OwnerId]
            Assert.That(ownerGuid, Is.EqualTo(Guid.Parse(ownerId)))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetStatusWithInvalidValues() =
        task {
            let parameters = Parameters.Repository.SetRepositoryStatusParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- "this is an invalid OrganizationId"
            parameters.RepositoryId <- repositoryIds[(rnd.Next(0, numberOfRepositories))]
            parameters.Status <- "Active"

            let! response = Client.PostAsync("/repository/setStatus", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! error = deserializeContent<GraceError> response
            Assert.That(error.Error, Is.EqualTo(getErrorMessage RepositoryError.InvalidOrganizationId))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetStatusWithEmptyStatus() =
        task {
            let parameters = Parameters.Repository.SetRepositoryStatusParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryIds[(rnd.Next(0, numberOfRepositories))]
            parameters.Status <- String.Empty

            let! response = Client.PostAsync("/repository/setStatus", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! responseStream = response.Content.ReadAsStreamAsync()
            let! error = deserializeAsync<GraceError> responseStream
            Assert.That(error.Error, Is.EqualTo(getErrorMessage RepositoryError.InvalidRepositoryStatus))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetVisibilityWithValidValues() =
        task {
            let! repositoryId = createRepositoryAsync ()
            let parameters = Parameters.Repository.SetRepositoryVisibilityParameters()

            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.Visibility <- "Public"
            parameters.CorrelationId <- generateCorrelationId ()

            do! grantRepoAdminAsync repositoryId

            let! response = Client.PostAsync("/repository/setVisibility", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore

            let! stored = getRepositoryAsync repositoryId
            Assert.That(stored.RepositoryType, Is.EqualTo(RepositoryType.Public))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetVisibilityWithInvalidValues() =
        task {
            let parameters = Parameters.Repository.SetRepositoryVisibilityParameters()

            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryIds[(rnd.Next(0, numberOfRepositories))]
            parameters.Visibility <- "Not a visibility value"

            let! response = Client.PostAsync("/repository/setVisibility", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! content = response.Content.ReadAsStringAsync()
            //Console.WriteLine($"{content}");
            Assert.That(content, Does.Contain("visibility"))
        }
