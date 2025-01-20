namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Dto
open Grace.Shared.Parameters.DirectoryVersion
open Grace.Shared
open Grace.Shared.Types
open Grace.Shared.Utilities
open System
open System.Collections.Generic
open System.Threading.Tasks

type DirectoryVersion() =
    /// Retrieves a DirectoryVersion instance.
    static member public Get(parameters: GetParameters) =
        postServer<GetParameters, DirectoryVersion> (parameters |> ensureCorrelationIdIsSet, $"directory/{nameof (DirectoryVersion.Get)}")

    /// Retrieves a DirectoryVersion instance.
    static member public GetByDirectoryIds(parameters: GetByDirectoryIdsParameters) =
        postServer<GetByDirectoryIdsParameters, IEnumerable<Types.DirectoryVersion>> (
            parameters |> ensureCorrelationIdIsSet,
            $"directory/{nameof (DirectoryVersion.GetByDirectoryIds)}"
        )

    /// Retrieves a DirectoryVersion instance.
    static member public GetBySha256Hash(parameters: GetBySha256HashParameters) =
        postServer<GetBySha256HashParameters, string> (parameters |> ensureCorrelationIdIsSet, $"directory/{nameof (DirectoryVersion.GetBySha256Hash)}")

    /// Retrieves the Uri to download the .zip file for a specific DirectoryVersion.
    static member public GetZipFile(parameters: GetZipFileParameters) =
        postServer<GetZipFileParameters, UriWithSharedAccessSignature> (
            parameters |> ensureCorrelationIdIsSet,
            $"directory/{nameof (DirectoryVersion.GetZipFile)}"
        )

    /// Saves a list of DirectoryVersion instances to the server.
    static member public Create(parameters: CreateParameters) =
        postServer<CreateParameters, string> (parameters |> ensureCorrelationIdIsSet, $"directory/{nameof (DirectoryVersion.Create)}")

    /// Saves a list of DirectoryVersion instances to the server.
    static member public SaveDirectoryVersions(parameters: SaveDirectoryVersionsParameters) =
        postServer<SaveDirectoryVersionsParameters, string> (
            parameters |> ensureCorrelationIdIsSet,
            $"directory/{nameof (DirectoryVersion.SaveDirectoryVersions)}"
        )

    /// Retrieves the recursive set of DirectoryVersions from a specific DirectoryVersion.
    static member public GetDirectoryVersionsRecursive(parameters: GetParameters) =
        postServer<GetParameters, IEnumerable<Types.DirectoryVersion>> (
            parameters |> ensureCorrelationIdIsSet,
            $"directory/{nameof (DirectoryVersion.GetDirectoryVersionsRecursive)}"
        )
