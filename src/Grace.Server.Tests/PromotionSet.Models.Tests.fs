namespace Grace.Server.Tests

open Grace.Actors.PromotionSetConflictModel
open Grace.Server
open Microsoft.Extensions.Configuration
open NUnit.Framework
open System
open System.Collections.Generic
open System.Threading.Tasks

[<TestFixture>]
type PromotionSetModelsTests() =

    let sampleRequest: ConflictResolutionModelRequest =
        { FilePath = "src/app.fs"; BaseContent = Some "let value = 1"; OursContent = Some "let value = 2"; TheirsContent = Some "let value = 3" }

    [<Test>]
    member _.CreateProviderDefaultsToNullProviderWhenNotConfigured() : Task =
        task {
            let configuration = ConfigurationBuilder().Build()
            let provider = PromotionSetModels.createProvider configuration

            Assert.That(provider.ProviderName, Is.EqualTo("none"))

            let! result = provider.SuggestResolution sampleRequest

            match result with
            | Ok _ -> Assert.Fail("Expected null provider to return error.")
            | Error errorText -> Assert.That(errorText, Does.Contain("not configured"))
        }

    [<Test>]
    member _.OpenRouterProviderReturnsDeterministicErrorWhenApiKeyMissing() : Task =
        task {
            let apiKeyEnvVarName = "GRACE_TEST_PROMOTIONSET_OPENROUTER_KEY_MISSING"
            let previousApiKeyValue = Environment.GetEnvironmentVariable(apiKeyEnvVarName)

            try
                Environment.SetEnvironmentVariable(apiKeyEnvVarName, null)

                let settings =
                    Dictionary<string, string>(
                        [
                            KeyValuePair("Grace:PromotionSetModels:Provider", "OpenRouter")
                            KeyValuePair("Grace:PromotionSetModels:OpenRouter:ApiBase", "https://openrouter.ai/api/v1")
                            KeyValuePair("Grace:PromotionSetModels:OpenRouter:ApiKeyEnvVar", apiKeyEnvVarName)
                            KeyValuePair("Grace:PromotionSetModels:OpenRouter:Model", "openrouter/auto")
                        ]
                    )

                let configuration =
                    ConfigurationBuilder()
                        .AddInMemoryCollection(settings)
                        .Build()

                let provider = PromotionSetModels.createProvider configuration

                Assert.That(provider.ProviderName, Is.EqualTo("OpenRouter"))

                let! result = provider.SuggestResolution sampleRequest

                match result with
                | Ok _ -> Assert.Fail("Expected missing API key to return an error.")
                | Error errorText ->
                    Assert.That(errorText, Does.Contain(apiKeyEnvVarName))
                    Assert.That(errorText, Does.Contain("not configured"))
            finally
                Environment.SetEnvironmentVariable(apiKeyEnvVarName, previousApiKeyValue)
        }

    [<Test>]
    member _.ParseModelResponseRejectsMissingConfidence() =
        let responseJson = """{"proposedContent":"let value = 2","shouldDelete":false,"explanation":"merge"}"""

        match PromotionSetModels.tryParseModelResponse responseJson with
        | Ok _ -> Assert.Fail("Expected parser to reject missing confidence.")
        | Error errorText -> Assert.That(errorText, Does.Contain("confidence"))

    [<Test>]
    member _.ParseModelResponseRejectsOutOfRangeConfidence() =
        let responseJson = """{"proposedContent":"let value = 2","shouldDelete":false,"confidence":1.5,"explanation":"merge"}"""

        match PromotionSetModels.tryParseModelResponse responseJson with
        | Ok _ -> Assert.Fail("Expected parser to reject out-of-range confidence.")
        | Error errorText -> Assert.That(errorText, Does.Contain("[0.0, 1.0]"))

    [<Test>]
    member _.ParseModelResponseAcceptsDeleteWithoutProposedContent() =
        let responseJson = """{"proposedContent":null,"shouldDelete":true,"confidence":0.91,"explanation":"delete"}"""

        match PromotionSetModels.tryParseModelResponse responseJson with
        | Error errorText -> Assert.Fail($"Expected delete response to parse successfully, but got error: {errorText}")
        | Ok parsed ->
            Assert.That(parsed.ShouldDelete, Is.True)
            Assert.That(parsed.ProposedContent, Is.EqualTo(None))
            Assert.That(parsed.Confidence, Is.EqualTo(0.91).Within(0.00001))
