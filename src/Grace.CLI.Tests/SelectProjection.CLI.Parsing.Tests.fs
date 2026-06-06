namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open NUnit.Framework
open System.Text.Json

[<TestFixture>]
module SelectProjectionParsingTests =

    let private parse selector = SelectProjection.tryParse "corr-select-parse" selector

    [<TestCase("Value")>]
    [<TestCase("Owner.Name")>]
    [<TestCase("_links.Self")>]
    [<TestCase("Items.Item1")>]
    let ``accepted simple property paths parse`` selector =
        match parse selector with
        | Ok _ -> ()
        | Error error -> Assert.Fail(error.Error)

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
