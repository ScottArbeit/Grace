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
open Microsoft.Extensions.Logging
open Swashbuckle.AspNetCore.Swagger
open Swashbuckle.AspNetCore.SwaggerGen
open Microsoft.OpenApi.Models
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

module Application =

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
                route "/" (warbler (fun _ -> htmlString $"<h1>Hello From Grace!</h1><br/><p>{getCurrentInstantExtended()}.</p>"))
                //route "/healthz" (warbler (fun _ -> htmlString $"<h1>Hello From Grace!</h1><br/><p>{GetCurrentInstantExtended()}.</p>"))
            ]
            PUT [
            ]
            subRoute "/branch" [
                GET [
                ]
                POST [
                    route "/create" Branch.Create
                    route "/rebase" Branch.Rebase
                    route "/get" Branch.Get
                    route "/getParentBranch" Branch.GetParentBranch
                    route "/promote" Branch.Promote
                    route "/commit" Branch.Commit
                    route "/checkpoint" Branch.Checkpoint
                    route "/save" Branch.Save
                    route "/tag" Branch.Tag
                    route "/enablePromotion" Branch.EnablePromotion
                    route "/enableCommit" Branch.EnableCommit
                    route "/enableCheckpoint" Branch.EnableCheckpoint
                    route "/enableSave" Branch.EnableSave
                    route "/enableTag" Branch.EnableTag
                    route "/getPromotions" Branch.GetPromotions
                    route "/getCommits" Branch.GetCommits
                    route "/getCheckpoints" Branch.GetCheckpoints
                    route "/getReference" Branch.GetReference
                    route "/getReferences" Branch.GetReferences
                    route "/getDiffsForReferenceType" Branch.GetDiffsForReferenceType
                    route "/getSaves" Branch.GetSaves
                    route "/getTags" (Branch.GetTags >=> mustBeLoggedIn)
                    route "/getVersion" Branch.GetVersion
                    route "/delete" Branch.Delete
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
                    route "/create" Directory.Create
                    route "/get" Directory.Get
                    route "/getByDirectoryIds" Directory.GetByDirectoryIds
                    route "/getBySha256Hash" Directory.GetBySha256Hash
                    route "/getDirectoryVersionsRecursive" Directory.GetDirectoryVersionsRecursive
                    route "/saveDirectoryVersions" Directory.SaveDirectoryVersions
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
                    route "/setName" Organization.SetName
                    route "/setType" Organization.SetType
                    route "/setSearchVisibility" Organization.SetSearchVisibility
                    route "/setDescription" Organization.SetDescription
                    route "/listRepositories" Organization.ListRepositories
                    route "/delete" Organization.Delete
                    route "/undelete" Organization.Undelete
                ]
            ]
            subRoute "/owner" [
                POST [
                    route "/create" Owner.Create
                    route "/setName" Owner.SetName                   
                    route "/setType" Owner.SetType        
                    route "/setSearchVisibility" Owner.SetSearchVisibility
                    route "/setDescription" Owner.SetDescription
                    route "/listOrganizations" Owner.ListOrganizations
                    route "/delete" Owner.Delete
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

        let enrichTelemetry (activity: Activity) (request: HttpRequest) = //(eventName: string) (obj: Object) =
            let currentProcess = Process.GetCurrentProcess()
            let context = request.HttpContext
            let workingSet = currentProcess.WorkingSet64.ToString("N0")
            let threadCount = currentProcess.Threads.Count.ToString("N0")
            let maxWorkingSet = currentProcess.PeakWorkingSet64.ToString("N0")

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
            activity.AddTag("working_set", workingSet)
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
            
            //let actorProxyOptions = ActorProxyOptions(JsonSerializerOptions = Constants.JsonSerializerOptions, DaprApiToken = Environment.GetEnvironmentVariable("DAPR_API_TOKEN"))
            let actorProxyOptions = ActorProxyOptions(JsonSerializerOptions = Constants.JsonSerializerOptions)
            actorProxyOptions.HttpEndpoint <- $"{Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DaprServerUri)}:{Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DaprHttpPort)}"
            logToConsole $"actorProxyOptions.HttpEndpoint: {actorProxyOptions.HttpEndpoint}"
            let actorProxyFactory = ActorProxyFactory(actorProxyOptions)
            ApplicationContext.setActorProxyFactory actorProxyFactory

            let openApiInfo = new OpenApiInfo()
            openApiInfo.Description <- "Grace is a version control system. Code and documentation can be found at https://gracevcs.com."
            openApiInfo.Title <- "Grace Server API"
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
                    .AddSingleton(Constants.JsonSerializerOptions)
                    .AddSingleton<Json.ISerializer>(SystemTextJson.Serializer(Constants.JsonSerializerOptions))
                    //.AddSingleton<Json.ISerializer, SystemTextJson.Serializer>()
                    .AddW3CLogging(fun options -> options.FileName <- "Grace.Server.log-"
                                                  options.LogDirectory <- Path.Combine(Environment.GetEnvironmentVariable("TEMP"), "Grace.Server.Logs"))
                    .AddSingleton<ActorProxyOptions>(actorProxyOptions)
                    .AddSingleton<IActorProxyFactory>(actorProxyFactory)
                    .AddApiVersioning(fun config ->
                        config.DefaultApiVersion <- ApiVersion(DateTime(2022, 10, 1))
                        config.ApiVersionReader <- HeaderApiVersionReader(Constants.ServerApiVersionHeaderKey)
                        config.AssumeDefaultVersionWhenUnspecified <- true
                    )
                    .AddVersionedApiExplorer(fun config -> 
                        config.DefaultApiVersion <- ApiVersion(DateTime(2022, 10, 1))
                        config.AssumeDefaultVersionWhenUnspecified <- true
                        config.ApiVersionParameterSource <- HeaderApiVersionReader(Constants.ServerApiVersionHeaderKey))
                    .AddDaprClient(fun daprClientBuilder ->
                        daprClientBuilder.UseJsonSerializationOptions(Constants.JsonSerializerOptions)
                                         //.UseDaprApiToken(Environment.GetEnvironmentVariable("DAPR_API_TOKEN"))
                                         .Build() |> ignore
                    )
            
            // When you create a new actor type, add it here.
            services.AddActors(fun options ->
                options.JsonSerializerOptions <- Constants.JsonSerializerOptions
                options.HttpEndpoint <- actorProxyOptions.HttpEndpoint
                options.Actors.RegisterActor<Branch.BranchActor>()
                options.Actors.RegisterActor<BranchName.BranchNameActor>()
                options.Actors.RegisterActor<ContainerName.ContainerNameActor>()
                options.Actors.RegisterActor<Diff.DiffActor>()
                options.Actors.RegisterActor<Directory.DirectoryActor>()
                options.Actors.RegisterActor<DirectoryAppearance.DirectoryAppearanceActor>()
                options.Actors.RegisterActor<FileAppearance.FileAppearanceActor>()
                options.Actors.RegisterActor<Organization.OrganizationActor>()
                options.Actors.RegisterActor<OrganizationName.OrganizationNameActor>()
                options.Actors.RegisterActor<Owner.OwnerActor>()
                options.Actors.RegisterActor<OwnerName.OwnerNameActor>()
                options.Actors.RegisterActor<Reference.ReferenceActor>()
                options.Actors.RegisterActor<Repository.RepositoryActor>()
                options.Actors.RegisterActor<RepositoryName.RepositoryNameActor>()

                // Default values for these options can be found at https://github.com/dapr/dapr/blob/master/pkg/actors/config.go.
                options.ActorIdleTimeout <- TimeSpan.FromMinutes(10.0)          // Default is 60m
                options.ActorScanInterval <- TimeSpan.FromSeconds(60.0)         // Default is 30s
                options.DrainOngoingCallTimeout <- TimeSpan.FromSeconds(30.0)   // Default is 60s
                options.DrainRebalancedActors <- true                           // Default is false
                options.RemindersStoragePartitions <- 4
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
                   endpointBuilder.MapActorsHandlers() |> ignore
                   endpointBuilder.MapGiraffeEndpoints(endpoints)
                   endpointBuilder.MapHub<Notifications.NotificationHub>("/notification") |> ignore)
               .UseGiraffe(notFoundHandler)

            ApplicationContext.Set.Wait()
