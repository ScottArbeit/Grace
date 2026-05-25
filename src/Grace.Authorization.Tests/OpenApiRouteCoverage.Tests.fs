namespace Grace.Authorization.Tests

open NUnit.Framework
open System
open System.IO
open System.Text.RegularExpressions

[<Parallelizable(ParallelScope.All)>]
type OpenApiRouteCoverageTests() =

    let openApiMainPath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "OpenAPI", "Main.OpenAPI.yaml"))

    let openApiPathRegex = Regex("^  (?<path>/[^:]+):\\s*$", RegexOptions.Compiled)

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
