namespace Grace.CLI.Tests

open Grace.CLI
open NUnit.Framework
open System
open System.Text.Json

/// Covers pure parser and complete-document JSON requirements for cache commands.
[<TestFixture>]
module CacheCliParsingTests =

    /// Parses one full JSON buffer and disposes its document before the assertion returns.
    let private parsesCompleteDocument (value: string) : JsonValueKind =
        use document = JsonDocument.Parse(value)
        document.RootElement.ValueKind

    /// Proves a complete-buffer parser rejects any non-document framing without assuming its concrete parser exception subtype.
    let private rejectsCompleteDocument (value: string) =
        let error = Assert.Catch(Action(fun () -> parsesCompleteDocument value |> ignore))

        Assert.That(error, Is.Not.Null)

    /// Verifies cache command names are accepted by the root parser without repository configuration.
    [<Test>]
    let ``cache command parser accepts enroll and status`` () =
        for arguments in
            [|
                [| "cache"; "status" |]
                [|
                    "cache"
                    "enroll"
                    "--display-name"
                    "test"
                    "--endpoint"
                    "https://cache.example.test"
                    "--boundary"
                    "owner"
                    "--owner-id"
                    "11111111-1111-1111-1111-111111111111"
                    "--repository-organization-id"
                    "22222222-2222-2222-2222-222222222222"
                    "--repository-id"
                    "33333333-3333-3333-3333-333333333333"
                |]
            |] do
            let result = GraceCommand.rootCommand.Parse(arguments)
            Assert.That(result.Errors.Count, Is.Zero)

    /// Verifies inert enrollment examples do not validate or execute administrator-supplied enrollment inputs.
    [<Test>]
    let ``cache enrollment examples are inert without required inputs`` () =
        let result = GraceCommand.rootCommand.Parse([| "cache"; "enroll"; "--examples" |])
        Assert.That(result.Errors.Count, Is.Zero)

    /// Verifies the JSON proof parses the complete buffer and rejects extra values or non-whitespace framing.
    [<Test>]
    let ``complete JSON output proof rejects prefix suffix and second documents`` () =
        Assert.That(parsesCompleteDocument "\n{\n  \"ReturnValue\": { \"Enrollment\": \"notEnrolled\" }\n}\n", Is.EqualTo(JsonValueKind.Object))

        rejectsCompleteDocument "prefix { }"
        rejectsCompleteDocument "{ } suffix"
        rejectsCompleteDocument "{ } { }"
