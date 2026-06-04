namespace Grace.Server.Middleware

open Grace.Types.Common
open Grace.Shared.Validation
open Grace.Shared.Validation.Errors
open Grace.Shared.Validation.Utilities
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

    type EntityKind =
        | Owner = 0
        | Organization = 1
        | Repository = 2
        | Branch = 3

    type EntityValidationMode =
        | Create = 0
        | Existing = 1

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

    let private pathEquals expectedPath (path: string) = path.Equals(expectedPath, StringComparison.InvariantCultureIgnoreCase)

    let validationModeForPath entityKind path =
        match entityKind with
        | EntityKind.Owner when pathEquals "/owner/create" path -> EntityValidationMode.Create
        | EntityKind.Organization when pathEquals "/organization/create" path -> EntityValidationMode.Create
        | EntityKind.Repository when pathEquals "/repository/create" path -> EntityValidationMode.Create
        | EntityKind.Branch when pathEquals "/branch/create" path -> EntityValidationMode.Create
        | _ -> EntityValidationMode.Existing

    let shouldValidateEntity currentError idProperty nameProperty =
        currentError |> Option.isNone
        && idProperty |> Option.isSome
        && nameProperty |> Option.isSome

    let getEntityValidationErrorMessage entityKind validationMode id name =
        task {
            match entityKind, validationMode with
            | EntityKind.Owner, EntityValidationMode.Create ->
                let validations =
                    [|
                        Common.String.isNotEmpty id OwnerError.OwnerIdIsRequired
                        Common.Guid.isValidAndNotEmptyGuid id OwnerError.InvalidOwnerId
                        Common.String.isNotEmpty name OwnerError.OwnerNameIsRequired
                        Common.String.isValidGraceName name OwnerError.InvalidOwnerName
                        Common.Input.eitherIdOrNameMustBeProvided id name OwnerError.EitherOwnerIdOrOwnerNameRequired
                    |]

                match! getFirstError validations with
                | Some error -> return Some(OwnerError.getErrorMessage error)
                | None -> return None
            | EntityKind.Owner, EntityValidationMode.Existing ->
                let validations =
                    [|
                        Common.Guid.isValidAndNotEmptyGuid id OwnerError.InvalidOwnerId
                        Common.String.isValidGraceName name OwnerError.InvalidOwnerName
                        Common.Input.eitherIdOrNameMustBeProvided id name OwnerError.EitherOwnerIdOrOwnerNameRequired
                    |]

                match! getFirstError validations with
                | Some error -> return Some(OwnerError.getErrorMessage error)
                | None -> return None
            | EntityKind.Organization, EntityValidationMode.Create ->
                let validations =
                    [|
                        Common.String.isNotEmpty id OrganizationError.OrganizationIdIsRequired
                        Common.Guid.isValidAndNotEmptyGuid id OrganizationError.InvalidOrganizationId
                        Common.String.isNotEmpty name OrganizationError.OrganizationNameIsRequired
                        Common.String.isValidGraceName name OrganizationError.InvalidOrganizationName
                        Common.Input.eitherIdOrNameMustBeProvided id name OrganizationError.EitherOrganizationIdOrOrganizationNameRequired
                    |]

                match! getFirstError validations with
                | Some error -> return Some(OrganizationError.getErrorMessage error)
                | None -> return None
            | EntityKind.Organization, EntityValidationMode.Existing ->
                let validations =
                    [|
                        Common.Guid.isValidAndNotEmptyGuid id OrganizationError.InvalidOrganizationId
                        Common.String.isValidGraceName name OrganizationError.InvalidOrganizationName
                        Common.Input.eitherIdOrNameMustBeProvided id name OrganizationError.EitherOrganizationIdOrOrganizationNameRequired
                    |]

                match! getFirstError validations with
                | Some error -> return Some(OrganizationError.getErrorMessage error)
                | None -> return None
            | EntityKind.Repository, EntityValidationMode.Create ->
                let validations =
                    [|
                        Common.String.isNotEmpty id RepositoryError.RepositoryIdIsRequired
                        Common.Guid.isValidAndNotEmptyGuid id RepositoryError.InvalidRepositoryId
                        Common.String.isNotEmpty name RepositoryError.RepositoryNameIsRequired
                        Common.String.isValidGraceName name RepositoryError.InvalidRepositoryName
                        Common.Input.eitherIdOrNameMustBeProvided id name RepositoryError.EitherRepositoryIdOrRepositoryNameRequired
                    |]

                match! getFirstError validations with
                | Some error -> return Some(RepositoryError.getErrorMessage error)
                | None -> return None
            | EntityKind.Repository, EntityValidationMode.Existing ->
                let validations =
                    [|
                        Common.Guid.isValidAndNotEmptyGuid id RepositoryError.InvalidRepositoryId
                        Common.String.isValidGraceName name RepositoryError.InvalidRepositoryName
                        Common.Input.eitherIdOrNameMustBeProvided id name RepositoryError.EitherRepositoryIdOrRepositoryNameRequired
                    |]

                match! getFirstError validations with
                | Some error -> return Some(RepositoryError.getErrorMessage error)
                | None -> return None
            | EntityKind.Branch, EntityValidationMode.Create ->
                let validations =
                    [|
                        Common.String.isNotEmpty id BranchError.BranchIdIsRequired
                        Common.Guid.isValidAndNotEmptyGuid id BranchError.InvalidBranchId
                        Common.String.isNotEmpty name BranchError.BranchNameIsRequired
                        Common.String.isValidGraceName name BranchError.InvalidBranchName
                        Common.Input.eitherIdOrNameMustBeProvided id name BranchError.EitherBranchIdOrBranchNameRequired
                    |]

                match! getFirstError validations with
                | Some error -> return Some(BranchError.getErrorMessage error)
                | None -> return None
            | EntityKind.Branch, EntityValidationMode.Existing ->
                let validations =
                    [|
                        Common.Guid.isValidAndNotEmptyGuid id BranchError.InvalidBranchId
                        Common.String.isValidGraceName name BranchError.InvalidBranchName
                        Common.Input.eitherIdOrNameMustBeProvided id name BranchError.EitherBranchIdOrBranchNameRequired
                    |]

                match! getFirstError validations with
                | Some error -> return Some(BranchError.getErrorMessage error)
                | None -> return None
            | _ -> return invalidArg (nameof entityKind) $"Unknown entity validation decision: {entityKind}/{validationMode}."
        }

    let notFoundErrorMessage entityKind id =
        let byId = not <| String.IsNullOrEmpty(id)

        match entityKind, byId with
        | EntityKind.Owner, true -> OwnerError.getErrorMessage OwnerError.OwnerIdDoesNotExist
        | EntityKind.Owner, false -> OwnerError.getErrorMessage OwnerError.OwnerDoesNotExist
        | EntityKind.Organization, true -> OrganizationError.getErrorMessage OrganizationError.OrganizationIdDoesNotExist
        | EntityKind.Organization, false -> OrganizationError.getErrorMessage OrganizationError.OrganizationDoesNotExist
        | EntityKind.Repository, true -> RepositoryError.getErrorMessage RepositoryError.RepositoryIdDoesNotExist
        | EntityKind.Repository, false -> RepositoryError.getErrorMessage RepositoryError.RepositoryDoesNotExist
        | EntityKind.Branch, true -> BranchError.getErrorMessage BranchError.BranchIdDoesNotExist
        | EntityKind.Branch, false -> BranchError.getErrorMessage BranchError.BranchDoesNotExist
        | _ -> invalidArg (nameof entityKind) $"Unknown entity kind: {entityKind}."

    let withEntityId entityKind (id: string) graceIds =
        let parsedId = Guid.Parse(id)

        match entityKind with
        | EntityKind.Owner -> { graceIds with OwnerId = parsedId; OwnerIdString = id; HasOwner = true }
        | EntityKind.Organization -> { graceIds with OrganizationId = parsedId; OrganizationIdString = id; HasOrganization = true }
        | EntityKind.Repository -> { graceIds with RepositoryId = parsedId; RepositoryIdString = id; HasRepository = true }
        | EntityKind.Branch -> { graceIds with BranchId = parsedId; BranchIdString = id; HasBranch = true }
        | _ -> invalidArg (nameof entityKind) $"Unknown entity kind: {entityKind}."
