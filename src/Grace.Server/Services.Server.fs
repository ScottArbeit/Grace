namespace Grace.Server

open Giraffe
open Grace.Actors
open Grace.Actors.Constants
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Server.ApplicationContext
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Parameters.Common
open Grace.Shared.Resources.Text
open Grace.Types.Types
open Grace.Shared.Utilities
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Caching.Memory
open Microsoft.Extensions.Logging
open NodaTime
open Orleans
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.Linq
open System.Net
open System.Threading.Tasks
open System.Text
open System.Text.Json

module Services =

    let log = ApplicationContext.loggerFactory.CreateLogger("Server.Services")

    /// Defines the type of all server queries in Grace.
    ///
    /// Takes an HttpContext, the MaxCount of results to return, and the GrainProxy to use for the query, and returns a Task containing the return value.
    type QueryResult<'T, 'U when 'T :> IGrain> = HttpContext -> int -> 'T -> Task<'U>

    /// Gets the CorrelationId from HttpContext.Items.
    let getCorrelationId (context: HttpContext) = (context.Items[Constants.CorrelationId] :?> CorrelationId)

    /// Gets the GraceIds record from HttpContext.Items.
    let getGraceIds (context: HttpContext) =
        if context.Items.ContainsKey(nameof (GraceIds)) then
            context.Items[nameof (GraceIds)] :?> GraceIds
        else
            GraceIds.Default

    /// Creates common metadata for Grace events.
    let createMetadata (context: HttpContext) : EventMetadata =
        { Timestamp = getCurrentInstant ()
          CorrelationId = context.Items[Constants.CorrelationId].ToString()
          Principal = context.User.Identity.Name
          Properties = new Dictionary<string, string>() }

    /// Parses the incoming request body into the specified type.
    let parse<'T when 'T :> CommonParameters> (context: HttpContext) =
        task {
            let! parameters = context.BindJsonAsync<'T>()

            if String.IsNullOrEmpty(parameters.CorrelationId) then
                parameters.CorrelationId <- getCorrelationId context

            return parameters
        }

    /// Parses the incoming request body into the provided type.
    let deserializeToType (requestBodyType: Type) (context: HttpContext) =
        task {
            try
                let! parameters = JsonSerializer.DeserializeAsync(context.Request.Body, requestBodyType, Constants.JsonSerializerOptions)

                if not <| isNull parameters then return Some parameters else return None
            with ex ->
                return None
        }

    /// Adds common attributes to the current OpenTelemetry activity, and returns the result.
    let returnResult<'T> (statusCode: int) (result: 'T) (context: HttpContext) =
        task {
            try
                Activity.Current
                    .AddTag("correlation_id", getCorrelationId context)
                    .AddTag("http.status_code", statusCode)
                |> ignore

                context.SetStatusCode(statusCode)

                //log.LogDebug("{CurrentInstant}: In returnResult: StatusCode: {statusCode}; result: {result}", getCurrentInstantExtended(), statusCode, serialize result)
                return! context.WriteJsonAsync(result) // .WriteJsonAsync() uses Grace's JsonSerializerOptions.
            with ex ->
                let exceptionResponse = Utilities.ExceptionResponse.Create ex

                return! context.WriteJsonAsync(GraceError.Create (serialize exceptionResponse) (getCorrelationId context))
        }

    /// Adds common attributes to the current OpenTelemetry activity, and returns a 404 Not found status.
    let result404NotFound (context: HttpContext) =
        task {
            Activity.Current
                .AddTag("correlation_id", getCorrelationId context)
                .AddTag("http.status_code", StatusCodes.Status404NotFound)
            |> ignore

            context.SetStatusCode(StatusCodes.Status404NotFound)
            return Some context
        }

    /// Adds common attributes to the current OpenTelemetry activity, and returns the result with a 200 Ok status.
    let result200Ok<'T> = returnResult<'T> StatusCodes.Status200OK

    /// Adds common attributes to the current OpenTelemetry activity, and returns the result with a 400 Bad request status.
    let result400BadRequest<'T> = returnResult<'T> StatusCodes.Status400BadRequest

    // /// Adds common attributes to the current OpenTelemetry activity, and returns the result with a 404 Not found status.
    // let result404NotFound<'T> = returnResult<'T> StatusCodes.Status404NotFound

    /// Adds common attributes to the current OpenTelemetry activity, and returns the result with a 500 Internal server error status.
    let result500ServerError<'T> = returnResult<'T> StatusCodes.Status500InternalServerError
