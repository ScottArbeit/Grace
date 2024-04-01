namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Dto
open Grace.Shared.Parameters.Directory
open Grace.Shared.Types
open System
open System.Collections.Generic
open System.Threading.Tasks

type Directory() =
    /// Retrieves a DirectoryVersion instance.
    static member public Get(parameters: GetParameters) =
        postServer<GetParameters, DirectoryVersion> (
            parameters |> ensureCorrelationIdIsSet,
            $"directory/{nameof (Directory.Get)}"
        )

    /// Retrieves a DirectoryVersion instance.
    static member public GetByDirectoryIds(parameters: GetByDirectoryIdsParameters) =
        postServer<GetByDirectoryIdsParameters, List<DirectoryVersion>> (
            parameters |> ensureCorrelationIdIsSet,
            $"directory/{nameof (Directory.GetByDirectoryIds)}"
        )

    /// Retrieves a DirectoryVersion instance.
    static member public GetBySha256Hash(parameters: GetBySha256HashParameters) =
        postServer<GetBySha256HashParameters, string> (
            parameters |> ensureCorrelationIdIsSet,
            $"directory/{nameof (Directory.GetBySha256Hash)}"
        )

    /// Saves a list of DirectoryVersion instances to the server.
    static member public Create(parameters: CreateParameters) =
        postServer<CreateParameters, string> (
            parameters |> ensureCorrelationIdIsSet,
            $"directory/{nameof (Directory.Create)}"
        )

    /// Saves a list of DirectoryVersion instances to the server.
    static member public SaveDirectoryVersions(parameters: SaveDirectoryVersionsParameters) =
        postServer<SaveDirectoryVersionsParameters, string> (
            parameters |> ensureCorrelationIdIsSet,
            $"directory/{nameof (Directory.SaveDirectoryVersions)}"
        )

    /// Retrieves the recursive set of DirectoryVersions from a specific DirectoryVersion.
    static member public GetDirectoryVersionsRecursive(parameters: GetParameters) =
        postServer<GetParameters, List<DirectoryVersion>> (
            parameters |> ensureCorrelationIdIsSet,
            $"directory/{nameof (Directory.GetDirectoryVersionsRecursive)}"
        )
