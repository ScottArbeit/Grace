namespace Grace.Server.Middleware

open Grace.Types.Types
open Microsoft.AspNetCore.Http
open System
open System.Reflection

/// Holds the PropertyInfo for each Entity Id and Name property.
type EntityProperties =
    {
        OwnerId: PropertyInfo option
        OwnerName: PropertyInfo option
        OrganizationId: PropertyInfo option
        OrganizationName: PropertyInfo option
        RepositoryId: PropertyInfo option
        RepositoryName: PropertyInfo option
        BranchId: PropertyInfo option
        BranchName: PropertyInfo option
    }

    static member Default =
        {
            OwnerId = None
            OwnerName = None
            OrganizationId = None
            OrganizationName = None
            RepositoryId = None
            RepositoryName = None
            BranchId = None
            BranchName = None
        }

module ValidateIdsDecisions =

    /// Paths that we want to ignore, because they won't have Ids and Names in the body.
    let ignoredPaths =
        [
            "/healthz"
            "/notifications"
            "/approval/request/_seedGenerated"
        ]

    let isIgnoredPath (path: string) =
        ignoredPaths
        |> Seq.exists (fun ignoredPath -> path.StartsWith(ignoredPath, StringComparison.InvariantCultureIgnoreCase))

    /// Gets the parameter type for the endpoint from the endpoint metadata created in Startup.Server.fs.
    let tryGetBodyType (path: string) (endpoint: Endpoint) =
        if isIgnoredPath path
           || isNull endpoint
           || endpoint.Metadata.Count = 0 then
            None
        else
            endpoint.Metadata
            |> Seq.tryPick (function
                | :? Type as requestBodyType -> Some requestBodyType
                | _ -> None)

    let discoverEntityProperties (requestBodyType: Type) =
        let properties = requestBodyType.GetProperties(BindingFlags.Public ||| BindingFlags.Instance)

        /// Checks if a property with the given name exists on the request body type.
        let findProperty name =
            properties
            |> Seq.tryFind (fun property -> property.Name = name)

        {
            OwnerId = findProperty (nameof OwnerId)
            OwnerName = findProperty (nameof OwnerName)
            OrganizationId = findProperty (nameof OrganizationId)
            OrganizationName = findProperty (nameof OrganizationName)
            RepositoryId = findProperty (nameof RepositoryId)
            RepositoryName = findProperty (nameof RepositoryName)
            BranchId = findProperty (nameof BranchId)
            BranchName = findProperty (nameof BranchName)
        }
