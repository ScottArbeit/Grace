namespace Grace.SDK

open Dapr.Client
open Grace.Shared.Client.Configuration
open Grace.Shared
open System
open System.Net.Http
open System.Text.Json
open System.Net.Http.Json

module Dapr =

    let private daprServerUri =
        Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DaprServerUri)

    let serverUri =
        if String.IsNullOrEmpty(daprServerUri) then
            $"{Current().ServerUri}"
        else
            $"{daprServerUri}"

    let private daprHttpPort =
        Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DaprHttpPort)

    let daprHttpEndpoint =
        if String.IsNullOrEmpty(daprHttpPort) then
            $"{serverUri}:3500"
        else
            $"{serverUri}:{daprHttpPort}"

    let private daprGrpcPort =
        Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DaprGrpcPort)

    let daprGrpcEndpoint =
        if String.IsNullOrEmpty(daprHttpPort) then
            $"{serverUri}:5005"
        else
            $"{serverUri}:{daprGrpcPort}"

    let daprClient =
        DaprClientBuilder()
            .UseGrpcEndpoint(daprGrpcEndpoint)
            .UseHttpEndpoint(daprHttpEndpoint)
            .UseJsonSerializationOptions(Constants.JsonSerializerOptions)
            .Build()
