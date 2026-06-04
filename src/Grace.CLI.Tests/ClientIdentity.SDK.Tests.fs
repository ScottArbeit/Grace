namespace Grace.CLI.Tests

open Grace.SDK
open Grace.Shared
open Grace.Types.Common
open NUnit.Framework

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

    [<SetUp>]
    member _.SetUp() = ClientIdentity.clear ()

    [<TearDown>]
    member _.TearDown() = ClientIdentity.clear ()

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
        Assert.That(httpClient.DefaultRequestHeaders.GetValues(Constants.ClientVersionHeaderKey), Is.EquivalentTo([ "1.2.3" ]))
