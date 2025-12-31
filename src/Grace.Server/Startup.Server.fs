namespace Grace.Server

open Asp.Versioning
open Asp.Versioning.ApiExplorer
open Azure.Core
open Azure.Identity
open Azure.Monitor.OpenTelemetry.Exporter
open Azure.Storage.Blobs
open Azure.Storage.Blobs.Models
open Giraffe
open Giraffe.EndpointRouting
open Grace.Actors
open Grace.Shared
open Grace.Shared.AzureEnvironment
open Grace.Shared.Utilities
open Grace.Server
open Grace.Server.Middleware
open Grace.Server.ReminderService
open Grace.Server.Security
open Grace.Server.Security.TestAuth
open Grace.Shared.Converters
open Grace.Shared.Parameters
open Grace.Types.Types
open Grace.Types.Authorization
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Authentication.JwtBearer
open Microsoft.AspNetCore.Authentication.OpenIdConnect
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.HttpLogging
open Microsoft.AspNetCore.Mvc
open Microsoft.Azure.Cosmos
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Hosting.Internal
open Microsoft.Extensions.Logging
open Microsoft.OpenApi
open NodaTime
open OpenTelemetry
open OpenTelemetry.Exporter
open OpenTelemetry.Instrumentation.AspNetCore
open OpenTelemetry.Metrics
open OpenTelemetry.Resources
open OpenTelemetry.Trace
open Orleans
open Orleans.Persistence.Cosmos
open Orleans.Serialization
open Orleans.Serialization.NodaTime
open Swashbuckle.AspNetCore.Swagger
open System
open System.Linq
open System.Reflection
open System.Text.Json
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Linq
open System.Text
open System.Threading
open System.Threading.Tasks
open FSharpPlus
open System.Net.Http
open System.Net.Security
open Microsoft.IdentityModel.Tokens
open System.Security.Claims

module Application =

    type CosmosWarmup(cosmosClient: CosmosClient, log: ILogger<CosmosWarmup>) =
        interface IHostedService with
            member _.StartAsync(ct: CancellationToken) : Task =
                log.LogInformation("Waiting for Cosmos DB emulator to be ready...")

                let rec loop i =
                    task {
                        if i > 120 || ct.IsCancellationRequested then
                            log.LogError("Cosmos DB emulator was not ready in time.")
                            return ()
                        else
                            try
                                let! _ = cosmosClient.ReadAccountAsync()
                                log.LogInformation("Cosmos DB emulator ready.")
                                return ()
                            with ex ->
                                log.LogWarning("Cosmos DB emulator not ready, retry {Try}...", i)
                                do! Task.Delay(1000, ct)
                                return! loop (i + 1)
                    }

                loop 1 :> Task

            member _.StopAsync(_ct: CancellationToken) : Task = Task.CompletedTask

    let private defaultAzureCredential = lazy (DefaultAzureCredential())

    type Startup(configuration: IConfiguration) =

        do ApplicationContext.setConfiguration configuration

        let notLoggedIn = RequestErrors.UNAUTHORIZED "Basic" "Some Realm" "You must be logged in."

        let mustBeLoggedIn = requiresAuthentication notLoggedIn

        let graceServerVersion = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).FileVersion

        let repositoryResourceFromContext (context: HttpContext) =
            task {
                let graceIds = Services.getGraceIds context
                return Resource.Repository(graceIds.OwnerId, graceIds.OrganizationId, graceIds.RepositoryId)
            }

        let uploadPathResourcesFromContext (context: HttpContext) =
            task {
                context.Request.EnableBuffering()
                let! parameters = context.BindJsonAsync<Storage.GetUploadMetadataForFilesParameters>()
                context.Request.Body.Seek(0L, IO.SeekOrigin.Begin) |> ignore

                let graceIds = Services.getGraceIds context

                let resources =
                    parameters.FileVersions
                    |> Seq.map (fun fileVersion ->
                        Resource.Path(graceIds.OwnerId, graceIds.OrganizationId, graceIds.RepositoryId, fileVersion.RelativePath))
                    |> Seq.toList

                return resources
            }

        let composeHandlers (first: HttpHandler) (second: HttpHandler) : HttpHandler =
            fun next context -> first (second next) context

        let requireRepoAdmin: HttpHandler =
            AuthorizationMiddleware.requiresPermission Operation.RepoAdmin repositoryResourceFromContext

        let requirePathWrite: HttpHandler =
            AuthorizationMiddleware.requiresPermissions Operation.PathWrite uploadPathResourcesFromContext

        let endpoints =
            [ GET
                  [ route
                        "/"
                        (warbler (fun _ ->
                            htmlString
                                $"<h1>Hello From Grace Server {graceServerVersion}!</h1><br/><p>The current server time is: {getCurrentInstantExtended ()}.</p>"))
                    route
                        "/healthz"
                        (warbler (fun _ ->
                            htmlString $"<h1>Grace server seems healthy!</h1><br/><p>The current server time is: {getCurrentInstantExtended ()}.</p>")) ]
              PUT []
              subRoute
                  "/branch"
                  [ POST
                        [ route "/assign" Branch.Assign |> addMetadata typeof<Branch.AssignParameters>

                          route "/checkpoint" Branch.Checkpoint
                          |> addMetadata typeof<Branch.CreateReferenceParameters>

                          route "/commit" Branch.Commit
                          |> addMetadata typeof<Branch.CreateReferenceParameters>

                          route "/create" Branch.Create
                          |> addMetadata typeof<Branch.CreateBranchParameters>

                          route "/createExternal" Branch.CreateExternal
                          |> addMetadata typeof<Branch.CreateReferenceParameters>

                          route "/delete" Branch.Delete
                          |> addMetadata typeof<Branch.DeleteBranchParameters>

                          route "/enableAssign" Branch.EnableAssign
                          |> addMetadata typeof<Branch.EnableFeatureParameters>

                          route "/enableAutoRebase" Branch.EnableAutoRebase
                          |> addMetadata typeof<Branch.EnableFeatureParameters>

                          route "/enableCheckpoint" Branch.EnableCheckpoint
                          |> addMetadata typeof<Branch.EnableFeatureParameters>

                          route "/enableCommit" Branch.EnableCommit
                          |> addMetadata typeof<Branch.EnableFeatureParameters>

                          route "/enableExternal" Branch.EnableExternal
                          |> addMetadata typeof<Branch.EnableFeatureParameters>

                          route "/enablePromotion" Branch.EnablePromotion
                          |> addMetadata typeof<Branch.EnableFeatureParameters>

                          route "/enableSave" Branch.EnableSave
                          |> addMetadata typeof<Branch.EnableFeatureParameters>

                          route "/enableTag" Branch.EnableTag
                          |> addMetadata typeof<Branch.EnableFeatureParameters>

                          route "/setPromotionMode" Branch.SetPromotionMode
                          |> addMetadata typeof<Branch.SetPromotionModeParameters>

                          route "/get" Branch.Get |> addMetadata typeof<Branch.GetBranchParameters>

                          route "/getEvents" Branch.GetEvents
                          |> addMetadata typeof<Branch.GetBranchParameters>

                          route "/getExternals" Branch.GetExternals
                          |> addMetadata typeof<Branch.GetReferenceParameters>

                          route "/getCheckpoints" Branch.GetCheckpoints
                          |> addMetadata typeof<Branch.GetBranchParameters>

                          route "/getCommits" Branch.GetCommits
                          |> addMetadata typeof<Branch.GetBranchParameters>

                          route "/getDiffsForReferenceType" Branch.GetDiffsForReferenceType
                          |> addMetadata typeof<Branch.GetDiffsForReferenceTypeParameters>

                          route "/getParentBranch" Branch.GetParentBranch
                          |> addMetadata typeof<Branch.GetBranchParameters>

                          route "/getPromotions" Branch.GetPromotions
                          |> addMetadata typeof<Branch.GetReferenceParameters>

                          route "/getRecursiveSize" Branch.GetRecursiveSize
                          |> addMetadata typeof<Branch.ListContentsParameters>

                          route "/getReference" Branch.GetReference
                          |> addMetadata typeof<Branch.GetReferenceParameters>

                          route "/getReferences" Branch.GetReferences
                          |> addMetadata typeof<Branch.GetReferencesParameters>

                          route "/getSaves" Branch.GetSaves
                          |> addMetadata typeof<Branch.GetReferenceParameters>

                          route "/getTags" Branch.GetTags
                          |> addMetadata typeof<Branch.GetReferenceParameters>

                          route "/getVersion" Branch.GetVersion
                          |> addMetadata typeof<Branch.GetBranchVersionParameters>

                          route "/listContents" Branch.ListContents
                          |> addMetadata typeof<Branch.ListContentsParameters>

                          route "/promote" Branch.Promote
                          |> addMetadata typeof<Branch.CreateReferenceParameters>

                          route "/rebase" Branch.Rebase |> addMetadata typeof<Branch.RebaseParameters>

                          route "/save" Branch.Save
                          |> addMetadata typeof<Branch.CreateReferenceParameters>

                          route "/tag" Branch.Tag |> addMetadata typeof<Branch.CreateReferenceParameters>

                          route "/updateParentBranch" Branch.UpdateParentBranch
                          |> addMetadata typeof<Branch.UpdateParentBranchParameters> ] ]
              subRoute
                  "/diff"
                  [ POST
                        [ route "/getDiff" Diff.GetDiff |> addMetadata typeof<Diff.GetDiffParameters>

                          route "/getDiffBySha256Hash" Diff.GetDiffBySha256Hash
                          |> addMetadata typeof<Diff.GetDiffBySha256HashParameters>

                          route "/populate" Diff.Populate |> addMetadata typeof<Diff.PopulateParameters> ] ]
              subRoute
                  "/directory"
                  [ POST
                        [ route "/create" DirectoryVersion.Create
                          |> addMetadata typeof<DirectoryVersion.CreateParameters>

                          route "/get" DirectoryVersion.Get
                          |> addMetadata typeof<DirectoryVersion.GetParameters>

                          route "/getByDirectoryIds" DirectoryVersion.GetByDirectoryIds
                          |> addMetadata typeof<DirectoryVersion.GetByDirectoryIdsParameters>

                          route "/getBySha256Hash" DirectoryVersion.GetBySha256Hash
                          |> addMetadata typeof<DirectoryVersion.GetBySha256HashParameters>

                          route "/getDirectoryVersionsRecursive" DirectoryVersion.GetDirectoryVersionsRecursive
                          |> addMetadata typeof<DirectoryVersion.GetParameters>

                          route "/getZipFile" DirectoryVersion.GetZipFile
                          |> addMetadata typeof<DirectoryVersion.GetZipFileParameters>

                          route "/saveDirectoryVersions" DirectoryVersion.SaveDirectoryVersions
                          |> addMetadata typeof<DirectoryVersion.SaveDirectoryVersionsParameters> ] ]
              subRoute "/notifications" [ GET [] ]
              subRoute
                  "/organization"
                  [ POST
                        [ route "/create" Organization.Create
                          |> addMetadata typeof<Organization.CreateOrganizationParameters>

                          route "/delete" Organization.Delete
                          |> addMetadata typeof<Organization.DeleteOrganizationParameters>

                          route "/get" Organization.Get
                          |> addMetadata typeof<Organization.GetOrganizationParameters>

                          route "/listRepositories" Organization.ListRepositories
                          |> addMetadata typeof<Organization.GetOrganizationParameters>

                          route "/setDescription" Organization.SetDescription
                          |> addMetadata typeof<Organization.SetOrganizationDescriptionParameters>

                          route "/setName" Organization.SetName
                          |> addMetadata typeof<Organization.SetOrganizationNameParameters>

                          route "/setSearchVisibility" Organization.SetSearchVisibility
                          |> addMetadata typeof<Organization.SetOrganizationSearchVisibilityParameters>

                          route "/setType" Organization.SetType
                          |> addMetadata typeof<Organization.SetOrganizationTypeParameters>

                          route "/undelete" Organization.Undelete
                          |> addMetadata typeof<Organization.UndeleteOrganizationParameters> ] ]
              subRoute
                  "/owner"
                  [ POST
                        [ route "/create" Owner.Create |> addMetadata typeof<Owner.CreateOwnerParameters>

                          route "/delete" Owner.Delete |> addMetadata typeof<Owner.DeleteOwnerParameters>

                          route "/get" Owner.Get |> addMetadata typeof<Owner.GetOwnerParameters>

                          route "/listOrganizations" Owner.ListOrganizations
                          |> addMetadata typeof<Owner.GetOwnerParameters>

                          route "/setDescription" Owner.SetDescription
                          |> addMetadata typeof<Owner.SetOwnerDescriptionParameters>

                          route "/setName" Owner.SetName
                          |> addMetadata typeof<Owner.SetOwnerNameParameters>

                          route "/setSearchVisibility" Owner.SetSearchVisibility
                          |> addMetadata typeof<Owner.SetOwnerSearchVisibilityParameters>

                          route "/setType" Owner.SetType
                          |> addMetadata typeof<Owner.SetOwnerTypeParameters>

                          route "/undelete" Owner.Undelete
                          |> addMetadata typeof<Owner.UndeleteOwnerParameters> ] ]
              subRoute
                  "/promotionGroup"
                  [ POST
                        [ route "/create" PromotionGroup.Create
                          |> addMetadata typeof<PromotionGroup.CreatePromotionGroupParameters>

                          route "/get" PromotionGroup.Get
                          |> addMetadata typeof<PromotionGroup.GetPromotionGroupParameters>

                          route "/getEvents" PromotionGroup.GetEvents
                          |> addMetadata typeof<PromotionGroup.GetPromotionGroupParameters>

                          route "/addPromotion" PromotionGroup.AddPromotion
                          |> addMetadata typeof<PromotionGroup.AddPromotionParameters>

                          route "/removePromotion" PromotionGroup.RemovePromotion
                          |> addMetadata typeof<PromotionGroup.RemovePromotionParameters>

                          route "/reorderPromotions" PromotionGroup.ReorderPromotions
                          |> addMetadata typeof<PromotionGroup.ReorderPromotionsParameters>

                          route "/schedule" PromotionGroup.Schedule
                          |> addMetadata typeof<PromotionGroup.ScheduleParameters>

                          route "/markReady" PromotionGroup.MarkReady
                          |> addMetadata typeof<PromotionGroup.MarkReadyParameters>

                          route "/start" PromotionGroup.Start
                          |> addMetadata typeof<PromotionGroup.StartParameters>

                          route "/complete" PromotionGroup.Complete
                          |> addMetadata typeof<PromotionGroup.CompleteParameters>

                          route "/block" PromotionGroup.Block
                          |> addMetadata typeof<PromotionGroup.BlockParameters>

                          route "/delete" PromotionGroup.Delete
                          |> addMetadata typeof<PromotionGroup.DeletePromotionGroupParameters> ] ]
              subRoute
                  "/repository"
                  [ POST
                        [ route "/create" Repository.Create
                          |> addMetadata typeof<Repository.CreateRepositoryParameters>

                          route "/delete" Repository.Delete
                          |> addMetadata typeof<Repository.DeleteRepositoryParameters>

                          route "/exists" Repository.Exists
                          |> addMetadata typeof<Repository.RepositoryParameters>

                          route "/get" Repository.Get
                          |> addMetadata typeof<Repository.RepositoryParameters>

                          route "/getBranches" Repository.GetBranches
                          |> addMetadata typeof<Repository.GetBranchesParameters>

                          route "/getBranchesByBranchId" Repository.GetBranchesByBranchId
                          |> addMetadata typeof<Repository.GetBranchesByBranchIdParameters>

                          route "/getReferencesByReferenceId" Repository.GetReferencesByReferenceId
                          |> addMetadata typeof<Repository.GetReferencesByReferenceIdParameters>

                          route "/isEmpty" Repository.IsEmpty
                          |> addMetadata typeof<Repository.IsEmptyParameters>

                          route "/setAllowsLargeFiles" Repository.SetAllowsLargeFiles
                          |> addMetadata typeof<Repository.SetAllowsLargeFilesParameters>

                          route "/setAnonymousAccess" Repository.SetAnonymousAccess
                          |> addMetadata typeof<Repository.SetAnonymousAccessParameters>

                          route "/setCheckpointDays" Repository.SetCheckpointDays
                          |> addMetadata typeof<Repository.SetCheckpointDaysParameters>

                          route "/setDiffCacheDays" Repository.SetDiffCacheDays
                          |> addMetadata typeof<Repository.SetDiffCacheDaysParameters>

                          route "/setDirectoryVersionCacheDays" Repository.SetDirectoryVersionCacheDays
                          |> addMetadata typeof<Repository.SetDirectoryVersionCacheDaysParameters>

                          route "/setDefaultServerApiVersion" Repository.SetDefaultServerApiVersion
                          |> addMetadata typeof<Repository.SetDefaultServerApiVersionParameters>

                          route "/setConflictResolutionPolicy" Repository.SetConflictResolutionPolicy
                          |> addMetadata typeof<Repository.SetConflictResolutionPolicyParameters>

                          route "/setDescription" (composeHandlers requireRepoAdmin Repository.SetDescription)
                          |> addMetadata typeof<Repository.SetRepositoryDescriptionParameters>

                          route "/setLogicalDeleteDays" Repository.SetLogicalDeleteDays
                          |> addMetadata typeof<Repository.SetLogicalDeleteDaysParameters>

                          route "/setName" Repository.SetName
                          |> addMetadata typeof<Repository.SetRepositoryNameParameters>

                          route "/setRecordSaves" Repository.SetRecordSaves
                          |> addMetadata typeof<Repository.RecordSavesParameters>

                          route "/setSaveDays" Repository.SetSaveDays
                          |> addMetadata typeof<Repository.SetSaveDaysParameters>

                          route "/setStatus" Repository.SetStatus
                          |> addMetadata typeof<Repository.SetRepositoryStatusParameters>

                          route "/setVisibility" Repository.SetVisibility
                          |> addMetadata typeof<Repository.SetRepositoryVisibilityParameters>

                          route "/undelete" Repository.Undelete
                          |> addMetadata typeof<Repository.UndeleteRepositoryParameters> ] ]
              subRoute
                  "/storage"
                  [ POST
                        [ route "/getUploadMetadataForFiles" (composeHandlers requirePathWrite Storage.GetUploadMetadataForFiles)
                          |> addMetadata typeof<Storage.GetUploadMetadataForFilesParameters>

                          route "/getDownloadUri" Storage.GetDownloadUri
                          |> addMetadata typeof<Storage.GetDownloadUriParameters>

                          route "/getUploadUri" Storage.GetUploadUris
                          |> addMetadata typeof<Storage.GetUploadUriParameters> ] ]
              subRoute
                  "/access"
                  [ POST
                        [ route "/grantRole" Access.GrantRole
                          |> addMetadata typeof<Access.GrantRoleParameters>

                          route "/revokeRole" Access.RevokeRole
                          |> addMetadata typeof<Access.RevokeRoleParameters>

                          route "/listRoleAssignments" Access.ListRoleAssignments
                          |> addMetadata typeof<Access.ListRoleAssignmentsParameters>

                          route "/upsertPathPermission" Access.UpsertPathPermission
                          |> addMetadata typeof<Access.UpsertPathPermissionParameters>

                          route "/removePathPermission" Access.RemovePathPermission
                          |> addMetadata typeof<Access.RemovePathPermissionParameters>

                          route "/listPathPermissions" Access.ListPathPermissions
                          |> addMetadata typeof<Access.ListPathPermissionsParameters>

                          route "/checkPermission" Access.CheckPermission
                          |> addMetadata typeof<Access.CheckPermissionParameters> ]
                    GET [ route "/listRoles" Access.ListRoles ] ]
              subRoute
                  "/auth"
                  [ GET
                        [ route "/me" Auth.Me
                          route "/login" Auth.Login
                          routef "/login/%s" (fun providerId -> Auth.LoginProvider providerId)
                          route "/logout" Auth.Logout ] ]
              subRoute
                  "/reminder"
                  [ POST
                        [ route "/list" Reminder.List
                          |> addMetadata typeof<Reminder.ListRemindersParameters>

                          route "/get" Reminder.Get |> addMetadata typeof<Reminder.GetReminderParameters>

                          route "/delete" Reminder.Delete
                          |> addMetadata typeof<Reminder.DeleteReminderParameters>

                          route "/updateTime" Reminder.UpdateTime
                          |> addMetadata typeof<Reminder.UpdateReminderTimeParameters>

                          route "/reschedule" Reminder.Reschedule
                          |> addMetadata typeof<Reminder.RescheduleReminderParameters>

                          route "/create" Reminder.Create
                          |> addMetadata typeof<Reminder.CreateReminderParameters> ] ]
              subRoute
                  "/admin"
                  [ POST
                        [
#if DEBUG
                          route "/deleteAllFromCosmosDB" Storage.DeleteAllFromCosmosDB
                          route "/deleteAllRemindersFromCosmosDB" Storage.DeleteAllRemindersFromCosmosDB
#endif
                        ] ] ]

        let notFoundHandler = "Not Found" |> text |> RequestErrors.notFound

        let mutable currentWorkingSet = String.Empty
        let mutable maxWorkingSet = String.Empty
        let mutable lastMetricsUpdateTime = Instant.MinValue
        let mutable threadCount = String.Empty

        let enrichTelemetry (activity: Activity) (request: HttpRequest) = //(eventName: string) (obj: Object) =
            let currentProcess = Process.GetCurrentProcess()
            let context = request.HttpContext

            if (lastMetricsUpdateTime + Duration.FromSeconds 10.0) < getCurrentInstant () then
                currentWorkingSet <- currentProcess.WorkingSet64.ToString("N0")
                maxWorkingSet <- currentProcess.PeakWorkingSet64.ToString("N0")
                lastMetricsUpdateTime <- getCurrentInstant ()
                threadCount <- currentProcess.Threads.Count.ToString("N0")

            let user = context.User

            if user.Identity.IsAuthenticated then
                let claimsList = stringBuilderPool.Get()

                try
                    if not <| isNull user.Claims then
                        for claim in user.Claims do
                            claimsList.Append($"{claim.Type}:{claim.Value};") |> ignore

                    if claimsList.Length > 1 then
                        claimsList.Remove(claimsList.Length - 1, 1) |> ignore

                    activity.AddTag("enduser.id", user.Identity.Name).AddTag("enduser.claims", claimsList.ToString())
                    |> ignore
                finally
                    stringBuilderPool.Return(claimsList)

            activity
                .AddTag("working_set", currentWorkingSet)
                .AddTag("max_working_set", maxWorkingSet)
                .AddTag("thread_count", threadCount)
                .AddTag("http.client_ip", context.Connection.RemoteIpAddress)
                .AddTag("enduser.is_authenticated", user.Identity.IsAuthenticated)
            |> ignore

        member _.ConfigureServices(services: IServiceCollection) =
            let mutable configurationObj: obj = null

            if
                not
                <| memoryCache.TryGetValue(Constants.MemoryCache.GraceConfiguration, &configurationObj)
            then
                invalidOp "Grace configuration not found in memory cache."

            let configuration = configurationObj :?> IConfigurationRoot

            let azureMonitorConnectionString =
                let connectionString = Environment.GetEnvironmentVariable Constants.EnvironmentVariables.ApplicationInsightsConnectionString

                if String.IsNullOrWhiteSpace connectionString then
                    configuration["Grace:ApplicationInsightsConnectionString"]
                else
                    connectionString

            ApplicationContext.setActorStateStorageProvider ActorStateStorageProvider.AzureCosmosDb

            // OpenTelemetry trace attribute specifications: https://github.com/open-telemetry/opentelemetry-specification/tree/main/specification/trace/semantic_conventions
            let globalOpenTelemetryAttributes = Dictionary<string, obj>()
            globalOpenTelemetryAttributes.Add("host.name", Environment.MachineName)
            globalOpenTelemetryAttributes.Add("process.pid", Environment.ProcessId)
            globalOpenTelemetryAttributes.Add("process.starttime", Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("u"))
            globalOpenTelemetryAttributes.Add("process.executable.name", Process.GetCurrentProcess().ProcessName)
            globalOpenTelemetryAttributes.Add("process.runtime.version", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription)

            let openApiInfo = new OpenApiInfo()
            openApiInfo.Description <- "Grace is a version control system. Code and documentation can be found at https://gracevcs.com."
            openApiInfo.Title <- "Grace Server API"
            openApiInfo.Version <- "v0.2"
            openApiInfo.Contact <- new OpenApiContact()
            openApiInfo.Contact.Name <- "Scott Arbeit"
            openApiInfo.Contact.Email <- "scott.arbeit@outlook.com"
            openApiInfo.Contact.Url <- Uri("https://gracevcs.com")

            // Telemetry configuration
            let graceServerAppId = "grace-server-integration-test"

            let tracingOtlpEndpoint = Environment.GetEnvironmentVariable("OTLP_ENDPOINT_URL")
            let otel = services.AddOpenTelemetry()

            otel
                .ConfigureResource(fun resourceBuilder ->
                    resourceBuilder.AddService(graceServerAppId).AddTelemetrySdk().AddAttributes(globalOpenTelemetryAttributes)
                    |> ignore)

                .WithMetrics(fun metricsBuilder ->
                    metricsBuilder
                        .AddAspNetCoreInstrumentation()
                        .AddMeter("Microsoft.AspNetCore.Hosting")
                        .AddMeter("Microsoft.AspNetCore.Server.Kestrel")
                        .AddPrometheusExporter(fun prometheusOptions -> prometheusOptions.ScrapeEndpointPath <- "/metrics")
                    |> ignore

                    if not <| String.IsNullOrWhiteSpace(azureMonitorConnectionString) then
                        logToConsole "OpenTelemetry: Configuring Azure Monitor metrics exporter"

                        metricsBuilder.AddAzureMonitorMetricExporter(fun options -> options.ConnectionString <- azureMonitorConnectionString)
                        |> ignore)
                .WithTracing(fun traceBuilder ->
                    traceBuilder.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddSource(graceServerAppId)
                    |> ignore

                    if not <| String.IsNullOrWhiteSpace(tracingOtlpEndpoint) then
                        logToConsole $"OpenTelemetry: Configuring OTLP exporter to {tracingOtlpEndpoint}"

                        traceBuilder.AddOtlpExporter(fun options -> options.Endpoint <- Uri(tracingOtlpEndpoint))
                        |> ignore
                    else
                        traceBuilder.AddConsoleExporter() |> ignore

                    if not <| String.IsNullOrWhiteSpace(azureMonitorConnectionString) then
                        logToConsole "OpenTelemetry: Configuring Azure Monitor trace exporter"

                        traceBuilder.AddAzureMonitorTraceExporter(fun options -> options.ConnectionString <- azureMonitorConnectionString)
                        |> ignore)
            |> ignore

            let isTesting =
                match Environment.GetEnvironmentVariable("GRACE_TESTING") with
                | null -> false
                | value ->
                    value.Equals("1", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("yes", StringComparison.OrdinalIgnoreCase)

            if isTesting then
                services
                    .AddAuthentication(fun options ->
                        options.DefaultAuthenticateScheme <- SchemeName
                        options.DefaultChallengeScheme <- SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, GraceTestAuthHandler>(SchemeName, fun _ -> ())
                |> ignore
            else
                let authBuilder =
                    services.AddAuthentication(fun options ->
                        options.DefaultScheme <- "GraceAuth"
                        options.DefaultChallengeScheme <- ExternalAuthConfig.MicrosoftScheme)

                authBuilder
                    .AddPolicyScheme(
                        "GraceAuth",
                        "GraceAuth",
                        fun options ->
                            options.ForwardDefaultSelector <-
                                fun context ->
                                    let authorization = context.Request.Headers.Authorization.ToString()

                                    if
                                        not (String.IsNullOrWhiteSpace authorization)
                                        && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                                    then
                                        JwtBearerDefaults.AuthenticationScheme
                                    else
                                        CookieAuthenticationDefaults.AuthenticationScheme
                    )
                    .AddCookie(
                        CookieAuthenticationDefaults.AuthenticationScheme,
                        fun options ->
                            options.SlidingExpiration <- true
                            options.Cookie.HttpOnly <- true
                    )
                |> ignore

                match ExternalAuthConfig.tryGetMicrosoftConfig configuration with
                | Some microsoftConfig when microsoftConfig.ClientSecret.IsSome ->
                    let authority =
                        let trimmed = microsoftConfig.Authority.TrimEnd('/')
                        if trimmed.EndsWith("/v2.0", StringComparison.OrdinalIgnoreCase) then
                            trimmed
                        else
                            $"{trimmed}/v2.0"

                    let apiAudience =
                        let trimmed = microsoftConfig.ApiScope.TrimEnd('/')
                        if String.IsNullOrWhiteSpace trimmed then
                            microsoftConfig.ClientId
                        elif trimmed.Contains("/") then
                            trimmed.Substring(0, trimmed.LastIndexOf('/'))
                        else
                            trimmed

                    let issuerValidator : IssuerValidator =
                        let allowAnyTenant =
                            let tenantId = microsoftConfig.TenantId.Trim()
                            tenantId.Equals("common", StringComparison.OrdinalIgnoreCase)
                            || tenantId.Equals("organizations", StringComparison.OrdinalIgnoreCase)
                            || tenantId.Equals("consumers", StringComparison.OrdinalIgnoreCase)

                        let prefix = "https://login.microsoftonline.com/"
                        let suffix = "/v2.0"

                        IssuerValidator(fun issuer _ _ ->
                            if String.IsNullOrWhiteSpace issuer then
                                raise (SecurityTokenInvalidIssuerException("Issuer is missing."))

                            let normalized = issuer.TrimEnd('/')
                            let matchesPattern =
                                normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                && normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)

                            if not matchesPattern then
                                raise (SecurityTokenInvalidIssuerException($"Issuer '{issuer}' is not a valid Microsoft v2 issuer."))

                            if allowAnyTenant then
                                issuer
                            else
                                let tenantSegment =
                                    normalized.Substring(
                                        prefix.Length,
                                        normalized.Length - prefix.Length - suffix.Length
                                    )

                                if tenantSegment.Equals(microsoftConfig.TenantId, StringComparison.OrdinalIgnoreCase) then
                                    issuer
                                else
                                    raise (
                                        SecurityTokenInvalidIssuerException(
                                            $"Issuer '{issuer}' does not match tenant '{microsoftConfig.TenantId}'."
                                        )
                                    ))

                    authBuilder
                        .AddOpenIdConnect(
                            ExternalAuthConfig.MicrosoftScheme,
                            fun options ->
                                options.Authority <- authority
                                options.ClientId <- microsoftConfig.ClientId
                                options.ClientSecret <- microsoftConfig.ClientSecret.Value
                                options.CallbackPath <- PathString("/signin-microsoft")
                                options.SignInScheme <- CookieAuthenticationDefaults.AuthenticationScheme
                                // Force auth code flow to avoid id_token implicit requirements.
                                options.ResponseType <- "code"
                                options.UsePkce <- true
                                options.SaveTokens <- true
                                options.GetClaimsFromUserInfoEndpoint <- true
                                options.Scope.Clear()
                                options.Scope.Add("openid") |> ignore
                                options.Scope.Add("profile") |> ignore
                                options.Scope.Add("email") |> ignore

                                if not (String.IsNullOrWhiteSpace microsoftConfig.ApiScope) then
                                    options.Scope.Add(microsoftConfig.ApiScope) |> ignore

                                options.TokenValidationParameters <-
                                    TokenValidationParameters(
                                        NameClaimType = "name",
                                        RoleClaimType = "roles",
                                        IssuerValidator = issuerValidator
                                    )
                        )
                        .AddJwtBearer(
                            JwtBearerDefaults.AuthenticationScheme,
                            fun options ->
                                options.Authority <- authority
                                options.TokenValidationParameters <-
                                    TokenValidationParameters(
                                        ValidateIssuer = true,
                                        NameClaimType = "name",
                                        RoleClaimType = "roles",
                                        ValidAudiences = [| apiAudience; microsoftConfig.ClientId; microsoftConfig.ApiScope |],
                                        IssuerValidator = issuerValidator
                                    )
                        )
                    |> ignore
                | _ -> ()

            services.AddTransient<IClaimsTransformation, GraceClaimsTransformation>() |> ignore

            services.AddSingleton<IGracePermissionEvaluator, GracePermissionEvaluator>() |> ignore

            services.AddW3CLogging(fun options ->
                options.FileName <- "Grace.Server.log-"

                options.LogDirectory <- Path.Combine(Environment.GetEnvironmentVariable("TEMP"), "Grace.Server.Logs"))
            |> ignore

            services
                .AddHostedService<CosmosWarmup>()
                .AddGiraffe()
                // Next line adds the Json serializer that Giraffe uses internally.
                .AddSingleton<Json.ISerializer>(Json.Serializer(Constants.JsonSerializerOptions))
                .AddSingleton<IPartitionKeyProvider, GracePartitionKeyProvider>()
                .AddRouting()
                .AddLogging()
                .AddHostedService<ReminderService>()
                .AddHostedService<Notification.Subscriber.GraceEventSubscriptionService>()
                .AddHttpLogging()
                .AddOrleans(fun siloBuilder ->
                    siloBuilder.Services.AddSerializer(fun serializerBuilder -> serializerBuilder.AddNodaTimeSerializers() |> ignore)
                    |> ignore)
            |> ignore

            services.AddSingleton<CosmosClient>(fun serviceProvider ->
                let cosmosConnectionString = configuration.GetValue<string>(getConfigKey Constants.EnvironmentVariables.AzureCosmosDBConnectionString)

                // Force SNI = "localhost" while we connect to 127.0.0.1.
                let httpHandler =
                    // Create and configure SslClientAuthenticationOptions
                    let sslOptions = SslClientAuthenticationOptions()
                    sslOptions.TargetHost <- "localhost" // SNI host_name must be DNS per RFC 6066
                    sslOptions.RemoteCertificateValidationCallback <- RemoteCertificateValidationCallback(fun _ _ _ _ -> true)
                    new SocketsHttpHandler(SslOptions = sslOptions)

                let options =
                    new CosmosClientOptions(
                        ConnectionMode = ConnectionMode.Gateway,
                        UseSystemTextJsonSerializerWithOptions = Constants.JsonSerializerOptions,
                        HttpClientFactory = (fun () -> new HttpClient(httpHandler, disposeHandler = true)),
                        LimitToEndpoint = true // prevents discovery probes that can trigger TLS issues on emulator
                    )

                if AzureEnvironment.useManagedIdentity then
                    let endpoint =
                        AzureEnvironment.tryGetCosmosEndpointUri ()
                        |> Option.defaultWith (fun () -> invalidOp "Azure Cosmos DB endpoint must be configured when using a managed identity.")

                    new CosmosClient(endpoint.AbsoluteUri, defaultAzureCredential.Value, options)
                else
                    if String.IsNullOrWhiteSpace cosmosConnectionString then
                        invalidOp "Azure Cosmos DB connection string is required when managed identity is disabled."

                    new CosmosClient(cosmosConnectionString, options))
            |> ignore

            let apiVersioningBuilder =
                services.AddApiVersioning(fun options ->
                    options.ReportApiVersions <- true
                    options.DefaultApiVersion <- new ApiVersion(1, 0)
                    options.AssumeDefaultVersionWhenUnspecified <- true
                    // Use whatever reader you want
                    options.ApiVersionReader <-
                        ApiVersionReader.Combine(
                            new UrlSegmentApiVersionReader(),
                            new HeaderApiVersionReader("x-api-version"),
                            new MediaTypeApiVersionReader("x-api-version")
                        ))

            apiVersioningBuilder.AddApiExplorer(fun options ->
                // add the versioned api explorer, which also adds IApiVersionDescriptionProvider service
                // note: the specified format code will format the version as "'v'major[.minor][-status]"
                options.GroupNameFormat <- "'v'VVV"
                options.DefaultApiVersion <- ApiVersion(DateOnly(2023, 10, 1))
                options.AssumeDefaultVersionWhenUnspecified <- true
                options.ApiVersionParameterSource <- HeaderApiVersionReader(Constants.ServerApiVersionHeaderKey)

                // note: this option is only necessary when versioning by url segment. the SubstitutionFormat
                // can also be used to control the format of the API version in route templates
                options.SubstituteApiVersionInUrl <- true)
            |> ignore

            services
                .AddSignalR(fun options -> options.EnableDetailedErrors <- true)
                //.AddStackExchangeRedis(
                //    "localhost:6379",
                //    fun options ->
                //        options.Configuration.ChannelPrefix <- StackExchange.Redis.RedisChannel.Literal("grace-server")
                //        options.Configuration.AbortOnConnectFail <- false
                //)
                .AddJsonProtocol(fun options -> options.PayloadSerializerOptions <- Constants.JsonSerializerOptions)
            |> ignore

            logToConsole $"Exiting ConfigureServices."

        // List all services to the log.
        //services |> Seq.iter (fun service -> logToConsole $"Service: {service.ServiceType}.")

        member _.Configure(app: IApplicationBuilder, env: IWebHostEnvironment) =
            let blobServiceClient = Context.blobServiceClient
            let containers = blobServiceClient.GetBlobContainers()

            let diffContainerName = Environment.GetEnvironmentVariable Constants.EnvironmentVariables.DiffContainerName

            if not <| containers.Any(fun c -> c.Name = diffContainerName) then
                logToConsole $"Creating blob container: {diffContainerName}."

                blobServiceClient.CreateBlobContainer(diffContainerName, PublicAccessType.None)
                |> ignore

            if env.IsDevelopment() then
                app //.UseSwagger()
                    //.UseSwaggerUI(fun config -> config.SwaggerEndpoint("/swagger", "Grace Server API"))
                    .UseDeveloperExceptionPage()
                |> ignore

            app
                //.UseMiddleware<FakeMiddleware>()
                .UseMiddleware<HttpSecurityHeadersMiddleware>()
                .UseW3CLogging()
                .UseMiddleware<CorrelationIdMiddleware>()
                .UseMiddleware<LogRequestHeadersMiddleware>()
                .UseStaticFiles()
                .UseRouting()
                .UseAuthentication()
                .UseAuthorization()
                .UseStatusCodePages()
                //.UseMiddleware<TimingMiddleware>()
                .UseMiddleware<ValidateIdsMiddleware>()
                .UseEndpoints(fun endpointBuilder ->
                    // Add Giraffe (Web API) endpoints
                    endpointBuilder.MapGiraffeEndpoints(endpoints)

                    // Add Prometheus scraping endpoint
                    endpointBuilder.MapPrometheusScrapingEndpoint() |> ignore

                    // Add SignalR hub endpoints
                    endpointBuilder.MapHub<Notification.NotificationHub>("/notifications") |> ignore)

                // If we get here, we didn't find a route.
                .UseGiraffe(notFoundHandler)

            // Set the global ApplicationContext.
            ApplicationContext.serviceProvider <- app.ApplicationServices
            ApplicationContext.Set().Wait()

            logToConsole $"Grace Server started successfully."
