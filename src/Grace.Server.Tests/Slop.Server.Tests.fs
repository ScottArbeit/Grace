namespace Grace.Server.Tests

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Types
open Grace.Types.Owner
open NUnit.Framework
open System
open System.Net.Http
open System.Text.Json

[<TestFixture>]
type Slop() =
    // Slop guard: if correlation middleware is removed or renamed, this breaks.
    [<Test; Category("Slop")>]
    member _.OwnerGetEchoesCorrelationIdHeader() =
        task {
            let correlationId = generateCorrelationId ()
            let parameters = Parameters.Owner.GetOwnerParameters()
            parameters.OwnerId <- Services.ownerId
            parameters.CorrelationId <- correlationId

            use request = new HttpRequestMessage(HttpMethod.Post, "/owner/get")
            request.Headers.Add(Constants.CorrelationIdHeaderKey, correlationId)
            request.Content <- createJsonContent parameters

            let! response = Services.Client.SendAsync(request)
            response.EnsureSuccessStatusCode() |> ignore

            Assert.That(response.Headers.Contains(Constants.CorrelationIdHeaderKey), Is.True)

            let echoed =
                response.Headers.GetValues(Constants.CorrelationIdHeaderKey)
                |> Seq.head

            Assert.That(echoed, Is.EqualTo(correlationId))
        }

    // Slop guard: if OwnerDto.Class or response shape changes, this breaks.
    [<Test; Category("Slop")>]
    member _.OwnerGetReturnsOwnerDtoClass() =
        task {
            let parameters = Parameters.Owner.GetOwnerParameters()
            parameters.OwnerId <- Services.ownerId
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Services.Client.PostAsync("/owner/get", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore

            let! payload = deserializeContent<GraceReturnValue<OwnerDto>> response
            Assert.That(payload.ReturnValue.Class, Is.EqualTo(nameof OwnerDto))
        }

    // Slop guard: if JSON serialization options change, round-tripping breaks.
    [<Test; Category("Slop")>]
    member _.OwnerDtoJsonRoundTrip() =
        let original = { OwnerDto.Default with OwnerId = Guid.NewGuid(); OwnerName = "SlopOwner" }

        let json = JsonSerializer.Serialize(original, Constants.JsonSerializerOptions)
        let roundTrip = JsonSerializer.Deserialize<OwnerDto>(json, Constants.JsonSerializerOptions)
        Assert.That(roundTrip, Is.EqualTo(original))
