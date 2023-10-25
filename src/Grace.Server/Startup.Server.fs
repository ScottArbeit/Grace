namespace Grace.Server

open Azure.Monitor.OpenTelemetry.Exporter
open Dapr.Actors.Client
open Dapr.Client
open Giraffe
open Giraffe.EndpointRouting
open Grace.Actors
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Server
open Grace.Server.Middleware
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Mvc
open Microsoft.AspNetCore.Mvc.Versioning
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Hosting.Internal
open Swashbuckle.AspNetCore.Swagger
open Swashbuckle.AspNetCore.SwaggerGen
open Microsoft.OpenApi.Models
open NodaTime
open OpenTelemetry
open OpenTelemetry.Exporter
open OpenTelemetry.Instrumentation.AspNetCore
open OpenTelemetry.Metrics
open OpenTelemetry.Resources
open OpenTelemetry.Trace
open System
open System.Linq
open System.Reflection
open System.Text.Json
open System.Collections.Generic
open System.Diagnostics
open System.Text
open System.IO
open Microsoft.Extensions.Configuration
open Grace.Shared.Converters
open Grace.Shared.Types

module Application =
    type FunctionThat_AddsOrUpdatesFile = DirectoryVersion -> FileVersion -> DirectoryVersion
    
    type FunctionThat_ChecksIfAFileIsInAnyOfTheseDirectories = DirectoryVersion array -> (FileVersion -> DirectoryVersion -> bool) -> DirectoryVersion array

    type Startup(configuration: IConfiguration) =

        do ApplicationContext.setConfiguration configuration

        let notLoggedIn =
            RequestErrors.UNAUTHORIZED
                "Basic"
                "Some Realm"
                "You must be logged in."
        
        let mustBeLoggedIn = requiresAuthentication notLoggedIn

        let endpoints = [
            GET [
                route "/" (warbler (fun _ ->
                                       let fileVersion = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).FileVersion
                                       htmlString $"<h1>Hello From Grace Server {fileVersion}!</h1><br/><p>The current server time is: {getCurrentInstantExtended()}.</p>"
                                   ))
                route "/healthz" (warbler (fun _ -> htmlString $"<h1>Grace server seems healthy!</h1><br/><p>The current server time is: {getCurrentInstantExtended()}.</p>"))
            ]
            PUT [
            ]
            subRoute "/branch" [
                GET [
                ]
                POST [
                    route "/checkpoint" Branch.Checkpoint
                    route "/commit" Branch.Commit
                    route "/create" Branch.Create
                    route "/delete" Branch.Delete
                    route "/enableCheckpoint" Branch.EnableCheckpoint
                    route "/enableCommit" Branch.EnableCommit
                    route "/enablePromotion" Branch.EnablePromotion
                    route "/enableSave" Branch.EnableSave
                    route "/enableTag" Branch.EnableTag
                    route "/get" Branch.Get
                    route "/getCheckpoints" Branch.GetCheckpoints
                    route "/getCommits" Branch.GetCommits
                    route "/getDiffsForReferenceType" Branch.GetDiffsForReferenceType
                    route "/getParentBranch" Branch.GetParentBranch
                    route "/getPromotions" Branch.GetPromotions
                    route "/getReference" Branch.GetReference
                    route "/getReferences" Branch.GetReferences
                    route "/getSaves" Branch.GetSaves
                    route "/getTags" (Branch.GetTags >=> mustBeLoggedIn)
                    route "/getVersion" Branch.GetVersion
                    route "/promote" Branch.Promote
                    route "/rebase" Branch.Rebase
                    route "/save" Branch.Save
                    route "/tag" Branch.Tag
                ]
            ]
            subRoute "/diff" [
                GET [
                ]
                POST [
                    route "/getDiff" Diff.GetDiff
                    route "/getDiffBySha256Hash" Diff.GetDiffBySha256Hash
                    route "/populate" Diff.Populate
                ]
            ]
            subRoute "/directory" [
                GET [
                ]
                POST [
                    route "/create" DirectoryVersion.Create
                    route "/get" DirectoryVersion.Get
                    route "/getByDirectoryIds" DirectoryVersion.GetByDirectoryIds
                    route "/getBySha256Hash" DirectoryVersion.GetBySha256Hash
                    route "/getDirectoryVersionsRecursive" DirectoryVersion.GetDirectoryVersionsRecursive
                    route "/saveDirectoryVersions" DirectoryVersion.SaveDirectoryVersions
                ]
            ]
            subRoute "/notifications" [
                GET [
                ]
                POST [
                    route "/post" Notifications.Post
                ]
            ]
            subRoute "/organization" [
                POST [
                    route "/create" Organization.Create
                    route "/delete" Organization.Delete
                    route "/get" Organization.Get
                    route "/listRepositories" Organization.ListRepositories
                    route "/setDescription" Organization.SetDescription
                    route "/setName" Organization.SetName
                    route "/setSearchVisibility" Organization.SetSearchVisibility
                    route "/setType" Organization.SetType
                    route "/undelete" Organization.Undelete
                ]
            ]
            subRoute "/owner" [
                POST [
                    route "/create" Owner.Create
                    route "/delete" Owner.Delete
                    route "/get" Owner.Get
                    route "/listOrganizations" Owner.ListOrganizations
                    route "/setDescription" Owner.SetDescription
                    route "/setName" Owner.SetName                   
                    route "/setSearchVisibility" Owner.SetSearchVisibility
                    route "/setType" Owner.SetType        
                    route "/undelete" Owner.Undelete
                ]
            ]
            subRoute "/repository" [
                GET [
                    
                ]
                POST [
                    route "/create" Repository.Create
                    route "/delete" Repository.Delete
                    route "/enableSingleStepPromotion" Repository.EnableSingleStepPromotion
                    route "/enableComplexPromotion" Repository.EnableComplexPromotion
                    route "/exists" Repository.Exists
                    route "/get" Repository.Get
                    route "/getBranches" Repository.GetBranches
                    route "/getBranchesByBranchId" Repository.GetBranchesByBranchId
                    route "/getReferencesByReferenceId" Repository.GetReferencesByReferenceId
                    route "/isEmpty" Repository.IsEmpty
                    route "/setCheckpointDays" Repository.SetCheckpointDays
                    route "/setDefaultServerApiVersion" Repository.SetDefaultServerApiVersion
                    route "/setDescription" Repository.SetDescription
                    route "/setRecordSaves" Repository.SetRecordSaves
                    route "/setSaveDays" Repository.SetSaveDays
                    route "/setStatus" Repository.SetStatus
                    route "/setVisibility" Repository.SetVisibility
                    route "/undelete" Repository.Undelete
                ]
            ]
            subRoute "/storage" [
                POST [
                    route "/filesExistInObjectStorage" Storage.FilesExistInObjectStorage
                    route "/getDownloadUri" Storage.GetDownloadUri
                    route "/getUploadUri" Storage.GetUploadUri
                    route "/deleteAllFromCosmosDb" Storage.DeleteAllFromCosmosDB
                ]
            ]
        ]

        let notFoundHandler =
            "Not Found"
            |> text
            |> RequestErrors.notFound

        let mutable currentWorkingSet = String.Empty
        let mutable maxWorkingSet = String.Empty
        let mutable lastMetricsUpdateTime = Instant.MinValue
        let mutable threadCount = String.Empty

        let enrichTelemetry (activity: Activity) (request: HttpRequest) = //(eventName: string) (obj: Object) =
            let currentProcess = Process.GetCurrentProcess()
            let context = request.HttpContext
            if (lastMetricsUpdateTime + Duration.FromSeconds 10.0) < getCurrentInstant() then
                currentWorkingSet <- currentProcess.WorkingSet64.ToString("N0")
                maxWorkingSet <- currentProcess.PeakWorkingSet64.ToString("N0")
                lastMetricsUpdateTime <- getCurrentInstant()
                threadCount <- currentProcess.Threads.Count.ToString("N0")

            let user = context.User                
            if user.Identity.IsAuthenticated then

                let claimsList = new StringBuilder()
                if not <| isNull user.Claims then
                    for claim in user.Claims do
                        claimsList.Append($"{claim.Type}:{claim.Value};") |> ignore
                if claimsList.Length > 1 then
                    claimsList.Remove(claimsList.Length - 1, 1) |> ignore
                activity.AddTag("enduser.id", user.Identity.Name)
                        .AddTag("enduser.claims", claimsList.ToString()) |> ignore
            activity.AddTag("working_set", currentWorkingSet)
                    .AddTag("max_working_set", maxWorkingSet)
                    .AddTag("thread_count", threadCount)
                    .AddTag("http.client_ip", context.Connection.RemoteIpAddress)
                    .AddTag("enduser.is_authenticated", user.Identity.IsAuthenticated) |> ignore

            //| :? HttpResponse as response ->
            //    activity.AddTag("http.response_content_length", response.ContentLength) |> ignore
            //| _ -> activity.AddTag("eventName", eventName) |> ignore

        member _.ConfigureServices (services: IServiceCollection) =
            let n = Constants.JsonSerializerOptions.Converters.Count
            Constants.JsonSerializerOptions.Converters.Add(BranchDtoConverter())
            //logToConsole $"Was {n}, now {Constants.JsonSerializerOptions.Converters.Count}."

            let env = (services.First(fun service -> service.ImplementationType = Type.GetType("IWebHostEnvironment")).ImplementationInstance) :?> HostingEnvironment

            let azureMonitorConnectionString = Environment.GetEnvironmentVariable("AZURE_MONITOR_CONNECTION_STRING")

            // OpenTelemetry trace attribute specifications: https://github.com/open-telemetry/opentelemetry-specification/tree/main/specification/trace/semantic_conventions
            let globalOpenTelemetryAttributes = Dictionary<string, obj>()
            globalOpenTelemetryAttributes.Add("host.name", Environment.MachineName)
            globalOpenTelemetryAttributes.Add("process.pid", Environment.ProcessId)
            globalOpenTelemetryAttributes.Add("process.starttime", Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("u"))
            globalOpenTelemetryAttributes.Add("process.executable.name", Process.GetCurrentProcess().ProcessName)
            globalOpenTelemetryAttributes.Add("process.runtime.version", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription)
            
            // Set up the ActorProxyFactory for the application.
            let actorProxyOptions = ActorProxyOptions(JsonSerializerOptions = Constants.JsonSerializerOptions)  // DaprApiToken = Environment.GetEnvironmentVariable("DAPR_API_TOKEN")) (when we actually implement auth)
            actorProxyOptions.HttpEndpoint <- $"{Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DaprServerUri)}:{Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DaprHttpPort)}"
            logToConsole $"actorProxyOptions.HttpEndpoint: {actorProxyOptions.HttpEndpoint}"
            let actorProxyFactory = ActorProxyFactory(actorProxyOptions)
            ApplicationContext.setActorProxyFactory actorProxyFactory
            ApplicationContext.setActorStateStorageProvider ActorStateStorageProvider.AzureCosmosDb

            let openApiInfo = new OpenApiInfo()
            openApiInfo.Description <- "Grace is a version control system. Code and documentation can be found at https://gracevcs.com."
            openApiInfo.Title <- "Grace Server API"
            openApiInfo.Version <- "v0.1"
            openApiInfo.Contact <- new OpenApiContact()
            openApiInfo.Contact.Name <- "Scott Arbeit"
            openApiInfo.Contact.Email <- "scott@scottarbeit.com"
            openApiInfo.Contact.Url <- Uri("https://gracevcs.com")

            services.AddOpenTelemetry()
                .WithTracing(fun config -> 
                    //if env.IsDevelopment() then
                    //    config.AddConsoleExporter(fun options -> options.Targets <- ConsoleExporterOutputTargets.Console) |> ignore
                    config.AddSource(Constants.GraceServerAppId)
                           .SetResourceBuilder(ResourceBuilder.CreateDefault()
                                .AddService(Constants.GraceServerAppId)
                                .AddTelemetrySdk()
                                .AddAttributes(globalOpenTelemetryAttributes))
                           .AddAspNetCoreInstrumentation(fun options -> options.EnrichWithHttpRequest <- enrichTelemetry)
                           .AddHttpClientInstrumentation()
                           .AddAzureMonitorTraceExporter(fun options -> options.ConnectionString <- azureMonitorConnectionString) |> ignore)
                           //.StartWithHost()
                           //.AddZipkinExporter()
                           |> ignore
            
            services.AddAuthentication() |> ignore

            services.AddRouting()
                    .AddLogging()
                    //.AddSwaggerGen(fun config -> config.SwaggerDoc("v0.1", openApiInfo))
                    .AddGiraffe()
                    //.AddSingleton(Constants.JsonSerializerOptions)
                    // Next line adds the Json serializer that Giraffe uses internally
                    .AddSingleton<Json.ISerializer>(SystemTextJson.Serializer(Constants.JsonSerializerOptions))
                    .AddW3CLogging(fun options -> options.FileName <- "Grace.Server.log-"
                                                  options.LogDirectory <- Path.Combine(Environment.GetEnvironmentVariable("TEMP"), "Grace.Server.Logs"))
                    .AddSingleton<ActorProxyOptions>(actorProxyOptions)
                    .AddSingleton<IActorProxyFactory>(actorProxyFactory)
                    .AddApiVersioning(fun config ->
                        config.DefaultApiVersion <- ApiVersion(DateTime(2023, 10, 1))
                        config.ApiVersionReader <- HeaderApiVersionReader(Constants.ServerApiVersionHeaderKey)
                        config.AssumeDefaultVersionWhenUnspecified <- true
                    )
                    .AddVersionedApiExplorer(fun config -> 
                        config.DefaultApiVersion <- ApiVersion(DateTime(2023, 10, 1))
                        config.AssumeDefaultVersionWhenUnspecified <- true
                        config.ApiVersionParameterSource <- HeaderApiVersionReader(Constants.ServerApiVersionHeaderKey))
                    .AddDaprClient(fun daprClientBuilder ->
                        daprClientBuilder.UseJsonSerializationOptions(Constants.JsonSerializerOptions)
                                         //.UseDaprApiToken(Environment.GetEnvironmentVariable("DAPR_API_TOKEN"))
                                         .Build() |> ignore
                    )
            
            // Configures the Dapr Actor subsystem.            
            services.AddActors(fun options ->
                options.JsonSerializerOptions <- Constants.JsonSerializerOptions
                options.HttpEndpoint <- actorProxyOptions.HttpEndpoint
            
                // When you create a new actor type, register it here.
                let actors = options.Actors
                actors.RegisterActor<Branch.BranchActor>()
                actors.RegisterActor<BranchName.BranchNameActor>()
                actors.RegisterActor<ContainerName.ContainerNameActor>()
                actors.RegisterActor<Diff.DiffActor>()
                actors.RegisterActor<DirectoryVersion.DirectoryVersionActor>()
                actors.RegisterActor<DirectoryAppearance.DirectoryAppearanceActor>()
                actors.RegisterActor<FileAppearance.FileAppearanceActor>()
                actors.RegisterActor<Organization.OrganizationActor>()
                actors.RegisterActor<OrganizationName.OrganizationNameActor>()
                actors.RegisterActor<Owner.OwnerActor>()
                actors.RegisterActor<OwnerName.OwnerNameActor>()
                actors.RegisterActor<Reference.ReferenceActor>()
                actors.RegisterActor<Repository.RepositoryActor>()
                actors.RegisterActor<RepositoryName.RepositoryNameActor>()

                // Default values for these options can be found at https://github.com/dapr/dapr/blob/master/pkg/actors/config.go.
                options.ActorIdleTimeout <- TimeSpan.FromMinutes(10.0)          // Default is 60m
                options.ActorScanInterval <- TimeSpan.FromSeconds(60.0)         // Default is 30s
                options.DrainOngoingCallTimeout <- TimeSpan.FromSeconds(30.0)   // Default is 60s
                options.DrainRebalancedActors <- true                           // Default is false
                options.RemindersStoragePartitions <- 32
            )

            services.AddSignalR(fun options -> options.EnableDetailedErrors <- true)
                    .AddJsonProtocol(fun options -> options.PayloadSerializerOptions <- Constants.JsonSerializerOptions) |> ignore

        member _.Configure(app: IApplicationBuilder, env: IWebHostEnvironment) =
            if env.IsDevelopment() then
                app//.UseSwagger()
                   //.UseSwaggerUI(fun config -> config.SwaggerEndpoint("/swagger", "Grace Server API"))
                   .UseDeveloperExceptionPage() |> ignore

            // This line enables Azure SDK (i.e. CosmosDB) OpenTelemetry output, which is currently in experimental support. 
            //   When this is fully supported, we can remove it.
            AppContext.SetSwitch("Azure.Experimental.EnableActivitySource", true)

            app.UseW3CLogging()
               .UseMiddleware<CorrelationIdMiddleware>()
               .UseMiddleware<HttpSecurityHeadersMiddleware>()
               .UseAuthentication()
               .UseStatusCodePages()
               .UseRouting()
               .UseStaticFiles()
               .UseEndpoints(fun endpointBuilder ->
                   // Add Dapr actor endpoints
                   endpointBuilder.MapActorsHandlers() |> ignore

                   // Add Dapr pub/sub endpoints
                   endpointBuilder.MapSubscribeHandler() |> ignore

                   // Add Giraffe (Web API) endpoints
                   endpointBuilder.MapGiraffeEndpoints(endpoints)

                   // Add SignalR hub endpoints
                   endpointBuilder.MapHub<Notifications.NotificationHub>("/notifications") |> ignore)
               .UseGiraffe(notFoundHandler)

            ApplicationContext.Set.Wait()
