namespace Grace.Server

open Asp.Versioning
open Asp.Versioning.ApiExplorer
open Azure.Monitor.OpenTelemetry.Exporter
open Azure.Storage.Blobs
open Azure.Storage.Blobs.Models
open Giraffe
open Giraffe.EndpointRouting
open Grace.Actors
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Server
open Grace.Server.Middleware
open Grace.Server.ReminderService
open Grace.Shared.Converters
open Grace.Shared.Parameters
open Grace.Types.Types
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

module Application =

    type CosmosWarmup(client: CosmosClient, log: ILogger<CosmosWarmup>) =
        interface IHostedService with
            member _.StartAsync(ct: CancellationToken) : Task =
                let rec loop i =
                    task {
                        if i > 120 then
                            log.LogError("Cosmos emulator was not ready in time.")
                            return ()
                        else
                            try
                                let! _ = client.ReadAccountAsync()
                                log.LogInformation("Cosmos emulator ready.")
                                return ()
                            with ex ->
                                log.LogWarning("Cosmos not ready, retry {Try}...", i)
                                do! Task.Delay(1000, ct)
                                return! loop (i + 1)
                    }

                log.LogInformation("The CosmosDB Emulator can take up to 2 minutes to be ready to receive https connections.")
                loop 1 :> Task

            member _.StopAsync(_ct: CancellationToken) : Task = Task.CompletedTask

    type Startup(configuration: IConfiguration) =

        do ApplicationContext.setConfiguration configuration

        let notLoggedIn = RequestErrors.UNAUTHORIZED "Basic" "Some Realm" "You must be logged in."

        let mustBeLoggedIn = requiresAuthentication notLoggedIn

        let graceServerVersion =
            FileVersionInfo
                .GetVersionInfo(Assembly.GetExecutingAssembly().Location)
                .FileVersion

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

                          route "/tag" Branch.Tag |> addMetadata typeof<Branch.CreateReferenceParameters> ] ]
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

                          route "/setDescription" Repository.SetDescription
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
                        [ route "/getUploadMetadataForFiles" Storage.GetUploadMetadataForFiles
                          |> addMetadata typeof<Storage.GetUploadMetadataForFilesParameters>

                          route "/getDownloadUri" Storage.GetDownloadUri
                          |> addMetadata typeof<Storage.GetDownloadUriParameters>

                          route "/getUploadUri" Storage.GetUploadUris
                          |> addMetadata typeof<Storage.GetUploadUriParameters> ] ]
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

                    activity
                        .AddTag("enduser.id", user.Identity.Name)
                        .AddTag("enduser.claims", claimsList.ToString())
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

        //| :? HttpResponse as response ->
        //    activity.AddTag("http.response_content_length", response.ContentLength) |> ignore
        //| _ -> activity.AddTag("eventName", eventName) |> ignore

        member _.ConfigureServices(services: IServiceCollection) =
            Constants.JsonSerializerOptions.Converters.Add(BranchDtoConverter())

            // Get the hosting environment.
            let env =
                (services
                    .First(fun service -> service.ImplementationType = Type.GetType("IWebHostEnvironment"))
                    .ImplementationInstance)
                :?> HostingEnvironment

            let azureMonitorConnectionString = Environment.GetEnvironmentVariable Constants.EnvironmentVariables.ApplicationInsightsConnectionString

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
            openApiInfo.Version <- "v0.1"
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
                    resourceBuilder
                        .AddService(graceServerAppId)
                        .AddTelemetrySdk()
                        .AddAttributes(globalOpenTelemetryAttributes)
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
                    traceBuilder
                        .AddAspNetCoreInstrumentation()
                        .AddHttpClientInstrumentation()
                        .AddSource(graceServerAppId)
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

            services.AddAuthentication() |> ignore

            services.AddW3CLogging(fun options ->
                options.FileName <- "Grace.Server.log-"

                options.LogDirectory <- Path.Combine(Environment.GetEnvironmentVariable("TEMP"), "Grace.Server.Logs"))
            |> ignore

            services
                .AddGiraffe()
                // Next line adds the Json serializer that Giraffe uses internally.
                .AddHostedService<CosmosWarmup>()
                .AddSingleton<Json.ISerializer>(Json.Serializer(Constants.JsonSerializerOptions))
                .AddSingleton<IPartitionKeyProvider, GracePartitionKeyProvider>()
                .AddRouting()
                .AddLogging()
                .AddHostedService<ReminderService>()
                .AddHttpLogging()
                .AddOrleans(fun siloBuilder ->
                    siloBuilder.Services.AddSerializer(fun serializerBuilder -> serializerBuilder.AddNodaTimeSerializers() |> ignore)
                    |> ignore)
            |> ignore

            services.AddSingleton<CosmosClient>(fun serviceProvider ->
                let configuration = serviceProvider.GetRequiredService<IConfiguration>()
                let cosmosConnectionString = configuration.GetValue<string>(Constants.EnvironmentVariables.AzureCosmosDBConnectionString) //?? throw new InvalidOperationException("Missing ConnectionStrings:cosmosdb");

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
                .AddJsonProtocol(fun options -> options.PayloadSerializerOptions <- Constants.JsonSerializerOptions)
            |> ignore

            logToConsole $"Exiting ConfigureServices."

        // List all services to the log.
        //services |> Seq.iter (fun service -> logToConsole $"Service: {service.ServiceType}.")

        member _.Configure(app: IApplicationBuilder, env: IWebHostEnvironment) =
            let blobServiceClient = Context.blobServiceClient
            let containers = blobServiceClient.GetBlobContainers()

            let directoryVersionContainerName = Environment.GetEnvironmentVariable Constants.EnvironmentVariables.DirectoryVersionContainerName

            if not <| containers.Any(fun c -> c.Name = directoryVersionContainerName) then
                logToConsole $"Creating blob container: {directoryVersionContainerName}."

                blobServiceClient.CreateBlobContainer(directoryVersionContainerName, PublicAccessType.None)
                |> ignore

            let diffContainerName = Environment.GetEnvironmentVariable Constants.EnvironmentVariables.DiffContainerName

            if not <| containers.Any(fun c -> c.Name = diffContainerName) then
                logToConsole $"Creating blob container: {diffContainerName}."

                blobServiceClient.CreateBlobContainer(diffContainerName, PublicAccessType.None)
                |> ignore

            let zipFileContainerName = Environment.GetEnvironmentVariable Constants.EnvironmentVariables.ZipFileContainerName

            if not <| containers.Any(fun c -> c.Name = zipFileContainerName) then
                logToConsole $"Creating blob container: {zipFileContainerName}."

                blobServiceClient.CreateBlobContainer(zipFileContainerName, PublicAccessType.None)
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
                    endpointBuilder.MapHub<Notifications.NotificationHub>("/notifications")
                    |> ignore)

                // If we get here, we didn't find a route.
                .UseGiraffe(notFoundHandler)

            // Set the global ApplicationContext.
            ApplicationContext.Set.Wait()

            //app.ApplicationServices.GetService<IGrainFactory>() |> ApplicationContext.setOrleansClient

            logToConsole $"Grace Server started successfully."
