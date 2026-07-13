namespace Grace.Server.Security

open Grace.Actors.ArtifactGrantSigningKeyActor
open Grace.Actors.Interfaces
open Grace.Types.ArtifactGrant
open Grace.Types.Common
open Grace.Types.MaterializationPlan
open NodaTime
open Orleans
open System

/// Connects authenticated server issuance and validation-key publication to the deployment-wide signing-key actor.
module ArtifactGrantKeys =

    /// Represents server-validated inputs for issuing one user- and holder-bound artifact grant.
    type ArtifactGrantIssueRequest =
        {
            AuthenticatedUserId: string
            HolderPublicKey: ArtifactGrantHolderPublicKey
            CacheId: string
            TargetRootDirectoryVersionId: DirectoryVersionId
            ExecutionMode: MaterializationExecutionMode
            ArtifactIdentities: string seq
            RequestedTtl: Duration option
        }

    /// Routes every server instance to the same durable Orleans signing-key owner.
    type ArtifactGrantKeyRing(grainFactory: IGrainFactory) =

        /// Resolves the one deployment-wide logical actor used by issuance and publication.
        member private _.Actor = grainFactory.GetGrain<IArtifactGrantSigningKeyActor>(DeploymentActorKey)

        /// Issues one grant for the authenticated user id supplied by server authentication wiring.
        member this.IssueGrant(now: Instant, request: ArtifactGrantIssueRequest) =
            let artifactIdentities =
                if
                    isNull (box request)
                    || isNull (box request.ArtifactIdentities)
                then
                    Array.empty
                else
                    request.ArtifactIdentities |> Seq.toArray

            let actorRequest =
                if isNull (box request) then
                    Unchecked.defaultof<ArtifactGrantSigningRequest>
                else
                    {
                        RequesterPrincipalType = ArtifactGrantRequesterPrincipalType.User
                        RequesterPrincipalId = request.AuthenticatedUserId
                        HolderPublicKey = request.HolderPublicKey
                        CacheId = request.CacheId
                        TargetRootDirectoryVersionId = request.TargetRootDirectoryVersionId
                        ExecutionMode = request.ExecutionMode
                        ArtifactIdentities = artifactIdentities
                        RequestedTtl = request.RequestedTtl
                    }

            this.Actor.IssueGrant(actorRequest, now)

        /// Publishes the same durable active and overlap key set used by every server instance.
        member this.PublishValidationKeys(now: Instant) = this.Actor.PublishValidationKeys now
