namespace Grace.Server

open Azure.Monitor.OpenTelemetry.Exporter
open Dapr
open Dapr.Actors.Client
open Dapr.Client
open Giraffe
open Giraffe.EndpointRouting
open Grace.Actors
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Server
open Grace.Server.Middleware
open Grace.Shared.Converters
open Grace.Shared.Parameters
open Grace.Shared.Types
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.HttpLogging
open Microsoft.AspNetCore.Mvc
open Asp.Versioning
open Asp.Versioning.ApiExplorer
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Hosting.Internal
open Microsoft.OpenApi.Models
open NodaTime
open OpenTelemetry
open OpenTelemetry.Exporter
open OpenTelemetry.Instrumentation.AspNetCore
open OpenTelemetry.Metrics
open OpenTelemetry.Resources
open OpenTelemetry.Trace
open Swashbuckle.AspNetCore.Swagger
open Swashbuckle.AspNetCore.SwaggerGen
open System
open System.Linq
open System.Reflection
open System.Text.Json
open System.Collections.Generic
open System.Diagnostics
open System.Text
open System.IO

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
                    route "/checkpoint" Branch.Checkpoint |> addMetadata typeof<Branch.CreateReferenceParameters>
                    route "/commit" Branch.Commit |> addMetadata typeof<Branch.CreateReferenceParameters>
                    route "/create" Branch.Create |> addMetadata typeof<Branch.CreateBranchParameters>
                    route "/delete" Branch.Delete |> addMetadata typeof<Branch.DeleteBranchParameters>
                    route "/enableCheckpoint" Branch.EnableCheckpoint |> addMetadata typeof<Branch.EnableFeatureParameters>
                    route "/enableCommit" Branch.EnableCommit |> addMetadata typeof<Branch.EnableFeatureParameters>
                    route "/enablePromotion" Branch.EnablePromotion |> addMetadata typeof<Branch.EnableFeatureParameters>
                    route "/enableSave" Branch.EnableSave |> addMetadata typeof<Branch.EnableFeatureParameters>
                    route "/enableTag" Branch.EnableTag |> addMetadata typeof<Branch.EnableFeatureParameters>
                    route "/get" Branch.Get |> addMetadata typeof<Branch.GetBranchParameters>
                    route "/getEvents" Branch.GetEvents |> addMetadata typeof<Branch.GetBranchParameters>
                    route "/getCheckpoints" Branch.GetCheckpoints |> addMetadata typeof<Branch.GetBranchParameters>
                    route "/getCommits" Branch.GetCommits |> addMetadata typeof<Branch.GetBranchParameters>
                    route "/getDiffsForReferenceType" Branch.GetDiffsForReferenceType |> addMetadata typeof<Branch.GetDiffsForReferenceTypeParameters>
                    route "/getParentBranch" Branch.GetParentBranch |> addMetadata typeof<Branch.GetBranchParameters>
                    route "/getPromotions" Branch.GetPromotions |> addMetadata typeof<Branch.GetReferenceParameters>
                    route "/getRecursiveSize" Branch.GetRecursiveSize |> addMetadata typeof<Branch.ListContentsParameters>
                    route "/getReference" Branch.GetReference |> addMetadata typeof<Branch.GetReferenceParameters>
                    route "/getReferences" Branch.GetReferences |> addMetadata typeof<Branch.GetReferencesParameters>
                    route "/getSaves" Branch.GetSaves |> addMetadata typeof<Branch.GetReferenceParameters>
                    route "/getTags" (Branch.GetTags >=> mustBeLoggedIn) |> addMetadata typeof<Branch.GetReferenceParameters>
                    route "/getVersion" Branch.GetVersion |> addMetadata typeof<Branch.GetBranchVersionParameters>
                    route "/listContents" Branch.ListContents |> addMetadata typeof<Branch.ListContentsParameters>
                    route "/promote" Branch.Promote |> addMetadata typeof<Branch.CreateReferenceParameters>
                    route "/rebase" Branch.Rebase |> addMetadata typeof<Branch.RebaseParameters>
                    route "/save" Branch.Save |> addMetadata typeof<Branch.CreateReferenceParameters>
                    route "/tag" Branch.Tag |> addMetadata typeof<Branch.CreateReferenceParameters>
                ]
            ]
            subRoute "/diff" [
                GET [
                ]
                POST [
                    route "/getDiff" Diff.GetDiff |> addMetadata typeof<Diff.GetDiffParameters>
                    route "/getDiffBySha256Hash" Diff.GetDiffBySha256Hash |> addMetadata typeof<Diff.GetDiffBySha256HashParameters>
                    route "/populate" Diff.Populate |> addMetadata typeof<Diff.PopulateParameters>
                ]
            ]
            subRoute "/directory" [
                GET [
                ]
                POST [
                    route "/create" DirectoryVersion.Create |> addMetadata typeof<Directory.CreateParameters>
                    route "/get" DirectoryVersion.Get |> addMetadata typeof<Directory.GetParameters>
                    route "/getByDirectoryIds" DirectoryVersion.GetByDirectoryIds |> addMetadata typeof<Directory.GetByDirectoryIdsParameters>
                    route "/getBySha256Hash" DirectoryVersion.GetBySha256Hash |> addMetadata typeof<Directory.GetBySha256HashParameters>
                    route "/getDirectoryVersionsRecursive" DirectoryVersion.GetDirectoryVersionsRecursive |> addMetadata typeof<Directory.GetParameters>
                    route "/saveDirectoryVersions" DirectoryVersion.SaveDirectoryVersions |> addMetadata typeof<Directory.SaveDirectoryVersionsParameters>
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
                    route "/create" Organization.Create |> addMetadata typeof<Organization.CreateOrganizationParameters>
                    route "/delete" Organization.Delete |> addMetadata typeof<Organization.DeleteOrganizationParameters>
                    route "/get" Organization.Get |> addMetadata typeof<Organization.GetOrganizationParameters>
                    route "/listRepositories" Organization.ListRepositories |> addMetadata typeof<Organization.GetOrganizationParameters>
                    route "/setDescription" Organization.SetDescription |> addMetadata typeof<Organization.SetOrganizationDescriptionParameters>
                    route "/setName" Organization.SetName |> addMetadata typeof<Organization.SetOrganizationNameParameters>
                    route "/setSearchVisibility" Organization.SetSearchVisibility |> addMetadata typeof<Organization.SetOrganizationSearchVisibilityParameters>
                    route "/setType" Organization.SetType |> addMetadata typeof<Organization.SetOrganizationTypeParameters>
                    route "/undelete" Organization.Undelete |> addMetadata typeof<Organization.UndeleteOrganizationParameters>
                ]
            ]
            subRoute "/owner" [
                POST [
                    route "/create" Owner.Create |> addMetadata typeof<Owner.CreateOwnerParameters>
                    route "/delete" Owner.Delete |> addMetadata typeof<Owner.DeleteOwnerParameters>
                    route "/get" Owner.Get |> addMetadata typeof<Owner.GetOwnerParameters>
                    route "/listOrganizations" Owner.ListOrganizations |> addMetadata typeof<Owner.GetOwnerParameters>
                    route "/setDescription" Owner.SetDescription |> addMetadata typeof<Owner.SetOwnerDescriptionParameters>
                    route "/setName" Owner.SetName |> addMetadata typeof<Owner.SetOwnerNameParameters>
                    route "/setSearchVisibility" Owner.SetSearchVisibility |> addMetadata typeof<Owner.SetOwnerSearchVisibilityParameters>
                    route "/setType" Owner.SetType |> addMetadata typeof<Owner.SetOwnerTypeParameters>
                    route "/undelete" Owner.Undelete |> addMetadata typeof<Owner.UndeleteOwnerParameters>
                ]
            ]
            subRoute "/repository" [
                GET [
                    
                ]
                POST [
                    route "/create" Repository.Create |> addMetadata typeof<Repository.CreateRepositoryParameters>
                    route "/delete" Repository.Delete |> addMetadata typeof<Repository.DeleteRepositoryParameters>
                    route "/exists" Repository.Exists |> addMetadata typeof<Repository.RepositoryParameters>
                    route "/get" Repository.Get |> addMetadata typeof<Repository.RepositoryParameters>
                    route "/getBranches" Repository.GetBranches |> addMetadata typeof<Repository.GetBranchesParameters>
                    route "/getBranchesByBranchId" Repository.GetBranchesByBranchId |> addMetadata typeof<Repository.GetBranchesByBranchIdParameters>
                    route "/getReferencesByReferenceId" Repository.GetReferencesByReferenceId |> addMetadata typeof<Repository.GetReferencesByReferenceIdParameters>
                    route "/isEmpty" Repository.IsEmpty |> addMetadata typeof<Repository.IsEmptyParameters>
                    route "/setCheckpointDays" Repository.SetCheckpointDays |> addMetadata typeof<Repository.SetCheckpointDaysParameters>
                    route "/setDiffCacheDays" Repository.SetDiffCacheDays |> addMetadata typeof<Repository.SetDiffCacheDaysParameters>
                    route "/setDirectoryVersionCacheDays" Repository.SetDirectoryVersionCacheDays |> addMetadata typeof<Repository.SetDirectoryVersionCacheDaysParameters>
                    route "/setDefaultServerApiVersion" Repository.SetDefaultServerApiVersion |> addMetadata typeof<Repository.SetDefaultServerApiVersionParameters>
                    route "/setDescription" Repository.SetDescription |> addMetadata typeof<Repository.SetRepositoryDescriptionParameters>
                    route "/setName" Repository.SetName |> addMetadata typeof<Repository.SetRepositoryNameParameters>
                    route "/setRecordSaves" Repository.SetRecordSaves |> addMetadata typeof<Repository.RecordSavesParameters>
                    route "/setSaveDays" Repository.SetSaveDays |> addMetadata typeof<Repository.SetSaveDaysParameters>
                    route "/setStatus" Repository.SetStatus |> addMetadata typeof<Repository.SetRepositoryStatusParameters>
                    route "/setVisibility" Repository.SetVisibility |> addMetadata typeof<Repository.SetRepositoryVisibilityParameters>
                    route "/undelete" Repository.Undelete |> addMetadata typeof<Repository.UndeleteRepositoryParameters>
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
            Constants.JsonSerializerOptions.Converters.Add(BranchDtoConverter())

            // Get the hosting environment.
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
            let actorProxyOptions = ActorProxyOptions()  // DaprApiToken = Environment.GetEnvironmentVariable("DAPR_API_TOKEN")) (when we actually implement auth)
            actorProxyOptions.HttpEndpoint <- $"{Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DaprServerUri)}:{Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DaprHttpPort)}"
            actorProxyOptions.JsonSerializerOptions <- Constants.JsonSerializerOptions
            actorProxyOptions.RequestTimeout <- TimeSpan.FromSeconds(20.0)
            //logToConsole $"actorProxyOptions.HttpEndpoint: {actorProxyOptions.HttpEndpoint}"
            let actorProxyFactory = new ActorProxyFactory(actorProxyOptions)
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

            // Telemetry configuration
            services.AddOpenTelemetry()
                .ConfigureResource(fun resourceBuilder -> resourceBuilder.AddService(Constants.GraceServerAppId) |> ignore)
                .WithTracing(fun tracerProviderBuilder ->
                    tracerProviderBuilder.AddSource(Constants.GraceServerAppId)
                           .SetResourceBuilder(ResourceBuilder.CreateDefault()
                                .AddService(Constants.GraceServerAppId)
                                .AddTelemetrySdk()
                                .AddAttributes(globalOpenTelemetryAttributes))
                           .AddAspNetCoreInstrumentation(fun options -> options.EnrichWithHttpRequest <- enrichTelemetry)
                           .AddHttpClientInstrumentation()
                           .AddAzureMonitorTraceExporter(fun options -> options.ConnectionString <- azureMonitorConnectionString) |> ignore)
                .WithMetrics(fun meterProviderBuilder ->
                    meterProviderBuilder.AddAspNetCoreInstrumentation()
                          .AddHttpClientInstrumentation()
                          .AddAzureMonitorMetricExporter(fun options -> options.ConnectionString <- azureMonitorConnectionString)
                          .AddOtlpExporter()
                          |> ignore)
                           //.StartWithHost()
                           //.AddZipkinExporter()
                //.WithLogging().ConfigureResource(fun resourceBuilder -> 
                //    resourceBuilder.AddService(Constants.GraceServerAppId)
                //                   .AddTelemetrySdk()
                //                   .AddAttributes(globalOpenTelemetryAttributes) |> ignore)
                |> ignore
            
            services.AddAuthentication() |> ignore

            services.AddRouting()
                    .AddLogging()
                    //.AddSwaggerGen(fun config -> config.SwaggerDoc("v0.1", openApiInfo))
                    .AddGiraffe()
                    //.AddSingleton(Constants.JsonSerializerOptions)
                    // Next line adds the Json serializer that Giraffe uses internally
                    .AddSingleton<Json.ISerializer>(SystemTextJson.Serializer(Constants.JsonSerializerOptions))
                    .AddHttpLogging(fun loggingOptions ->
                        loggingOptions.CombineLogs <- true
                        loggingOptions.LoggingFields <- HttpLoggingFields.All)
                    .AddW3CLogging(fun options -> 
                        options.FileName <- "Grace.Server.log-"
                        options.LogDirectory <- Path.Combine(Environment.GetEnvironmentVariable("TEMP"), "Grace.Server.Logs"))
                    .AddSingleton<ActorProxyOptions>(actorProxyOptions)
                    .AddSingleton<IActorProxyFactory>(actorProxyFactory)
                    .AddDaprClient(fun daprClientBuilder ->
                        daprClientBuilder.UseJsonSerializationOptions(Constants.JsonSerializerOptions)
                                         //.UseDaprApiToken(Environment.GetEnvironmentVariable("DAPR_API_TOKEN"))
                                         .Build() // This builds the DaprClient.
                                         |> ignore
                    )

            let apiVersioningBuilder = services.AddApiVersioning(fun options ->
                options.ReportApiVersions <- true
                options.DefaultApiVersion <- new ApiVersion(1, 0)
                options.AssumeDefaultVersionWhenUnspecified <- true
                // Use whatever reader you want
                options.ApiVersionReader <- ApiVersionReader.Combine(new UrlSegmentApiVersionReader(),
                                                new HeaderApiVersionReader("x-api-version"),
                                                new MediaTypeApiVersionReader("x-api-version"));
            )
            
            apiVersioningBuilder.AddApiExplorer(fun options ->
                // add the versioned api explorer, which also adds IApiVersionDescriptionProvider service
                // note: the specified format code will format the version as "'v'major[.minor][-status]"
                options.GroupNameFormat <- "'v'VVV"
                options.DefaultApiVersion <- ApiVersion(DateOnly(2023, 10, 1))
                options.AssumeDefaultVersionWhenUnspecified <- true
                options.ApiVersionParameterSource <- HeaderApiVersionReader(Constants.ServerApiVersionHeaderKey)
            
                // note: this option is only necessary when versioning by url segment. the SubstitutionFormat
                // can also be used to control the format of the API version in route templates
                options.SubstituteApiVersionInUrl <- true) |> ignore
            
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

            // List all services to the log.
            //services |> Seq.iter (fun service -> logToConsole $"Service: {service.ServiceType}.")

        member _.Configure(app: IApplicationBuilder, env: IWebHostEnvironment) =
            if env.IsDevelopment() then
                app//.UseSwagger()
                   //.UseSwaggerUI(fun config -> config.SwaggerEndpoint("/swagger", "Grace Server API"))
                   .UseDeveloperExceptionPage() |> ignore

            // This line enables Azure SDK (i.e. CosmosDB) OpenTelemetry output, which is currently in experimental support. 
            //   When this is fully supported, we can remove it.
            AppContext.SetSwitch("Azure.Experimental.EnableActivitySource", true)
            app.UseW3CLogging()
               .UseCloudEvents()
               .UseAuthentication()
               .UseStatusCodePages()
               .UseStaticFiles()
               .UseRouting()
               .UseMiddleware<LogRequestHeadersMiddleware>()
               .UseMiddleware<CorrelationIdMiddleware>()
               .UseMiddleware<HttpSecurityHeadersMiddleware>()
               .UseMiddleware<ValidateIdsMiddleware>()
               .UseEndpoints(fun endpointBuilder ->
                   // Add Dapr actor endpoints
                   endpointBuilder.MapActorsHandlers() |> ignore

                   // Add Dapr pub/sub endpoints
                   endpointBuilder.MapSubscribeHandler() |> ignore

                   // Add Giraffe (Web API) endpoints
                   endpointBuilder.MapGiraffeEndpoints(endpoints)

                   // Add SignalR hub endpoints
                   endpointBuilder.MapHub<Notifications.NotificationHub>("/notifications") |> ignore)

               // If we get here, we didn't find a route.
               .UseGiraffe(notFoundHandler)

            // Set the global ApplicationContext.
            ApplicationContext.Set.Wait()
