namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Extensions.MemoryCache
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared.Types
open Grace.Shared.Utilities
open Microsoft.Extensions.Logging
open NodaTime
open Orleans
open Orleans.Runtime
open System
open System.Threading.Tasks

module OrganizationName =

    type OrganizationNameActor(log: ILogger<OrganizationNameActor>) =
        inherit Grain()

        static let actorName = ActorName.OrganizationName

        let mutable cachedOrganizationId: OrganizationId option = None

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            //let idSections = this.GetGrainId().Key.ToString().Split('|')
            //let organizationName = idSections[0]
            //let ownerId = idSections[1]

            logActorActivation log this.IdentityString "In-memory only"

            Task.CompletedTask

        interface IOrganizationNameActor with
            member this.GetOrganizationId correlationId =
                this.correlationId <- correlationId
                cachedOrganizationId |> returnTask

            member this.SetOrganizationId (organizationId: OrganizationId) correlationId =
                this.correlationId <- correlationId

                if organizationId <> Guid.Empty then
                    cachedOrganizationId <- Some organizationId

                Task.CompletedTask
