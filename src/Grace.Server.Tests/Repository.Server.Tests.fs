namespace Grace.Server.Tests

open Grace.Shared
open Grace.Shared.Utilities
open NUnit.Framework
open System
open System.Net.Http.Json
open System.Net

[<Parallelizable(ParallelScope.All)>]
type Repository() =

    let ownerId = $"{Guid.NewGuid()}"
    let organizationId = $"{Guid.NewGuid()}"
    let repositoryId = $"{Guid.NewGuid()}"

    [<OneTimeSetUp>]
    member public this.Setup() =
        task {
            let ownerParameters = Parameters.Owner.CreateParameters()
            ownerParameters.OwnerId <- ownerId
            ownerParameters.OwnerName <- $"TestOwner{Random.Shared.Next(1000)}"
            let! response = Services.Client.PostAsync("/owner/create", jsonContent ownerParameters)

            let organizationParameters = Parameters.Organization.CreateParameters()
            organizationParameters.OwnerId <- ownerId
            organizationParameters.OrganizationId <- organizationId
            organizationParameters.OrganizationName <- $"TestOrganization{Random.Shared.Next(1000)}"
            let! response = Services.Client.PostAsync("/organization/create", jsonContent organizationParameters)

            let repositoryParameters = Parameters.Repository.CreateParameters()
            repositoryParameters.OwnerId <- ownerId
            repositoryParameters.OrganizationId <- organizationId
            repositoryParameters.RepositoryId <- repositoryId
            repositoryParameters.RepositoryName <- $"TestRepository{Random.Shared.Next(1000)}"
            let! response = Services.Client.PostAsync("/repository/create", jsonContent repositoryParameters)

            let! content = response.Content.ReadAsStringAsync()
            Console.WriteLine($"{content}");
            response.EnsureSuccessStatusCode() |> ignore
            Assert.That(content.Length, Is.GreaterThan(0))
        }

    [<OneTimeTearDown>]
    member public this.Teardown() =
        task {
            let repositoryDeleteParameters = Parameters.Repository.DeleteParameters()
            repositoryDeleteParameters.OwnerId <- ownerId
            repositoryDeleteParameters.OrganizationId <- organizationId
            repositoryDeleteParameters.RepositoryId <- repositoryId
            repositoryDeleteParameters.DeleteReason <- "Deleting test repository"
            let! response = Services.Client.PostAsync("/repository/delete", jsonContent repositoryDeleteParameters)

            let organizationDeleteParameters = Parameters.Organization.DeleteParameters()
            organizationDeleteParameters.OwnerId <- ownerId
            organizationDeleteParameters.OrganizationId <- organizationId
            organizationDeleteParameters.DeleteReason <- "Deleting test organization"
            let! response = Services.Client.PostAsync("/organization/delete", jsonContent organizationDeleteParameters)

            let ownerDeleteParameters = Parameters.Owner.DeleteParameters()
            ownerDeleteParameters.OwnerId <- ownerId
            ownerDeleteParameters.DeleteReason <- "Deleting test owner"
            let! response = Services.Client.PostAsync("/owner/delete", jsonContent ownerDeleteParameters)

            let! content = response.Content.ReadAsStringAsync()
            Console.WriteLine($"{content}");
            response.EnsureSuccessStatusCode() |> ignore
            Assert.That(content.Length, Is.GreaterThan(0))
        }
   
    [<Test>]
    [<Repeat(1)>]
    member public this.SetDescriptionWithValidValues() =
        task {
            let parameters = Grace.Shared.Parameters.Repository.DescriptionParameters()
            parameters.Description <- $"Description set at {getCurrentInstantGeneral()}."
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            
            let! response = Services.Client.PostAsync("/repository/setDescription", jsonContent parameters)
            let! content = response.Content.ReadAsStringAsync()
            Console.WriteLine($"{content}");
            response.EnsureSuccessStatusCode() |> ignore
            Assert.That(content.Length, Is.GreaterThan(0))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetDescriptionWithInvalidValues() =
        task {
            let parameters = Parameters.Repository.DescriptionParameters()
            parameters.Description <- $"Description set at {getCurrentInstantGeneral()}."
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- "not a guid"
           
            let! response = Services.Client.PostAsync("/repository/setDescription", jsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! content = response.Content.ReadAsStringAsync()
            Console.WriteLine($"{content}");
            Assert.That(content, Does.Contain("is not a valid Guid."))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetSaveDaysWithValidValues() =
        task {
            let parameters = Grace.Shared.Parameters.Repository.SaveDaysParameters()
            parameters.SaveDays <- 17.5
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            
            let! response = Services.Client.PostAsync("/repository/setSaveDays", jsonContent parameters)
            let! content = response.Content.ReadAsStringAsync()
            Console.WriteLine($"{content}");
            response.EnsureSuccessStatusCode() |> ignore
            Assert.That(content.Length, Is.GreaterThan(0))
        }
        
    [<Test>]
    [<Repeat(1)>]
    member public this.SetSaveDaysWithInvalidValues() =
        task {
            let parameters = Grace.Shared.Parameters.Repository.SaveDaysParameters()
            parameters.SaveDays <- -1
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            
            let! response = Services.Client.PostAsync("/repository/setSaveDays", jsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! content = response.Content.ReadAsStringAsync()
            Console.WriteLine($"{content}");
            Assert.That(content, Does.Contain("SaveDays is invalid."))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetCheckpointDaysWithValidValues() =
        task {
            let parameters = Grace.Shared.Parameters.Repository.CheckpointDaysParameters()
            parameters.CheckpointDays <- 17.5
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            
            let! response = Services.Client.PostAsync("/repository/setCheckpointDays", jsonContent parameters)
            let! content = response.Content.ReadAsStringAsync()
            Console.WriteLine($"{content}");
            response.EnsureSuccessStatusCode() |> ignore
            Assert.That(content.Length, Is.GreaterThan(0))
        }
        
    [<Test>]
    [<Repeat(1)>]
    member public this.SetCheckpointDaysWithInvalidValues() =
        task {
            let parameters = Grace.Shared.Parameters.Repository.CheckpointDaysParameters()
            parameters.CheckpointDays <- -1
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            
            let! response = Services.Client.PostAsync("/repository/setCheckpointDays", jsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! content = response.Content.ReadAsStringAsync()
            Console.WriteLine($"{content}");
            Assert.That(content, Does.Contain("CheckpointDays is invalid."))
        }
                
    [<Test>]
    [<Repeat(1)>]
    member public this.GetBranchesWithValidValues() =
        task {
            let parameters = Grace.Shared.Parameters.Repository.GetBranchesParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            
            let! response = Services.Client.PostAsync("/repository/getBranches", jsonContent parameters)
            let! content = response.Content.ReadAsStringAsync()
            Console.WriteLine($"{content}");
            response.EnsureSuccessStatusCode() |> ignore
            Assert.That(content.Length, Is.GreaterThan(0))
        }
        
    [<Test>]
    [<Repeat(1)>]
    member public this.GetBranchesWithInvalidValues() =
        task {
            let parameters = Grace.Shared.Parameters.Repository.GetBranchesParameters()
            parameters.OwnerId <- "not a Guid"
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            
            let! response = Services.Client.PostAsync("/repository/getBranches", jsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! content = response.Content.ReadAsStringAsync()
            Console.WriteLine($"{content}");
            Assert.That(content, Does.Contain("is not a valid Guid."))
        }
        
    [<Test>]
    [<Repeat(1)>]
    member public this.SetStatusWithValidValues() =
        task {
            let parameters = Grace.Shared.Parameters.Repository.StatusParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.Status <- "Active"
            
            let! response = Services.Client.PostAsync("/repository/setStatus", jsonContent parameters)
            let! content = response.Content.ReadAsStringAsync()
            Console.WriteLine($"{content}");
            response.EnsureSuccessStatusCode() |> ignore
            Assert.That(content.Length, Is.GreaterThan(0))
        }
        
    [<Test>]
    [<Repeat(1)>]
    member public this.SetStatusWithInvalidValues() =
        task {
            let parameters = Grace.Shared.Parameters.Repository.StatusParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- "not a Guid"
            parameters.RepositoryId <- repositoryId
            parameters.Status <- "Active"
            
            let! response = Services.Client.PostAsync("/repository/setStatus", jsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! content = response.Content.ReadAsStringAsync()
            Console.WriteLine($"{content}");
            Assert.That(content, Does.Contain("is not a valid Guid."))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetVisibilityWithValidValues() =
        task {
            let parameters = Grace.Shared.Parameters.Repository.VisibilityParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.Visibility <- "Public"
            
            let! response = Services.Client.PostAsync("/repository/setVisibility", jsonContent parameters)
            let! content = response.Content.ReadAsStringAsync()
            Console.WriteLine($"{content}");
            response.EnsureSuccessStatusCode() |> ignore
            Assert.That(content.Length, Is.GreaterThan(0))
        }
        
    [<Test>]
    [<Repeat(1)>]
    member public this.SetVisibilityWithInvalidValues() =
        task {
            let parameters = Grace.Shared.Parameters.Repository.VisibilityParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.Visibility <- "Not a visibility value"
            
            let! response = Services.Client.PostAsync("/repository/setVisibility", jsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! content = response.Content.ReadAsStringAsync()
            Console.WriteLine($"{content}");
            Assert.That(content, Does.Contain("visibility"))
        }
