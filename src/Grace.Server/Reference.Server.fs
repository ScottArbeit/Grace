namespace Grace.Server

open Dapr.Actors.Client
open Giraffe
open Grace.Server.Services
open Grace.Server.Validations
open Grace.Shared
open Grace.Shared.Parameters.Reference
open Grace.Shared.Validation.Common
open Grace.Shared.Validation.Utilities
open Grace.Shared.Validation.Errors
open Grace.Shared.Types
open Grace.Shared.Utilities
open Microsoft.AspNetCore.Http
open System
open System.Diagnostics
open System.Threading.Tasks

module Reference =
    let x = 1
    //type Validations<'T when 'T :> ReferenceParameters> = 'T -> HttpContext -> Task<Result<unit, ReferenceError>>[]
    
    //let activitySource = new ActivitySource("Reference")
    
    //let getActorProxy (context: HttpContext) (ownerId: string) =
    //    let actorProxyFactory = context.GetService<IActorProxyFactory>()
    //    let actorId = GetActorId (ReferenceId (Guid.Parse(ownerId)))
    //    actorProxyFactory.CreateActorProxy<IReferenceActor>(actorId, ActorName.Reference)
