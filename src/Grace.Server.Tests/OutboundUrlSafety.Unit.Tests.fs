namespace Grace.Server.Tests

open Microsoft.Extensions.Configuration
open NUnit.Framework
open System
open System.Collections.Generic
open System.Net
open System.Text
open Grace.Shared.Utilities

module OutboundUrlPolicy = Grace.Server.Security.OutboundUrlSafety
type UrlSafety = Grace.Types.Webhooks.OutboundUrlSafety
type ValidationFailure = Grace.Server.Security.OutboundUrlSafety.ValidationFailure
type ValidationRequest = Grace.Server.Security.OutboundUrlSafety.ValidationRequest
type ValidatedOutboundUrl = Grace.Server.Security.OutboundUrlSafety.ValidatedOutboundUrl

[<Parallelizable(ParallelScope.All)>]
type OutboundUrlSafetyUnit() =

    let configuration (values: (string * string) list) =
        let pairs =
            values
            |> List.map (fun (key, value) -> KeyValuePair<string, string>(getConfigKey key, value))

        ConfigurationBuilder()
            .AddInMemoryCollection(pairs)
            .Build()
        :> IConfiguration

    let emptyConfiguration = configuration []

    let assertAccepted (result: Result<ValidatedOutboundUrl, ValidationFailure>) =
        match result with
        | Ok value -> value
        | Error failure -> raise (AssertionException(sprintf "Expected URL to be accepted but got %A." failure))

    let assertRejected (expected: ValidationFailure) (result: Result<ValidatedOutboundUrl, ValidationFailure>) =
        match result with
        | Ok value -> raise (AssertionException(sprintf "Expected URL to be rejected with %A but got %s." expected value.ScopedUrl.Url))
        | Error failure -> Assert.That(failure, Is.EqualTo(expected))

    let publicRequest url = ValidationRequest.PublicHttps url

    let localRequest url : ValidationRequest = { Url = url; RequestedSafety = UrlSafety.LocalUnsafeDevOnly; AcknowledgeUnsafeLocalDevelopment = true }

    let publicResolver host =
        match host with
        | "hooks.example.test" -> [| IPAddress.Parse("93.184.216.34") |]
        | "private.example.test" -> [| IPAddress.Parse("10.1.2.3") |]
        | "metadata.example.test" -> [| IPAddress.Parse("169.254.169.254") |]
        | _ -> [| IPAddress.Parse("93.184.216.34") |]

    let validatePublic request = OutboundUrlPolicy.validateWithResolver publicResolver emptyConfiguration request

    [<Test>]
    member _.PublicTargetsRequireAbsoluteHttps() =
        let validated =
            validatePublic (publicRequest "https://hooks.example.test/events")
            |> assertAccepted

        Assert.That(validated.ScopedUrl.Safety, Is.EqualTo(UrlSafety.PublicHttps))
        Assert.That(validated.ScopedUrl.Url, Is.EqualTo("https://hooks.example.test/events"))
        Assert.That(validated.RedirectPolicy, Is.EqualTo(OutboundUrlPolicy.RevalidateEveryRedirect))

        validatePublic (publicRequest "http://hooks.example.test/events")
        |> assertRejected ValidationFailure.HttpsRequired

        validatePublic (publicRequest "/relative")
        |> assertRejected ValidationFailure.InvalidUri

    [<Test>]
    member _.RejectsUnsupportedSchemesAndEmbeddedCredentials() =
        validatePublic (publicRequest "ftp://hooks.example.test/events")
        |> assertRejected (ValidationFailure.UnsupportedScheme "ftp")

        validatePublic (publicRequest "https://user:secret@hooks.example.test/events")
        |> assertRejected ValidationFailure.EmbeddedCredentialsRejected

    [<Test>]
    member _.RejectsLocalhostLoopbackAndPrivateTargetsByDefault() =
        let rejectedUrls =
            [
                "https://localhost/events"
                "https://127.0.0.1/events"
                "https://[::1]/events"
                "https://10.1.2.3/events"
                "https://172.16.0.1/events"
                "https://192.168.1.20/events"
                "https://169.254.169.254/metadata"
                "https://[::ffff:127.0.0.1]/events"
                "https://224.0.0.1/events"
                "https://0.0.0.0/events"
                "https://[fc00::1]/events"
                "https://[fe80::1]/events"
                "https://[ff02::1]/events"
            ]

        for url in rejectedUrls do
            match OutboundUrlPolicy.validate emptyConfiguration (publicRequest url) with
            | Error (ValidationFailure.UnsafeHostRejected _) -> ()
            | Error failure -> Assert.Fail(sprintf "Expected %s to be rejected as an unsafe host but got %A." url failure)
            | Ok value -> Assert.Fail($"Expected {url} to be rejected but got {value.ScopedUrl.Url}.")

    [<Test>]
    member _.UnsafeLocalDevelopmentRequiresServerOptInAndPerCommandAcknowledgement() =
        let config = configuration [ OutboundUrlPolicy.UnsafeLocalDevelopmentConfigKey, "true" ]

        OutboundUrlPolicy.validate emptyConfiguration (localRequest "http://localhost:5000/webhook")
        |> assertRejected ValidationFailure.LocalTargetRequiresDevelopmentAcknowledgement

        let missingAcknowledgement: ValidationRequest =
            { Url = "http://localhost:5000/webhook"; RequestedSafety = UrlSafety.LocalUnsafeDevOnly; AcknowledgeUnsafeLocalDevelopment = false }

        OutboundUrlPolicy.validate config missingAcknowledgement
        |> assertRejected ValidationFailure.LocalTargetRequiresDevelopmentAcknowledgement

        let localhost =
            OutboundUrlPolicy.validate config (localRequest "http://localhost:5000/webhook")
            |> assertAccepted

        let ipv4 =
            OutboundUrlPolicy.validate config (localRequest "http://127.0.0.1:5000/webhook")
            |> assertAccepted

        let ipv6 =
            OutboundUrlPolicy.validate config (localRequest "http://[::1]:5000/webhook")
            |> assertAccepted

        Assert.That(localhost.ScopedUrl.Safety, Is.EqualTo(UrlSafety.LocalUnsafeDevOnly))
        Assert.That(ipv4.ScopedUrl.Safety, Is.EqualTo(UrlSafety.LocalUnsafeDevOnly))
        Assert.That(ipv6.ScopedUrl.Safety, Is.EqualTo(UrlSafety.LocalUnsafeDevOnly))

    [<Test>]
    member _.UnsafeLocalDevelopmentStillRejectsPrivateAndMetadataTargets() =
        let config = configuration [ OutboundUrlPolicy.UnsafeLocalDevelopmentConfigKey, "true" ]

        for url in
            [
                "http://10.0.0.1/webhook"
                "http://127.0.0.2/webhook"
                "http://192.168.0.10/webhook"
                "http://169.254.169.254/metadata"
            ] do
            match OutboundUrlPolicy.validate config (localRequest url) with
            | Error (ValidationFailure.UnsafeHostRejected _) -> ()
            | Error failure -> Assert.Fail(sprintf "Expected %s to be rejected as an unsafe host but got %A." url failure)
            | Ok value -> Assert.Fail($"Expected {url} to be rejected but got {value.ScopedUrl.Url}.")

    [<Test>]
    member _.PublicHostnamesRejectUnsafeResolvedAddresses() =
        OutboundUrlPolicy.validateWithResolver publicResolver emptyConfiguration (publicRequest "https://private.example.test/events")
        |> assertRejected (ValidationFailure.UnsafeHostRejected "10.1.2.3")

        OutboundUrlPolicy.validateWithResolver publicResolver emptyConfiguration (publicRequest "https://metadata.example.test/events")
        |> assertRejected (ValidationFailure.UnsafeHostRejected "169.254.169.254")

    [<Test>]
    member _.IPv4MappedIPv6LoopbackLiteralIsRejected() =
        OutboundUrlPolicy.validate emptyConfiguration (publicRequest "https://[::ffff:127.0.0.1]/events")
        |> assertRejected (ValidationFailure.UnsafeHostRejected "127.0.0.1")

    [<Test>]
    member _.RedirectsRequireRevalidation() =
        let original =
            validatePublic (publicRequest "https://hooks.example.test/events")
            |> assertAccepted

        OutboundUrlPolicy.validateRedirect emptyConfiguration original (Uri("https://169.254.169.254/metadata"))
        |> function
            | Error (ValidationFailure.UnsafeHostRejected _) -> ()
            | Error failure -> Assert.Fail(sprintf "Expected redirect to metadata IP to be rejected but got %A." failure)
            | Ok value -> Assert.Fail($"Expected redirect to be rejected but got {value.ScopedUrl.Url}.")

    [<Test>]
    member _.RedactsUserInfoAndSensitiveQueryValues() =
        let redacted = OutboundUrlPolicy.Redaction.redactUri "https://user:secret@hooks.example.test/path?sig=abc&keep=value&access_token=token&nested=key"

        Assert.That(redacted, Is.EqualTo("https://REDACTED@hooks.example.test/path?sig=REDACTED&keep=value&access_token=REDACTED&nested=key"))

    [<Test>]
    member _.MalformedSecretBearingUrlsAreNotReturnedDuringRedaction() =
        let malformed = "https://[hooks.example.test/path?sig=abc&token=secret"
        let redacted = OutboundUrlPolicy.Redaction.redactUri malformed

        Assert.That(redacted, Is.EqualTo("[invalid-uri-redacted]"))
        Assert.That(redacted, Does.Not.Contain("abc"))
        Assert.That(redacted, Does.Not.Contain("secret"))

    [<Test>]
    member _.SigningInputIncludesIdentityTimestampKeyAndPayloadHash() =
        let payload = Encoding.UTF8.GetBytes("""{"event":"promotion-set.applied"}""")
        let signingInput = OutboundUrlPolicy.Signing.createSigningInput "delivery-1" "2026-06-02T12:00:00Z" "secret-v3" payload
        let signedMaterial = Encoding.UTF8.GetString(signingInput.SignedMaterial)

        Assert.That(signingInput.RequestId, Is.EqualTo("delivery-1"))
        Assert.That(signingInput.Timestamp, Is.EqualTo("2026-06-02T12:00:00Z"))
        Assert.That(signingInput.KeyId, Is.EqualTo("secret-v3"))
        Assert.That(signingInput.PayloadSha256Hex, Has.Length.EqualTo(64))
        Assert.That(signedMaterial, Does.StartWith("delivery-1.2026-06-02T12:00:00Z.secret-v3."))
        Assert.That(signedMaterial, Does.Not.Contain("{\"event\""))
