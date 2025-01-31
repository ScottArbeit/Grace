namespace Grace.Server

open Microsoft.AspNetCore
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Server.Kestrel.Core
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open System
open System.Collections.Generic
open System.IO
open System.Linq
open System.Security.Authentication
open System.Threading.Tasks
open System.Net
open Grace.Shared.Utilities
open Microsoft.Extensions.Logging
open ApplicationContext
open Microsoft.Extensions.Caching.Memory
open System.Collections.Immutable

module Program =
    let graceAppPort = Environment.GetEnvironmentVariable("DAPR_APP_PORT") |> int

    let createHostBuilder (args: string[]) : IHostBuilder =
        Host
            .CreateDefaultBuilder(args)
            .ConfigureLogging(fun logConfig ->
                logConfig.AddOpenTelemetry(fun openTelemetryOptions -> openTelemetryOptions.IncludeScopes <- true)
                |> ignore)

            .ConfigureWebHostDefaults(fun webBuilder ->
                webBuilder
                    .UseStartup<Application.Startup>()
                    .UseKestrel(fun kestrelServerOptions ->
                        kestrelServerOptions.Listen(IPAddress.Any, graceAppPort)
                        //kestrelServerOptions.Limits.MaxConcurrentConnections <- 1000
                        //kestrelServerOptions.Limits.MaxConcurrentUpgradedConnections <- 1000
                        //kestrelServerOptions.Listen(IPAddress.Any, 5001, (fun listenOptions -> listenOptions.UseHttps("/etc/certificates/gracedevcert.pfx", "GraceDevCert") |> ignore))
                        kestrelServerOptions.ConfigureEndpointDefaults(fun listenOptions -> listenOptions.Protocols <- HttpProtocols.Http1AndHttp2)

                        kestrelServerOptions.ConfigureHttpsDefaults(fun options ->
                            options.SslProtocols <- SslProtocols.Tls12 ||| SslProtocols.Tls13
#if DEBUG
                            options.AllowAnyClientCertificate()
#endif
                        ))
                |> ignore)

    [<EntryPoint>]
    let main args =
        //let dir = new DirectoryInfo("/etc/certificates")
        //let files = dir.EnumerateFiles()
        //logToConsole $"In main. Contents of {dir.FullName} ({files.Count()} files):"
        //files |> Seq.iter (fun file -> logToConsole $"{file.Name}: {file.Length} bytes")

        logToConsole "-----------------------------------------------------------"
        let host = createHostBuilder(args).Build()

        // Build the configuration
        let environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")

        let config =
            ConfigurationBuilder()
                .AddJsonFile("appsettings.json", true, true) // Load appsettings.json
                .AddJsonFile($"appsettings.{environment}.json", false, true) // Load environment-specific settings
                .AddEnvironmentVariables() // Include environment variables
                .Build()


        // for kvp in config.AsEnumerable().ToImmutableSortedDictionary() do
        //     Console.WriteLine($"{kvp.Key}: {kvp.Value}");

        // Just placing some much-used services into ApplicationContext where they're easy to find.
        Grace.Actors.Context.setHostServiceProvider host.Services

        let loggerFactory = host.Services.GetService(typeof<ILoggerFactory>) :?> ILoggerFactory
        ApplicationContext.setLoggerFactory (loggerFactory)

        host.Run()

        0 // Exit code
