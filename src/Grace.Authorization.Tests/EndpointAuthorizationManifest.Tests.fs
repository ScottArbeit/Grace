namespace Grace.Authorization.Tests

open Grace.Server.Security.EndpointAuthorizationManifest
open Grace.Types.Authorization
open NUnit.Framework
open System
open System.IO
open System.Text.RegularExpressions

[<Parallelizable(ParallelScope.All)>]
type EndpointAuthorizationManifestTests() =

    let startupPath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Server", "Startup.Server.fs"))

    let tokenRegex = Regex("(?<method>\\bGET\\b|\\bPOST\\b|\\bPUT\\b)\\s*\\[|(?<kind>subRoute|routef|route)\\s+\"(?<path>[^\"]+)\"", RegexOptions.Multiline)

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
                else if String.IsNullOrWhiteSpace currentMethod then
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
        |> List.append [ ("GET", "/metrics")
                         ("GET", "/notifications") ]

    let assertRouteSecurity method_ path (expectedSecurity: EndpointSecurity) =
        let matchingDefinitions =
            definitions
            |> List.filter (fun definition ->
                definition.Method = method_
                && definition.Path = path)

        match matchingDefinitions with
        | [ definition ] ->
            Assert.That(
                definition.Security,
                Is.EqualTo(expectedSecurity),
                $"Expected {method_} {path} to use {expectedSecurity}, but manifest uses {definition.Security}."
            )
        | [] -> Assert.Fail($"Expected EndpointAuthorizationManifest to include {method_} {path}.")
        | _ -> Assert.Fail($"Expected EndpointAuthorizationManifest to include one entry for {method_} {path}.")

    let assertRoutesUseSecurity expectedSecurity routes =
        for method_, path in routes do
            assertRouteSecurity method_ path expectedSecurity

    let assertRouteRequiresSecurity method_ path (expectedSecurity: EndpointSecurity) =
        let matchingDefinitions =
            definitions
            |> List.filter (fun definition ->
                definition.Method = method_
                && definition.Path = path)

        let rec includesSecurity actualSecurity =
            actualSecurity = expectedSecurity
            || match actualSecurity with
               | AllOf requirements -> requirements |> List.exists includesSecurity
               | _ -> false

        match matchingDefinitions with
        | [ definition ] ->
            Assert.That(
                includesSecurity definition.Security,
                Is.True,
                $"Expected {method_} {path} to require {expectedSecurity}, but manifest uses {definition.Security}."
            )
        | [] -> Assert.Fail($"Expected EndpointAuthorizationManifest to include {method_} {path}.")
        | _ -> Assert.Fail($"Expected EndpointAuthorizationManifest to include one entry for {method_} {path}.")

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
            |> List.choose (fun (key, entries) -> if entries.Length > 1 then Some key else None)

        if duplicates.Length > 0 then
            let message =
                duplicates
                |> List.map (fun (method, path) -> $"{method} {path}")
                |> String.concat Environment.NewLine

            Assert.Fail($"EndpointAuthorizationManifest contains duplicate entries:{Environment.NewLine}{message}")

    [<Test>]
    member _.ApprovalPolicyRoutesRequireRepositoryPolicyManage() =
        [
            "POST", "/approval/policy/create"
            "POST", "/approval/policy/list"
            "POST", "/approval/policy/show"
            "POST", "/approval/policy/update"
            "POST", "/approval/policy/enable"
            "POST", "/approval/policy/disable"
            "POST", "/approval/policy/delete"
            "POST", "/approval/policy/evaluate"
        ]
        |> assertRoutesUseSecurity (Authorized(Operation.ApprovalPolicyManage, ResourceKind.Repository))

    [<Test>]
    member _.ApprovalRequestRoutesUseExpectedRepositoryPolicies() =
        [
            "POST", "/approval/request/list"
            "POST", "/approval/request/show"
            "POST", "/approval/request/history"
        ]
        |> assertRoutesUseSecurity (Authorized(Operation.ApprovalRequestRead, ResourceKind.Repository))

        [
            "POST", "/approval/request/approve"
            "POST", "/approval/request/reject"
        ]
        |> assertRoutesUseSecurity (Authorized(Operation.ApprovalRequestRespond, ResourceKind.Repository))

        assertRouteSecurity "POST" "/approval/request/_seedGenerated" Authenticated

    [<Test>]
    member _.PolicyAndQueueRoutesUseAuthenticatedAccess() =
        [
            "POST", "/policy/acknowledge"
            "POST", "/policy/current"
            "POST", "/policy/_seedSnapshot"
            "POST", "/queue/dequeue"
            "POST", "/queue/enqueue"
            "POST", "/queue/pause"
            "POST", "/queue/resume"
            "POST", "/queue/status"
        ]
        |> assertRoutesUseSecurity Authenticated

    [<Test>]
    member _.WebhookRoutesUseExpectedRepositoryPolicies() =
        [
            "POST", "/webhook/rule/create"
            "POST", "/webhook/rule/list"
            "POST", "/webhook/rule/show"
            "POST", "/webhook/rule/update"
            "POST", "/webhook/rule/enable"
            "POST", "/webhook/rule/disable"
            "POST", "/webhook/rule/delete"
            "POST", "/webhook/rule/test"
        ]
        |> assertRoutesUseSecurity (Authorized(Operation.WebhookManage, ResourceKind.Repository))

        [
            "POST", "/webhook/delivery/list"
            "POST", "/webhook/delivery/show"
        ]
        |> assertRoutesUseSecurity (Authorized(Operation.WebhookDeliveryRead, ResourceKind.Repository))

    [<Test>]
    member _.AuthRoutesUseExpectedAnonymousAndAuthenticatedPolicies() =
        [
            "GET", "/auth/login"
            "GET", "/auth/login/%s"
            "GET", "/auth/oidc/config"
        ]
        |> assertRoutesUseSecurity AllowAnonymous

        [
            "GET", "/auth/logout"
            "GET", "/auth/me"
            "POST", "/auth/token/create"
            "POST", "/auth/token/list"
            "POST", "/auth/token/revoke"
        ]
        |> assertRoutesUseSecurity Authenticated

    [<Test>]
    member _.MetricsAndManualRoutesUseExpectedPolicies() =
        assertRouteSecurity "GET" "/metrics" (Authorized(Operation.SystemAdmin, ResourceKind.System))
        assertRouteSecurity "GET" "/notifications" Authenticated

    [<Test>]
    member _.StorageRoutesUseExpectedPathAndRepositoryPolicies() =
        [
            "POST", "/storage/getDownloadUri"
            "POST", "/storage/getContentBlockDownloadUri"
        ]
        |> assertRoutesUseSecurity (Authorized(Operation.PathRead, ResourceKind.Path))

        [
            "POST", "/storage/discoverContentBlocks"
        ]
        |> assertRoutesUseSecurity (Authorized(Operation.RepositoryRead, ResourceKind.Repository))

        [
            "POST", "/storage/claimReuseRanges"
            "POST", "/storage/confirmContentBlockUpload"
            "POST", "/storage/finalizeManifestUpload"
            "POST", "/storage/getContentBlockUploadUri"
            "POST", "/storage/getUploadMetadataForFiles"
            "POST", "/storage/getUploadUri"
            "POST", "/storage/issueDedupeDiscovery"
            "POST", "/storage/registerContentBlockUpload"
            "POST", "/storage/startManifestUploadSession"
        ]
        |> assertRoutesUseSecurity (Authorized(Operation.PathWrite, ResourceKind.Path))

    [<Test>]
    member _.BranchAnnotateRequiresBranchReadAndPathRead() =
        assertRouteRequiresSecurity "POST" "/branch/annotate" (Authorized(Operation.BranchRead, ResourceKind.Branch))
        assertRouteRequiresSecurity "POST" "/branch/annotate" (Authorized(Operation.PathRead, ResourceKind.Path))

    [<Test>]
    member _.SelectedWorkItemRoutesUseExpectedPolicies() =
        [
            "POST", "/work/create"
            "POST", "/work/add-summary"
            "POST", "/work/link/artifact"
            "POST", "/work/link/promotion-set"
            "POST", "/work/link/reference"
            "POST", "/work/links/remove/artifact"
            "POST", "/work/links/remove/artifact-type"
            "POST", "/work/links/remove/promotion-set"
            "POST", "/work/links/remove/reference"
            "POST", "/work/update"
        ]
        |> assertRoutesUseSecurity (Authorized(Operation.RepositoryWrite, ResourceKind.Repository))

        [
            "POST", "/work/get"
            "POST", "/work/links/list"
            "POST", "/work/attachments/list"
            "POST", "/work/attachments/show"
            "POST", "/work/attachments/download"
        ]
        |> assertRoutesUseSecurity (Authorized(Operation.RepositoryRead, ResourceKind.Repository))
