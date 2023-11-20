namespace Grace.Server

open Dapr.Actors
open Dapr.Actors.Client
open Giraffe
open Grace.Actors
open Grace.Actors.Commands
open Grace.Actors.Constants
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared.Constants
open Grace.Shared.Types
open Grace.Shared.Utilities
open Microsoft.AspNetCore.Http
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading.Tasks
open ApplicationContext

module Validations =

    let actorProxyFactory = ApplicationContext.actorProxyFactory
    let memoryCache = ApplicationContext.memoryCache

    module Owner =

        /// Validates that the given ownerId exists in the database.
        let ownerIdExists<'T> (ownerId: string) (error: 'T) =
            task {
                let mutable ownerGuid = Guid.Empty
                if (not <| String.IsNullOrEmpty(ownerId)) && Guid.TryParse(ownerId, &ownerGuid) then
                    let mutable x = null
                    let cached = memoryCache.TryGetValue(ownerGuid, &x)
                    if cached then
                        return Ok ()
                    else
                        let actorId = Owner.GetActorId(ownerGuid)
                        let ownerActorProxy = actorProxyFactory.CreateActorProxy<IOwnerActor>(actorId, ActorName.Owner)
                        let! exists = ownerActorProxy.Exists()
                        if exists then
                            use newCacheEntry = memoryCache.CreateEntry(ownerGuid, Value = null, SlidingExpiration = DefaultExpirationTime)
                            return Ok ()
                        else
                            return Error error
                else
                    return Error error
            }

        /// Validates that the given ownerId does not already exist in the database.
        let ownerIdDoesNotExist<'T> (ownerId: string) (error: 'T) =
            task {
                let mutable ownerGuid = Guid.Empty
                if (not <| String.IsNullOrEmpty(ownerId)) && Guid.TryParse(ownerId, &ownerGuid) then
                    let actorId = Owner.GetActorId(ownerGuid)
                    let ownerActorProxy = actorProxyFactory.CreateActorProxy<IOwnerActor>(actorId, ActorName.Owner)
                    let! exists = ownerActorProxy.Exists()
                    if exists then
                        return Error error
                    else
                        return Ok ()
                else
                    return Error error
            }

        /// Validates that the owner exists in the database.
        let ownerExists<'T> ownerId ownerName (error: 'T) =
            task {
                let mutable ownerGuid = Guid.Empty
                match! resolveOwnerId ownerId ownerName with
                | Some ownerId ->
                    if Guid.TryParse(ownerId, &ownerGuid) then
                        let mutable x = null
                        let cached = memoryCache.TryGetValue(ownerGuid, &x)
                        if cached then
                            return Ok ()
                        else
                            let actorId = Owner.GetActorId(ownerGuid)
                            let ownerActorProxy = actorProxyFactory.CreateActorProxy<IOwnerActor>(actorId, ActorName.Owner)
                            let! exists = ownerActorProxy.Exists()
                            if exists then
                                use newCacheEntry = memoryCache.CreateEntry(ownerGuid, Value = null, SlidingExpiration = DefaultExpirationTime)
                                return Ok ()
                            else
                                return Error error
                    else
                        return Ok ()
                | None -> return Error error
            }

        /// Validates that the given ownerName does not already exist in the database.
        let ownerNameDoesNotExist<'T> (ownerName: string) (error: 'T) =
            task {
                match! resolveOwnerId String.Empty ownerName with
                | Some ownerId -> return Error error
                | None -> return Ok ()
            }

        /// Validates that the owner is deleted.
        let ownerIsDeleted<'T> ownerId ownerName (error: 'T) =
            task {
                let mutable ownerGuid = Guid.Empty
                match! resolveOwnerId ownerId ownerName with
                | Some ownerId ->
                    if Guid.TryParse(ownerId, &ownerGuid) then
                        let actorId = Owner.GetActorId(ownerGuid)
                        let ownerActorProxy = actorProxyFactory.CreateActorProxy<IOwnerActor>(actorId, ActorName.Owner)
                        let! isDeleted = ownerActorProxy.IsDeleted()
                        if isDeleted then
                            return Ok ()
                        else
                            return Error error
                    else
                        return Error error
                | None -> return Error error
            }

        /// Validates that the owner is not deleted.
        let ownerIsNotDeleted<'T> ownerId ownerName (error: 'T) =
            task {
                let mutable ownerGuid = Guid.Empty
                match! resolveOwnerId ownerId ownerName with
                | Some ownerId ->
                    if Guid.TryParse(ownerId, &ownerGuid) then
                        let actorId = Owner.GetActorId(ownerGuid)
                        let ownerActorProxy = actorProxyFactory.CreateActorProxy<IOwnerActor>(actorId, ActorName.Owner)
                        let! isDeleted = ownerActorProxy.IsDeleted()
                        if isDeleted then
                            return Error error
                        else
                            return Ok ()
                    else
                        return Error error
                | None -> return Error error
            }

        /// Validates that the given ownerId does not already exist in the database.
        let ownerDoesNotExist<'T> ownerId ownerName (error: 'T) =
            task {
                if not <| String.IsNullOrEmpty(ownerId) && not <| String.IsNullOrEmpty(ownerName) then
                    match! ownerExists ownerId ownerName error with 
                    | Ok _ -> return Error error
                    | Error _ -> return Ok ()
                else
                    return Ok ()
            }

    module Organization =

        /// Validates that the given organizationId exists in the database.
        let organizationIdExists<'T> (organizationId: string) (error: 'T) =
            task {
                let mutable organizationGuid = Guid.Empty
                if (not <| String.IsNullOrEmpty(organizationId)) && Guid.TryParse(organizationId, &organizationGuid) then
                    let mutable x = null
                    let cached = memoryCache.TryGetValue(organizationGuid, &x)
                    if cached then
                        return Ok ()
                    else
                        let actorId = Organization.GetActorId(organizationGuid)
                        let organizationActorProxy = actorProxyFactory.CreateActorProxy<IOrganizationActor>(actorId, ActorName.Organization)
                        let! exists = organizationActorProxy.Exists()
                        if exists then
                            use newCacheEntry = memoryCache.CreateEntry(organizationGuid, Value = null, SlidingExpiration = DefaultExpirationTime)
                            return Ok ()
                        else
                            return Error error
                else
                    return Ok ()
            }

        /// Validates that the given organizationId does not already exist in the database.
        let organizationIdDoesNotExist<'T> (organizationId: string) (error: 'T) =
            task {
                if not <| String.IsNullOrEmpty(organizationId) then
                    match! organizationIdExists organizationId error with 
                    | Ok _ -> return Error error
                    | Error _ -> return Ok ()
                else
                    return Ok ()
            }

        /// Validates that the given organizationName does not already exist for this owner.
        let organizationNameIsUnique<'T> (ownerId: string) (ownerName: string) (organizationName: string) (error: 'T) =
            task {
                if not <| String.IsNullOrEmpty(organizationName) then
                    match! organizationNameIsUnique ownerId ownerName organizationName with 
                    | Ok isUnique -> 
                        if isUnique then 
                            return Ok () 
                        else
                            return Error error
                    | Error internalError ->
                        logToConsole internalError
                        return Error error
                else
                    return Ok ()
            }

        /// Validates that the organization exists.
        let organizationExists<'T> ownerId ownerName organizationId organizationName (error: 'T) =
            task {
                let mutable organizationGuid = Guid.Empty
                match! resolveOrganizationId ownerId ownerName organizationId organizationName with
                | Some organizationId ->
                    if Guid.TryParse(organizationId, &organizationGuid) then
                        let mutable x = null
                        let cached = memoryCache.TryGetValue(organizationGuid, &x)
                        if cached then
                            return Ok ()
                        else
                            let actorId = Organization.GetActorId(organizationGuid)
                            let organizationActorProxy = actorProxyFactory.CreateActorProxy<IOrganizationActor>(actorId, ActorName.Organization)
                            let! exists = organizationActorProxy.Exists()
                            if exists then
                                use newCacheEntry = memoryCache.CreateEntry(organizationGuid, Value = null, SlidingExpiration = DefaultExpirationTime)
                                return Ok ()
                            else
                                return Error error
                    else
                        return Ok ()
                | None -> return Error error
            }

        /// Validates that the organization does not exist.
        let organizationDoesNotExist<'T> ownerId ownerName organizationId organizationName (error: 'T) =
            task {
                match! resolveOrganizationId ownerId ownerName organizationId organizationName with
                | Some organizationId ->
                        return Error error
                | None -> return Ok ()
            }

        /// Validates that the organization is deleted.
        let organizationIsDeleted<'T> ownerId ownerName organizationId organizationName (error: 'T) =
            task {
                match! resolveOrganizationId ownerId ownerName organizationId organizationName with
                | Some organizationId ->
                    let actorId = Organization.GetActorId(Guid.Parse(organizationId))
                    let organizationActorProxy = actorProxyFactory.CreateActorProxy<IOrganizationActor>(actorId, ActorName.Organization)
                    let! isDeleted = organizationActorProxy.IsDeleted()
                    if isDeleted then
                        return Ok ()
                    else
                        return Error error
                | None -> return Error error
            }

        /// Validates that the organization is not deleted.
        let organizationIsNotDeleted<'T> ownerId ownerName organizationId organizationName (error: 'T) =
            task {
                match! resolveOrganizationId ownerId ownerName organizationId organizationName with
                | Some organizationId ->
                    let actorId = Organization.GetActorId(Guid.Parse(organizationId))
                    let organizationActorProxy = actorProxyFactory.CreateActorProxy<IOrganizationActor>(actorId, ActorName.Organization)
                    let! isDeleted = organizationActorProxy.IsDeleted()
                    if isDeleted then
                        return Error error
                    else
                        return Ok ()
                | None -> return Error error
            }

    module Repository =

        /// Validates that the given RepositoryId exists in the database.
        let repositoryIdExists<'T> (repositoryId: string) (error: 'T) =
            task {
                let mutable repositoryGuid = Guid.Empty
                if (not <| String.IsNullOrEmpty(repositoryId)) && Guid.TryParse(repositoryId, &repositoryGuid) then
                    let mutable x = null
                    let cached = memoryCache.TryGetValue(repositoryGuid, &x)
                    if cached then
                        return Ok ()
                    else
                        let actorId = ActorId(repositoryId)
                        let repositoryActorProxy = actorProxyFactory.CreateActorProxy<IRepositoryActor>(actorId, ActorName.Repository)
                        let! exists = repositoryActorProxy.Exists()
                        if exists then
                            use newCacheEntry = memoryCache.CreateEntry(repositoryGuid, Value = null, SlidingExpiration = DefaultExpirationTime)
                            return Ok ()
                        else
                            return Error error
                else
                    return Ok ()
            }

        /// Validates that the given repositoryId does not already exist in the database.
        let repositoryIdDoesNotExist<'T> (repositoryId: string) (error: 'T) =
            task {
                if not <| String.IsNullOrEmpty(repositoryId) then
                    match! repositoryIdExists repositoryId error with 
                    | Ok _ -> return Error error
                    | Error _ -> return Ok ()
                else
                    return Ok ()
            }

        /// Validates that the repository exists.
        let repositoryExists<'T> ownerId ownerName organizationId organizationName repositoryId repositoryName (error: 'T) =
            task {
                let mutable repositoryGuid = Guid.Empty
                match! resolveRepositoryId ownerId ownerName organizationId organizationName repositoryId repositoryName with
                | Some repositoryId ->
                    if Guid.TryParse(repositoryId, &repositoryGuid) then
                        let mutable x = null
                        let cached = memoryCache.TryGetValue(repositoryGuid, &x)
                        if cached then
                            return Ok ()
                        else
                            let actorId = ActorId($"{repositoryGuid}")
                            let repositoryActorProxy = actorProxyFactory.CreateActorProxy<IRepositoryActor>(actorId, ActorName.Repository)
                            let! exists = repositoryActorProxy.Exists()
                            if exists then
                                use newCacheEntry = memoryCache.CreateEntry(repositoryGuid, Value = null, SlidingExpiration = DefaultExpirationTime)
                                return Ok ()
                            else
                                return Error error
                    else
                        return Ok ()
                | None -> return Error error
            }

        /// Validates that the repository is deleted.
        let repositoryIsDeleted<'T> ownerId ownerName organizationId organizationName repositoryId repositoryName (error: 'T) =
            task {
                let mutable guid = Guid.Empty
                match! resolveRepositoryId ownerId ownerName organizationId organizationName repositoryId repositoryName with
                | Some repositoryId ->
                    if Guid.TryParse(repositoryId, &guid) then
                        let actorId = ActorId($"{guid}")
                        let repositoryActorProxy = actorProxyFactory.CreateActorProxy<IRepositoryActor>(actorId, ActorName.Repository)
                        let! isDeleted = repositoryActorProxy.IsDeleted()
                        if isDeleted then
                            return Ok ()
                        else
                            return Error error
                    else
                        return Error error
                | None -> return Error error
            }

        /// Validates that the repository is not deleted.
        let repositoryIsNotDeleted<'T> ownerId ownerName organizationId organizationName repositoryId repositoryName (error: 'T) =
            task {
                let mutable guid = Guid.Empty
                match! resolveRepositoryId ownerId ownerName organizationId organizationName repositoryId repositoryName with
                | Some repositoryId ->
                    if Guid.TryParse(repositoryId, &guid) then
                        let actorId = ActorId($"{guid}")
                        let repositoryActorProxy = actorProxyFactory.CreateActorProxy<IRepositoryActor>(actorId, ActorName.Repository)
                        let! isDeleted = repositoryActorProxy.IsDeleted()
                        if isDeleted then
                            return Error error
                        else
                            return Ok ()
                    else
                        return Error error
                | None -> return Error error
            }

    module Branch =

        /// Validates that the given branchId exists in the database.
        let branchIdExists<'T> (branchId: string) (error: 'T) =
            task {
                let mutable branchGuid = Guid.Empty
                if (not <| String.IsNullOrEmpty(branchId)) && Guid.TryParse(branchId, &branchGuid) then
                    let mutable x = null
                    let cached = memoryCache.TryGetValue(branchGuid, &x)
                    if cached then
                        return Ok ()
                    else
                        let actorId = ActorId(branchId)
                        let branchActorProxy = actorProxyFactory.CreateActorProxy<IBranchActor>(actorId, ActorName.Branch)
                        let! exists = branchActorProxy.Exists()
                        if exists then
                            use newCacheEntry = memoryCache.CreateEntry(branchGuid, Value = null, SlidingExpiration = DefaultExpirationTime)
                            return Ok ()
                        else
                            return Error error
                else
                    return Ok ()
            }


        /// Validates that the given branchId does not exist in the database.
        let branchIdDoesNotExist<'T> (branchId: string) (error: 'T) =
            task {
                let mutable guid = Guid.Empty
                if (not <| String.IsNullOrEmpty(branchId)) && Guid.TryParse(branchId, &guid) then
                    let actorId = ActorId($"{guid}")
                    let branchActorProxy = actorProxyFactory.CreateActorProxy<IBranchActor>(actorId, ActorName.Branch)
                    let! exists = branchActorProxy.Exists()
                    if exists then
                        return Error error
                    else
                        return Ok ()
                else
                    return Ok ()
            }

        /// Validates that the branch exists in the database.
        let branchExists<'T> ownerId ownerName organizationId organizationName repositoryId repositoryName branchId branchName (error: 'T) =
            task {
                let mutable branchGuid = Guid.Empty
                match! resolveRepositoryId ownerId ownerName organizationId organizationName repositoryId repositoryName with
                | Some repositoryId ->
                    match! resolveBranchId repositoryId branchId branchName with
                    | Some branchId ->
                        if Guid.TryParse(branchId, &branchGuid) then
                            let mutable x = null
                            let cached = memoryCache.TryGetValue(branchGuid, &x)
                            if cached then
                                return Ok ()
                            else
                                let actorId = ActorId($"{branchGuid}")
                                let branchActorProxy = actorProxyFactory.CreateActorProxy<IBranchActor>(actorId, ActorName.Branch)
                                let! exists = branchActorProxy.Exists()
                                if exists then
                                    use newCacheEntry = memoryCache.CreateEntry(branchGuid, Value = null, SlidingExpiration = DefaultExpirationTime)
                                    return Ok ()
                                else
                                    return Error error
                        else
                            return Error error
                    | None -> return Error error
                | None -> return Error error
            }

        /// Validates that a branch allows a specific reference type.
        let branchAllowsReferenceType<'T> ownerId ownerName organizationId organizationName repositoryId repositoryName branchId branchName (referenceType: ReferenceType) (error: 'T) =
            task {
                let mutable guid = Guid.Empty
                match! resolveRepositoryId ownerId ownerName organizationId organizationName repositoryId repositoryName with
                | Some repositoryId ->
                    match! resolveBranchId repositoryId branchId branchName with
                    | Some branchId ->
                        if Guid.TryParse(branchId, &guid) then
                            let actorId = ActorId($"{guid}")
                            let branchActorProxy = actorProxyFactory.CreateActorProxy<IBranchActor>(actorId, ActorName.Branch)
                            let! branchDto = branchActorProxy.Get()
                            let allowed = 
                                match referenceType with
                                | Promotion -> if branchDto.PromotionEnabled then true else false
                                | Commit -> if branchDto.CommitEnabled then true else false
                                | Checkpoint -> if branchDto.CheckpointEnabled then true else false
                                | Save -> if branchDto.SaveEnabled then true else false
                                | Tag -> if branchDto.TagEnabled then true else false
                            if allowed then return Ok () else return Error error
                        else
                            return Error error
                    | None -> return Error error
                | None -> return Error error
            }

        /// Validates that the given branchName does not exist in the database.
        let branchNameDoesNotExist<'T> ownerId ownerName organizationId organizationName repositoryId repositoryName branchName (error: 'T) =
            task {
                match! resolveRepositoryId ownerId ownerName organizationId organizationName repositoryId repositoryName with
                | Some repositoryId ->
                    match! resolveBranchId repositoryId String.Empty branchName with
                    | Some branchId -> return Error error
                    | None -> return Ok ()
                | None -> return Ok ()
            }

        /// Validates that the given ReferenceId exists in the database.
        let referenceIdExists<'T> (referenceId: ReferenceId) (error: 'T) =
            task {
                if not <| (referenceId = Guid.Empty) then
                    let actorId = ActorId($"{referenceId}")
                    let referenceActorProxy = actorProxyFactory.CreateActorProxy<IReferenceActor>(actorId, ActorName.Reference)
                    let! exists = referenceActorProxy.Exists()
                    if exists then
                        return Ok ()
                    else
                        return Error error
                else
                    return Ok ()
            }

    module Directory =
        /// Validates that the given DirectoryId exists in the database.
        let directoryIdExists<'T> (directoryId: Guid) (error: 'T) =
            task {
                let mutable x = null
                let cached = memoryCache.TryGetValue(directoryId, &x)
                if cached then
                    return Ok ()
                else
                    let actorId = DirectoryVersion.GetActorId(directoryId)
                    let directoryVersionActorProxy = ApplicationContext.actorProxyFactory.CreateActorProxy<IDirectoryVersionActor>(actorId, ActorName.DirectoryVersion)
                    let! exists = directoryVersionActorProxy.Exists()
                    if exists then
                        use newCacheEntry = memoryCache.CreateEntry(directoryId, Value = null, SlidingExpiration = DefaultExpirationTime)
                        return Ok ()
                    else
                        return Error error
            }

        /// Validates that all of the given DirectoryIds exist in the database.
        let directoryIdsExist<'T> (directoryIds: List<DirectoryId>) (error: 'T) =
            task {
                let mutable allExist = true
                let directoryIdStack = Queue<DirectoryId>(directoryIds)
                while directoryIdStack.Count > 0 && allExist do
                    let directoryId = directoryIdStack.Dequeue()
                    let actorId = DirectoryVersion.GetActorId(directoryId)
                    let directoryVersionActorProxy = actorProxyFactory.CreateActorProxy<IDirectoryVersionActor>(actorId, ActorName.DirectoryVersion)
                    let! exists = directoryVersionActorProxy.Exists()
                    allExist <- exists
                if allExist then
                    return Ok ()
                else
                    return Error error
            }

        /// Validates that the branch exists in the database.
        let sha256HashExists<'T> repositoryId branchId branchName sha256Hash (error: 'T) =
            task {
                let mutable guid = Guid.Empty
                match! resolveBranchId repositoryId branchId branchName with
                | Some branchId ->
                    if Guid.TryParse(branchId, &guid) then
                        let actorId = ActorId($"{guid}")
                        let branchActorProxy = actorProxyFactory.CreateActorProxy<IBranchActor>(actorId, ActorName.Branch)
                        let! exists = branchActorProxy.Exists()
                        if exists || guid = Grace.Shared.Constants.DefaultParentBranchId then
                            return Ok ()
                        else
                            return Error error
                    else
                        return Error error
                | None -> return Error error
            }
