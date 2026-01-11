namespace Grace.Server

open Giraffe
open Grace.Actors.Constants
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Server.ApplicationContext
open Grace.Server.Services
open Grace.Shared
open Grace.Shared.Extensions
open Grace.Shared.Parameters.Review
open Grace.Shared.Validation.Common
open Grace.Shared.Validation.Errors
open Grace.Shared.Validation.Utilities
open Grace.Types.Policy
open Grace.Types.Review
open Grace.Types.Types
open Grace.Shared.Utilities
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Collections.Generic
open System.Diagnostics
open System.Threading.Tasks

module Review =
    type Validations<'T when 'T :> ReviewParameters> = 'T -> ValueTask<Result<unit, ReviewError>> array

    let log = ApplicationContext.loggerFactory.CreateLogger("Review.Server")

    let activitySource = new ActivitySource("Review")

    let processCommand<'T when 'T :> ReviewParameters> (context: HttpContext) (validations: Validations<'T>) (command: 'T -> ValueTask<ReviewCommand>) =
        task {
            let commandName = context.Items["Command"] :?> string
            let graceIds = getGraceIds context
            let correlationId = getCorrelationId context
            let parameterDictionary = Dictionary<string, obj>()

            try
                use activity = activitySource.StartActivity("processCommand", ActivityKind.Server)
                let! parameters = context |> parse<'T>
                parameterDictionary.AddRange(getParametersAsDictionary parameters)

                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                let handleCommand candidateId cmd =
                    task {
                        let actorProxy = Review.CreateActorProxy candidateId graceIds.RepositoryId correlationId
                        let metadata = createMetadata context

                        match! actorProxy.Handle cmd metadata with
                        | Ok graceReturnValue ->
                            graceReturnValue
                                .enhance(parameterDictionary)
                                .enhance(nameof OwnerId, graceIds.OwnerId)
                                .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                .enhance(nameof CandidateId, candidateId)
                                .enhance("Command", commandName)
                                .enhance ("Path", context.Request.Path.Value)
                            |> ignore

                            return! context |> result200Ok graceReturnValue
                        | Error graceError ->
                            graceError
                                .enhance(parameterDictionary)
                                .enhance(nameof OwnerId, graceIds.OwnerId)
                                .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                .enhance(nameof CandidateId, candidateId)
                                .enhance("Command", commandName)
                                .enhance ("Path", context.Request.Path.Value)
                            |> ignore

                            return! context |> result400BadRequest graceError
                    }

                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    let! cmd = command parameters
                    let candidateId = Guid.Parse(parameters.CandidateId)
                    return! handleCommand candidateId cmd
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = ReviewError.getErrorMessage error

                    let graceError =
                        (GraceError.Create errorMessage correlationId)
                            .enhance(parameterDictionary)
                            .enhance(nameof OwnerId, graceIds.OwnerId)
                            .enhance(nameof OrganizationId, graceIds.OrganizationId)
                            .enhance(nameof RepositoryId, graceIds.RepositoryId)
                            .enhance("Command", commandName)
                            .enhance ("Path", context.Request.Path.Value)

                    return! context |> result400BadRequest graceError
            with
            | ex ->
                log.LogError(
                    ex,
                    "{CurrentInstant}: Exception in Review.Server.processCommand. CorrelationId: {correlationId}.",
                    getCurrentInstantExtended (),
                    correlationId
                )

                let graceError =
                    (GraceError.CreateWithException ex String.Empty correlationId)
                        .enhance(parameterDictionary)
                        .enhance(nameof OwnerId, graceIds.OwnerId)
                        .enhance(nameof OrganizationId, graceIds.OrganizationId)
                        .enhance(nameof RepositoryId, graceIds.RepositoryId)
                        .enhance ("Path", context.Request.Path.Value)

                return! context |> result500ServerError graceError
        }

    let processQuery<'T, 'U when 'T :> ReviewParameters>
        (context: HttpContext)
        (parameters: 'T)
        (validations: Validations<'T>)
        (query: QueryResult<IReviewActor, 'U>)
        =
        task {
            use activity = activitySource.StartActivity("processQuery", ActivityKind.Server)
            let graceIds = getGraceIds context
            let correlationId = getCorrelationId context
            let parameterDictionary = getParametersAsDictionary parameters

            try
                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    let candidateId = Guid.Parse(parameters.CandidateId)
                    let actorProxy = Review.CreateActorProxy candidateId graceIds.RepositoryId correlationId
                    let! queryResult = query context 0 actorProxy

                    let graceReturnValue =
                        (GraceReturnValue.Create queryResult correlationId)
                            .enhance(parameterDictionary)
                            .enhance(nameof OwnerId, graceIds.OwnerId)
                            .enhance(nameof OrganizationId, graceIds.OrganizationId)
                            .enhance(nameof RepositoryId, graceIds.RepositoryId)
                            .enhance(nameof CandidateId, candidateId)
                            .enhance ("Path", context.Request.Path.Value)

                    return! context |> result200Ok graceReturnValue
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = ReviewError.getErrorMessage error

                    let graceError =
                        (GraceError.Create errorMessage correlationId)
                            .enhance(parameterDictionary)
                            .enhance(nameof OwnerId, graceIds.OwnerId)
                            .enhance(nameof OrganizationId, graceIds.OrganizationId)
                            .enhance(nameof RepositoryId, graceIds.RepositoryId)
                            .enhance ("Path", context.Request.Path.Value)

                    return! context |> result400BadRequest graceError
            with
            | ex ->
                let graceError =
                    (GraceError.CreateWithException ex String.Empty correlationId)
                        .enhance(parameterDictionary)
                        .enhance(nameof OwnerId, graceIds.OwnerId)
                        .enhance(nameof OrganizationId, graceIds.OrganizationId)
                        .enhance(nameof RepositoryId, graceIds.RepositoryId)
                        .enhance ("Path", context.Request.Path.Value)

                return! context |> result500ServerError graceError
        }

    /// Gets a review packet.
    let GetPacket: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context

                let validations (parameters: GetReviewPacketParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.CandidateId ReviewError.InvalidCandidateId
                    |]

                let query (context: HttpContext) _ (actorProxy: IReviewActor) = actorProxy.GetPacket(getCorrelationId context)

                let! parameters = context |> parse<GetReviewPacketParameters>
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString
                context.Items[ "Command" ] <- "GetPacket"
                return! processQuery context parameters validations query
            }

    /// Records a review checkpoint.
    let Checkpoint: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context

                let validations (parameters: ReviewCheckpointParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.CandidateId ReviewError.InvalidCandidateId
                        Guid.isValidAndNotEmptyGuid parameters.ReviewedUpToReferenceId ReviewError.InvalidReferenceId
                        String.isNotEmpty parameters.PolicySnapshotId ReviewError.InvalidPolicySnapshotId
                    |]

                let command (parameters: ReviewCheckpointParameters) =
                    let candidateId = Guid.Parse(parameters.CandidateId)

                    let promotionGroupId =
                        if String.IsNullOrEmpty(parameters.PromotionGroupId) then
                            None
                        else
                            Some(Guid.Parse(parameters.PromotionGroupId))

                    let principal =
                        if
                            isNull context.User
                            || isNull context.User.Identity
                            || String.IsNullOrEmpty(context.User.Identity.Name)
                        then
                            Constants.GraceSystemUser
                        else
                            context.User.Identity.Name

                    let checkpoint =
                        {
                            ReviewCheckpointId = Guid.NewGuid()
                            CandidateId = Some candidateId
                            PromotionGroupId = promotionGroupId
                            ReviewedUpToReferenceId = Guid.Parse(parameters.ReviewedUpToReferenceId)
                            PolicySnapshotId = PolicySnapshotId parameters.PolicySnapshotId
                            Reviewer = UserId principal
                            Timestamp = getCurrentInstant ()
                        }

                    ReviewCommand.AddCheckpoint checkpoint
                    |> returnValueTask

                context.Items[ "Command" ] <- nameof Checkpoint
                return! processCommand context validations command
            }

    /// Resolves a finding.
    let ResolveFinding: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context

                let validations (parameters: ResolveFindingParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.CandidateId ReviewError.InvalidCandidateId
                        Guid.isValidAndNotEmptyGuid parameters.FindingId ReviewError.InvalidFindingId
                        DiscriminatedUnion.isMemberOf<FindingResolutionState, ReviewError> parameters.ResolutionState ReviewError.InvalidResolutionState
                    |]

                let command (parameters: ResolveFindingParameters) =
                    let principal =
                        if
                            isNull context.User
                            || isNull context.User.Identity
                            || String.IsNullOrEmpty(context.User.Identity.Name)
                        then
                            Constants.GraceSystemUser
                        else
                            context.User.Identity.Name

                    let resolutionState =
                        discriminatedUnionFromString<FindingResolutionState> parameters.ResolutionState
                        |> Option.get

                    let note = if String.IsNullOrEmpty(parameters.Note) then None else Some parameters.Note

                    ReviewCommand.ResolveFinding(Guid.Parse(parameters.FindingId), resolutionState, UserId principal, note)
                    |> returnValueTask

                context.Items[ "Command" ] <- nameof ResolveFinding
                return! processCommand context validations command
            }

    /// Requests deeper analysis (placeholder).
    let Deepen: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let correlationId = getCorrelationId context
                let graceError = GraceError.Create "Deepen is not implemented yet." correlationId
                return! context |> result400BadRequest graceError
            }
