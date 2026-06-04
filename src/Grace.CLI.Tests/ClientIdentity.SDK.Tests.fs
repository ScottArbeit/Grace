namespace Grace.CLI.Tests

open Grace.CLI
open Grace.SDK
open Grace.Shared
open Grace.Types.Common
open NUnit.Framework
open Spectre.Console
open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Http

[<Parallelizable(ParallelScope.All)>]
type ApiContractVersionSharedTests() =

    [<Test>]
    member _.CurrentReleasedVersionUsesDateOnlyContractFormat() =
        Assert.That(ApiContractVersion.CurrentReleased, Is.EqualTo("2023-10-01"))
        Assert.That(ApiContractVersion.CurrentReleasedDate, Is.EqualTo(System.DateOnly(2023, 10, 1)))

    [<Test>]
    member _.NormalizePinsReleasedVersionAndAliases() =
        match ApiContractVersion.normalize "2023-10-01" with
        | Ok value -> Assert.That(value, Is.EqualTo(ApiContractVersion.CurrentReleased))
        | Error message -> Assert.Fail($"Expected the current released version to normalize, but got: {message}.")

        match ApiContractVersion.normalize "EDGE" with
        | Ok value -> Assert.That(value, Is.EqualTo(ApiContractVersion.Edge))
        | Error message -> Assert.Fail($"Expected the edge alias to normalize, but got: {message}.")

        match ApiContractVersion.normalize " Latest " with
        | Ok value -> Assert.That(value, Is.EqualTo(ApiContractVersion.Latest))
        | Error message -> Assert.Fail($"Expected the latest alias to normalize, but got: {message}.")

    [<Test>]
    member _.NormalizeRejectsStaleDatesAndInvalidPreviewSuffixes() =
        match ApiContractVersion.normalize "2022-01-01" with
        | Error message -> Assert.That(message, Does.Contain("Unsupported API contract version"))
        | Ok value -> Assert.Fail($"Expected a stale date to be rejected, but got {value}.")

        match ApiContractVersion.normalize "2023-10-01-preview" with
        | Error message -> Assert.That(message, Does.Contain("Invalid API contract version"))
        | Ok value -> Assert.Fail($"Expected a preview suffix to be rejected, but got {value}.")

[<NonParallelizable>]
type ClientIdentitySdkTests() =

    let setAnsiConsoleOutput (writer: TextWriter) =
        let settings = AnsiConsoleSettings()
        settings.Out <- AnsiConsoleOutput(writer)
        AnsiConsole.Console <- AnsiConsole.Create(settings)

    let captureOutput (action: unit -> unit) =
        use writer = new StringWriter()
        let originalOut = Console.Out

        try
            Console.SetOut(writer)
            setAnsiConsoleOutput writer
            action ()
            writer.ToString()
        finally
            Console.SetOut(originalOut)
            setAnsiConsoleOutput originalOut

    let parseOutput outputFormat = GraceCommand.rootCommand.Parse([| "--output"; outputFormat |])

    let parseNormalOutput () = parseOutput "Normal"

    let parseVerboseOutput () = parseOutput "Verbose"

    let addLifecycleHeaders
        (response: HttpResponseMessage)
        (status: string)
        (unsupportedAfter: string)
        (minimumVersion: string)
        (recommendedVersion: string option)
        (updateUrl: string)
        =
        response.Headers.TryAddWithoutValidation(Constants.SdkLifecycleStatusHeaderKey, status)
        |> ignore

        response.Headers.TryAddWithoutValidation(Constants.SdkLifecycleUnsupportedAfterHeaderKey, unsupportedAfter)
        |> ignore

        response.Headers.TryAddWithoutValidation(Constants.SdkLifecycleMinimumVersionHeaderKey, minimumVersion)
        |> ignore

        match recommendedVersion with
        | Some value ->
            response.Headers.TryAddWithoutValidation(Constants.SdkLifecycleRecommendedVersionHeaderKey, value)
            |> ignore
        | None -> ()

        response.Headers.TryAddWithoutValidation(Constants.SdkLifecycleUpdateUrlHeaderKey, updateUrl)
        |> ignore

    let lifecycleProperties status unsupportedAfter minimumVersion recommendedVersion updateUrl =
        let response = new HttpResponseMessage(HttpStatusCode.OK)

        addLifecycleHeaders response status unsupportedAfter minimumVersion recommendedVersion updateUrl

        match ClientIdentity.parseLifecycleDiagnostics response with
        | Some diagnostics -> ClientIdentity.lifecycleDiagnosticsToProperties diagnostics
        | None -> Dictionary<string, obj>()

    [<SetUp>]
    member _.SetUp() =
        ClientIdentity.clear ()
        Common.resetLifecycleWarningSuppression ()

    [<TearDown>]
    member _.TearDown() =
        ClientIdentity.clear ()
        Common.resetLifecycleWarningSuppression ()

    [<Test>]
    member _.GetHttpClientPinsReleasedApiContractVersionByDefault() =
        use httpClient = ClientIdentity.getHttpClient "corr-sdk-default"

        let values = httpClient.DefaultRequestHeaders.GetValues(Constants.ServerApiVersionHeaderKey)

        Assert.That(values, Is.EquivalentTo([ ApiContractVersion.CurrentReleased ]))

    [<Test>]
    member _.GetHttpClientUsesExplicitEdgeAndLatestOverrides() =
        ClientIdentity.configureApiContractVersion ApiContractVersion.Edge

        use edgeClient = ClientIdentity.getHttpClient "corr-sdk-edge"

        Assert.That(edgeClient.DefaultRequestHeaders.GetValues(Constants.ServerApiVersionHeaderKey), Is.EquivalentTo([ ApiContractVersion.Edge ]))

        ClientIdentity.configureApiContractVersion ApiContractVersion.Latest

        use latestClient = ClientIdentity.getHttpClient "corr-sdk-latest"

        Assert.That(latestClient.DefaultRequestHeaders.GetValues(Constants.ServerApiVersionHeaderKey), Is.EquivalentTo([ ApiContractVersion.Latest ]))

    [<Test>]
    member _.ConfigureApiContractVersionRejectsInvalidAndStaleVersions() =
        Assert.Throws<System.ArgumentException>(System.Action(fun () -> ClientIdentity.configureApiContractVersion "2022-01-01"))
        |> ignore

        Assert.Throws<System.ArgumentException>(System.Action(fun () -> ClientIdentity.configureApiContractVersion "2023-10-01-preview"))
        |> ignore

    [<Test>]
    member _.ClientIdentityHeadersRemainSeparateFromApiContractVersion() =
        ClientIdentity.configure (ClientType.CLI "1.2.3")

        use httpClient = ClientIdentity.getHttpClient "corr-sdk-identity"

        Assert.That(httpClient.DefaultRequestHeaders.GetValues(Constants.ServerApiVersionHeaderKey), Is.EquivalentTo([ ApiContractVersion.CurrentReleased ]))
        Assert.That(httpClient.DefaultRequestHeaders.GetValues(Constants.ClientTypeHeaderKey), Is.EquivalentTo([ "CLI" ]))
        Assert.That(httpClient.DefaultRequestHeaders.GetValues(Constants.ClientVersionHeaderKey), Is.EquivalentTo([ "1.2.3" ]))

    [<Test>]
    member _.GetHttpClientSendsClientIdentityByDefault() =
        use httpClient = ClientIdentity.getHttpClient "corr-sdk-default-identity"

        Assert.That(httpClient.DefaultRequestHeaders.GetValues(Constants.ClientTypeHeaderKey), Is.EquivalentTo([ "CLI" ]))
        Assert.That(httpClient.DefaultRequestHeaders.GetValues(Constants.ClientVersionHeaderKey), Is.Not.Empty)

    [<Test>]
    member _.LifecycleDiagnosticsAreAddedToSuccessAndErrorResults() =
        use response = new HttpResponseMessage(HttpStatusCode.UpgradeRequired)
        addLifecycleHeaders response "unsupported" "2026-12-01" "0.1.0" (Some "0.2.0") "https://github.com/ScottArbeit/Grace/releases"

        let success = ClientIdentity.enhanceWithLifecycleDiagnostics response (Ok(GraceReturnValue.Create "ok" "corr-success"))

        let failure = ClientIdentity.enhanceWithLifecycleDiagnostics response (Error(GraceError.Create "UnsupportedClientVersion" "corr-failure"))

        match success with
        | Ok value ->
            Assert.That(value.Properties[ClientIdentity.LifecycleStatusPropertyKey], Is.EqualTo("unsupported"))
            Assert.That(value.Properties[ClientIdentity.LifecycleRecommendedVersionPropertyKey], Is.EqualTo("0.2.0"))
        | Error error -> Assert.Fail($"Expected success diagnostics, got {error.Error}.")

        match failure with
        | Error error ->
            Assert.That(error.Properties[ClientIdentity.LifecycleStatusPropertyKey], Is.EqualTo("unsupported"))
            Assert.That(error.Properties[ClientIdentity.LifecycleRecommendedVersionPropertyKey], Is.EqualTo("0.2.0"))
        | Ok _ -> Assert.Fail("Expected error diagnostics.")

    [<Test>]
    member _.DirectSdkStringResponsePathPreservesLifecycleDiagnosticsOnSuccessAndError() =
        task {
            use successResponse = new HttpResponseMessage(HttpStatusCode.OK)
            successResponse.Content <- new StringContent("https://storage.example.test/upload")
            addLifecycleHeaders successResponse "deprecated" "2026-12-01" "0.1.0" (Some "0.2.0") "https://github.com/ScottArbeit/Grace/releases"

            let! success = Storage.readStringResponseWithLifecycle successResponse "corr-direct-success" "direct failure"

            match success with
            | Ok value ->
                Assert.That(value.ReturnValue, Is.EqualTo("https://storage.example.test/upload"))
                Assert.That(value.Properties[ClientIdentity.LifecycleStatusPropertyKey], Is.EqualTo("deprecated"))
                Assert.That(value.Properties[ClientIdentity.LifecycleRecommendedVersionPropertyKey], Is.EqualTo("0.2.0"))
            | Error error -> Assert.Fail($"Expected direct SDK success diagnostics, got {error.Error}.")

            use errorResponse = new HttpResponseMessage(HttpStatusCode.UpgradeRequired)
            errorResponse.Content <- new StringContent("UnsupportedClientVersion")
            addLifecycleHeaders errorResponse "unsupported" "2026-12-01" "0.1.0" (Some "0.2.0") "https://github.com/ScottArbeit/Grace/releases"

            let! failure = Storage.readStringResponseWithLifecycle errorResponse "corr-direct-error" "direct failure"

            match failure with
            | Error error ->
                Assert.That(error.Error, Does.Contain("direct failure"))
                Assert.That(error.Properties[ClientIdentity.LifecycleStatusPropertyKey], Is.EqualTo("unsupported"))
                Assert.That(error.Properties[ClientIdentity.LifecycleRecommendedVersionPropertyKey], Is.EqualTo("0.2.0"))
            | Ok _ -> Assert.Fail("Expected direct SDK error diagnostics.")
        }

    [<Test>]
    member _.LifecycleDiagnosticsHandleMissingRecommendedVersionMalformedDateAndNonHttpsUrl() =
        use response = new HttpResponseMessage(HttpStatusCode.OK)
        addLifecycleHeaders response "mystery" "not-a-date" "0.1.0" None "http://example.test/update"

        match ClientIdentity.parseLifecycleDiagnostics response with
        | Some diagnostics ->
            Assert.That(diagnostics.Status, Is.EqualTo(Some "mystery"))
            Assert.That(diagnostics.RecommendedVersion, Is.EqualTo(None))
            Assert.That(diagnostics.UnsupportedAfterIsMalformed, Is.True)
            Assert.That(diagnostics.UpdateUrlIsHttps, Is.EqualTo(Some false))
        | None -> Assert.Fail("Expected lifecycle diagnostics.")

    [<Test>]
    member _.CliLifecycleWarningIsRenderedOncePerCommand() =
        let parseResult = parseNormalOutput ()
        let properties = lifecycleProperties "deprecated" "2026-12-01" "0.1.0" (Some "0.2.0") "https://github.com/ScottArbeit/Grace/releases"

        let output =
            captureOutput (fun () ->
                let first = GraceReturnValue.Create "first" "corr-one"
                first.enhance properties |> ignore

                let second = GraceReturnValue.Create "second" "corr-two"
                second.enhance properties |> ignore

                Common.renderOutput parseResult (Ok first)
                |> ignore

                Common.renderOutput parseResult (Ok second)
                |> ignore)

        let firstIndex = output.IndexOf("This Grace client version is deprecated.", StringComparison.Ordinal)
        let lastIndex = output.LastIndexOf("This Grace client version is deprecated.", StringComparison.Ordinal)

        Assert.That(firstIndex, Is.GreaterThanOrEqualTo(0))
        Assert.That(lastIndex, Is.EqualTo(firstIndex))
        Assert.That(output, Does.Contain("Update to Grace CLI/SDK version 0.2.0 or newer."))

    [<Test>]
    member _.CliUnsupportedClientGuidanceUsesLifecycleMetadata() =
        let parseResult = parseNormalOutput ()
        let properties = lifecycleProperties "unsupported" "not-a-date" "0.1.0" None "http://example.test/update"
        let error = GraceError.Create "UnsupportedClientVersion" "corr-unsupported"
        error.enhance properties |> ignore

        let output =
            captureOutput (fun () ->
                Common.renderOutput parseResult (Error error)
                |> ignore)

        Assert.That(output, Does.Contain("This Grace client version is no longer supported."))
        Assert.That(output, Does.Contain("The server rejected this request because the client version is unsupported."))
        Assert.That(output, Does.Contain("Update to Grace CLI/SDK version 0.1.0 or newer."))
        Assert.That(output, Does.Contain("Unsupported-after value from server could not be parsed: not-a-date."))
        Assert.That(output, Does.Contain("Update URL from server was not HTTPS and was not displayed."))
        Assert.That(output, Does.Not.Contain("http://example.test/update"))

    [<Test>]
    member _.CliVerboseSuccessOutputRedactsNonHttpsLifecycleUpdateUrl() =
        let parseResult = parseVerboseOutput ()
        let properties = lifecycleProperties "deprecated" "2026-12-01" "0.1.0" (Some "0.2.0") "http://example.test/update"
        let returnValue = GraceReturnValue.Create "ok" "corr-verbose-success"
        returnValue.enhance properties |> ignore

        let output =
            captureOutput (fun () ->
                Common.renderOutput parseResult (Ok returnValue)
                |> ignore)

        Assert.That(output, Does.Contain("Properties:"))
        Assert.That(output, Does.Contain(ClientIdentity.LifecycleStatusPropertyKey))
        Assert.That(output, Does.Not.Contain("http://example.test/update"))

    [<Test>]
    member _.CliVerboseErrorOutputRedactsNonHttpsLifecycleUpdateUrl() =
        let parseResult = parseVerboseOutput ()
        let properties = lifecycleProperties "unsupported" "2026-12-01" "0.1.0" (Some "0.2.0") "http://example.test/update"
        let error = GraceError.Create "UnsupportedClientVersion" "corr-verbose-error"
        error.enhance properties |> ignore

        let output =
            captureOutput (fun () ->
                Common.renderOutput parseResult (Error error)
                |> ignore)

        Assert.That(output, Does.Contain("UnsupportedClientVersion"))
        Assert.That(output, Does.Contain(ClientIdentity.LifecycleStatusPropertyKey))
        Assert.That(output, Does.Not.Contain("http://example.test/update"))
