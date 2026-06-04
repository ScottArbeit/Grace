namespace Grace.Authorization.Tests

open Grace.Shared
open NUnit.Framework
open System
open System.IO
open System.Text.RegularExpressions

[<Parallelizable(ParallelScope.All)>]
type OpenApiRouteCoverageTests() =

    let openApiMainPath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "OpenAPI", "Main.OpenAPI.yaml"))

    let openApiPathRegex = Regex("^  (?<path>/[^:]+):\\s*$", RegexOptions.Compiled)
    let openApiVersionRegex = Regex("""^  version: "(?<version>[^"]+)"\s*$""", RegexOptions.Compiled)

    let openApiVersion () =
        File.ReadAllLines(openApiMainPath)
        |> Array.pick (fun line ->
            let matchItem = openApiVersionRegex.Match(line)

            if matchItem.Success then Some matchItem.Groups["version"].Value else None)

    let openApiPaths () =
        File.ReadAllLines(openApiMainPath)
        |> Array.choose (fun line ->
            let matchItem = openApiPathRegex.Match(line)

            if matchItem.Success then Some matchItem.Groups["path"].Value else None)
        |> Set.ofArray

    [<Test>]
    member _.OpenApiCoversAdrStorageRoutes() =
        let expectedStorageRoutes =
            [
                "/storage/getUploadMetadataForFiles"
                "/storage/getUploadUri"
                "/storage/getDownloadUri"
                "/storage/getContentBlockUploadUri"
                "/storage/getContentBlockDownloadUri"
                "/storage/discoverContentBlocks"
                "/storage/startManifestUploadSession"
                "/storage/issueDedupeDiscovery"
                "/storage/claimReuseRanges"
                "/storage/registerContentBlockUpload"
                "/storage/confirmContentBlockUpload"
                "/storage/finalizeManifestUpload"
            ]
            |> Set.ofList

        let missing = expectedStorageRoutes - openApiPaths ()

        if missing.Count > 0 then
            let missingText =
                missing
                |> Seq.sort
                |> String.concat Environment.NewLine

            Assert.Fail($"OpenAPI is missing ADR-0001 storage routes:{Environment.NewLine}{missingText}")

    [<Test>]
    member _.OpenApiInfoVersionMatchesCurrentApiContractVersion() = Assert.That(openApiVersion (), Is.EqualTo(ApiContractVersion.CurrentReleased))
