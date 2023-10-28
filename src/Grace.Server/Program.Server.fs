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

module Program =
    let createHostBuilder (args: string[]) : IHostBuilder =
        Host.CreateDefaultBuilder(args)
            .ConfigureLogging(fun logConfig ->
                logConfig.AddOpenTelemetry(fun openTelemetryOptions ->
                    openTelemetryOptions.IncludeScopes <- true
                ) |> ignore
            )
            
            .ConfigureWebHostDefaults(fun webBuilder ->
                webBuilder.UseStartup<Application.Startup>()
                          //.UseUrls("http://*:5000", "https://*:5001")
                          //.UseUrls("http://*:5000")
                          .UseKestrel(fun kestrelServerOptions ->
                              kestrelServerOptions.Listen(IPAddress.Any, 5000)
                              kestrelServerOptions.Listen(IPAddress.Loopback, 5001, (fun listenOptions -> listenOptions.UseHttps("/etc/certificates/gracedevcert.pfx", "GraceDevCert") |> ignore))
                              kestrelServerOptions.ConfigureEndpointDefaults(fun listenOptions -> listenOptions.Protocols <- HttpProtocols.Http1AndHttp2)
                              kestrelServerOptions.ConfigureHttpsDefaults(fun options -> 
                                options.SslProtocols <- SslProtocols.Tls12 ||| SslProtocols.Tls13
#if DEBUG
                                options.AllowAnyClientCertificate()
#endif
                              )
                          ) |> ignore
            )

    [<EntryPoint>]
    let main args =
        let dir = new DirectoryInfo("/etc/certificates")
        let files = dir.EnumerateFiles() 
        logToConsole $"In main. Contents of {dir.FullName} ({files.Count()} files):"
        files |> Seq.iter (fun file -> logToConsole $"{file.Name}: {file.Length} bytes")
        logToConsole "-----------------------------------------------------------"
        let host = createHostBuilder(args).Build()
        let loggerFactory = host.Services.GetService(typeof<ILoggerFactory>) :?> ILoggerFactory
        ApplicationContext.setLoggerFactory(loggerFactory)
        host.Run()

        0 // Exit code
