namespace Grace.Actors.Extensions

open Grace.Shared.Constants
open Grace.Shared.Types
open Microsoft.Extensions.Caching.Memory
open System

module MemoryCache =
    [<Literal>]
    let ownerIdPrefix = "OwI"
    [<Literal>]
    let organizationIdPrefix = "OrI"
    [<Literal>]
    let repositoryIdPrefix = "ReI"
    [<Literal>]
    let branchIdPrefix = "BrI"
    [<Literal>]
    let ownerNamePrefix = "OwN"
    [<Literal>]
    let organizationNamePrefix = "OrN"
    [<Literal>]
    let repositoryNamePrefix = "ReN"
    [<Literal>]
    let branchNamePrefix = "BrN"

    type Microsoft.Extensions.Caching.Memory.IMemoryCache with

        /// Create a new entry in MemoryCache with a default expiration time.
        member this.CreateWithDefaultExpirationTime (key: string) value =
            use newCacheEntry = this.CreateEntry(key, Value = value, AbsoluteExpirationRelativeToNow = MemoryCache.DefaultExpirationTime)
            ()

        /// Create a new entry in MemoryCache to confirm that an OwnerId exists.
        member this.CreateOwnerIdEntry (ownerId: OwnerId) (value: string) =
            this.CreateWithDefaultExpirationTime $"{ownerIdPrefix}:{ownerId}" value

        /// Create a new entry in MemoryCache to confirm that an OrganizationId exists.
        member this.CreateOrganizationIdEntry (organizationId: OrganizationId) (value: string) =
            this.CreateWithDefaultExpirationTime $"{organizationIdPrefix}:{organizationId}" value

        /// Create a new entry in MemoryCache to confirm that a RepositoryId exists.
        member this.CreateRepositoryIdEntry (repositoryId: RepositoryId) (value: string) =
            this.CreateWithDefaultExpirationTime $"{repositoryIdPrefix}:{repositoryId}" value

        /// Create a new entry in MemoryCache to confirm that a BranchId exists.
        member this.CreateBranchIdEntry (branchId: BranchId) (value: string) =
            this.CreateWithDefaultExpirationTime $"{branchIdPrefix}:{branchId}" value

        /// Create a new entry in MemoryCache to link an OwnerName with an OwnerId.
        member this.CreateOwnerNameEntry (ownerName: OwnerName) (ownerId: OwnerId) =
            this.CreateWithDefaultExpirationTime $"{ownerNamePrefix}:{ownerName}" ownerId

        /// Create a new entry in MemoryCache to link an OrganizationName with an OrganizationId.
        member this.CreateOrganizationNameEntry (organizationName: OrganizationName) (organizationId: OrganizationId) =
            this.CreateWithDefaultExpirationTime $"{organizationNamePrefix}:{organizationName}" organizationId

        /// Create a new entry in MemoryCache to link a RepositoryName with a RepositoryId.
        member this.CreateRepositoryNameEntry(repositoryName: RepositoryName) (repositoryId: RepositoryId) =
            this.CreateWithDefaultExpirationTime $"{repositoryNamePrefix}:{repositoryName}" repositoryId

        /// Create a new entry in MemoryCache to link a BranchName with a BranchId.
        member this.CreateBranchNameEntry(branchName: string) (branchId: BranchId) =
            this.CreateWithDefaultExpirationTime $"{branchNamePrefix}:{branchName}" branchId

        /// Get a value from MemoryCache, if it exists.
        member this.GetFromCache<'T> (key: string) =
            let mutable value = Unchecked.defaultof<'T>
            if this.TryGetValue(key, &value) then
                Some value
            else
                None

        /// Check if we have an entry in MemoryCache for an OwnerId.
        member this.GetOwnerIdEntry(ownerId: OwnerId) =
            this.GetFromCache<string> $"{ownerIdPrefix}:{ownerId}"

        /// Check if we have an entry in MemoryCache for an OrganizationId.
        member this.GetOrganizationIdEntry(organizationId: OrganizationId) =
            this.GetFromCache<string> $"{organizationIdPrefix}:{organizationId}"
            
        /// Check if we have an entry in MemoryCache for a RepositoryId.
        member this.GetRepositoryIdEntry(repositoryId: RepositoryId) =
            this.GetFromCache<string> $"{repositoryIdPrefix}:{repositoryId}"

        /// Check if we have an entry in MemoryCache for a BranchId.
        member this.GetBranchIdEntry(branchId: BranchId) =
            this.GetFromCache<string> $"{branchIdPrefix}:{branchId}"

        /// Check if we have an entry in MemoryCache for an OwnerName, and return the OwnerId if we have it.
        member this.GetOwnerNameEntry(ownerName: string) =
            this.GetFromCache<Guid> $"{ownerNamePrefix}:{ownerName}"

        /// Check if we have an entry in MemoryCache for an OrganizationName, and return the OrganizationId if we have it.
        member this.GetOrganizationNameEntry(organizationName: string) =
            this.GetFromCache<Guid> $"{organizationNamePrefix}:{organizationName}"

        /// Check if we have an entry in MemoryCache for a RepositoryName, and return the RepositoryId if we have it.
        member this.GetRepositoryNameEntry(repositoryName: string) =
            this.GetFromCache<Guid> $"{repositoryNamePrefix}:{repositoryName}"

        /// Check if we have an entry in MemoryCache for a BranchName, and return the BranchId if we have it.
        member this.GetBranchNameEntry(branchName: string) =
            this.GetFromCache<Guid> $"{branchNamePrefix}:{branchName}"


        /// Remove an entry in MemoryCache for an OwnerId.
        member this.RemoveOwnerIdEntry(ownerId: OwnerId) =
            this.Remove($"{ownerIdPrefix}:{ownerId}")

        /// Remove an entry in MemoryCache for an OrganizationId.
        member this.RemoveOrganizationIdEntry(organizationId: OrganizationId) =
            this.Remove($"{organizationIdPrefix}:{organizationId}")

        /// Remove an entry in MemoryCache for a RepositoryId.
        member this.RemoveRepositoryIdEntry(repositoryId: RepositoryId) =
            this.Remove($"{repositoryIdPrefix}:{repositoryId}")

        /// Remove an entry in MemoryCache for a BranchId.
        member this.RemoveBranchIdEntry(branchId: BranchId) =
            this.Remove($"{branchIdPrefix}:{branchId}")

        /// Remove an entry in MemoryCache for an OwnerName.
        member this.RemoveOwnerNameEntry(ownerName: string) =
            this.Remove($"{ownerNamePrefix}:{ownerName}")

        /// Remove an entry in MemoryCache for an OrganizationName.
        member this.RemoveOrganizationNameEntry(organizationName: string) =
            this.Remove($"{organizationNamePrefix}:{organizationName}")

        /// Remove an entry in MemoryCache for a RepositoryName.
        member this.RemoveRepositoryNameEntry(repositoryName: string) =
            this.Remove($"{repositoryNamePrefix}:{repositoryName}")

        /// Remove an entry in MemoryCache for a BranchName.
        member this.RemoveBranchNameEntry(branchName: string) =
            this.Remove($"{branchNamePrefix}:{branchName}")
