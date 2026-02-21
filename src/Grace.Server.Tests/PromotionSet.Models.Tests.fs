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
