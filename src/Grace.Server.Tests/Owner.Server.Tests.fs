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
open Grace.Types.Common
open Grace.Types
open System.Net.Http

/// Captures owner values used by the test suite.
[<Parallelizable(ParallelScope.All)>]
type Owner() =

    let log =
        LoggerFactory
            .Create(fun builder -> builder.AddConsole().AddDebug() |> ignore)
            .CreateLogger("Owner.Server.Tests")

    /// Builds a deterministic owner for integration setup fixture for the server integration owner assertions.
    let createOwnerAsync () =
        task {
            let createdOwnerId = $"{Guid.NewGuid()}"
            let parameters = Parameters.Owner.CreateOwnerParameters()
            parameters.OwnerId <- createdOwnerId
            parameters.OwnerName <- $"AssertionOwner{Guid.NewGuid():N}"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/owner/create", createJsonContent parameters)
            let! responseText = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), responseText)
            return createdOwnerId
        }

    /// Gets owner from the running test server.
    let getOwnerAsync ownerId =
        task {
            let parameters = Parameters.Owner.GetOwnerParameters()
            parameters.OwnerId <- ownerId
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/owner/get", createJsonContent parameters)
            let! responseText = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), responseText)
            Assert.That(response.Content.Headers.ContentType.MediaType, Is.EqualTo("application/json"))

            let returnValue = deserialize<GraceReturnValue<Owner.OwnerDto>> responseText
            return returnValue.ReturnValue
        }

    /// Exposes test context for test diagnostics.
    member val public TestContext = TestContext.CurrentContext with get, set

    /// Verifies the set description with valid values scenario.
    [<Test>]
    [<Repeat(1)>]
    member public this.SetDescriptionWithValidValues() =
        task {
            let! createdOwnerId = createOwnerAsync ()
            let parameters = Parameters.Owner.SetOwnerDescriptionParameters()
            let expectedDescription = $"Description set at {getCurrentInstantGeneral ()}."

            parameters.Description <- expectedDescription
            parameters.OwnerId <- createdOwnerId
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/owner/setDescription", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore
            let! returnValue = deserializeContent<GraceReturnValue<string>> response
            Assert.That(returnValue.CorrelationId, Is.Not.Empty)

            let! stored = getOwnerAsync createdOwnerId
            Assert.That(stored.OwnerId, Is.EqualTo(Guid.Parse(createdOwnerId)))
            Assert.That(stored.Description, Is.EqualTo(expectedDescription))
        }

    /// Verifies the set type to public scenario.
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
            let ownerGuid = Common.requireGuidProperty (nameof OwnerId) returnValue.Properties[nameof OwnerId]
            Assert.That(ownerGuid, Is.EqualTo(Guid.Parse(ownerId)))
        }

    /// Verifies the set type to private scenario.
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
            let ownerGuid = Common.requireGuidProperty (nameof OwnerId) returnValue.Properties[nameof OwnerId]
            Assert.That(ownerGuid, Is.EqualTo(Guid.Parse(ownerId)))
        }

    /// Verifies the set type to invalid type scenario.
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

    /// Verifies the set type to empty type scenario.
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

    /// Verifies the set description with invalid description scenario.
    [<Test>]
    [<Repeat(1)>]
    member public this.SetDescriptionWithInvalidDescription() =
        task {
            let! createdOwnerId = createOwnerAsync ()
            let baseline = "Owner description should survive invalid update."

            let validParameters = Parameters.Owner.SetOwnerDescriptionParameters()
            validParameters.Description <- baseline
            validParameters.OwnerId <- createdOwnerId
            validParameters.CorrelationId <- generateCorrelationId ()

            let! validResponse = Client.PostAsync("/owner/setDescription", createJsonContent validParameters)
            validResponse.EnsureSuccessStatusCode() |> ignore

            let parameters = Parameters.Owner.SetOwnerDescriptionParameters()
            parameters.Description <- "a".PadRight(2049, 'a')
            parameters.OwnerId <- createdOwnerId
            parameters.CorrelationId <- generateCorrelationId ()
            let! response = Client.PostAsync("/owner/setDescription", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
            let! error = deserializeContent<GraceError> response
            Assert.That(error.Error, Is.EqualTo(getErrorMessage OwnerError.DescriptionIsTooLong))
            Assert.That(error.CorrelationId, Is.Not.Empty)

            let! stored = getOwnerAsync createdOwnerId
            Assert.That(stored.Description, Is.EqualTo(baseline))
        }

    /// Verifies the set description with invalid owner ID scenario.
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

    /// Verifies the set description with empty description scenario.
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

    /// Verifies the set description with empty owner ID scenario.
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

    /// Verifies the set search visibility to visible scenario.
    [<Test>]
    [<Repeat(1)>]
    member public this.SetSearchVisibilityToVisible() =
        task {
            let! createdOwnerId = createOwnerAsync ()
            let parameters = Parameters.Owner.SetOwnerSearchVisibilityParameters()
            parameters.SearchVisibility <- "Visible"
            parameters.OwnerId <- createdOwnerId
            parameters.CorrelationId <- generateCorrelationId ()
            let! response = Client.PostAsync("/owner/setSearchVisibility", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore
            let! returnValue = deserializeContent<GraceReturnValue<string>> response
            let ownerGuid = Common.requireGuidProperty (nameof OwnerId) returnValue.Properties[nameof OwnerId]
            Assert.That(ownerGuid, Is.EqualTo(Guid.Parse(createdOwnerId)))

            let! stored = getOwnerAsync createdOwnerId
            Assert.That(stored.SearchVisibility, Is.EqualTo(SearchVisibility.Visible))
        }

    /// Verifies the set search visibility to not visible scenario.
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
            let ownerGuid = Common.requireGuidProperty (nameof OwnerId) returnValue.Properties[nameof OwnerId]
            Assert.That(ownerGuid, Is.EqualTo(Guid.Parse(ownerId)))
        }

    /// Verifies the set search visibility to invalid visibility scenario.
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

    /// Verifies the set search visibility to empty visibility scenario.
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

    /// Verifies the set search visibility with invalid owner ID scenario.
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

    /// Verifies the set search visibility with empty owner ID scenario.
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

    /// Verifies the set name to valid name scenario.
    [<Test>]
    [<Repeat(1)>]
    member public this.SetNameToValidName() =
        task {
            let! createdOwnerId = createOwnerAsync ()
            let parameters = Parameters.Owner.SetOwnerNameParameters()
            parameters.NewName <- $"NewOwnerName{rnd.NextInt64()}"
            parameters.OwnerId <- createdOwnerId
            parameters.CorrelationId <- generateCorrelationId ()
            let! response = Client.PostAsync("/owner/setName", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore
            let! returnValue = deserializeContent<GraceReturnValue<string>> response
            let ownerGuid = Common.requireGuidProperty (nameof OwnerId) returnValue.Properties[nameof OwnerId]
            Assert.That(ownerGuid, Is.EqualTo(Guid.Parse(createdOwnerId)))

            let! stored = getOwnerAsync createdOwnerId
            Assert.That(stored.OwnerName, Is.EqualTo(parameters.NewName))
        }

    /// Verifies the set name to invalid name scenario.
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

    /// Verifies the set name to empty name scenario.
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

    /// Verifies the set name with invalid owner ID scenario.
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

    /// Verifies the set name with empty owner ID scenario.
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
