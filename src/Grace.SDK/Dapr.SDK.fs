namespace Grace.SDK

open Grace.Shared.Client.Configuration
open Grace.Shared
open System
open System.Net.Http
open System.Text.Json
open System.Net.Http.Json

module Dapr =

    let serverUri = $"{Current().ServerUri}"
