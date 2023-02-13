namespace Grace.Server.Tests

open Grace.Shared
open Grace.Shared.Utilities
open Microsoft.AspNetCore.Mvc.Testing
open NUnit.Framework
open System
open System.Net.Http.Json
open System.Net

[<Parallelizable(ParallelScope.Children)>]
type Repository() =

    [<SetUp>]
    member public this.Setup() =
        ()
   
    [<Test>]
    [<Repeat(1)>]
    member public this.SetDescriptionWithValidValues() =
        task {
            let parameters = Grace.Shared.Parameters.Repository.DescriptionParameters()
            parameters.Description <- $"Description set at {getCurrentInstantGeneral()}."
            parameters.OwnerId <- "c7fadc8b-17ee-4832-86e2-520ccc1d630d"
            parameters.OrganizationId <- "1a354bba-2973-41cd-8572-cb71d535bb52"
            parameters.RepositoryId <- "a9e5fe8a-832f-4b5d-afe5-c19773774dd6"
            
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
            parameters.OwnerId <- Guid.NewGuid().ToString()
            parameters.OrganizationId <- Guid.NewGuid().ToString()
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
            parameters.OwnerId <- "c7fadc8b-17ee-4832-86e2-520ccc1d630d"
            parameters.OrganizationId <- "1a354bba-2973-41cd-8572-cb71d535bb52"
            parameters.RepositoryId <- "a9e5fe8a-832f-4b5d-afe5-c19773774dd6"
            
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
            parameters.OwnerId <- Guid.NewGuid().ToString()
            parameters.OrganizationId <- Guid.NewGuid().ToString()
            parameters.RepositoryId <- Guid.NewGuid().ToString()
            
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
            parameters.OwnerId <- "c7fadc8b-17ee-4832-86e2-520ccc1d630d"
            parameters.OrganizationId <- "1a354bba-2973-41cd-8572-cb71d535bb52"
            parameters.RepositoryId <- "a9e5fe8a-832f-4b5d-afe5-c19773774dd6"
            
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
            parameters.OwnerId <- Guid.NewGuid().ToString()
            parameters.OrganizationId <- Guid.NewGuid().ToString()
            parameters.RepositoryId <- Guid.NewGuid().ToString()
            
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
            parameters.OwnerId <- "c7fadc8b-17ee-4832-86e2-520ccc1d630d"
            parameters.OrganizationId <- "1a354bba-2973-41cd-8572-cb71d535bb52"
            parameters.RepositoryId <- "a9e5fe8a-832f-4b5d-afe5-c19773774dd6"
            
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
            parameters.OrganizationId <- Guid.NewGuid().ToString()
            parameters.RepositoryId <- Guid.NewGuid().ToString()
            
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
            parameters.OwnerId <- "c7fadc8b-17ee-4832-86e2-520ccc1d630d"
            parameters.OrganizationId <- "1a354bba-2973-41cd-8572-cb71d535bb52"
            parameters.RepositoryId <- "a9e5fe8a-832f-4b5d-afe5-c19773774dd6"
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
            parameters.OwnerId <- Guid.NewGuid().ToString()
            parameters.OrganizationId <- "not a Guid"
            parameters.RepositoryId <- Guid.NewGuid().ToString()
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
            parameters.OwnerId <- "c7fadc8b-17ee-4832-86e2-520ccc1d630d"
            parameters.OrganizationId <- "1a354bba-2973-41cd-8572-cb71d535bb52"
            parameters.RepositoryId <- "a9e5fe8a-832f-4b5d-afe5-c19773774dd6"
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
            parameters.OwnerId <- Guid.NewGuid().ToString()
            parameters.OrganizationId <- Guid.NewGuid().ToString()
            parameters.RepositoryId <- "not a Guid"
            parameters.Visibility <- "Public"
            
            let! response = Services.Client.PostAsync("/repository/setVisibility", jsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! content = response.Content.ReadAsStringAsync()
            Console.WriteLine($"{content}");
            Assert.That(content, Does.Contain("is not a valid Guid."))
        }
