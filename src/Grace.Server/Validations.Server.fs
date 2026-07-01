namespace Grace.Server

open FSharpPlus
open Giraffe
open Grace.Actors
open Grace.Actors.Constants
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Extensions.MemoryCache
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Server.ApplicationContext
open Grace.Shared.Constants
open Grace.Types.Common
open Grace.Shared.Utilities
open Grace.Shared.Validation
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Caching.Memory
open Microsoft.Extensions.Logging
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.Threading.Tasks
open Services
open Microsoft.Extensions.Caching.Memory

/// Contains Grace Server validations behavior and supporting helpers.
module Validations =

    let log = ApplicationContext.loggerFactory.CreateLogger("Validations.Server")

    /// Converts an Option to a Result<unit, 'TError> to match the format of validation functions.
    let optionToResult<'TError, 'T> (error: 'TError) (option: Task<'T option>) =
        task {
            match! option with
            | Some value -> return Ok()
            | None -> return Error error
        }

    /// Contains Grace Server owner behavior and supporting helpers.
    module Owner =

        /// Implements owner id exists for the server request pipeline.
        let ownerIdExists<'T> (ownerId: string) correlationId (error: 'T) =
            task {
                let mutable ownerGuid = Guid.Empty

                if
                    (not <| String.IsNullOrEmpty(ownerId))
                    && Guid.TryParse(ownerId, &ownerGuid)
                then
                    match memoryCache.GetOwnerIdEntry ownerGuid with
                    | Some value ->
                        match value with
                        | MemoryCache.Exists -> return Ok()
                        | MemoryCache.DoesNotExist -> return Error error
                        | _ ->
                            return!
                                ownerExists ownerId correlationId
                                |> optionToResult error
                    | None ->
                        return!
                            ownerExists ownerId correlationId
                            |> optionToResult error
                else
                    return Error error
            }
            |> ValidationResult

        /// Implements owner id does not exist for the server request pipeline.
        let ownerIdDoesNotExist<'T> (ownerId: string) correlationId (error: 'T) =
            task {
                let mutable ownerGuid = Guid.Empty
                //logToConsole $"In ownerIdDoesNotExist: ownerId: {ownerId}; correlationId: {correlationId}"
                if
                    (not <| String.IsNullOrEmpty(ownerId))
                    && Guid.TryParse(ownerId, &ownerGuid)
                then
                    let ownerActorProxy = Owner.CreateActorProxy ownerGuid correlationId
                    //logToConsole $"In ownerIdDoesNotExist: ownerActorProxy: {serialize ownerActorProxy}"
                    let! exists = ownerActorProxy.Exists correlationId
                    if exists then return Error error else return Ok()
                else
                    return Error error
            }
            |> ValidationResult

        /// Implements owner exists for the server request pipeline.
        let ownerExists<'T> ownerId ownerName (context: HttpContext) (error: 'T) =
            let result =
                let graceIds = getGraceIds context
                if graceIds.HasOwner then Ok() else Error error

            ValueTask.FromResult(result)

        /// Implements owner name does not exist for the server request pipeline.
        let ownerNameDoesNotExist<'T> (ownerName: string) correlationId (error: 'T) =
            task {
                let! ownerNameExists = ownerNameExists ownerName false correlationId
                if ownerNameExists then return Error error else return Ok()
            }
            |> ValidationResult

        /// Implements owner is deleted for the server request pipeline.
        let ownerIsDeleted<'T> context correlationId (error: 'T) =
            task {
                let graceIds = getGraceIds context
                let ownerGuid = Guid.Parse(graceIds.OwnerIdString)

                match memoryCache.GetDeletedOwnerIdEntry ownerGuid with
                | Some value ->
                    match value with
                    | MemoryCache.DoesNotExist -> return Ok()
                    | MemoryCache.Exists -> return Error error
                    | _ ->
                        return!
                            ownerIsDeleted graceIds.OwnerIdString correlationId
                            |> optionToResult error
                | None ->
                    return!
                        ownerIsDeleted graceIds.OwnerIdString correlationId
                        |> optionToResult error
            }
            |> ValidationResult

        /// Implements owner is not deleted for the server request pipeline.
        let ownerIsNotDeleted<'T> context correlationId (error: 'T) =
            task {
                match! ownerIsDeleted context correlationId error with
                | Ok _ -> return Error error
                | Error _ -> return Ok()
            }
            |> ValidationResult

        /// Implements owner does not exist for the server request pipeline.
        let ownerDoesNotExist<'T> ownerId ownerName correlationId (error: 'T) =
            task {
                if not <| String.IsNullOrEmpty(ownerId)
                   && not <| String.IsNullOrEmpty(ownerName) then
                    match! ownerExists ownerId ownerName correlationId error with
                    | Ok _ -> return Error error
                    | Error _ -> return Ok()
                else
                    return Ok()
            }
            |> ValidationResult

    /// Contains Grace Server organization behavior and supporting helpers.
    module Organization =

        /// Implements organization id exists for the server request pipeline.
        let organizationIdExists<'T> (organizationId: string) correlationId (error: 'T) =
            task {
                let mutable organizationGuid = Guid.Empty

                if
                    (not <| String.IsNullOrEmpty(organizationId))
                    && Guid.TryParse(organizationId, &organizationGuid)
                then
                    match memoryCache.GetOrganizationIdEntry organizationGuid with
                    | Some value ->
                        match value with
                        | MemoryCache.Exists -> return Ok()
                        | MemoryCache.DoesNotExist -> return Error error
                        | _ ->
                            return!
                                organizationExists organizationId correlationId
                                |> optionToResult error
                    | None ->
                        return!
                            organizationExists organizationId correlationId
                            |> optionToResult error
                else
                    return Ok()
            }
            |> ValidationResult

        /// Implements organization id does not exist for the server request pipeline.
        let organizationIdDoesNotExist<'T> (organizationId: string) correlationId (error: 'T) =
            task {
                if not <| String.IsNullOrEmpty(organizationId) then
                    match! organizationIdExists organizationId correlationId error with
                    | Ok _ -> return Error error
                    | Error _ -> return Ok()
                else
                    return Ok()
            }
            |> ValidationResult

        /// Implements organization name is unique within owner for the server request pipeline.
        let organizationNameIsUniqueWithinOwner<'T>
            (ownerId: string)
            (ownerName: string)
            (organizationName: string)
            (context: HttpContext)
            correlationId
            (error: 'T)
            =
            task {
                if not <| String.IsNullOrEmpty(organizationName) then
                    let graceIds = getGraceIds context

                    match! organizationNameIsUnique graceIds.OwnerIdString organizationName correlationId with
                    | Ok isUnique ->
                        //logToConsole
                        //    $"In organizationNameIsUnique: correlationId: {correlationId}; ownerId: {ownerId}; ownerName: {ownerName}; organizationName: {organizationName}; isUnique: {isUnique}"

                        if isUnique then return Ok() else return Error error
                    | Error internalError ->
                        logToConsole internalError
                        return Error error
                else
                    return Ok()
            }
            |> ValidationResult

        /// Implements organization exists for the server request pipeline.
        let organizationExists<'T> ownerId ownerName organizationId organizationName correlationId (error: 'T) =
            task {
                try
                    let mutable organizationGuid = Guid.Empty

                    if
                        not <| String.IsNullOrEmpty(organizationId)
                        && Guid.TryParse(organizationId, &organizationGuid)
                    then
                        match memoryCache.GetOrganizationIdEntry organizationGuid with
                        | Some value ->
                            match value with
                            | MemoryCache.Exists -> return Ok()
                            | MemoryCache.DoesNotExist -> return Error error
                            | _ ->
                                return!
                                    organizationExists organizationId correlationId
                                    |> optionToResult error
                        | None ->
                            return!
                                organizationExists organizationId correlationId
                                |> optionToResult error
                    else
                        return Error error
                with
                | ex ->
                    log.LogError(ex, "{CurrentInstant}: Exception in Grace.Server.Validations.organizationExists.", getCurrentInstantExtended ())

                    return Error error
            }
            |> ValidationResult

        /// Implements organization does not exist for the server request pipeline.
        let organizationDoesNotExist<'T> ownerId ownerName organizationId organizationName correlationId (error: 'T) =
            task {
                match! organizationExists ownerId ownerName organizationId organizationName correlationId error with
                | Ok _ -> return Error error
                | Error error -> return Ok()
            }
            |> ValidationResult

        /// Implements organization is deleted for the server request pipeline.
        let organizationIsDeleted<'T> context correlationId (error: 'T) =
            task {
                let graceIds = getGraceIds context
                let organizationGuid = Guid.Parse(graceIds.OrganizationIdString)

                match memoryCache.GetDeletedOrganizationIdEntry organizationGuid with
                | Some value ->
                    match value with
                    | MemoryCache.DoesNotExist -> return Ok()
                    | MemoryCache.Exists -> return Error error
                    | _ ->
                        return!
                            organizationIsDeleted graceIds.OrganizationIdString correlationId
                            |> optionToResult error
                | None ->
                    return!
                        organizationIsDeleted graceIds.OrganizationIdString correlationId
                        |> optionToResult error
            }
            |> ValidationResult

        /// Implements organization is not deleted for the server request pipeline.
        let organizationIsNotDeleted<'T> context correlationId (error: 'T) =
            task {
                match! organizationIsDeleted context correlationId error with
                | Ok _ -> return Error error
                | Error _ -> return Ok()
            }
            |> ValidationResult

    /// Contains Grace Server repository behavior and supporting helpers.
    module Repository =

        /// Implements repository id exists for the server request pipeline.
        let repositoryIdExists<'T> (organizationId: OrganizationId) (repositoryId: string) correlationId (error: 'T) =
            task {
                let mutable repositoryGuid = Guid.Empty

                if
                    (not <| String.IsNullOrEmpty(repositoryId))
                    && Guid.TryParse(repositoryId, &repositoryGuid)
                then
                    match memoryCache.GetRepositoryIdEntry repositoryGuid with
                    | Some value ->
                        match value with
                        | MemoryCache.Exists -> return Ok()
                        | MemoryCache.DoesNotExist -> return Error error
                        | _ ->
                            return!
                                repositoryExists organizationId repositoryId correlationId
                                |> optionToResult error
                    | None ->
                        return!
                            repositoryExists organizationId repositoryId correlationId
                            |> optionToResult error
                else
                    return Ok()
            }
            |> ValidationResult

        /// Implements repository id does not exist for the server request pipeline.
        let repositoryIdDoesNotExist<'T> (organizationId: OrganizationId) (repositoryId: string) correlationId (error: 'T) =
            task {
                if not <| String.IsNullOrEmpty(repositoryId) then
                    match! repositoryIdExists organizationId repositoryId correlationId error with
                    | Ok _ -> return Error error
                    | Error _ -> return Ok()
                else
                    return Ok()
            }
            |> ValidationResult

        /// Implements repository exists for the server request pipeline.
        let repositoryExists<'T> ownerId ownerName organizationId organizationName repositoryId repositoryName correlationId (error: 'T) =
            task {
                match! resolveRepositoryId ownerId organizationId repositoryId repositoryName correlationId with
                | Some repositoryId ->
                    let exists = memoryCache.Get<string>(repositoryId)

                    match exists with
                    | MemoryCache.Exists -> return Ok()
                    | MemoryCache.DoesNotExist -> return Error error
                    | _ ->
                        let repositoryActorProxy = Repository.CreateActorProxy organizationId repositoryId correlationId

                        let! exists = repositoryActorProxy.Exists correlationId

                        if exists then
                            use newCacheEntry =
                                memoryCache.CreateEntry(
                                    repositoryId,
                                    Value = MemoryCache.Exists,
                                    AbsoluteExpirationRelativeToNow = MemoryCache.DefaultExpirationTime
                                )

                            return Ok()
                        else
                            return Error error
                | None -> return Error error
            }
            |> ValidationResult

        /// Implements repository is deleted for the server request pipeline.
        let repositoryIsDeleted<'T> context correlationId (error: 'T) =
            task {
                let graceIds = getGraceIds context
                let repositoryGuid = Guid.Parse(graceIds.RepositoryIdString)

                match memoryCache.GetDeletedRepositoryIdEntry repositoryGuid with
                | Some value ->
                    match value with
                    | MemoryCache.DoesNotExist -> return Ok()
                    | MemoryCache.Exists -> return Error error
                    | _ ->
                        return!
                            repositoryIsDeleted graceIds.OrganizationId graceIds.RepositoryIdString correlationId
                            |> optionToResult error
                | None ->
                    return!
                        repositoryIsDeleted graceIds.OrganizationId graceIds.RepositoryIdString correlationId
                        |> optionToResult error
            }
            |> ValidationResult

        /// Implements repository is not deleted for the server request pipeline.
        let repositoryIsNotDeleted<'T> context correlationId (error: 'T) =
            task {
                match! repositoryIsDeleted context correlationId error with
                | Ok _ -> return Error error
                | Error _ -> return Ok()
            }
            |> ValidationResult

        /// Implements repository name is unique for the server request pipeline.
        let repositoryNameIsUnique<'T> ownerId organizationId repositoryName correlationId (error: 'T) =
            task {
                if not <| String.IsNullOrEmpty(repositoryName) then
                    match! repositoryNameIsUnique ownerId organizationId repositoryName correlationId with
                    | Ok isUnique -> if isUnique then return Ok() else return Error error
                    | Error internalError ->
                        logToConsole internalError
                        return Error error
                else
                    return Ok()
            }
            |> ValidationResult

    /// Contains Grace Server branch behavior and supporting helpers.
    module Branch =

        /// Implements branch id exists for the server request pipeline.
        let branchIdExists<'T> (branchId: string) repositoryId correlationId (error: 'T) =
            task {
                let mutable branchGuid = Guid.Empty

                if
                    (not <| String.IsNullOrEmpty(branchId))
                    && Guid.TryParse(branchId, &branchGuid)
                then
                    match memoryCache.GetBranchIdEntry branchGuid with
                    | Some value ->
                        match value with
                        | MemoryCache.Exists -> return Ok()
                        | MemoryCache.DoesNotExist -> return Error error
                        | _ ->
                            return!
                                branchExists branchGuid repositoryId correlationId
                                |> optionToResult error
                    | None ->
                        return!
                            branchExists branchGuid repositoryId correlationId
                            |> optionToResult error
                else
                    return Ok()
            }
            |> ValidationResult

        /// Implements branch id does not exist for the server request pipeline.
        let branchIdDoesNotExist<'T> (branchId: string) repositoryId correlationId (error: 'T) =
            task {
                let mutable branchGuid = Guid.Empty

                if
                    (not <| String.IsNullOrEmpty(branchId))
                    && Guid.TryParse(branchId, &branchGuid)
                then
                    let branchActorProxy = Branch.CreateActorProxy branchGuid repositoryId correlationId

                    let! exists = branchActorProxy.Exists correlationId
                    if exists then return Error error else return Ok()
                else
                    return Ok()
            }
            |> ValidationResult

        /// Implements branch exists for the server request pipeline.
        let branchExists<'T> ownerId organizationId repositoryId branchId branchName correlationId (error: 'T) =
            task {
                match! resolveBranchId ownerId organizationId repositoryId branchId branchName correlationId with
                | Some branchId ->
                    let exists = memoryCache.Get<string>(branchId)

                    match exists with
                    | MemoryCache.Exists -> return Ok()
                    | MemoryCache.DoesNotExist -> return Error error
                    | _ ->
                        let branchActorProxy = Branch.CreateActorProxy branchId repositoryId correlationId

                        let! exists = branchActorProxy.Exists correlationId

                        if exists then
                            use newCacheEntry =
                                memoryCache.CreateEntry(
                                    branchId,
                                    Value = MemoryCache.Exists,
                                    AbsoluteExpirationRelativeToNow = MemoryCache.DefaultExpirationTime
                                )

                            return Ok()
                        else
                            return Error error
                | None -> return Error error
            }
            |> ValidationResult

        /// Implements branch allows reference type for the server request pipeline.
        let branchAllowsReferenceType<'T> ownerId organizationId repositoryId branchId branchName (referenceType: ReferenceType) correlationId (error: 'T) =
            task {
                let mutable guid = Guid.Empty

                match! resolveBranchId ownerId organizationId repositoryId branchId branchName correlationId with
                | Some branchId ->
                    let mutable allowed = new obj ()

                    if memoryCache.TryGetValue($"{branchId}{referenceType}Allowed", &allowed) then
                        let allowed = allowed :?> bool
                        if allowed then return Ok() else return Error error
                    else
                        let branchActorProxy = Branch.CreateActorProxy branchId repositoryId correlationId

                        let! branchDto = branchActorProxy.Get correlationId

                        let allowed =
                            match referenceType with
                            | Promotion -> if branchDto.PromotionEnabled then true else false
                            | Commit -> if branchDto.CommitEnabled then true else false
                            | Checkpoint -> if branchDto.CheckpointEnabled then true else false
                            | Save -> if branchDto.SaveEnabled then true else false
                            | Tag -> if branchDto.TagEnabled then true else false
                            | External -> if branchDto.ExternalEnabled then true else false
                            | Rebase -> true // Rebase is always allowed.

                        use newCacheEntry =
                            memoryCache.CreateEntry(
                                $"{branchId}{referenceType}Allowed",
                                Value = allowed,
                                AbsoluteExpirationRelativeToNow = MemoryCache.DefaultExpirationTime
                            )

                        if allowed then return Ok() else return Error error
                | None -> return Error error
            }
            |> ValidationResult


        /// Implements branch allows assign for the server request pipeline.
        let branchAllowsAssign<'T> ownerId organizationId repositoryId branchId branchName correlationId (error: 'T) =
            task {
                let mutable guid = Guid.Empty

                match! resolveBranchId ownerId organizationId repositoryId branchId branchName correlationId with
                | Some branchId ->
                    let mutable allowed = new obj ()

                    if memoryCache.TryGetValue($"{branchId}AssignAllowed", &allowed) then
                        let allowed = allowed :?> bool
                        if allowed then return Ok() else return Error error
                    else
                        let branchActorProxy = Branch.CreateActorProxy branchId repositoryId correlationId

                        let! branchDto = branchActorProxy.Get correlationId
                        let allowed = branchDto.AssignEnabled

                        use newCacheEntry =
                            memoryCache.CreateEntry(
                                $"{branchId}AssignAllowed",
                                Value = allowed,
                                AbsoluteExpirationRelativeToNow = MemoryCache.DefaultExpirationTime
                            )

                        if allowed then return Ok() else return Error error
                | None -> return Error error
            }
            |> ValidationResult

        /// Implements parent branch allows promotions for the server request pipeline.
        let parentBranchAllowsPromotions<'T> ownerId organizationId repositoryId parentBranchId parentBranchName correlationId (error: 'T) =
            task {
                match! resolveBranchId ownerId organizationId repositoryId parentBranchId parentBranchName correlationId with
                | Some resolvedParentBranchId ->
                    let mutable allowed = new obj ()

                    if memoryCache.TryGetValue($"{resolvedParentBranchId}PromotionAllowed", &allowed) then
                        let allowed = allowed :?> bool
                        if allowed then return Ok() else return Error error
                    else
                        let branchActorProxy = Branch.CreateActorProxy resolvedParentBranchId repositoryId correlationId

                        let! branchDto = branchActorProxy.Get correlationId
                        let allowed = branchDto.PromotionEnabled

                        use newCacheEntry =
                            memoryCache.CreateEntry(
                                $"{resolvedParentBranchId}PromotionAllowed",
                                Value = allowed,
                                AbsoluteExpirationRelativeToNow = MemoryCache.DefaultExpirationTime
                            )

                        if allowed then return Ok() else return Error error
                | None -> return Error error
            }
            |> ValidationResult

        /// Implements branch name does not exist for the server request pipeline.
        let branchNameDoesNotExist<'T> ownerId organizationId repositoryId branchName correlationId (error: 'T) =
            task {
                match! resolveBranchId ownerId organizationId repositoryId String.Empty branchName correlationId with
                | Some branchId -> return Error error
                | None -> return Ok()
            }
            |> ValidationResult

        /// Implements reference id exists for the server request pipeline.
        let referenceIdExists<'T> (referenceId: ReferenceId) repositoryId correlationId (error: 'T) =
            task {
                if not <| (referenceId = Guid.Empty) then
                    let referenceActorProxy = Reference.CreateActorProxy referenceId repositoryId correlationId

                    let! exists = referenceActorProxy.Exists correlationId
                    if exists then return Ok() else return Error error
                else
                    return Ok()
            }
            |> ValidationResult

    /// Contains Grace Server directory version behavior and supporting helpers.
    module DirectoryVersion =
        /// Implements directory id exists for the server request pipeline.
        let directoryIdExists<'T> (directoryId: DirectoryVersionId) repositoryId correlationId (error: 'T) =
            task {
                let exists = memoryCache.Get<string>(directoryId)

                match exists with
                | MemoryCache.Exists -> return Ok()
                | MemoryCache.DoesNotExist -> return Error error
                | _ ->
                    let directoryVersionActorProxy = DirectoryVersion.CreateActorProxy directoryId repositoryId correlationId

                    let! exists = directoryVersionActorProxy.Exists correlationId

                    if exists then
                        use newCacheEntry =
                            memoryCache.CreateEntry(
                                directoryId,
                                Value = MemoryCache.Exists,
                                AbsoluteExpirationRelativeToNow = MemoryCache.DefaultExpirationTime
                            )

                        return Ok()
                    else
                        return Error error
            }
            |> ValidationResult

        /// Implements directory ids exist for the server request pipeline.
        let directoryIdsExist<'T> (directoryIds: List<DirectoryVersionId>) repositoryId correlationId (error: 'T) =
            task {
                let mutable allExist = true
                let directoryIdStack = Queue<DirectoryVersionId>(directoryIds)

                while directoryIdStack.Count > 0 && allExist do
                    let directoryId = directoryIdStack.Dequeue()
                    let directoryVersionActorProxy = DirectoryVersion.CreateActorProxy directoryId repositoryId correlationId

                    let! exists = directoryVersionActorProxy.Exists correlationId
                    allExist <- exists

                if allExist then return Ok() else return Error error
            }
            |> ValidationResult

        /// Computes sha256 hash exists data used by Grace Server.
        let sha256HashExists<'T> repositoryId sha256Hash correlationId (error: 'T) =
            task {
                let repositoryActorProxy = Repository.CreateActorProxy repositoryId correlationId

                match! getDirectoryVersionBySha256Hash repositoryId sha256Hash correlationId with
                | Some directoryVersion -> return Ok()
                | None -> return Error error
            }
            |> ValidationResult
