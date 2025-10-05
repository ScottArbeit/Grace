namespace Grace.Server.Tests

open FSharp.Control
open FSharpPlus
open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Validation.Errors
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
open Grace.Types.Types
open System.Net.Http

[<Parallelizable(ParallelScope.All)>]
type Owner() =

    let log =
        LoggerFactory
            .Create(fun builder -> builder.AddConsole().AddDebug() |> ignore)
            .CreateLogger("Owner.Server.Tests")

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
            Assert.That(returnValue.Properties[nameof OwnerId], Is.EqualTo(ownerId))
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
            Assert.That(returnValue.Properties[nameof OwnerId], Is.EqualTo(ownerId))
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
            Assert.That(error.Error, Is.EqualTo(getErrorMessage OwnerError.InvalidOwnerType))
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
            Assert.That(error.Error, Is.EqualTo(getErrorMessage OwnerError.OwnerTypeIsRequired))
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
            Assert.That(error.Error, Is.EqualTo(getErrorMessage OwnerError.DescriptionIsTooLong))
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
            Assert.That(error.Error, Is.EqualTo(getErrorMessage OwnerError.InvalidOwnerId))
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
            Assert.That(error.Error, Is.EqualTo(getErrorMessage OwnerError.DescriptionIsRequired))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetDescriptionWithEmptyOwnerId() =
        task {
            let parameters = Parameters.Owner.SetOwnerDescriptionParameters()
            parameters.Description <- $"Description set at {getCurrentInstantGeneral ()}."
            parameters.OwnerId <- ""
            let! response = Client.PostAsync("/owner/setDescription", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! responseStream = response.Content.ReadAsStreamAsync()
            let! error = deserializeAsync<GraceError> responseStream
            Assert.That(error.Error, Is.EqualTo(getErrorMessage OwnerError.EitherOwnerIdOrOwnerNameRequired))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetSearchVisibilityToVisible() =
        task {
            let parameters = Parameters.Owner.SetOwnerSearchVisibilityParameters()
            parameters.SearchVisibility <- "Visible"
            parameters.OwnerId <- ownerId
            let! response = Client.PostAsync("/owner/setSearchVisibility", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore
            let! returnValue = deserializeContent<GraceReturnValue<string>> response
            Assert.That(returnValue.Properties[nameof OwnerId], Is.EqualTo(ownerId))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetSearchVisibilityToNotVisible() =
        task {
            let parameters = Parameters.Owner.SetOwnerSearchVisibilityParameters()
            parameters.SearchVisibility <- "NotVisible"
            parameters.OwnerId <- ownerId
            let! response = Client.PostAsync("/owner/setSearchVisibility", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore
            let! returnValue = deserializeContent<GraceReturnValue<string>> response
            Assert.That(returnValue.Properties[nameof OwnerId], Is.EqualTo(ownerId))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetSearchVisibilityToInvalidVisibility() =
        task {
            let parameters = Parameters.Owner.SetOwnerSearchVisibilityParameters()
            parameters.SearchVisibility <- "Not a valid search visibility"
            parameters.OwnerId <- ownerId
            let! response = Client.PostAsync("/owner/setSearchVisibility", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! error = deserializeContent<GraceError> response
            Assert.That(error.Error, Is.EqualTo(getErrorMessage OwnerError.InvalidSearchVisibility))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetSearchVisibilityToEmptyVisibility() =
        task {
            let parameters = Parameters.Owner.SetOwnerSearchVisibilityParameters()
            parameters.SearchVisibility <- String.Empty
            parameters.OwnerId <- ownerId
            let! response = Client.PostAsync("/owner/setSearchVisibility", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! error = deserializeContent<GraceError> response
            Assert.That(error.Error, Is.EqualTo(getErrorMessage OwnerError.SearchVisibilityIsRequired))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetSearchVisibilityWithInvalidOwnerId() =
        task {
            let parameters = Parameters.Owner.SetOwnerSearchVisibilityParameters()
            parameters.SearchVisibility <- "Visible"
            parameters.OwnerId <- "this is not an owner id"
            let! response = Client.PostAsync("/owner/setSearchVisibility", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! error = deserializeContent<GraceError> response
            Assert.That(error.Error, Is.EqualTo(getErrorMessage OwnerError.InvalidOwnerId))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetSearchVisibilityWithEmptyOwnerId() =
        task {
            let parameters = Parameters.Owner.SetOwnerSearchVisibilityParameters()
            parameters.SearchVisibility <- "Visible"
            parameters.OwnerId <- ""
            let! response = Client.PostAsync("/owner/setSearchVisibility", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! responseStream = response.Content.ReadAsStreamAsync()
            let! error = deserializeAsync<GraceError> responseStream
            Assert.That(error.Error, Is.EqualTo(getErrorMessage OwnerError.EitherOwnerIdOrOwnerNameRequired))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetNameToValidName() =
        task {
            let parameters = Parameters.Owner.SetOwnerNameParameters()
            parameters.NewName <- $"NewOwnerName{rnd.NextInt64()}"
            parameters.OwnerId <- ownerId
            let! response = Client.PostAsync("/owner/setName", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore
            let! returnValue = deserializeContent<GraceReturnValue<string>> response
            Assert.That(returnValue.Properties[nameof OwnerId], Is.EqualTo(ownerId))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetNameToInvalidName() =
        task {
            let parameters = Parameters.Owner.SetOwnerNameParameters()
            parameters.NewName <- "doesn't match Regex"
            parameters.OwnerId <- ownerId
            let! response = Client.PostAsync("/owner/setName", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! error = deserializeContent<GraceError> response
            Assert.That(error.Error, Is.EqualTo(getErrorMessage OwnerError.InvalidOwnerName))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetNameToEmptyName() =
        task {
            let parameters = Parameters.Owner.SetOwnerNameParameters()
            parameters.NewName <- ""
            parameters.OwnerId <- ownerId
            let! response = Client.PostAsync("/owner/setName", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! error = deserializeContent<GraceError> response
            Assert.That(error.Error, Is.EqualTo(getErrorMessage OwnerError.OwnerNameIsRequired))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetNameWithInvalidOwnerId() =
        task {
            let parameters = Parameters.Owner.SetOwnerNameParameters()
            parameters.NewName <- $"NewOwnerName{rnd.NextInt64()}"
            parameters.OwnerId <- "this is not an owner id"
            let! response = Client.PostAsync("/owner/setName", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! error = deserializeContent<GraceError> response
            Assert.That(error.Error, Is.EqualTo(getErrorMessage OwnerError.InvalidOwnerId))
        }

    [<Test>]
    [<Repeat(1)>]
    member public this.SetNameWithEmptyOwnerId() =
        task {
            let parameters = Parameters.Owner.SetOwnerNameParameters()
            parameters.NewName <- $"NewOwnerName{rnd.NextInt64()}"
            parameters.OwnerId <- ""
            let! response = Client.PostAsync("/owner/setName", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! error = deserializeContent<GraceError> response
            Assert.That(error.Error, Is.EqualTo(getErrorMessage OwnerError.EitherOwnerIdOrOwnerNameRequired))
        }
