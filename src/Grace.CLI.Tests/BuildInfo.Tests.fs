namespace Grace.CLI.Tests

open FsUnit
open Grace.Shared
open NUnit.Framework

/// Groups build info coverage for the CLI test project.
module BuildInfoTests =

    /// Verifies that create from values prefers informational version for display identity.
    [<Test>]
    let ``createFromValues prefers informational version for display identity`` () =
        let buildInfo = BuildInfo.createFromValues (Some "0.2.0-ci.1234+abcdef1") (Some "0.2.0-ci.1234") (Some "0.2.0.1234") (Some "0.2.0.0")

        buildInfo.InformationalVersion
        |> should equal "0.2.0-ci.1234+abcdef1"

        buildInfo.SourceRevisionId
        |> should equal (Some "abcdef1")

    /// Verifies that create from values falls back to product version when informational version is missing.
    [<Test>]
    let ``createFromValues falls back to product version when informational version is missing`` () =
        let buildInfo = BuildInfo.createFromValues None (Some "0.2.0-dev.20260521220544") (Some "0.2.0.0") (Some "0.2.0.0")

        buildInfo.InformationalVersion
        |> should equal "0.2.0-dev.20260521220544"

    /// Verifies that create from values falls back to file version when product metadata is missing.
    [<Test>]
    let ``createFromValues falls back to file version when product metadata is missing`` () =
        let buildInfo = BuildInfo.createFromValues None None (Some "0.2.0.1234") (Some "0.2.0.0")

        buildInfo.InformationalVersion
        |> should equal "0.2.0.1234"

    /// Verifies that create from values falls back to assembly version when file metadata is missing.
    [<Test>]
    let ``createFromValues falls back to assembly version when file metadata is missing`` () =
        let buildInfo = BuildInfo.createFromValues None None None (Some "0.2.0.0")

        buildInfo.InformationalVersion
        |> should equal "0.2.0.0"

    /// Verifies that create from values returns safe non empty identity when metadata is unavailable.
    [<Test>]
    let ``createFromValues returns safe non-empty identity when metadata is unavailable`` () =
        let buildInfo = BuildInfo.createFromValues None None None None

        buildInfo.InformationalVersion
        |> should equal "unknown"
