namespace Grace.Actors

open Dapr.Actors
open Dapr.Actors.Runtime
open System
open System.Threading.Tasks

module OrganizationName =

    let GetActorId (organizationName: string) = ActorId(organizationName)

    type IOrganizationNameActor =
        inherit IActor
        /// <summary>
        /// Returns true if an organization with this organization name already exists in the database.
        /// </summary>
        abstract member SetOrganizationId: organizationName: string -> Task

        /// <summary>
        /// Returns the OrganizationId for the given OrganizationName.
        /// </summary>
        abstract member GetOrganizationId: unit -> Task<String option>

    type OrganizationNameActor(host: ActorHost) =
        inherit Actor(host)

        let mutable cachedOrganizationId: string option = None

        interface IOrganizationNameActor with
            member this.GetOrganizationId() = Task.FromResult(cachedOrganizationId)

            member this.SetOrganizationId(organizationId: string) =
                let mutable guid = Guid.Empty
                if Guid.TryParse(organizationId, &guid) && guid <> Guid.Empty then
                    cachedOrganizationId <- Some organizationId
                Task.CompletedTask
