namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Parameters.Repository
open Grace.Types.Common
open Grace.Types.Usage
open System
open System.Threading.Tasks

/// SDK entry point for usage reads: what Grace keeps for a repository and how much of it is counted.
type Usage() =

    /// Reads the retained logical bytes Grace keeps for one repository, by content class, with the coverage stated.
    /// Calls `GET /usage/storage` and needs OwnerAdmin on the owner. Takes explicit owner, organization, and repository
    /// GUIDs; name selectors are rejected here rather than ignored, because the server does not resolve names.
    static member GetStorage(parameters: GetRepositoryParameters) : Task<GraceResult<RepositoryStorageUsage>> =
        task {
            /// Requires a supplied non-empty GUID.
            let validId (value: string) =
                match Guid.TryParse value with
                | true, id -> id <> Guid.Empty
                | _ -> false

            if isNull (box parameters) then
                return Error(GraceError.Create "Usage reads need explicit owner, organization, and repository GUIDs." "")
            elif
                [
                    parameters.OwnerName
                    parameters.OrganizationName
                    parameters.RepositoryName
                ]
                |> List.exists (String.IsNullOrEmpty >> not)
            then
                return
                    Error(
                        GraceError.Create
                            "Usage reads need explicit owner, organization, and repository GUIDs; names are not accepted."
                            parameters.CorrelationId
                    )
            elif
                not
                    (
                        validId parameters.OwnerId
                        && validId parameters.OrganizationId
                        && validId parameters.RepositoryId
                    )
            then
                return
                    Error(
                        GraceError.Create
                            "Usage reads need explicit owner, organization, and repository GUIDs. Pass --owner-id, --organization-id, and --repository-id, or run inside a connected repository."
                            parameters.CorrelationId
                    )
            else
                let query =
                    [
                        "OwnerId", parameters.OwnerId
                        "OrganizationId", parameters.OrganizationId
                        "RepositoryId", parameters.RepositoryId
                    ]
                    |> List.map (fun (key, value) -> $"{key}={Uri.EscapeDataString value}")
                    |> String.concat "&"

                return! getServer<GetRepositoryParameters, RepositoryStorageUsage> (ensureCorrelationIdIsSet parameters, $"usage/storage?{query}")
        }
