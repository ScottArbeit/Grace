namespace Grace.Server.Tests

open FSharp.Control
open FSharpPlus
open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors.Repository
open Microsoft.Extensions.Logging
open NUnit.Framework
open System
open System.Net.Http.Json
open System.Net
open System.Threading.Tasks
open System.IO
open System.Text
open System.Diagnostics
open Grace.Shared.Types
open System.Net.Http
open Grace.Shared.Validation

[<Parallelizable(ParallelScope.All)>]
type Repository() =

    let log = LoggerFactory.Create(fun builder -> builder.AddConsole().AddDebug() |> ignore).CreateLogger("RepositoryTests")

    member val public TestContext = TestContext.CurrentContext with get, set

    [<Test>]
    [<Repeat(1)>]
    member public this.SetDescriptionWithValidValues() =
        task {
            let parameters = Parameters.Repository.SetRepositoryDescriptionParameters()

            parameters.Description <- $"Description set at {getCurrentInstantGeneral ()}."
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryIds[(rnd.Next(0, numberOfRepositories))]

            let! response = Client.PostAsync("/repository/setDescription", createJsonContent parameters)
            let! content = response.Content.ReadAsStringAsync()
            response.EnsureSuccessStatusCode() |> ignore
            Assert.That(content.Length, Is.GreaterThan(0))
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
            let parameters = Parameters.Repository.SetRepositoryDescriptionParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryIds[(rnd.Next(0, numberOfRepositories))]
            parameters.Description <- String.Empty

            let! response = Client.PostAsync("/repository/setDescription", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! responseStream = response.Content.ReadAsStreamAsync()
            let! error = deserializeAsync<GraceError> responseStream
            Assert.That(error.Error, Is.EqualTo(RepositoryError.getErrorMessage DescriptionIsRequired))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetSaveDaysWithValidValues() =
        task {
            let parameters = Parameters.Repository.SetSaveDaysParameters()
            parameters.SaveDays <- 17.5
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryIds[(rnd.Next(0, numberOfRepositories))]

            let! response = Client.PostAsync("/repository/setSaveDays", createJsonContent parameters)
            let! content = response.Content.ReadAsStringAsync()
            response.EnsureSuccessStatusCode() |> ignore
            Assert.That(content.Length, Is.GreaterThan(0))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetSaveDaysWithInvalidValues() =
        task {
            let parameters = Parameters.Repository.SetSaveDaysParameters()
            parameters.SaveDays <- -1
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryIds[(rnd.Next(0, numberOfRepositories))]

            let! response = Client.PostAsync("/repository/setSaveDays", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! responseStream = response.Content.ReadAsStreamAsync()
            let! error = deserializeAsync<GraceError> responseStream
            Assert.That(error.Error, Is.EqualTo(RepositoryError.getErrorMessage InvalidSaveDaysValue))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetCheckpointDaysWithValidValues() =
        task {
            let parameters = Parameters.Repository.SetCheckpointDaysParameters()
            parameters.CheckpointDays <- 17.5
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryIds[(rnd.Next(0, numberOfRepositories))]

            let! response = Client.PostAsync("/repository/setCheckpointDays", createJsonContent parameters)
            let! content = response.Content.ReadAsStringAsync()
            response.EnsureSuccessStatusCode() |> ignore
            Assert.That(content.Length, Is.GreaterThan(0))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetCheckpointDaysWithInvalidValues() =
        task {
            let parameters = Parameters.Repository.SetCheckpointDaysParameters()
            parameters.CheckpointDays <- -1
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryIds[(rnd.Next(0, numberOfRepositories))]

            let! response = Client.PostAsync("/repository/setCheckpointDays", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! responseStream = response.Content.ReadAsStreamAsync()
            let! error = deserializeAsync<GraceError> responseStream
            Assert.That(error.Error, Is.EqualTo(RepositoryError.getErrorMessage InvalidCheckpointDaysValue))
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
            Assert.That(error.Error, Is.EqualTo(RepositoryError.getErrorMessage InvalidOwnerId))
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
            Assert.That(returnValue.Properties[nameof(OwnerId)], Is.EqualTo(ownerId))
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
            Assert.That(error.Error, Is.EqualTo(RepositoryError.getErrorMessage InvalidOrganizationId))
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
            Assert.That(error.Error, Is.EqualTo(RepositoryError.getErrorMessage InvalidRepositoryStatus))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetVisibilityWithValidValues() =
        task {
            let parameters = Parameters.Repository.SetRepositoryVisibilityParameters()

            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryIds[(rnd.Next(0, numberOfRepositories))]
            parameters.Visibility <- "Public"

            let! response = Client.PostAsync("/repository/setVisibility", createJsonContent parameters)
            let! content = response.Content.ReadAsStringAsync()
            //Console.WriteLine($"{content}");
            response.EnsureSuccessStatusCode() |> ignore
            Assert.That(content.Length, Is.GreaterThan(0))
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
