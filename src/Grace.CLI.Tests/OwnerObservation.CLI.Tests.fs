namespace Grace.CLI.Tests

open System
open System.IO
open System.Text.Json
open System.Threading
open Grace.CLI
open NUnit.Framework
open Spectre.Console

/// Executes the explicit owner command parser, action, renderer, selection and output metadata.
[<NonParallelizable>]
type OwnerObservationCliTests() =
    let ids =
        [|
            "--observation-id"
            "11111111-1111-1111-1111-111111111111"
            "--owner-id"
            "22222222-2222-2222-2222-222222222222"
            "--organization-id"
            "33333333-3333-3333-3333-333333333333"
            "--repository-id"
            "44444444-4444-4444-4444-444444444444"
        |]

    /// Parses the real root command and its inherited output switches.
    let parse args =
        GraceCommand.rootCommand.Parse(
            Array.concat [ [|
                               "owner"
                               "get-directory-version-observation"
                           |]
                           args ]
        )

    /// Captures both the common JSON renderer and human observation output.
    let invoke args =
        task {
            use output = new StringWriter()
            let original = Console.Out
            let originalAnsi = AnsiConsole.Console
            let settings = AnsiConsoleSettings()
            settings.Out <- AnsiConsoleOutput output

            try
                Console.SetOut output
                AnsiConsole.Console <- AnsiConsole.Create settings
                let parsed = parse args
                Assert.That(parsed.Errors, Is.Empty)

                let! code =
                    (new Grace.CLI.Command.Owner.GetDirectoryVersionObservation())
                        .InvokeAsync(parsed, CancellationToken.None)

                return code, output.ToString()
            finally
                Console.SetOut original
                AnsiConsole.Console <- originalAnsi
        }

    /// All required IDs are explicit; named selectors cannot substitute for them.
    [<Test>]
    member _.``parser requires each ID and rejects names``() =
        for index in [ 0; 2; 4; 6 ] do
            let missing =
                ids
                |> Array.mapi (fun i item -> i, item)
                |> Array.choose (fun (i, item) -> if i = index || i = index + 1 then None else Some item)

            Assert.That((parse missing).Errors, Is.Not.Empty)

        for name in
            [
                "--owner-name"
                "--organization-name"
                "--repository-name"
            ] do
            Assert.That(
                (parse (Array.append ids [| name; "name" |]))
                    .Errors,
                Is.Not.Empty
            )

    /// The complete entry point can read historical scope outside any configured repository.
    [<Test>]
    [<Platform(Exclude = "Win", Reason = "Windows KnownFolder ignores test HOME overrides; Linux CI isolates command history in the temporary profile.")>]
    member _.``entry point does not require a repository configuration``() =
        task {
            let temporary = Path.Combine(Path.GetTempPath(), $"grace-owner-read-{Guid.NewGuid():N}")
            Directory.CreateDirectory temporary |> ignore
            let previousDirectory = Environment.CurrentDirectory
            let environmentNames = [ "USERPROFILE"; "HOME"; "GRACE_TOKEN" ]

            let previousEnvironment =
                environmentNames
                |> List.map (fun name -> name, Environment.GetEnvironmentVariable name)

            use output = new StringWriter()
            let original = Console.Out
            let originalAnsi = AnsiConsole.Console
            let settings = AnsiConsoleSettings()
            settings.Out <- AnsiConsoleOutput output

            try
                Environment.SetEnvironmentVariable("USERPROFILE", temporary)
                Environment.SetEnvironmentVariable("HOME", temporary)
                Environment.SetEnvironmentVariable("GRACE_TOKEN", "owner-read-fixture-token")
                Assert.That(Grace.Shared.Client.UserConfiguration.getUserGraceDirectory (), Does.StartWith temporary)
                Environment.CurrentDirectory <- temporary
                Console.SetOut output
                AnsiConsole.Console <- AnsiConsole.Create settings

                let! code, (_, query, _) =
                    OwnerObservationClientFixture.withServer 200 (OwnerObservationClientFixture.body Int64.MaxValue) (fun () ->
                        System.Threading.Tasks.Task.Run (fun () ->
                            GraceCommand.main (
                                Array.concat [ [|
                                                   "owner"
                                                   "get-directory-version-observation"
                                               |]
                                               ids
                                               [| "--output"; "Json" |] ]
                            )))

                Assert.That(code, Is.Zero, output.ToString())
                Assert.That(query, Does.Contain "OwnerId=22222222-2222-2222-2222-222222222222")
                use document = JsonDocument.Parse(output.ToString())

                Assert.That(
                    document
                        .RootElement
                        .GetProperty("ReturnValue")
                        .GetProperty("DeclaredLogicalBytes")
                        .GetString(),
                    Is.EqualTo "9223372036854775807"
                )
            finally
                Console.SetOut original
                AnsiConsole.Console <- originalAnsi
                Environment.CurrentDirectory <- previousDirectory
                Grace.SDK.Auth.clearTokenProvider ()

                for name, value in previousEnvironment do
                    Environment.SetEnvironmentVariable(name, value)

                Directory.Delete(temporary, true)
        }

    /// HTTP-backed action output and selection retain all decimal and fractional digits.
    [<TestCase(0L)>]
    [<TestCase(9007199254740993L)>]
    [<TestCase(Int64.MaxValue)>]
    member _.``JSON and selection retain exact strings``(quantity) =
        task {
            let! (code, output), _ =
                OwnerObservationClientFixture.withServer 200 (OwnerObservationClientFixture.body quantity) (fun () ->
                    invoke (Array.append ids [| "--output"; "Json" |]))

            Assert.That(code, Is.Zero)
            use document = JsonDocument.Parse output
            let value = document.RootElement.GetProperty("ReturnValue")

            for name in
                [
                    "DeclaredLogicalBytes"
                    "DistinctContentCount"
                ] do
                Assert.That(value.GetProperty(name).GetString(), Is.EqualTo(string quantity))

            Assert.That(
                value
                    .GetProperty("EnumerationStartedAt")
                    .GetString(),
                Is.EqualTo "2026-09-07T01:02:03.123456789Z"
            )

            Assert.That(
                value
                    .GetProperty("EnumerationFinishedAt")
                    .GetString(),
                Is.EqualTo "2026-09-07T01:02:04.987654321Z"
            )

            let! (selectedCode, selected), _ =
                OwnerObservationClientFixture.withServer 200 (OwnerObservationClientFixture.body quantity) (fun () ->
                    invoke (Array.append ids [| "--select"; "DeclaredLogicalBytes" |]))

            Assert.That(selectedCode, Is.Zero)
            use selectedJson = JsonDocument.Parse selected

            Assert.That(selectedJson.RootElement.GetString(), Is.EqualTo(string quantity))
        }

    /// Human output exposes full values with source-specific meaning; errors publish no observation.
    [<Test>]
    member _.``human output and error conventions``() =
        task {
            let! (code, output), _ = OwnerObservationClientFixture.withServer 200 (OwnerObservationClientFixture.body Int64.MaxValue) (fun () -> invoke ids)
            Assert.That(code, Is.Zero)
            Assert.That(output, Does.Contain "Retained DirectoryVersion metadata declarations")
            Assert.That(output, Does.Contain "9223372036854775807")
            Assert.That(output, Does.Contain "2026-09-07T01:02:03.123456789Z")

            let! (errorCode, errorOutput), _ =
                OwnerObservationClientFixture.withServer 403 "Forbidden." (fun () -> invoke (Array.append ids [| "--output"; "Json" |]))

            Assert.That(errorCode, Is.Not.Zero)
            Assert.That(errorOutput, Does.Not.Contain "DeclaredLogicalBytes")
        }

    /// Registry schema and examples describe the command's actual string projection.
    [<Test>]
    member _.``schema and examples declare exact local representation``() =
        let entry =
            CommandOutputContract.entries
            |> List.find (fun entry -> entry.Identity.CommandId = "owner.get-directory-version-observation")

        let schema = JsonSerializer.SerializeToElement entry.ReturnValueContract.Schema

        Assert.That(
            schema
                .GetProperty("properties")
                .GetProperty("DeclaredLogicalBytes")
                .GetProperty("type")
                .GetString(),
            Is.EqualTo "string"
        )

        let example = JsonSerializer.SerializeToElement entry.ReturnValueContract.Example

        Assert.That(
            example
                .GetProperty("DeclaredLogicalBytes")
                .GetString(),
            Is.EqualTo "9007199254740993"
        )

        Assert.That(
            example
                .GetProperty("EnumerationStartedAt")
                .GetString(),
            Does.EndWith "123456789Z"
        )
