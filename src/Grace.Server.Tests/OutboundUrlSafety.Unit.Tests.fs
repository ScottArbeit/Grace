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
        | "mapped-public.example.test" -> [| IPAddress.Parse("::ffff:8.8.8.8") |]
        | _ -> [| IPAddress.Parse("93.184.216.34") |]

    let developmentConfiguration = configuration [ "ASPNETCORE_ENVIRONMENT", "Development" ]

    let unsafeLocalDevelopmentConfiguration =
        configuration [ "ASPNETCORE_ENVIRONMENT", "Development"
                        OutboundUrlPolicy.UnsafeLocalDevelopmentConfigKey, "true" ]

    let validateWithHostEnvironment isDevelopmentHostEnvironment configuration request =
        OutboundUrlPolicy.validateWithResolver publicResolver isDevelopmentHostEnvironment configuration request

    let validateOutsideDevelopment configuration request = validateWithHostEnvironment false configuration request

    let validateInDevelopment configuration request = validateWithHostEnvironment true configuration request

    let validatePublic request = validateOutsideDevelopment emptyConfiguration request

    let addressList (addresses: IPAddress array) =
        addresses
        |> Array.map (fun address -> address.ToString())
        |> String.concat ","

    [<Test>]
    member _.PublicTargetsRequireAbsoluteHttps() =
        let validated =
            validatePublic (publicRequest "https://hooks.example.test/events")
            |> assertAccepted

        Assert.That(validated.ScopedUrl.Safety, Is.EqualTo(UrlSafety.PublicHttps))
        Assert.That(validated.ScopedUrl.Url, Is.EqualTo("https://hooks.example.test/events"))
        Assert.That(validated.RedirectPolicy, Is.EqualTo(OutboundUrlPolicy.RevalidateEveryRedirect))
        Assert.That(addressList validated.ResolvedAddresses, Is.EqualTo("93.184.216.34"))

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
    member _.RejectsFragmentsBeforePersistingScopedUrls() =
        validatePublic (publicRequest "https://hooks.example.test/events#access_token=fragment-secret")
        |> assertRejected ValidationFailure.FragmentRejected

        validatePublic (publicRequest "https://hooks.example.test/events?keep=value#nonce=fragment-secret")
        |> assertRejected ValidationFailure.FragmentRejected

        let validated =
            validatePublic (publicRequest "https://hooks.example.test/events?keep=value")
            |> assertAccepted

        Assert.That(validated.ScopedUrl.Url, Does.Not.Contain("#"))

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
                "https://192.88.99.1/events"
                "https://[::ffff:127.0.0.1]/events"
                "https://[::ffff:8.8.8.8]/events"
                "https://224.0.0.1/events"
                "https://0.0.0.0/events"
                "https://[100::1]/events"
                "https://[100:0:0:1::1]/events"
                "https://[64:ff9b:1::1]/events"
                "https://[2001::1]/events"
                "https://[2001:2::1]/events"
                "https://[2001:db8::1]/events"
                "https://[2002::1]/events"
                "https://[3fff::1]/events"
                "https://[5f00::1]/events"
                "https://[fc00::1]/events"
                "https://[fe80::1]/events"
                "https://[ff02::1]/events"
            ]

        for url in rejectedUrls do
            match validateOutsideDevelopment emptyConfiguration (publicRequest url) with
            | Error (ValidationFailure.UnsafeHostRejected _) -> ()
            | Error failure -> Assert.Fail(sprintf "Expected %s to be rejected as an unsafe host but got %A." url failure)
            | Ok value -> Assert.Fail($"Expected {url} to be rejected but got {value.ScopedUrl.Url}.")

    [<Test>]
    member _.UnsafeLocalDevelopmentRequiresServerOptInAndPerCommandAcknowledgement() =
        validateInDevelopment emptyConfiguration (localRequest "http://localhost:5000/webhook")
        |> assertRejected ValidationFailure.LocalTargetRequiresDevelopmentAcknowledgement

        let missingAcknowledgement: ValidationRequest =
            { Url = "http://localhost:5000/webhook"; RequestedSafety = UrlSafety.LocalUnsafeDevOnly; AcknowledgeUnsafeLocalDevelopment = false }

        validateInDevelopment unsafeLocalDevelopmentConfiguration missingAcknowledgement
        |> assertRejected ValidationFailure.LocalTargetRequiresDevelopmentAcknowledgement

        validateInDevelopment developmentConfiguration (localRequest "http://localhost:5000/webhook")
        |> assertRejected ValidationFailure.LocalTargetRequiresDevelopmentAcknowledgement

        let localhost =
            validateInDevelopment unsafeLocalDevelopmentConfiguration (localRequest "http://localhost:5000/webhook")
            |> assertAccepted

        let ipv4 =
            validateInDevelopment unsafeLocalDevelopmentConfiguration (localRequest "http://127.0.0.1:5000/webhook")
            |> assertAccepted

        let ipv6 =
            validateInDevelopment unsafeLocalDevelopmentConfiguration (localRequest "http://[::1]:5000/webhook")
            |> assertAccepted

        Assert.That(localhost.ScopedUrl.Safety, Is.EqualTo(UrlSafety.LocalUnsafeDevOnly))
        Assert.That(ipv4.ScopedUrl.Safety, Is.EqualTo(UrlSafety.LocalUnsafeDevOnly))
        Assert.That(ipv6.ScopedUrl.Safety, Is.EqualTo(UrlSafety.LocalUnsafeDevOnly))

        Assert.That(addressList localhost.ResolvedAddresses, Is.EqualTo("127.0.0.1,::1"))

        Assert.That(addressList ipv4.ResolvedAddresses, Is.EqualTo("127.0.0.1"))
        Assert.That(addressList ipv6.ResolvedAddresses, Is.EqualTo("::1"))

    [<Test>]
    member _.UnsafeLocalDevelopmentConfigAndAcknowledgementAreInsufficientWithoutDevelopmentHostEnvironment() =
        let productionConfig =
            configuration [ "ASPNETCORE_ENVIRONMENT", "Production"
                            OutboundUrlPolicy.UnsafeLocalDevelopmentConfigKey, "true" ]

        let mutableDevelopmentConfig =
            configuration [ "ASPNETCORE_ENVIRONMENT", "Development"
                            "DOTNET_ENVIRONMENT", "Development"
                            OutboundUrlPolicy.UnsafeLocalDevelopmentConfigKey, "true" ]

        validateOutsideDevelopment productionConfig (localRequest "http://localhost:5000/webhook")
        |> assertRejected ValidationFailure.LocalTargetRequiresDevelopmentAcknowledgement

        validateOutsideDevelopment mutableDevelopmentConfig (localRequest "http://127.0.0.1:5000/webhook")
        |> assertRejected ValidationFailure.LocalTargetRequiresDevelopmentAcknowledgement

        validateInDevelopment mutableDevelopmentConfig (localRequest "http://127.0.0.1:5000/webhook")
        |> assertAccepted
        |> ignore

    [<Test>]
    member _.UnsafeLocalDevelopmentStillRejectsPrivateAndMetadataTargets() =
        for url in
            [
                "http://10.0.0.1/webhook"
                "http://127.0.0.2/webhook"
                "http://192.168.0.10/webhook"
                "http://169.254.169.254/metadata"
            ] do
            match validateInDevelopment unsafeLocalDevelopmentConfiguration (localRequest url) with
            | Error (ValidationFailure.UnsafeHostRejected _) -> ()
            | Error failure -> Assert.Fail(sprintf "Expected %s to be rejected as an unsafe host but got %A." url failure)
            | Ok value -> Assert.Fail($"Expected {url} to be rejected but got {value.ScopedUrl.Url}.")

    [<Test>]
    member _.PublicHostnamesRejectUnsafeResolvedAddresses() =
        validateOutsideDevelopment emptyConfiguration (publicRequest "https://private.example.test/events")
        |> assertRejected (ValidationFailure.UnsafeHostRejected "10.1.2.3")

        validateOutsideDevelopment emptyConfiguration (publicRequest "https://metadata.example.test/events")
        |> assertRejected (ValidationFailure.UnsafeHostRejected "169.254.169.254")

        validateOutsideDevelopment emptyConfiguration (publicRequest "https://mapped-public.example.test/events")
        |> assertRejected (ValidationFailure.UnsafeHostRejected "8.8.8.8")

    [<Test>]
    member _.PublicHostnamesCarryResolvedAddressesForAddressPinning() =
        let resolver host =
            match host with
            | "multi.example.test" ->
                [|
                    IPAddress.Parse("93.184.216.34")
                    IPAddress.Parse("2606:2800:220:1:248:1893:25c8:1946")
                |]
            | _ -> [||]

        let validated =
            OutboundUrlPolicy.validateWithResolver resolver false emptyConfiguration (publicRequest "https://multi.example.test/events")
            |> assertAccepted

        Assert.That(addressList validated.ResolvedAddresses, Is.EqualTo("93.184.216.34,2606:2800:220:1:248:1893:25c8:1946"))

    [<Test>]
    member _.IPv4MappedIPv6LoopbackLiteralIsRejected() =
        validateOutsideDevelopment emptyConfiguration (publicRequest "https://[::ffff:127.0.0.1]/events")
        |> assertRejected (ValidationFailure.UnsafeHostRejected "127.0.0.1")

    [<Test>]
    member _.WellKnownNat64IPv6LiteralIsRejected() =
        validateOutsideDevelopment emptyConfiguration (publicRequest "https://[64:ff9b::7f00:1]/events")
        |> assertRejected (ValidationFailure.UnsafeHostRejected "64:ff9b::7f00:1")

    [<Test>]
    member _.DeprecatedOrchidIPv6LiteralIsRejected() =
        validateOutsideDevelopment emptyConfiguration (publicRequest "https://[2001:10::1]/events")
        |> assertRejected (ValidationFailure.UnsafeHostRejected "2001:10::1")

    [<Test>]
    member _.DeprecatedSiteLocalIPv6LiteralIsRejected() =
        validateOutsideDevelopment emptyConfiguration (publicRequest "https://[fec0::1]/events")
        |> assertRejected (ValidationFailure.UnsafeHostRejected "fec0::1")

    [<Test>]
    member _.RedirectsRequireRevalidation() =
        let original =
            validatePublic (publicRequest "https://hooks.example.test/events")
            |> assertAccepted

        OutboundUrlPolicy.validateRedirect null emptyConfiguration original (Uri("https://169.254.169.254/metadata"))
        |> function
            | Error (ValidationFailure.UnsafeHostRejected _) -> ()
            | Error failure -> Assert.Fail(sprintf "Expected redirect to metadata IP to be rejected but got %A." failure)
            | Ok value -> Assert.Fail($"Expected redirect to be rejected but got {value.ScopedUrl.Url}.")

    [<Test>]
    member _.RelativeRedirectsAreRejectedWithoutDereferencingAbsoluteUri() =
        let original =
            validatePublic (publicRequest "https://hooks.example.test/events")
            |> assertAccepted

        OutboundUrlPolicy.validateRedirect null emptyConfiguration original (Uri("/metadata", UriKind.Relative))
        |> assertRejected ValidationFailure.InvalidUri

    [<Test>]
    member _.RedactsUserInfoAndSensitiveQueryValues() =
        let redacted =
            OutboundUrlPolicy.Redaction.redactUri
                "https://user:secret@hooks.example.test/path?sig=abc&keep=value&access_token=token&nested=key&api_key=k&client_secret=s&X-Amz-Signature=aws&SharedAccessSignature=sas&sv=2024&sp=r&sr=b&se=tomorrow&skoid=o&sktid=t&skt=st&ske=se&sks=b&skv=v"

        Assert.That(
            redacted,
            Is.EqualTo(
                "https://REDACTED@hooks.example.test/[redacted-path]?sig=REDACTED&keep=value&access_token=REDACTED&nested=key&api_key=REDACTED&client_secret=REDACTED&X-Amz-Signature=REDACTED&SharedAccessSignature=REDACTED&sv=REDACTED&sp=REDACTED&sr=REDACTED&se=REDACTED&skoid=REDACTED&sktid=REDACTED&skt=REDACTED&ske=REDACTED&sks=REDACTED&skv=REDACTED"
            )
        )

    [<Test>]
    member _.RedactsTokenAndCredentialQueryNamesCaseInsensitively() =
        let redacted =
            OutboundUrlPolicy.Redaction.redactUri
                "https://hooks.example.test/path?refresh_token=refresh&id_token=id&id_token_hint=hint&State=state&NONCE=nonce&SAMLResponse=saml&RelayState=relay&X-Amz-Credential=credential&X-Amz-Security-Token=session&Api_Key=key&oauth_token=oauth&x-api-key=header"

        Assert.That(
            redacted,
            Is.EqualTo(
                "https://hooks.example.test/[redacted-path]?refresh_token=REDACTED&id_token=REDACTED&id_token_hint=REDACTED&State=REDACTED&NONCE=REDACTED&SAMLResponse=REDACTED&RelayState=REDACTED&X-Amz-Credential=REDACTED&X-Amz-Security-Token=REDACTED&Api_Key=REDACTED&oauth_token=REDACTED&x-api-key=REDACTED"
            )
        )

    [<Test>]
    member _.RedactsJwtBearerOAuthAssertionQueryNamesCaseInsensitively() =
        let redacted =
            OutboundUrlPolicy.Redaction.redactUri
                "https://hooks.example.test/token?client_assertion=header.payload.signature&ASSERTION=jwt-bearer-material&keep=value"

        Assert.That(redacted, Is.EqualTo("https://hooks.example.test/[redacted-path]?client_assertion=REDACTED&ASSERTION=REDACTED&keep=value"))

        Assert.That(redacted, Does.Not.Contain("header.payload.signature"))
        Assert.That(redacted, Does.Not.Contain("jwt-bearer-material"))

    [<Test>]
    member _.RedactsGoogleCloudSignedUrlQueryNamesCaseInsensitively() =
        let redacted =
            OutboundUrlPolicy.Redaction.redactUri
                "https://storage.googleapis.com/bucket/object?X-Goog-Signature=sig&x-goog-credential=credential&X-GOOG-ALGORITHM=GOOG4-RSA-SHA256&x-goog-date=20260602T000000Z&X-Goog-Expires=3600&x-goog-signedheaders=host&keep=value"

        Assert.That(
            redacted,
            Is.EqualTo(
                "https://storage.googleapis.com/[redacted-path]?X-Goog-Signature=REDACTED&x-goog-credential=REDACTED&X-GOOG-ALGORITHM=REDACTED&x-goog-date=REDACTED&X-Goog-Expires=REDACTED&x-goog-signedheaders=REDACTED&keep=value"
            )
        )

    [<Test>]
    member _.RedactsAwsSigV4AndAzureSasMetadataQueryNamesCaseInsensitively() =
        let redacted =
            OutboundUrlPolicy.Redaction.redactUri
                "https://hooks.example.test/path?X-Amz-Algorithm=AWS4-HMAC-SHA256&x-amz-date=20260602T120000Z&X-AMZ-Expires=900&x-Amz-SignedHeaders=host&X-Amz-Credential=credential&x-amz-signature=signature&X-Amz-Security-Token=session&ST=2026-06-02T12%3A00%3A00Z&se=2026-06-02T13%3A00%3A00Z&keep=value"

        Assert.That(
            redacted,
            Is.EqualTo(
                "https://hooks.example.test/[redacted-path]?X-Amz-Algorithm=REDACTED&x-amz-date=REDACTED&X-AMZ-Expires=REDACTED&x-Amz-SignedHeaders=REDACTED&X-Amz-Credential=REDACTED&x-amz-signature=REDACTED&X-Amz-Security-Token=REDACTED&ST=REDACTED&se=REDACTED&keep=value"
            )
        )

        Assert.That(redacted, Does.Not.Contain("AWS4-HMAC-SHA256"))
        Assert.That(redacted, Does.Not.Contain("20260602T120000Z"))
        Assert.That(redacted, Does.Not.Contain("2026-06-02T12%3A00%3A00Z"))

    [<Test>]
    member _.RedactsSensitiveQueryValuesDelimitedWithSemicolons() =
        let redacted = OutboundUrlPolicy.Redaction.redactUri "https://hooks.example.test/path?keep=value;access_token=secret;client_secret=also-secret"

        Assert.That(redacted, Is.EqualTo("https://hooks.example.test/[redacted-path]?keep=value&access_token=REDACTED&client_secret=REDACTED"))

        Assert.That(redacted, Does.Not.Contain("=secret"))
        Assert.That(redacted, Does.Not.Contain("also-secret"))

    [<Test>]
    member _.RedactionOmitsFragments() =
        let redacted = OutboundUrlPolicy.Redaction.redactUri "https://hooks.example.test/callback?keep=value#access_token=fragment-token&oauth_token=oauth"

        Assert.That(redacted, Is.EqualTo("https://hooks.example.test/[redacted-path]?keep=value"))
        Assert.That(redacted, Does.Not.Contain("fragment-token"))
        Assert.That(redacted, Does.Not.Contain("oauth"))

    [<Test>]
    member _.RedactionOmitsSecretBearingPathSegments() =
        let redacted =
            OutboundUrlPolicy.Redaction.redactUri
                "https://hooks.example.test/hooks/bearer-token-abc123/customer-secret-value?keep=value&id_token_hint=logout-token"

        Assert.That(redacted, Is.EqualTo("https://hooks.example.test/[redacted-path]?keep=value&id_token_hint=REDACTED"))
        Assert.That(redacted, Does.Not.Contain("hooks/bearer-token-abc123"))
        Assert.That(redacted, Does.Not.Contain("customer-secret-value"))
        Assert.That(redacted, Does.Not.Contain("logout-token"))

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
