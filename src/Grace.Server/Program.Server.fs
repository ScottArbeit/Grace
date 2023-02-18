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
                          .UseUrls("http://*:5000", "https://*:5001")
                          .UseKestrel(fun kestrelServerOptions ->
                              kestrelServerOptions.ConfigureEndpointDefaults(fun listenOptions -> listenOptions.Protocols <- HttpProtocols.Http1AndHttp2)
                              kestrelServerOptions.ConfigureHttpsDefaults(fun options -> options.SslProtocols <- SslProtocols.Tls12 ||| SslProtocols.Tls13)
                          ) |> ignore
            )

    [<EntryPoint>]
    let main args =
        let host = createHostBuilder(args).Build()
        host.Run()

        0 // Exit code
