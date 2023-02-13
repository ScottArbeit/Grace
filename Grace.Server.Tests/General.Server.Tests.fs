namespace Grace.Server.Tests

open Microsoft.AspNetCore.Hosting
open Grace.Shared
open Grace.Shared.Utilities
open Microsoft.AspNetCore.Mvc.Testing
open NUnit.Framework
open System
open System.Net.Http.Json

module Common =    
    let okResult: Result<unit, string> = Result.Ok () 
    let errorResult: Result<unit, string> = Result.Error "error"

module Services =
    let public Factory = (new WebApplicationFactory<Grace.Server.Application.Startup>()).WithWebHostBuilder(fun builder -> 
        builder.UseEnvironment("Development")
            .ConfigureKestrel(fun options ->
                options.ListenLocalhost(5000)
            ) |> ignore
        ()
    )
    let clientOptions = WebApplicationFactoryClientOptions()
    clientOptions.BaseAddress <- new Uri("http://localhost:5000")
    let public Client = Factory.CreateClient(clientOptions);
    
[<Parallelizable>]
type General() =

    [<Test>]
    member public this.RootPathReturnsValue() =
        task {
            let! response = Services.Client.GetAsync("/")
            let! content = response.Content.ReadAsStringAsync()
            Console.WriteLine($"{content}");
            Assert.That(content, Does.Contain("Grace"))
        }
