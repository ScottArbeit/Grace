namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open NUnit.Framework
open System.Text.Json

/// Groups select projection parsing coverage for the CLI test project.
[<TestFixture>]
module SelectProjectionParsingTests =

    /// Parses representative arguments through the production CLI parser for CLI select Projection CLI assertions.
    let private parse selector = SelectProjection.tryParse "corr-select-parse" selector

    /// Verifies that accepted simple property paths parse.
    [<TestCase("Value")>]
    [<TestCase("Owner.Name")>]
    [<TestCase("_links.Self")>]
    [<TestCase("Items.Item1")>]
    let ``accepted simple property paths parse`` selector =
        match parse selector with
        | Ok _ -> ()
        | Error error -> Assert.Fail(error.Error)

    /// Verifies that metadata paths are rejected.
    [<TestCase("Properties.CorrelationId")>]
    [<TestCase("properties.CorrelationId")>]
    [<TestCase("CorrelationId")>]
    [<TestCase("EventTime")>]
    [<TestCase("ReturnValue.Value")>]
    let ``metadata paths are rejected`` selector =
        match parse selector with
        | Ok _ -> Assert.Fail($"Expected selector '{selector}' to be rejected.")
        | Error error ->
            error.Error
            |> should contain "cannot read envelope metadata"

    /// Verifies that malformed expression selectors are rejected.
    [<TestCase("")>]
    [<TestCase(" ")>]
    [<TestCase("Value ")>]
    [<TestCase(".Value")>]
    [<TestCase("Value.")>]
    [<TestCase("Value..Name")>]
    [<TestCase("Value[0]")>]
    [<TestCase("Items[*]")>]
    [<TestCase("Items?(@.Name)")>]
    [<TestCase("Name()")>]
    [<TestCase("Name | length")>]
    let ``malformed expression selectors are rejected`` selector =
        match parse selector with
        | Ok _ -> Assert.Fail($"Expected selector '{selector}' to be rejected.")
        | Error error ->
            error.CorrelationId
            |> should equal "corr-select-parse"

    /// Verifies that comma separated multi path selectors are rejected in v1.
    [<Test>]
    let ``comma-separated multi-path selectors are rejected in V1`` () =
        match parse "Value,Name" with
        | Ok _ -> Assert.Fail("Expected comma-separated selector to be rejected.")
        | Error error ->
            error.Error
            |> should contain "supports only dot-separated ReturnValue property names"

    /// Verifies that project returns selected nested property.
    [<Test>]
    let ``project returns selected nested property`` () =
        match parse "Container.Value" with
        | Error error -> Assert.Fail(error.Error)
        | Ok selector ->
            match SelectProjection.project "corr-project" selector (box {| Container = {| Value = 42 |} |}) with
            | Error error -> Assert.Fail(error.Error)
            | Ok selected ->
                selected.ValueKind
                |> should equal JsonValueKind.Number

                selected.GetInt32() |> should equal 42
