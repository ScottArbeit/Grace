namespace Grace.Server.Tests

open FSharp.Control
open FSharpPlus
open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Validation.Errors.Owner
open Grace.Shared.Utilities
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

[<Parallelizable(ParallelScope.All)>]
type Owner() =

    let log = LoggerFactory.Create(fun builder -> builder.AddConsole().AddDebug() |> ignore).CreateLogger("Owner.Server.Tests")

    member val public TestContext = TestContext.CurrentContext with get, set

    [<Test>]
    [<Repeat(1)>]
    member public this.SetDescriptionWithValidValues() =
        task {
            let parameters = Parameters.Owner.SetOwnerDescriptionParameters()

            parameters.Description <- $"Description set at {getCurrentInstantGeneral ()}."
            parameters.OwnerId <- ownerId

            let! response = Client.PostAsync("/owner/setDescription", createJsonContent parameters)
            let! content = response.Content.ReadAsStringAsync()
            response.EnsureSuccessStatusCode() |> ignore
            Assert.That(content.Length, Is.GreaterThan(0))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetTypeToPublic() =
        task {
            let parameters = Parameters.Owner.SetOwnerTypeParameters()
            parameters.OwnerType <- "Public"
            parameters.OwnerId <- ownerId
            let! response = Client.PostAsync("/owner/setType", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore
            let! returnValue = deserializeContent<GraceReturnValue<string>> response
            Assert.That(returnValue.Properties[nameof (OwnerId)], Is.EqualTo(ownerId))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetTypeToPrivate() =
        task {
            let parameters = Parameters.Owner.SetOwnerTypeParameters()
            parameters.OwnerType <- "Private"
            parameters.OwnerId <- ownerId
            let! response = Client.PostAsync("/owner/setType", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore
            let! returnValue = deserializeContent<GraceReturnValue<string>> response
            Assert.That(returnValue.Properties[nameof (OwnerId)], Is.EqualTo(ownerId))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetTypeToInvalidType() =
        task {
            let parameters = Parameters.Owner.SetOwnerTypeParameters()
            parameters.OwnerType <- "Not a valid owner type"
            parameters.OwnerId <- ownerId
            let! response = Client.PostAsync("/owner/setType", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! error = deserializeContent<GraceError> response
            Assert.That(error.Error, Is.EqualTo(OwnerError.getErrorMessage InvalidOwnerType))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetTypeToEmptyType() =
        task {
            let parameters = Parameters.Owner.SetOwnerTypeParameters()
            parameters.OwnerType <- String.Empty
            parameters.OwnerId <- ownerId
            let! response = Client.PostAsync("/owner/setType", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! error = deserializeContent<GraceError> response
            Assert.That(error.Error, Is.EqualTo(OwnerError.getErrorMessage OwnerTypeIsRequired))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetDescriptionWithInvalidDescription() =
        task {
            let parameters = Parameters.Owner.SetOwnerDescriptionParameters()
            parameters.Description <- "a".PadRight(2049, 'a')
            parameters.OwnerId <- ownerId
            let! response = Client.PostAsync("/owner/setDescription", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! error = deserializeContent<GraceError> response
            Assert.That(error.Error, Is.EqualTo(OwnerError.getErrorMessage DescriptionIsTooLong))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetDescriptionWithInvalidOwnerId() =
        task {
            let parameters = Parameters.Owner.SetOwnerDescriptionParameters()

            parameters.Description <- $"Description set at {getCurrentInstantGeneral ()}."
            parameters.OwnerId <- "this is not an owner id"

            let! response = Client.PostAsync("/owner/setDescription", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! error = deserializeContent<GraceError> response
            Assert.That(error.Error, Is.EqualTo(OwnerError.getErrorMessage OwnerError.InvalidOwnerId))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetDescriptionWithEmptyDescription() =
        task {
            let parameters = Parameters.Owner.SetOwnerDescriptionParameters()

            parameters.Description <- ""
            parameters.OwnerId <- ownerId

            let! response = Client.PostAsync("/owner/setDescription", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! responseStream = response.Content.ReadAsStreamAsync()
            let! error = deserializeAsync<GraceError> responseStream
            Assert.That(error.Error, Is.EqualTo(OwnerError.getErrorMessage DescriptionIsRequired))
        }

