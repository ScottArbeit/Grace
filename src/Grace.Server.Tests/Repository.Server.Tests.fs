namespace Grace.Server.Tests

open Grace.Shared
open Grace.Shared.Utilities
open NUnit.Framework
open System
open System.Net.Http.Json
open System.Net
open System.Threading.Tasks

[<Parallelizable(ParallelScope.All)>]
type Repository() =

    let rnd = Random.Shared
    let numberOfRepositories = 3
    let ownerId = $"{Guid.NewGuid()}"
    let organizationId = $"{Guid.NewGuid()}"
    let repositoryIds = Array.init numberOfRepositories (fun _ -> $"{Guid.NewGuid()}")

    [<OneTimeSetUp>]
    member public this.Setup() =
        task {
            let ownerParameters = Parameters.Owner.CreateOwnerParameters()
            ownerParameters.OwnerId <- ownerId
            ownerParameters.OwnerName <- $"TestOwner{rnd.Next(1000)}"
            let! response = Services.Client.PostAsync("/owner/create", jsonContent ownerParameters)
            let! content = response.Content.ReadAsStringAsync()
            //Console.WriteLine($"{content}");
            response.EnsureSuccessStatusCode() |> ignore

            let organizationParameters = Parameters.Organization.CreateOrganizationParameters()
            organizationParameters.OwnerId <- ownerId
            organizationParameters.OrganizationId <- organizationId
            organizationParameters.OrganizationName <- $"TestOrganization{rnd.Next(1000)}"
            let! response = Services.Client.PostAsync("/organization/create", jsonContent organizationParameters)
            let! content = response.Content.ReadAsStringAsync()
            //Console.WriteLine($"{content}");
            response.EnsureSuccessStatusCode() |> ignore

            do! Parallel.ForEachAsync(repositoryIds, Constants.ParallelOptions, (fun repositoryId ct ->
                ValueTask(task {
                    let repositoryParameters = Parameters.Repository.CreateRepositoryParameters()
                    repositoryParameters.OwnerId <- ownerId
                    repositoryParameters.OrganizationId <- organizationId
                    repositoryParameters.RepositoryId <- repositoryId
                    repositoryParameters.RepositoryName <- $"TestRepository{rnd.Next(100000):X4}"
                    //Console.WriteLine(serialize repositoryParameters)
                    let! response = Services.Client.PostAsync("/repository/create", jsonContent repositoryParameters)
                    let! content = response.Content.ReadAsStringAsync()
                    //Console.WriteLine($"{content}");
                    response.EnsureSuccessStatusCode() |> ignore
                    Assert.That(content.Length, Is.GreaterThan(0))
                })))
        }

    [<OneTimeTearDown>]
    member public this.Teardown() =
        task {
            do! Parallel.ForEachAsync(repositoryIds, Constants.ParallelOptions, (fun repositoryId ct ->
                ValueTask(task {
                    let repositoryDeleteParameters = Parameters.Repository.DeleteRepositoryParameters()
                    repositoryDeleteParameters.OwnerId <- ownerId 
                    repositoryDeleteParameters.OrganizationId <- organizationId
                    repositoryDeleteParameters.RepositoryId <- repositoryId
                    repositoryDeleteParameters.DeleteReason <- "Deleting test repository"
                    let! response = Services.Client.PostAsync("/repository/delete", jsonContent repositoryDeleteParameters)
                    let! content = response.Content.ReadAsStringAsync()
                    //Console.WriteLine($"{content}");
                    response.EnsureSuccessStatusCode() |> ignore
                    Assert.That(content.Length, Is.GreaterThan(0))
                })))
            
            let organizationDeleteParameters = Parameters.Organization.DeleteOrganizationParameters()
            organizationDeleteParameters.OwnerId <- ownerId
            organizationDeleteParameters.OrganizationId <- organizationId
            organizationDeleteParameters.DeleteReason <- "Deleting test organization"
            let! response = Services.Client.PostAsync("/organization/delete", jsonContent organizationDeleteParameters)

            let ownerDeleteParameters = Parameters.Owner.DeleteOwnerParameters()
            ownerDeleteParameters.OwnerId <- ownerId
            ownerDeleteParameters.DeleteReason <- "Deleting test owner"
            let! response = Services.Client.PostAsync("/owner/delete", jsonContent ownerDeleteParameters)

            let! content = response.Content.ReadAsStringAsync()
            //Console.WriteLine($"{content}");
            response.EnsureSuccessStatusCode() |> ignore
            Assert.That(content.Length, Is.GreaterThan(0))
        }
   
    [<Test>]
    [<Repeat(1)>]
    member public this.SetDescriptionWithValidValues() =
        task {
            let parameters = Grace.Shared.Parameters.Repository.SetRepositoryDescriptionParameters()
            parameters.Description <- $"Description set at {getCurrentInstantGeneral()}."
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryIds[(rnd.Next(0, numberOfRepositories))]
            
            let! response = Services.Client.PostAsync("/repository/setDescription", jsonContent parameters)
            let! content = response.Content.ReadAsStringAsync()
            //Console.WriteLine($"{content}");
            response.EnsureSuccessStatusCode() |> ignore
            Assert.That(content.Length, Is.GreaterThan(0))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetDescriptionWithInvalidValues() =
        task {
            let parameters = Parameters.Repository.SetRepositoryDescriptionParameters()
            parameters.Description <- $"Description set at {getCurrentInstantGeneral()}."
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- "not a guid"
           
            let! response = Services.Client.PostAsync("/repository/setDescription", jsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! content = response.Content.ReadAsStringAsync()
            //Console.WriteLine($"{content}");
            Assert.That(content, Does.Contain("is not a valid Guid."))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetSaveDaysWithValidValues() =
        task {
            let parameters = Grace.Shared.Parameters.Repository.SetSaveDaysParameters()
            parameters.SaveDays <- 17.5
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryIds[(rnd.Next(0, numberOfRepositories))]
            
            let! response = Services.Client.PostAsync("/repository/setSaveDays", jsonContent parameters)
            let! content = response.Content.ReadAsStringAsync()
            //Console.WriteLine($"{content}");
            response.EnsureSuccessStatusCode() |> ignore
            Assert.That(content.Length, Is.GreaterThan(0))
        }
        
    [<Test>]
    [<Repeat(1)>]
    member public this.SetSaveDaysWithInvalidValues() =
        task {
            let parameters = Grace.Shared.Parameters.Repository.SetSaveDaysParameters()
            parameters.SaveDays <- -1
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryIds[(rnd.Next(0, numberOfRepositories))]
            
            let! response = Services.Client.PostAsync("/repository/setSaveDays", jsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! content = response.Content.ReadAsStringAsync()
            //Console.WriteLine($"{content}");
            Assert.That(content, Does.Contain("SaveDays is invalid."))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetCheckpointDaysWithValidValues() =
        task {
            let parameters = Grace.Shared.Parameters.Repository.SetCheckpointDaysParameters()
            parameters.CheckpointDays <- 17.5
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryIds[(rnd.Next(0, numberOfRepositories))]
            
            let! response = Services.Client.PostAsync("/repository/setCheckpointDays", jsonContent parameters)
            let! content = response.Content.ReadAsStringAsync()
            //Console.WriteLine($"{content}");
            response.EnsureSuccessStatusCode() |> ignore
            Assert.That(content.Length, Is.GreaterThan(0))
        }
        
    [<Test>]
    [<Repeat(1)>]
    member public this.SetCheckpointDaysWithInvalidValues() =
        task {
            let parameters = Grace.Shared.Parameters.Repository.SetCheckpointDaysParameters()
            parameters.CheckpointDays <- -1
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryIds[(rnd.Next(0, numberOfRepositories))]
            
            let! response = Services.Client.PostAsync("/repository/setCheckpointDays", jsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! content = response.Content.ReadAsStringAsync()
            //Console.WriteLine($"{content}");
            Assert.That(content, Does.Contain("CheckpointDays is invalid."))
        }
                
    [<Test>]
    [<Repeat(1)>]
    member public this.GetBranchesWithValidValues() =
        task {
            let parameters = Grace.Shared.Parameters.Repository.GetBranchesParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryIds[(rnd.Next(0, numberOfRepositories))]
            
            let! response = Services.Client.PostAsync("/repository/getBranches", jsonContent parameters)
            let! content = response.Content.ReadAsStringAsync()
            //Console.WriteLine($"{content}");
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
            parameters.RepositoryId <- repositoryIds[(rnd.Next(0, numberOfRepositories))]
            
            let! response = Services.Client.PostAsync("/repository/getBranches", jsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! content = response.Content.ReadAsStringAsync()
            //Console.WriteLine($"{content}");
            Assert.That(content, Does.Contain("is not a valid Guid."))
        }
        
    [<Test>]
    [<Repeat(1)>]
    member public this.SetStatusWithValidValues() =
        task {
            let parameters = Grace.Shared.Parameters.Repository.SetRepositoryStatusParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryIds[(rnd.Next(0, numberOfRepositories))]
            parameters.Status <- "Active"
            
            let! response = Services.Client.PostAsync("/repository/setStatus", jsonContent parameters)
            let! content = response.Content.ReadAsStringAsync()
            //Console.WriteLine($"{content}");
            response.EnsureSuccessStatusCode() |> ignore
            Assert.That(content.Length, Is.GreaterThan(0))
        }
        
    [<Test>]
    [<Repeat(1)>]
    member public this.SetStatusWithInvalidValues() =
        task {
            let parameters = Grace.Shared.Parameters.Repository.SetRepositoryStatusParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- "not a Guid"
            parameters.RepositoryId <- repositoryIds[(rnd.Next(0, numberOfRepositories))]
            parameters.Status <- "Active"
            
            let! response = Services.Client.PostAsync("/repository/setStatus", jsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! content = response.Content.ReadAsStringAsync()
            //Console.WriteLine($"{content}");
            Assert.That(content, Does.Contain("is not a valid Guid."))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetVisibilityWithValidValues() =
        task {
            let parameters = Grace.Shared.Parameters.Repository.SetRepositoryVisibilityParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryIds[(rnd.Next(0, numberOfRepositories))]
            parameters.Visibility <- "Public"
            
            let! response = Services.Client.PostAsync("/repository/setVisibility", jsonContent parameters)
            let! content = response.Content.ReadAsStringAsync()
            //Console.WriteLine($"{content}");
            response.EnsureSuccessStatusCode() |> ignore
            Assert.That(content.Length, Is.GreaterThan(0))
        }
        
    [<Test>]
    [<Repeat(1)>]
    member public this.SetVisibilityWithInvalidValues() =
        task {
            let parameters = Grace.Shared.Parameters.Repository.SetRepositoryVisibilityParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryIds[(rnd.Next(0, numberOfRepositories))]
            parameters.Visibility <- "Not a visibility value"
            
            let! response = Services.Client.PostAsync("/repository/setVisibility", jsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! content = response.Content.ReadAsStringAsync()
            //Console.WriteLine($"{content}");
            Assert.That(content, Does.Contain("visibility"))
        }
