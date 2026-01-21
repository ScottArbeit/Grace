namespace Grace.Authorization.Tests

open Grace.Server.Security.EndpointAuthorizationManifest
open NUnit.Framework
open System
open System.IO
open System.Text.RegularExpressions

[<Parallelizable(ParallelScope.All)>]
type EndpointAuthorizationManifestTests() =

    let startupPath =
        Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Server", "Startup.Server.fs"))

    let tokenRegex =
        Regex(
            "(?<method>\\bGET\\b|\\bPOST\\b|\\bPUT\\b)\\s*\\[|(?<kind>subRoute|routef|route)\\s+\"(?<path>[^\"]+)\"",
            RegexOptions.Multiline
        )

    let parseStartupRoutes () =
        let text = File.ReadAllText(startupPath)
        let matches = tokenRegex.Matches(text)
        let mutable currentPrefix = String.Empty
        let mutable currentMethod = String.Empty
        let routes = ResizeArray<string * string>()

        for matchItem in matches do
            if matchItem.Groups["method"].Success then
                currentMethod <- matchItem.Groups["method"].Value
            elif matchItem.Groups["kind"].Success then
                let kind = matchItem.Groups["kind"].Value
                let path = matchItem.Groups["path"].Value

                if kind = "subRoute" then
                    currentPrefix <- path
                else
                    if String.IsNullOrWhiteSpace currentMethod then
                        invalidOp $"Missing HTTP method before route '{path}'."
                    else
                        let fullPath =
                            if String.IsNullOrWhiteSpace currentPrefix then
                                path
                            else
                                $"{currentPrefix}{path}"

                        routes.Add(currentMethod, fullPath)

        routes
        |> Seq.toList
        |> List.append [ ("GET", "/metrics"); ("GET", "/notifications") ]

    [<Test>]
    member _.ManifestCoversAllRoutes() =
        let startupRoutes = parseStartupRoutes () |> Set.ofList
        let manifestRoutes =
            definitions
            |> List.map (fun definition -> definition.Method, definition.Path)
            |> Set.ofList

        let missing = startupRoutes - manifestRoutes
        if missing.Count > 0 then
            let missingText =
                missing
                |> Seq.sort
                |> Seq.map (fun (method, path) -> $"{method} {path}")
                |> String.concat Environment.NewLine

            Assert.Fail($"EndpointAuthorizationManifest is missing routes:{Environment.NewLine}{missingText}")

        let extra = manifestRoutes - startupRoutes
        if extra.Count > 0 then
            let extraText =
                extra
                |> Seq.sort
                |> Seq.map (fun (method, path) -> $"{method} {path}")
                |> String.concat Environment.NewLine

            Assert.Fail($"EndpointAuthorizationManifest includes routes not in Startup.Server.fs:{Environment.NewLine}{extraText}")

    [<Test>]
    member _.ManifestDoesNotContainDuplicates() =
        let duplicates =
            definitions
            |> List.groupBy (fun definition -> definition.Method, definition.Path)
            |> List.choose (fun (key, entries) ->
                if entries.Length > 1 then
                    Some key
                else
                    None)

        if duplicates.Length > 0 then
            let message =
                duplicates
                |> List.map (fun (method, path) -> $"{method} {path}")
                |> String.concat Environment.NewLine

            Assert.Fail($"EndpointAuthorizationManifest contains duplicate entries:{Environment.NewLine}{message}")
